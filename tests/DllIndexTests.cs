using System;
using System.Collections.Generic;
using System.IO;
using FastLoad.Early;

namespace FastLoad.Tests
{
    public static class DllIndexTests
    {
        public static void Run()
        {
            T.Group("DllIndex");

            string root = Path.Combine(Path.GetTempPath(), "fastload_dllindex_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "A"));
                Directory.CreateDirectory(Path.Combine(Path.Combine(root, "B"), "deep"));
                File.WriteAllText(Path.Combine(root, "Top.dll"), "x");
                File.WriteAllText(Path.Combine(Path.Combine(root, "A"), "Twice.dll"), "x");
                File.WriteAllText(Path.Combine(Path.Combine(Path.Combine(root, "B"), "deep"), "Twice.dll"), "x");
                File.WriteAllText(Path.Combine(Path.Combine(root, "A"), "notes.txt"), "x");
                File.WriteAllText(Path.Combine(Path.Combine(root, "A"), "Other.dllx"), "x");

                DllIndex index = DllIndex.Build(root);
                T.Eq(4, index.Dirs, "the folder and its three sub-folders");
                T.Eq(3, index.Files, "only .dll files are counted");

                List<string> top = index.Find("Top");
                T.True(top != null && top.Count == 1 && top[0] == Path.Combine(root, "Top.dll"), "a file in the folder itself");
                T.True(index.Find("top") != null, "capital letters do not matter (as on the disk)");

                List<string> twice = index.Find("Twice");
                T.True(twice != null && twice.Count == 2, "a name in two folders gives both");
                T.True(twice != null && twice.Count == 2 && File.Exists(twice[0]) && File.Exists(twice[1]), "both paths are real files");

                T.True(index.Find("Missing") == null, "a name that is nowhere");
                T.True(index.Find("Other") == null, "a longer ending is not a dll");
                T.True(index.Find("notes") == null, "another kind of file");
                T.True(index.Find(null) == null, "null name");

                DllIndex none = DllIndex.Build(Path.Combine(root, "nope"));
                T.Eq(0, none.Dirs, "a folder that does not exist gives an empty index");
                T.True(none.Find("Top") == null, "and finds nothing");
                T.True(DllIndex.Build(null).Find("Top") == null, "null folder");
            }
            finally
            {
                try
                {
                    Directory.Delete(root, true);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
