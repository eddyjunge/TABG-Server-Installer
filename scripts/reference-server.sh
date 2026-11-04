#!/usr/bin/env bash
# Run Landfall's own TABG dedicated server OFFLINE and join it with tabg-probe, to capture ground
# truth for our protocol code. Linux only; needs unprivileged user namespaces (no root).
#
#   scripts/reference-server.sh /path/to/TotallyAccurateBattlegroundsDedicatedServer [out-dir]
#
# The server runs in its own network namespace with only loopback: it cannot reach the internet,
# so it never appears in Landfall's server list and never talks to Epic/Unity. Your install is not
# modified; the needed files are copied (reflinked where possible) into the output directory.
# Captured payloads land in <out-dir>/fixtures-*; they contain map data, so don't commit them.
set -euo pipefail

if [[ "${1:-}" == "--inside" ]]; then
  D=$2; OUT=$3; PROBE=$4
  ip link set lo up
  printf '127.0.0.1 localhost\n127.0.0.1 ipv4.icanhazip.com\n::1 localhost\n' > "$OUT/hosts"
  mount --bind "$OUT/hosts" /etc/hosts
  # Resolve names from /etc/hosts only (systemd-resolved is reachable even without a network).
  grep -v '^hosts:' /etc/nsswitch.conf > "$OUT/nsswitch.conf"; echo 'hosts: files' >> "$OUT/nsswitch.conf"
  mount --bind "$OUT/nsswitch.conf" /etc/nsswitch.conf
  # The server looks up its public IP at startup and aborts if that fails: answer it locally.
  python3 -I -c '
import http.server
class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200); self.end_headers(); self.wfile.write(b"127.0.0.1\n")
    def log_message(self, *a): pass
http.server.HTTPServer(("127.0.0.1", 80), H).serve_forever()' >/dev/null 2>&1 &
  HP=$!
  trap 'kill $HP ${SP:-} 2>/dev/null || true' EXIT
  cd "$D"
  LD_LIBRARY_PATH="$D/TABG_Data/Plugins:$D/TABG_Data/Plugins/x86_64" ./TABG.exe -batchmode -nographics -logFile "$OUT/server.log" >/dev/null 2>&1 &
  SP=$!
  for _ in $(seq 1 240); do
    grep -q "Started ENET Host" "$OUT/server.log" 2>/dev/null && break
    kill -0 $SP 2>/dev/null || { echo "reference server exited, see $OUT/server.log"; exit 1; }
    sleep 1
  done
  "$PROBE" --port 7777 --name probe-a --seconds 12 --dump "$OUT/fixtures-a" > "$OUT/probe-a.txt" 2>&1 &
  PA=$!
  sleep 3
  "$PROBE" --port 7777 --name probe-b --seconds 7 --dump "$OUT/fixtures-b" > "$OUT/probe-b.txt" 2>&1 || true
  wait $PA || true
  kill $SP 2>/dev/null || true; sleep 1; kill -9 $SP 2>/dev/null || true
  exit 0
fi

SRC=${1:?usage: $0 /path/to/TotallyAccurateBattlegroundsDedicatedServer [out-dir]}
OUT=$(realpath -m "${2:-target/reference-run}")
cd "$(dirname "$0")/.."
cargo build -q --release -p tabg-probe
rm -rf "$OUT"; mkdir -p "$OUT/server"
cp -a --reflink=auto "$SRC/TABG.exe" "$SRC/UnityPlayer.so" "$SRC/TABG_Data" "$OUT/server/"
cat > "$OUT/server/game_settings.txt" <<'SETTINGS'
ServerName=tabg reference
ServerDescription=offline reference server
Port=7777
Password=
MaxPlayers=8
Relay=false
GameMode=BattleRoyale
TeamMode=SQUAD
UseTimedForceStart=false
PlayersToStart=4
AntiCheat=false
SETTINGS
unshare -rnm bash "$0" --inside "$OUT/server" "$OUT" "$PWD/target/release/tabg-probe"
echo "== probe-a"; cat "$OUT/probe-a.txt"
echo "== probe-b"; cat "$OUT/probe-b.txt"
echo "server log: $OUT/server.log   captured payloads: $OUT/fixtures-a, $OUT/fixtures-b"
