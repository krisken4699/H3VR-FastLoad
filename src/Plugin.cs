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
    [BepInDependency(MwoBridge.Guid, BepInDependency.DependencyFlags.HardDependency)]
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

            Config.Bind("Speed", "FastDllLookup", true,
                "Faster start: when the game asks for an assembly that is not loaded, BepInEx searches every folder of the profile again, each time.\n"
                + "On a big profile that is thousands of folders per question and many questions. With this on, the folders are read once and every\n"
                + "question is answered from that list. The answers are the same. Turn off only to compare start-up times.\n"
                + "Example: FastDllLookup = false");
            Logger.LogInfo("Fast DLL lookup so far: " + DllLookup.Summary());

            Config.Bind("Speed", "WarmDlls", true,
                "Faster first start of the day: the moment the game starts, read every mod DLL once in the background, on 8 threads.\n"
                + "A start after the PC has done other things reads those files one by one and can sit for half a minute before the game window\n"
                + "even shows; read ahead in parallel they come from Windows' file cache. Nothing is changed and nothing is kept.\n"
                + "The result is one line in the log: 'Warm DLL read: warm dlls=856 ... totalMs=...'.\n"
                + "Example: WarmDlls = false");
            Logger.LogInfo("Warm DLL read: " + Warm.Mode);

            Config.Bind("OnDemand", "SosigModels", true,
                "CustomSosigModelFramework reads every installed sosig model pack into memory when the game starts (about 9 s and 4 GB for one\n"
                + "pack), also when its ActiveModel is 'Sosig' and no pack is shown. With this on, a pack is read the first time it is needed:\n"
                + "when it is the selected model and a sosig appears, or when you pick it in the wrist menu (the game stands still for those\n"
                + "seconds, with a black screen). With ActiveModel = Sosig nothing is read at all. 'Random' reads every pack at start as before.\n"
                + "Does nothing when that mod is not installed. Needs a game restart.\n"
                + "Example: SosigModels = false");
            ConfigEntry<bool> itemIcons = Config.Bind("OnDemand", "ItemIcons", true,
                "Every item in the item spawner has a picture, and all of those pictures are read into memory while the item lists load, whether\n"
                + "or not you ever open the spawner. On a big profile that is thousands of mostly uncompressed textures: about 16 GB of the game's\n"
                + "32 GB on the profile this was built on. With this on, their pixels are dropped again as the lists come in; Unity reads a picture\n"
                + "back from its mod file by itself the moment the spawner shows it, and it is dropped again once it is off screen.\n"
                + "Cost: a spawner page can take a moment longer to show its pictures the first time. Nothing is changed on disk.\n"
                + "Log: 'Item icons on demand: dropped N item pictures ...: memory -X MB'.\n"
                + "Example: ItemIcons = false");
            if (itemIcons.Value)
            {
                try
                {
                    gameObject.AddComponent<IconGovernor>().Log = Logger;
                }
                catch (Exception e)
                {
                    Logger.LogError("Item icons on demand could not start: " + e.GetType().Name + ": " + e.Message);
                }
            }

            ConfigEntry<bool> textures = Config.Bind("OnDemand", "Textures", true,
                "Mod loaders read whole mod files to get at a few things in them. Spawning one modular rifle loaded every part pack for its\n"
                + "platform in full on the profile this was built on: 8600 textures, +31 GB, for the handful of parts on the gun.\n"
                + "With this on, textures that nothing in the scene uses are dropped again a few seconds after they come in. Unity reads a dropped\n"
                + "texture back from its mod file by itself when something shows it (a part you pick in the workshop, a gun you spawn).\n"
                + "Left alone: the map's own textures, anything a renderer or UI element in the scene uses, textures made while the game runs.\n"
                + "Cost: the first time a dropped texture is shown again it is read from disk, which can be a short hitch.\n"
                + "Log: 'Textures on demand: dropped N textures nothing in the scene uses: memory -X MB'.\n"
                + "Example: Textures = false");
            if (textures.Value)
            {
                try
                {
                    gameObject.AddComponent<TextureGovernor>().Log = Logger;
                }
                catch (Exception e)
                {
                    Logger.LogError("Textures on demand could not start: " + e.GetType().Name + ": " + e.Message);
                }
            }

            // A pack being read stops the game: black in the headset instead of a stuck picture.
            bool modelCurtain = false;
            OnDemandSosigModels.Before = delegate() { modelCurtain = !Curtain.Closed; if (modelCurtain) Curtain.Close(); };
            OnDemandSosigModels.After = delegate() { if (modelCurtain) Curtain.Open(0.3f); };
            Logger.LogInfo("Sosig models on demand: " + OnDemandSosigModels.Mode);

            ConfigEntry<string> laterPlugins = Config.Bind("Later", "Plugins", "",
                "Faster start: plugins to hold back until the game is on screen. BepInEx starts every plugin before the first frame, one after\n"
                + "another; a few heavy ones can take most of that time. The plugins named here are started after you are in the map instead.\n"
                + "Good candidates: things you do not need in the first minute (hats, voice packs).\n"
                + "Bad candidates: loaders (OtherLoader, Sideloader, Deli, Stratum, Atlas), anything that patches the menu, anything other mods need.\n"
                + "Name a plugin by its name or its GUID, exactly as in its 'Loading [Name 1.0.0]' log line; separate with commas. Capital letters do not matter.\n"
                + "A plugin that needs a held one is held too. The game can stand still for as long as a held plugin takes to start.\n"
                + "To find the slow ones: BepInEx/FastLoad/report_*.txt, section '9. Plugin loading'.\n"
                + "Example: Plugins = Sosig Hat Realism\n"
                + "Leave empty to start every plugin the normal way. Needs a game restart.");
            ConfigEntry<LaterWhen> laterWhen = Config.Bind("Later", "When", LaterWhen.InTheMap,
                "When the held plugins start.\n"
                + "InTheMap: a few seconds after the first scene is on screen (after the [Start] Scene when one is set).\n"
                + "AfterItemsLoaded: only after OtherLoader reports 'All items loaded', so they do not compete with item loading.\n"
                + "Example: When = AfterItemsLoaded");
            try
            {
                LaterLoader later = gameObject.AddComponent<LaterLoader>();
                later.When = laterWhen.Value;
                later.Log = Logger;
                later.Begin();
            }
            catch (Exception e)
            {
                Logger.LogError("Later plugins could not start: " + e.GetType().Name + ": " + e.Message);
            }

            ConfigEntry<string> startScene = Config.Bind("Start", "Scene", "",
                "Skip the main menu: when the game starts, go straight to this scene. Leave empty to start in the main menu as usual.\n"
                + "The menu is never shown: the headset stays on the loading screen until you stand in the scene. (The game engine always loads\n"
                + "its menu scene first, that cannot be switched off; the chosen scene starts loading behind it the moment it exists.)\n"
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
                + "Immediately: the menu is never shown. Mods are still loading in the background when you arrive, so the first minute or so in the map can stutter and the item spawner fills up as it goes.\n"
                + "AfterItemsLoaded: wait in the menu (shown as usual) until OtherLoader reports 'All items loaded', then go. Smooth from the start, item spawner complete.\n"
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

            try
            {
                if (System.IO.File.Exists(AutoTest.ScriptPath()))
                {
                    AutoTest test = gameObject.AddComponent<AutoTest>();
                    test.Log = Logger;
                    test.Begin();
                }
            }
            catch (Exception e)
            {
                Logger.LogError("Autotest could not start: " + e.GetType().Name + ": " + e.Message);
            }

            ConfigEntry<bool> rebinders = Config.Bind("Tame", "ConfigRebinders", true,
                "Some plugins bind their whole config again every frame or physics tick (measured: 0.95 ms a tick for one plugin with 8 settings).\n"
                + "Binding a setting twice just returns the same setting, so everything after the first time is wasted. With this on, a plugin's\n"
                + "Update / FixedUpdate / LateUpdate that does nothing but bind settings runs once and is skipped afterwards. Found by looking at\n"
                + "the plugin's code when the game starts, so it works for plugins FastLoad has never heard of. Each one found is named in the log.\n"
                + "Example: ConfigRebinders = false");
            ConfigEntry<string> throttle = Config.Bind("Tame", "Throttle", "JerryTF.ThngFlingerPGin.SearchTarget@0.5",
                "Mod methods that run every frame or physics tick but do not need to: each runs at most once per the given number of seconds,\n"
                + "the calls in between are skipped. Write each as Namespace.Type.Method@seconds, separated by commas. Only methods that return\n"
                + "nothing. Entries for mods that are not installed do nothing.\n"
                + "The default entry: Voice Control For ThingFlinger looks for every physics object within 50 m of you on every physics tick\n"
                + "(measured 1.0 ms a frame with three guns and three sosigs around, more with more objects); twice a second is plenty.\n"
                + "To find candidates: tools/tests/07_lag_profile.txt in the FastLoad source writes BepInEx/FastLoad/lag_*.txt, mod by mod.\n"
                + "Example: Throttle = JerryTF.ThngFlingerPGin.SearchTarget@0.5, Some.Mod.Scanner.FindTargets@1\n"
                + "Leave empty to throttle nothing.");
            try
            {
                Tame tame = gameObject.AddComponent<Tame>();
                tame.Log = Logger;
                tame.Rebinders = rebinders.Value;
                tame.ThrottleList = throttle.Value ?? "";
            }
            catch (Exception e)
            {
                Logger.LogError("Tame could not start: " + e.GetType().Name + ": " + e.Message);
            }

            ConfigEntry<bool> window = Config.Bind("Hud", "LoadingWindow", true,
                "A small line of text in front of you, above your line of sight, while something loads: what is loading and from which mod,\n"
                + "with a count when there is more than one thing ('Loading mw_ar2_plus from Not_Wolfie-AR2_Plus   5/24'). Gone when it is done.\n"
                + "It moves with your head and is drawn over everything, so a gun in front of it does not hide it. Unloading is not shown.\n"
                + "Example: LoadingWindow = false");
            ConfigEntry<bool> boost = Config.Bind("Speed", "BoostSpawnLoads", true,
                "When you spawn a modular gun, it cannot appear until the part packs for it are loaded. The game loads in the background at its\n"
                + "slowest setting (2 ms of each frame). With this on, background loading gets 10 ms of each frame while such a spawn is waiting,\n"
                + "and goes back afterwards: the gun appears sooner, frames are a little longer meanwhile.\n"
                + "Example: BoostSpawnLoads = false");
            try
            {
                MwoBridge.Install(Logger);
                Logger.LogInfo("ModulWorkshop_Optimizer bridge: " + MwoBridge.Mode);
                // The window reads what the bundle hooks see; with the probe off they are not in yet.
                if (window.Value && !Timeline.Enabled) BundleHooks.Install(false);
                LoadingHud hud = gameObject.AddComponent<LoadingHud>();
                hud.Log = Logger;
                hud.ShowWindow = window.Value;
                hud.Boost = boost.Value;
            }
            catch (Exception e)
            {
                Logger.LogError("Loading window could not start: " + e.GetType().Name + ": " + e.Message);
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
                Timeline.Write("SCN", "loaded " + scene.name + " mode=" + mode + " backgroundLoadingPriority=" + UnityEngine.Application.backgroundLoadingPriority);
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
