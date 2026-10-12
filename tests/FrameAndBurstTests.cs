using System.Collections.Generic;
using FastLoad.Early;

namespace FastLoad.Tests
{
    public static class FrameAndBurstTests
    {
        private const long MB = 1048576L;

        private static BundleRecord Rec(string name, int start, int end, long size, long commitStart, long commitEnd)
        {
            BundleRecord r = new BundleRecord();
            r.Name = name;
            r.Mod = "M";
            r.By = "L";
            r.StartMs = start;
            r.EndMs = end;
            r.Size = size;
            r.CommitStart = commitStart;
            r.CommitEnd = commitEnd;
            r.Ok = end >= 0;
            return r;
        }

        public static void Run()
        {
            T.Group("FrameLog");
            FrameLog.Reset();
            // 100 frames of 14 ms from t=1000, with four slow ones.
            for (int i = 0; i < 100; i++)
            {
                float ms = 14f;
                if (i == 10) ms = 30f;
                if (i == 20) ms = 60f;
                if (i == 30) ms = 120f;
                if (i == 40) ms = 300f;
                FrameLog.Add(1000 + i * 14, ms);
            }
            FrameSample[] frames = FrameLog.Snapshot();
            T.Eq(100, frames.Length, "all frames kept");

            FrameStats all = FrameLog.Stats(frames, 0, int.MaxValue);
            T.Eq(100, all.Count, "count");
            T.Eq(4, all.Over25, "frames of 25 ms or more");
            T.Eq(3, all.Over50, "frames of 50 ms or more");
            T.Eq(2, all.Over100, "frames of 100 ms or more");
            T.Eq(1, all.Over250, "frames of 250 ms or more");
            T.Eq(14f, all.Median, "median");
            T.Eq(300f, all.Worst, "worst");
            T.True(all.P99 >= 120f, "99th percentile is among the slow frames: " + all.P99);

            FrameStats window = FrameLog.Stats(frames, 1000, 1000 + 15 * 14);
            T.Eq(15, window.Count, "window is from inclusive, to exclusive");
            T.Eq(1, window.Over25, "window sees only its own slow frame");

            FrameStats none = FrameLog.Stats(frames, 900000, 900001);
            T.Eq(0, none.Count, "empty window");
            T.Eq(0f, none.Median, "empty window median is 0");
            T.Eq(0, FrameLog.Stats(new FrameSample[0], 0, 10).Count, "no frames at all");
            T.Eq(2, FrameLog.CountAtLeast(frames, 1000 + 25 * 14, int.MaxValue, 100f), "count at least, in a window");

            T.Group("MemLog");
            MemLog.Reset();
            MemSample found;
            T.True(!MemLog.AtOrAfter(MemLog.Snapshot(), 5, out found), "nothing sampled yet");
            for (int i = 1; i <= 5; i++)
            {
                MemSample s = new MemSample();
                s.AtMs = i * 1000;
                s.Commit = (i == 4 ? 20000 : 10000 + i) * MB;
                s.WorkingSet = 8000 * MB;
                s.SysFree = (i == 3 ? 900 : 5000) * MB;
                s.SysLimit = 52700 * MB;
                MemLog.Add(s);
            }
            MemSample[] mem = MemLog.Snapshot();
            T.True(MemLog.AtOrAfter(mem, 2500, out found) && found.AtMs == 3000, "first sample at or after the mark");
            T.True(MemLog.AtOrAfter(mem, 99999, out found) && found.AtMs == 5000, "after the last sample gives the last");
            T.True(MemLog.PeakCommit(mem, out found) && found.AtMs == 4000, "peak commit");
            T.True(MemLog.LowestSysFree(mem, out found) && found.AtMs == 3000, "lowest system commit free");
            T.True(!MemLog.PeakCommit(new MemSample[0], out found), "no samples, no peak");

            T.Group("QuietTracker");
            QuietTracker q = new QuietTracker();
            T.Eq(-1, q.Step(1000, -1, false), "no menu yet: never quiet");
            T.Eq(-1, q.Step(50000, 50000, false), "window starts");
            T.Eq(-1, q.Step(55000, 50000, true), "busy restarts the window");
            T.Eq(-1, q.Step(64999, 50000, false), "not yet 10 s since the last busy moment");
            T.Eq(55000, q.Step(65000, 50000, false), "quiet reported once, with the time it began");
            T.Eq(-1, q.Step(90000, 50000, false), "never reported again");

            T.Group("Bursts");
            List<BundleRecord> recs = new List<BundleRecord>();
            recs.Add(Rec("boot1", 1000, 5000, 100 * MB, 1000 * MB, 1100 * MB));       // before quiet: ignored
            recs.Add(Rec("ar2_c", 201500, 204000, 30 * MB, 5020 * MB, 5090 * MB));    // out of order on purpose
            recs.Add(Rec("ar2_a", 200000, 201000, 10 * MB, 5000 * MB, 5010 * MB));
            recs.Add(Rec("ar2_b", 200900, 203000, 20 * MB, 5008 * MB, 5060 * MB));
            recs.Add(Rec("map_x", 300000, -1, 700 * MB, 6000 * MB, 0));               // still loading
            FrameSample[] hits = new FrameSample[3];
            hits[0].AtMs = 200500; hits[0].Ms = 80f;
            hits[1].AtMs = 203900; hits[1].Ms = 40f;
            hits[2].AtMs = 250000; hits[2].Ms = 90f;

            List<Burst> bursts = Bursts.Find(recs, 100000, hits, 25f);
            T.Eq(2, bursts.Count, "two bursts after quiet");
            Burst b = bursts[0];
            T.Eq(3, b.Bundles.Count, "three bundles within 2 s of each other");
            T.Eq("ar2_a", b.Bundles[0].Name, "bundles in start order");
            T.Eq(200000, b.StartMs, "burst start");
            T.Eq(204000, b.EndMs, "burst end = last bundle to finish");
            T.Eq(60 * MB, b.Size, "burst size");
            T.Eq(90 * MB, b.CommitGrowth, "commit growth from first start to last end");
            T.Eq(2, b.Hitches, "slow frames inside the burst");
            T.Eq(1, bursts[1].Bundles.Count, "second burst");
            T.Eq(300000, bursts[1].EndMs, "unfinished bundle: burst end is its start");
            T.Eq(0L, bursts[1].CommitGrowth, "unfinished bundle: no growth claimed");

            T.Eq(0, Bursts.Find(recs, -1, hits, 25f).Count, "no quiet mark, no bursts");
            T.Eq(0, Bursts.Find(new List<BundleRecord>(), 100000, hits, 25f).Count, "no records, no bursts");
        }
    }
}
