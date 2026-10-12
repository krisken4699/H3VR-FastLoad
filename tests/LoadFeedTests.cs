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
        }
    }
}
