using System.IO;
using System.Text.RegularExpressions;
using FastLoad.Early;

namespace FastLoad.Tests
{
    public static class TimelineWriterTests
    {
        public static void Run()
        {
            T.Group("TimelineWriter");
            Clock.Start(0);

            string dir = Path.Combine(Path.GetTempPath(), "fastload_tw_" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "run_test.log");
            try
            {
                TimelineWriter w = new TimelineWriter();
                string error;
                T.True(w.Open(path, out error), "opens: " + error);

                w.Write("LOG", "one");
                // Flush per line: readable while the writer still holds the file (a crash loses nothing written).
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader r = new StreamReader(fs))
                {
                    T.True(r.ReadToEnd().Contains("LOG  one"), "line is on disk before Close");
                }

                // MultiLineTextIsOneLine
                w.Frame = 7;
                w.Write("LOG", "boom\n  at X.Y ()\n  at Z.W ()");
                w.Close();
                w.Write("LOG", "after close is ignored");

                string[] lines = File.ReadAllLines(path);
                T.Eq(2, lines.Length, "two events are two lines");
                T.True(Regex.IsMatch(lines[0], @"^\s*\d+ f=- LOG  one$"), "first line format: " + lines[0]);
                T.True(Regex.IsMatch(lines[1], @"^\s*\d+ f=7 LOG  boom"), "second line format: " + lines[1]);

                TimelineWriter bad = new TimelineWriter();
                T.True(!bad.Open(Path.Combine(dir, "no_such_folder", "x.log"), out error), "bad path returns false");
                T.True(error != null && error.Length > 0, "bad path gives an error text");
                bad.Write("LOG", "ignored, must not throw");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
