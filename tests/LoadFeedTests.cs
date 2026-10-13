using FastLoad.Early;

namespace FastLoad.Tests
{
    public static class LoadFeedTests
    {
        public static void Run()
        {
            T.Group("LoadFeed");
            string text;

            LoadFeed.Reset();
            T.True(!LoadFeed.Line(0, 300, out text), "nothing loading = nothing to show");

            int a = LoadFeed.Begin("modulglock", "Arpy-ModulGlock", false, false, 1000);
            T.True(!LoadFeed.Line(1100, 300, out text), "a load younger than the minimum age is not shown");
            T.True(LoadFeed.Line(1400, 300, out text), "shown once it lasts");
            T.Eq("Loading modulglock from Arpy-ModulGlock", text, "one load: what and which mod, no count");
            LoadFeed.End(a, 1500);
            T.True(!LoadFeed.Line(1600, 300, out text), "gone when done");

            // Next load inside the gap: same batch, counted.
            int b = LoadFeed.Begin("shotclock", "Melon-Shotclock", false, false, 2000);
            LoadFeed.Line(2400, 300, out text);
            T.Eq("Loading shotclock from Melon-Shotclock   2/2", text, "second load of the same batch");
            LoadFeed.End(b, 2500);

            // After the gap: a new batch.
            int c = LoadFeed.Begin("assets_resources_objectids", "(game)", false, false, 2500 + LoadFeed.BurstGapMs);
            LoadFeed.Line(9000, 300, out text);
            T.Eq("Loading assets_resources_objectids", text, "a new batch starts at 1; '(game)' is not a mod name");
            LoadFeed.End(c, 9100);

            // Announced batch: the total is known from the start.
            LoadFeed.Reset();
            LoadFeed.Expect(24, 20000);
            int first = LoadFeed.Begin("mw_ar2_plus", "Not_Wolfie-AR2_Plus", true, true, 20000);
            T.Eq(1, LoadFeed.UrgentActive, "an urgent load is counted as such");
            LoadFeed.Line(20500, 300, out text);
            T.Eq("Loading mw_ar2_plus from Not_Wolfie-AR2_Plus   1/24", text, "announced batch shows the whole total");
            LoadFeed.End(first, 21000);
            T.Eq(0, LoadFeed.UrgentActive, "and no longer when it ended");
            int second = LoadFeed.Begin("mw_acr_v2", "Volks-ACR", true, true, 21000);
            LoadFeed.Line(21500, 300, out text);
            T.Eq("Loading mw_acr_v2 from Volks-ACR   2/24", text, "next of the announced batch");
            int extra = LoadFeed.Begin("late_modular2", "Meat-Modular2", false, false, 21600);
            LoadFeed.Line(21700, 300, out text);
            T.Eq("Loading mw_acr_v2 from Volks-ACR   2/25", text, "an unannounced load in between adds to the total; the oldest load is the one named");
            LoadFeed.End(second, 21800);
            LoadFeed.End(extra, 21900);
            LoadFeed.End(extra, 21900);
            LoadFeed.End(0, 21900);
            T.True(!LoadFeed.Line(22000, 300, out text), "ending twice or with no token changes nothing");

            // The map: shown alone, at once, in front of a batch, without the batch's count.
            LoadFeed.Reset();
            int item = LoadFeed.Begin("icarus lmg", "Nuigo-Surplus", false, false, 30000);
            LoadFeed.Begin("passam", "Nuigo-Surplus", false, false, 30000);
            int map = LoadFeed.BeginAlone("map IndoorRange_Updated", 30010);
            T.True(LoadFeed.Line(30020, 300, out text), "a load named alone shows without waiting");
            T.Eq("Loading map IndoorRange_Updated", text, "the map line: no mod, no count, in front of the batch");
            LoadFeed.End(map, 31000);
            LoadFeed.Line(31000, 300, out text);
            T.Eq("Loading icarus lmg from Nuigo-Surplus   1/2", text, "the batch is back when the map is in, its count untouched");
            LoadFeed.End(item, 31100);

            // The clean line: background loads are not in it, waited-for ones are, counted among themselves.
            LoadFeed.Reset();
            LoadFeed.Begin("icarus lmg", "Nuigo-Surplus", false, false, 40000);
            T.True(!LoadFeed.WaitedLine(41000, out text), "a background load alone gives no clean line");
            LoadFeed.Expect(3, 41000);
            int p1 = LoadFeed.Begin("mw_ar2_plus", "Not_Wolfie-AR2_Plus", true, true, 41000);
            T.True(LoadFeed.WaitedLine(41100, out text), "a waited-for load does");
            T.Eq("Loading mw_ar2_plus from Not_Wolfie-AR2_Plus   1/3", text, "counted among the waited-for loads only");
            LoadFeed.End(p1, 41500);
            int p2 = LoadFeed.Begin("mw_acr_v2", "Volks-ACR", true, true, 41500);
            LoadFeed.WaitedLine(41600, out text);
            T.Eq("Loading mw_acr_v2 from Volks-ACR   2/3", text, "second of three");
            LoadFeed.End(p2, 42000);
            T.True(!LoadFeed.WaitedLine(42100, out text), "nothing waited for = no line, though the background load still runs");
        }
    }
}
