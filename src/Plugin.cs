using System;
using BepInEx;
using BepInEx.Configuration;
using FastLoad.Early;
using UnityEngine.SceneManagement;

namespace FastLoad
{
    /// <summary>
    /// FastLoad piece 1: the probe. Measures and writes files; changes nothing about how the game loads.
    /// The preloader half (FastLoadPatcher.dll) has already started the clock, the timeline file and the bundle
    /// hooks by the time this loads.
    /// </summary>
    [BepInPlugin("ken.h3vr.fastload", "FastLoad", "0.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        private void Awake()
        {
            // Bound first, always: this writes ken.h3vr.fastload.cfg, which the preloader half reads at the next start.
            ConfigEntry<bool> enabled = Config.Bind("Probe", "Enabled", true,
                "Turns the whole probe off: no hooks, no sampler, no files. Takes effect at the next game start. Example: false");
            ConfigEntry<int> hitchMs = Config.Bind("Probe", "HitchMs", 25,
                "A frame this long or longer (milliseconds) is written to the timeline as a slow frame. Example: 25 catches anything under 40 fps");
            Config.Bind("Probe", "KeepRuns", 6,
                "How many runs' files to keep in BepInEx/FastLoad. Older ones are deleted at game start. Example: 6");
            Config.Bind("Probe", "NativeHooks", false,
                "Also hook the engine-native bundle load methods. Leave off unless the report's cross-check shows missed loads. Takes effect at the next game start. Example: true");

            if (!Timeline.Enabled)
            {
                Logger.LogInfo(enabled.Value
                    ? "Probe is off for this run (the preloader half did not start; see its log line above)"
                    : "Probe is off ([Probe] Enabled = false)");
                return;
            }

            try
            {
                // Already done at 'Chainloader started'; does nothing the second time.
                BundleHooks.Install(Timeline.NativeHooks);

                float hitch = Math.Max(1, hitchMs.Value);
                ReportJob.HitchMs = hitch;
                Sampler sampler = gameObject.AddComponent<Sampler>();
                sampler.HitchMs = hitch;

                SceneManager.sceneLoaded += OnSceneLoaded;
                SceneManager.activeSceneChanged += OnActiveSceneChanged;

                Logger.LogInfo("Probe on. Timeline: " + RunFiles.RunPath(Timeline.Dir, Timeline.Stamp) + " | bundle hooks: " + BundleHooks.Mode);
            }
            catch (Exception e)
            {
                Logger.LogError("Probe could not start its sampler: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                int now = Clock.NowMs;
                Timeline.Write("SCN", "loaded " + scene.name + " mode=" + mode);
                if (Phases.SeeScene(scene.name, now))
                {
                    Timeline.Write("PHS", "menu scene at " + now);
                    ReportJob.Kick();
                }
            }
            catch (Exception)
            {
            }
        }

        private static void OnActiveSceneChanged(Scene from, Scene to)
        {
            try
            {
                Timeline.Write("SCN", "active " + to.name + " (was " + from.name + ")");
            }
            catch (Exception)
            {
            }
        }
    }
}
