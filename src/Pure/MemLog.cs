using System.Collections.Generic;
using System.Threading;

namespace FastLoad
{
    public struct MemSample
    {
        public int AtMs;
        /// <summary>Private bytes this process has committed.</summary>
        public long Commit;
        public long WorkingSet;
        /// <summary>How much more the whole system can still commit.</summary>
        public long SysFree;
        /// <summary>RAM plus page files.</summary>
        public long SysLimit;
    }

    /// <summary>One memory sample per second for the whole run.</summary>
    public static class MemLog
    {
        private static readonly object s_gate = new object();
        private static readonly List<MemSample> s_samples = new List<MemSample>(4096);

        public static void Add(MemSample s)
        {
            Monitor.Enter(s_gate);
            try
            {
                s_samples.Add(s);
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        public static MemSample[] Snapshot()
        {
            Monitor.Enter(s_gate);
            try
            {
                return s_samples.ToArray();
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
                s_samples.Clear();
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        /// <summary>The first sample at or after <paramref name="ms"/>; the last one if the run ended before that.</summary>
        public static bool AtOrAfter(MemSample[] all, int ms, out MemSample found)
        {
            found = new MemSample();
            if (all.Length == 0) return false;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].AtMs >= ms)
                {
                    found = all[i];
                    return true;
                }
            }
            found = all[all.Length - 1];
            return true;
        }

        public static bool PeakCommit(MemSample[] all, out MemSample found)
        {
            found = new MemSample();
            if (all.Length == 0) return false;
            found = all[0];
            for (int i = 1; i < all.Length; i++)
            {
                if (all[i].Commit > found.Commit) found = all[i];
            }
            return true;
        }

        public static bool LowestSysFree(MemSample[] all, out MemSample found)
        {
            found = new MemSample();
            if (all.Length == 0) return false;
            found = all[0];
            for (int i = 1; i < all.Length; i++)
            {
                if (all[i].SysFree < found.SysFree) found = all[i];
            }
            return true;
        }
    }
}
