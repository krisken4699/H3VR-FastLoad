using System.Collections.Generic;
using System.Threading;

namespace FastLoad.Early
{
    /// <summary>
    /// Which log sources wrote something since the last frame. The sampler reads it for a slow frame ("what was
    /// talking while this frame hung") and clears it every frame. At most 8 names; no allocation when nothing changed.
    /// </summary>
    public static class FrameSources
    {
        private const int Max = 8;
        private static readonly object s_gate = new object();
        private static readonly List<string> s_names = new List<string>(Max);

        public static void Add(string source)
        {
            if (source == null) source = "?";
            Monitor.Enter(s_gate);
            try
            {
                if (s_names.Count < Max && !s_names.Contains(source)) s_names.Add(source);
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        public static void Reset()
        {
            Monitor.Enter(s_gate);
            try
            {
                if (s_names.Count > 0) s_names.Clear();
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        public static string TakeAndReset()
        {
            Monitor.Enter(s_gate);
            try
            {
                if (s_names.Count == 0) return "";
                string joined = string.Join(", ", s_names.ToArray());
                s_names.Clear();
                return joined;
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }
    }
}
