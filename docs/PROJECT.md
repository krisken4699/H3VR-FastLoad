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
