using System;
using System.Collections.Generic;
using System.Threading;

namespace FastLoad
{
    public struct FrameSample
    {
        /// <summary>Run-clock time at which the frame ended.</summary>
        public int AtMs;
        /// <summary>How long the frame took, real time.</summary>
        public float Ms;
    }

    public struct FrameStats
    {
        public int Count;
        public int Over25;
        public int Over50;
        public int Over100;
        public int Over250;
        public float Median;
        public float P99;
        public float Worst;
    }

    /// <summary>Every frame time of the run (8 bytes each: an hour at 72 fps is about 2 MB).</summary>
    public static class FrameLog
    {
        private static readonly object s_gate = new object();
        private static readonly List<FrameSample> s_frames = new List<FrameSample>(65536);

        public static void Add(int atMs, float ms)
        {
            FrameSample f = new FrameSample();
            f.AtMs = atMs;
            f.Ms = ms;
            Monitor.Enter(s_gate);
            try
            {
                s_frames.Add(f);
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        public static FrameSample[] Snapshot()
        {
            Monitor.Enter(s_gate);
            try
            {
                return s_frames.ToArray();
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
                s_frames.Clear();
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        /// <summary>Stats of the frames with fromMs &lt;= AtMs &lt; toMs.</summary>
        public static FrameStats Stats(FrameSample[] all, int fromMs, int toMs)
        {
            FrameStats s = new FrameStats();
            List<float> ms = new List<float>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].AtMs < fromMs || all[i].AtMs >= toMs) continue;
                float v = all[i].Ms;
                ms.Add(v);
                if (v >= 25f) s.Over25++;
                if (v >= 50f) s.Over50++;
                if (v >= 100f) s.Over100++;
                if (v >= 250f) s.Over250++;
                if (v > s.Worst) s.Worst = v;
            }
            s.Count = ms.Count;
            if (ms.Count == 0) return s;
            ms.Sort();
            s.Median = ms[ms.Count / 2];
            s.P99 = ms[Math.Min(ms.Count - 1, (int)(ms.Count * 0.99))];
            return s;
        }

        public static int CountAtLeast(FrameSample[] all, int fromMs, int toMs, float ms)
        {
            int n = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].AtMs >= fromMs && all[i].AtMs < toMs && all[i].Ms >= ms) n++;
            }
            return n;
        }
    }
}
