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
                if (PluginLoads.Wants(source, text)) PluginLoads.See(text, now, Memory.Commit());
                if (Phases.SeeLog(source, text, now))
                {
                    // The frame sampler only exists once plugins have loaded: memory at the early marks is read here.
                    long commit, ws, free, limit;
                    Memory.Read(out commit, out ws, out free, out limit);
                    int which = Phases.LastMarked;
                    Timeline.Write("PHS", (which >= 0 ? Phases.Names[which] : "?") + " at " + now + " commit=" + LineFormat.Mb(commit)
                        + " ws=" + LineFormat.Mb(ws) + " sysfree=" + LineFormat.Mb(free));
                    Timeline.Write("RES", DllLookup.Summary());
                    // Plugins start loading right after this line: hook bundle loading before any of their code runs.
                    if (string.Equals(text, RunFiles.StartedMark, StringComparison.Ordinal))
                    {
                        try
                        {
                            HookGate.Go();
                        }
                        catch (Exception hookError)
                        {
                            Timeline.Write("ERR", "bundle hooks not installed: " + hookError.GetType().Name + ": " + hookError.Message);
                        }
                    }
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
