using System.Collections.Generic;
using FastLoad.Early;

namespace FastLoad.Tests
{
    public static class PluginLoadsTests
    {
        private const long MB = 1048576L;

        public static void Run()
        {
            T.Group("PluginLoads");
            PluginLoads.Reset();

            T.True(PluginLoads.Wants("BepInEx", "Loading [Sodalite 1.4.2]"), "wants a loading line");
            T.True(PluginLoads.Wants("BepInEx", "Chainloader startup complete"), "wants the end of the chainloader");
            T.True(!PluginLoads.Wants("BepInEx", "Chainloader started"), "does not want other BepInEx lines");
            T.True(!PluginLoads.Wants("Deli", "Loading [x]"), "does not want another mod's line");
            T.True(!PluginLoads.Wants(null, null), "null is not wanted");

            PluginLoads.See("Loading [Sodalite 1.4.2]", 34000, 3000 * MB);
            PluginLoads.See("Loading [H3VR Sideloader 0.3.6]", 36500, 3050 * MB);
            List<PluginLoad> mid = PluginLoads.Snapshot();
            T.Eq(2, mid.Count, "two plugins seen");
            T.Eq("Sodalite 1.4.2", mid[0].Name, "name is what is inside the brackets");
            T.Eq(2500, mid[0].Ms, "a plugin's turn ends when the next one starts");
            T.Eq(50 * MB, mid[0].Growth, "commit growth during its turn");
            T.Eq(-1, mid[1].Ms, "the one loading now has no end yet");
            T.Eq(0L, mid[1].Growth, "and no growth claimed yet");

            PluginLoads.See("Chainloader startup complete", 129000, 11000 * MB);
            List<PluginLoad> done = PluginLoads.Snapshot();
            T.Eq(92500, done[1].Ms, "the last plugin's turn ends with the chainloader");
            T.Eq(7950 * MB, done[1].Growth, "last plugin growth");

            PluginLoads.See("Chainloader startup complete", 130000, 1);
            T.Eq(92500, PluginLoads.Snapshot()[1].Ms, "a second end line changes nothing");

            PluginLoads.See("Loading [Name [with] brackets 1.0]", 131000, 0);
            T.Eq("Name [with] brackets 1.0", PluginLoads.Snapshot()[2].Name, "brackets inside a name survive");
            PluginLoads.See(null, 1, 1);

            done[0].Name = "changed";
            T.Eq("Sodalite 1.4.2", PluginLoads.Snapshot()[0].Name, "snapshot is a copy");

            PluginLoads.Reset();
            T.Eq(0, PluginLoads.Snapshot().Count, "reset clears");
        }
    }
}
