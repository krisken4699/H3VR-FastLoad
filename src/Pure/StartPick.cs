using System;
using System.Collections.Generic;
using System.Text;

namespace FastLoad
{
    /// <summary>One modded map as its loader knows it.</summary>
    public struct MapEntry
    {
        /// <summary>The loader's own identifier: the safest thing to put in the config.</summary>
        public string Id;
        /// <summary>The name shown on the map's panel in the menu.</summary>
        public string Name;
        public string Mode;
        public string Author;
    }

    public enum PickKind
    {
        /// <summary>Nothing asked for.</summary>
        Blank,
        /// <summary>One of the game's own scenes; Value = its exact name.</summary>
        Game,
        /// <summary>A modded map; Value = its identifier.</summary>
        Modded,
        NotFound,
        /// <summary>The text is the display name of more than one modded map; Note lists their identifiers.</summary>
        Ambiguous
    }

    public struct Pick
    {
        public PickKind Kind;
        public string Value;
        public string Note;
    }

    /// <summary>Turns what the player typed in "[Start] Scene" into something loadable. No game access: testable.</summary>
    public static class StartPick
    {
        /// <summary>"Assets/Scenes/IndoorRange.unity" -> "IndoorRange".</summary>
        public static string SceneNameFromPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string p = path.Replace('\\', '/');
            int slash = p.LastIndexOf('/');
            if (slash >= 0) p = p.Substring(slash + 1);
            if (p.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) p = p.Substring(0, p.Length - 6);
            return p;
        }

        /// <summary>Game scene name first, then a modded map's identifier, then a modded map's display name. Case does not matter.</summary>
        public static Pick Resolve(string wanted, IList<string> gameScenes, IList<MapEntry> modded)
        {
            Pick pick = new Pick();
            pick.Note = "";
            string w = wanted == null ? "" : wanted.Trim();
            if (w.Length == 0)
            {
                pick.Kind = PickKind.Blank;
                return pick;
            }

            for (int i = 0; i < gameScenes.Count; i++)
            {
                if (Same(gameScenes[i], w))
                {
                    pick.Kind = PickKind.Game;
                    pick.Value = gameScenes[i];
                    return pick;
                }
            }
            for (int i = 0; i < modded.Count; i++)
            {
                if (Same(modded[i].Id, w))
                {
                    pick.Kind = PickKind.Modded;
                    pick.Value = modded[i].Id;
                    return pick;
                }
            }

            List<string> byName = new List<string>();
            for (int i = 0; i < modded.Count; i++)
            {
                if (Same(modded[i].Name, w)) byName.Add(modded[i].Id);
            }
            if (byName.Count == 1)
            {
                pick.Kind = PickKind.Modded;
                pick.Value = byName[0];
                return pick;
            }
            if (byName.Count > 1)
            {
                pick.Kind = PickKind.Ambiguous;
                pick.Note = string.Join(", ", byName.ToArray());
                return pick;
            }
            pick.Kind = PickKind.NotFound;
            return pick;
        }

        private static bool Same(string a, string b)
        {
            return a != null && string.Equals(a.Trim(), b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The text of scenes.txt: everything that can be put in "[Start] Scene".</summary>
        public static string ListText(IList<string> gameScenes, IList<MapEntry> modded, bool moddedLoaderFound)
        {
            StringBuilder sb = new StringBuilder(8192);
            sb.AppendLine("FastLoad: every scene the game can start in.");
            sb.AppendLine("Copy one name into BepInEx/config/ken.h3vr.fastload.cfg:   [Start]   Scene = <name>");
            sb.AppendLine("Capital letters do not matter. Leave Scene empty to start in the main menu as usual.");
            sb.AppendLine();
            sb.AppendLine("== The game's own scenes (" + gameScenes.Count + ") ==");
            List<string> sorted = new List<string>(gameScenes);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < sorted.Count; i++) sb.AppendLine("Scene = " + sorted[i]);
            sb.AppendLine();
            if (!moddedLoaderFound)
            {
                sb.AppendLine("== Modded maps ==");
                sb.AppendLine("Atlas is not installed (or not loaded yet): no modded maps to list.");
                return sb.ToString();
            }
            sb.AppendLine("== Modded maps (Atlas) (" + modded.Count + ") ==");
            sb.AppendLine("Use the identifier (left). The name on the map's menu panel (right) works too when no other map has the same name.");
            List<MapEntry> maps = new List<MapEntry>(modded);
            maps.Sort(ByName);
            for (int i = 0; i < maps.Count; i++)
            {
                MapEntry m = maps[i];
                sb.AppendLine("Scene = " + m.Id + "      | name: " + m.Name + " | mode: " + m.Mode + " | by: " + m.Author);
            }
            return sb.ToString();
        }

        private static int ByName(MapEntry a, MapEntry b)
        {
            return string.Compare(a.Name ?? "", b.Name ?? "", StringComparison.OrdinalIgnoreCase);
        }
    }
}
