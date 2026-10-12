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
    /// "[Start] Scene": go straight to a chosen scene when the game starts, once. Also the two ways to find a
    /// scene's name: a console line on every scene load, and BepInEx/FastLoad/scenes.txt.
    /// Works with the probe switched off.
    ///
    /// You start in the main menu as always. A second later the loading window says "Loading map X" and the scene
    /// changes when it is in; nothing is blacked out. (An earlier version hid the menu and went straight to the
    /// scene; invent60 asked for this instead on 2026-10-13. Early() below is that version's entry and is not called.)
    /// </summary>
    public sealed class StartScene : MonoBehaviour
    {
        public string Wanted = "";
        public StartWhen When = StartWhen.Immediately;
        public ManualLogSource Log;

        /// <summary>The menu's own start-up code gets this long before a jump that goes through the game's own loader.</summary>
        private const double SettleSeconds = 1.0;
        /// <summary>The menu is hidden for this long at most while a modded map is still being looked for.</summary>
        private const double LookCurtainSeconds = 45.0;
        /// <summary>A direct load that has not arrived by then gives the picture back.</summary>
        private const double ArriveCurtainSeconds = 90.0;
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
        private bool m_saidLooking;
        private bool m_jumped;
        private readonly Stopwatch m_sinceJump = new Stopwatch();
        /// <summary>True while a scene loaded directly (not through the game's loader) is on its way.</summary>
        private bool m_direct;
        /// <summary>Loading-window entry for the map on its way.</summary>
        private int m_feed;

        /// <summary>True from the game's start until the [Start] Scene has loaded, or it is clear that there is no jump.</summary>
        public static bool Busy;

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
            Busy = true;
            if (When == StartWhen.AfterItemsLoaded)
            {
                m_watch = new ItemsLoadedWatch();
                BepInEx.Logging.Logger.Listeners.Add(m_watch);
            }
            Log.LogInfo("Start scene: will go to '" + Wanted.Trim() + "' from the main menu "
                + (When == StartWhen.Immediately ? "right away" : "after all items are loaded"));
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                // Not "!= Single": Unity reports the first scene of the game with a mode that is neither value.
                if (mode == LoadSceneMode.Additive) return;
                string id = AtlasBridge.CurrentId();
                List<string> game = GameScenes();
                bool isGame = Contains(game, scene.name);
                string paste = isGame || id.Length == 0 ? scene.name : id;
                Log.LogInfo("Scene loaded: " + scene.name + "   (to start the game here: [Start] Scene = " + paste + ")");

                if (m_firstSceneSeen)
                {
                    // The scene the jump asked for (or whatever came instead) is here.
                    if (m_jumped) Arrived();
                    return;
                }
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
                // A jump that never arrives must not keep others waiting.
                if (Busy && m_jumped && m_sinceJump.Elapsed.TotalSeconds > 120.0)
                {
                    Busy = false;
                    FastLoad.Early.LoadFeed.End(m_feed, FastLoad.Early.Clock.NowMs);
                    m_feed = 0;
                }
                if (Curtain.Closed) CurtainWatch();
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
                // Loaded directly, not through the game's loader: that one blacks the headset out and loads at the
                // slowest setting. You stand in the menu, the loading window says where to, and the scene changes.
                ThreadPriority before = Application.backgroundLoadingPriority;
                Application.backgroundLoadingPriority = ThreadPriority.High;
                AsyncOperation op = SceneManager.LoadSceneAsync(pick.Value, LoadSceneMode.Single);
                if (op == null)
                {
                    Application.backgroundLoadingPriority = before;
                    Log.LogWarning("Start scene: Unity would not load '" + pick.Value + "'. Staying in the menu.");
                    return;
                }
                Jumped();
                m_direct = true;
                m_feed = FastLoad.Early.LoadFeed.Begin("map " + pick.Value, "", false, false, FastLoad.Early.Clock.NowMs);
                return;
            }
            if (pick.Kind == PickKind.Modded)
            {
                Finish();
                string error;
                bool asked = AtlasBridge.Load(pick.Value, out error);
                if (asked) Jumped();
                if (asked) m_feed = FastLoad.Early.LoadFeed.Begin("map " + pick.Value, "", false, false, FastLoad.Early.Clock.NowMs);
                if (asked)
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
            if (sinceMenu < GiveUpSeconds && AtlasBridge.Found && (When == StartWhen.Immediately || !m_watch.Seen || sinceMenu < SettleSeconds + 5))
            {
                if (!m_saidLooking)
                {
                    m_saidLooking = true;
                    Log.LogInfo("Start scene: '" + Wanted.Trim() + "' is not a game scene and not a modded map registered so far; checking again for up to "
                        + (int)GiveUpSeconds + " s while mods load");
                }
                return;
            }
            Finish();
            string close = StartPick.Suggest(Wanted, game, maps);
            Log.LogWarning("Start scene: no scene or modded map is called '" + Wanted.Trim() + "'. Staying in the menu."
                + (close.Length > 0 ? " Did you mean: " + close + "?" : "") + " Every name that works is listed in "
                + ListPath() + (AtlasBridge.Found ? "" : " (Atlas, which loads modded maps, is not installed)"));
        }

        /// <summary>
        /// Runs inside the menu scene's "loaded" call, before its first frame. One of the game's own scenes starts
        /// loading here; anything else is left to Step, with the menu hidden.
        /// </summary>
        private void Early()
        {
            Pick pick = StartPick.Resolve(Wanted, GameScenes(), AtlasBridge.Maps());
            if (pick.Kind == PickKind.Game)
            {
                // Step says so in the log.
                if (string.Equals(pick.Value, SceneManager.GetActiveScene().name, StringComparison.Ordinal)) return;

                ThreadPriority before = Application.backgroundLoadingPriority;
                Curtain.Close();
                // Hand finished pieces to the scene as fast as they come; the game's own loader would crawl here.
                Application.backgroundLoadingPriority = ThreadPriority.High;
                AsyncOperation op = SceneManager.LoadSceneAsync(pick.Value, LoadSceneMode.Single);
                if (op == null)
                {
                    Application.backgroundLoadingPriority = before;
                    Curtain.Open(0f);
                    Log.LogWarning("Start scene: Unity would not start loading '" + pick.Value + "' this early; going through the menu instead");
                    return;
                }
                Finish();
                Jumped();
                m_direct = true;
                Log.LogInfo("Start scene: loading '" + pick.Value + "' now; the main menu is not shown");
                return;
            }
            // A modded map is registered by its loader a little later (or the name is wrong): Step finds out.
            Curtain.Close();
        }

        /// <summary>The scene after the menu is here.</summary>
        private void Arrived()
        {
            Busy = false;
            FastLoad.Early.LoadFeed.End(m_feed, FastLoad.Early.Clock.NowMs);
            m_feed = 0;
            if (m_direct)
            {
                m_direct = false;
                // What the game's own scene loader leaves behind after every load: slow, smooth background loading.
                Application.backgroundLoadingPriority = ThreadPriority.Low;
            }
            if (Curtain.Closed) Curtain.Open(0.5f);
        }

        /// <summary>The picture comes back whenever the reason it was taken away is gone, or took too long.</summary>
        private void CurtainWatch()
        {
            if (m_jumped)
            {
                if (m_sinceJump.Elapsed.TotalSeconds < ArriveCurtainSeconds) return;
                Curtain.Open(0.3f);
                Log.LogWarning("Start scene: '" + Wanted.Trim() + "' has not arrived after " + (int)ArriveCurtainSeconds + " s; showing the game again");
            }
            else if (m_done)
            {
                // Staying in the menu after all; the reason is in the log line before this.
                Curtain.Open(0.3f);
            }
            else if (m_sinceMenu.Elapsed.TotalSeconds >= LookCurtainSeconds)
            {
                Curtain.Open(0.3f);
                Log.LogInfo("Start scene: still looking for '" + Wanted.Trim() + "'; showing the main menu meanwhile");
            }
        }

        private void Jumped()
        {
            m_jumped = true;
            Busy = true;
            m_sinceJump.Start();
        }

        private void Finish()
        {
            m_done = true;
            // Set again by Jumped when this Finish is the one before a jump.
            Busy = false;
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
    }
}
