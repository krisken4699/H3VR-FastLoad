using System;
using BepInEx.Logging;

namespace FastLoad.Early
{
    /// <summary>
    /// Gets every BepInEx log event (all mods, Unity's own log included) and writes it to the timeline with the run
    /// clock. BepInEx keeps listeners added in the preloader for the whole run. When the chainloader starts it
    /// replays the preloader's lines to every listener, so those lines appear twice; phase marks keep the first.
    /// </summary>
    public sealed class LogMirror : ILogListener
    {
        public void LogEvent(object sender, LogEventArgs e)
        {
            try
            {
                string source = e.Source != null ? e.Source.SourceName : "?";
                string text = e.Data != null ? e.Data.ToString() : "";
                Timeline.Write("LOG", "[" + e.Level + ":" + source + "] " + text);
                FrameSources.Add(source);

                int now = Clock.NowMs;
                if (Phases.SeeLog(source, text, now))
                {
                    Timeline.Write("PHS", "mark at " + now);
                    // Plugins start loading right after this line: hook bundle loading before any of their code runs.
                    if (string.Equals(text, RunFiles.StartedMark, StringComparison.Ordinal)) HookGate.Go();
                }
            }
            catch (Exception)
            {
                // Never let the probe break another mod's log call.
            }
        }

        public void Dispose()
        {
        }
    }
}
