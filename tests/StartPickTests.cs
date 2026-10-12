using System.Collections.Generic;

namespace FastLoad.Tests
{
    public static class StartPickTests
    {
        private static MapEntry Map(string id, string name, string mode)
        {
            MapEntry m = new MapEntry();
            m.Id = id;
            m.Name = name;
            m.Mode = mode;
            m.Author = "someone";
            return m;
        }

        public static void Run()
        {
            T.Group("StartPick");

            T.Eq("IndoorRange", StartPick.SceneNameFromPath("Assets/Scenes/IndoorRange.unity"), "scene name from a build path");
            T.Eq("MainMenu3", StartPick.SceneNameFromPath("MainMenu3"), "a bare name stays");
            T.Eq("Take&Hold_Lobby_2", StartPick.SceneNameFromPath(@"Assets\A\B\Take&Hold_Lobby_2.UNITY"), "backslashes and capitals in the ending");
            T.Eq("", StartPick.SceneNameFromPath(null), "null path");

            List<string> game = new List<string>();
            game.Add("MainMenu3");
            game.Add("IndoorRange");
            game.Add("ArizonaTargets");
            List<MapEntry> maps = new List<MapEntry>();
            maps.Add(Map("abass20.junglevalley", "JungleValley", "takeandhold"));
            maps.Add(Map("marcel.aporkalypse", "Aporkalypse Now!", "takeandhold"));
            maps.Add(Map("marcel.aporkalypse_sr", "Aporkalypse Now!", "supplyraid"));
            maps.Add(Map("IndoorRange", "A mod that reuses a game name", "sandbox"));

            T.Eq(PickKind.Blank, StartPick.Resolve("", game, maps).Kind, "empty = nothing asked");
            T.Eq(PickKind.Blank, StartPick.Resolve("   ", game, maps).Kind, "spaces = nothing asked");
            T.Eq(PickKind.Blank, StartPick.Resolve(null, game, maps).Kind, "null = nothing asked");

            Pick g = StartPick.Resolve("  indoorrange ", game, maps);
            T.Eq(PickKind.Game, g.Kind, "game scene, any capitals, spaces around");
            T.Eq("IndoorRange", g.Value, "value is the game's own spelling");

            Pick byId = StartPick.Resolve("Abass20.JungleValley", game, maps);
            T.Eq(PickKind.Modded, byId.Kind, "modded map by identifier");
            T.Eq("abass20.junglevalley", byId.Value, "value is the identifier");

            Pick byName = StartPick.Resolve("junglevalley", game, maps);
            T.Eq(PickKind.Modded, byName.Kind, "modded map by the name on its panel");
            T.Eq("abass20.junglevalley", byName.Value, "name resolves to the identifier");

            Pick twice = StartPick.Resolve("Aporkalypse Now!", game, maps);
            T.Eq(PickKind.Ambiguous, twice.Kind, "a name two maps share is not guessed");
            T.True(twice.Note.Contains("marcel.aporkalypse") && twice.Note.Contains("marcel.aporkalypse_sr"), "both identifiers are named: " + twice.Note);

            T.Eq(PickKind.NotFound, StartPick.Resolve("NoSuchPlace", game, maps).Kind, "unknown name");
            T.Eq(PickKind.NotFound, StartPick.Resolve("IndoorRange", new List<string>(), new List<MapEntry>()).Kind, "nothing known yet");

            // A near miss gets the real names offered.
            game.Add("IndoorRange_Updated");
            T.Eq("IndoorRange, IndoorRange_Updated", StartPick.Suggest("indoorrange_", game, maps), "suggests names on both sides of the text, each once");
            T.Eq("abass20.junglevalley", StartPick.Suggest("jungle", game, maps), "suggests a modded map by part of its name");
            T.Eq("ArizonaTargets", StartPick.Suggest("ArizonaTargets_Day", game, maps), "suggests a name the text contains");
            T.Eq("", StartPick.Suggest("zzzz", game, maps), "nothing close, nothing suggested");
            T.Eq("", StartPick.Suggest("", game, maps), "empty text, nothing suggested");
            game.Remove("IndoorRange_Updated");

            string list = StartPick.ListText(game, maps, true);
            T.True(list.Contains("Scene = IndoorRange") && list.Contains("Scene = ArizonaTargets"), "list has the game scenes ready to paste");
            T.True(list.IndexOf("Scene = ArizonaTargets") < list.IndexOf("Scene = IndoorRange"), "game scenes are sorted");
            T.True(list.Contains("Scene = abass20.junglevalley") && list.Contains("name: JungleValley") && list.Contains("mode: takeandhold"), "list has modded maps with name and mode");
            T.True(list.Contains("invent60.h3vr.fastload.cfg"), "list says where to put the name");
            T.True(StartPick.ListText(game, new List<MapEntry>(), false).Contains("Atlas is not installed"), "no loader is said plainly");
        }
    }
}
