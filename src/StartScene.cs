using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FastLoad
{
    public enum StartWhen
    {
        Immediately,
        AfterItemsLoaded
    }

    /// <summary>
    /// "[Start] Scene": go straight from the main menu to a chosen scene when the game starts, once. Also the two
    /// ways to find a scene's name: a console line on every scene load, and BepInEx/FastLoad/scenes.txt.
    /// Works with the probe switched off.
    /// </summary>
    public sealed class StartScene : MonoBehaviour
    {
        public string Wanted = "";
        public StartWhen When = StartWhen.Immediately;
        public ManualLogSource Log;

        /// <summary>The menu's own start-up code gets this long before the jump.</summary>
        private const double SettleSeconds = 1.0;
        /// <summary>A name that is not known yet may belong to a map its loader has not registered yet.</summary>
        private const double GiveUpSeconds = 120.0;
        /// <summary>AfterItemsLoaded on a profile where the "all items loaded" line never comes.</summary>
        private const double ItemsWaitLimitSeconds = 600.0;

        private readonly Stopwatch m_sinceMenu = new Stopwatch();
        private readonly Stopwatch m_sinceList = new Stopwatch();
        private bool m_done;
        private bool m_firstSceneSeen;
        private ItemsLoadedWatch m_watch;
        private int m_listedGame = -1;
        private int m_listedMaps = -1;
        private bool m_saidWaiting;

        private void Awake()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            m_sinceList.Start();
        }

        /// <summary>Call once after the fields are set.</summary>
        public void Begin()
        {
            if (Wanted.Trim().Length == 0)
            {
                m_done = true;
                return;
            }
            if (When == StartWhen.AfterItemsLoaded)
            {
                m_watch = new ItemsLoadedWatch();
                BepInEx.Logging.Logger.Listeners.Add(m_watch);
            }
            Log.LogInfo("Start scene: will go to '" + Wanted.Trim() + "' when the main menu has loaded ("
                + (When == StartWhen.Immediately ? "right away" : "after all items are loaded") + ")");
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                if (mode != LoadSceneMode.Single) return;
                string id = AtlasBridge.CurrentId();
                List<string> game = GameScenes();
                bool isGame = Contains(game, scene.name);
                string paste = isGame || id.Length == 0 ? scene.name : id;
                Log.LogInfo("Scene loaded: " + scene.name + "   (to start the game here: [Start] Scene = " + paste + ")");

                if (m_firstSceneSeen) return;
                m_firstSceneSeen = true;
                if (m_done) return;
                if (scene.name.StartsWith("MainMenu", StringComparison.Ordinal))
                {
                    m_sinceMenu.Start();
                }
                else
                {
                    // Something else already took the game past the menu: nothing to skip.
                    Finish();
                    Log.LogInfo("Start scene: the game did not start in the main menu (it is in '" + scene.name + "'); left as it is");
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("Start scene: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private void Update()
        {
            try
            {
                if (m_sinceList.Elapsed.TotalSeconds >= 2.0)
                {
                    m_sinceList.Reset();
                    m_sinceList.Start();
                    WriteListIfChanged();
                }
                if (m_done || !m_sinceMenu.IsRunning) return;
                Step(m_sinceMenu.Elapsed.TotalSeconds);
            }
            catch (Exception e)
            {
                Finish();
                Log.LogWarning("Start scene switched off: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private void Step(double sinceMenu)
        {
            if (sinceMenu < SettleSeconds) return;

            if (When == StartWhen.AfterItemsLoaded && !m_watch.Seen)
            {
                if (sinceMenu < ItemsWaitLimitSeconds)
                {
                    if (!m_saidWaiting)
                    {
                        m_saidWaiting = true;
                        Log.LogInfo("Start scene: waiting for 'All items loaded' before going to '" + Wanted.Trim() + "'");
                    }
                    return;
                }
                Log.LogWarning("Start scene: no 'All items loaded' line after " + (int)ItemsWaitLimitSeconds + " s; going now");
            }

            List<string> game = GameScenes();
            List<MapEntry> maps = AtlasBridge.Maps();
            Pick pick = StartPick.Resolve(Wanted, game, maps);

            if (pick.Kind == PickKind.Game)
            {
                Finish();
                if (string.Equals(pick.Value, SceneManager.GetActiveScene().name, StringComparison.Ordinal))
                {
                    Log.LogInfo("Start scene: '" + pick.Value + "' is where the game already is");
                    return;
                }
                Log.LogInfo("Start scene: going to '" + pick.Value + "' (" + sinceMenu.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s after the menu loaded)");
                // The same call the menu's own Load button makes.
                SteamVR_LoadLevel.Begin(pick.Value, false, 0.5f, 0f, 0f, 0f, 1f);
                return;
            }
            if (pick.Kind == PickKind.Modded)
            {
                Finish();
                string error;
                if (AtlasBridge.Load(pick.Value, out error))
                    Log.LogInfo("Start scene: asked Atlas to load '" + pick.Value + "' (" + sinceMenu.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s after the menu loaded)");
                else
                    Log.LogWarning("Start scene: could not load modded map '" + pick.Value + "': " + error + ". Staying in the menu.");
                return;
            }
            if (pick.Kind == PickKind.Ambiguous)
            {
                Finish();
                Log.LogWarning("Start scene: '" + Wanted.Trim() + "' is the name of more than one modded map. Use one of these identifiers instead: "
                    + pick.Note + ". Staying in the menu.");
                return;
            }
            // Not found: a modded map may still be on its way in. Atlas maps are registered while mods load.
            if (sinceMenu < GiveUpSeconds && (When == StartWhen.Immediately || !m_watch.Seen || sinceMenu < SettleSeconds + 5)) return;
            Finish();
            Log.LogWarning("Start scene: no scene or modded map is called '" + Wanted.Trim() + "'. Staying in the menu. Every name that works is listed in "
                + ListPath() + (AtlasBridge.Found ? "" : " (Atlas, which loads modded maps, is not installed)"));
        }

        private void Finish()
        {
            m_done = true;
            m_sinceMenu.Stop();
            if (m_watch != null)
            {
                // Never from inside a log call: BepInEx is walking its listener list there.
                BepInEx.Logging.Logger.Listeners.Remove(m_watch);
                m_watch = null;
            }
        }

        private static List<string> GameScenes()
        {
            List<string> names = new List<string>();
            int n = SceneManager.sceneCountInBuildSettings;
            for (int i = 0; i < n; i++)
            {
                string name = StartPick.SceneNameFromPath(SceneUtility.GetScenePathByBuildIndex(i));
                if (name.Length > 0) names.Add(name);
            }
            return names;
        }

        private static bool Contains(List<string> names, string name)
        {
            for (int i = 0; i < names.Count; i++)
            {
                if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string ListPath()
        {
            return Path.Combine(Path.Combine(Paths.BepInExRootPath, "FastLoad"), "scenes.txt");
        }

        /// <summary>Rewrites scenes.txt when the number of known scenes or modded maps changed (maps register while mods load).</summary>
        private void WriteListIfChanged()
        {
            List<MapEntry> maps = AtlasBridge.Maps();
            if (maps.Count == m_listedMaps && m_listedGame >= 0) return;
            List<string> game = GameScenes();
            m_listedMaps = maps.Count;
            m_listedGame = game.Count;
            try
            {
                string path = ListPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, StartPick.ListText(game, maps, AtlasBridge.Found));
            }
            catch (Exception e)
            {
                Log.LogWarning("Could not write scenes.txt: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>Hears OtherLoader say that every item is loaded.</summary>
        private sealed class ItemsLoadedWatch : ILogListener
        {
            public volatile bool Seen;

            public void LogEvent(object sender, LogEventArgs e)
            {
                try
                {
                    if (Seen || e.Source == null || e.Data == null) return;
                    if (!e.Source.SourceName.StartsWith("OtherLoader", StringComparison.Ordinal)) return;
                    if (e.Data.ToString().IndexOf("All items loaded", StringComparison.Ordinal) >= 0) Seen = true;
                }
                catch (Exception)
                {
                }
            }

            public void Dispose()
            {
            }
        }
    }
}
