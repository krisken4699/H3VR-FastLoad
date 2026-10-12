using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace FastLoad.Early
{
    public delegate void WarmDone(string summary);

    /// <summary>
    /// "[Speed] WarmDlls": reads every mod DLL once, on several threads, the moment the preloader starts. The first
    /// game start after a while reads the same files one at a time (a patcher looks into each plugin DLL, then
    /// BepInEx does) and that took half a minute on a big profile; read ahead in parallel, those reads come from
    /// Windows' file cache. Nothing is kept: the bytes are thrown away as they are read.
    /// </summary>
    public static class Warm
    {
        private static readonly object s_gate = new object();
        private static string[] s_files = new string[0];
        private static int s_next;
        private static int s_read;
        private static int s_failed;
        private static long s_bytes;
        private static double s_slowestMs;
        private static string s_slowest = "";

        /// <summary>"off", "reading" or the summary of the finished pass.</summary>
        public static volatile string Mode = "off";
        /// <summary>Called once from the reading thread when the pass is over.</summary>
        public static WarmDone OnDone;

        /// <summary>Starts the pass and returns at once.</summary>
        public static void Start(string[] roots, int threads)
        {
            try
            {
                Mode = "reading";
                Thread lead = new Thread(delegate() { Run(roots, threads); });
                lead.IsBackground = true;
                lead.Name = "FastLoad warm";
                lead.Start();
            }
            catch (Exception e)
            {
                Mode = "failed: " + e.GetType().Name + ": " + e.Message;
            }
        }

        /// <summary>The whole pass on the calling thread plus its workers; returns the summary.</summary>
        public static string Run(string[] roots, int threads)
        {
            Stopwatch sw = Stopwatch.StartNew();
            string summary;
            try
            {
                List<string> files = new List<string>();
                for (int i = 0; roots != null && i < roots.Length; i++)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(roots[i]) && Directory.Exists(roots[i]))
                            files.AddRange(Directory.GetFiles(roots[i], "*.dll", SearchOption.AllDirectories));
                    }
                    catch (Exception)
                    {
                        // A folder that cannot be listed is the game's problem, later; not ours.
                    }
                }
                double listMs = sw.Elapsed.TotalMilliseconds;

                s_files = files.ToArray();
                s_next = 0;
                s_read = 0;
                s_failed = 0;
                s_bytes = 0;
                s_slowestMs = 0;
                s_slowest = "";

                int n = Math.Max(1, Math.Min(threads, 16));
                Thread[] workers = new Thread[n];
                for (int i = 0; i < n; i++)
                {
                    workers[i] = new Thread(Work);
                    workers[i].IsBackground = true;
                    workers[i].Name = "FastLoad warm " + i;
                    workers[i].Start();
                }
                for (int i = 0; i < n; i++) workers[i].Join();

                summary = "warm dlls=" + s_read + " failed=" + s_failed
                    + " mb=" + (s_bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture)
                    + " listMs=" + ((int)listMs).ToString(CultureInfo.InvariantCulture)
                    + " totalMs=" + ((int)sw.Elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)
                    + " threads=" + n
                    + " slowest=" + ((int)s_slowestMs).ToString(CultureInfo.InvariantCulture) + "ms " + s_slowest;
            }
            catch (Exception e)
            {
                summary = "warm failed: " + e.GetType().Name + ": " + e.Message;
            }
            Mode = summary;
            try
            {
                WarmDone done = OnDone;
                if (done != null) done(summary);
            }
            catch (Exception)
            {
            }
            return summary;
        }

        private static void Work()
        {
            byte[] buffer = new byte[65536];
            Stopwatch sw = new Stopwatch();
            while (true)
            {
                int i = Interlocked.Increment(ref s_next) - 1;
                string[] files = s_files;
                if (i >= files.Length) return;
                long bytes = 0;
                bool ok = true;
                sw.Reset();
                sw.Start();
                try
                {
                    // Shared for everything: another patcher may be opening the same file right now.
                    using (FileStream fs = new FileStream(files[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536))
                    {
                        int got;
                        while ((got = fs.Read(buffer, 0, buffer.Length)) > 0) bytes += got;
                    }
                }
                catch (Exception)
                {
                    ok = false;
                }
                double ms = sw.Elapsed.TotalMilliseconds;

                // Monitor by hand: the game's runtime has no Monitor.Enter(object, ref bool), which 'lock' compiles to.
                Monitor.Enter(s_gate);
                try
                {
                    if (ok) s_read++;
                    else s_failed++;
                    s_bytes += bytes;
                    if (ms > s_slowestMs)
                    {
                        s_slowestMs = ms;
                        s_slowest = Path.GetFileName(files[i]);
                    }
                }
                finally
                {
                    Monitor.Exit(s_gate);
                }
            }
        }
    }
}
