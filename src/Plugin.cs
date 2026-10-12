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

            ConfigEntry<string> startScene = Config.Bind("Start", "Scene", "",
                "Skip the main menu: when the game starts, go straight to this scene. Leave empty to start in the main menu as usual.\n"
                + "Works for the game's own scenes and for modded maps (Atlas). Capital letters do not matter.\n"
                + "Examples:  Scene = IndoorRange_Updated     Scene = ArizonaTargets     Scene = ProvingGround     Scene = DriftWood (a modded map)\n"
                + "How to find a name:\n"
                + "  1. Open BepInEx/FastLoad/scenes.txt (written every time the game starts): every game scene and every modded map, ready to copy.\n"
                + "  2. Or load the map once by hand and read the BepInEx console / LogOutput.log: FastLoad prints\n"
                + "     'Scene loaded: <name>   (to start the game here: [Start] Scene = <name>)'.\n"
                + "For a modded map use its identifier from scenes.txt; the name on its menu panel works too if no other map shares it.\n"
                + "A name that does not exist changes nothing: you stay in the menu and the log says so.\n"
                + "The wrist menu's return to main menu works as always; the jump happens once per game start.");
            ConfigEntry<StartWhen> startWhen = Config.Bind("Start", "When", StartWhen.Immediately,
                "When to go to [Start] Scene.\n"
                + "Immediately: the moment the main menu has loaded. Mods are still loading in the background then, so the first minute or so in the map can stutter and the item spawner fills up as it goes.\n"
                + "AfterItemsLoaded: wait in the menu until OtherLoader reports 'All items loaded', then go. Smooth from the start, item spawner complete.\n"
                + "Example: When = AfterItemsLoaded");
            try
            {
                StartScene start = gameObject.AddComponent<StartScene>();
                start.Wanted = startScene.Value ?? "";
                start.When = startWhen.Value;
                start.Log = Logger;
                start.Begin();
            }
            catch (Exception e)
            {
                Logger.LogError("Start scene could not start: " + e.GetType().Name + ": " + e.Message);
            }

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

                // A run that dies while plugins are still loading leaves a report too.
                ReportJob.Kick();

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
