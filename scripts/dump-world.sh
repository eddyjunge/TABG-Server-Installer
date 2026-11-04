#!/usr/bin/env bash
# Export map data (v0: spawn points) from your own TABG dedicated server install, using the
# Tabg.WorldDumper BepInEx plugin. Runs offline in a network namespace; your install is not modified.
#
#   scripts/dump-world.sh /path/to/TotallyAccurateBattlegroundsDedicatedServer [out.json]
#
# Needs BepInEx 5 (core/ + doorstop_libs/) in that install, and TABG_CLIENT_DIR set to build the
# plugin. The output is derived from Landfall's map files: keep it local, don't commit it.
set -euo pipefail

if [[ "${1:-}" == "--inside" ]]; then
  D=$2; OUT=$3
  ip link set lo up
  # The map only loads after startup succeeds, and startup needs an answer to its public-IP lookup.
  printf '127.0.0.1 localhost\n127.0.0.1 ipv4.icanhazip.com\n' > "$D/hosts"
  mount --bind "$D/hosts" /etc/hosts
  grep -v '^hosts:' /etc/nsswitch.conf > "$D/nsswitch.conf"; echo 'hosts: files' >> "$D/nsswitch.conf"
  mount --bind "$D/nsswitch.conf" /etc/nsswitch.conf
  python3 -I -c '
import http.server
class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200); self.end_headers(); self.wfile.write(b"127.0.0.1\n")
    def log_message(self, *a): pass
http.server.HTTPServer(("127.0.0.1", 80), H).serve_forever()' >/dev/null 2>&1 &
  HP=$!
  trap 'kill $HP 2>/dev/null || true' EXIT
  cd "$D"
  export DOORSTOP_ENABLE=TRUE DOORSTOP_INVOKE_DLL_PATH="$D/BepInEx/core/BepInEx.Preloader.dll" DOORSTOP_CORLIB_OVERRIDE_PATH=""
  export LD_PRELOAD="$D/doorstop_libs/libdoorstop_x64.so" LD_LIBRARY_PATH="$D/doorstop_libs:$D/TABG_Data/Plugins:$D/TABG_Data/Plugins/x86_64"
  export TABG_WORLD_OUT="$OUT" TABG_WORLD_QUIT=1
  timeout 300 ./TABG.exe -batchmode -nographics -logFile "$D/server.log" >/dev/null 2>&1 || true
  exit 0
fi

SRC=${1:?usage: $0 /path/to/TotallyAccurateBattlegroundsDedicatedServer [out.json]}
OUT=$(realpath -m "${2:-target/tabg-world.json}")
cd "$(dirname "$0")/.."
for need in "$SRC/BepInEx/core/BepInEx.Preloader.dll" "$SRC/doorstop_libs/libdoorstop_x64.so"; do
  [[ -e "$need" ]] || { echo "missing $need (install BepInEx 5 for Linux into the server first)"; exit 1; }
done
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
[ -d "$HOME/.dotnet" ] && export PATH="$HOME/.dotnet:$PATH"
dotnet build -v q -c Release mods/src/WorldDumper >/dev/null || { dotnet build -c Release mods/src/WorldDumper | grep -E " error " | sort -u; exit 1; }

W=target/world-dump/server
rm -rf "$W"; mkdir -p "$W/BepInEx/plugins" "$(dirname "$OUT")"
cp -a --reflink=auto "$SRC/TABG.exe" "$SRC/UnityPlayer.so" "$SRC/TABG_Data" "$SRC/doorstop_libs" "$W/"
cp -a --reflink=auto "$SRC/BepInEx/core" "$W/BepInEx/"
cp mods/src/WorldDumper/bin/Release/netstandard2.0/Tabg.WorldDumper.dll "$W/BepInEx/plugins/"
rm -f "$OUT"
unshare -rnm bash "$0" --inside "$(realpath "$W")" "$OUT"
if [[ -s "$OUT" ]]; then
  echo "wrote $OUT ($(grep -c '"group"' "$OUT") spawn points)"
else
  echo "no output; see $W/server.log and $W/BepInEx/LogOutput.log"; exit 1
fi
