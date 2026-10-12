using System;
using System.Collections.Generic;
using System.IO;

namespace FastLoad.Early
{
    /// <summary>
    /// Every .dll under one folder, by file name, read from disk once. BepInEx looks for a missing assembly by
    /// walking the whole folder tree again on every lookup; on a big profile that is thousands of folders each time.
    /// The order of the answers is the order BepInEx itself would try them in: the folder first, then its
    /// sub-folders in the order the system lists them.
    /// </summary>
    public sealed class DllIndex
    {
        private readonly Dictionary<string, List<string>> m_byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public int Dirs;
        public int Files;

        public static DllIndex Build(string root)
        {
            DllIndex index = new DllIndex();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return index;

            List<string> dirs = new List<string>();
            dirs.Add(root);
            dirs.AddRange(Directory.GetDirectories(root, "*", SearchOption.AllDirectories));
            index.Dirs = dirs.Count;
            for (int i = 0; i < dirs.Count; i++)
            {
                string[] files;
                try
                {
                    files = Directory.GetFiles(dirs[i], "*.dll");
                }
                catch (Exception)
                {
                    continue;
                }
                for (int f = 0; f < files.Length; f++)
                {
                    string file = Path.GetFileName(files[f]);
                    // "*.dll" also matches longer endings on some systems.
                    if (!file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                    string name = file.Substring(0, file.Length - 4);
                    List<string> paths;
                    if (!index.m_byName.TryGetValue(name, out paths))
                    {
                        paths = new List<string>();
                        index.m_byName[name] = paths;
                    }
                    // Built from the folder's own name, as BepInEx builds it.
                    paths.Add(Path.Combine(dirs[i], name + ".dll"));
                    index.Files++;
                }
            }
            return index;
        }

        /// <summary>Every file called name + ".dll", in try order. Null when there is none.</summary>
        public List<string> Find(string name)
        {
            List<string> paths;
            if (name != null && m_byName.TryGetValue(name, out paths)) return paths;
            return null;
        }
    }
}
