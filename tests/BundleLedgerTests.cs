using System.Collections.Generic;
using FastLoad.Early;

namespace FastLoad.Tests
{
    public static class BundleLedgerTests
    {
        private const long MB = 1048576L;

        public static void Run()
        {
            T.Group("BundleLedger");
            BundleLedger.Reset();

            string p = @"C:\Users\Hp\AppData\Roaming\com.kesomannen.gale\h3vr\profiles\new2\BepInEx\plugins\Not_Wolfie-AR2_Plus\ar2plus-main";
            T.Eq("Not_Wolfie-AR2_Plus", BundleLedger.ModOf(p), "mod folder from a plugins path");
            T.Eq("Not_Wolfie-AR2_Plus", BundleLedger.ModOf(p.Replace('\\', '/')), "forward slashes too");
            T.Eq("Meats", BundleLedger.ModOf(@"D:\x\PLUGINS\Meats\sub\folder\bundle"), "case does not matter, nested folders");
            T.Eq("(memory)", BundleLedger.ModOf(null), "no path = loaded from memory");
            T.Eq("(game)", BundleLedger.ModOf(@"C:\Program Files (x86)\Steam\steamapps\common\H3VR\h3vr_Data\StreamingAssets\assets_x"), "not under plugins");
            T.Eq("ar2plus-main", BundleLedger.NameOf(p), "file name");
            T.Eq("(memory)", BundleLedger.NameOf(null), "name with no path");

            // A normal async load.
            BundleRecord a = BundleLedger.Begin(p, 412 * MB, "OtherLoader", true, 1000, 5000 * MB);
            T.Eq(1, BundleLedger.InFlight, "one in flight");
            T.Eq("ar2plus-main", BundleLedger.InFlightNames(4), "in-flight names");
            T.Eq(-1, a.WallMs, "wall unknown while loading");
            T.True(!a.Done, "not done");
            BundleLedger.End(a, 101, true, 19034, 5388 * MB, 212.5);
            T.Eq(0, BundleLedger.InFlight, "none in flight");
            T.Eq(1, BundleLedger.LoadedCount, "one loaded");
            T.Eq(18034, a.WallMs, "wall time");
            T.Eq(388 * MB, a.CommitGrowth, "commit growth");
            T.Eq("Not_Wolfie-AR2_Plus", a.Mod, "record mod");
            T.Eq("OtherLoader", a.By, "record caller");
            T.True(a.Done && a.Ok, "done and ok");

            // Use.
            T.Eq(0, a.AssetCalls, "no asset calls yet");
            T.True(ReferenceEquals(a, BundleLedger.Use(101, 20000)), "use finds the record by bundle id");
            BundleLedger.Use(101, 20500);
            T.Eq(2, a.AssetCalls, "asset calls counted");
            T.Eq(20500, a.LastUseMs, "last use");

            // FailedLoadIsClosed: the engine returned nothing (bundle already loaded, bad file).
            BundleRecord f = BundleLedger.Begin(@"C:\x\plugins\ModB\broken", 10 * MB, "Stratum", false, 21000, 5388 * MB);
            BundleLedger.End(f, 0, false, 21050, 5388 * MB, 50);
            T.Eq(0, BundleLedger.InFlight, "failed load is not left in flight");
            T.Eq(1, BundleLedger.LoadedCount, "failed load is not counted as loaded");
            T.True(f.Done && !f.Ok, "failed record is done, not ok");
            T.True(BundleLedger.Use(0, 21060) == null, "id 0 never matches a record");

            // UnknownBundleIsAdopted: an asset call on a bundle the hooks never saw load.
            T.True(BundleLedger.Use(999, 22000) == null, "unknown id is not found");
            BundleRecord u = BundleLedger.Adopt(999, "mystery_bundle", 22000);
            T.Eq("mystery_bundle", u.Name, "adopted name");
            T.Eq("(not seen loading)", u.Mod, "adopted mod");
            T.Eq(1, u.AssetCalls, "adopting counts the call");
            T.True(ReferenceEquals(u, BundleLedger.Use(999, 22100)), "adopted record is found next time");
            T.Eq(2, BundleLedger.LoadedCount, "adopted counts as loaded");

            // Unload.
            BundleLedger.Unloaded(a, 30000);
            T.Eq(30000, a.UnloadMs, "unload time");
            T.Eq(1, BundleLedger.LoadedCount, "unloaded leaves the loaded count");
            BundleLedger.Unloaded(a, 31000);
            T.Eq(30000, a.UnloadMs, "second unload does not move the time");
            T.True(BundleLedger.Use(101, 32000) == null, "an unloaded bundle's id is forgotten");

            // End twice / null are ignored.
            BundleLedger.End(a, 101, true, 40000, 1, 1);
            T.Eq(18034, a.WallMs, "ending twice changes nothing");
            BundleLedger.End(null, 5, true, 1, 1, 1);
            BundleLedger.Unloaded(null, 1);

            // Snapshot is a copy.
            List<BundleRecord> snap = BundleLedger.Snapshot();
            T.Eq(3, snap.Count, "snapshot has every record");
            snap[0].AssetCalls = 77;
            T.Eq(2, a.AssetCalls, "changing a snapshot does not change the ledger");

            // In-flight name list is cut.
            BundleLedger.Reset();
            for (int i = 0; i < 6; i++) BundleLedger.Begin(@"C:\x\plugins\M\b" + i, 1, "L", true, i, 0);
            T.Eq("b0, b1, b2 (+3)", BundleLedger.InFlightNames(3), "in-flight names are cut with a count");
            BundleLedger.Reset();
            T.Eq(0, BundleLedger.InFlight, "reset clears in flight");
            T.Eq("", BundleLedger.InFlightNames(3), "no names when nothing loads");

            T.Group("Memory");
            long commit, ws, free, limit;
            T.True(Memory.Read(out commit, out ws, out free, out limit), "memory read works on this PC");
            T.True(commit > MB && ws > MB, "process numbers are plausible");
            T.True(limit > free && free > 0, "system commit limit is above what is free");
            T.True(Memory.Commit() > MB, "Commit() alone");
        }
    }
}
