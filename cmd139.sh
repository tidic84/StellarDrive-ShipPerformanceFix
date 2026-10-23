#!/usr/bin/env bash
# cmd.sh for the remote PC: sends dev commands, then shows the new ShipPerformanceFix lines, after WAIT seconds or,
# with UNTIL set, as soon as a new line matches that pattern (at most 180 s).
HOST="${HOST:-tidic@192.168.10.139}"
RGAME='C:\Program Files (x86)\Steam\steamapps\common\StellarDrive Demo'
log() { ssh "$HOST" "type \"$RGAME\\MelonLoader\\Latest.log\" 2>nul"; }
before=$(log | wc -l)
printf '%s\r\n' "$@" | ssh "$HOST" "more > \"$RGAME\\UserData\\stellar_rooms_cmd.txt\""
if [ -n "$UNTIL" ]; then
  for i in $(seq 1 90); do log | tail -n +$((before+1)) | grep -qa "$UNTIL" && break; sleep 2; done
else
  sleep "${WAIT:-2}"
fi
log | tail -n +$((before+1)) | grep -aE "\[ShipPerformanceFix\]|Exception" | cut -c1-300
