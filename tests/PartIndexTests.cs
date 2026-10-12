using System.Collections.Generic;
using FastLoad.Early;

namespace FastLoad.Tests
{
    public static class PartIndexTests
    {
        private static Dictionary<string, bool> Set(params string[] keys)
        {
            Dictionary<string, bool> d = new Dictionary<string, bool>();
            for (int i = 0; i < keys.Length; i++) d[keys[i]] = true;
            return d;
        }

        public static void Run()
        {
            T.Group("PartIndex");

            PartIndex index = new PartIndex();
            PartIndex.Entry stocks = index.Learn(@"C:\mods\A\mw_stocks", 100, 5);
            PartIndex.AddPart(stocks, "AR15 Stock", "Magpul CTR");
            PartIndex.AddPart(stocks, "AR15 Stock", "B5 Sopmod");
            PartIndex.AddSkin(stocks, "AR15 Stock/Magpul CTR");
            PartIndex.Entry barrels = index.Learn(@"C:\mods\B\mw_barrels", 200, 6);
            PartIndex.AddPart(barrels, "AR15 Barrel", "16in Carbine");
            PartIndex.Entry empty = index.Learn(@"C:\mods\C\mw_empty", 1, 1);

            T.True(index.Find(@"c:\MODS\a\mw_stocks", 100, 5) != null, "found whatever the capitals in the path");
            T.True(index.Find(@"C:\mods\A\mw_stocks", 101, 5) == null, "another size = changed file = unknown");
            T.True(index.Find(@"C:\mods\A\mw_stocks", 100, 9) == null, "another time = changed file = unknown");
            T.True(index.Find(@"C:\mods\Z\never", 1, 1) == null, "never learned = unknown");
            T.True(index.Find(null, 1, 1) == null, "no path = unknown");

            Dictionary<string, bool> none = Set();
            T.True(PartIndex.Needed(null, none, none, null), "an unknown bundle is always needed");
            T.True(PartIndex.Needed(stocks, Set(PartIndex.PartKey("AR15 Stock", "B5 Sopmod")), none, null), "needed: holds a part that is on the gun");
            T.True(!PartIndex.Needed(stocks, Set(PartIndex.PartKey("AR15 Stock", "Some Other")), none, null), "not needed: same group, other part");
            T.True(!PartIndex.Needed(barrels, Set(PartIndex.PartKey("AR15 Stock", "16in Carbine")), none, null), "not needed: same part name in another group");
            T.True(PartIndex.Needed(stocks, none, Set("AR15 Stock/Magpul CTR"), null), "needed: holds skins for a part on the gun");
            T.True(PartIndex.Needed(barrels, none, none, Set("AR15 Barrel")), "needed: the whole group was asked for (workshop open)");
            T.True(!PartIndex.Needed(barrels, none, none, Set("AR15 Stock")), "not needed: another group was asked for");
            T.True(PartIndex.Needed(empty, none, none, null), "a bundle with nothing nameable in it is not judged");

            PartIndex back = PartIndex.Parse(index.Serialize());
            T.Eq(3, back.Count, "three bundles survive writing and reading");
            PartIndex.Entry again = back.Find(@"C:\mods\A\mw_stocks", 100, 5);
            T.True(again != null && again.Parts.Count == 2 && again.Skins.Count == 1 && again.Groups.Count == 1, "parts, skins and groups survive");
            T.True(again != null && again.Parts.ContainsKey("AR15 Stock|Magpul CTR"), "part key survives with its space");

            T.Eq(0, PartIndex.Parse(null).Count, "nothing to read = empty");
            T.Eq(0, PartIndex.Parse("some other file\nB\tx\t1\t1\n").Count, "a file that is not ours = empty");
            T.Eq(1, PartIndex.Parse("FastLoad modular part index v1\nP\torphan|x\nB\tx\tnot a number\t1\nG\tlost\nB\ty\t1\t2\nG\tg\nrubbish\n").Count, "broken lines are skipped, good ones kept");
        }
    }
}
