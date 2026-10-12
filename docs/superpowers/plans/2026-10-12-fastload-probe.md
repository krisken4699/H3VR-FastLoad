# FastLoad Probe Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A BepInEx patcher + plugin for H3VR that writes a timestamped load timeline and a summary report (phases, slow frames, bundles, memory) for the `new2` profile, changing no game behaviour.

**Architecture:** Two assemblies. `FastLoadPatcher.dll` (BepInEx preloader patcher) starts the run clock, mirrors every BepInEx log line into a timeline file, tracks phases, and installs Harmony hooks on `UnityEngine.AssetBundle` the moment "Chainloader started" is logged. `FastLoad.dll` (plugin) adds a per-frame sampler, scene marks and the report writer. Everything with logic lives in `Pure/` folders with no Unity or BepInEx types, and is unit-tested from a plain .NET console project.

**Tech Stack:** C# 8 compiled for `net46`, run on Unity 5.6.7's Mono (.NET 3.5 profile). BepInEx 5.4.17, HarmonyX 2.5.5. Tests: `net9.0` console app, no test framework, no NuGet packages. Bash + Python 3.10 + `ilspycmd` for deploy.

**Spec:** `docs/superpowers/specs/2026-10-12-fastload-probe-design.md` (read it first). Project brief: `docs/PROJECT.md`.

## Global Constraints

- Repo root: `C:\Users\Hp\Desktop\FastLoad_Source` (bash: `/c/Users/Hp/Desktop/FastLoad_Source`). All paths below are relative to it. Branch `main`.
- Plugin GUID `ken.h3vr.fastload`, name `FastLoad`, version `0.1.0`. Patcher assembly `FastLoadPatcher`, namespace `FastLoad.Early`. Plugin namespace `FastLoad`.
- Game runtime is Mono with the .NET 3.5 class library. Code compiled for `net46` can bind to members the game does not have. Never use any of these:
  - `lock (x) { }` — write `Monitor.Enter(x); try { } finally { Monitor.Exit(x); }`
  - `System.Action` (non-generic) — declare an own delegate type
  - `yield` iterators, `async`/`await`
  - `Marshal.SizeOf<T>()` — use `Marshal.SizeOf(typeof(T))`
  - `Stopwatch.Restart()` — use `Reset()` then `Start()`
  - `StringBuilder.Clear()`, `string.IsNullOrWhiteSpace`, `string.Join(string, IEnumerable<string>)` (pass a `string[]`), `Path.Combine` with 3+ arguments, `File.ReadLines`, `Directory.EnumerateFiles`, `Enum.HasFlag`, `Stream.CopyTo`, `Tuple`, `System.Collections.Concurrent`
  - `==` / `!=` on `Type`, `MethodInfo`, `FieldInfo` — use `ReferenceEquals` or compare names
- No LINQ in mod code (use `List<T>.Sort` and loops).
- All timing comes from `FastLoad.Early.Clock` (a `Stopwatch`, real time). Never read `UnityEngine.Time.time`, `deltaTime` or `timeScale`.
- Every number or date written to a file is formatted with `CultureInfo.InvariantCulture` (the PC's locale uses the Buddhist calendar).
- The strings "CursedLL", "slow-mo", "slow motion", "timeScale" never appear in code, logs, config text or docs.
- Every hook body and every `Update` step is wrapped in try/catch; a failure writes one `ERR` line and switches that hook group off. Nothing ever throws into another mod's call.
- Never enable, disable, rename or move any other mod's files in a Gale profile. The only files this plan writes inside a profile are `BepInEx/plugins/FastLoad/FastLoad.dll`, `BepInEx/patchers/FastLoad/FastLoadPatcher.dll`, and at run time `BepInEx/FastLoad/*` and `BepInEx/config/ken.h3vr.fastload.cfg`.
- Deploy target is the Gale profile `new2` only: `C:\Users\Hp\AppData\Roaming\com.kesomannen.gale\h3vr\profiles\new2`. Deploy only through `tools/deploy.sh`.
- No manifest, README, CHANGELOG or version bump in this plan (no Thunderstore package yet).
- Commits: one per task, message in normal English, ending with the trailer line `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`.
- Config descriptions carry their own example value (see Task 9).
- Do not launch the game. The in-game run is Ken's (Task 10).

## Review Focus

Conditions the spec implies that are most likely to bite, each pinned by a test in the task named:

1. PC locale is Thai (Buddhist calendar, year 2569): file stamps must still be `2026...` and decimals must use `.` — Task 2, `RunFilesTests.StampIgnoresLocale`, `LineFormatTests.NumbersIgnoreLocale`.
2. A log event whose text has newlines or a long stack trace must stay one timeline line — Task 2, `LineFormatTests.FlattenKeepsOneLine`, `TimelineWriterTests.MultiLineTextIsOneLine`.
3. Restart on launch: the handed-over start leaves a file with no "Chainloader started" line, and may still hold it open when the second start prunes — Task 2, `RunFilesTests.StubIsDeleted`, `RunFilesTests.LockedFileDoesNotThrow`, `RunFilesTests.CurrentRunIsNeverDeleted`.
4. A bundle load that returns nothing (already loaded, bad file), and an asset call on a bundle the hooks never saw load — Task 4, `BundleLedgerTests.FailedLoadIsClosed`, `BundleLedgerTests.UnknownBundleIsAdopted`.
5. A run that dies before the menu or before quiet (the game does crash): the report must be writable from empty or partial data and say "not seen" — Task 8, `ReportBuilderTests.EmptyRunBuilds`, `ReportBuilderTests.MissingPhasesSayNotSeen`.

---

## File Structure

```
FastLoad.csproj                    plugin project (root), excludes patcher/ and tests/
patcher/
  FastLoadPatcher.csproj
  Pure/Clock.cs                    run clock (ms since process start)
  Pure/LineFormat.cs               timeline line + number formatting
  Pure/TimelineWriter.cs           thread-safe, flush-per-line file writer
  Pure/CfgPeek.cs                  reads one value from a BepInEx .cfg text
  Pure/RunFiles.cs                 file naming, stub detection, pruning
  Pure/Phases.cs                   phase marks from log text and scene names
  Pure/FrameSources.cs             which log sources wrote during the current frame
  Pure/BundleLedger.cs             BundleRecord + the table of all bundles
  Pure/Memory.cs                   process commit / working set / system commit (P/Invoke)
  Timeline.cs                      static facade shared with the plugin
  LogMirror.cs                     BepInEx ILogListener
  HookGate.cs                      keeps Unity types out of the preloader's JIT
  BundleHooks.cs                   Harmony patches on UnityEngine.AssetBundle
  Entry.cs                         the preloader patcher class
src/
  Pure/FrameLog.cs                 frame samples + stats
  Pure/MemLog.cs                   per-second memory samples
  Pure/QuietTracker.cs             finds the first quiet 10 s
  Pure/Bursts.cs                   groups bundle loads into spawn bursts
  Pure/ReportBuilder.cs            builds the report text
  ReportJob.cs                     snapshot + background write
  Sampler.cs                       MonoBehaviour: per-frame work
  Plugin.cs                        BepInPlugin: config, sampler, scene marks
tests/
  FastLoad.Tests.csproj            net9.0 console, links both Pure/ folders
  T.cs                             tiny assert helper
  Program.cs                       runs every test class
  *Tests.cs
tools/
  deploy.sh                        build + runtime check + copy to new2
  check_runtime.py                 copied from ActionCam_Source/tools
```

Reference paths used by the project files:

- `NEW2 = C:\Users\Hp\AppData\Roaming\com.kesomannen.gale\h3vr\profiles\new2\BepInEx`
- `MANAGED = C:\Program Files (x86)\Steam\steamapps\common\H3VR\h3vr_Data\Managed`

---

### Task 1: Scaffold, build tooling, test harness

**Files:**
- Create: `FastLoad.csproj`, `patcher/FastLoadPatcher.csproj`, `tests/FastLoad.Tests.csproj`, `tests/T.cs`, `tests/Program.cs`, `tests/SmokeTests.cs`, `tools/deploy.sh`, `tools/check_runtime.py`, `patcher/Entry.cs`, `src/Plugin.cs`
- Modify: `.gitignore`

**Interfaces:**
- Consumes: nothing.
- Produces: `T.Eq<X>(X expected, X actual, string what)`, `T.True(bool condition, string what)`, `T.Group(string name)`; each later test class is `static class XTests { public static void Run() }` and is added to `tests/Program.cs`. Commands: `dotnet run --project tests/FastLoad.Tests.csproj` (tests), `bash tools/deploy.sh --check-only` (build + runtime check, no copy).

- [ ] **Step 1: Write the patcher project**

`patcher/FastLoadPatcher.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net46</TargetFramework>
    <AssemblyName>FastLoadPatcher</AssemblyName>
    <RootNamespace>FastLoad.Early</RootNamespace>
    <Nullable>disable</Nullable>
    <LangVersion>8</LangVersion>
    <Optimize>true</Optimize>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include="BepInEx">
      <HintPath>C:\Users\Hp\AppData\Roaming\com.kesomannen.gale\h3vr\profiles\new2\BepInEx\core\BepInEx.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="0Harmony">
      <HintPath>C:\Users\Hp\AppData\Roaming\com.kesomannen.gale\h3vr\profiles\new2\BepInEx\core\0Harmony.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="Mono.Cecil">
      <HintPath>C:\Users\Hp\AppData\Roaming\com.kesomannen.gale\h3vr\profiles\new2\BepInEx\core\Mono.Cecil.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="UnityEngine">
      <HintPath>C:\Program Files (x86)\Steam\steamapps\common\H3VR\h3vr_Data\Managed\UnityEngine.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Write the plugin project**

`FastLoad.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net46</TargetFramework>
    <AssemblyName>FastLoad</AssemblyName>
    <RootNamespace>FastLoad</RootNamespace>
    <Nullable>disable</Nullable>
    <LangVersion>8</LangVersion>
    <Optimize>true</Optimize>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <!-- SDK projects glob **/*.cs: keep the patcher and the tests out of the plugin. -->
    <DefaultItemExcludes>$(DefaultItemExcludes);patcher\**;tests\**;docs\**;tools\**</DefaultItemExcludes>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="patcher\FastLoadPatcher.csproj">
      <Private>false</Private>
    </ProjectReference>
    <Reference Include="BepInEx">
      <HintPath>C:\Users\Hp\AppData\Roaming\com.kesomannen.gale\h3vr\profiles\new2\BepInEx\core\BepInEx.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="UnityEngine">
      <HintPath>C:\Program Files (x86)\Steam\steamapps\common\H3VR\h3vr_Data\Managed\UnityEngine.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Write the two entry stubs so both projects build**

`patcher/Entry.cs`:

```csharp
using System.Collections.Generic;
using Mono.Cecil;

namespace FastLoad.Early
{
    /// <summary>BepInEx preloader patcher. Patches no assembly; it only starts the probe as early as possible.</summary>
    public static class Entry
    {
        public static IEnumerable<string> TargetDLLs { get; } = new string[0];

        public static void Patch(AssemblyDefinition assembly)
        {
        }

        public static void Initialize()
        {
        }
    }
}
```

`src/Plugin.cs`:

```csharp
using BepInEx;

namespace FastLoad
{
    [BepInPlugin("ken.h3vr.fastload", "FastLoad", "0.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        private void Awake()
        {
        }
    }
}
```

- [ ] **Step 4: Write the test project and harness**

`tests/FastLoad.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <AssemblyName>FastLoad.Tests</AssemblyName>
    <RootNamespace>FastLoad.Tests</RootNamespace>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <LangVersion>8</LangVersion>
  </PropertyGroup>

  <ItemGroup>
    <!-- The logic under test: files with no Unity or BepInEx types. -->
    <Compile Include="..\patcher\Pure\*.cs" LinkBase="PatcherPure" />
    <Compile Include="..\src\Pure\*.cs" LinkBase="PluginPure" />
  </ItemGroup>

</Project>
```

`tests/T.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace FastLoad.Tests
{
    /// <summary>Tiny assert helper: counts, prints failures, never throws.</summary>
    public static class T
    {
        public static int Passed;
        public static int Failed;
        private static string s_group = "";

        public static void Group(string name)
        {
            s_group = name;
        }

        public static void Eq<X>(X expected, X actual, string what)
        {
            if (EqualityComparer<X>.Default.Equals(expected, actual))
            {
                Passed++;
                return;
            }
            Failed++;
            Console.WriteLine("FAIL " + s_group + ": " + what + "\n     expected: " + expected + "\n     actual:   " + actual);
        }

        public static void True(bool condition, string what)
        {
            if (condition)
            {
                Passed++;
                return;
            }
            Failed++;
            Console.WriteLine("FAIL " + s_group + ": " + what);
        }
    }
}
```

`tests/SmokeTests.cs`:

```csharp
namespace FastLoad.Tests
{
    public static class SmokeTests
    {
        public static void Run()
        {
            T.Group("Smoke");
            T.Eq(4, 2 + 2, "harness runs");
        }
    }
}
```

`tests/Program.cs`:

```csharp
using System;

namespace FastLoad.Tests
{
    public static class Program
    {
        public static int Main()
        {
            SmokeTests.Run();

            Console.WriteLine(T.Passed + " passed, " + T.Failed + " failed");
            return T.Failed > 0 ? 1 : 0;
        }
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `cd /c/Users/Hp/Desktop/FastLoad_Source && dotnet run --project tests/FastLoad.Tests.csproj`
Expected: last line `1 passed, 0 failed`, exit code 0.

(`tests` compiles `..\patcher\Pure\*.cs` and `..\src\Pure\*.cs`; both globs match nothing yet, which is fine.)

- [ ] **Step 6: Copy the runtime check and write the deploy script**

Run: `cp /c/Users/Hp/Desktop/ActionCam_Source/tools/check_runtime.py tools/check_runtime.py`

`tools/deploy.sh`:

```bash
#!/bin/bash
# Build, run the unit tests, check both DLLs against the game's own .NET, deploy to the Gale profile new2.
# Refuses to deploy if anything fails.   --check-only: everything except the copy.
set -e
set -o pipefail
cd "$(dirname "$0")/.."
PROFILE="/c/Users/Hp/AppData/Roaming/com.kesomannen.gale/h3vr/profiles/new2/BepInEx"

B=$(dotnet build -c Release patcher/FastLoadPatcher.csproj 2>&1; dotnet build -c Release FastLoad.csproj 2>&1) || true
echo "$B" | grep -E " error |Build succeeded" | sort -u
# Never deploy an old DLL over a failed build.
echo "$B" | grep -q "Build succeeded" || { echo "BUILD FAILED: nothing deployed"; exit 1; }
echo "$B" | grep -q " error " && { echo "BUILD FAILED: nothing deployed"; exit 1; }

dotnet run --project tests/FastLoad.Tests.csproj | tail -n 30 || { echo "TESTS FAILED: nothing deployed"; exit 1; }

T="${TMPDIR:-/tmp}"
GAME="/c/Program Files (x86)/Steam/steamapps/common/H3VR/h3vr_Data/Managed/mscorlib.dll"
[ -f "$T/game_mscorlib.il" ] || ilspycmd -il "$GAME" > "$T/game_mscorlib.il" 2>/dev/null
ilspycmd -il patcher/bin/Release/FastLoadPatcher.dll > "$T/fastloadpatcher.il" 2>/dev/null
python tools/check_runtime.py "$T/fastloadpatcher.il" "$T/game_mscorlib.il"
ilspycmd -il bin/Release/FastLoad.dll > "$T/fastload.il" 2>/dev/null
python tools/check_runtime.py "$T/fastload.il" "$T/game_mscorlib.il"

# Words that must never ship (project rule).
if grep -rniE "cursedll|slow-mo|slow motion|timescale" src patcher --include=*.cs; then
  echo "FORBIDDEN WORD in source: nothing deployed"; exit 1
fi

[ "$1" = "--check-only" ] && { echo "check only: nothing deployed"; exit 0; }

PD="$PROFILE/plugins/FastLoad"
TD="$PROFILE/patchers/FastLoad"
# The running game holds the DLLs: check both can be written before copying either.
for f in "$PD/FastLoad.dll" "$TD/FastLoadPatcher.dll"; do
  [ -f "$f" ] && { ( : >> "$f" ) 2>/dev/null || { echo "$f is in use (game running?): nothing deployed"; exit 1; }; }
done
mkdir -p "$PD" "$TD"
cp patcher/bin/Release/FastLoadPatcher.dll "$TD/"
cp bin/Release/FastLoad.dll "$PD/"
echo "deployed to new2"
```

`.gitignore` (replace content):

```
bin/
obj/
*.user
```

- [ ] **Step 7: Run the check**

Run: `bash tools/deploy.sh --check-only`
Expected output contains `Build succeeded`, `1 passed, 0 failed`, `runtime check ok` twice, `check only: nothing deployed`. No ` error ` line.

With failing tests the script stops at the `dotnet run ... | tail` line (`set -o pipefail` carries the test exit code through `tail`). That is the intended refusal.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "Scaffold patcher, plugin, test harness and deploy check" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Timeline core (clock, line format, writer, cfg peek, run files)

**Files:**
- Create: `patcher/Pure/Clock.cs`, `patcher/Pure/LineFormat.cs`, `patcher/Pure/TimelineWriter.cs`, `patcher/Pure/CfgPeek.cs`, `patcher/Pure/RunFiles.cs`
- Test: `tests/LineFormatTests.cs`, `tests/TimelineWriterTests.cs`, `tests/CfgPeekTests.cs`, `tests/RunFilesTests.cs`
- Modify: `tests/Program.cs`

**Interfaces:**
- Consumes: `T` from Task 1.
- Produces (namespace `FastLoad.Early`):
  - `static class Clock { static void Start(double msSinceProcessStart); static int NowMs { get; } static double NowMsExact { get; } }`
  - `static class LineFormat { static string Line(int ms, int frame, string kind, string text); static string Flatten(string s); static string Mb(long bytes); static string SignedMb(long bytes); static string N0(double v); static string N1(double v); static string Sec(int ms); }`
  - `sealed class TimelineWriter { volatile int Frame; bool Open(string path, out string error); void Write(string kind, string text); void Close(); }`
  - `static class CfgPeek { static string Value(string cfgText, string section, string key); static bool Bool(string cfgText, string section, string key, bool fallback); static int Int(string cfgText, string section, string key, int fallback); }`
  - `static class RunFiles { const string StartedMark = "Chainloader started"; static string Stamp(DateTime now, int pid); static string RunPath(string dir, string stamp); static string ReportPath(string dir, string stamp); static bool IsStub(string runPath); static List<string> Prune(string dir, string currentStamp, int keep); }`

- [ ] **Step 1: Write the failing tests**

`tests/LineFormatTests.cs`:

```csharp
using System.Globalization;
using System.Threading;
using FastLoad.Early;

namespace FastLoad.Tests
{
    public static class LineFormatTests
    {
        public static void Run()
        {
            T.Group("LineFormat");

            T.Eq("   48213 f=1432 LOG  hello", LineFormat.Line(48213, 1432, "LOG", "hello"), "line with frame");
            T.Eq("      12 f=- RUN  x", LineFormat.Line(12, -1, "RUN", "x"), "no frame yet prints -");

            // FlattenKeepsOneLine
            string flat = LineFormat.Flatten("first\r\n  at A.B ()\n  at C.D ()\rend");
            T.True(flat.IndexOf('\n') < 0 && flat.IndexOf('\r') < 0, "flatten removes every line break");
            T.True(flat.Contains("first") && flat.Contains("at A.B ()") && flat.Contains("at C.D ()") && flat.Contains("end"), "flatten keeps the text");
            T.Eq("", LineFormat.Flatten(null), "flatten null is empty");

            T.Eq("412MB", LineFormat.Mb(412L * 1048576L + 5), "Mb");
            T.Eq("+388MB", LineFormat.SignedMb(388L * 1048576L), "SignedMb plus");
            T.Eq("-12MB", LineFormat.SignedMb(-12L * 1048576L), "SignedMb minus");
            T.Eq("+0MB", LineFormat.SignedMb(0), "SignedMb zero");
            T.Eq("12.3 s", LineFormat.Sec(12345), "Sec");
            T.Eq("not seen", LineFormat.Sec(-1), "Sec of a mark that never came");

            // NumbersIgnoreLocale
            CultureInfo before = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                T.Eq("187", LineFormat.N0(186.6), "N0 rounds");
                T.Eq("18.5", LineFormat.N1(18.46), "N1 uses a dot in a comma locale");
                T.Eq("12.3 s", LineFormat.Sec(12345), "Sec uses a dot in a comma locale");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = before;
            }
        }
    }
}
```

`tests/TimelineWriterTests.cs`:

```csharp
using System.IO;
using System.Text.RegularExpressions;
using FastLoad.Early;

namespace FastLoad.Tests
{
    public static class TimelineWriterTests
    {
        public static void Run()
        {
            T.Group("TimelineWriter");
            Clock.Start(0);

            string dir = Path.Combine(Path.GetTempPath(), "fastload_tw_" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "run_test.log");
            try
            {
                TimelineWriter w = new TimelineWriter();
                string error;
                T.True(w.Open(path, out error), "opens: " + error);

                w.Write("LOG", "one");
                // Flush per line: readable while the writer still holds the file (a crash loses nothing written).
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader r = new StreamReader(fs))
                {
                    T.True(r.ReadToEnd().Contains("LOG  one"), "line is on disk before Close");
                }

                // MultiLineTextIsOneLine
                w.Frame = 7;
                w.Write("LOG", "boom\n  at X.Y ()\n  at Z.W ()");
                w.Close();
                w.Write("LOG", "after close is ignored");

                string[] lines = File.ReadAllLines(path);
                T.Eq(2, lines.Length, "two events are two lines");
                T.True(Regex.IsMatch(lines[0], @"^\s*\d+ f=- LOG  one$"), "first line format: " + lines[0]);
                T.True(Regex.IsMatch(lines[1], @"^\s*\d+ f=7 LOG  boom"), "second line format: " + lines[1]);

                TimelineWriter bad = new TimelineWriter();
                T.True(!bad.Open(Path.Combine(dir, "no_such_folder", "x.log"), out error), "bad path returns false");
                T.True(error != null && error.Length > 0, "bad path gives an error text");
                bad.Write("LOG", "ignored, must not throw");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
```

`tests/CfgPeekTests.cs`:

```csharp
using FastLoad.Early;

namespace FastLoad.Tests
{
    public static class CfgPeekTests
    {
        public static void Run()
        {
            T.Group("CfgPeek");
            string cfg =
                "## Settings file was created by plugin FastLoad v0.1.0\r\n" +
                "## Plugin GUID: ken.h3vr.fastload\r\n\r\n" +
                "[Other]\r\n\r\nEnabled = true\r\n\r\n" +
                "[Probe]\r\n\r\n" +
                "## Turns the whole probe off.\r\n# Setting type: Boolean\r\n# Default value: true\r\n" +
                "Enabled = false\r\n\r\n" +
                "HitchMs = 40\r\nKeepRuns = banana\r\n";

            T.Eq("false", CfgPeek.Value(cfg, "Probe", "Enabled"), "value from the right section");
            T.Eq(false, CfgPeek.Bool(cfg, "Probe", "Enabled", true), "bool read");
            T.Eq(true, CfgPeek.Bool(cfg, "Other", "Enabled", false), "same key, other section");
            T.Eq(40, CfgPeek.Int(cfg, "Probe", "HitchMs", 25), "int read");
            T.Eq(6, CfgPeek.Int(cfg, "Probe", "KeepRuns", 6), "garbled int falls back");
            T.Eq(true, CfgPeek.Bool(cfg, "Probe", "NativeHooks", true), "missing key falls back");
            T.Eq(true, CfgPeek.Bool("", "Probe", "Enabled", true), "empty file falls back");
            T.Eq(true, CfgPeek.Bool(null, "Probe", "Enabled", true), "no file falls back");
            T.Eq((string)null, CfgPeek.Value(cfg, "Nope", "Enabled"), "missing section is null");
        }
    }
}
```

`tests/RunFilesTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using FastLoad.Early;

namespace FastLoad.Tests
{
    public static class RunFilesTests
    {
        private static void Make(string dir, string stamp, bool started)
        {
            File.WriteAllText(RunFiles.RunPath(dir, stamp),
                "       5 f=- RUN  x\n" + (started ? "     900 f=- LOG  [Message:BepInEx] Chainloader started\n" : ""));
            File.WriteAllText(RunFiles.ReportPath(dir, stamp), "report");
        }

        public static void Run()
        {
            T.Group("RunFiles");

            // StampIgnoresLocale
            CultureInfo before = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("th-TH");
                T.Eq("20261012-163005_123", RunFiles.Stamp(new DateTime(2026, 10, 12, 16, 30, 5), 123), "stamp is Gregorian in a Thai locale");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = before;
            }

            string dir = Path.Combine(Path.GetTempPath(), "fastload_rf_" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try
            {
                T.True(RunFiles.RunPath(dir, "S").EndsWith("run_S.log"), "run path");
                T.True(RunFiles.ReportPath(dir, "S").EndsWith("report_S.txt"), "report path");

                // StubIsDeleted
                Make(dir, "20261012-100000_1", true);
                Make(dir, "20261012-100100_2", false);
                Make(dir, "20261012-100200_3", true);
                T.True(!RunFiles.IsStub(RunFiles.RunPath(dir, "20261012-100000_1")), "started run is not a stub");
                T.True(RunFiles.IsStub(RunFiles.RunPath(dir, "20261012-100100_2")), "run with no chainloader line is a stub");

                List<string> gone = RunFiles.Prune(dir, "20261012-100200_3", 6);
                T.True(gone.Contains("20261012-100100_2"), "stub reported deleted");
                T.True(!File.Exists(RunFiles.RunPath(dir, "20261012-100100_2")), "stub run file deleted");
                T.True(!File.Exists(RunFiles.ReportPath(dir, "20261012-100100_2")), "stub report deleted");
                T.True(File.Exists(RunFiles.RunPath(dir, "20261012-100000_1")), "real run kept");

                // CurrentRunIsNeverDeleted: the current run has no chainloader line yet when Prune runs.
                Make(dir, "20261012-100300_4", false);
                RunFiles.Prune(dir, "20261012-100300_4", 6);
                T.True(File.Exists(RunFiles.RunPath(dir, "20261012-100300_4")), "current run survives although it looks like a stub");

                // Keep N counts the current run.
                Make(dir, "20261012-100400_5", true);
                Make(dir, "20261012-100500_6", true);
                Make(dir, "20261012-100600_7", true);
                RunFiles.Prune(dir, "20261012-100600_7", 3);
                T.True(File.Exists(RunFiles.RunPath(dir, "20261012-100600_7")), "keep 3: current kept");
                T.True(File.Exists(RunFiles.RunPath(dir, "20261012-100500_6")), "keep 3: newest older kept");
                T.True(File.Exists(RunFiles.RunPath(dir, "20261012-100400_5")), "keep 3: second newest older kept");
                T.True(!File.Exists(RunFiles.RunPath(dir, "20261012-100200_3")), "keep 3: older run deleted");
                T.True(!File.Exists(RunFiles.ReportPath(dir, "20261012-100000_1")), "keep 3: oldest report deleted");

                RunFiles.Prune(dir, "20261012-100600_7", 0);
                T.True(File.Exists(RunFiles.RunPath(dir, "20261012-100600_7")), "keep 0 still keeps the current run");

                // LockedFileDoesNotThrow: the handed-over start may still hold its file.
                Make(dir, "20261012-100700_8", false);
                using (new FileStream(RunFiles.RunPath(dir, "20261012-100700_8"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    bool threw = false;
                    try { RunFiles.Prune(dir, "20261012-100800_9", 6); }
                    catch (Exception) { threw = true; }
                    T.True(!threw, "pruning a file another process holds does not throw");
                }

                bool threwMissing = false;
                try { RunFiles.Prune(Path.Combine(dir, "nope"), "x", 6); }
                catch (Exception) { threwMissing = true; }
                T.True(!threwMissing, "pruning a missing folder does not throw");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
```

In `tests/Program.cs`, add after `SmokeTests.Run();`:

```csharp
            LineFormatTests.Run();
            TimelineWriterTests.Run();
            CfgPeekTests.Run();
            RunFilesTests.Run();
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet run --project tests/FastLoad.Tests.csproj`
Expected: build error `The type or namespace name 'Early' does not exist in the namespace 'FastLoad'` (nothing implemented yet).

- [ ] **Step 3: Implement**

`patcher/Pure/Clock.cs`:

```csharp
using System.Diagnostics;

namespace FastLoad.Early
{
    /// <summary>The run clock: real milliseconds since the game process started. Never game time.</summary>
    public static class Clock
    {
        private static readonly Stopwatch s_watch = new Stopwatch();
        private static double s_offsetMs;

        /// <param name="msSinceProcessStart">How long the process had already been running when this is called.</param>
        public static void Start(double msSinceProcessStart)
        {
            s_offsetMs = msSinceProcessStart;
            s_watch.Reset();
            s_watch.Start();
        }

        public static double NowMsExact
        {
            get { return s_offsetMs + s_watch.Elapsed.TotalMilliseconds; }
        }

        public static int NowMs
        {
            get { return (int)NowMsExact; }
        }
    }
}
```

`patcher/Pure/LineFormat.cs`:

```csharp
using System;
using System.Globalization;

namespace FastLoad.Early
{
    /// <summary>How timeline lines and numbers are written. Always the same on every PC locale.</summary>
    public static class LineFormat
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>"   48213 f=1432 LOG  text": ms since process start, Unity frame ("-" before the first), kind, text.</summary>
        public static string Line(int ms, int frame, string kind, string text)
        {
            return ms.ToString(Inv).PadLeft(8) + " f=" + (frame < 0 ? "-" : frame.ToString(Inv)) + " " + kind + "  " + text;
        }

        /// <summary>One event is one line: line breaks inside a message become " \n ".</summary>
        public static string Flatten(string s)
        {
            if (s == null) return "";
            if (s.IndexOf('\n') < 0 && s.IndexOf('\r') < 0) return s;
            return s.Replace("\r\n", " \\n ").Replace("\n", " \\n ").Replace("\r", " \\n ");
        }

        public static string Mb(long bytes)
        {
            return (bytes / 1048576L).ToString(Inv) + "MB";
        }

        public static string SignedMb(long bytes)
        {
            long mb = bytes / 1048576L;
            return (mb >= 0 ? "+" : "") + mb.ToString(Inv) + "MB";
        }

        public static string N0(double v)
        {
            return ((long)Math.Round(v)).ToString(Inv);
        }

        public static string N1(double v)
        {
            return v.ToString("0.0", Inv);
        }

        /// <summary>Milliseconds as seconds, or "not seen" for a mark that never came (negative).</summary>
        public static string Sec(int ms)
        {
            return ms < 0 ? "not seen" : (ms / 1000.0).ToString("0.0", Inv) + " s";
        }
    }
}
```

`patcher/Pure/TimelineWriter.cs`:

```csharp
using System;
using System.IO;
using System.Text;
using System.Threading;

namespace FastLoad.Early
{
    /// <summary>
    /// Appends timeline lines to one file. Safe from any thread. Every line is flushed to the operating system as it
    /// is written, so a game crash loses nothing that was already written.
    /// </summary>
    public sealed class TimelineWriter
    {
        private readonly object m_gate = new object();
        private StreamWriter m_out;

        /// <summary>Unity frame number, set by the sampler each frame; -1 until the first frame.</summary>
        public volatile int Frame = -1;

        public bool Open(string path, out string error)
        {
            error = "";
            try
            {
                FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                StreamWriter w = new StreamWriter(fs, new UTF8Encoding(false));
                w.AutoFlush = true;
                m_out = w;
                return true;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        public void Write(string kind, string text)
        {
            if (m_out == null) return;
            string line = LineFormat.Line(Clock.NowMs, Frame, kind, LineFormat.Flatten(text));
            Monitor.Enter(m_gate);
            try
            {
                if (m_out != null) m_out.WriteLine(line);
            }
            catch (Exception)
            {
                // A full disk or a closed handle must never reach the caller (it may be another mod's log call).
            }
            finally
            {
                Monitor.Exit(m_gate);
            }
        }

        public void Close()
        {
            Monitor.Enter(m_gate);
            try
            {
                if (m_out != null) m_out.Close();
            }
            catch (Exception)
            {
            }
            finally
            {
                m_out = null;
                Monitor.Exit(m_gate);
            }
        }
    }
}
```

`patcher/Pure/CfgPeek.cs`:

```csharp
using System;
using System.Globalization;

namespace FastLoad.Early
{
    /// <summary>
    /// Reads single values out of a BepInEx .cfg text. The preloader patcher runs before BepInEx's config objects
    /// exist, so it reads the plugin's file itself.
    /// </summary>
    public static class CfgPeek
    {
        /// <summary>The raw value of key under [section], or null when the file, section or key is missing.</summary>
        public static string Value(string cfgText, string section, string key)
        {
            if (cfgText == null) return null;
            string current = "";
            string[] lines = cfgText.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    current = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }
                if (!string.Equals(current, section, StringComparison.Ordinal)) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.Ordinal))
                    return line.Substring(eq + 1).Trim();
            }
            return null;
        }

        public static bool Bool(string cfgText, string section, string key, bool fallback)
        {
            string v = Value(cfgText, section, key);
            if (v == null) return fallback;
            if (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        public static int Int(string cfgText, string section, string key, int fallback)
        {
            string v = Value(cfgText, section, key);
            int n;
            if (v != null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
            return fallback;
        }
    }
}
```

`patcher/Pure/RunFiles.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace FastLoad.Early
{
    /// <summary>Names of the per-run files and which old ones to delete.</summary>
    public static class RunFiles
    {
        /// <summary>BepInEx logs this once per real start, before any plugin loads.</summary>
        public const string StartedMark = "Chainloader started";

        public static string Stamp(DateTime now, int pid)
        {
            return now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "_" + pid.ToString(CultureInfo.InvariantCulture);
        }

        public static string RunPath(string dir, string stamp)
        {
            return Path.Combine(dir, "run_" + stamp + ".log");
        }

        public static string ReportPath(string dir, string stamp)
        {
            return Path.Combine(dir, "report_" + stamp + ".txt");
        }

        /// <summary>
        /// A run file with no "Chainloader started" line: the start that handed over to a second start (restart on
        /// launch), or one that died in the preloader. Unreadable files count as not a stub (left alone).
        /// </summary>
        public static bool IsStub(string runPath)
        {
            try
            {
                using (FileStream fs = new FileStream(runPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader r = new StreamReader(fs))
                {
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        if (line.IndexOf(StartedMark, StringComparison.Ordinal) >= 0) return false;
                    }
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Deletes stubs, then the oldest runs until at most <paramref name="keep"/> remain counting the current one.
        /// The current run is never touched. Returns the stamps it deleted. Never throws.
        /// </summary>
        public static List<string> Prune(string dir, string currentStamp, int keep)
        {
            List<string> deleted = new List<string>();
            try
            {
                if (!Directory.Exists(dir)) return deleted;
                List<string> real = new List<string>();
                string[] files = Directory.GetFiles(dir, "run_*.log");
                for (int i = 0; i < files.Length; i++)
                {
                    string name = Path.GetFileNameWithoutExtension(files[i]);
                    string stamp = name.Substring("run_".Length);
                    if (string.Equals(stamp, currentStamp, StringComparison.Ordinal)) continue;
                    if (IsStub(files[i]))
                    {
                        if (Remove(dir, stamp)) deleted.Add(stamp);
                    }
                    else
                    {
                        real.Add(stamp);
                    }
                }
                real.Sort(string.CompareOrdinal);
                int allowed = Math.Max(0, keep - 1);
                for (int i = 0; i < real.Count - allowed; i++)
                {
                    if (Remove(dir, real[i])) deleted.Add(real[i]);
                }
            }
            catch (Exception)
            {
            }
            return deleted;
        }

        private static bool Remove(string dir, string stamp)
        {
            try
            {
                File.Delete(RunPath(dir, stamp));
                string report = ReportPath(dir, stamp);
                if (File.Exists(report)) File.Delete(report);
                return true;
            }
            catch (Exception)
            {
                // Still held by the start that is handing over: it goes at the next start.
                return false;
            }
        }
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet run --project tests/FastLoad.Tests.csproj`
Expected: last line `N passed, 0 failed` (N around 50), exit code 0.

- [ ] **Step 5: Check the patcher still builds for the game**

Run: `bash tools/deploy.sh --check-only`
Expected: `Build succeeded`, `runtime check ok` twice, `check only: nothing deployed`.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Add timeline core: clock, line format, writer, cfg peek, run file pruning" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: Phases and per-frame log sources

**Files:**
- Create: `patcher/Pure/Phases.cs`, `patcher/Pure/FrameSources.cs`
- Test: `tests/PhasesTests.cs`
- Modify: `tests/Program.cs`

**Interfaces:**
- Consumes: `T`.
- Produces (namespace `FastLoad.Early`):
  - `enum Phase { ProcessStart = 0, PreloaderDone = 1, ChainloaderStart = 2, ChainloaderDone = 3, StratumDone = 4, OtherLoaderDone = 5, MenuLoaded = 6, Quiet = 7 }`
  - `static class Phases { const int Count = 8; static readonly string[] Names; static int LoaderBundleLines; static void Reset(); static int At(Phase p); static bool Mark(Phase p, int ms); static bool SeeLog(string source, string text, int ms); static bool SeeScene(string sceneName, int ms); static int[] Snapshot(); }` — `At` returns -1 for a phase not seen; `Mark`/`SeeLog`/`SeeScene` return true only when a phase was newly set.
  - `static class FrameSources { static void Add(string source); static void Reset(); static string TakeAndReset(); }`

- [ ] **Step 1: Write the failing test**

`tests/PhasesTests.cs` (the log lines are copied from the real `new2` boot log of 2026-10-09):

```csharp
using FastLoad.Early;

namespace FastLoad.Tests
{
    public static class PhasesTests
    {
        public static void Run()
        {
            T.Group("Phases");
            Phases.Reset();

            T.Eq(0, Phases.At(Phase.ProcessStart), "process start is always 0");
            T.Eq(-1, Phases.At(Phase.MenuLoaded), "unseen phase is -1");

            T.True(!Phases.SeeLog("BepInEx", "Preloader started", 700), "unrelated line marks nothing");
            T.True(Phases.SeeLog("BepInEx", "Preloader finished", 4100), "preloader finished");
            T.True(Phases.SeeLog("BepInEx", "Chainloader started", 4300), "chainloader started");
            // BepInEx replays the preloader's lines to every listener when the chainloader starts.
            T.True(!Phases.SeeLog("BepInEx", "Preloader finished", 4350), "replayed line does not move the mark");
            T.Eq(4100, Phases.At(Phase.PreloaderDone), "first time wins");

            T.True(!Phases.SeeLog("SomeMod", "Chainloader started", 5000), "wrong source is ignored");
            T.True(Phases.SeeLog("BepInEx", "Chainloader startup complete", 31000), "chainloader done");
            T.True(Phases.SeeLog("Stratum", "Loading complete (50.341s)", 82000), "stratum done");
            T.True(Phases.SeeLog("OtherLoaderPatched", "All items loaded, took 69.703 seconds!", 101000), "otherloader done");

            T.True(!Phases.SeeScene("TakeAndHold_Lobby_2", 90000), "another scene is not the menu");
            T.True(Phases.SeeScene("MainMenu3", 95000), "menu scene");
            T.True(!Phases.SeeScene("MainMenu3", 400000), "coming back to the menu does not move the mark");
            T.Eq(95000, Phases.At(Phase.MenuLoaded), "menu mark");

            T.True(Phases.Mark(Phase.Quiet, 130000), "quiet set from outside");
            T.True(!Phases.Mark(Phase.Quiet, 140000), "quiet set once");

            Phases.SeeLog("OtherLoaderPatched", "Completed loading bundle 'shotclock' in 19.131 seconds!", 60000);
            Phases.SeeLog("ModulWorkshop_Optimizer", "[BundleLoader] On-demand async loading modular bundle: C:\\x\\mw_ar2_plus", 200000);
            Phases.SeeLog("OtherLoaderPatched", "Spawning item by ItemSpawnerID 'AttIronBackup1'", 200100);
            T.Eq(2, Phases.LoaderBundleLines, "loader bundle lines counted");

            int[] snap = Phases.Snapshot();
            T.Eq(Phases.Count, snap.Length, "snapshot length");
            T.Eq(82000, snap[(int)Phase.StratumDone], "snapshot value");
            T.Eq(Phases.Count, Phases.Names.Length, "a name for every phase");

            Phases.SeeLog(null, null, 1);
            T.True(true, "null source and text do not throw");

            Phases.Reset();
            T.Eq(-1, Phases.At(Phase.StratumDone), "reset clears marks");
            T.Eq(0, Phases.LoaderBundleLines, "reset clears the count");

            T.Group("FrameSources");
            FrameSources.Reset();
            T.Eq("", FrameSources.TakeAndReset(), "nothing logged");
            FrameSources.Add("OtherLoaderPatched");
            FrameSources.Add("TNHFramework");
            FrameSources.Add("OtherLoaderPatched");
            T.Eq("OtherLoaderPatched, TNHFramework", FrameSources.TakeAndReset(), "distinct sources in order");
            T.Eq("", FrameSources.TakeAndReset(), "taking resets");
            for (int i = 0; i < 20; i++) FrameSources.Add("m" + i);
            T.Eq(8, FrameSources.TakeAndReset().Split(',').Length, "at most 8 sources per frame");
            FrameSources.Add(null);
            T.Eq("?", FrameSources.TakeAndReset(), "null source is shown as ?");
        }
    }
}
```

Add `PhasesTests.Run();` to `tests/Program.cs` after `RunFilesTests.Run();`.

- [ ] **Step 2: Run to see it fail**

Run: `dotnet run --project tests/FastLoad.Tests.csproj`
Expected: build error, `The name 'Phases' does not exist in the current context`.

- [ ] **Step 3: Implement**

`patcher/Pure/Phases.cs`:

```csharp
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
            "quiet (10 s with nothing loading and no slow frame)"
        };

        private static readonly object s_gate = new object();
        private static readonly int[] s_at = NewMarks();

        /// <summary>How many bundle loads the loaders themselves reported in their log lines (cross-check for the hooks).</summary>
        public static int LoaderBundleLines;

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
                if (text.IndexOf("On-demand async loading modular bundle", StringComparison.Ordinal) >= 0
                    || text.IndexOf("On-demand loading modular bundle", StringComparison.Ordinal) >= 0)
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
```

`patcher/Pure/FrameSources.cs`:

```csharp
using System.Collections.Generic;
using System.Threading;

namespace FastLoad.Early
{
    /// <summary>
    /// Which log sources wrote something since the last frame. The sampler reads it for a slow frame ("what was
    /// talking while this frame hung") and clears it every frame. At most 8 names; no allocation when nothing changed.
    /// </summary>
    public static class FrameSources
    {
        private const int Max = 8;
        private static readonly object s_gate = new object();
        private static readonly List<string> s_names = new List<string>(Max);

        public static void Add(string source)
        {
            if (source == null) source = "?";
            Monitor.Enter(s_gate);
            try
            {
                if (s_names.Count < Max && !s_names.Contains(source)) s_names.Add(source);
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        public static void Reset()
        {
            Monitor.Enter(s_gate);
            try
            {
                if (s_names.Count > 0) s_names.Clear();
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        public static string TakeAndReset()
        {
            Monitor.Enter(s_gate);
            try
            {
                if (s_names.Count == 0) return "";
                string joined = string.Join(", ", s_names.ToArray());
                s_names.Clear();
                return joined;
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet run --project tests/FastLoad.Tests.csproj`
Expected: `N passed, 0 failed`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add phase marks and per-frame log source tracking" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: Bundle ledger and memory reader

**Files:**
- Create: `patcher/Pure/BundleLedger.cs`, `patcher/Pure/Memory.cs`
- Test: `tests/BundleLedgerTests.cs`
- Modify: `tests/Program.cs`

**Interfaces:**
- Consumes: `T`.
- Produces (namespace `FastLoad.Early`):
  - `sealed class BundleRecord { string Path, Name, Mod, By; long Size; bool Async; int StartMs; int EndMs = -1; double MainMs; long CommitStart, CommitEnd; bool Ok; int AssetCalls; int LastUseMs = -1; int UnloadMs = -1; int WallMs { get; } long CommitGrowth { get; } bool Done { get; } BundleRecord Copy(); }`
  - `static class BundleLedger { static BundleRecord Begin(string path, long size, string by, bool isAsync, int nowMs, long commit); static void End(BundleRecord r, int bundleId, bool ok, int nowMs, long commit, double mainMs); static BundleRecord Use(int bundleId, int nowMs); static BundleRecord Adopt(int bundleId, string name, int nowMs); static void Unloaded(BundleRecord r, int nowMs); static int InFlight { get; } static int LoadedCount { get; } static string InFlightNames(int max); static List<BundleRecord> Snapshot(); static void Reset(); static string ModOf(string path); static string NameOf(string path); }`
  - `static class Memory { static long Commit(); static bool Read(out long commit, out long workingSet, out long sysCommitFree, out long sysCommitLimit); }` — all in bytes; `Commit()` returns 0 if the call fails.

- [ ] **Step 1: Write the failing test**

`tests/BundleLedgerTests.cs`:

```csharp
using System.Collections.Generic;
using FastLoad.Early;

namespace FastLoad.Tests
{
    public static class BundleLedgerTests
    {
        private const long MB = 1048576L;

        public static void Run()
        {
            T.Group("BundleLedger");
            BundleLedger.Reset();

            string p = @"C:\Users\Hp\AppData\Roaming\com.kesomannen.gale\h3vr\profiles\new2\BepInEx\plugins\Not_Wolfie-AR2_Plus\ar2plus-main";
            T.Eq("Not_Wolfie-AR2_Plus", BundleLedger.ModOf(p), "mod folder from a plugins path");
            T.Eq("Not_Wolfie-AR2_Plus", BundleLedger.ModOf(p.Replace('\\', '/')), "forward slashes too");
            T.Eq("Meats", BundleLedger.ModOf(@"D:\x\PLUGINS\Meats\sub\folder\bundle"), "case does not matter, nested folders");
            T.Eq("(memory)", BundleLedger.ModOf(null), "no path = loaded from memory");
            T.Eq("(game)", BundleLedger.ModOf(@"C:\Program Files (x86)\Steam\steamapps\common\H3VR\h3vr_Data\StreamingAssets\assets_x"), "not under plugins");
            T.Eq("ar2plus-main", BundleLedger.NameOf(p), "file name");
            T.Eq("(memory)", BundleLedger.NameOf(null), "name with no path");

            // A normal async load.
            BundleRecord a = BundleLedger.Begin(p, 412 * MB, "OtherLoader", true, 1000, 5000 * MB);
            T.Eq(1, BundleLedger.InFlight, "one in flight");
            T.Eq("ar2plus-main", BundleLedger.InFlightNames(4), "in-flight names");
            T.Eq(-1, a.WallMs, "wall unknown while loading");
            T.True(!a.Done, "not done");
            BundleLedger.End(a, 101, true, 19034, 5388 * MB, 212.5);
            T.Eq(0, BundleLedger.InFlight, "none in flight");
            T.Eq(1, BundleLedger.LoadedCount, "one loaded");
            T.Eq(18034, a.WallMs, "wall time");
            T.Eq(388 * MB, a.CommitGrowth, "commit growth");
            T.Eq("Not_Wolfie-AR2_Plus", a.Mod, "record mod");
            T.Eq("OtherLoader", a.By, "record caller");
            T.True(a.Done && a.Ok, "done and ok");

            // Use.
            T.Eq(0, a.AssetCalls, "no asset calls yet");
            T.True(ReferenceEquals(a, BundleLedger.Use(101, 20000)), "use finds the record by bundle id");
            BundleLedger.Use(101, 20500);
            T.Eq(2, a.AssetCalls, "asset calls counted");
            T.Eq(20500, a.LastUseMs, "last use");

            // FailedLoadIsClosed: the engine returned nothing (bundle already loaded, bad file).
            BundleRecord f = BundleLedger.Begin(@"C:\x\plugins\ModB\broken", 10 * MB, "Stratum", false, 21000, 5388 * MB);
            BundleLedger.End(f, 0, false, 21050, 5388 * MB, 50);
            T.Eq(0, BundleLedger.InFlight, "failed load is not left in flight");
            T.Eq(1, BundleLedger.LoadedCount, "failed load is not counted as loaded");
            T.True(f.Done && !f.Ok, "failed record is done, not ok");
            T.True(BundleLedger.Use(0, 21060) == null, "id 0 never matches a record");

            // UnknownBundleIsAdopted: an asset call on a bundle the hooks never saw load.
            T.True(BundleLedger.Use(999, 22000) == null, "unknown id is not found");
            BundleRecord u = BundleLedger.Adopt(999, "mystery_bundle", 22000);
            T.Eq("mystery_bundle", u.Name, "adopted name");
            T.Eq("(not seen loading)", u.Mod, "adopted mod");
            T.Eq(1, u.AssetCalls, "adopting counts the call");
            T.True(ReferenceEquals(u, BundleLedger.Use(999, 22100)), "adopted record is found next time");
            T.Eq(2, BundleLedger.LoadedCount, "adopted counts as loaded");

            // Unload.
            BundleLedger.Unloaded(a, 30000);
            T.Eq(30000, a.UnloadMs, "unload time");
            T.Eq(1, BundleLedger.LoadedCount, "unloaded leaves the loaded count");
            BundleLedger.Unloaded(a, 31000);
            T.Eq(30000, a.UnloadMs, "second unload does not move the time");
            T.True(BundleLedger.Use(101, 32000) == null, "an unloaded bundle's id is forgotten");

            // End twice / null are ignored.
            BundleLedger.End(a, 101, true, 40000, 1, 1);
            T.Eq(18034, a.WallMs, "ending twice changes nothing");
            BundleLedger.End(null, 5, true, 1, 1, 1);
            BundleLedger.Unloaded(null, 1);

            // Snapshot is a copy.
            List<BundleRecord> snap = BundleLedger.Snapshot();
            T.Eq(3, snap.Count, "snapshot has every record");
            snap[0].AssetCalls = 77;
            T.Eq(2, a.AssetCalls, "changing a snapshot does not change the ledger");

            // In-flight name list is cut.
            BundleLedger.Reset();
            for (int i = 0; i < 6; i++) BundleLedger.Begin(@"C:\x\plugins\M\b" + i, 1, "L", true, i, 0);
            T.Eq("b0, b1, b2 (+3)", BundleLedger.InFlightNames(3), "in-flight names are cut with a count");
            BundleLedger.Reset();
            T.Eq(0, BundleLedger.InFlight, "reset clears in flight");
            T.Eq("", BundleLedger.InFlightNames(3), "no names when nothing loads");

            T.Group("Memory");
            long commit, ws, free, limit;
            T.True(Memory.Read(out commit, out ws, out free, out limit), "memory read works on this PC");
            T.True(commit > MB && ws > MB, "process numbers are plausible");
            T.True(limit > free && free > 0, "system commit limit is above what is free");
            T.True(Memory.Commit() > MB, "Commit() alone");
        }
    }
}
```

Add `BundleLedgerTests.Run();` to `tests/Program.cs` after `PhasesTests.Run();`.

- [ ] **Step 2: Run to see it fail**

Run: `dotnet run --project tests/FastLoad.Tests.csproj`
Expected: build error, `The name 'BundleLedger' does not exist in the current context`.

- [ ] **Step 3: Implement**

`patcher/Pure/BundleLedger.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;

namespace FastLoad.Early
{
    /// <summary>Everything known about one asset bundle load.</summary>
    public sealed class BundleRecord
    {
        public string Path;
        public string Name;
        /// <summary>Folder under BepInEx/plugins the file sits in.</summary>
        public string Mod;
        /// <summary>Assembly that asked for the load (usually a loader mod, not the content mod).</summary>
        public string By;
        public long Size;
        public bool Async;
        public int StartMs;
        public int EndMs = -1;
        /// <summary>Milliseconds the main thread spent inside the load call itself.</summary>
        public double MainMs;
        public long CommitStart;
        public long CommitEnd;
        public bool Ok;
        public int AssetCalls;
        public int LastUseMs = -1;
        public int UnloadMs = -1;

        public bool Done
        {
            get { return EndMs >= 0; }
        }

        /// <summary>Start to end in ms; -1 while still loading.</summary>
        public int WallMs
        {
            get { return EndMs < 0 ? -1 : EndMs - StartMs; }
        }

        /// <summary>Process commit growth while this bundle was loading. Loads that overlap share the same growth.</summary>
        public long CommitGrowth
        {
            get { return EndMs < 0 ? 0 : CommitEnd - CommitStart; }
        }

        public BundleRecord Copy()
        {
            return (BundleRecord)MemberwiseClone();
        }
    }

    /// <summary>The table of every bundle load seen this run. Safe from any thread.</summary>
    public static class BundleLedger
    {
        private static readonly object s_gate = new object();
        private static readonly List<BundleRecord> s_all = new List<BundleRecord>(1024);
        private static readonly Dictionary<int, BundleRecord> s_byId = new Dictionary<int, BundleRecord>();
        private static readonly List<BundleRecord> s_flying = new List<BundleRecord>();
        private static int s_loaded;

        public static int InFlight
        {
            get { return s_flying.Count; }
        }

        public static int LoadedCount
        {
            get { return s_loaded; }
        }

        public static void Reset()
        {
            Monitor.Enter(s_gate);
            try
            {
                s_all.Clear();
                s_byId.Clear();
                s_flying.Clear();
                s_loaded = 0;
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        public static BundleRecord Begin(string path, long size, string by, bool isAsync, int nowMs, long commit)
        {
            BundleRecord r = new BundleRecord();
            r.Path = path;
            r.Name = NameOf(path);
            r.Mod = ModOf(path);
            r.By = by ?? "?";
            r.Size = size;
            r.Async = isAsync;
            r.StartMs = nowMs;
            r.CommitStart = commit;
            Monitor.Enter(s_gate);
            try
            {
                s_all.Add(r);
                s_flying.Add(r);
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
            return r;
        }

        /// <param name="bundleId">Unity instance id of the loaded bundle; 0 when the load gave nothing.</param>
        public static void End(BundleRecord r, int bundleId, bool ok, int nowMs, long commit, double mainMs)
        {
            if (r == null) return;
            Monitor.Enter(s_gate);
            try
            {
                if (r.EndMs >= 0) return;
                r.EndMs = nowMs;
                r.CommitEnd = commit;
                r.MainMs += mainMs;
                r.Ok = ok;
                s_flying.Remove(r);
                if (ok)
                {
                    s_loaded++;
                    if (bundleId != 0) s_byId[bundleId] = r;
                }
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        /// <summary>Counts one asset call on a known bundle. Null when the id is not known.</summary>
        public static BundleRecord Use(int bundleId, int nowMs)
        {
            if (bundleId == 0) return null;
            Monitor.Enter(s_gate);
            try
            {
                BundleRecord r;
                if (!s_byId.TryGetValue(bundleId, out r)) return null;
                r.AssetCalls++;
                r.LastUseMs = nowMs;
                return r;
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        /// <summary>Makes a record for a bundle that was loaded where the hooks could not see, and counts this call.</summary>
        public static BundleRecord Adopt(int bundleId, string name, int nowMs)
        {
            BundleRecord r = new BundleRecord();
            r.Name = name ?? "?";
            r.Mod = "(not seen loading)";
            r.By = "?";
            r.StartMs = nowMs;
            r.EndMs = nowMs;
            r.Ok = true;
            r.AssetCalls = 1;
            r.LastUseMs = nowMs;
            Monitor.Enter(s_gate);
            try
            {
                s_all.Add(r);
                s_loaded++;
                if (bundleId != 0) s_byId[bundleId] = r;
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
            return r;
        }

        public static void Unloaded(BundleRecord r, int nowMs)
        {
            if (r == null) return;
            Monitor.Enter(s_gate);
            try
            {
                if (r.UnloadMs >= 0) return;
                r.UnloadMs = nowMs;
                if (r.Ok) s_loaded--;
                int key = 0;
                foreach (KeyValuePair<int, BundleRecord> kv in s_byId)
                {
                    if (ReferenceEquals(kv.Value, r))
                    {
                        key = kv.Key;
                        break;
                    }
                }
                if (key != 0) s_byId.Remove(key);
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        /// <summary>"a, b, c (+3)": names of bundles loading right now, at most <paramref name="max"/>.</summary>
        public static string InFlightNames(int max)
        {
            Monitor.Enter(s_gate);
            try
            {
                int n = s_flying.Count;
                if (n == 0) return "";
                int show = Math.Min(n, max);
                string[] names = new string[show];
                for (int i = 0; i < show; i++) names[i] = s_flying[i].Name;
                string joined = string.Join(", ", names);
                return n > show ? joined + " (+" + (n - show) + ")" : joined;
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        /// <summary>Copies of every record, in the order the loads began.</summary>
        public static List<BundleRecord> Snapshot()
        {
            Monitor.Enter(s_gate);
            try
            {
                List<BundleRecord> copy = new List<BundleRecord>(s_all.Count);
                for (int i = 0; i < s_all.Count; i++) copy.Add(s_all[i].Copy());
                return copy;
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        /// <summary>The folder right under ".../plugins/" in the path; "(memory)" with no path; "(game)" elsewhere.</summary>
        public static string ModOf(string path)
        {
            if (path == null) return "(memory)";
            string p = path.Replace('/', '\\');
            int at = p.IndexOf("\\plugins\\", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return "(game)";
            int from = at + "\\plugins\\".Length;
            int to = p.IndexOf('\\', from);
            if (to < 0) return "(plugins root)";
            return p.Substring(from, to - from);
        }

        public static string NameOf(string path)
        {
            if (path == null) return "(memory)";
            string p = path.Replace('/', '\\');
            int at = p.LastIndexOf('\\');
            return at < 0 ? p : p.Substring(at + 1);
        }
    }
}
```

`patcher/Pure/Memory.cs`:

```csharp
using System;
using System.Runtime.InteropServices;

namespace FastLoad.Early
{
    /// <summary>Process and system memory numbers straight from Windows. All values in bytes.</summary>
    public static class Memory
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCountersEx
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivateUsage;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCountersEx counters, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

        private static readonly uint CountersSize = (uint)Marshal.SizeOf(typeof(ProcessMemoryCountersEx));
        private static readonly uint StatusSize = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));

        /// <summary>Private bytes this process has committed (what counts against the page file); 0 if unreadable.</summary>
        public static long Commit()
        {
            try
            {
                ProcessMemoryCountersEx c;
                if (!GetProcessMemoryInfo(GetCurrentProcess(), out c, CountersSize)) return 0;
                return (long)c.PrivateUsage.ToUInt64();
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <param name="sysCommitFree">How much more the whole system can still commit before allocations fail.</param>
        /// <param name="sysCommitLimit">RAM plus page files.</param>
        public static bool Read(out long commit, out long workingSet, out long sysCommitFree, out long sysCommitLimit)
        {
            commit = 0;
            workingSet = 0;
            sysCommitFree = 0;
            sysCommitLimit = 0;
            try
            {
                ProcessMemoryCountersEx c;
                if (!GetProcessMemoryInfo(GetCurrentProcess(), out c, CountersSize)) return false;
                commit = (long)c.PrivateUsage.ToUInt64();
                workingSet = (long)c.WorkingSetSize.ToUInt64();

                MemoryStatusEx s = new MemoryStatusEx();
                s.dwLength = StatusSize;
                if (!GlobalMemoryStatusEx(ref s)) return false;
                sysCommitFree = (long)s.ullAvailPageFile;
                sysCommitLimit = (long)s.ullTotalPageFile;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet run --project tests/FastLoad.Tests.csproj`
Expected: `N passed, 0 failed`.

- [ ] **Step 5: Check and commit**

Run: `bash tools/deploy.sh --check-only`
Expected: `runtime check ok` twice.

```bash
git add -A
git commit -m "Add bundle ledger and Windows memory reader" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Patcher glue (timeline facade, log mirror, entry)

**Files:**
- Create: `patcher/Timeline.cs`, `patcher/LogMirror.cs`, `patcher/HookGate.cs`, `patcher/BundleHooks.cs` (stub, filled in Task 6)
- Modify: `patcher/Entry.cs`

**Interfaces:**
- Consumes: everything from Tasks 2 to 4.
- Produces (namespace `FastLoad.Early`):
  - `static class Timeline { static bool Enabled; static string Dir; static string Stamp; static string Profile; static string Args; static int RestartCostMs; static bool NativeHooks; static TimelineWriter Writer; static void Write(string kind, string text); static void SetFrame(int frame); }` — `RestartCostMs` is -1 when no restart on launch happened.
  - `static class BundleHooks { static string Mode; static void Install(bool native); static void Sweep(); static void CheckUnloads(); }` (bodies in Task 6).
  - `static class HookGate { static void Go(); }`
  - Timeline kinds written here: `RUN` (once), `LOG` (every log event), `PHS` (a phase mark), `ERR`.

No unit tests in this task: these four files are glue over BepInEx and Unity types that the test project cannot load. Their logic lives in the tested `Pure/` files. They are verified by the build, the runtime check, and Ken's run in Task 10.

- [ ] **Step 1: Write the facade**

`patcher/Timeline.cs`:

```csharp
namespace FastLoad.Early
{
    /// <summary>What the plugin and the patcher share for one run.</summary>
    public static class Timeline
    {
        /// <summary>False when the probe is switched off or could not open its file: nothing else may run then.</summary>
        public static bool Enabled;
        public static string Dir = "";
        public static string Stamp = "";
        public static string Profile = "";
        public static string Args = "";
        /// <summary>Cost of the restart on launch in ms, as measured by the mod that did it; -1 = no restart.</summary>
        public static int RestartCostMs = -1;
        public static bool NativeHooks;
        public static TimelineWriter Writer;

        public static void Write(string kind, string text)
        {
            TimelineWriter w = Writer;
            if (w != null) w.Write(kind, text);
        }

        public static void SetFrame(int frame)
        {
            TimelineWriter w = Writer;
            if (w != null) w.Frame = frame;
        }
    }
}
```

- [ ] **Step 2: Write the hook stub and the gate**

`patcher/BundleHooks.cs` (stub; Task 6 replaces the whole file):

```csharp
namespace FastLoad.Early
{
    public static class BundleHooks
    {
        public static string Mode = "not installed";

        public static void Install(bool native)
        {
        }

        public static void Sweep()
        {
        }

        public static void CheckUnloads()
        {
        }
    }
}
```

`patcher/HookGate.cs`:

```csharp
using System.Runtime.CompilerServices;

namespace FastLoad.Early
{
    /// <summary>
    /// The only door from preloader code to BundleHooks. BundleHooks has fields of Unity types; a method that calls
    /// it directly would make the runtime look at those types when that method is compiled, which for the log mirror
    /// is during the preloader. This method is compiled only when it is first called: at "Chainloader started".
    /// </summary>
    public static class HookGate
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Go()
        {
            BundleHooks.Install(Timeline.NativeHooks);
        }
    }
}
```

- [ ] **Step 3: Write the log mirror**

`patcher/LogMirror.cs`:

```csharp
using System;
using BepInEx.Logging;

namespace FastLoad.Early
{
    /// <summary>
    /// Gets every BepInEx log event (all mods, Unity's own log included) and writes it to the timeline with the run
    /// clock. BepInEx keeps listeners added in the preloader for the whole run. When the chainloader starts it
    /// replays the preloader's lines to every listener, so those lines appear twice; phase marks keep the first.
    /// </summary>
    public sealed class LogMirror : ILogListener
    {
        public void LogEvent(object sender, LogEventArgs e)
        {
            try
            {
                string source = e.Source != null ? e.Source.SourceName : "?";
                string text = e.Data != null ? e.Data.ToString() : "";
                Timeline.Write("LOG", "[" + e.Level + ":" + source + "] " + text);
                FrameSources.Add(source);

                int now = Clock.NowMs;
                if (Phases.SeeLog(source, text, now))
                {
                    Timeline.Write("PHS", "mark at " + now);
                    // Plugins start loading right after this line: hook bundle loading before any of their code runs.
                    if (string.Equals(text, RunFiles.StartedMark, StringComparison.Ordinal)) HookGate.Go();
                }
            }
            catch (Exception)
            {
                // Never let the probe break another mod's log call.
            }
        }

        public void Dispose()
        {
        }
    }
}
```

- [ ] **Step 4: Write the entry**

Replace `patcher/Entry.cs`:

```csharp
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
                if (!CfgPeek.Bool(cfg, "Probe", "Enabled", true))
                {
                    log.LogInfo("Probe is off ([Probe] Enabled = false in " + ConfigFile + ")");
                    return;
                }

                Process self = Process.GetCurrentProcess();
                Clock.Start((DateTime.Now - self.StartTime).TotalMilliseconds);
                Phases.Reset();

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
```

- [ ] **Step 5: Build and check**

Run: `bash tools/deploy.sh --check-only`
Expected: `Build succeeded`, tests `0 failed`, `runtime check ok` twice.

If the runtime check prints `method missing` or `type missing`, the listed member does not exist in the game's .NET: replace that call with a .NET 2.0 equivalent (see Global Constraints) and run again. Do not deploy around it.

- [ ] **Step 6: Confirm the patcher has the shape BepInEx requires**

Run: `ilspycmd -t FastLoad.Early.Entry patcher/bin/Release/FastLoadPatcher.dll | grep -E "TargetDLLs|static void (Patch|Initialize)"`
Expected, three matches: a `public static IEnumerable<string> TargetDLLs` property, `public static void Patch(AssemblyDefinition assembly)`, `public static void Initialize()`. BepInEx 5.4.17 skips a patcher class that lacks `TargetDLLs` or `Patch`.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Add preloader entry, timeline facade and log mirror" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: Bundle hooks

**Files:**
- Modify: `patcher/BundleHooks.cs` (replace the stub)

**Interfaces:**
- Consumes: `BundleLedger`, `BundleRecord`, `Memory`, `Clock`, `Timeline`, `LineFormat`.
- Produces: `BundleHooks.Mode` (text for the report, e.g. `managed, 12 of 12 hooks on`), `BundleHooks.Install(bool native)` (does nothing the second time), `BundleHooks.Sweep()` (closes finished async loads; called every frame by the sampler and at the start of every hook), `BundleHooks.CheckUnloads()` (called once per second by the sampler). Timeline kinds: `HKS` (once), `BUN` (`load-start`, `load-end`, `asset`, `unload`), `ERR`.

No unit test: every line here touches Unity or Harmony types. The ledger it drives is tested in Task 4. Verified by build, runtime check, the decompile check in Step 1, and Task 10.

- [ ] **Step 1: Confirm which engine methods call which (decides what is safe to patch together)**

Run:

```bash
ilspycmd -t UnityEngine.AssetBundle "/c/Program Files (x86)/Steam/steamapps/common/H3VR/h3vr_Data/Managed/UnityEngine.dll" > "${TMPDIR:-/tmp}/AssetBundle.cs"
grep -n -A6 -E "public (static )?[A-Za-z\[\]<>]+ (LoadFromFile|LoadFromFileAsync|LoadFromMemory|LoadFromMemoryAsync|LoadAsset|LoadAssetAsync|LoadAssetWithSubAssets|LoadAssetWithSubAssetsAsync|LoadAllAssets|LoadAllAssetsAsync)\(" "${TMPDIR:-/tmp}/AssetBundle.cs"
```

Check these three facts in the output. Each hook list below depends on them:

1. `LoadFromFile(string)` and `LoadFromFile(string, uint)` each call the three-argument `LoadFromFile(path, crc, offset)` directly, not each other. Same for `LoadFromFileAsync`. `LoadFromMemory(byte[])` calls `LoadFromMemory(binary, 0u)`; same for the async one.
2. `LoadAsset(string, Type)`, `LoadAssetAsync(string, Type)`, `LoadAssetWithSubAssets(string, Type)`, `LoadAssetWithSubAssetsAsync(string, Type)` each call their own `*_Internal` method and none of the other five asset targets.
3. `LoadAllAssets(Type)` and `LoadAllAssetsAsync(Type)` call `LoadAssetWithSubAssets_Internal` / `LoadAssetWithSubAssetsAsync_Internal`, not the public `LoadAssetWithSubAssets(string, Type)`.

A fourth fact, not checkable from the decompile: the one-argument wrappers are tiny, and a just-in-time compiler may copy a tiny method into its callers, which would skip a patch on it. MonoMod (under HarmonyX) marks a patched method as not-inlinable, and the hooks go in before any plugin code is compiled, so callers compiled afterwards do call the patched wrapper. The report's cross-check line (hooks saw N, loaders reported M) is what proves it in Task 10.

If a patched method turns out to call another patched method, remove the outer one from the list in Step 2 (otherwise one load is counted twice), and write the change and the reason in the commit message.

- [ ] **Step 2: Write the hooks**

Replace `patcher/BundleHooks.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FastLoad.Early
{
    /// <summary>
    /// Harmony patches on UnityEngine.AssetBundle that feed the ledger. They only observe: no argument or result is
    /// ever changed. All of this runs on Unity's main thread (bundle calls are main-thread only).
    /// </summary>
    public static class BundleHooks
    {
        /// <summary>What a prefix hands to its postfix.</summary>
        public struct Call
        {
            public BundleRecord Rec;
            public long T0;
        }

        private struct Pending
        {
            public BundleRecord Rec;
            public AssetBundleCreateRequest Req;
        }

        private struct Live
        {
            public BundleRecord Rec;
            public AssetBundle Bundle;
        }

        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        /// <summary>A single asset call that holds the main thread this long gets its own timeline line.</summary>
        private const double SlowAssetMs = 5.0;

        private static readonly List<Pending> s_pending = new List<Pending>();
        private static readonly List<Live> s_live = new List<Live>();
        private static bool s_installed;
        private static bool s_loadsOff;
        private static bool s_assetsOff;

        public static string Mode = "not installed";

        public static void Install(bool native)
        {
            if (s_installed) return;
            s_installed = true;
            try
            {
                Harmony h = new Harmony("ken.h3vr.fastload.early");
                Type ab = typeof(AssetBundle);
                Type str = typeof(string);
                Type u32 = typeof(uint);
                Type u64 = typeof(ulong);
                Type bytes = typeof(byte[]);
                Type type = typeof(Type);
                int on = 0;
                int all = 0;

                if (native)
                {
                    all += 4;
                    on += Patch(h, ab, "LoadFromFile", new Type[] { str, u32, u64 }, "FilePre", "SyncPost");
                    on += Patch(h, ab, "LoadFromFileAsync", new Type[] { str, u32, u64 }, "FileAsyncPre", "AsyncPost");
                    on += Patch(h, ab, "LoadFromMemory", new Type[] { bytes, u32 }, "MemPre", "SyncPost");
                    on += Patch(h, ab, "LoadFromMemoryAsync", new Type[] { bytes, u32 }, "MemAsyncPre", "AsyncPost");
                }
                else
                {
                    all += 6;
                    on += Patch(h, ab, "LoadFromFile", new Type[] { str }, "FilePre", "SyncPost");
                    on += Patch(h, ab, "LoadFromFile", new Type[] { str, u32 }, "FilePre", "SyncPost");
                    on += Patch(h, ab, "LoadFromFileAsync", new Type[] { str }, "FileAsyncPre", "AsyncPost");
                    on += Patch(h, ab, "LoadFromFileAsync", new Type[] { str, u32 }, "FileAsyncPre", "AsyncPost");
                    on += Patch(h, ab, "LoadFromMemory", new Type[] { bytes }, "MemPre", "SyncPost");
                    on += Patch(h, ab, "LoadFromMemoryAsync", new Type[] { bytes }, "MemAsyncPre", "AsyncPost");
                }

                all += 6;
                on += Patch(h, ab, "LoadAsset", new Type[] { str, type }, "AssetPre", "NamedAssetPost");
                on += Patch(h, ab, "LoadAssetAsync", new Type[] { str, type }, "AssetPre", "NamedAssetPost");
                on += Patch(h, ab, "LoadAssetWithSubAssets", new Type[] { str, type }, "AssetPre", "NamedAssetPost");
                on += Patch(h, ab, "LoadAssetWithSubAssetsAsync", new Type[] { str, type }, "AssetPre", "NamedAssetPost");
                on += Patch(h, ab, "LoadAllAssets", new Type[] { type }, "AssetPre", "AllAssetsPost");
                on += Patch(h, ab, "LoadAllAssetsAsync", new Type[] { type }, "AssetPre", "AllAssetsPost");

                Mode = (native ? "native" : "managed") + ", " + on + " of " + all + " hooks on";
            }
            catch (Exception e)
            {
                Mode = "failed: " + e.GetType().Name + ": " + e.Message;
            }
            Timeline.Write("HKS", Mode);
        }

        private static int Patch(Harmony h, Type target, string name, Type[] args, string prefix, string postfix)
        {
            try
            {
                MethodInfo m = target.GetMethod(name, Any, null, args, null);
                if (m == null)
                {
                    Timeline.Write("ERR", "hook target not found: AssetBundle." + name + "/" + args.Length);
                    return 0;
                }
                Type me = typeof(BundleHooks);
                h.Patch(m, new HarmonyMethod(me.GetMethod(prefix, Any)), new HarmonyMethod(me.GetMethod(postfix, Any)));
                return 1;
            }
            catch (Exception e)
            {
                Timeline.Write("ERR", "hook not applied: AssetBundle." + name + "/" + args.Length + " (" + e.GetType().Name + ": " + e.Message + ")");
                return 0;
            }
        }

        // ---- bundle loads ------------------------------------------------------------------------------------

        private static void FilePre(string path, out Call __state)
        {
            __state = Open(path, -1, false);
        }

        private static void FileAsyncPre(string path, out Call __state)
        {
            __state = Open(path, -1, true);
        }

        private static void MemPre(byte[] binary, out Call __state)
        {
            __state = Open(null, binary != null ? binary.Length : 0, false);
        }

        private static void MemAsyncPre(byte[] binary, out Call __state)
        {
            __state = Open(null, binary != null ? binary.Length : 0, true);
        }

        private static Call Open(string path, long memorySize, bool isAsync)
        {
            Call c = new Call();
            if (s_loadsOff) return c;
            try
            {
                Sweep();
                long size = memorySize;
                if (path != null)
                {
                    size = 0;
                    try
                    {
                        FileInfo fi = new FileInfo(path);
                        if (fi.Exists) size = fi.Length;
                    }
                    catch (Exception)
                    {
                    }
                }
                c.Rec = BundleLedger.Begin(path, size, Caller(), isAsync, Clock.NowMs, Memory.Commit());
                Timeline.Write("BUN", "load-start name=" + c.Rec.Name + " mod=" + c.Rec.Mod + " size=" + LineFormat.Mb(size)
                    + " by=" + c.Rec.By + " async=" + (isAsync ? "1" : "0"));
                c.T0 = Stopwatch.GetTimestamp();
            }
            catch (Exception e)
            {
                LoadsFailed("load prefix", e);
                c.Rec = null;
            }
            return c;
        }

        private static void SyncPost(AssetBundle __result, Call __state)
        {
            if (__state.Rec == null) return;
            try
            {
                double main = Elapsed(__state.T0);
                bool ok = __result != null;
                Close(__state.Rec, ok ? __result : null, main);
            }
            catch (Exception e)
            {
                LoadsFailed("load postfix", e);
            }
        }

        private static void AsyncPost(AssetBundleCreateRequest __result, Call __state)
        {
            if (__state.Rec == null) return;
            try
            {
                __state.Rec.MainMs += Elapsed(__state.T0);
                if (__result == null)
                {
                    Close(__state.Rec, null, 0);
                    return;
                }
                Pending p = new Pending();
                p.Rec = __state.Rec;
                p.Req = __result;
                s_pending.Add(p);
            }
            catch (Exception e)
            {
                LoadsFailed("async load postfix", e);
            }
        }

        private static void Close(BundleRecord rec, AssetBundle bundle, double mainMs)
        {
            bool ok = bundle != null;
            BundleLedger.End(rec, ok ? bundle.GetInstanceID() : 0, ok, Clock.NowMs, Memory.Commit(), mainMs);
            if (ok)
            {
                Live l = new Live();
                l.Rec = rec;
                l.Bundle = bundle;
                s_live.Add(l);
            }
            Timeline.Write("BUN", "load-end name=" + rec.Name + " mod=" + rec.Mod + " wall=" + rec.WallMs + "ms main=" + LineFormat.N0(rec.MainMs)
                + "ms commit=" + LineFormat.SignedMb(rec.CommitGrowth) + " ok=" + (ok ? "1" : "0"));
        }

        /// <summary>Closes async loads that have finished. Cheap when nothing is pending.</summary>
        public static void Sweep()
        {
            if (s_pending.Count == 0) return;
            try
            {
                for (int i = s_pending.Count - 1; i >= 0; i--)
                {
                    Pending p = s_pending[i];
                    // assetBundle on a request that is not done would force the load to finish right now: only after isDone.
                    if (!p.Req.isDone) continue;
                    s_pending.RemoveAt(i);
                    Close(p.Rec, p.Req.assetBundle, 0);
                }
            }
            catch (Exception e)
            {
                s_pending.Clear();
                LoadsFailed("sweep", e);
            }
        }

        /// <summary>
        /// Unload is an engine-native method and is not hooked. A bundle Unity reports as destroyed is marked unloaded
        /// here; called once per second, so the time is good to about a second.
        /// </summary>
        public static void CheckUnloads()
        {
            if (s_live.Count == 0) return;
            try
            {
                for (int i = s_live.Count - 1; i >= 0; i--)
                {
                    Live l = s_live[i];
                    if (l.Bundle != null) continue;
                    s_live.RemoveAt(i);
                    BundleLedger.Unloaded(l.Rec, Clock.NowMs);
                    Timeline.Write("BUN", "unload name=" + l.Rec.Name + " mod=" + l.Rec.Mod + " assetCalls=" + l.Rec.AssetCalls);
                }
            }
            catch (Exception e)
            {
                s_live.Clear();
                LoadsFailed("unload check", e);
            }
        }

        // ---- asset use ---------------------------------------------------------------------------------------

        private static void AssetPre(out long __state)
        {
            __state = s_assetsOff ? 0 : Stopwatch.GetTimestamp();
        }

        private static void NamedAssetPost(AssetBundle __instance, string name, long __state)
        {
            Used(__instance, name, __state);
        }

        private static void AllAssetsPost(AssetBundle __instance, long __state)
        {
            Used(__instance, "(all)", __state);
        }

        private static void Used(AssetBundle bundle, string asset, long t0)
        {
            if (s_assetsOff || t0 == 0) return;
            try
            {
                double ms = Elapsed(t0);
                int now = Clock.NowMs;
                int id = bundle.GetInstanceID();
                BundleRecord r = BundleLedger.Use(id, now);
                if (r == null) r = BundleLedger.Adopt(id, bundle.name, now);
                if (ms >= SlowAssetMs)
                    Timeline.Write("BUN", "asset bundle=" + r.Name + " mod=" + r.Mod + " asset=" + asset + " main=" + LineFormat.N0(ms) + "ms");
            }
            catch (Exception e)
            {
                s_assetsOff = true;
                Timeline.Write("ERR", "asset hooks switched off: " + e.GetType().Name + ": " + e.Message);
            }
        }

        // ---- helpers -----------------------------------------------------------------------------------------

        private static double Elapsed(long t0)
        {
            return (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        }

        private static void LoadsFailed(string where, Exception e)
        {
            if (s_loadsOff) return;
            s_loadsOff = true;
            Timeline.Write("ERR", "bundle load hooks switched off (" + where + "): " + e.GetType().Name + ": " + e.Message);
        }

        /// <summary>The first assembly on the call stack that is not the engine, a patching library or this mod.</summary>
        private static string Caller()
        {
            try
            {
                StackTrace st = new StackTrace(2, false);
                int n = st.FrameCount;
                for (int i = 0; i < n; i++)
                {
                    MethodBase m = st.GetFrame(i).GetMethod();
                    if (m == null) continue;
                    Type t = m.DeclaringType;
                    if (t == null) continue;    // Harmony's generated wrapper has no declaring type
                    string a = t.Assembly.GetName().Name;
                    if (a == "UnityEngine" || a == "mscorlib" || a == "0Harmony" || a == "FastLoadPatcher" || a == "FastLoad") continue;
                    if (a.StartsWith("MonoMod", StringComparison.Ordinal) || a.StartsWith("System", StringComparison.Ordinal)) continue;
                    return a;
                }
            }
            catch (Exception)
            {
            }
            return "?";
        }
    }
}
```

- [ ] **Step 3: Build and check**

Run: `bash tools/deploy.sh --check-only`
Expected: `Build succeeded`, tests `0 failed`, `runtime check ok` twice.

- [ ] **Step 4: Confirm every hook name in `Install` exists with the parameters Harmony will inject**

Run: `ilspycmd -t FastLoad.Early.BundleHooks patcher/bin/Release/FastLoadPatcher.dll | grep -E "static void (FilePre|FileAsyncPre|MemPre|MemAsyncPre|SyncPost|AsyncPost|AssetPre|NamedAssetPost|AllAssetsPost)\("`
Expected, nine lines. Check by eye: parameters named `path` (file prefixes), `binary` (memory prefixes), `name` (`NamedAssetPost`), `__instance`, `__result`, `__state` only. Harmony binds by these names; `path`, `binary` and `name` are the engine methods' own parameter names (confirm against the Step 1 output).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add AssetBundle hooks feeding the bundle ledger" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: Frame log, memory log, quiet tracker, bursts

**Files:**
- Create: `src/Pure/FrameLog.cs`, `src/Pure/MemLog.cs`, `src/Pure/QuietTracker.cs`, `src/Pure/Bursts.cs`
- Test: `tests/FrameAndBurstTests.cs`
- Modify: `tests/Program.cs`

**Interfaces:**
- Consumes: `BundleRecord` (Task 4), `T`.
- Produces (namespace `FastLoad`):
  - `struct FrameSample { int AtMs; float Ms; }`
  - `struct FrameStats { int Count, Over25, Over50, Over100, Over250; float Median, P99, Worst; }`
  - `static class FrameLog { static void Add(int atMs, float ms); static FrameSample[] Snapshot(); static void Reset(); static FrameStats Stats(FrameSample[] all, int fromMs, int toMs); static int CountAtLeast(FrameSample[] all, int fromMs, int toMs, float ms); }` — window is `fromMs <= AtMs < toMs`.
  - `struct MemSample { int AtMs; long Commit, WorkingSet, SysFree, SysLimit; }`
  - `static class MemLog { static void Add(MemSample s); static MemSample[] Snapshot(); static void Reset(); static bool AtOrAfter(MemSample[] all, int ms, out MemSample found); static bool PeakCommit(MemSample[] all, out MemSample found); static bool LowestSysFree(MemSample[] all, out MemSample found); }`
  - `sealed class QuietTracker { const int WindowMs = 10000; int Step(int nowMs, int menuAtMs, bool busy); }` — returns the start of the quiet window once, else -1.
  - `sealed class Burst { int StartMs, EndMs; List<BundleRecord> Bundles; long Size; long CommitGrowth; int Hitches; }`
  - `static class Bursts { const int GapMs = 2000; static List<Burst> Find(List<BundleRecord> records, int afterMs, FrameSample[] frames, float hitchMs); }`

- [ ] **Step 1: Write the failing test**

`tests/FrameAndBurstTests.cs`:

```csharp
using System.Collections.Generic;
using FastLoad.Early;

namespace FastLoad.Tests
{
    public static class FrameAndBurstTests
    {
        private const long MB = 1048576L;

        private static BundleRecord Rec(string name, int start, int end, long size, long commitStart, long commitEnd)
        {
            BundleRecord r = new BundleRecord();
            r.Name = name;
            r.Mod = "M";
            r.By = "L";
            r.StartMs = start;
            r.EndMs = end;
            r.Size = size;
            r.CommitStart = commitStart;
            r.CommitEnd = commitEnd;
            r.Ok = end >= 0;
            return r;
        }

        public static void Run()
        {
            T.Group("FrameLog");
            FrameLog.Reset();
            // 100 frames of 14 ms from t=1000, with four slow ones.
            for (int i = 0; i < 100; i++)
            {
                float ms = 14f;
                if (i == 10) ms = 30f;
                if (i == 20) ms = 60f;
                if (i == 30) ms = 120f;
                if (i == 40) ms = 300f;
                FrameLog.Add(1000 + i * 14, ms);
            }
            FrameSample[] frames = FrameLog.Snapshot();
            T.Eq(100, frames.Length, "all frames kept");

            FrameStats all = FrameLog.Stats(frames, 0, int.MaxValue);
            T.Eq(100, all.Count, "count");
            T.Eq(4, all.Over25, "frames of 25 ms or more");
            T.Eq(3, all.Over50, "frames of 50 ms or more");
            T.Eq(2, all.Over100, "frames of 100 ms or more");
            T.Eq(1, all.Over250, "frames of 250 ms or more");
            T.Eq(14f, all.Median, "median");
            T.Eq(300f, all.Worst, "worst");
            T.True(all.P99 >= 120f, "99th percentile is among the slow frames: " + all.P99);

            FrameStats window = FrameLog.Stats(frames, 1000, 1000 + 15 * 14);
            T.Eq(15, window.Count, "window is from inclusive, to exclusive");
            T.Eq(1, window.Over25, "window sees only its own slow frame");

            FrameStats none = FrameLog.Stats(frames, 900000, 900001);
            T.Eq(0, none.Count, "empty window");
            T.Eq(0f, none.Median, "empty window median is 0");
            T.Eq(0, FrameLog.Stats(new FrameSample[0], 0, 10).Count, "no frames at all");
            T.Eq(2, FrameLog.CountAtLeast(frames, 1000 + 25 * 14, int.MaxValue, 100f), "count at least, in a window");

            T.Group("MemLog");
            MemLog.Reset();
            MemSample found;
            T.True(!MemLog.AtOrAfter(MemLog.Snapshot(), 5, out found), "nothing sampled yet");
            for (int i = 1; i <= 5; i++)
            {
                MemSample s = new MemSample();
                s.AtMs = i * 1000;
                s.Commit = (i == 4 ? 20000 : 10000 + i) * MB;
                s.WorkingSet = 8000 * MB;
                s.SysFree = (i == 3 ? 900 : 5000) * MB;
                s.SysLimit = 52700 * MB;
                MemLog.Add(s);
            }
            MemSample[] mem = MemLog.Snapshot();
            T.True(MemLog.AtOrAfter(mem, 2500, out found) && found.AtMs == 3000, "first sample at or after the mark");
            T.True(MemLog.AtOrAfter(mem, 99999, out found) && found.AtMs == 5000, "after the last sample gives the last");
            T.True(MemLog.PeakCommit(mem, out found) && found.AtMs == 4000, "peak commit");
            T.True(MemLog.LowestSysFree(mem, out found) && found.AtMs == 3000, "lowest system commit free");
            T.True(!MemLog.PeakCommit(new MemSample[0], out found), "no samples, no peak");

            T.Group("QuietTracker");
            QuietTracker q = new QuietTracker();
            T.Eq(-1, q.Step(1000, -1, false), "no menu yet: never quiet");
            T.Eq(-1, q.Step(50000, 50000, false), "window starts");
            T.Eq(-1, q.Step(55000, 50000, true), "busy restarts the window");
            T.Eq(-1, q.Step(64999, 50000, false), "not yet 10 s since the last busy moment");
            T.Eq(55000, q.Step(65000, 50000, false), "quiet reported once, with the time it began");
            T.Eq(-1, q.Step(90000, 50000, false), "never reported again");

            T.Group("Bursts");
            List<BundleRecord> recs = new List<BundleRecord>();
            recs.Add(Rec("boot1", 1000, 5000, 100 * MB, 1000 * MB, 1100 * MB));       // before quiet: ignored
            recs.Add(Rec("ar2_c", 201500, 204000, 30 * MB, 5020 * MB, 5090 * MB));    // out of order on purpose
            recs.Add(Rec("ar2_a", 200000, 201000, 10 * MB, 5000 * MB, 5010 * MB));
            recs.Add(Rec("ar2_b", 200900, 203000, 20 * MB, 5008 * MB, 5060 * MB));
            recs.Add(Rec("map_x", 300000, -1, 700 * MB, 6000 * MB, 0));               // still loading
            FrameSample[] hits = new FrameSample[3];
            hits[0].AtMs = 200500; hits[0].Ms = 80f;
            hits[1].AtMs = 203900; hits[1].Ms = 40f;
            hits[2].AtMs = 250000; hits[2].Ms = 90f;

            List<Burst> bursts = Bursts.Find(recs, 100000, hits, 25f);
            T.Eq(2, bursts.Count, "two bursts after quiet");
            Burst b = bursts[0];
            T.Eq(3, b.Bundles.Count, "three bundles within 2 s of each other");
            T.Eq("ar2_a", b.Bundles[0].Name, "bundles in start order");
            T.Eq(200000, b.StartMs, "burst start");
            T.Eq(204000, b.EndMs, "burst end = last bundle to finish");
            T.Eq(60 * MB, b.Size, "burst size");
            T.Eq(90 * MB, b.CommitGrowth, "commit growth from first start to last end");
            T.Eq(2, b.Hitches, "slow frames inside the burst");
            T.Eq(1, bursts[1].Bundles.Count, "second burst");
            T.Eq(300000, bursts[1].EndMs, "unfinished bundle: burst end is its start");
            T.Eq(0L, bursts[1].CommitGrowth, "unfinished bundle: no growth claimed");

            T.Eq(0, Bursts.Find(recs, -1, hits, 25f).Count, "no quiet mark, no bursts");
            T.Eq(0, Bursts.Find(new List<BundleRecord>(), 100000, hits, 25f).Count, "no records, no bursts");
        }
    }
}
```

Add `FrameAndBurstTests.Run();` to `tests/Program.cs` after `BundleLedgerTests.Run();`.

- [ ] **Step 2: Run to see it fail**

Run: `dotnet run --project tests/FastLoad.Tests.csproj`
Expected: build error, `The name 'FrameLog' does not exist in the current context`.

- [ ] **Step 3: Implement**

`src/Pure/FrameLog.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;

namespace FastLoad
{
    public struct FrameSample
    {
        /// <summary>Run-clock time at which the frame ended.</summary>
        public int AtMs;
        /// <summary>How long the frame took, real time.</summary>
        public float Ms;
    }

    public struct FrameStats
    {
        public int Count;
        public int Over25;
        public int Over50;
        public int Over100;
        public int Over250;
        public float Median;
        public float P99;
        public float Worst;
    }

    /// <summary>Every frame time of the run (8 bytes each: an hour at 72 fps is about 2 MB).</summary>
    public static class FrameLog
    {
        private static readonly object s_gate = new object();
        private static readonly List<FrameSample> s_frames = new List<FrameSample>(65536);

        public static void Add(int atMs, float ms)
        {
            FrameSample f = new FrameSample();
            f.AtMs = atMs;
            f.Ms = ms;
            Monitor.Enter(s_gate);
            try
            {
                s_frames.Add(f);
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        public static FrameSample[] Snapshot()
        {
            Monitor.Enter(s_gate);
            try
            {
                return s_frames.ToArray();
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        public static void Reset()
        {
            Monitor.Enter(s_gate);
            try
            {
                s_frames.Clear();
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        /// <summary>Stats of the frames with fromMs &lt;= AtMs &lt; toMs.</summary>
        public static FrameStats Stats(FrameSample[] all, int fromMs, int toMs)
        {
            FrameStats s = new FrameStats();
            List<float> ms = new List<float>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].AtMs < fromMs || all[i].AtMs >= toMs) continue;
                float v = all[i].Ms;
                ms.Add(v);
                if (v >= 25f) s.Over25++;
                if (v >= 50f) s.Over50++;
                if (v >= 100f) s.Over100++;
                if (v >= 250f) s.Over250++;
                if (v > s.Worst) s.Worst = v;
            }
            s.Count = ms.Count;
            if (ms.Count == 0) return s;
            ms.Sort();
            s.Median = ms[ms.Count / 2];
            s.P99 = ms[Math.Min(ms.Count - 1, (int)(ms.Count * 0.99))];
            return s;
        }

        public static int CountAtLeast(FrameSample[] all, int fromMs, int toMs, float ms)
        {
            int n = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].AtMs >= fromMs && all[i].AtMs < toMs && all[i].Ms >= ms) n++;
            }
            return n;
        }
    }
}
```

`src/Pure/MemLog.cs`:

```csharp
using System.Collections.Generic;
using System.Threading;

namespace FastLoad
{
    public struct MemSample
    {
        public int AtMs;
        /// <summary>Private bytes this process has committed.</summary>
        public long Commit;
        public long WorkingSet;
        /// <summary>How much more the whole system can still commit.</summary>
        public long SysFree;
        /// <summary>RAM plus page files.</summary>
        public long SysLimit;
    }

    /// <summary>One memory sample per second for the whole run.</summary>
    public static class MemLog
    {
        private static readonly object s_gate = new object();
        private static readonly List<MemSample> s_samples = new List<MemSample>(4096);

        public static void Add(MemSample s)
        {
            Monitor.Enter(s_gate);
            try
            {
                s_samples.Add(s);
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        public static MemSample[] Snapshot()
        {
            Monitor.Enter(s_gate);
            try
            {
                return s_samples.ToArray();
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        public static void Reset()
        {
            Monitor.Enter(s_gate);
            try
            {
                s_samples.Clear();
            }
            finally
            {
                Monitor.Exit(s_gate);
            }
        }

        /// <summary>The first sample at or after <paramref name="ms"/>; the last one if the run ended before that.</summary>
        public static bool AtOrAfter(MemSample[] all, int ms, out MemSample found)
        {
            found = new MemSample();
            if (all.Length == 0) return false;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].AtMs >= ms)
                {
                    found = all[i];
                    return true;
                }
            }
            found = all[all.Length - 1];
            return true;
        }

        public static bool PeakCommit(MemSample[] all, out MemSample found)
        {
            found = new MemSample();
            if (all.Length == 0) return false;
            found = all[0];
            for (int i = 1; i < all.Length; i++)
            {
                if (all[i].Commit > found.Commit) found = all[i];
            }
            return true;
        }

        public static bool LowestSysFree(MemSample[] all, out MemSample found)
        {
            found = new MemSample();
            if (all.Length == 0) return false;
            found = all[0];
            for (int i = 1; i < all.Length; i++)
            {
                if (all[i].SysFree < found.SysFree) found = all[i];
            }
            return true;
        }
    }
}
```

`src/Pure/QuietTracker.cs`:

```csharp
namespace FastLoad
{
    /// <summary>
    /// Finds "quiet": the first 10 s after the menu scene loaded with no bundle loading and no slow frame. That is
    /// the moment the menu is really usable.
    /// </summary>
    public sealed class QuietTracker
    {
        public const int WindowMs = 10000;

        private int m_since = -1;
        private bool m_done;

        /// <param name="menuAtMs">When the menu scene loaded; negative while it has not.</param>
        /// <param name="busy">A bundle is loading, or this frame was slow.</param>
        /// <returns>Once: the time the quiet window began. Otherwise -1.</returns>
        public int Step(int nowMs, int menuAtMs, bool busy)
        {
            if (m_done || menuAtMs < 0) return -1;
            if (busy || m_since < 0)
            {
                m_since = nowMs;
                return -1;
            }
            if (nowMs - m_since >= WindowMs)
            {
                m_done = true;
                return m_since;
            }
            return -1;
        }
    }
}
```

`src/Pure/Bursts.cs`:

```csharp
using System.Collections.Generic;
using FastLoad.Early;

namespace FastLoad
{
    /// <summary>Bundle loads that began within 2 s of each other after the menu went quiet: one spawn, one map load.</summary>
    public sealed class Burst
    {
        public int StartMs;
        public int EndMs;
        public List<BundleRecord> Bundles = new List<BundleRecord>();
        public long Size;
        /// <summary>Process commit at the last bundle's end minus commit at the first one's start.</summary>
        public long CommitGrowth;
        /// <summary>Slow frames between the burst's start and end.</summary>
        public int Hitches;
    }

    public static class Bursts
    {
        public const int GapMs = 2000;

        /// <param name="afterMs">The quiet mark; negative = quiet never came, no bursts.</param>
        public static List<Burst> Find(List<BundleRecord> records, int afterMs, FrameSample[] frames, float hitchMs)
        {
            List<Burst> found = new List<Burst>();
            if (afterMs < 0) return found;

            List<BundleRecord> late = new List<BundleRecord>();
            for (int i = 0; i < records.Count; i++)
            {
                if (records[i].StartMs >= afterMs) late.Add(records[i]);
            }
            late.Sort(ByStart);

            Burst cur = null;
            int lastStart = 0;
            BundleRecord lastEnded = null;
            for (int i = 0; i < late.Count; i++)
            {
                BundleRecord r = late[i];
                if (cur == null || r.StartMs - lastStart > GapMs)
                {
                    Finish(cur, lastEnded, frames, hitchMs);
                    cur = new Burst();
                    cur.StartMs = r.StartMs;
                    cur.EndMs = r.StartMs;
                    lastEnded = null;
                    found.Add(cur);
                }
                cur.Bundles.Add(r);
                cur.Size += r.Size;
                lastStart = r.StartMs;
                if (r.EndMs >= 0 && r.EndMs >= cur.EndMs)
                {
                    cur.EndMs = r.EndMs;
                    lastEnded = r;
                }
            }
            Finish(cur, lastEnded, frames, hitchMs);
            return found;
        }

        private static void Finish(Burst b, BundleRecord lastEnded, FrameSample[] frames, float hitchMs)
        {
            if (b == null) return;
            if (lastEnded != null) b.CommitGrowth = lastEnded.CommitEnd - b.Bundles[0].CommitStart;
            b.Hitches = FrameLog.CountAtLeast(frames, b.StartMs, b.EndMs + 1, hitchMs);
        }

        private static int ByStart(BundleRecord a, BundleRecord b)
        {
            return a.StartMs.CompareTo(b.StartMs);
        }
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet run --project tests/FastLoad.Tests.csproj`
Expected: `N passed, 0 failed`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add frame log, memory log, quiet tracker and spawn bursts" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 8: Report builder

**Files:**
- Create: `src/Pure/ReportBuilder.cs`
- Test: `tests/ReportBuilderTests.cs`
- Modify: `tests/Program.cs`

**Interfaces:**
- Consumes: `BundleRecord`, `Phase`, `Phases.Names`, `Phases.Count`, `LineFormat` (Tasks 2 to 4); `FrameSample`, `FrameLog.Stats`, `MemSample`, `MemLog`, `Bursts`, `Burst` (Task 7).
- Produces (namespace `FastLoad`):
  - `sealed class ReportInput { string Profile = ""; string Args = ""; string HookMode = ""; int PluginCount = -1; int RestartCostMs = -1; int NowMs; float HitchMs = 25f; int[] PhaseAt; int LoaderBundleLines; List<BundleRecord> Bundles; FrameSample[] Frames; MemSample[] Mem; }` — every collection field has a non-null default.
  - `static class ReportBuilder { const int TopN = 40; static string Build(ReportInput input); }`

- [ ] **Step 1: Write the failing test**

`tests/ReportBuilderTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using FastLoad.Early;

namespace FastLoad.Tests
{
    public static class ReportBuilderTests
    {
        private const long MB = 1048576L;

        private static BundleRecord Rec(string mod, string name, string by, int start, int end, long size, double main, long growthMb, int assetCalls)
        {
            BundleRecord r = new BundleRecord();
            r.Path = @"C:\p\plugins\" + mod + @"\" + name;
            r.Name = name;
            r.Mod = mod;
            r.By = by;
            r.StartMs = start;
            r.EndMs = end;
            r.Size = size;
            r.MainMs = main;
            r.CommitStart = 5000 * MB;
            r.CommitEnd = (5000 + growthMb) * MB;
            r.Ok = true;
            r.AssetCalls = assetCalls;
            return r;
        }

        private static string Section(string report, string title)
        {
            int at = report.IndexOf(title, StringComparison.Ordinal);
            if (at < 0) return "";
            int next = report.IndexOf("\n== ", at + title.Length, StringComparison.Ordinal);
            return next < 0 ? report.Substring(at) : report.Substring(at, next - at);
        }

        public static void Run()
        {
            T.Group("ReportBuilder");

            // EmptyRunBuilds: the game died before anything was measured.
            string empty = null;
            bool threw = false;
            try { empty = ReportBuilder.Build(new ReportInput()); }
            catch (Exception) { threw = true; }
            T.True(!threw && empty != null, "a report builds from nothing");
            for (int i = 1; i <= 8; i++) T.True(empty.Contains("== " + i + ". "), "empty report has section " + i);

            // MissingPhasesSayNotSeen
            ReportInput early = new ReportInput();
            early.NowMs = 30000;
            early.PhaseAt = new int[] { 0, 4100, 4300, -1, -1, -1, -1, -1 };
            string er = ReportBuilder.Build(early);
            string phases = Section(er, "== 2. Phases");
            T.True(phases.Contains("4.1 s"), "seen phase shows its time");
            T.True(phases.Split(new string[] { "not seen" }, StringSplitOptions.None).Length - 1 == 5, "five unseen phases say not seen: " + phases);
            T.True(Section(er, "== 3. Menu frames").Contains("menu scene not seen"), "menu frames say why they are missing");
            T.True(Section(er, "== 8. Spawn bursts").Contains("quiet not seen"), "bursts say why they are missing");

            // A full run.
            ReportInput inp = new ReportInput();
            inp.Profile = @"C:\profiles\new2\BepInEx";
            inp.Args = "h3vr.exe -force-gfx-jobs";
            inp.HookMode = "managed, 12 of 12 hooks on";
            inp.PluginCount = 767;
            inp.RestartCostMs = 1166;
            inp.NowMs = 400000;
            inp.HitchMs = 25f;
            inp.LoaderBundleLines = 5;
            inp.PhaseAt = new int[] { 0, 4100, 4300, 31000, 82000, 101000, 95000, 130000 };

            inp.Bundles.Add(Rec("Not_Wolfie-AR2_Plus", "ar2plus-main", "OtherLoader", 40000, 58034, 412 * MB, 212, 388, 30));
            inp.Bundles.Add(Rec("Meat_banono-Meats_ModulAR", "modular", "OtherLoader", 41000, 45000, 900 * MB, 1500, 950, 0));
            inp.Bundles.Add(Rec("Marcel-TNH_Modern_Mansion", "mansion", "Atlas", 42000, 43000, 790 * MB, 20, 5, 0));
            inp.Bundles.Add(Rec("Not_Wolfie-AR2_Plus", "mw_ar2_plus", "ModulWorkshopOptimizer", 200000, 201000, 50 * MB, 40, 60, 12));
            inp.Bundles.Add(Rec("Volks-AR2VPack", "mw_ar2vpack", "ModulWorkshopOptimizer", 200500, 203000, 1020 * MB, 90, 800, 3));
            BundleRecord loading = Rec("Slow-Mod", "still_loading", "Stratum", 390000, -1, 10 * MB, 0, 0, 0);
            loading.Ok = false;
            inp.Bundles.Add(loading);

            List<FrameSample> fl = new List<FrameSample>();
            for (int t = 95000; t < 170000; t += 14)
            {
                FrameSample f = new FrameSample();
                f.AtMs = t;
                f.Ms = (t < 130000 && (t / 14) % 50 == 0) ? 120f : 14f;
                fl.Add(f);
            }
            FrameSample hit = new FrameSample();
            hit.AtMs = 201500;
            hit.Ms = 300f;
            fl.Add(hit);
            inp.Frames = fl.ToArray();

            List<MemSample> ml = new List<MemSample>();
            for (int t = 1000; t <= 400000; t += 1000)
            {
                MemSample m = new MemSample();
                m.AtMs = t;
                m.Commit = (4000 + t / 100) * MB;
                m.WorkingSet = (3000 + t / 200) * MB;
                m.SysFree = (30000 - t / 20) * MB;
                m.SysLimit = 52700 * MB;
                ml.Add(m);
            }
            inp.Mem = ml.ToArray();

            string r = ReportBuilder.Build(inp);

            string run = Section(r, "== 1. Run");
            T.True(run.Contains(@"C:\profiles\new2\BepInEx"), "run: profile");
            T.True(run.Contains("767"), "run: plugin count");
            T.True(run.Contains("restart on launch: yes, cost 1166 ms"), "run: restart cost");
            T.True(run.Contains("managed, 12 of 12 hooks on"), "run: hook mode");
            T.True(run.Contains("hooks saw 6") && run.Contains("loaders reported 5"), "run: cross-check numbers: " + run);

            string ph = Section(r, "== 2. Phases");
            T.True(ph.Contains("82.0 s") && ph.Contains("101.0 s") && ph.Contains("130.0 s"), "phases: times");
            T.True(!ph.Contains("not seen"), "phases: all seen");
            T.True(ph.Contains("-6.0 s"), "phases: the menu came 6 s before OtherLoader finished, shown as a negative gap: " + ph);

            string mf = Section(r, "== 3. Menu frames");
            T.True(mf.Contains("menu loaded until quiet") && mf.Contains("30 s after quiet"), "menu frames: both windows");

            string bu = Section(r, "== 4. Bundles");
            T.True(bu.IndexOf("ar2plus-main", StringComparison.Ordinal) < bu.IndexOf("modular", StringComparison.Ordinal), "bundles: longest wall time first");
            T.True(bu.Contains("by main-thread time") && bu.Contains("by commit growth"), "bundles: three tables");
            T.True(bu.Contains("still loading: 1"), "bundles: unfinished loads are counted, not ranked");

            string mods = Section(r, "== 5. Mods");
            T.True(mods.Contains("Not_Wolfie-AR2_Plus") && mods.Contains("bundles=2"), "mods: grouped by folder");
            T.True(mods.Contains("OtherLoader") && mods.Contains("ModulWorkshopOptimizer"), "mods: per loader");

            string nu = Section(r, "== 6. Loaded, never used");
            T.True(nu.Contains("modular") && nu.Contains("mansion"), "never used: listed");
            T.True(!nu.Contains("ar2plus-main"), "never used: a used bundle is not listed");
            T.True(nu.Contains("scene"), "never used: says scene bundles show here too");

            string me = Section(r, "== 7. Memory");
            T.True(me.Contains("peak commit") && me.Contains("lowest system commit free") && me.Contains("52700MB"), "memory: peak, low, limit");

            string sb = Section(r, "== 8. Spawn bursts");
            T.True(sb.Contains("bundles=2") && sb.Contains("mw_ar2vpack") && sb.Contains("slow frames=1"), "bursts: the AR2 spawn: " + sb);

            // Top tables are cut at TopN.
            ReportInput many = new ReportInput();
            for (int i = 0; i < 100; i++) many.Bundles.Add(Rec("Mod" + i, "bundle" + i, "L", 1000, 1000 + i, MB, i, i, 1));
            string mr = ReportBuilder.Build(many);
            string top = Section(mr, "== 4. Bundles");
            T.True(top.Contains("bundle99") && !top.Contains("bundle10 "), "bundles: only the top " + ReportBuilder.TopN + " are listed");
        }
    }
}
```

Add `ReportBuilderTests.Run();` to `tests/Program.cs` after `FrameAndBurstTests.Run();`.

- [ ] **Step 2: Run to see it fail**

Run: `dotnet run --project tests/FastLoad.Tests.csproj`
Expected: build error, `The name 'ReportBuilder' does not exist in the current context`.

- [ ] **Step 3: Implement**

`src/Pure/ReportBuilder.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text;
using FastLoad.Early;

namespace FastLoad
{
    /// <summary>Everything one report is built from. A snapshot: nothing in here changes while the text is built.</summary>
    public sealed class ReportInput
    {
        public string Profile = "";
        public string Args = "";
        public string HookMode = "";
        public int PluginCount = -1;
        /// <summary>-1 = no restart on launch.</summary>
        public int RestartCostMs = -1;
        public int NowMs;
        public float HitchMs = 25f;
        /// <summary>Indexed by Phase; -1 = not seen. Null = nothing seen.</summary>
        public int[] PhaseAt;
        public int LoaderBundleLines;
        public List<BundleRecord> Bundles = new List<BundleRecord>();
        public FrameSample[] Frames = new FrameSample[0];
        public MemSample[] Mem = new MemSample[0];
    }

    /// <summary>Turns a snapshot into the report text. No file or game access: fully testable.</summary>
    public static class ReportBuilder
    {
        public const int TopN = 40;

        private sealed class Agg
        {
            public string Name;
            public int Count;
            public long Size;
            public double MainMs;
            public long Commit;
        }

        public static string Build(ReportInput inp)
        {
            int[] at = inp.PhaseAt;
            if (at == null || at.Length != Phases.Count)
            {
                at = new int[Phases.Count];
                for (int i = 0; i < at.Length; i++) at[i] = -1;
                at[(int)Phase.ProcessStart] = 0;
            }

            StringBuilder sb = new StringBuilder(16384);
            sb.AppendLine("FastLoad probe report, written " + LineFormat.Sec(inp.NowMs) + " into the run");
            sb.AppendLine("Times are real seconds since the game process started. MB = 1048576 bytes.");

            Run(sb, inp);
            PhaseTable(sb, at);
            MenuFrames(sb, inp, at);
            BundleTables(sb, inp);
            Mods(sb, inp);
            NeverUsed(sb, inp);
            MemorySection(sb, inp, at);
            BurstSection(sb, inp, at);
            return sb.ToString();
        }

        private static void Run(StringBuilder sb, ReportInput inp)
        {
            sb.AppendLine();
            sb.AppendLine("== 1. Run ==");
            sb.AppendLine("profile: " + inp.Profile);
            sb.AppendLine("start options: " + inp.Args);
            sb.AppendLine("plugins loaded: " + (inp.PluginCount < 0 ? "unknown" : inp.PluginCount.ToString()));
            sb.AppendLine("restart on launch: " + (inp.RestartCostMs < 0 ? "no" : "yes, cost " + inp.RestartCostMs + " ms"));
            sb.AppendLine("bundle hooks: " + inp.HookMode);
            sb.AppendLine("cross-check: hooks saw " + inp.Bundles.Count + " bundle loads, loaders reported " + inp.LoaderBundleLines
                + " in their own log lines (a much higher loader number means loads are being missed)");
        }

        private static void PhaseTable(StringBuilder sb, int[] at)
        {
            sb.AppendLine();
            sb.AppendLine("== 2. Phases ==");
            sb.AppendLine("reached at | since the mark before it | phase");
            int prev = 0;
            for (int i = 0; i < Phases.Count; i++)
            {
                string since = at[i] < 0 ? "" : Gap(at[i] - prev);
                sb.AppendLine(LineFormat.Sec(at[i]).PadLeft(10) + " | " + since.PadLeft(10) + " | " + Phases.Names[i]);
                if (at[i] >= 0) prev = at[i];
            }
            sb.AppendLine("(Stratum, OtherLoader and the menu scene do not always come in this order; 'since' is against the line above that was seen.)");
        }

        /// <summary>"+12.3 s" / "-6.0 s": a phase can be reached before the one listed above it.</summary>
        private static string Gap(int ms)
        {
            return (ms < 0 ? "-" : "+") + LineFormat.N1(Math.Abs(ms) / 1000.0) + " s";
        }

        private static void MenuFrames(StringBuilder sb, ReportInput inp, int[] at)
        {
            sb.AppendLine();
            sb.AppendLine("== 3. Menu frames ==");
            int menu = at[(int)Phase.MenuLoaded];
            int quiet = at[(int)Phase.Quiet];
            if (menu < 0)
            {
                sb.AppendLine("menu scene not seen: no menu frame numbers");
                return;
            }
            if (quiet < 0)
            {
                sb.AppendLine("quiet not seen yet: the first line runs up to now");
                FrameLine(sb, "menu loaded until now", FrameLog.Stats(inp.Frames, menu, int.MaxValue), inp.NowMs - menu);
                return;
            }
            FrameLine(sb, "menu loaded until quiet", FrameLog.Stats(inp.Frames, menu, quiet), quiet - menu);
            FrameLine(sb, "30 s after quiet", FrameLog.Stats(inp.Frames, quiet, quiet + 30000), 30000);
        }

        private static void FrameLine(StringBuilder sb, string label, FrameStats s, int spanMs)
        {
            sb.AppendLine(label + " (" + LineFormat.Sec(spanMs) + "): frames=" + s.Count
                + " median=" + LineFormat.N1(s.Median) + "ms p99=" + LineFormat.N1(s.P99) + "ms worst=" + LineFormat.N0(s.Worst) + "ms"
                + " | frames of >=25ms: " + s.Over25 + ", >=50ms: " + s.Over50 + ", >=100ms: " + s.Over100 + ", >=250ms: " + s.Over250);
        }

        private static void BundleTables(StringBuilder sb, ReportInput inp)
        {
            sb.AppendLine();
            sb.AppendLine("== 4. Bundles ==");
            List<BundleRecord> done = new List<BundleRecord>();
            int loading = 0;
            int failed = 0;
            for (int i = 0; i < inp.Bundles.Count; i++)
            {
                BundleRecord r = inp.Bundles[i];
                if (!r.Done) loading++;
                else if (!r.Ok) failed++;
                else done.Add(r);
            }
            sb.AppendLine("loaded: " + done.Count + ", still loading: " + loading + ", load gave nothing: " + failed);
            sb.AppendLine("wall = start to end (waiting in a loader's queue counts). main = time the main thread sat inside the load call.");
            sb.AppendLine("commit = process commit growth while it loaded; loads that overlap share the same growth.");

            sb.AppendLine("-- top " + TopN + " by wall time --");
            Table(sb, done, ByWall);
            sb.AppendLine("-- top " + TopN + " by main-thread time --");
            Table(sb, done, ByMain);
            sb.AppendLine("-- top " + TopN + " by commit growth --");
            Table(sb, done, ByCommit);
        }

        private static void Table(StringBuilder sb, List<BundleRecord> done, Comparison<BundleRecord> order)
        {
            List<BundleRecord> sorted = new List<BundleRecord>(done);
            sorted.Sort(order);
            int n = Math.Min(TopN, sorted.Count);
            for (int i = 0; i < n; i++) sb.AppendLine(Row(sorted[i]));
            if (sorted.Count == 0) sb.AppendLine("(none)");
        }

        private static string Row(BundleRecord r)
        {
            return "wall=" + LineFormat.Sec(r.WallMs).PadLeft(8) + " main=" + (LineFormat.N0(r.MainMs) + "ms").PadLeft(8)
                + " commit=" + LineFormat.SignedMb(r.CommitGrowth).PadLeft(8) + " size=" + LineFormat.Mb(r.Size).PadLeft(7)
                + " at=" + LineFormat.Sec(r.StartMs).PadLeft(8) + " uses=" + r.AssetCalls.ToString().PadLeft(5)
                + "  " + r.Name + " | mod=" + r.Mod + " | by=" + r.By;
        }

        private static int ByWall(BundleRecord a, BundleRecord b)
        {
            return b.WallMs.CompareTo(a.WallMs);
        }

        private static int ByMain(BundleRecord a, BundleRecord b)
        {
            return b.MainMs.CompareTo(a.MainMs);
        }

        private static int ByCommit(BundleRecord a, BundleRecord b)
        {
            return b.CommitGrowth.CompareTo(a.CommitGrowth);
        }

        private static void Mods(StringBuilder sb, ReportInput inp)
        {
            sb.AppendLine();
            sb.AppendLine("== 5. Mods ==");
            sb.AppendLine("-- top " + TopN + " mod folders by commit growth --");
            Group(sb, inp.Bundles, true);
            sb.AppendLine("-- by the loader that asked for the bundles --");
            Group(sb, inp.Bundles, false);
        }

        private static void Group(StringBuilder sb, List<BundleRecord> all, bool byMod)
        {
            Dictionary<string, Agg> map = new Dictionary<string, Agg>();
            List<Agg> list = new List<Agg>();
            for (int i = 0; i < all.Count; i++)
            {
                BundleRecord r = all[i];
                if (!r.Done || !r.Ok) continue;
                string key = (byMod ? r.Mod : r.By) ?? "?";
                Agg a;
                if (!map.TryGetValue(key, out a))
                {
                    a = new Agg();
                    a.Name = key;
                    map[key] = a;
                    list.Add(a);
                }
                a.Count++;
                a.Size += r.Size;
                a.MainMs += r.MainMs;
                a.Commit += r.CommitGrowth;
            }
            list.Sort(AggByCommit);
            int n = Math.Min(TopN, list.Count);
            for (int i = 0; i < n; i++)
            {
                Agg a = list[i];
                sb.AppendLine("commit=" + LineFormat.SignedMb(a.Commit).PadLeft(8) + " main=" + (LineFormat.N0(a.MainMs) + "ms").PadLeft(8)
                    + " size=" + LineFormat.Mb(a.Size).PadLeft(7) + " bundles=" + a.Count + "  " + a.Name);
            }
            if (list.Count == 0) sb.AppendLine("(none)");
        }

        private static int AggByCommit(Agg a, Agg b)
        {
            return b.Commit.CompareTo(a.Commit);
        }

        private static void NeverUsed(StringBuilder sb, ReportInput inp)
        {
            sb.AppendLine();
            sb.AppendLine("== 6. Loaded, never used ==");
            sb.AppendLine("Bundles that loaded and had no asset call up to now. Candidates for loading later or on demand.");
            sb.AppendLine("Scene bundles (maps) show here too: scenes are not loaded through asset calls.");
            List<BundleRecord> idle = new List<BundleRecord>();
            long total = 0;
            for (int i = 0; i < inp.Bundles.Count; i++)
            {
                BundleRecord r = inp.Bundles[i];
                if (r.Done && r.Ok && r.AssetCalls == 0)
                {
                    idle.Add(r);
                    total += r.Size;
                }
            }
            idle.Sort(BySize);
            sb.AppendLine("count=" + idle.Count + " total size=" + LineFormat.Mb(total));
            for (int i = 0; i < idle.Count; i++)
            {
                BundleRecord r = idle[i];
                sb.AppendLine("size=" + LineFormat.Mb(r.Size).PadLeft(7) + " commit=" + LineFormat.SignedMb(r.CommitGrowth).PadLeft(8)
                    + (r.UnloadMs >= 0 ? " unloaded at " + LineFormat.Sec(r.UnloadMs) : " still loaded") + "  " + r.Name + " | mod=" + r.Mod);
            }
        }

        private static int BySize(BundleRecord a, BundleRecord b)
        {
            return b.Size.CompareTo(a.Size);
        }

        private static void MemorySection(StringBuilder sb, ReportInput inp, int[] at)
        {
            sb.AppendLine();
            sb.AppendLine("== 7. Memory ==");
            if (inp.Mem.Length == 0)
            {
                sb.AppendLine("no memory samples (the run ended before the first second of sampling)");
                return;
            }
            sb.AppendLine("commit = private memory this process has committed. system commit free = how much more Windows can hand out");
            sb.AppendLine("before allocations fail (RAM + page files - everything committed).");
            MemSample m;
            for (int i = 0; i < Phases.Count; i++)
            {
                if (at[i] < 0) continue;
                if (MemLog.AtOrAfter(inp.Mem, at[i], out m)) sb.AppendLine(MemRow(m) + "  at: " + Phases.Names[i]);
            }
            if (MemLog.PeakCommit(inp.Mem, out m)) sb.AppendLine("peak commit: " + LineFormat.Mb(m.Commit) + " at " + LineFormat.Sec(m.AtMs));
            if (MemLog.LowestSysFree(inp.Mem, out m))
                sb.AppendLine("lowest system commit free: " + LineFormat.Mb(m.SysFree) + " at " + LineFormat.Sec(m.AtMs)
                    + " (system commit limit " + LineFormat.Mb(m.SysLimit) + ")");
        }

        private static string MemRow(MemSample m)
        {
            return LineFormat.Sec(m.AtMs).PadLeft(10) + " commit=" + LineFormat.Mb(m.Commit).PadLeft(8) + " working set=" + LineFormat.Mb(m.WorkingSet).PadLeft(8)
                + " system commit free=" + LineFormat.Mb(m.SysFree).PadLeft(8);
        }

        private static void BurstSection(StringBuilder sb, ReportInput inp, int[] at)
        {
            sb.AppendLine();
            sb.AppendLine("== 8. Spawn bursts ==");
            int quiet = at[(int)Phase.Quiet];
            if (quiet < 0)
            {
                sb.AppendLine("quiet not seen: bursts are only counted after the menu went quiet");
                return;
            }
            sb.AppendLine("Bundle loads after quiet that began within " + (Bursts.GapMs / 1000) + " s of each other. One gun spawn or one map load is one burst.");
            List<Burst> bursts = Bursts.Find(inp.Bundles, quiet, inp.Frames, inp.HitchMs);
            if (bursts.Count == 0) sb.AppendLine("(none)");
            for (int i = 0; i < bursts.Count; i++)
            {
                Burst b = bursts[i];
                sb.AppendLine("burst " + (i + 1) + " at " + LineFormat.Sec(b.StartMs) + ": bundles=" + b.Bundles.Count + " size=" + LineFormat.Mb(b.Size)
                    + " took=" + LineFormat.Sec(b.EndMs - b.StartMs) + " commit=" + LineFormat.SignedMb(b.CommitGrowth) + " slow frames=" + b.Hitches);
                for (int j = 0; j < b.Bundles.Count; j++)
                {
                    BundleRecord r = b.Bundles[j];
                    sb.AppendLine("    " + LineFormat.Mb(r.Size).PadLeft(7) + " uses=" + r.AssetCalls.ToString().PadLeft(4) + "  " + r.Name + " | mod=" + r.Mod);
                }
            }
        }
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet run --project tests/FastLoad.Tests.csproj`
Expected: `N passed, 0 failed`.

- [ ] **Step 5: Read one report by eye**

Add this temporary line at the end of `ReportBuilderTests.Run()`, run the tests once, read the printed report for anything misleading or unreadable (column alignment, a label that does not say what the number is), fix it in `ReportBuilder.cs`, then remove the line:

```csharp
            System.Console.WriteLine(r);
```

- [ ] **Step 6: Check and commit**

Run: `bash tools/deploy.sh --check-only`
Expected: `runtime check ok` twice.

```bash
git add -A
git commit -m "Add report builder" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 9: Plugin, sampler, report job, deploy

**Files:**
- Create: `src/Sampler.cs`, `src/ReportJob.cs`
- Modify: `src/Plugin.cs`, `docs/PROJECT.md` (status table)

**Interfaces:**
- Consumes: `Timeline`, `Clock`, `Phases`, `Phase`, `FrameSources`, `BundleLedger`, `BundleHooks`, `Memory`, `RunFiles`, `LineFormat` (patcher); `FrameLog`, `MemLog`, `MemSample`, `QuietTracker`, `ReportInput`, `ReportBuilder` (plugin).
- Produces: the deployed mod. Timeline kinds added here: `HIT`, `SEC`, `SCN`, `PHS` (quiet). Files written at run time: `BepInEx/FastLoad/report_<stamp>.txt`, `BepInEx/config/ken.h3vr.fastload.cfg`.

No unit test: glue over Unity. Verified by build, runtime check, deploy, and Task 10.

- [ ] **Step 1: Write the report job**

`src/ReportJob.cs`:

```csharp
using System;
using System.IO;
using System.Threading;
using BepInEx.Bootstrap;
using FastLoad.Early;

namespace FastLoad
{
    /// <summary>
    /// Writes the report file. The snapshot is taken on the main thread (cheap copies); building the text and writing
    /// the file happens on a worker thread so the report never costs a frame.
    /// </summary>
    public static class ReportJob
    {
        public static float HitchMs = 25f;

        private static int s_busy;

        private static ReportInput Snapshot()
        {
            ReportInput inp = new ReportInput();
            inp.Profile = Timeline.Profile;
            inp.Args = Timeline.Args;
            inp.HookMode = BundleHooks.Mode;
            inp.RestartCostMs = Timeline.RestartCostMs;
            inp.NowMs = Clock.NowMs;
            inp.HitchMs = HitchMs;
            inp.PhaseAt = Phases.Snapshot();
            inp.LoaderBundleLines = Phases.LoaderBundleLines;
            inp.Bundles = BundleLedger.Snapshot();
            inp.Frames = FrameLog.Snapshot();
            inp.Mem = MemLog.Snapshot();
            try
            {
                inp.PluginCount = Chainloader.PluginInfos.Count;
            }
            catch (Exception)
            {
                inp.PluginCount = -1;
            }
            return inp;
        }

        /// <summary>Starts a background write unless one is still running.</summary>
        public static void Kick()
        {
            if (Interlocked.CompareExchange(ref s_busy, 1, 0) != 0) return;
            try
            {
                ReportInput inp = Snapshot();
                ThreadPool.QueueUserWorkItem(Work, inp);
            }
            catch (Exception e)
            {
                Interlocked.Exchange(ref s_busy, 0);
                Timeline.Write("ERR", "report snapshot failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>Writes on the calling thread (game is quitting: a worker might not get to finish).</summary>
        public static void WriteNow()
        {
            try
            {
                Save(Snapshot());
            }
            catch (Exception e)
            {
                Timeline.Write("ERR", "final report failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void Work(object state)
        {
            try
            {
                Save((ReportInput)state);
            }
            catch (Exception e)
            {
                Timeline.Write("ERR", "report write failed: " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                Interlocked.Exchange(ref s_busy, 0);
            }
        }

        private static readonly object s_fileGate = new object();

        private static void Save(ReportInput inp)
        {
            string text = ReportBuilder.Build(inp);
            string path = RunFiles.ReportPath(Timeline.Dir, Timeline.Stamp);
            string tmp = path + ".tmp";
            Monitor.Enter(s_fileGate);
            try
            {
                // Write beside it, then swap: a crash in the middle leaves the last good report in place.
                File.WriteAllText(tmp, text);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            finally
            {
                Monitor.Exit(s_fileGate);
            }
        }
    }
}
```

- [ ] **Step 2: Write the sampler**

`src/Sampler.cs`:

```csharp
using System;
using FastLoad.Early;
using UnityEngine;

namespace FastLoad
{
    /// <summary>
    /// Runs once per frame: measures the frame that just ended on the real clock, writes slow frames and one summary
    /// line per second, keeps the bundle hooks' async list moving, and starts the report every 10 s.
    /// </summary>
    public sealed class Sampler : MonoBehaviour
    {
        public float HitchMs = 25f;

        private const int ReportEveryMs = 10000;

        private double m_last = -1;
        private int m_secStart;
        private int m_secFrames;
        private double m_secWorst;
        private int m_nextReport;
        private bool m_off;
        private readonly QuietTracker m_quiet = new QuietTracker();

        private void Update()
        {
            if (m_off) return;
            try
            {
                Step();
            }
            catch (Exception e)
            {
                m_off = true;
                Timeline.Write("ERR", "sampler switched off: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private void Step()
        {
            double now = Clock.NowMsExact;
            int nowMs = (int)now;
            Timeline.SetFrame(Time.frameCount);
            BundleHooks.Sweep();

            if (m_last < 0)
            {
                m_secStart = nowMs;
                m_nextReport = nowMs + ReportEveryMs;
            }
            else
            {
                double ms = now - m_last;
                bool slow = ms >= HitchMs;
                FrameLog.Add(nowMs, (float)ms);
                m_secFrames++;
                if (ms > m_secWorst) m_secWorst = ms;

                if (slow)
                {
                    Timeline.Write("HIT", "frame=" + LineFormat.N0(ms) + "ms loading=" + BundleLedger.InFlight
                        + " [" + BundleLedger.InFlightNames(4) + "] logs=[" + FrameSources.TakeAndReset() + "]");
                }
                else
                {
                    FrameSources.Reset();
                }

                int quietSince = m_quiet.Step(nowMs, Phases.At(Phase.MenuLoaded), slow || BundleLedger.InFlight > 0);
                if (quietSince >= 0 && Phases.Mark(Phase.Quiet, quietSince))
                {
                    Timeline.Write("PHS", "quiet since " + quietSince);
                    ReportJob.Kick();
                }
            }
            m_last = now;

            if (nowMs - m_secStart >= 1000) Second(nowMs);

            if (nowMs >= m_nextReport)
            {
                m_nextReport = nowMs + ReportEveryMs;
                ReportJob.Kick();
            }
        }

        private void Second(int nowMs)
        {
            MemSample m = new MemSample();
            m.AtMs = nowMs;
            Memory.Read(out m.Commit, out m.WorkingSet, out m.SysFree, out m.SysLimit);
            MemLog.Add(m);

            double span = nowMs - m_secStart;
            double fps = span > 0 ? m_secFrames * 1000.0 / span : 0;
            Timeline.Write("SEC", "fps=" + LineFormat.N0(fps) + " worst=" + LineFormat.N0(m_secWorst) + "ms commit=" + LineFormat.Mb(m.Commit)
                + " ws=" + LineFormat.Mb(m.WorkingSet) + " sysfree=" + LineFormat.Mb(m.SysFree)
                + " mono=" + LineFormat.Mb(GC.GetTotalMemory(false)) + " bundles=" + BundleLedger.LoadedCount + " inflight=" + BundleLedger.InFlight);

            BundleHooks.CheckUnloads();
            m_secStart = nowMs;
            m_secFrames = 0;
            m_secWorst = 0;
        }

        private void OnApplicationQuit()
        {
            if (!Timeline.Enabled) return;
            Timeline.Write("RUN", "quit");
            ReportJob.WriteNow();
        }
    }
}
```

- [ ] **Step 3: Write the plugin**

Replace `src/Plugin.cs`:

```csharp
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
```

- [ ] **Step 4: Build and check**

Run: `bash tools/deploy.sh --check-only`
Expected: `Build succeeded`, tests `0 failed`, `runtime check ok` twice, no `FORBIDDEN WORD`.

If the runtime check reports a missing member, fix the call (Global Constraints) and re-run. Likely suspects in this task: none known; `File.Move`, `ThreadPool.QueueUserWorkItem(WaitCallback, object)`, `Interlocked.CompareExchange(ref int, int, int)` and `GC.GetTotalMemory(bool)` all exist in .NET 2.0.

- [ ] **Step 5: Confirm the plugin does not pull the patcher DLL into its own output**

Run: `ls bin/Release/`
Expected: `FastLoad.dll` (and `.pdb`); no `FastLoadPatcher.dll`, no `BepInEx.dll`, no `UnityEngine.dll`. If `FastLoadPatcher.dll` is there, the `<Private>false</Private>` on the `ProjectReference` in `FastLoad.csproj` is missing; a second copy under `plugins/` would be loaded as a second assembly with its own empty `Timeline`.

- [ ] **Step 6: Make sure the game is not running, then deploy**

Run: `tasklist | grep -i h3vr.exe || echo "not running"`
Expected: `not running`. If the game is running, stop here and ask Ken to close it; do not kill it.

Run: `bash tools/deploy.sh`
Expected last line: `deployed to new2`.

- [ ] **Step 7: Verify the deployed files**

Run:

```bash
P="/c/Users/Hp/AppData/Roaming/com.kesomannen.gale/h3vr/profiles/new2/BepInEx"
ls -la "$P/plugins/FastLoad" "$P/patchers/FastLoad"
cmp bin/Release/FastLoad.dll "$P/plugins/FastLoad/FastLoad.dll" && cmp patcher/bin/Release/FastLoadPatcher.dll "$P/patchers/FastLoad/FastLoadPatcher.dll" && echo "deployed files match the build"
```

Expected: one DLL in each folder, `deployed files match the build`.

- [ ] **Step 8: Update the status table and commit**

In `docs/PROJECT.md`, change the status cell of piece 1 from `spec written` to `built and deployed to new2 2026-10-12, not yet run in game`.

```bash
git add -A
git commit -m "Add plugin, frame sampler and report job; deploy probe to new2" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 10: Ken's run and reading the results

**Files:**
- Modify: `docs/PROJECT.md` (status + findings)
- Create: `docs/PROBE-RESULTS-<yyyy-mm-dd>.md`

**Interfaces:**
- Consumes: the deployed mod; the files it writes under `new2/BepInEx/FastLoad/`.
- Produces: the numbers that piece 2's design starts from.

This task needs Ken. Nothing before this point is called working.

- [ ] **Step 1: Hand Ken the test**

Tell Ken exactly this, and that the mod only measures:

1. Launch `new2` the usual way.
2. Stand in the main menu until it feels smooth, then about 15 s more.
3. Spawn one AR2 modular gun from the item spawner.
4. Load one T&H map, then go back to the main menu.
5. Quit the game from its own menu (not Alt+F4, so the final report is written).

- [ ] **Step 2: Find the run and check the probe itself worked**

Run:

```bash
D="/c/Users/Hp/AppData/Roaming/com.kesomannen.gale/h3vr/profiles/new2/BepInEx/FastLoad"
ls -la "$D"
R=$(ls -t "$D"/run_*.log | head -1); echo "$R"
grep -a -c "" "$R"
grep -a -E " (RUN|HKS|ERR|PHS) " "$R" | head -40
for k in LOG BUN HIT SEC SCN; do echo "$k $(grep -a -c " $k  " "$R")"; done
```

Expected: a `RUN` line with `restartOnLaunch=yes`; one `HKS` line reading `managed, 12 of 12 hooks on`; no `ERR` lines; `PHS` lines for the phases; non-zero counts for `LOG`, `BUN`, `SEC`, `SCN`. Any `ERR` line or a hook count below 12 is reported to Ken with the exact text before anything else.

Also confirm the game's own log shows the mod loaded and nothing new broke:

```bash
L="/c/Users/Hp/AppData/Roaming/com.kesomannen.gale/h3vr/profiles/new2/BepInEx/LogOutput.log"
grep -a -n -E "FastLoad" "$L" | head -20
```

Expected: `Loaded 1 patcher method from [FastLoadPatcher 1.0.0.0]`, `[Info   :FastLoad Patcher] Probe on: ...`, `Loading [FastLoad 0.1.0]`, `[Info   :  FastLoad] Probe on. ...`. No exception mentioning `FastLoad`.

Known failure shape to look for: the patcher line says `Probe on` but the plugin line says `Probe is off for this run (the preloader half did not start...)`, and the run file has `LOG`/`BUN` lines but no `HIT`/`SEC` lines. That means the game loaded `FastLoadPatcher.dll` a second time for the plugin, with its own empty `Timeline`. Fix: in `Entry.Initialize` store the live objects with `AppDomain.CurrentDomain.SetData("FastLoad.Timeline", ...)`, and have the plugin read them from there instead of from the static fields. Report it to Ken as a probe bug with that cause, fix, redeploy, and ask for one more run.

- [ ] **Step 3: Read the report against the spec's six questions**

Read the newest `report_*.txt` in full. For each question write the answer with its numbers, or "not answered" with the reason:

1. How long each start-up phase takes (section 2).
2. Which menu frames were slow and what was loading then (section 3, plus the `HIT` lines between the menu and quiet marks in the run file: group them by the names in `[...]` and `logs=[...]`).
3. Which bundles and mods cost the most time and memory (sections 4 and 5).
4. Which bundles were loaded and never used (section 6).
5. How close process and system came to the commit limit, and when (section 7).
6. What the AR2 spawn pulled in (section 8, the burst at the time Ken spawned it).

Check the cross-check line in section 1. If the loaders reported many more bundles than the hooks saw, say so, and propose one more run with `NativeHooks = true` (Ken decides; it adds native detours).

- [ ] **Step 4: Write the results down**

Create `docs/PROBE-RESULTS-<date>.md` with: the run file name, the six answers with numbers, anything the probe got wrong or could not see, and the three largest costs found. Facts only; no fix proposals mixed into the numbers (those belong to piece 2's design).

Update `docs/PROJECT.md`: piece 1 status to `done, results in docs/PROBE-RESULTS-<date>.md`.

- [ ] **Step 5: Commit and report to Ken**

```bash
git add -A
git commit -m "Record first probe run results" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

Report to Ken in a few lines: the six answers, then ask to start the design of piece 2 (menu-first scheduler).
