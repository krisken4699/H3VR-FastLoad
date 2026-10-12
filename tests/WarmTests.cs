using System;
using System.IO;
using FastLoad.Early;

namespace FastLoad.Tests
{
    public static class WarmTests
    {
        public static void Run()
        {
            T.Group("Warm");

            string root = Path.Combine(Path.GetTempPath(), "fastload_warm_" + Guid.NewGuid().ToString("N"));
            string deep = Path.Combine(Path.Combine(root, "a"), "b");
            Directory.CreateDirectory(deep);
            try
            {
                File.WriteAllBytes(Path.Combine(root, "one.dll"), new byte[100]);
                File.WriteAllBytes(Path.Combine(deep, "two.dll"), new byte[200000]);
                File.WriteAllBytes(Path.Combine(deep, "three.dll"), new byte[0]);
                File.WriteAllBytes(Path.Combine(deep, "not_a_dll.txt"), new byte[50]);

                string heard = null;
                Warm.OnDone = delegate(string s) { heard = s; };
                string summary = Warm.Run(new string[] { root, Path.Combine(root, "missing"), null }, 4);
                Warm.OnDone = null;

                T.True(summary.StartsWith("warm dlls=3 failed=0 mb=0.2 ", StringComparison.Ordinal), "three DLLs in nested folders read, other files left alone: " + summary);
                T.True(summary.IndexOf(" threads=4 ", StringComparison.Ordinal) > 0, "thread count reported");
                T.Eq(summary, heard, "the summary is handed to OnDone");
                T.Eq(summary, Warm.Mode, "and kept in Mode");

                string none = Warm.Run(new string[0], 0);
                T.True(none.StartsWith("warm dlls=0 failed=0 ", StringComparison.Ordinal), "no folders = nothing read, no error: " + none);
                T.True(none.IndexOf(" threads=1 ", StringComparison.Ordinal) > 0, "at least one thread");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
