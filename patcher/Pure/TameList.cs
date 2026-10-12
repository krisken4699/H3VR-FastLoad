using System;
using System.Collections.Generic;
using System.Globalization;

namespace FastLoad.Early
{
    /// <summary>"[Tame] Throttle": "Namespace.Type.Method@0.5, Other.Type.Method@2" as method name and seconds.</summary>
    public static class TameList
    {
        public static List<KeyValuePair<string, double>> Parse(string text)
        {
            List<KeyValuePair<string, double>> list = new List<KeyValuePair<string, double>>();
            if (string.IsNullOrEmpty(text)) return list;
            string[] parts = text.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                int at = part.LastIndexOf('@');
                if (at <= 0 || at == part.Length - 1) continue;
                string name = part.Substring(0, at).Trim();
                double seconds;
                if (name.IndexOf('.') <= 0) continue;
                if (!double.TryParse(part.Substring(at + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)) continue;
                if (seconds <= 0 || seconds > 3600) continue;
                bool dup = false;
                for (int k = 0; k < list.Count; k++) if (string.Equals(list[k].Key, name, StringComparison.Ordinal)) dup = true;
                if (!dup) list.Add(new KeyValuePair<string, double>(name, seconds));
            }
            return list;
        }
    }
}
