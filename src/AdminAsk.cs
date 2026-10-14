using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using FastLoad.Early;
using Microsoft.Win32;

namespace FastLoad
{
    /// <summary>
    /// "[Admin] PageFileGB": the multi-second freezes measured on new2 came while Windows was growing its page file
    /// in the middle of play (commit left under 1 GB). A page file of fixed size is already full size when the game
    /// starts. Changing it needs administrator rights, which the game itself must not have (every mod would run as
    /// administrator, and SteamVR input breaks when the game and SteamVR run at different rights). So FastLoad starts
    /// its own small program, FastLoadHelper.exe, with "run as administrator": Windows asks once, on the desktop.
    /// The answer is kept in BepInEx/cache/FastLoad.admin.txt; it is not asked again (delete that file to be asked
    /// again). Takes effect after the next Windows restart.
    /// </summary>
    public static class AdminAsk
    {
        private const string Key = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";

        private static ManualLogSource s_log;
        private static string s_state = "";
        private static Thread s_thread;
        private static volatile string s_result;
        private static int s_feed;
        public static string Mode = "off";

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ShellExecuteInfo
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            public string lpVerb;
            public string lpFile;
            public string lpParameters;
            public string lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            public string lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr hProcess;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ShellExecuteExW(ref ShellExecuteInfo info);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint ms);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>Wanted size in GB; 0 = never ask.</summary>
        public static void Install(ManualLogSource log, int gb)
        {
            s_log = log;
            s_state = Path.Combine(Paths.CachePath, "FastLoad.admin.txt");
            try
            {
                if (gb <= 0)
                {
                    Mode = "off (PageFileGB = 0)";
                    return;
                }
                string wanted = @"C:\pagefile.sys " + (gb * 1024L) + " " + (gb * 1024L);
                string[] now = null;
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(Key, false))
                {
                    if (k != null) now = k.GetValue("PagingFiles") as string[];
                }
                string current = now == null ? "unknown" : string.Join(" | ", now);
                if (now != null && now.Length == 1 && string.Equals(now[0].Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    long size = 0;
                    try
                    {
                        FileInfo pf = new FileInfo(@"C:\pagefile.sys");
                        if (pf.Exists) size = pf.Length;
                    }
                    catch (Exception)
                    {
                    }
                    Mode = size >= gb * 1073741824L * 95 / 100
                        ? "page file is a fixed " + gb + " GB"
                        : "page file set to a fixed " + gb + " GB; Windows has not been restarted since (now " + (size / 1073741824L) + " GB)";
                    return;
                }
                if (File.Exists(Path.Combine(Path.Combine(Paths.BepInExRootPath, "FastLoad"), "autotest.txt")))
                {
                    Mode = "not asking during a scripted test run (page file now: " + current + ")";
                    return;
                }
                string before = File.Exists(s_state) ? File.ReadAllText(s_state).Trim() : "";
                if (before.Length > 0)
                {
                    Mode = "not asking again (" + before + "; delete " + s_state + " to be asked again). Page file now: " + current;
                    return;
                }
                string helper = Path.Combine(Path.GetDirectoryName(typeof(AdminAsk).Assembly.Location), "FastLoadHelper.exe");
                if (!File.Exists(helper))
                {
                    Mode = "FastLoadHelper.exe missing next to FastLoad.dll; page file left as it is (" + current + ")";
                    return;
                }
                Mode = "asking Windows for administrator rights to set a fixed " + gb + " GB page file (now: " + current + ")";
                s_feed = LoadFeed.BeginAlone("Windows asks on the desktop: allow FastLoad to set the page file", Clock.NowMs);
                string result = Path.Combine(Paths.CachePath, "FastLoad.helper-result.txt");
                string args = "pagefile " + gb + " \"" + result + "\"";
                s_thread = new Thread(delegate() { s_result = Run(helper, args, result); });
                s_thread.IsBackground = true;
                s_thread.Start();
            }
            catch (Exception e)
            {
                Mode = "failed: " + e.GetType().Name + ": " + e.Message;
            }
            finally
            {
                log.LogInfo("Page file: " + Mode);
            }
        }

        /// <summary>On a thread of its own: Windows' question can stay open as long as the player likes.</summary>
        private static string Run(string helper, string args, string resultFile)
        {
            try
            {
                if (File.Exists(resultFile)) File.Delete(resultFile);
                ShellExecuteInfo info = new ShellExecuteInfo();
                info.cbSize = Marshal.SizeOf(typeof(ShellExecuteInfo));
                info.fMask = 0x00000040; // SEE_MASK_NOCLOSEPROCESS
                info.lpVerb = "runas";
                info.lpFile = helper;
                info.lpParameters = args;
                info.nShow = 0;
                if (!ShellExecuteExW(ref info))
                {
                    int err = Marshal.GetLastWin32Error();
                    return err == 1223 ? "declined at Windows' question" : "could not start the helper (Windows error " + err + ")";
                }
                WaitForSingleObject(info.hProcess, 60000);
                CloseHandle(info.hProcess);
                return File.Exists(resultFile) ? File.ReadAllText(resultFile).Trim() : "the helper wrote no result";
            }
            catch (Exception e)
            {
                return "failed: " + e.GetType().Name + ": " + e.Message;
            }
        }

        /// <summary>Called every frame.</summary>
        public static void Tick()
        {
            if (s_thread == null || s_result == null) return;
            string result = s_result;
            s_thread = null;
            LoadFeed.End(s_feed, Clock.NowMs);
            try
            {
                File.WriteAllText(s_state, DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " " + result + "\n");
            }
            catch (Exception)
            {
            }
            Mode = result;
            Timeline.Write("DEF", "page file: " + result);
            if (s_log != null) s_log.LogInfo("Page file: " + result);
        }
    }
}
