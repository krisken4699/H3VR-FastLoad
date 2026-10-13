# FastLoad

Fast-load and memory mod for huge H3VR mod profiles. Target profile: Gale `new2`.

## invent60's request, verbatim (2026-10-12)

> make a fast load mod for huge mod profiles. basically scheduling system of which mod to load first and also use
> the modulworkshop optimizer mod where you can load assets on demand. look at all the custom sosigs and maps beind
> loaded at game start or smth. make it order the things to load and stuff like that. sometimes game and steamvr
> andwhatever crash cuz u dont have enough virtual memory to load things. like i have a few gb of free space in my c
> drive left and my ram is obviously not enough for the entire game + new2 profile so itll spill into growing page
> files n stuff. if its ever not enough you should free up things that arent loaded like u swap maps and sosig
> voicelines or have a bunch of guns not loaded. if possible also like when u load a modular weapon with like a
> single attachment from a mod with a hundred more idk if u shuold load or have to load the entire mod there or just
> get that one thing. idk basically help optimize the game memory and so some maybe hot swap shit and make the game
> load faster while loading more things in the background cuz rn even if the menu scene is loaded and im standing in
> there its already so laggy i cant do shit until it all loads. can u move the mod loading to another thread maybe?
> make sure its compatible with action cam mod and especially when action cam mod can do the restart thing on launch
> too

## Pieces (invent60 approved this split and order, 2026-10-12)

Each piece gets its own spec, plan and build. All four are in scope; none is dropped.

| # | Piece | What it covers from the request | Status |
|---|-------|---------------------------------|--------|
| 1 | Load timeline + memory probe | Measure where load time, menu lag and memory go | built, deployed to new2, start-up measured 2026-10-12 (docs/PROBE-RESULTS-2026-10-12.md); spawn + map part needs invent60 |
| 2 | Menu-first scheduler | Order of what loads first, background loading, off-thread work, menu usable while loading | not started |
| 3 | Memory governor | Free things not in use when memory runs short: maps, sosig voice lines, unused guns; hot swap | not started |
| 4 | Modul part-level loading | Load only the parts a modular gun uses, not every bundle of every mod in the part group | not started |

Compatibility with ActionCam, including its restart on launch, applies to every piece.

## Facts found 2026-10-12 (survey of `new2`, boot log of 2026-10-09)

- 767 plugins, 856 plugin folders, 82.5 GB on disk, 68.4 GB of it asset bundles (files with no extension).
- Stratum "Loading complete (50.341s)". OtherLoaderPatched "All items loaded, took 69.703 seconds!". 733 bundles
  reported, `MaxActiveLoaders = 6`, most bundles report about 18 s because they wait in the queue.
- OtherLoaderPatched `OptimizeMemory = true` and Sirdoggy ModulWorkshop Optimizer are both on already, so guns
  load on demand already.
- One AR2 spawn made ModulWorkshop Optimizer load 35+ whole modular bundles on demand (log lines 9573 onward).
- Other start-up work seen in the log: Custom Sosig Loader (632 sosigs), TNHFramework (410 vault files, 30
  characters), MagazinePatcher cache apply, H3VR Sideloader, SideloaderMint, Atlas.
- `LogOutput.log` has no timestamps, so which of these makes the menu laggy is not known yet.
- PC: 31.7 GB RAM, page file 21.5 GB allocated (peak use 12.6 GB), commit limit 52.7 GB, C: 79.2 GB free,
  i7-14700HX (28 threads), NVMe SSD.
- Windows System log has no Resource-Exhaustion-Detector events. The five `h3vr.exe` crashes logged on 2026-10-12
  (05:26, 05:41, 06:03, 13:15, 13:54) are all exception `0xc0000374` in `ntdll.dll`. Cause not traced.
- ActionCam's preloader patcher (`ActionCam_Source/patcher/GraphicsJobs.cs`) starts the game again once with
  `-force-gfx-jobs`, before any plugin loads. It sets the environment variable `ACTIONCAM_GFXJOBS_RESTART=1` on
  the second start. Measured cost 1166 ms.
- BepInEx 5.4.17, HarmonyX 2.5.5, MonoMod 21.9.19.1, Unity 5.6.7. `bbepis-XUnity_ResourceRedirector` is present in
  `BepInEx/core`.

## Limits that shape the design

- Unity 5.6 API is main-thread only. File reads, JSON and manifest parsing, hashing and cache building can move to
  worker threads. Instantiating objects, `Awake`, texture and mesh upload cannot; those are spread over frames with
  a time budget instead.
- Never enable, disable or move mods on disk inside a Gale profile. FastLoad changes how things load, not which
  mods are installed.

## Start scene (added 2026-10-12 on invent60's request, part of the same mod)

`[Start] Scene` in `invent60.h3vr.fastload.cfg`: skip the main menu into a game scene or an Atlas map; blank = no change.
`[Start] When = Immediately | AfterItemsLoaded`. Names: `BepInEx/FastLoad/scenes.txt` (rewritten every start) and
the console line `Scene loaded: <name>   (to start the game here: [Start] Scene = <name>)`.
Code: `src/StartScene.cs`, `src/AtlasBridge.cs` (Atlas found at run time, no reference), `src/Pure/StartPick.cs`.

Tested by Claude with `tools/startup_run.sh` on new2, 2026-10-12:
- `Scene = IndoorRange_Updated`: menu loaded 121.0 s, range loaded 138.6 s.
- `Scene = driftwood range` (Atlas map by its panel name): menu 127.0 s, DriftWood loaded 147.5 s.
- `Scene = IndoorRange` (no such scene; the indoor range is `IndoorRange_Updated`): stays in menu.
Seen only in the runs that jumped (cause not traced): one "Anvil: Couldn't find bundle ... NGA-SafehouseProgressionMode_Beta
: nga_shp3" + "The Object you want to instantiate is null" just before the jump finished; one "Steamworks is not
initialized"; single-frame NullReferenceExceptions from JerryTF / JerryAKMagEject / JerryTape / PlayerFootsteps in the
first frame of the new scene. OtherLoader's 3 "Failed to load asset" errors are the same with and without a jump.
Not tested: a Take & Hold map as start scene (it would skip the T&H lobby), how the map plays in the headset,
`When = AfterItemsLoaded`.
Unity reports the game's first scene with a load mode that is neither Single nor Additive (prints as 2).

## Fast first load (2026-10-12 evening, invent60: "i want first load to be fast ... go right in and start playing while other things start loading")

invent60's words: QOL mods and tweakers load fast; custom sosigs and T&H characters can load later while he is already in the
indoor range. Built on branch `probe`, all three parts work with the probe switched off (not run that way yet).

### 1. Fast DLL lookup (`[Speed] FastDllLookup`, default on)
Cause found with the probe: when the runtime asks for an assembly that is not loaded, BepInEx (`Utility.TryResolveDllAssembly`)
walks every folder under `core`, `patchers` and `plugins` again, per question. new2 has 1710 plugin folders: about 285 ms per
question. About 140 questions were asked while plugins loaded (421 folder searches, 5 distinct names: Stratum, UnityEditor,
ProBuilderCore-Unity5, H3VRCode-CSharp, BepInEx.BepIn4Patcher), triggered by Harmony's all-assembly type scans
(`PatchAll`, `AccessTools.TypeByName`) hitting assemblies with missing references. The first plugin to scan paid for it,
which is why AmbientMuter (15.8 s), Player Pain Sounds (11.3 s) and Ciarences (7.9 s) looked slow.
Fix: `patcher/DllLookup.cs` answers both public overloads from `DllIndex` (folders read once, 346 ms, same try order).
Measured: plugin loading 89.6 s -> 47.2 s. AmbientMuter 0.7 s, Player Pain Sounds 0.4 s, Ciarences 2.1 s.
Timeline kinds added: `RES` (index built, each missing name once with who asked, totals at each phase mark),
`SLO` (a Harmony type scan that took 100 ms or more).

### 2. Later plugins (`[Later] Plugins`, `[Later] When`)
Plugins named in the config are not started in BepInEx's round (prefix on `GameObject.AddComponent(Type)` returns nothing
for them); `src/LaterLoader.cs` starts them one at a time 3 s after the first scene, or after the `[Start] Scene` when set.
A plugin that depends on a held one is held too. StratumPlugin / DeliBehaviour types are never held.
Measured on new2 with `Plugins = Sosig Hat Realism, CustomSosigModelFramework`:
- CustomSosigModelFramework: 8.9 s and +4151 MB, now paid in the map instead of before the first frame.
- Sosig Hat Realism: only 1.8 s / +131 MB when started late. The 13 s in its turn was game data that the first plugin to
  touch it loads; with the plugin held, most of those seconds showed up between "plugins loaded" and the menu scene
  (1.7 s -> 12 s). Third run with only CustomSosigModelFramework held: menu 71.7 s, range 93.1 s, so holding
  Sosig Hat Realism as well is worth about 4 s. Both stay in new2's list.
Not tested in game: a held plugin with dependents, the StratumPlugin guard, `When = AfterItemsLoaded`.

### 3. Result on new2 (start scene `IndoorRange_Updated`, later = both plugins above)
| mark | before | after |
|---|---|---|
| all plugins loaded | 126.1 s | 55.4 s |
| menu scene | 127.8 s (121.0 s in the jump run) | 67.4 s |
| in the indoor range | 138.6 s | 88.8 s |
Same error lines as the earlier jump runs; nothing new from the two speed-ups.
What is left before the range: preloader 19 s (MonoMod patching about 11 s of it), engine start 11 s, plugins 25 s
(700 small ones), game's own start 12 s, one long frame of about 7.7 s right after the menu loads (Stratum's runtime stage
starting), range load 13.6 s.

### Not answered yet
- Smoothness in the map while items load. The evening runs drew about 5 frames a second from start to end (median 186-201 ms),
  with and without the changes; the afternoon runs had 16-18 ms. Cause not traced (nobody was wearing the headset). Frame
  numbers and "items loaded" times from the evening runs cannot be compared with the afternoon ones.
- The game sets `Application.backgroundLoadingPriority` to High in the menu and Low in the indoor range.
- Where 19 GB goes while items load: not unpacked bundles. Of 756 bundle files, 697 are LZ4HC (read from disk in chunks),
  43 LZMA (1.4 GB unpacked). OtherLoader runs 13 `LoadAllAssetsAsync<T>` per bundle and keeps 730 of 758 bundles open
  (29 unloads seen). Needs a count of what is in memory (textures, meshes, audio) at the marks: next probe addition.
- Freezes when OtherLoader finishes (9.5 s and 4 s): log lines in those frames come from ModulWorkshop_Optimizer,
  MagazinePatcher, PrefabReplacer, Custom Sosig Loader. Cause not traced.
- CustomSosigModelFramework loads its model (4 GB) although `ActiveModel = Sosig` (the model is not shown).

## 2026-10-13: menu never shown, warm DLL read, sosig models on demand

invent60 (verbatim): "i said no need to spawn in menu. skip it entirely and spawn in your configured map." and
"Do we really have to load every single custom sosig while we test guns in the indoor range? Doesn't matter where we
are. I'm pretty sure I started with saying on demand and memory management. Same for everything else."

- Start scene, `When = Immediately`: the scene is asked for inside the menu scene's "loaded" call with a direct
  `SceneManager.LoadSceneAsync` at High priority; headset on the loading screen (`Curtain`). Measured on new2: range
  loaded 0.7 s after the menu scene (was 12.4 s through the game's loader, queued behind mod loads at Low priority).
  The menu's first frame never runs. The engine still loads the menu scene first; that cannot be switched off.
- `[Speed] WarmDlls`: 8 threads read all 856 mod DLLs at preloader start (410 ms warm). Aimed at the 31-39 s gap
  seen before Sodalite's patcher on the first start after hours (3-4 s on repeat starts). Not yet seen on a cold start.
- `[OnDemand] SosigModels`: CustomSosigModelFramework 1.2.7 reads every model pack in Awake whatever ActiveModel says
  (LoadModel per pack: mesh, 12 gz animation clips, textures; 9-10.7 s, +4.2 GB for the one pack on new2). Now its
  pack list is trimmed to the selected pack during Awake and the rest get empty entries filled by its own LoadModel
  on first use (TryAttachProxy / SelectModel). Measured: "loaded 0 model addons", no 9 s stop. The fill path (picking
  the pack in the wrist menu) has not been run in the game.
- Controllers not moving in invent60's 00:23 run: SteamVR log shows Steam Link switching to hand tracking at 5 min 12 s
  ("Switched active controller driver to: 1"); the game then logs 4 IPCError lines a frame. Why Quest switched: not traced.
- Next, by invent60's priority: items on demand and memory (OtherLoader's 730 bundles: 5 -> 32 GB commit, the lag in the map).

## 2026-10-13 (night): on demand, measured with the in-game test driver

invent60: "go on. i let u keep testing on your own. load maps. spawn weapons. spawn attachments. spawn modded content and
maps. spawn sosigs. test if the on demand thing works." and "Might wanna clean up scene, clear guns, etc too" (test
scripts now `clear` after their spawns).

Test driver: `tools/test_run.sh tools/tests/NN_*.txt` (steps in `src/AutoTest.cs`); results are ` TST ` lines in the
run log. Full `census` reads every dropped texture back in: use `census name counts` in governor runs.

What the memory was (new2, IndoorRange_Updated, nobody in the headset):
- After item loading: 9263 item-spawner icon textures (mostly uncompressed RGBA32) = 16 GB of 32 GB commit.
- Spawning one ModulAR15: ModulWorkshop_Optimizer loads all 52 `mw_*` part bundles of the platform with
  `LoadAllAssetsAsync()`: +8600 textures, +31 GB commit, 79-84 s before the gun appears.

| | before | icons on demand | + textures on demand |
|---|---|---|---|
| commit at "all items loaded" | 29.7 GB | 14.0 GB | 10.5 GB |
| settled in the range | 32.3 GB | 16.6 GB | 12.6 GB |
| after spawning ModulAR15 | 47.7 GB | (not run) | 15.1 GB |
| peak of the whole run | 52.2 GB | | 20.1 GB |

- `[OnDemand] ItemIcons` (IconGovernor) and `[OnDemand] Textures` (TextureGovernor): Resources.UnloadAsset on textures
  nothing in the scene uses; Unity reads them back from the open bundle on use. 385 dropped textures came back by
  themselves within 20 s (in use) and were then kept. 0 "not droppable". 12 of 12 sampled icons had pixels again.
- Sosig model pack on demand: read when picked (10.3 s, +4.0 GB), sosig spawned with it; not read otherwise.
- Game gun, modded guns, attachments, sosig clothing, sosigs, modded map (DriftWood), ProvingGround, T&H lobby, back to
  the range: all steps ended without a failure line; 0 probe errors.
- NOT solved: the first modular gun still takes 66 s to appear (the 52 bundles are still read in full; only their
  textures are dropped afterwards). Second spawn 24 ms. Needs part-level loading in/around ModulWorkshop_Optimizer.
- NOT checked: how it looks. Nobody has seen the item spawner or a gun in the headset with the governors on.

## 2026-10-13 request: loading window, ModulWorkshop_Optimizer, lag survey (invent60, verbatim)

"When loading things have a small window that scales horizontally to the text. Make it say what it's loading and from
what mod. And if it's like you have to load a lot of things have like idk loading smth from smth 5/24, In case you have
a lot of things to load. And make it disappear when it's done. No need to report unloading. Have it float in front of
you somewhere on the top like a UI but have its render over everything else so you can't block it with a gun or
something. I don't want it to stutter when you look around too would be good if you can I don't know mount it to your
head. But this shouldn't replace the modul workshop optimizer mod but maybe patch it silently to make it work better
with ours. Make it a dependency that this mod needs. Or at least it's a message I want to send thank you for your work
to that mod author silently without including anything in the read me or stuff. Keep looking for big wins and
especially test a bit on what mods make you lag just having it loaded or having its models in front of you if it's guns
or whatever. Basically see what modded content is adding lag to new2. When you spot it, look for the root cause of
things and try to come up with your way to optimize it and make not just new2 but any other Future mod profiles work
well with all these mods"

Items: (1) loading window: width fits text; what + from which mod; n/m when many; gone when done; no unloading;
head-mounted, top, drawn over everything, no stutter. (2) ModulWorkshop_Optimizer stays; patched silently to work
better with FastLoad; a dependency of FastLoad (BepInDependency now, Thunderstore manifest dependency at packaging);
nothing about it in README. (3) lag survey: which mods cost frame time just loaded / with their models in view; root
causes; fixes that work for any profile.

### Built for that request (2026-10-13, early morning)

- **Loading window** (`[Hud] LoadingWindow`, `src/LoadingHud.cs`, feed `patcher/Pure/LoadFeed.cs`): TextMesh + backing quad
  on the head camera's transform, depth test off, render queue 4000/4001, backing as wide as the text, hidden when the
  feed is empty (0.7 s hold between loads of a batch). Text from the bundle hooks and the optimizer bridge:
  "Loading mw_noveske_n4 from FraDirahra-Noveske_N4   3/48". In the run log as ` HUD ` lines. NOT seen in the headset.
- **ModulWorkshop_Optimizer** is a hard BepInDependency (`00.Sirdoggy.ModulWorkshopOptimizer`). `src/MwoBridge.cs` wraps
  its `LoadAndRegisterBundleAsync` routine (unchanged inside) to name and count its loads, and `[Speed] BoostSpawnLoads`
  raises background loading to Normal while a spawn waits for them. One ModulAR15, 48 bundles: 65 s at the game's
  setting, 57 s at Normal (26-45 fps), 48 s at High (14-46 fps); Normal is the default.
  AT PACKAGING: add its Thunderstore dependency string to manifest.json; nothing about it in README (invent60's wish).
  Still open: loading only the bundles of the parts on the gun (the rest when the workshop UI asks) would remove most of
  those 57 s; that needs part-name -> bundle data the optimizer's cache does not hold.
- **Lag profiler** (test step `profile 20 label`, `src/LagProfiler.cs`): times every mod component's per-frame methods
  and every mod Harmony prefix/postfix (8549 methods on new2; 56 s to set up). Result on new2, indoor range, idle:
  2.49 ms of mod code a frame, of which JerryFBT2.FBTPlugin.FixedUpdate 1.21 ms and JerryFBT.BepInExPlugin.FixedUpdate
  0.41 ms (both: `FixedUpdate() { SetConfig(); }`, i.e. Config.Bind for every setting on every physics tick), ActionCam
  0.53 ms; with 3 guns + 3 sosigs JerryTF.ThngFlingerPGin.FixedUpdate 0.99 ms (Physics.OverlapSphere 50 m + GetComponent
  per body, every tick). Every other mod under 0.04 ms.
- **Tame** (`src/Tame.cs`): `[Tame] ConfigRebinders` finds plugin per-frame methods that only bind config (IL check:
  no branches, calls only Config.Bind / get_Config / BepInEx.Configuration constructors / same-class methods of the same
  kind) and lets them run once; found FBT, FBT2 and Pressig on new2. `[Tame] Throttle` = list of `Type.Method@seconds`
  (default: `JerryTF.ThngFlingerPGin.SearchTarget@0.5`). After: mod code 0.69 ms idle (was 2.49), 0.95 ms with guns and
  sosigs (was 2.92); frame 14.2 ms (was 15.7).
- Not done yet from the request: what modded *models* cost in view (triangles, materials, lights per item).

## 2026-10-13 request: unload setting, load only what is needed (invent60, verbatim)

"have settings to unload things or not. on by default. unload by like if a mod is just sitting there, user spawned its
guns to load it but delete the guns and anything else from that mod from the scene so now its just sitting for a
configurable amount of time, unload it silently. unloading better NOT lag the game at all aight? also u say the load
things one by one thing. do it. load only the thing needed and any dependencies for it. not like u load the gun but not
its scripts."

Items: (1) setting to unload or not, default on; (2) content that was used and is no longer in the scene is unloaded
after a configurable time; (3) silently; (4) unloading must not cause lag; (5) modular guns: load only the part packs
the gun needs plus whatever those need, never a gun without what it depends on.

### Built for the unload / part-level request and what followed (2026-10-13, 03:30-04:40)

- **Modular parts on demand** (`[OnDemand] ModularParts`, `src/MwoParts.cs`, `patcher/Pure/PartIndex.cs`): with a learned
  index ModulAR15 appears in 10.8-11.3 s with 7 of 47 bundles (was 57-65 s, 48 bundles). `check` step (transforms,
  components, renderers, missing materials/meshes) gives the same numbers as the full load for ModulAR15, NWAR2-HK416D
  and ModulAK101Factory. The workshop-panel path (rest of a group loads when a panel initialises) has not been run.
- **Unload** (`[Unload] Enabled`, `IdleSeconds` default 120): a deleted gun's textures were dropped 24-30 s after
  `clear` with IdleSeconds = 30; respawn 24-29 ms, same `check` numbers. Governor work per frame after the scene-walk
  rewrite: 4.6-5.8 ms in its heaviest frames (the one call that lists loaded textures takes 4.4-5.6 ms and cannot be
  split), one 38.6 ms frame in the run not yet explained. Before the rewrite: 22-55 ms. Drops are not logged to the console.
- **Start scene, as invent60 asked 2026-10-13** ("no need to skip right to the map anymore. make it spawn in menu. say
  loading map ... and go. no need to black out the screen"): starts in the menu, window says "Loading map X" from the
  first frame, scene loaded directly (no fade). Then invent60: "in menu theres supposed to be mod stands that lets u
  select tgm and supply raid ... its here in indoor range": cause = those mods (AMP, SupplyRaid, TeamsGameMode) load a
  bundle at menu load and build their stand when it arrives, into whatever scene is there. Now the jump waits until
  bundle loads that began around the menu's load (not by the item loaders) are over and the menu's top-level object
  count is stable for 2.5 s (limit 45 s). Measured: went 17.0 s after the menu loaded; range has no stands; menu has
  Atlas_MapSelector, SR_MapSelector, TGM_MapSelector.
- **Hands**: both profiles' logs (new2 and dev) carry the SteamVR skeleton IPCError lines, and vrserver.txt shows
  Steam Link switching controllers <-> hand tracking many times an hour ("Switched active controller driver to: 1/0").
  In new2 runs the game logs "Setting control mode to CMODE: Oculus ... oculus quest2 (left/right controller)" in the
  menu and again in the range. Nothing found in FastLoad that touches hands; not proven either way.
- Author name is invent60; GUID `invent60.h3vr.fastload`; old config file is moved to the new name at start.

### 2026-10-13 morning: hands stuck after the start-scene jump (fixed), sosig voices (not hooked yet)

- invent60: hands work in dev, not in new2. Probe now writes per second and hand: init, SteamVR pose valid/connected,
  how far SteamVR says the controller moved, how far the hand object moved, and `gap` = distance between the hand
  object and the controller pose. Following = gap 0 mm.
- Measured headless: all features on -> in the range gap 721 / 802 mm, hands not moving, pose ok. All features on but
  `[Start] Scene` empty -> gap 0. Start scene through `SteamVR_LoadLevel.Begin` (the menu buttons' call) -> gap 0 in
  the range. So the direct `SceneManager.LoadSceneAsync` jump was the cause; why the hand stops following after it is
  not traced. The jump goes through the game's loader again (black fade is back). Range reached at 90 s.
- Lesson: a scene reached any other way than SteamVR_LoadLevel must be checked with the hand gap line before it ships.
- Items-loaded freeze (3.3-7.3 s) = Custom Sosig Loader `LoadCustomVoiceLines` (851 .wav decoded on the main thread),
  then 10,927 .ogg requests = the 5-24 fps crawl for ~15 s. `src/SosigVoices.cs` (`[OnDemand] SosigVoices`) defers both to
  first use; it did NOT install in the runs so far (ModifySosig not found by parameter name; lookup changed to the
  Sosig-typed first parameter, not yet seen working).
- FastLoad was removed from new2 at invent60's request while hands were broken and put back after the fix.

### 2026-10-13 afternoon: hands still stuck in invent60's own session; hand guard

- His session 15:45 (jump through SteamVR_LoadLevel): in the menu gap 0; right after the range loaded both controllers
  read disconnected / pose BAD for a moment, then connected with valid pose, and the hands stayed parked (gap ~740 mm,
  controllers moving) until he quit. So the earlier "fixed" (gap 0 headless, controllers lying still and connected
  throughout) did not cover his case. The direct-load theory is not the whole cause.
- From the SteamVR plugin code: pose values are refreshed in Update with events skipped; the events that move the
  hand (SteamVR_Behaviour_Pose.UpdateTransform) fire only from the visual update in SteamVR_Behaviour's
  Camera.onPreCull callback for a stereo camera, and UpdateTransform also needs poseAction.active. During a
  SteamVR_LoadLevel load the same picture shows (values move, hand does not, input on).
- `src/HandGuard.cs` (`[Fix] HandsAfterSceneLoad`): (1) input set off with a controller connected for 1 s -> sets that
  were on are switched on again (seen working once: "switched on again: default"); (2) connected controller, valid
  pose, hand > 50 mm from the pose for 0.75 s outside a load -> FastLoad calls SteamVR_Input.UpdateVisualActions(false)
  every frame until the next scene, and logs what it found (SteamVR objects, eye-camera callbacks, pauseRendering,
  main camera). Repair 2 has NOT been seen working: in the test run the controllers were disconnected.
- Which of the two (or neither) is his fault is unknown until a session of his writes the "Hands:" line.

### 2026-10-13 16:16: hands, the definite cause

- In the scene the start-scene jump leads to, each hand's `SteamVR_Behaviour_Pose` component has
  `poseAction = /actions/mixedreality/in/ExternalCamera` (set `mixedreality`, never active). The hand itself
  (`FVRViveHand.Pose`) has `/actions/default/in/Pose`. `SteamVR_Behaviour_Pose.UpdateTransform` only moves the object
  while `poseAction.active`, so the hands never move although the controller poses are valid and changing.
  Read straight off the objects by the `hands` test step / the guard's description line.
- Repair in `HandGuard` (step 0, every 0.25 s, connected or not): when the component's action path differs from the
  hand's, disable the component, set `poseAction = hand.Pose`, enable it. Measured in the run of 16:13-16:16 with the
  controllers being moved: repair logged 6 s after the range loaded, then steamvrMoved == handMoved, gap 0 mm, both hands.
- NOT traced: why the component comes up with the ExternalCamera action in that load and not in the menu or in loads
  made later by hand. (`poseAction` has a field initialiser `GetAction<SteamVR_Action_Pose>("Pose")` and is re-bound in
  `SteamVR_Action.InitAfterDeserialize`; which of the two goes wrong, and what in new2 triggers it, is open.)
- Earlier explanations in this file (direct load, input set off, pre-cull callback) were wrong or incomplete; the
  guard's other steps stay as fallbacks.

### 2026-10-13 evening: black modular guns, white spawner icons (invent60's report), what was wrong

invent60: loading window full of item loading in the range ("keep it to yourself"; no test script was running, it was
the real item loading being named); spawner icons that are placeholder squares; ModulAero M4E1 / ModulAR parts / HK417 /
MCX pitch black, ModulAK and Kastov fine.

- **Loading window**: now only shows what the player waits for (the map, the part packs of a gun being spawned).
  Background item loading is shown only with `[Hud] ShowBackgroundLoading = true`.
- **Black guns = `[OnDemand] Textures`.** Measured with the new `look` step (the gun alone in a picture): healthy
  brightness 43, after dropping its 72 textures 0; asking the material for the texture (`GetTexture`) reads it back but
  the gun stays black; only `SetTexture(slot, GetTexture(slot))` brings it back (43). So a dropped texture does NOT come
  back by being drawn, which the governor was built on. `Textures` is now off by default and marked not safe. A correct
  version needs: every material holding a dropped texture re-bound when it enters the scene, and no dropping of
  textures that are only reachable through material slots it does not know. Not built.
- **White icons = two causes in `[OnDemand] ItemIcons`**, both fixed and audited:
  1. OtherLoader closes 29 older mods' files while keeping their contents (`Unload(false)`); their icons can never be
     read back, so dropping one loses it for good (sprite alive, `sprite.texture` null, white square). Now a prefix on
     `AssetBundle.Unload(false)` protects the icons of that file's spawner entries, its sprites and same-named files.
  2. Icons held only by spawner entries made at run time are never dropped (their file is unknown).
  Also: a picture any UI image or sprite renderer still holds, shown or hidden, is not dropped, and a hook on
  `Image.set_sprite` reads a dropped picture back when a sprite is handed to an image.
  Audit (`iconaudit`, every icon asked for its picture after item loading): 9264 icons, 0 lost (before: 1 in 12-48
  sampled). Hide-and-show of a 12-icon page: 12 pictures before and after. Commit after item loading 17.3 GB.
- Without the texture governor nothing unloads a gun's textures after it is deleted (`[Unload]` only acts on icons now),
  and each modular platform adds about 8.5 GB when first spawned.
