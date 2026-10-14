using System;
using System.IO;
using Microsoft.Win32;

namespace FastLoad.Helper
{
    /// <summary>
    /// Small separate program that FastLoad starts with "run as administrator" for the few things a game mod must
    /// not do itself. The game never runs as administrator; Windows asks the player once, on the desktop.
    ///   FastLoadHelper pagefile &lt;GB&gt; &lt;result file&gt;   fixed-size page file on C: (takes effect after a restart)
    ///   FastLoadHelper undo &lt;result file&gt;              puts back what was there before
    /// The result file gets one line: "ok ...", "unchanged ..." or "failed ...".
    /// </summary>
    public static class Program
    {
        private const string Key = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
        private const long KeepFreeGB = 15;

        public static int Main(string[] args)
        {
            string result = args.Length > 0 ? args[args.Length - 1] : null;
            try
            {
                if (args.Length == 3 && args[0] == "pagefile") return Write(result, PageFile(int.Parse(args[1]), result + ".before"));
                if (args.Length == 2 && args[0] == "undo") return Write(result, Undo(result + ".before"));
                return Write(result, "failed: usage: pagefile <GB> <result file> | undo <result file>");
            }
            catch (Exception e)
            {
                return Write(result, "failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static int Write(string file, string line)
        {
            Console.WriteLine(line);
            try
            {
                if (!string.IsNullOrEmpty(file)) File.WriteAllText(file, line + "\n");
            }
            catch (Exception)
            {
            }
            return line.StartsWith("failed", StringComparison.Ordinal) ? 1 : 0;
        }

        private static string PageFile(int gb, string backup)
        {
            if (gb < 8 || gb > 256) return "failed: size " + gb + " GB is outside 8-256";
            long mb = gb * 1024L;
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(Key, true))
            {
                if (k == null) return "failed: memory settings key not found";
                string[] now = k.GetValue("PagingFiles") as string[] ?? new string[0];
                string wanted = @"C:\pagefile.sys " + mb + " " + mb;
                if (now.Length == 1 && string.Equals(now[0].Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                    return "unchanged: page file is already a fixed " + gb + " GB on C:";

                // Room for it: what the current page file takes comes back when it is replaced.
                long current = 0;
                try
                {
                    FileInfo pf = new FileInfo(@"C:\pagefile.sys");
                    if (pf.Exists) current = pf.Length;
                }
                catch (Exception)
                {
                }
                DriveInfo c = new DriveInfo("C");
                long freeAfter = c.AvailableFreeSpace + current - mb * 1048576L;
                if (freeAfter < KeepFreeGB * 1073741824L)
                    return "failed: C: would have " + (freeAfter / 1073741824L) + " GB left with a " + gb + " GB page file (at least " + KeepFreeGB + " GB must stay free); nothing changed";

                if (!File.Exists(backup)) File.WriteAllText(backup, string.Join("\n", now));
                k.SetValue("PagingFiles", new[] { wanted }, RegistryValueKind.MultiString);
                return "ok: page file set to a fixed " + gb + " GB on C: (was: " + (now.Length == 0 ? "none" : string.Join(" | ", now))
                    + "); takes effect after the next Windows restart";
            }
        }

        private static string Undo(string backup)
        {
            if (!File.Exists(backup)) return "failed: nothing to undo (" + backup + " not found)";
            string[] before = File.ReadAllText(backup).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(Key, true))
            {
                if (k == null) return "failed: memory settings key not found";
                k.SetValue("PagingFiles", before, RegistryValueKind.MultiString);
            }
            File.Delete(backup);
            return "ok: page file settings put back to: " + string.Join(" | ", before) + "; takes effect after the next Windows restart";
        }
    }
}
