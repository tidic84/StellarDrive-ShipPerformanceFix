#!/usr/bin/env bash
# Sends dev commands to the running game and shows the new log lines.
# Usage: [ROLE=host|client] [MOD=build|anchor|factory|logistics|rooms] [WAIT=2] ./cmd.sh "cmd 1" "cmd 2" ...
GAME="/c/Program Files (x86)/Steam/steamapps/common/StellarDrive Demo"
MOD="${MOD:-build}"
SUFFIX="${ROLE:+_$ROLE}"
if [ -n "$ROLE" ]; then LOG=$(ls -1t --time=birth "$GAME"/MelonLoader/Logs/*.log | sed -n "$([ "$ROLE" = host ] && echo 2 || echo 1)p"); else LOG="$GAME/MelonLoader/Latest.log"; fi
before=$(wc -l < "$LOG")
printf '%s\n' "$@" > "$GAME/UserData/stellar_${MOD}${SUFFIX}_cmd.txt"
sleep "${WAIT:-2}"
tail -n +$((before+1)) "$LOG" | grep -E "\[(Stellar[A-Za-z]*|ShipPerformanceFix)\]|Exception|rror" | cut -c1-400
