# FastLoad

Fast-load and memory mod for huge H3VR mod profiles. Target profile: Gale `new2`.

## Ken's request, verbatim (2026-10-12)

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

## Pieces (Ken approved this split and order, 2026-10-12)

Each piece gets its own spec, plan and build. All four are in scope; none is dropped.

| # | Piece | What it covers from the request | Status |
|---|-------|---------------------------------|--------|
| 1 | Load timeline + memory probe | Measure where load time, menu lag and memory go | built, deployed to new2, start-up measured 2026-10-12 (docs/PROBE-RESULTS-2026-10-12.md); spawn + map part needs Ken |
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

## Start scene (added 2026-10-12 on Ken's request, part of the same mod)

`[Start] Scene` in `ken.h3vr.fastload.cfg`: skip the main menu into a game scene or an Atlas map; blank = no change.
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

## Fast first load (2026-10-12 evening, Ken: "i want first load to be fast ... go right in and start playing while other things start loading")

Ken's words: QOL mods and tweakers load fast; custom sosigs and T&H characters can load later while he is already in the
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

Ken (verbatim): "i said no need to spawn in menu. skip it entirely and spawn in your configured map." and
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
- Controllers not moving in Ken's 00:23 run: SteamVR log shows Steam Link switching to hand tracking at 5 min 12 s
  ("Switched active controller driver to: 1"); the game then logs 4 IPCError lines a frame. Why Quest switched: not traced.
- Next, by Ken's priority: items on demand and memory (OtherLoader's 730 bundles: 5 -> 32 GB commit, the lag in the map).
