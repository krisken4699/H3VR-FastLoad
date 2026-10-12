using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using FastLoad.Early;

namespace FastLoad.Tests
{
    public static class RunFilesTests
    {
        private static void Make(string dir, string stamp, bool started)
        {
            File.WriteAllText(RunFiles.RunPath(dir, stamp),
                "       5 f=- RUN  x\n" + (started ? "     900 f=- LOG  [Message:BepInEx] Chainloader started\n" : ""));
            File.WriteAllText(RunFiles.ReportPath(dir, stamp), "report");
        }

        public static void Run()
        {
            T.Group("RunFiles");

            // StampIgnoresLocale
            CultureInfo before = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("th-TH");
                T.Eq("20261012-163005_123", RunFiles.Stamp(new DateTime(2026, 10, 12, 16, 30, 5), 123), "stamp is Gregorian in a Thai locale");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = before;
            }

            string dir = Path.Combine(Path.GetTempPath(), "fastload_rf_" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try
            {
                T.True(RunFiles.RunPath(dir, "S").EndsWith("run_S.log"), "run path");
                T.True(RunFiles.ReportPath(dir, "S").EndsWith("report_S.txt"), "report path");

                // StubIsDeleted
                Make(dir, "20261012-100000_1", true);
                Make(dir, "20261012-100100_2", false);
                Make(dir, "20261012-100200_3", true);
                T.True(!RunFiles.IsStub(RunFiles.RunPath(dir, "20261012-100000_1")), "started run is not a stub");
                T.True(RunFiles.IsStub(RunFiles.RunPath(dir, "20261012-100100_2")), "run with no chainloader line is a stub");

                List<string> gone = RunFiles.Prune(dir, "20261012-100200_3", 6);
                T.True(gone.Contains("20261012-100100_2"), "stub reported deleted");
                T.True(!File.Exists(RunFiles.RunPath(dir, "20261012-100100_2")), "stub run file deleted");
                T.True(!File.Exists(RunFiles.ReportPath(dir, "20261012-100100_2")), "stub report deleted");
                T.True(File.Exists(RunFiles.RunPath(dir, "20261012-100000_1")), "real run kept");

                // CurrentRunIsNeverDeleted: the current run has no chainloader line yet when Prune runs.
                Make(dir, "20261012-100300_4", false);
                RunFiles.Prune(dir, "20261012-100300_4", 6);
                T.True(File.Exists(RunFiles.RunPath(dir, "20261012-100300_4")), "current run survives although it looks like a stub");

                // Keep N counts the current run.
                Make(dir, "20261012-100400_5", true);
                Make(dir, "20261012-100500_6", true);
                Make(dir, "20261012-100600_7", true);
                RunFiles.Prune(dir, "20261012-100600_7", 3);
                T.True(File.Exists(RunFiles.RunPath(dir, "20261012-100600_7")), "keep 3: current kept");
                T.True(File.Exists(RunFiles.RunPath(dir, "20261012-100500_6")), "keep 3: newest older kept");
                T.True(File.Exists(RunFiles.RunPath(dir, "20261012-100400_5")), "keep 3: second newest older kept");
                T.True(!File.Exists(RunFiles.RunPath(dir, "20261012-100200_3")), "keep 3: older run deleted");
                T.True(!File.Exists(RunFiles.ReportPath(dir, "20261012-100000_1")), "keep 3: oldest report deleted");

                RunFiles.Prune(dir, "20261012-100600_7", 0);
                T.True(File.Exists(RunFiles.RunPath(dir, "20261012-100600_7")), "keep 0 still keeps the current run");

                // LockedFileDoesNotThrow: the handed-over start may still hold its file.
                Make(dir, "20261012-100700_8", false);
                using (new FileStream(RunFiles.RunPath(dir, "20261012-100700_8"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    bool threw = false;
                    try { RunFiles.Prune(dir, "20261012-100800_9", 6); }
                    catch (Exception) { threw = true; }
                    T.True(!threw, "pruning a file another process holds does not throw");
                }

                bool threwMissing = false;
                try { RunFiles.Prune(Path.Combine(dir, "nope"), "x", 6); }
                catch (Exception) { threwMissing = true; }
                T.True(!threwMissing, "pruning a missing folder does not throw");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
