using System;
using System.Runtime.CompilerServices;
using BepInEx.Logging;

namespace FastLoad.Early
{
    /// <summary>
    /// The only door from preloader code to LatePlugins (which has Unity types in it; see HookGate for why that
    /// matters). Listens for BepInEx's own two lines around its plugin round.
    /// </summary>
    public sealed class LateGate : ILogListener
    {
        private bool m_started;
        private bool m_done;

        public void LogEvent(object sender, LogEventArgs e)
        {
            if (m_done) return;
            try
            {
                if (e.Source == null || e.Data == null || e.Source.SourceName != "BepInEx") return;
                string text = e.Data.ToString();
                if (!m_started && string.Equals(text, RunFiles.StartedMark, StringComparison.Ordinal))
                {
                    m_started = true;
                    Go();
                }
                else if (m_started && string.Equals(text, "Chainloader startup complete", StringComparison.Ordinal))
                {
                    m_done = true;
                    Done();
                }
            }
            catch (Exception)
            {
                // Never break another mod's log call.
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Go()
        {
            LatePlugins.Install();
            Timeline.Write("DEF", "later plugins: " + LatePlugins.Mode);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Done()
        {
            LatePlugins.Release();
            Timeline.Write("DEF", "held " + LatePlugins.HeldCount + ": " + LatePlugins.HeldText());
        }

        public void Dispose()
        {
        }
    }
}
