using System;
using System.Collections.Generic;

namespace FastLoad.Early
{
    /// <summary>"[Later] Plugins": which plugins are held back until the game is on screen. No game access: testable.</summary>
    public static class LaterList
    {
        /// <summary>What the config asked for, read by the preloader half. Empty = nothing is held back.</summary>
        public static List<string> Wanted = new List<string>();

        /// <summary>Entries are separated by commas (or semicolons, or new lines). Spaces around an entry do not count.</summary>
        public static List<string> Parse(string raw)
        {
            List<string> list = new List<string>();
            if (raw == null) return list;
            string[] parts = raw.Split(',', ';', '\n', '\r');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 0) continue;
                bool dup = false;
                for (int k = 0; k < list.Count; k++)
                {
                    if (string.Equals(list[k], p, StringComparison.OrdinalIgnoreCase)) dup = true;
                }
                if (!dup) list.Add(p);
            }
            return list;
        }

        /// <summary>An entry names a plugin by its GUID or by its name; capital letters do not matter.</summary>
        public static bool Has(IList<string> wanted, string guid, string name)
        {
            if (wanted == null) return false;
            for (int i = 0; i < wanted.Count; i++)
            {
                if (guid != null && string.Equals(wanted[i], guid.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
                if (name != null && string.Equals(wanted[i], name.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
