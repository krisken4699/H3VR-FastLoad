using System;
using System.Threading;

namespace FastLoad.Early
{
    public enum Phase
    {
        ProcessStart = 0,
        PreloaderDone = 1,
        ChainloaderStart = 2,
        ChainloaderDone = 3,
        StratumDone = 4,
        OtherLoaderDone = 5,
        MenuLoaded = 6,
        Quiet = 7
    }

    /// <summary>
    /// When each start-up phase was reached, in run-clock milliseconds; -1 = not seen. A mark is set once: the first
    /// time wins. Marks that depend on another mod are matched on that mod's own log text.
    /// </summary>
    public static class Phases
    {
        public const int Count = 8;

        public static readonly string[] Names =
        {
            "process start",
            "preloader done",
            "chainloader start (plugins begin loading)",
            "chainloader done (all plugins loaded)",
            "Stratum loading complete",
            "OtherLoader all items loaded",
            "main menu scene loaded",
            "quiet (10 s with nothing loading and no frame of 50 ms or more)"
        };

        private static readonly object s_gate = new object();
        private static readonly int[] s_at = NewMarks();

        /// <summary>How many bundle loads the loaders themselves reported in their log lines (cross-check for the hooks).</summary>
        public static int LoaderBundleLines;

        /// <summary>Index of the phase marked most recently; -1 = none yet.</summary>
        public static int LastMarked = -1;

        private static int[] NewMarks()
        {
            int[] a = new int[Count];
            for (int i = 0; i < Count; i++) a[i] = -1;
            a[(int)Phase.ProcessStart] = 0;
            return a;
        }

        public static void Reset()
        {
            Monitor.Enter(s_gate);
            try
            {
                for (int i = 0; i < Count; i++) s_at[i] = -1;
                s_at[(int)Phase.ProcessStart] = 0;
                LoaderBundleLines = 0;
                LastMarked = -1;
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        public static int At(Phase p)
        {
            return s_at[(int)p];
        }

        public static int[] Snapshot()
        {
            Monitor.Enter(s_gate);
            try
            {
                return (int[])s_at.Clone();
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        /// <summary>Sets the mark if it was not set. True when this call set it.</summary>
        public static bool Mark(Phase p, int ms)
        {
            Monitor.Enter(s_gate);
            try
            {
                if (s_at[(int)p] >= 0) return false;
                s_at[(int)p] = ms;
                LastMarked = (int)p;
                return true;
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        /// <summary>Feeds one log event. True when it newly set a phase.</summary>
        public static bool SeeLog(string source, string text, int ms)
        {
            if (source == null || text == null) return false;

            if (string.Equals(source, "BepInEx", StringComparison.Ordinal))
            {
                if (string.Equals(text, "Preloader finished", StringComparison.Ordinal)) return Mark(Phase.PreloaderDone, ms);
                if (string.Equals(text, "Chainloader started", StringComparison.Ordinal)) return Mark(Phase.ChainloaderStart, ms);
                if (string.Equals(text, "Chainloader startup complete", StringComparison.Ordinal)) return Mark(Phase.ChainloaderDone, ms);
                return false;
            }
            if (string.Equals(source, "Stratum", StringComparison.Ordinal))
            {
                if (text.IndexOf("Loading complete (", StringComparison.Ordinal) >= 0) return Mark(Phase.StratumDone, ms);
                return false;
            }
            if (source.StartsWith("OtherLoader", StringComparison.Ordinal))
            {
                if (text.IndexOf("All items loaded", StringComparison.Ordinal) >= 0) return Mark(Phase.OtherLoaderDone, ms);
                if (text.IndexOf("Completed loading bundle", StringComparison.Ordinal) >= 0) Interlocked.Increment(ref LoaderBundleLines);
                return false;
            }
            if (string.Equals(source, "ModulWorkshop_Optimizer", StringComparison.Ordinal))
            {
                // The mod writes "On-demand async loading modular bundle" and "On-demand SYNC loading modular bundle".
                if (text.IndexOf("On-demand", StringComparison.Ordinal) >= 0
                    && text.IndexOf("loading modular bundle", StringComparison.Ordinal) >= 0)
                    Interlocked.Increment(ref LoaderBundleLines);
            }
            return false;
        }

        /// <summary>Feeds one loaded scene. H3VR's main menu scene is "MainMenu3".</summary>
        public static bool SeeScene(string sceneName, int ms)
        {
            if (sceneName == null) return false;
            if (sceneName.StartsWith("MainMenu", StringComparison.Ordinal)) return Mark(Phase.MenuLoaded, ms);
            return false;
        }
    }
}
