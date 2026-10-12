using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace FastLoad.Early
{
    /// <summary>Names of the per-run files and which old ones to delete.</summary>
    public static class RunFiles
    {
        /// <summary>BepInEx logs this once per real start, before any plugin loads.</summary>
        public const string StartedMark = "Chainloader started";

        public static string Stamp(DateTime now, int pid)
        {
            return now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "_" + pid.ToString(CultureInfo.InvariantCulture);
        }

        public static string RunPath(string dir, string stamp)
        {
            return Path.Combine(dir, "run_" + stamp + ".log");
        }

        public static string ReportPath(string dir, string stamp)
        {
            return Path.Combine(dir, "report_" + stamp + ".txt");
        }

        /// <summary>
        /// A run file with no "Chainloader started" line: the start that handed over to a second start (restart on
        /// launch), or one that died in the preloader. Unreadable files count as not a stub (left alone).
        /// </summary>
        public static bool IsStub(string runPath)
        {
            try
            {
                using (FileStream fs = new FileStream(runPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader r = new StreamReader(fs))
                {
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        if (line.IndexOf(StartedMark, StringComparison.Ordinal) >= 0) return false;
                    }
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Deletes stubs, then the oldest runs until at most <paramref name="keep"/> remain counting the current one.
        /// The current run is never touched. Returns the stamps it deleted. Never throws.
        /// </summary>
        public static List<string> Prune(string dir, string currentStamp, int keep)
        {
            List<string> deleted = new List<string>();
            try
            {
                if (!Directory.Exists(dir)) return deleted;
                List<string> real = new List<string>();
                string[] files = Directory.GetFiles(dir, "run_*.log");
                for (int i = 0; i < files.Length; i++)
                {
                    string name = Path.GetFileNameWithoutExtension(files[i]);
                    string stamp = name.Substring("run_".Length);
                    if (string.Equals(stamp, currentStamp, StringComparison.Ordinal)) continue;
                    if (IsStub(files[i]))
                    {
                        if (Remove(dir, stamp)) deleted.Add(stamp);
                    }
                    else
                    {
                        real.Add(stamp);
                    }
                }
                real.Sort(string.CompareOrdinal);
                int allowed = Math.Max(0, keep - 1);
                for (int i = 0; i < real.Count - allowed; i++)
                {
                    if (Remove(dir, real[i])) deleted.Add(real[i]);
                }
            }
            catch (Exception)
            {
            }
            return deleted;
        }

        private static bool Remove(string dir, string stamp)
        {
            try
            {
                File.Delete(RunPath(dir, stamp));
                string report = ReportPath(dir, stamp);
                if (File.Exists(report)) File.Delete(report);
                return true;
            }
            catch (Exception)
            {
                // Still held by the start that is handing over: it goes at the next start.
                return false;
            }
        }
    }
}
