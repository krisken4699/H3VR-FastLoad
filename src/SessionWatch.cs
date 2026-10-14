using System;
using System.IO;
using System.Threading;
using BepInEx;
using FastLoad.Early;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FastLoad
{
    /// <summary>
    /// "[Debug] SessionLog": while someone plays, writes what each slow frame was made of, so one session gives the
    /// whole picture:
    ///   - every slow frame (HIT line) gets the split into physics / scripts / render / wait and the timed methods
    ///     that took longest in it;
    ///   - every 30 s a PRF line: average split, fps, sosigs alive, scene;
    ///   - per scene, a lag_*.txt table of every timed method (written when the scene changes and at quit).
    /// Timing a method means patching it. The list is made on a second thread; the patches go in a few at a time,
    /// most of them while a map is loading (screen black anyway), about 2 ms a frame otherwise.
    /// </summary>
    public sealed class SessionWatch : MonoBehaviour
    {
        private const float SpreadMs = 30f;

        private Thread m_finder;
        private volatile string m_found;
        private bool m_started;
        private bool m_announced;
        private string m_scene = "";
        private float m_nextPrf;
        private int m_patchedLast = -1;

        public static string Mode = "off";

        private void Update()
        {
            try
            {
                Step();
            }
            catch (Exception e)
            {
                Timeline.Write("ERR", "session watch switched off: " + e.GetType().Name + ": " + e.Message);
                enabled = false;
            }
        }

        private void Step()
        {
            string scene = SceneManager.GetActiveScene().name;
            if (!m_started)
            {
                // Start once the player is somewhere other than the menu.
                if (scene == "MainMenu3" || string.IsNullOrEmpty(scene)) return;
                m_started = true;
                m_scene = scene;
                FramePhases.Begin();
                LagProfiler.Begin();
                m_nextPrf = Time.realtimeSinceStartup + SpreadMs;
                m_finder = new Thread(Find);
                m_finder.IsBackground = true;
                m_finder.Priority = System.Threading.ThreadPriority.BelowNormal;
                m_finder.Start();
                Mode = "watching";
                Timeline.Write("PRF", "session log on in " + scene + "; listing methods to time on a second thread");
                return;
            }

            // Patches go in while the screen is black for a load; otherwise a little a frame.
            if (m_found != null)
            {
                if (!m_announced)
                {
                    m_announced = true;
                    Timeline.Write("PRF", m_found);
                }
                if (LagProfiler.Pending > 0)
                {
                    bool loading = false;
                    try
                    {
                        loading = SteamVR_LoadLevel.loading;
                    }
                    catch (Exception)
                    {
                    }
                    System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                    LagProfiler.PatchSome(loading ? 40.0 : 2.0);
                    FramePhases.PatchMs += sw.Elapsed.TotalMilliseconds;
                    if (LagProfiler.Pending == 0) Timeline.Write("PRF", "every listed method is timed now");
                }
            }

            if (scene != m_scene && !string.IsNullOrEmpty(scene))
            {
                Table(m_scene);
                Timeline.Write("PRF", "scene " + m_scene + " -> " + scene);
                m_scene = scene;
            }

            if (Time.realtimeSinceStartup >= m_nextPrf)
            {
                m_nextPrf = Time.realtimeSinceStartup + SpreadMs;
                int sosigs = -1;
                try
                {
                    sosigs = FindObjectsOfType<FistVR.Sosig>().Length;
                }
                catch (Exception)
                {
                }
                int timed = LagProfiler.Timed;
                string progress = LagProfiler.Pending > 0 ? " (" + LagProfiler.Pending + " still to time)" : "";
                Timeline.Write("PRF", "last 30 s in " + scene + ", sosigs " + sosigs + ", methods timed " + timed + progress + ": " + FramePhases.Window()
                    + " | costliest: " + LagProfiler.WindowTop(8));
                m_patchedLast = timed;
            }
        }

        private void Find()
        {
            try
            {
                m_found = LagProfiler.Find();
            }
            catch (Exception e)
            {
                m_found = "could not list methods to time: " + e.GetType().Name + ": " + e.Message;
            }
        }

        private static void Table(string label)
        {
            try
            {
                string safe = label;
                foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
                Timeline.Write("PRF", "table for " + label + ": " + LagProfiler.End(safe));
                LagProfiler.Begin();
            }
            catch (Exception e)
            {
                Timeline.Write("PRF", "table for " + label + " not written: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private void OnApplicationQuit()
        {
            if (m_started) Table(m_scene);
        }
    }
}
