using System;
using System.Diagnostics;
using BepInEx.Logging;
using FastLoad.Early;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FastLoad
{
    public enum LaterWhen
    {
        InTheMap,
        AfterItemsLoaded
    }

    /// <summary>
    /// Starts the plugins "[Later] Plugins" held back, one at a time, once the player is where they wanted to be:
    /// the first scene, or the [Start] Scene when one is set. Works with the probe switched off.
    /// </summary>
    public sealed class LaterLoader : MonoBehaviour
    {
        public LaterWhen When = LaterWhen.InTheMap;
        public ManualLogSource Log;

        /// <summary>The scene gets this long to itself before the first held plugin starts.</summary>
        private const double SettleSeconds = 3.0;
        /// <summary>Breathing room between two held plugins.</summary>
        private const double GapSeconds = 1.0;
        /// <summary>A [Start] Scene jump that never arrives does not hold the plugins back for ever.</summary>
        private const double JumpLimitSeconds = 180.0;
        /// <summary>AfterItemsLoaded on a profile where the "all items loaded" line never comes.</summary>
        private const double ItemsLimitSeconds = 600.0;

        private readonly Stopwatch m_sinceFirstScene = new Stopwatch();
        private readonly Stopwatch m_wait = new Stopwatch();
        private ItemsLoadedWatch m_items;
        private bool m_ready;
        private bool m_done;
        private bool m_said;

        private void Awake()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        public void Begin()
        {
            if (When == LaterWhen.AfterItemsLoaded)
            {
                m_items = new ItemsLoadedWatch();
                BepInEx.Logging.Logger.Listeners.Add(m_items);
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode == LoadSceneMode.Additive) return;
            if (!m_sinceFirstScene.IsRunning) m_sinceFirstScene.Start();
            // Every new scene restarts the settle time.
            m_wait.Reset();
            m_wait.Start();
        }

        private void Update()
        {
            if (m_done) return;
            try
            {
                if (LatePlugins.Remaining <= 0)
                {
                    Finish();
                    return;
                }
                if (!m_said)
                {
                    m_said = true;
                    Log.LogInfo("Later plugins: " + LatePlugins.HeldCount + " held back until "
                        + (When == LaterWhen.InTheMap ? "you are in the map" : "all items are loaded") + ": " + LatePlugins.HeldText());
                }
                if (!m_sinceFirstScene.IsRunning) return;
                double since = m_sinceFirstScene.Elapsed.TotalSeconds;

                if (!m_ready)
                {
                    if (StartScene.Busy && since < JumpLimitSeconds) return;
                    if (m_items != null && !m_items.Seen && since < ItemsLimitSeconds) return;
                    if (m_wait.Elapsed.TotalSeconds < SettleSeconds) return;
                    m_ready = true;
                }
                else if (m_wait.Elapsed.TotalSeconds < GapSeconds) return;

                string name = LatePlugins.NextName();
                Log.LogInfo("Later plugins: starting " + name + " now (the game may stand still while it loads)");
                string report;
                // The game stands still while it starts: black in the headset beats a stuck picture.
                bool curtain = !Curtain.Closed;
                if (curtain) Curtain.Close();
                try
                {
                    LatePlugins.LoadNext(out report);
                }
                finally
                {
                    if (curtain) Curtain.Open(0.3f);
                }
                Log.LogInfo("Later plugins: " + report + ". " + LatePlugins.Remaining + " left.");
                m_wait.Reset();
                m_wait.Start();
            }
            catch (Exception e)
            {
                Finish();
                Log.LogError("Later plugins stopped: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private void Finish()
        {
            m_done = true;
            if (m_items != null)
            {
                BepInEx.Logging.Logger.Listeners.Remove(m_items);
                m_items = null;
            }
        }
    }

    /// <summary>Hears OtherLoader say that every item is loaded.</summary>
    public sealed class ItemsLoadedWatch : ILogListener
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
