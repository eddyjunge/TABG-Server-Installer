#!/usr/bin/env bash
# Extract the assets of your own TABG install (UI, prefabs, scenes, models, textures, sounds,
# animations, shaders) with AssetRipper, for making UI and model mods. Code: scripts/decompile.sh.
#
#   scripts/extract-assets.sh [client|server] [unity|content|both]   (default: client both)
#
#   unity   -> target/assets/<side>-unity/    Unity 2021.3 project (prefabs, UI, scenes, materials);
#              open it in the Unity editor (2021.3.45f2) to inspect UI and build AssetBundles.
#   content -> target/assets/<side>-content/  plain files: models as .glb (Blender), .png, .ogg.
#
# Install paths default to Steam's; override with TABG_CLIENT_DIR / TABG_SERVER_DIR.
# AssetRipper (GPL-3.0) is downloaded once, sha256-pinned, to ~/.local/share/tabg/tools.
# This is Landfall's content: keep it local, never commit or share it. Needs ~10 GB per side.
set -euo pipefail
cd "$(dirname "$0")/.."

SIDE=${1:-client}; MODE=${2:-both}
STEAM=${STEAM_DIR:-$HOME/.local/share/Steam/steamapps/common}
case $SIDE in
  client) GAME=${TABG_CLIENT_DIR:-$STEAM/TotallyAccurateBattlegrounds} ;;
  server) GAME=${TABG_SERVER_DIR:-$STEAM/TotallyAccurateBattlegroundsDedicatedServer} ;;
  *) echo "usage: $0 [client|server] [unity|content|both]"; exit 1 ;;
esac
[[ $MODE =~ ^(unity|content|both)$ ]] || { echo "usage: $0 [client|server] [unity|content|both]"; exit 1; }
GAME=$(realpath "$GAME")
[[ -d $GAME ]] || { echo "no $SIDE install at $GAME"; exit 1; }

VER=2.0.0
SHA=6f5d4352d36795b944243eb3ec790ec37dbca6d6756e52605bc8616e042d384e
TOOL=$HOME/.local/share/tabg/tools/assetripper-$VER
BIN=$TOOL/AssetRipper.GUI.Free
if [[ ! -x $BIN ]]; then
  echo "downloading AssetRipper $VER"
  mkdir -p "$TOOL"; tmp=$(mktemp -d)
  curl -fsSL -o "$tmp/ar.tar.xz" "https://github.com/AssetRipper/AssetRipper/releases/download/$VER/AssetRipper_linux_x64.tar.xz"
  echo "$SHA  $tmp/ar.tar.xz" | sha256sum -c --quiet
  tar xf "$tmp/ar.tar.xz" -C "$TOOL"; rm -rf "$tmp"
fi

PORT=$(python3 -I -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1])')
mkdir -p target/assets
LOG=$PWD/target/assets/assetripper-$SIDE.log
(cd "$TOOL" && exec ./AssetRipper.GUI.Free --headless --port "$PORT" --log-path "$LOG") >/dev/null 2>&1 &
AR=$!
trap 'kill $AR 2>/dev/null || true' EXIT
URL=http://127.0.0.1:$PORT
for _ in $(seq 60); do curl -fs -o /dev/null "$URL/" && break; sleep 0.5; done

post() { curl -fs -o /dev/null -X POST --data-urlencode "path=$2" "$URL/$1"; }
echo "$SIDE: loading $GAME"
post LoadFolder "$GAME"
export_to() { # endpoint, dir
  local out=$PWD/target/assets/$SIDE-$2
  rm -rf "$out"; echo "$SIDE: exporting $out (takes minutes)"
  post "$1" "$out"
  echo "$SIDE: $(du -sh "$out" | cut -f1) in $out"
}
[[ $MODE != content ]] && export_to Export/UnityProject unity
[[ $MODE != unity ]] && export_to Export/PrimaryContent content
echo "log: $LOG"
