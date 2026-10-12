using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;

namespace FastLoad.Early
{
    /// <summary>
    /// BepInEx preloader patcher. Patches no assembly; it starts the probe as early as a mod can run.
    ///
    /// Restart on launch: another mod's patcher may start the game a second time from the preloader and end this
    /// process. Nothing heavy happens here, and the file this start leaves has no "Chainloader started" line, so
    /// the next start deletes it (RunFiles.Prune). In the second start that mod sets the environment variables
    /// read below.
    /// </summary>
    public static class Entry
    {
        public static IEnumerable<string> TargetDLLs { get; } = new string[0];

        private const string ConfigFile = "ken.h3vr.fastload.cfg";
        private const string RestartMarker = "ACTIONCAM_GFXJOBS_RESTART";

        public static void Patch(AssemblyDefinition assembly)
        {
        }

        public static void Initialize()
        {
            ManualLogSource log = Logger.CreateLogSource("FastLoad Patcher");
            try
            {
                string cfgPath = Path.Combine(Paths.ConfigPath, ConfigFile);
                string cfg = File.Exists(cfgPath) ? File.ReadAllText(cfgPath) : "";
                // Speed-ups come first and do not depend on the probe.
                if (CfgPeek.Bool(cfg, "Speed", "FastDllLookup", true)) DllLookup.Install();
                log.LogInfo("Fast DLL lookup: " + DllLookup.Mode);

                LaterList.Wanted = LaterList.Parse(CfgPeek.Value(cfg, "Later", "Plugins"));
                if (LaterList.Wanted.Count > 0)
                {
                    Logger.Listeners.Add(new LateGate());
                    log.LogInfo("Later plugins: will hold back " + string.Join(", ", LaterList.Wanted.ToArray()));
                }

                if (!CfgPeek.Bool(cfg, "Probe", "Enabled", true))
                {
                    log.LogInfo("Probe is off ([Probe] Enabled = false in " + ConfigFile + ")");
                    return;
                }

                Process self = Process.GetCurrentProcess();
                Clock.Start((DateTime.Now - self.StartTime).TotalMilliseconds);
                Phases.Reset();
                PluginLoads.Reset();

                string dir = Path.Combine(Paths.BepInExRootPath, "FastLoad");
                Directory.CreateDirectory(dir);
                string stamp = RunFiles.Stamp(DateTime.Now, self.Id);
                List<string> pruned = RunFiles.Prune(dir, stamp, CfgPeek.Int(cfg, "Probe", "KeepRuns", 6));

                TimelineWriter writer = new TimelineWriter();
                string error;
                if (!writer.Open(RunFiles.RunPath(dir, stamp), out error))
                {
                    log.LogError("Probe is off for this run: could not open " + RunFiles.RunPath(dir, stamp) + " (" + error + ")");
                    return;
                }

                Timeline.Dir = dir;
                Timeline.Stamp = stamp;
                Timeline.Profile = Paths.BepInExRootPath;
                Timeline.Args = string.Join(" ", Environment.GetCommandLineArgs());
                Timeline.NativeHooks = CfgPeek.Bool(cfg, "Probe", "NativeHooks", false);
                Timeline.RestartCostMs = -1;
                if (Environment.GetEnvironmentVariable(RestartMarker) == "1")
                {
                    double cost;
                    string ms = Environment.GetEnvironmentVariable(RestartMarker + "_MS");
                    Timeline.RestartCostMs = ms != null && double.TryParse(ms, NumberStyles.Float, CultureInfo.InvariantCulture, out cost) ? (int)cost : 0;
                }
                Timeline.Writer = writer;
                Timeline.Enabled = true;

                Timeline.Write("RUN", "profile=" + Timeline.Profile + " pid=" + self.Id
                    + " restartOnLaunch=" + (Timeline.RestartCostMs >= 0 ? "yes cost=" + Timeline.RestartCostMs + "ms" : "no")
                    + " prunedRuns=" + pruned.Count + " args=" + Timeline.Args);

                ScanTimers.Install();
                Timeline.Write("RUN", "dllLookup=" + DllLookup.Mode + " scanTimers=" + ScanTimers.Mode);
                Logger.Listeners.Add(new LogMirror());
                log.LogInfo("Probe on: " + RunFiles.RunPath(dir, stamp));
            }
            catch (Exception e)
            {
                Timeline.Enabled = false;
                log.LogError("Probe is off for this run: " + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
