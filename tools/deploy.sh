#!/bin/bash
# Build, run the unit tests, check both DLLs against the game's own .NET, deploy to the Gale profile new2.
# Refuses to deploy if anything fails.   --check-only: everything except the copy.
set -e
set -o pipefail
cd "$(dirname "$0")/.."
PROFILE="/c/Users/Hp/AppData/Roaming/com.kesomannen.gale/h3vr/profiles/new2/BepInEx"

B=$(dotnet build -c Release patcher/FastLoadPatcher.csproj 2>&1; dotnet build -c Release FastLoad.csproj 2>&1; dotnet build -c Release helper/FastLoadHelper.csproj 2>&1) || true
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
cp helper/bin/Release/FastLoadHelper.exe "$PD/"
echo "deployed to new2"
