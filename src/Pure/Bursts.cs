using System.Collections.Generic;
using FastLoad.Early;

namespace FastLoad
{
    /// <summary>Bundle loads that began within 2 s of each other after the menu went quiet: one spawn, one map load.</summary>
    public sealed class Burst
    {
        public int StartMs;
        public int EndMs;
        public List<BundleRecord> Bundles = new List<BundleRecord>();
        public long Size;
        /// <summary>Process commit at the last bundle's end minus commit at the first one's start.</summary>
        public long CommitGrowth;
        /// <summary>Slow frames between the burst's start and end.</summary>
        public int Hitches;
    }

    public static class Bursts
    {
        public const int GapMs = 2000;

        /// <param name="afterMs">The quiet mark; negative = quiet never came, no bursts.</param>
        public static List<Burst> Find(List<BundleRecord> records, int afterMs, FrameSample[] frames, float hitchMs)
        {
            List<Burst> found = new List<Burst>();
            if (afterMs < 0) return found;

            List<BundleRecord> late = new List<BundleRecord>();
            for (int i = 0; i < records.Count; i++)
            {
                if (records[i].StartMs >= afterMs) late.Add(records[i]);
            }
            late.Sort(ByStart);

            Burst cur = null;
            int lastStart = 0;
            BundleRecord lastEnded = null;
            for (int i = 0; i < late.Count; i++)
            {
                BundleRecord r = late[i];
                if (cur == null || r.StartMs - lastStart > GapMs)
                {
                    Finish(cur, lastEnded, frames, hitchMs);
                    cur = new Burst();
                    cur.StartMs = r.StartMs;
                    cur.EndMs = r.StartMs;
                    lastEnded = null;
                    found.Add(cur);
                }
                cur.Bundles.Add(r);
                cur.Size += r.Size;
                lastStart = r.StartMs;
                if (r.EndMs >= 0 && r.EndMs >= cur.EndMs)
                {
                    cur.EndMs = r.EndMs;
                    lastEnded = r;
                }
            }
            Finish(cur, lastEnded, frames, hitchMs);
            return found;
        }

        private static void Finish(Burst b, BundleRecord lastEnded, FrameSample[] frames, float hitchMs)
        {
            if (b == null) return;
            if (lastEnded != null) b.CommitGrowth = lastEnded.CommitEnd - b.Bundles[0].CommitStart;
            b.Hitches = FrameLog.CountAtLeast(frames, b.StartMs, b.EndMs + 1, hitchMs);
        }

        private static int ByStart(BundleRecord a, BundleRecord b)
        {
            return a.StartMs.CompareTo(b.StartMs);
        }
    }
}
