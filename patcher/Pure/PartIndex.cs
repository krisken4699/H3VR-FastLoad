using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FastLoad.Early
{
    /// <summary>
    /// Which modular part bundle holds which parts and skins. Learned the first time a bundle is loaded and kept
    /// between game starts, so that later a gun only needs the bundles of the parts that are on it.
    ///
    /// A bundle is remembered together with its file size and time; a file that changed is unknown again.
    /// </summary>
    public sealed class PartIndex
    {
        public sealed class Entry
        {
            public long Size;
            public long Ticks;
            /// <summary>"group|part name".</summary>
            public readonly Dictionary<string, bool> Parts = new Dictionary<string, bool>(StringComparer.Ordinal);
            /// <summary>Groups this bundle has parts for.</summary>
            public readonly Dictionary<string, bool> Groups = new Dictionary<string, bool>(StringComparer.Ordinal);
            /// <summary>"group/part name" skin paths.</summary>
            public readonly Dictionary<string, bool> Skins = new Dictionary<string, bool>(StringComparer.Ordinal);
        }

        private readonly Dictionary<string, Entry> m_byPath = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public int Count
        {
            get { return m_byPath.Count; }
        }

        public static string PartKey(string group, string part)
        {
            return (group ?? "") + "|" + (part ?? "");
        }

        /// <summary>Starts (or restarts) what is known about one bundle file.</summary>
        public Entry Learn(string path, long size, long ticks)
        {
            Entry e = new Entry();
            e.Size = size;
            e.Ticks = ticks;
            m_byPath[path] = e;
            return e;
        }

        public static void AddPart(Entry e, string group, string part)
        {
            if (e == null || string.IsNullOrEmpty(group)) return;
            e.Groups[group] = true;
            if (!string.IsNullOrEmpty(part)) e.Parts[PartKey(group, part)] = true;
        }

        public static void AddSkin(Entry e, string skinPath)
        {
            if (e != null && !string.IsNullOrEmpty(skinPath)) e.Skins[skinPath] = true;
        }

        /// <summary>What is known about that file as it is on disk now; null when never learned or changed since.</summary>
        public Entry Find(string path, long size, long ticks)
        {
            Entry e;
            if (path == null || !m_byPath.TryGetValue(path, out e)) return null;
            return e.Size == size && e.Ticks == ticks ? e : null;
        }

        public string Serialize()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("FastLoad modular part index v1\n");
            foreach (KeyValuePair<string, Entry> pair in m_byPath)
            {
                sb.Append("B\t").Append(pair.Key).Append('\t').Append(pair.Value.Size.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(pair.Value.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
                foreach (string g in pair.Value.Groups.Keys) sb.Append("G\t").Append(g).Append('\n');
                foreach (string p in pair.Value.Parts.Keys) sb.Append("P\t").Append(p).Append('\n');
                foreach (string s in pair.Value.Skins.Keys) sb.Append("S\t").Append(s).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>Reads what Serialize wrote. Anything it does not understand is skipped; a wrong first line gives an empty index.</summary>
        public static PartIndex Parse(string text)
        {
            PartIndex index = new PartIndex();
            if (string.IsNullOrEmpty(text)) return index;
            string[] lines = text.Split('\n');
            if (lines.Length == 0 || lines[0].Trim() != "FastLoad modular part index v1") return index;
            Entry current = null;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (line.Length < 3 || line[1] != '\t') continue;
                string rest = line.Substring(2);
                if (line[0] == 'B')
                {
                    current = null;
                    string[] f = rest.Split('\t');
                    long size, ticks;
                    if (f.Length != 3 || !long.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out size)
                        || !long.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks)) continue;
                    current = index.Learn(f[0], size, ticks);
                }
                else if (current == null) continue;
                else if (line[0] == 'G') current.Groups[rest] = true;
                else if (line[0] == 'P') current.Parts[rest] = true;
                else if (line[0] == 'S') current.Skins[rest] = true;
            }
            return index;
        }

        /// <summary>
        /// Whether a bundle is needed for an object that has these part groups, shows these parts and can show
        /// skins for these skin paths. An unknown bundle is always needed (loading it is how it becomes known).
        /// </summary>
        public static bool Needed(Entry e, Dictionary<string, bool> wantedParts, Dictionary<string, bool> wantedSkins, Dictionary<string, bool> wholeGroups)
        {
            if (e == null) return true;
            foreach (string part in e.Parts.Keys) if (wantedParts.ContainsKey(part)) return true;
            foreach (string skin in e.Skins.Keys) if (wantedSkins.ContainsKey(skin)) return true;
            if (wholeGroups != null) foreach (string g in e.Groups.Keys) if (wholeGroups.ContainsKey(g)) return true;
            // Holds nothing this code knows how to name: not safe to judge.
            return e.Parts.Count == 0 && e.Skins.Count == 0 && e.Groups.Count == 0;
        }
    }
}
