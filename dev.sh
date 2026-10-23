#!/usr/bin/env bash
# Dev loop (WSL): build with DEV, deploy, (re)start the game and open a world (WORLD, default "TF Mother Ship").
set -e
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
GAME="/mnt/c/Program Files (x86)/Steam/steamapps/common/StellarDrive Demo"
LOG="$GAME/MelonLoader/Latest.log"
cd "$(dirname "$0")"
# Never close a game the player has open: only with FORCE=1 (once they have handed it over).
if tasklist.exe 2>/dev/null | grep -qi "StellarDrive.exe"; then
  if [ "${FORCE:-0}" != "1" ]; then echo "StellarDrive is running: close it or rerun with FORCE=1"; exit 2; fi
  taskkill.exe /IM StellarDrive.exe /F > /dev/null 2>&1 || true
fi
dotnet build src/ShipPerformanceFix/ShipPerformanceFix.csproj -c Release -p:DefineConstants=DEV -p:GamePath="$GAME" -nologo -v q 2>&1 | grep -E "rror CS" && exit 1
sleep 2
cp bin/Release/ShipPerformanceFix.dll "$GAME/Mods/"
rm -f "$LOG"
(cd "$GAME" && cmd.exe /c start "" StellarDrive.exe < /dev/null > /dev/null 2>&1 &)
for i in $(seq 1 120); do [ -f "$LOG" ] && grep -q "Backup menus ready" "$LOG" && break; sleep 1; done
sleep 3
printf 'load %s\n' "${WORLD:-TF Mother Ship}" > "$GAME/UserData/stellar_rooms_cmd.txt"
for i in $(seq 1 240); do grep -q "SafetyRails\] world loaded" "$LOG" && break; sleep 1; done
sleep 15
grep -aE "ShipPerformanceFix.*(Exception|rror)" "$LOG" | tail -5 || true
echo loaded
