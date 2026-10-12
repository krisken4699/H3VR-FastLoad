# Probe results, 2026-10-12 (start-up only, two runs launched by Claude, new2, 763 plugins)

Run files: `new2/BepInEx/FastLoad/run_20261012-171816_36028.log` (run 1), `run_20261012-173131_22168.log` (run 2).
Probe health both runs: 0 ERR lines, bundle hooks "managed, 12 of 12", restart on launch seen (cost 2264 / 1315 ms),
hooks saw 760 bundle loads vs 734 reported by loaders, 0 bundles used without being seen loading.
Not yet measured (needs invent60 in headset): one AR2 spawn, one T&H map load and back.

## Time (run 2; run 1 within a few seconds)
| mark | at |
|---|---|
| preloader done | 24.1 s |
| plugins begin loading | 35.7 s |
| all plugins loaded (no frame drawn until here) | 126.1 s |
| menu scene loaded | 127.8 s |
| Stratum loading complete | 179.2 s |
| OtherLoader all items loaded | 194.1 s |
| quiet (menu really usable) | 212.2 s |

Menu loaded until quiet = 84 s: median frame 18 ms, 252 frames >= 50 ms, 22 frames >= 250 ms, worst 9.5 s (run 1: 10.2 s,
at the moment OtherLoader finishes; also 5.1 s, 4.0 s, 3.1 s freezes). OtherLoader had up to 529 bundles in flight at once.
30 s after quiet: median 14.0 ms, p99 24.5 ms.

## Plugin loading (90 s, +7.9 GB commit), biggest turns
| plugin | time | commit |
|---|---|---|
| AmbientMuter 2.2.1 | 15.8 s | +68 MB |
| Sosig Hat Realism 1.0.0 | 14.2 s | +2210 MB |
| Player Pain Sounds 1.0.7 | 11.3 s | +12 MB |
| CustomSosigModelFramework 1.2.7 | 8.7 s | +3785 MB |
| CiarencesUnbelievableModifications 1.3.1 | 7.9 s | -458 MB |
| Deli 0.4.1 | 3.9 s | +996 MB |
| Blood System 3.4.1 | 3.7 s | +265 MB |
| SideloaderMint 1.5.0 | 3.0 s | +120 MB |
These 8 = 68.5 of the 89.6 s. A plugin's turn = everything that ran between its "Loading [...]" line and the next.

## Memory
Process commit: 3.3 GB when plugins begin, 11.2 GB when all plugins are loaded, 30.4 GB at Stratum done, 32.0 GB at
OtherLoader done, peak 34.9 GB (run 1). The bundle files loaded in that phase total 3.8 GB on disk.
System commit left: lowest 115 MB (run 1, at 185 s); 395 MB at OtherLoader done (run 2). The system commit limit grew from
52.7 GB to 70.5 GB during run 1 (Windows growing the page file).
Working set stays 7 to 17 GB: most of the commit is not in RAM.

## Open
- What the 19 GB between "all plugins loaded" and "Stratum done" is. Per-bundle commit cannot say: hundreds of loads overlap.
- Run 1, after load: a 150 to 240 ms hitch every ~26 s with nothing logging. Cause unknown; SEC lines now carry gc= to check.
