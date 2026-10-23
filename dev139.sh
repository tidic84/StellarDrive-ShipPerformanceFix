#!/usr/bin/env bash
# Same as dev.sh, on another PC over SSH (HOST, default tidic@192.168.10.139): build with DEV, deploy, start the game
# in the logged-in Windows session (scheduled task) and open WORLD (default SPF_Test).
set -e
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
HOST="${HOST:-tidic@192.168.10.139}"
RGAME='C:\Program Files (x86)\Steam\steamapps\common\StellarDrive Demo'
cd "$(dirname "$0")"
dotnet build src/ShipPerformanceFix/ShipPerformanceFix.csproj -c Release -p:DefineConstants=DEV -p:GamePath="/mnt/c/Program Files (x86)/Steam/steamapps/common/StellarDrive Demo" -nologo -v q 2>&1 | grep -E "rror CS" && exit 1
ssh "$HOST" "taskkill /IM StellarDrive.exe /F >nul 2>&1 & exit 0"
sleep 3
scp -q bin/Release/ShipPerformanceFix.dll "$HOST:/C:/Program Files (x86)/Steam/steamapps/common/StellarDrive Demo/Mods/"
ssh "$HOST" "del \"$RGAME\\MelonLoader\\Latest.log\" >nul 2>&1 & schtasks /Create /F /TN StellarDev /SC ONCE /ST 23:59 /IT /TR \"\\\"$RGAME\\StellarDrive.exe\\\"\" >nul && schtasks /Run /TN StellarDev >nul"
log() { ssh "$HOST" "type \"$RGAME\\MelonLoader\\Latest.log\" 2>nul"; }
for i in $(seq 1 60); do log | grep -q "Backup menus ready\|ShipPerformanceFix ready" && break; sleep 3; done
sleep 8
ssh "$HOST" "echo load ${WORLD:-SPF_Test}> \"$RGAME\\UserData\\stellar_rooms_cmd.txt\""
for i in $(seq 1 120); do log | grep -q "SafetyRails\] world loaded" && break; sleep 3; done
log | grep -aE "ShipPerformanceFix.*(Exception|rror)|world loaded" | tail -3
