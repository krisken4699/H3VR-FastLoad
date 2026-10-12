# FastLoad piece 1: load timeline + memory probe

Date: 2026-10-12. Design approved by Ken in chat the same day. Project brief: `docs/PROJECT.md`.

## Purpose

Find out, with numbers, where start-up time, menu lag and memory go in the `new2` profile, so pieces 2 to 4
(scheduler, memory governor, modular part-level loading) are built against measurements and not against guesses.

The probe changes no game behaviour. It only observes and writes files.

## Success

After one test run by Ken, the files answer all of these:

1. How long each start-up phase takes, in wall-clock time.
2. Which frames in the main menu were slow, and what was loading during each one.
3. Which bundles and which plugins cost the most time and the most memory.
4. Which bundles were loaded and never used in the session.
5. How close the process and the system came to the commit limit, and when.
6. What one modular gun spawn pulled in: bundles, time, memory.

## Mod

- Name `FastLoad`. Repo `C:\Users\Hp\Desktop\FastLoad_Source`, branch `main`.
- Plugin GUID `ken.h3vr.fastload`. Patcher assembly `FastLoadPatcher`.
- `net46`, C# 8, references taken from the Gale `new2` profile and the game's `Managed` folder, same layout as
  `ActionCam_Source` (`FastLoad.csproj` at the root with `patcher\**` excluded, `patcher/FastLoadPatcher.csproj`).
- Deploy target: `new2`, to `BepInEx/plugins/FastLoad/` and `BepInEx/patchers/FastLoad/`. Deploy goes through a
  `tools/deploy.sh` that builds, runs the runtime check (calls checked against the game's `mscorlib`), then copies.
- No dependency on Sodalite or any loader mod.
- Pieces 2 to 4 are added to this same mod later.

## Output files

All under `<profile>/BepInEx/FastLoad/`:

| File | Content |
|------|---------|
| `run_<yyyyMMdd-HHmmss>_<pid>.log` | Timeline, one event per line, appended and flushed as it happens |
| `report_<yyyyMMdd-HHmmss>_<pid>.txt` | Summary of that run, rewritten every 10 s and at quit |

The newest 6 runs are kept; older `run_*` and `report_*` pairs are deleted at start-up.

Every timeline line starts with `ms since process start` (from `Process.StartTime`, kept running by a
`Stopwatch`) and the Unity frame number (`-` before the first frame), then a kind tag, then the fields:

```
  48213 f=1432 LOG  [Info:OtherLoaderPatched] Completed loading bundle 'shotclock' in 19.131 seconds!
  48240 f=1433 BUN  load-end path=...\Not_Wolfie-AR2_Plus\ar2plus-main size=412MB wall=18034ms main=212ms commit=+388MB by=OtherLoader
  48260 f=1434 HIT  frame=187ms loading=3 [ar2plus-main, modulglock, surplus]
  49000 f=1466 SEC  fps=31 worst=187ms commit=14212MB ws=11020MB sysfree=21544MB mono=1890MB bundles=214 inflight=3
  61020 f=2110 SCN  loaded MainMenu3
```

## Parts

### 1. Patcher: clock and log mirror

`FastLoadPatcher.Initialize()` (BepInEx preloader patcher, `TargetDLLs` empty):

- Starts the run clock and opens the `run_*.log` file.
- Adds a BepInEx `ILogListener` that writes every log event as a `LOG` line. This turns the existing 30k-line
  `LogOutput.log` content into a timeline with no hooks in other mods.
- Exposes the clock and the writer to the plugin through a small static class in the patcher assembly
  (`FastLoadPatcher.Timeline`), which the plugin references.
- Restart on launch (ActionCam starts the game a second time before any plugin loads): the patcher does nothing
  heavy and always opens its run file, in both starts. At start-up it deletes every older run file that has no
  "Chainloader started" line (the file left by a start that handed over), so those never count against the kept
  runs. In the second start ActionCam sets `ACTIONCAM_GFXJOBS_RESTART=1` and `ACTIONCAM_GFXJOBS_RESTART_MS`; when
  present, the report's Run section prints "restart on launch: yes, cost N ms" from that value.
- The writer is thread safe (log events and bundle callbacks can come from other threads) and flushes on every
  line, so a crash loses nothing that was already written.

### 2. Bundle hooks

Harmony patches on `UnityEngine.AssetBundle` (Unity 5.6.7 has no `LoadFromStream`). The hook code lives in the
patcher assembly and is installed the moment the log mirror sees BepInEx's "Chainloader started" line, which is
before any plugin's code runs; the plugin's `Awake` calls the same install again as a safety net (it does nothing
the second time).

- Bundle loads, managed wrappers (default): `LoadFromFile(string)`, `LoadFromFile(string, uint)`,
  `LoadFromFileAsync(string)`, `LoadFromFileAsync(string, uint)`, `LoadFromMemory(byte[])`,
  `LoadFromMemoryAsync(byte[])`.
- Bundle loads, engine-native methods (only with `Probe.NativeHooks = true`): `LoadFromFile(string, uint, ulong)`,
  `LoadFromFileAsync(string, uint, ulong)`, `LoadFromMemory(byte[], uint)`, `LoadFromMemoryAsync(byte[], uint)`.
  Off by default: the game already has unexplained `0xc0000374` crashes, and native detours are not added to it
  without need. A mod that calls the three-argument form directly is missed in the default mode.
- Asset use: `LoadAsset(string, Type)`, `LoadAssetAsync(string, Type)`, `LoadAssetWithSubAssets(string, Type)`,
  `LoadAssetWithSubAssetsAsync(string, Type)`, `LoadAllAssets(Type)`, `LoadAllAssetsAsync(Type)`. Every other
  public overload goes through one of these.
- `Unload` exists only as an engine-native method, so it is not hooked. Once per second the probe checks each
  bundle it knows; one that Unity reports as destroyed is marked unloaded (precision about 1 s).

For each bundle the probe records: file path and size (when loaded from a file), the mod it belongs to (folder
name under `BepInEx/plugins/`), who asked for it (first assembly on the call stack that is not Unity, Harmony,
MonoMod, mscorlib or FastLoad, read once per load call), load start and end, wall time, main-thread time spent
inside the call, process commit before and after, number of asset calls, time of last use, unload time. Async
loads end when the request's `isDone` turns true, checked on every hook call and by the sampler each frame.

Lines written: `BUN load-start`, `BUN load-end`, `BUN asset` (only when a single asset call blocks the main
thread for 5 ms or more), `BUN unload`, and one `HKS` line saying which mode is on and how many hooks applied.

Cross-check, always printed in the report: the number of bundles the hooks saw next to the number the loaders
reported in their own log lines (OtherLoaderPatched "Completed loading bundle", ModulWorkshop Optimizer "On-demand
async loading modular bundle"). A large gap means loads are being missed and is reported to Ken, never hidden.

### 3. Sampler

One `MonoBehaviour` on a `DontDestroyOnLoad` object, created in the plugin's `Awake`:

- Every frame: frame time from the run clock (real time, not `Time.deltaTime`).
- `HIT` line for every frame of 25 ms or more: frame time, bundles in flight at that moment, and the sources of
  the log lines written during that frame.
- `SEC` line once per second: fps, worst frame of the second, process commit (`PrivateUsage`), working set,
  system commit still free (`GlobalMemoryStatusEx`), Mono heap (`GC.GetTotalMemory(false)`), bundles loaded,
  bundles in flight.
- Memory numbers come from `GetProcessMemoryInfo` and `GlobalMemoryStatusEx` through P/Invoke.

### 4. Scene marks

`SceneManager.sceneLoaded` and `activeSceneChanged` write `SCN` lines.

### 5. Report

`report_*.txt`, plain text, rewritten every 10 s from data held in memory, and at `OnApplicationQuit`:

1. **Run**: profile path, command line flags, plugin count, whether a restart on launch happened and its cost.
2. **Phases**, start time and length of each: process start, preloader done, chainloader start, chainloader done,
   Stratum "Loading complete", OtherLoaderPatched "All items loaded", first menu scene loaded, quiet (first 10 s
   with no bundle in flight and no frame of 25 ms or more).
3. **Menu frames**: count of frames of 25, 50, 100, 250 ms or more, and median and 99th percentile frame time,
   for two windows: menu scene loaded until quiet, and the 30 s after quiet.
4. **Bundles**: top 40 by wall time, top 40 by main-thread time, top 40 by commit growth; each with path, size,
   owner plugin.
5. **Mods**: per mod folder, bundle count, total size, total main-thread time, total commit growth; and the same
   totals per loader assembly that asked for the bundles.
6. **Loaded, never used**: bundles with zero asset calls by the end of the run, with size. Scene bundles (maps)
   show here too, because scenes are not loaded through asset calls; the report says so.
7. **Memory**: commit and working set at each phase mark, peak commit with its time, lowest system commit free
   with its time.
8. **Spawn bursts**: after quiet, every group of bundle loads that starts within 2 s of each other is listed as
   one burst: bundles, total size, wall time, frames of 25 ms or more during it, commit growth. One modular gun
   spawn shows up as one burst.

Phase marks that depend on another mod's log text are matched by text; a mark whose text never appeared is
printed as "not seen", never guessed.

## Config

`BepInEx/config/ken.h3vr.fastload.cfg`:

| Key | Default | Meaning |
|-----|---------|---------|
| `Probe.Enabled` | `true` | Turns the whole probe off: no hooks, no sampler, no files |
| `Probe.HitchMs` | `25` | A frame this long or longer is written as `HIT`. Example: `25` catches anything under 40 fps |
| `Probe.KeepRuns` | `6` | How many runs to keep. Example: `6` |
| `Probe.NativeHooks` | `false` | Also hook the engine-native bundle load methods. Example: `true` when the report's cross-check shows missed loads |

Examples sit in the config descriptions, as above. The patcher reads `Probe.Enabled` straight from the cfg file,
because BepInEx config objects are not available in the preloader.

## Errors

- Every hook body and every sampler step is wrapped in try/catch. A failure is logged once per hook with its
  exception type and message, that hook is switched off, and the game goes on untouched.
- If the output folder or file cannot be opened, the probe logs one error and stays off for that run.
- The probe never throws into another mod's call.

## Rules carried over from Ken's other H3VR mods

- No `System.Action` (non-generic) anywhere: own delegate types. Runtime check before every deploy.
- All timing on real time (`Stopwatch`); nothing reads the game's time scale.
- No mention of CursedLL in logs, type names, config text or docs shipped with the mod.
- Git commits for iteration; no manifest version bump or CHANGELOG until a Thunderstore package is asked for.
- Logs stay in; the probe is never stripped when later pieces are built.

## Test

Build checks, by me:

- `dotnet build` of both projects clean; runtime check passes.
- Unit tests (plain .NET test project, no Unity) for the parts with logic: timeline line format, phase matcher
  on lines copied from the 2026-10-09 `LogOutput.log`, burst grouping, never-used list, run pruning.

In game, by Ken, once:

1. Launch `new2`.
2. Stand in the menu until it feels smooth.
3. Spawn one AR2 modular gun from the item spawner.
4. Load one T&H map, then go back to the main menu.
5. Quit.

Then I read `run_*.log` and `report_*.txt`, check each of the six success questions has an answer, and report the
numbers. Nothing is called working until that run exists.

## Not in this piece

- Any change to load order, timing, threading or memory use.
- Any in-game readout.
- Thunderstore packaging.
