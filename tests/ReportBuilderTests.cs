using System;
using System.Collections.Generic;
using FastLoad.Early;

namespace FastLoad.Tests
{
    public static class ReportBuilderTests
    {
        private const long MB = 1048576L;

        private static BundleRecord Rec(string mod, string name, string by, int start, int end, long size, double main, long growthMb, int assetCalls)
        {
            BundleRecord r = new BundleRecord();
            r.Path = @"C:\p\plugins\" + mod + @"\" + name;
            r.Name = name;
            r.Mod = mod;
            r.By = by;
            r.StartMs = start;
            r.EndMs = end;
            r.Size = size;
            r.MainMs = main;
            r.CommitStart = 5000 * MB;
            r.CommitEnd = (5000 + growthMb) * MB;
            r.Ok = true;
            r.AssetCalls = assetCalls;
            return r;
        }

        private static string Section(string report, string title)
        {
            int at = report.IndexOf(title, StringComparison.Ordinal);
            if (at < 0) return "";
            int next = report.IndexOf("\n== ", at + title.Length, StringComparison.Ordinal);
            return next < 0 ? report.Substring(at) : report.Substring(at, next - at);
        }

        public static void Run()
        {
            T.Group("ReportBuilder");

            // EmptyRunBuilds: the game died before anything was measured.
            string empty = null;
            bool threw = false;
            try { empty = ReportBuilder.Build(new ReportInput()); }
            catch (Exception) { threw = true; }
            T.True(!threw && empty != null, "a report builds from nothing");
            for (int i = 1; i <= 9; i++) T.True(empty.Contains("== " + i + ". "), "empty report has section " + i);

            // MissingPhasesSayNotSeen
            ReportInput early = new ReportInput();
            early.NowMs = 30000;
            early.PhaseAt = new int[] { 0, 4100, 4300, -1, -1, -1, -1, -1 };
            string er = ReportBuilder.Build(early);
            string phases = Section(er, "== 2. Phases");
            T.True(phases.Contains("4.1 s"), "seen phase shows its time");
            T.True(phases.Split(new string[] { "not seen" }, StringSplitOptions.None).Length - 1 == 5, "five unseen phases say not seen: " + phases);
            T.True(Section(er, "== 3. Menu frames").Contains("menu scene not seen"), "menu frames say why they are missing");
            T.True(Section(er, "== 8. Spawn bursts").Contains("quiet not seen"), "bursts say why they are missing");

            // A full run.
            ReportInput inp = new ReportInput();
            inp.Profile = @"C:\profiles\new2\BepInEx";
            inp.Args = "h3vr.exe -force-gfx-jobs";
            inp.HookMode = "managed, 12 of 12 hooks on";
            inp.PluginCount = 767;
            inp.RestartCostMs = 1166;
            inp.NowMs = 400000;
            inp.HitchMs = 25f;
            inp.LoaderBundleLines = 5;
            inp.PhaseAt = new int[] { 0, 4100, 4300, 31000, 82000, 101000, 95000, 130000 };

            inp.Bundles.Add(Rec("Not_Wolfie-AR2_Plus", "ar2plus-main", "OtherLoader", 40000, 58034, 412 * MB, 212, 388, 30));
            inp.Bundles.Add(Rec("Meat_banono-Meats_ModulAR", "modular", "OtherLoader", 41000, 45000, 900 * MB, 1500, 950, 0));
            inp.Bundles.Add(Rec("Marcel-TNH_Modern_Mansion", "mansion", "Atlas", 42000, 43000, 790 * MB, 20, 5, 0));
            inp.Bundles.Add(Rec("Not_Wolfie-AR2_Plus", "mw_ar2_plus", "ModulWorkshopOptimizer", 200000, 201000, 50 * MB, 40, 60, 12));
            inp.Bundles.Add(Rec("Volks-AR2VPack", "mw_ar2vpack", "ModulWorkshopOptimizer", 200500, 203000, 1020 * MB, 90, 800, 3));
            BundleRecord loading = Rec("Slow-Mod", "still_loading", "Stratum", 390000, -1, 10 * MB, 0, 0, 0);
            loading.Ok = false;
            inp.Bundles.Add(loading);

            List<FrameSample> fl = new List<FrameSample>();
            for (int t = 95000; t < 170000; t += 14)
            {
                FrameSample f = new FrameSample();
                f.AtMs = t;
                f.Ms = (t < 130000 && (t / 14) % 50 == 0) ? 120f : 14f;
                fl.Add(f);
            }
            FrameSample hit = new FrameSample();
            hit.AtMs = 201500;
            hit.Ms = 300f;
            fl.Add(hit);
            inp.Frames = fl.ToArray();

            List<MemSample> ml = new List<MemSample>();
            for (int t = 31000; t <= 400000; t += 1000)
            {
                MemSample m = new MemSample();
                m.AtMs = t;
                m.Commit = (4000 + t / 100) * MB;
                m.WorkingSet = (3000 + t / 200) * MB;
                m.SysFree = (30000 - t / 20) * MB;
                m.SysLimit = 52700 * MB;
                ml.Add(m);
            }
            inp.Mem = ml.ToArray();

            PluginLoad pa = new PluginLoad();
            pa.Name = "H3VR Sideloader 0.3.6"; pa.StartMs = 36500; pa.EndMs = 76500; pa.CommitStart = 3000 * MB; pa.CommitEnd = 3200 * MB;
            PluginLoad pb = new PluginLoad();
            pb.Name = "TNHFramework 0.2.4"; pb.StartMs = 76500; pb.EndMs = 81500; pb.CommitStart = 3200 * MB; pb.CommitEnd = 9200 * MB;
            PluginLoad pc = new PluginLoad();
            pc.Name = "StillLoading 1.0"; pc.StartMs = 81500;
            inp.Plugins.Add(pa);
            inp.Plugins.Add(pb);
            inp.Plugins.Add(pc);

            string r = ReportBuilder.Build(inp);

            string pl = Section(r, "== 9. Plugin loading");
            T.True(pl.Contains("by time") && pl.Contains("by commit growth"), "plugins: two tables");
            T.True(pl.IndexOf("H3VR Sideloader", StringComparison.Ordinal) < pl.IndexOf("TNHFramework", StringComparison.Ordinal), "plugins: slowest first in the time table");
            T.True(pl.Contains("40.0 s") && pl.Contains("+6000MB"), "plugins: time and growth shown: " + pl);
            T.True(pl.Contains("StillLoading 1.0") && pl.Contains("was loading when this was written"), "plugins: the one in progress is named");

            // Loads that overlap share one commit growth: a group must not add the same growth up many times.
            string modsNow = Section(r, "== 5. Mods");
            T.True(modsNow.Contains("over the group's span"), "mods: says how commit is counted");
            T.True(!modsNow.Contains("+1338MB"), "mods: the loader row is not a sum of overlapping growth: " + modsNow);

            string run = Section(r, "== 1. Run");
            T.True(run.Contains(@"C:\profiles\new2\BepInEx"), "run: profile");
            T.True(run.Contains("767"), "run: plugin count");
            T.True(run.Contains("restart on launch: yes, cost 1166 ms"), "run: restart cost");
            T.True(run.Contains("managed, 12 of 12 hooks on"), "run: hook mode");
            T.True(run.Contains("hooks saw 6") && run.Contains("loaders reported 5"), "run: cross-check numbers: " + run);
            T.True(run.Contains("not seen loading: 0"), "run: cross-check says how many bundles were only seen in use");

            string ph = Section(r, "== 2. Phases");
            T.True(ph.Contains("82.0 s") && ph.Contains("101.0 s") && ph.Contains("130.0 s"), "phases: times");
            T.True(!ph.Contains("not seen"), "phases: all seen");
            T.True(ph.Contains("-6.0 s"), "phases: the menu came 6 s before OtherLoader finished, shown as a negative gap: " + ph);

            string mf = Section(r, "== 3. Menu frames");
            T.True(mf.Contains("menu loaded until quiet") && mf.Contains("30 s after quiet"), "menu frames: both windows");

            string bu = Section(r, "== 4. Bundles");
            T.True(bu.IndexOf("ar2plus-main", StringComparison.Ordinal) < bu.IndexOf("modular", StringComparison.Ordinal), "bundles: longest wall time first");
            T.True(bu.Contains("by main-thread time") && bu.Contains("by commit growth"), "bundles: three tables");
            T.True(bu.Contains("still loading: 1"), "bundles: unfinished loads are counted, not ranked");

            string mods = Section(r, "== 5. Mods");
            T.True(mods.Contains("Not_Wolfie-AR2_Plus") && mods.Contains("bundles=2"), "mods: grouped by folder");
            T.True(mods.Contains("OtherLoader") && mods.Contains("ModulWorkshopOptimizer"), "mods: per loader");

            string nu = Section(r, "== 6. Loaded, never used");
            T.True(nu.Contains("modular") && nu.Contains("mansion"), "never used: listed");
            T.True(!nu.Contains("ar2plus-main"), "never used: a used bundle is not listed");
            T.True(nu.Contains("scene"), "never used: says scene bundles show here too");

            string me = Section(r, "== 7. Memory");
            T.True(me.Contains("peak commit") && me.Contains("lowest system commit free") && me.Contains("52700MB"), "memory: peak, low, limit");

            string sb = Section(r, "== 8. Spawn bursts");
            T.True(sb.Contains("bundles=2") && sb.Contains("mw_ar2vpack") && sb.Contains("slow frames=1"), "bursts: the AR2 spawn: " + sb);

            T.True(sb.Contains("(1 still loading)"), "bursts: an unfinished load is said to be unfinished: " + sb);

            T.True(bu.Contains(@"path=C:\p\plugins\Not_Wolfie-AR2_Plus\ar2plus-main"), "bundles: full path is printed");

            // Review I2: a bundle only seen in use is a missed load, not a load the hooks saw.
            BundleRecord adopted = Rec("x", "ghost", "?", 250000, 250000, 0, 0, 0, 4);
            adopted.Mod = "(not seen loading)";
            inp.Bundles.Add(adopted);
            string run2 = Section(ReportBuilder.Build(inp), "== 1. Run");
            T.True(run2.Contains("hooks saw 6") && run2.Contains("not seen loading: 1"), "cross-check keeps missed loads apart: " + run2);
            inp.Bundles.Remove(adopted);

            // Review I3: the sampler starts with the plugin; an early phase has no memory sample of its own.
            T.True(me.Contains("no sample at that time"), "memory: early phases do not borrow a later sample: " + me);

            // Review I4: a headset that never gives 10 clean seconds must not empty the burst list.
            int keptQuiet = inp.PhaseAt[(int)Phase.Quiet];
            inp.PhaseAt[(int)Phase.Quiet] = -1;
            string noQuiet = Section(ReportBuilder.Build(inp), "== 8. Spawn bursts");
            T.True(noQuiet.Contains("quiet not seen") && noQuiet.Contains("counted from") && noQuiet.Contains("mw_ar2vpack"), "bursts fall back to the later of menu / OtherLoader done: " + noQuiet);
            inp.PhaseAt[(int)Phase.Quiet] = keptQuiet;

            // Top tables are cut at TopN.
            ReportInput many = new ReportInput();
            for (int i = 0; i < 100; i++) many.Bundles.Add(Rec("Mod" + i, "bundle" + i, "L", 1000, 1000 + i, MB, i, i, 1));
            string mr = ReportBuilder.Build(many);
            string top = Section(mr, "== 4. Bundles");
            T.True(top.Contains("bundle99") && !top.Contains("bundle10 "), "bundles: only the top " + ReportBuilder.TopN + " are listed");
        }
    }
}
