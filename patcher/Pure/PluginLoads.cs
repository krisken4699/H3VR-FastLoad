using System;
using System.Collections.Generic;
using System.Threading;

namespace FastLoad.Early
{
    /// <summary>One plugin's turn while BepInEx loads plugins: from its "Loading [...]" line to the next one.</summary>
    public sealed class PluginLoad
    {
        public string Name;
        public int StartMs;
        public int EndMs = -1;
        public long CommitStart;
        public long CommitEnd;

        /// <summary>How long its turn took; -1 while it is still the one loading.</summary>
        public int Ms
        {
            get { return EndMs < 0 ? -1 : EndMs - StartMs; }
        }

        /// <summary>Process commit growth during its turn (everything that ran then, whoever asked for it).</summary>
        public long Growth
        {
            get { return EndMs < 0 ? 0 : CommitEnd - CommitStart; }
        }

        public PluginLoad Copy()
        {
            return (PluginLoad)MemberwiseClone();
        }
    }

    /// <summary>
    /// Time and memory per plugin while the chainloader runs. BepInEx loads plugins one after another on the main
    /// thread and logs "Loading [Name Version]" before each, so the gap between two such lines is one plugin's
    /// start-up work. The frame sampler does not exist yet at that point; this is the only view into that phase.
    /// </summary>
    public static class PluginLoads
    {
        private static readonly object s_gate = new object();
        private static readonly List<PluginLoad> s_all = new List<PluginLoad>(1024);
        private static PluginLoad s_open;

        public static void Reset()
        {
            Monitor.Enter(s_gate);
            try
            {
                s_all.Clear();
                s_open = null;
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        /// <summary>True for the log lines this class needs (so the caller reads memory only for those).</summary>
        public static bool Wants(string source, string text)
        {
            if (source == null || text == null) return false;
            if (!string.Equals(source, "BepInEx", StringComparison.Ordinal)) return false;
            return text.StartsWith("Loading [", StringComparison.Ordinal)
                || string.Equals(text, "Chainloader startup complete", StringComparison.Ordinal);
        }

        /// <summary>Feeds one log line that <see cref="Wants"/> accepted.</summary>
        public static void See(string text, int ms, long commit)
        {
            if (text == null) return;
            Monitor.Enter(s_gate);
            try
            {
                if (s_open != null)
                {
                    s_open.EndMs = ms;
                    s_open.CommitEnd = commit;
                    s_open = null;
                }
                if (!text.StartsWith("Loading [", StringComparison.Ordinal)) return;
                PluginLoad p = new PluginLoad();
                int close = text.LastIndexOf(']');
                p.Name = close > 9 ? text.Substring(9, close - 9) : text;
                p.StartMs = ms;
                p.CommitStart = commit;
                s_all.Add(p);
                s_open = p;
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        public static List<PluginLoad> Snapshot()
        {
            Monitor.Enter(s_gate);
            try
            {
                List<PluginLoad> copy = new List<PluginLoad>(s_all.Count);
                for (int i = 0; i < s_all.Count; i++) copy.Add(s_all[i].Copy());
                return copy;
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }
    }
}
