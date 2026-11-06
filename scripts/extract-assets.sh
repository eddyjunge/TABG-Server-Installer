#!/usr/bin/env bash
# Extract the assets of your own TABG install (UI, prefabs, scenes, models, textures, sounds,
# animations, shaders) with AssetRipper, for making UI and model mods. Code: scripts/decompile.sh.
#
#   scripts/extract-assets.sh [client|server] [unity|content|sounds|shaders|all]   (default: client all)
#
#   unity   -> target/assets/<side>-unity/    Unity 2021.3 project (prefabs, UI, scenes, materials);
#              open it in the Unity editor (2021.3.45f2) to inspect UI and build AssetBundles.
#   content -> target/assets/<side>-content/  plain files: models as .glb (Blender), .png, .ogg.
#   sounds  -> target/assets/<side>-sounds/<Bank>/*.wav  the FMOD banks (gunshots, hits, vehicles,
#              ambience, UI, catchphrases), which Unity/AssetRipper can't see; via vgmstream.
#   shaders -> target/assets/<side>-shaders/**/*.shader  every shader decompiled from its compiled
#              D3D11 bytecode to ShaderLab + HLSL, via USCSandbox (GPL-3.0) built from source with
#              scripts/patches/uscsandbox.patch. The Unity project above only has placeholder
#              shaders (AssetRipper's decompiler is paid). Output is machine-like HLSL (tmp0, tmp1,
#              ...) per variant: a starting point for your own shaders, not compile-ready source.
#
# Install paths default to Steam's; override with TABG_CLIENT_DIR / TABG_SERVER_DIR.
# AssetRipper (GPL-3.0) and vgmstream (ISC) are downloaded once, sha256-pinned, and USCSandbox
# is cloned at a pinned commit, to ~/.local/share/tabg/tools. Shaders need the .NET 8 SDK.
# This is Landfall's content: keep it local, never commit or share it. Needs ~10 GB per side.
set -euo pipefail
cd "$(dirname "$0")/.."

SIDE=${1:-client}; MODE=${2:-all}
STEAM=${STEAM_DIR:-$HOME/.local/share/Steam/steamapps/common}
case $SIDE in
  client) GAME=${TABG_CLIENT_DIR:-$STEAM/TotallyAccurateBattlegrounds} ;;
  server) GAME=${TABG_SERVER_DIR:-$STEAM/TotallyAccurateBattlegroundsDedicatedServer} ;;
  *) echo "usage: $0 [client|server] [unity|content|sounds|shaders|all]"; exit 1 ;;
esac
[[ $MODE =~ ^(unity|content|sounds|shaders|all)$ ]] || { echo "usage: $0 [client|server] [unity|content|sounds|shaders|all]"; exit 1; }
GAME=$(realpath "$GAME")
[[ -d $GAME ]] || { echo "no $SIDE install at $GAME"; exit 1; }

fetch() { # dir, url, sha256, archive-name, unpack-command
  [[ -d $1 ]] && return 0
  echo "downloading $(basename "$1")"
  local tmp; tmp=$(mktemp -d)
  curl -fsSL -o "$tmp/$4" "$2"
  echo "$3  $tmp/$4" | sha256sum -c --quiet
  mkdir -p "$1.part"; (cd "$1.part" && $5 "$tmp/$4"); rm -rf "$tmp"; mv "$1.part" "$1"
}

if [[ $MODE == sounds || $MODE == all ]]; then
  VG=$HOME/.local/share/tabg/tools/vgmstream-r2117
  fetch "$VG" https://github.com/vgmstream/vgmstream/releases/download/r2117/vgmstream-linux.zip \
    2f98c77f756079f63fbd119939067f1ed461d77e70993bc4cc372736d859c84a v.zip "unzip -q"
  chmod +x "$VG/vgmstream-cli"
  OUT=target/assets/$SIDE-sounds; rm -rf "$OUT"
  n=0
  while IFS= read -r -d '' bank; do
    [[ $bank == *.strings.bank ]] && continue
    name=$(basename "$bank" .bank); [[ $bank == */FmodBanks/* ]] && name="FmodBanks-$name"
    mkdir -p "$OUT/$name"
    "$VG/vgmstream-cli" -S 0 -i -o "$OUT/$name/?n.wav" "$bank" >/dev/null 2>&1 || echo "  FAIL $name"
    c=$(find "$OUT/$name" -name '*.wav' | wc -l); n=$((n + c)); echo "  $name: $c sounds"
  done < <(find "$GAME" -name '*.bank' -print0 | sort -z)
  echo "$SIDE: $n sounds, $(du -sh "$OUT" 2>/dev/null | cut -f1) in $OUT"
  [[ $MODE == sounds ]] && exit 0
fi

if [[ $MODE == shaders || $MODE == all ]]; then
  export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
  if [ -d "$HOME/.dotnet" ]; then export DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH"; fi
  command -v dotnet >/dev/null || { echo "shaders need the .NET 8 SDK (dotnet)"; exit 1; }
  USC_REV=044bc319866ca2f7a570942cb55d74458de7e2c0
  USC=$HOME/.local/share/tabg/tools/uscsandbox-${USC_REV:0:7}
  PATCH=$PWD/scripts/patches/uscsandbox.patch
  STAMP=$(sha256sum "$PATCH" | cut -c1-16)
  if [[ ! -f $USC/.built-$STAMP ]]; then
    echo "building USCSandbox ${USC_REV:0:7} (+ tabg patch)"
    rm -rf "$USC"
    git clone -q https://github.com/nesrak1/USCSandbox "$USC"
    git -C "$USC" checkout -q "$USC_REV"
    git -C "$USC" apply "$PATCH"
    dotnet build -v q -c Release "$USC/USCSandbox/USCSandbox.csproj" >/dev/null \
      || { dotnet build -c Release "$USC/USCSandbox/USCSandbox.csproj" | grep -E " error " | sort -u; exit 1; }
    touch "$USC/.built-$STAMP"
  fi
  OUT=$PWD/target/assets/$SIDE-shaders; rm -rf "$OUT"
  WORK=$(mktemp -d); cp "$USC/Files/classdata.tpk" "$WORK/"
  DLL=$USC/USCSandbox/bin/Release/net8.0/USCSandbox.dll
  ok=0; bad=0
  while IFS= read -r -d '' f; do
    log=$(cd "$WORK" && dotnet "$DLL" null "$f" --all 2>&1 || true)
    ok=$((ok + $(grep -c ' decompiled$' <<<"$log" || true)))
    bad=$((bad + $(grep -c ' FAILED' <<<"$log" || true)))
    grep ' FAILED' <<<"$log" | sed 's/^/  /' || true
  done < <(find "$GAME" -path '*_Data/*' \( -name '*.assets' -o -name 'level[0-9]*' \) ! -name '*.resS' -print0 | sort -z)
  if [[ -d $WORK/out ]]; then mv "$WORK/out" "$OUT"; else mkdir -p "$OUT"; fi
  rm -rf "$WORK"
  echo "$SIDE: $ok shaders decompiled, $bad failed, $(du -sh "$OUT" | cut -f1) in $OUT"
  [[ $MODE == shaders ]] && exit 0
fi

VER=2.0.0
SHA=6f5d4352d36795b944243eb3ec790ec37dbca6d6756e52605bc8616e042d384e
TOOL=$HOME/.local/share/tabg/tools/assetripper-$VER
fetch "$TOOL" "https://github.com/AssetRipper/AssetRipper/releases/download/$VER/AssetRipper_linux_x64.tar.xz" \
  "$SHA" ar.tar.xz "tar xf"

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
if [[ $MODE != content ]]; then export_to Export/UnityProject unity; fi
if [[ $MODE != unity ]]; then export_to Export/PrimaryContent content; fi
echo "log: $LOG"
