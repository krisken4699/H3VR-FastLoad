#!/bin/bash
# Scripted in-game test without a person: puts a step list where the plugin looks for it, runs the game until the
# script says it is done (or the time limit), closes the game, takes the step list away again.
#   tools/test_run.sh <script file> [max seconds, default 900]
# Steps: see src/AutoTest.cs. Results: " TST " lines in the run log, census_*.txt, items.txt, sosigs.txt.
cd "$(dirname "$0")/.."
SCRIPT="$1"
[ -f "$SCRIPT" ] || { echo "no such script: $SCRIPT"; exit 1; }
DEST="/c/Users/Hp/AppData/Roaming/com.kesomannen.gale/h3vr/profiles/new2/BepInEx/FastLoad/autotest.txt"
cp "$SCRIPT" "$DEST"
UNTIL=" TST  script done" bash tools/startup_run.sh "${2:-900}"
rm -f "$DEST"
echo "autotest script removed from the profile"
