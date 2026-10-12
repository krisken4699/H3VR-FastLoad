using System;
using System.Collections.Generic;
using System.Text;
using FastLoad.Early;

namespace FastLoad
{
    /// <summary>Everything one report is built from. A snapshot: nothing in here changes while the text is built.</summary>
    public sealed class ReportInput
    {
        public string Profile = "";
        public string Args = "";
        public string HookMode = "";
        public int PluginCount = -1;
        /// <summary>-1 = no restart on launch.</summary>
        public int RestartCostMs = -1;
        public int NowMs;
        public float HitchMs = 25f;
        /// <summary>Indexed by Phase; -1 = not seen. Null = nothing seen.</summary>
        public int[] PhaseAt;
        public int LoaderBundleLines;
        public List<BundleRecord> Bundles = new List<BundleRecord>();
        public FrameSample[] Frames = new FrameSample[0];
        public MemSample[] Mem = new MemSample[0];
    }

    /// <summary>Turns a snapshot into the report text. No file or game access: fully testable.</summary>
    public static class ReportBuilder
    {
        public const int TopN = 40;

        private sealed class Agg
        {
            public string Name;
            public int Count;
            public long Size;
            public double MainMs;
            public long Commit;
        }

        public static string Build(ReportInput inp)
        {
            int[] at = inp.PhaseAt;
            if (at == null || at.Length != Phases.Count)
            {
                at = new int[Phases.Count];
                for (int i = 0; i < at.Length; i++) at[i] = -1;
                at[(int)Phase.ProcessStart] = 0;
            }

            StringBuilder sb = new StringBuilder(16384);
            sb.AppendLine("FastLoad probe report, written " + LineFormat.Sec(inp.NowMs) + " into the run");
            sb.AppendLine("Times are real seconds since the game process started. MB = 1048576 bytes.");

            Run(sb, inp);
            PhaseTable(sb, at);
            MenuFrames(sb, inp, at);
            BundleTables(sb, inp);
            Mods(sb, inp);
            NeverUsed(sb, inp);
            MemorySection(sb, inp, at);
            BurstSection(sb, inp, at);
            return sb.ToString();
        }

        private static void Run(StringBuilder sb, ReportInput inp)
        {
            sb.AppendLine();
            sb.AppendLine("== 1. Run ==");
            sb.AppendLine("profile: " + inp.Profile);
            sb.AppendLine("start options: " + inp.Args);
            sb.AppendLine("plugins loaded: " + (inp.PluginCount < 0 ? "unknown" : inp.PluginCount.ToString()));
            sb.AppendLine("restart on launch: " + (inp.RestartCostMs < 0 ? "no" : "yes, cost " + inp.RestartCostMs + " ms"));
            sb.AppendLine("bundle hooks: " + inp.HookMode);
            sb.AppendLine("cross-check: hooks saw " + inp.Bundles.Count + " bundle loads, loaders reported " + inp.LoaderBundleLines
                + " in their own log lines (a much higher loader number means loads are being missed)");
        }

        private static void PhaseTable(StringBuilder sb, int[] at)
        {
            sb.AppendLine();
            sb.AppendLine("== 2. Phases ==");
            sb.AppendLine("reached at | since the mark before it | phase");
            int prev = 0;
            for (int i = 0; i < Phases.Count; i++)
            {
                string since = at[i] < 0 ? "" : Gap(at[i] - prev);
                sb.AppendLine(LineFormat.Sec(at[i]).PadLeft(10) + " | " + since.PadLeft(10) + " | " + Phases.Names[i]);
                if (at[i] >= 0) prev = at[i];
            }
            sb.AppendLine("(Stratum, OtherLoader and the menu scene do not always come in this order; 'since' is against the line above that was seen.)");
        }

        /// <summary>"+12.3 s" / "-6.0 s": a phase can be reached before the one listed above it.</summary>
        private static string Gap(int ms)
        {
            return (ms < 0 ? "-" : "+") + LineFormat.N1(Math.Abs(ms) / 1000.0) + " s";
        }

        private static void MenuFrames(StringBuilder sb, ReportInput inp, int[] at)
        {
            sb.AppendLine();
            sb.AppendLine("== 3. Menu frames ==");
            int menu = at[(int)Phase.MenuLoaded];
            int quiet = at[(int)Phase.Quiet];
            if (menu < 0)
            {
                sb.AppendLine("menu scene not seen: no menu frame numbers");
                return;
            }
            if (quiet < 0)
            {
                sb.AppendLine("quiet not seen yet: the first line runs up to now");
                FrameLine(sb, "menu loaded until now", FrameLog.Stats(inp.Frames, menu, int.MaxValue), inp.NowMs - menu);
                return;
            }
            FrameLine(sb, "menu loaded until quiet", FrameLog.Stats(inp.Frames, menu, quiet), quiet - menu);
            FrameLine(sb, "30 s after quiet", FrameLog.Stats(inp.Frames, quiet, quiet + 30000), 30000);
        }

        private static void FrameLine(StringBuilder sb, string label, FrameStats s, int spanMs)
        {
            sb.AppendLine(label + " (" + LineFormat.Sec(spanMs) + "): frames=" + s.Count
                + " median=" + LineFormat.N1(s.Median) + "ms p99=" + LineFormat.N1(s.P99) + "ms worst=" + LineFormat.N0(s.Worst) + "ms"
                + " | frames of >=25ms: " + s.Over25 + ", >=50ms: " + s.Over50 + ", >=100ms: " + s.Over100 + ", >=250ms: " + s.Over250);
        }

        private static void BundleTables(StringBuilder sb, ReportInput inp)
        {
            sb.AppendLine();
            sb.AppendLine("== 4. Bundles ==");
            List<BundleRecord> done = new List<BundleRecord>();
            int loading = 0;
            int failed = 0;
            for (int i = 0; i < inp.Bundles.Count; i++)
            {
                BundleRecord r = inp.Bundles[i];
                if (!r.Done) loading++;
                else if (!r.Ok) failed++;
                else done.Add(r);
            }
            sb.AppendLine("loaded: " + done.Count + ", still loading: " + loading + ", load gave nothing: " + failed);
            sb.AppendLine("wall = start to end (waiting in a loader's queue counts). main = time the main thread sat inside the load call.");
            sb.AppendLine("commit = process commit growth while it loaded; loads that overlap share the same growth.");

            sb.AppendLine("-- top " + TopN + " by wall time --");
            Table(sb, done, ByWall);
            sb.AppendLine("-- top " + TopN + " by main-thread time --");
            Table(sb, done, ByMain);
            sb.AppendLine("-- top " + TopN + " by commit growth --");
            Table(sb, done, ByCommit);
        }

        private static void Table(StringBuilder sb, List<BundleRecord> done, Comparison<BundleRecord> order)
        {
            List<BundleRecord> sorted = new List<BundleRecord>(done);
            sorted.Sort(order);
            int n = Math.Min(TopN, sorted.Count);
            for (int i = 0; i < n; i++) sb.AppendLine(Row(sorted[i]));
            if (sorted.Count == 0) sb.AppendLine("(none)");
        }

        private static string Row(BundleRecord r)
        {
            return "wall=" + LineFormat.Sec(r.WallMs).PadLeft(8) + " main=" + (LineFormat.N0(r.MainMs) + "ms").PadLeft(8)
                + " commit=" + LineFormat.SignedMb(r.CommitGrowth).PadLeft(8) + " size=" + LineFormat.Mb(r.Size).PadLeft(7)
                + " at=" + LineFormat.Sec(r.StartMs).PadLeft(8) + " uses=" + r.AssetCalls.ToString().PadLeft(5)
                + "  " + r.Name + " | mod=" + r.Mod + " | by=" + r.By;
        }

        private static int ByWall(BundleRecord a, BundleRecord b)
        {
            return b.WallMs.CompareTo(a.WallMs);
        }

        private static int ByMain(BundleRecord a, BundleRecord b)
        {
            return b.MainMs.CompareTo(a.MainMs);
        }

        private static int ByCommit(BundleRecord a, BundleRecord b)
        {
            return b.CommitGrowth.CompareTo(a.CommitGrowth);
        }

        private static void Mods(StringBuilder sb, ReportInput inp)
        {
            sb.AppendLine();
            sb.AppendLine("== 5. Mods ==");
            sb.AppendLine("-- top " + TopN + " mod folders by commit growth --");
            Group(sb, inp.Bundles, true);
            sb.AppendLine("-- by the loader that asked for the bundles --");
            Group(sb, inp.Bundles, false);
        }

        private static void Group(StringBuilder sb, List<BundleRecord> all, bool byMod)
        {
            Dictionary<string, Agg> map = new Dictionary<string, Agg>();
            List<Agg> list = new List<Agg>();
            for (int i = 0; i < all.Count; i++)
            {
                BundleRecord r = all[i];
                if (!r.Done || !r.Ok) continue;
                string key = (byMod ? r.Mod : r.By) ?? "?";
                Agg a;
                if (!map.TryGetValue(key, out a))
                {
                    a = new Agg();
                    a.Name = key;
                    map[key] = a;
                    list.Add(a);
                }
                a.Count++;
                a.Size += r.Size;
                a.MainMs += r.MainMs;
                a.Commit += r.CommitGrowth;
            }
            list.Sort(AggByCommit);
            int n = Math.Min(TopN, list.Count);
            for (int i = 0; i < n; i++)
            {
                Agg a = list[i];
                sb.AppendLine("commit=" + LineFormat.SignedMb(a.Commit).PadLeft(8) + " main=" + (LineFormat.N0(a.MainMs) + "ms").PadLeft(8)
                    + " size=" + LineFormat.Mb(a.Size).PadLeft(7) + " bundles=" + a.Count + "  " + a.Name);
            }
            if (list.Count == 0) sb.AppendLine("(none)");
        }

        private static int AggByCommit(Agg a, Agg b)
        {
            return b.Commit.CompareTo(a.Commit);
        }

        private static void NeverUsed(StringBuilder sb, ReportInput inp)
        {
            sb.AppendLine();
            sb.AppendLine("== 6. Loaded, never used ==");
            sb.AppendLine("Bundles that loaded and had no asset call up to now. Candidates for loading later or on demand.");
            sb.AppendLine("Scene bundles (maps) show here too: scenes are not loaded through asset calls.");
            List<BundleRecord> idle = new List<BundleRecord>();
            long total = 0;
            for (int i = 0; i < inp.Bundles.Count; i++)
            {
                BundleRecord r = inp.Bundles[i];
                if (r.Done && r.Ok && r.AssetCalls == 0)
                {
                    idle.Add(r);
                    total += r.Size;
                }
            }
            idle.Sort(BySize);
            sb.AppendLine("count=" + idle.Count + " total size=" + LineFormat.Mb(total));
            for (int i = 0; i < idle.Count; i++)
            {
                BundleRecord r = idle[i];
                sb.AppendLine("size=" + LineFormat.Mb(r.Size).PadLeft(7) + " commit=" + LineFormat.SignedMb(r.CommitGrowth).PadLeft(8)
                    + (r.UnloadMs >= 0 ? " unloaded at " + LineFormat.Sec(r.UnloadMs) : " still loaded") + "  " + r.Name + " | mod=" + r.Mod);
            }
        }

        private static int BySize(BundleRecord a, BundleRecord b)
        {
            return b.Size.CompareTo(a.Size);
        }

        private static void MemorySection(StringBuilder sb, ReportInput inp, int[] at)
        {
            sb.AppendLine();
            sb.AppendLine("== 7. Memory ==");
            if (inp.Mem.Length == 0)
            {
                sb.AppendLine("no memory samples (the run ended before the first second of sampling)");
                return;
            }
            sb.AppendLine("commit = private memory this process has committed. system commit free = how much more Windows can hand out");
            sb.AppendLine("before allocations fail (RAM + page files - everything committed).");
            MemSample m;
            for (int i = 0; i < Phases.Count; i++)
            {
                if (at[i] < 0) continue;
                if (MemLog.AtOrAfter(inp.Mem, at[i], out m)) sb.AppendLine(MemRow(m) + "  at: " + Phases.Names[i]);
            }
            if (MemLog.PeakCommit(inp.Mem, out m)) sb.AppendLine("peak commit: " + LineFormat.Mb(m.Commit) + " at " + LineFormat.Sec(m.AtMs));
            if (MemLog.LowestSysFree(inp.Mem, out m))
                sb.AppendLine("lowest system commit free: " + LineFormat.Mb(m.SysFree) + " at " + LineFormat.Sec(m.AtMs)
                    + " (system commit limit " + LineFormat.Mb(m.SysLimit) + ")");
        }

        private static string MemRow(MemSample m)
        {
            return LineFormat.Sec(m.AtMs).PadLeft(10) + " commit=" + LineFormat.Mb(m.Commit).PadLeft(8) + " working set=" + LineFormat.Mb(m.WorkingSet).PadLeft(8)
                + " system commit free=" + LineFormat.Mb(m.SysFree).PadLeft(8);
        }

        private static void BurstSection(StringBuilder sb, ReportInput inp, int[] at)
        {
            sb.AppendLine();
            sb.AppendLine("== 8. Spawn bursts ==");
            int quiet = at[(int)Phase.Quiet];
            if (quiet < 0)
            {
                sb.AppendLine("quiet not seen: bursts are only counted after the menu went quiet");
                return;
            }
            sb.AppendLine("Bundle loads after quiet that began within " + (Bursts.GapMs / 1000) + " s of each other. One gun spawn or one map load is one burst.");
            List<Burst> bursts = Bursts.Find(inp.Bundles, quiet, inp.Frames, inp.HitchMs);
            if (bursts.Count == 0) sb.AppendLine("(none)");
            for (int i = 0; i < bursts.Count; i++)
            {
                Burst b = bursts[i];
                int unfinished = 0;
                for (int j = 0; j < b.Bundles.Count; j++)
                {
                    if (!b.Bundles[j].Done) unfinished++;
                }
                sb.AppendLine("burst " + (i + 1) + " at " + LineFormat.Sec(b.StartMs) + ": bundles=" + b.Bundles.Count + " size=" + LineFormat.Mb(b.Size)
                    + " took=" + LineFormat.Sec(b.EndMs - b.StartMs) + " commit=" + LineFormat.SignedMb(b.CommitGrowth) + " slow frames=" + b.Hitches
                    + (unfinished > 0 ? " (" + unfinished + " still loading)" : ""));
                for (int j = 0; j < b.Bundles.Count; j++)
                {
                    BundleRecord r = b.Bundles[j];
                    sb.AppendLine("    " + LineFormat.Mb(r.Size).PadLeft(7) + " uses=" + r.AssetCalls.ToString().PadLeft(4) + "  " + r.Name + " | mod=" + r.Mod);
                }
            }
        }
    }
}
