using System;
using System.Globalization;

namespace FastLoad.Early
{
    /// <summary>
    /// Reads single values out of a BepInEx .cfg text. The preloader patcher runs before BepInEx's config objects
    /// exist, so it reads the plugin's file itself.
    /// </summary>
    public static class CfgPeek
    {
        /// <summary>The raw value of key under [section], or null when the file, section or key is missing.</summary>
        public static string Value(string cfgText, string section, string key)
        {
            if (cfgText == null) return null;
            string current = "";
            string[] lines = cfgText.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    current = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }
                if (!string.Equals(current, section, StringComparison.Ordinal)) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.Ordinal))
                    return line.Substring(eq + 1).Trim();
            }
            return null;
        }

        public static bool Bool(string cfgText, string section, string key, bool fallback)
        {
            string v = Value(cfgText, section, key);
            if (v == null) return fallback;
            if (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        public static int Int(string cfgText, string section, string key, int fallback)
        {
            string v = Value(cfgText, section, key);
            int n;
            if (v != null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
            return fallback;
        }
    }
}
