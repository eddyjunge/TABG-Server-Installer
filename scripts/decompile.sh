#!/usr/bin/env bash
# Decompile your own TABG client and dedicated server installs into browsable C# projects, for
# writing mods. Skips the Unity engine, Unity packages, Mono and .NET framework assemblies (Unity
# publishes those itself); everything else the game ships is decompiled.
#
#   scripts/decompile.sh [client|server|both]      (default: both)
#   scripts/decompile.sh --all [client|server|both] (also Unity/framework assemblies)
#
# Install paths default to Steam's; override with TABG_CLIENT_DIR / TABG_SERVER_DIR.
# Output: target/decompiled/{client,server}/<Assembly>/ (git-ignored).
# This is Landfall's code: read it and patch it with Harmony, but never commit or share it.
set -euo pipefail
cd "$(dirname "$0")/.."

ALL=0
if [[ "${1:-}" == "--all" ]]; then ALL=1; shift; fi
WHICH=${1:-both}

STEAM=${STEAM_DIR:-$HOME/.local/share/Steam/steamapps/common}
CLIENT=${TABG_CLIENT_DIR:-$STEAM/TotallyAccurateBattlegrounds}
SERVER=${TABG_SERVER_DIR:-$STEAM/TotallyAccurateBattlegroundsDedicatedServer}

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_ROLL_FORWARD=Major
if [ -d "$HOME/.dotnet" ]; then export DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"; fi
command -v ilspycmd >/dev/null || { echo "ilspycmd not found; install it with: dotnet tool install -g ilspycmd"; exit 1; }

SKIP='^(System|Mono|mscorlib|netstandard|Microsoft|Unity|UnityEngine|unity-)'

decompile() { # name, Managed dir
  local name=$1 managed=$2 out=target/decompiled/$1
  [[ -d "$managed" ]] || { echo "$name: no install at $managed (set TABG_${name^^}_DIR)"; return 1; }
  rm -rf "$out"; mkdir -p "$out"
  local dlls=()
  for f in "$managed"/*.dll; do
    b=$(basename "$f" .dll)
    if [[ $ALL == 1 ]] || ! [[ $b =~ $SKIP ]]; then dlls+=("$f"); fi
  done
  echo "$name: decompiling ${#dlls[@]} assemblies from $managed"
  printf '%s\0' "${dlls[@]}" | xargs -0 -P "$(nproc)" -I{} sh -c '
    b=$(basename "$1" .dll)
    if ilspycmd -p -r "$2" -o "$3/$b" "$1" >"$3/$b.log" 2>&1; then rm -f "$3/$b.log"; echo "  ok   $b"
    else echo "  FAIL $b (see $3/$b.log)"; fi' _ {} "$managed" "$out"
  echo "$name: $(find "$out" -name '*.cs' | wc -l) files in $out"
}

case $WHICH in
  client) decompile client "$CLIENT/TotallyAccurateBattlegrounds_Data/Managed" ;;
  server) decompile server "$SERVER/TABG_Data/Managed" ;;
  both)   decompile client "$CLIENT/TotallyAccurateBattlegrounds_Data/Managed"
          decompile server "$SERVER/TABG_Data/Managed" ;;
  *) echo "usage: $0 [--all] [client|server|both]"; exit 1 ;;
esac
