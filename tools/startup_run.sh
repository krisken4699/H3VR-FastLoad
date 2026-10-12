#!/bin/bash
# Start-up test without a person: launch the Gale profile new2, wait until the probe says the menu is quiet (or a
# time limit), close the game the normal way, print where the run's files are.
#   tools/startup_run.sh [max seconds to wait for quiet, default 600]
LIMIT="${1:-600}"
PROFILE="/c/Users/Hp/AppData/Roaming/com.kesomannen.gale/h3vr/profiles/new2/BepInEx"
DEVLOG="/c/Users/Hp/AppData/Roaming/com.kesomannen.gale/h3vr/profiles/dev/BepInEx/LogOutput.log"
D="$PROFILE/FastLoad"
running() { tasklist 2>/dev/null | grep -qi "h3vr.exe"; }

running && { echo "game already running: not started"; exit 1; }
MARK=$(mktemp)
DEV_BEFORE=$(stat -c %Y "$DEVLOG" 2>/dev/null || echo 0)
cmd //c "C:\\Users\\Hp\\Desktop\\launch_new.bat"
echo "launched $(date +%H:%M:%S)"

for i in $(seq 1 36); do running && break; sleep 5; done
running || { echo "h3vr.exe never appeared within 180 s"; exit 1; }
echo "game process seen $(date +%H:%M:%S)"

RUN=""
T0=$(date +%s)
WHY="time limit ${LIMIT}s"
while [ $(( $(date +%s) - T0 )) -lt "$LIMIT" ]; do
  sleep 5
  if ! running; then WHY="GAME PROCESS GONE (crash or restart that did not come back)"; sleep 8; running && { WHY="time limit ${LIMIT}s"; continue; }; break; fi
  # Newest run file that has a chainloader line (the start that handed over to a restart has none).
  for f in $(ls -t "$D"/run_*.log 2>/dev/null); do
    [ "$f" -nt "$MARK" ] && grep -a -q "Chainloader started" "$f" && { RUN="$f"; break; }
  done
  if [ -z "$RUN" ] && [ $(( $(date +%s) - T0 )) -gt 150 ]; then
    WHY="no FastLoad run file after 150 s"
    [ "$(stat -c %Y "$DEVLOG" 2>/dev/null || echo 0)" != "$DEV_BEFORE" ] && WHY="$WHY; the DEV profile's log changed instead: the launcher started dev, not new2"
    break
  fi
  if [ -n "$UNTIL" ]; then
    if [ -n "$RUN" ] && grep -a -q "$UNTIL" "$RUN"; then WHY="'$UNTIL' seen"; sleep 5; break; fi
  elif [ -n "$RUN" ] && grep -a -q " PHS  quiet since" "$RUN"; then WHY="quiet reached"; sleep 20; break; fi
done
echo "stopping: $WHY $(date +%H:%M:%S)"

if running; then
  taskkill //IM h3vr.exe >/dev/null 2>&1      # asks the window to close: the game quits normally
  for i in $(seq 1 12); do running || break; sleep 5; done
  if running; then echo "did not close in 60 s: forcing"; taskkill //F //IM h3vr.exe >/dev/null 2>&1; fi
fi
running && echo "GAME STILL RUNNING" || echo "game closed $(date +%H:%M:%S)"
echo "run file: ${RUN:-none}"
[ -n "$RUN" ] && echo "report:   $(echo "$RUN" | sed 's#/run_#/report_#; s#\.log$#.txt#')"
rm -f "$MARK"
