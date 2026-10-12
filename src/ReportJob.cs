using System;
using System.IO;
using System.Threading;
using BepInEx.Bootstrap;
using FastLoad.Early;

namespace FastLoad
{
    /// <summary>
    /// Writes the report file. The snapshot is taken on the main thread (cheap copies); building the text and writing
    /// the file happens on a worker thread so the report never costs a frame.
    /// </summary>
    public static class ReportJob
    {
        public static float HitchMs = 25f;

        private static int s_busy;

        private static ReportInput Snapshot()
        {
            ReportInput inp = new ReportInput();
            inp.Profile = Timeline.Profile;
            inp.Args = Timeline.Args;
            inp.HookMode = BundleHooks.Mode;
            inp.RestartCostMs = Timeline.RestartCostMs;
            inp.NowMs = Clock.NowMs;
            inp.HitchMs = HitchMs;
            inp.PhaseAt = Phases.Snapshot();
            inp.LoaderBundleLines = Phases.LoaderBundleLines;
            inp.Bundles = BundleLedger.Snapshot();
            inp.Frames = FrameLog.Snapshot();
            inp.Mem = MemLog.Snapshot();
            try
            {
                inp.PluginCount = Chainloader.PluginInfos.Count;
            }
            catch (Exception)
            {
                inp.PluginCount = -1;
            }
            return inp;
        }

        /// <summary>Starts a background write unless one is still running.</summary>
        public static void Kick()
        {
            if (Interlocked.CompareExchange(ref s_busy, 1, 0) != 0) return;
            try
            {
                ReportInput inp = Snapshot();
                ThreadPool.QueueUserWorkItem(Work, inp);
            }
            catch (Exception e)
            {
                Interlocked.Exchange(ref s_busy, 0);
                Timeline.Write("ERR", "report snapshot failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>Writes on the calling thread (game is quitting: a worker might not get to finish).</summary>
        public static void WriteNow()
        {
            try
            {
                Save(Snapshot());
            }
            catch (Exception e)
            {
                Timeline.Write("ERR", "final report failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void Work(object state)
        {
            try
            {
                Save((ReportInput)state);
            }
            catch (Exception e)
            {
                Timeline.Write("ERR", "report write failed: " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                Interlocked.Exchange(ref s_busy, 0);
            }
        }

        private static readonly object s_fileGate = new object();

        private static void Save(ReportInput inp)
        {
            string text = ReportBuilder.Build(inp);
            string path = RunFiles.ReportPath(Timeline.Dir, Timeline.Stamp);
            string tmp = path + ".tmp";
            Monitor.Enter(s_fileGate);
            try
            {
                // Write beside it, then swap: a crash in the middle leaves the last good report in place.
                File.WriteAllText(tmp, text);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            finally
            {
                Monitor.Exit(s_fileGate);
            }
        }
    }
}
