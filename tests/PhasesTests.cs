using FastLoad.Early;

namespace FastLoad.Tests
{
    public static class PhasesTests
    {
        public static void Run()
        {
            T.Group("Phases");
            Phases.Reset();

            T.Eq(0, Phases.At(Phase.ProcessStart), "process start is always 0");
            T.Eq(-1, Phases.At(Phase.MenuLoaded), "unseen phase is -1");

            T.True(!Phases.SeeLog("BepInEx", "Preloader started", 700), "unrelated line marks nothing");
            T.True(Phases.SeeLog("BepInEx", "Preloader finished", 4100), "preloader finished");
            T.True(Phases.SeeLog("BepInEx", "Chainloader started", 4300), "chainloader started");
            // BepInEx replays the preloader's lines to every listener when the chainloader starts.
            T.True(!Phases.SeeLog("BepInEx", "Preloader finished", 4350), "replayed line does not move the mark");
            T.Eq(4100, Phases.At(Phase.PreloaderDone), "first time wins");

            T.True(!Phases.SeeLog("SomeMod", "Chainloader started", 5000), "wrong source is ignored");
            T.True(Phases.SeeLog("BepInEx", "Chainloader startup complete", 31000), "chainloader done");
            T.True(Phases.SeeLog("Stratum", "Loading complete (50.341s)", 82000), "stratum done");
            T.True(Phases.SeeLog("OtherLoaderPatched", "All items loaded, took 69.703 seconds!", 101000), "otherloader done");

            T.True(!Phases.SeeScene("TakeAndHold_Lobby_2", 90000), "another scene is not the menu");
            T.True(Phases.SeeScene("MainMenu3", 95000), "menu scene");
            T.True(!Phases.SeeScene("MainMenu3", 400000), "coming back to the menu does not move the mark");
            T.Eq(95000, Phases.At(Phase.MenuLoaded), "menu mark");

            T.True(Phases.Mark(Phase.Quiet, 130000), "quiet set from outside");
            T.True(!Phases.Mark(Phase.Quiet, 140000), "quiet set once");

            Phases.SeeLog("OtherLoaderPatched", "Completed loading bundle 'shotclock' in 19.131 seconds!", 60000);
            Phases.SeeLog("ModulWorkshop_Optimizer", "[BundleLoader] On-demand async loading modular bundle: C:\\x\\mw_ar2_plus", 200000);
            Phases.SeeLog("OtherLoaderPatched", "Spawning item by ItemSpawnerID 'AttIronBackup1'", 200100);
            T.Eq(2, Phases.LoaderBundleLines, "loader bundle lines counted");
            // Review I2: the real log also has the sync wording.
            Phases.SeeLog("ModulWorkshop_Optimizer", "[BundleLoader] On-demand SYNC loading modular bundle: C:/x/mw_tti_glock", 200200);
            T.Eq(3, Phases.LoaderBundleLines, "sync on-demand loads are counted too");
            T.Eq((int)Phase.Quiet, Phases.LastMarked, "the last phase marked is remembered");

            int[] snap = Phases.Snapshot();
            T.Eq(Phases.Count, snap.Length, "snapshot length");
            T.Eq(82000, snap[(int)Phase.StratumDone], "snapshot value");
            T.Eq(Phases.Count, Phases.Names.Length, "a name for every phase");

            Phases.SeeLog(null, null, 1);
            T.True(true, "null source and text do not throw");

            Phases.Reset();
            T.Eq(-1, Phases.At(Phase.StratumDone), "reset clears marks");
            T.Eq(0, Phases.LoaderBundleLines, "reset clears the count");
            T.Eq(-1, Phases.LastMarked, "reset clears the last mark");

            T.Group("FrameSources");
            FrameSources.Reset();
            T.Eq("", FrameSources.TakeAndReset(), "nothing logged");
            FrameSources.Add("OtherLoaderPatched");
            FrameSources.Add("TNHFramework");
            FrameSources.Add("OtherLoaderPatched");
            T.Eq("OtherLoaderPatched, TNHFramework", FrameSources.TakeAndReset(), "distinct sources in order");
            T.Eq("", FrameSources.TakeAndReset(), "taking resets");
            for (int i = 0; i < 20; i++) FrameSources.Add("m" + i);
            T.Eq(8, FrameSources.TakeAndReset().Split(',').Length, "at most 8 sources per frame");
            FrameSources.Add(null);
            T.Eq("?", FrameSources.TakeAndReset(), "null source is shown as ?");
        }
    }
}
