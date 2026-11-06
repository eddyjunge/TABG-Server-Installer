#!/usr/bin/env bash
# Compile the mod shaders (mods/unity/Assets/Bundles/<name>/) into Windows asset bundles
# (target/bundles/<name>) with the Unity editor TABG itself uses (2021.3.45f2), headless.
# Client mods embed these bundles (e.g. mods/src/Visuals embeds target/bundles/visuals).
#
#   scripts/build-shaders.sh
#
# First run downloads the editor (~3.3 GB) + Windows build support (~0.6 GB), md5-checked against
# Unity's release API, to ~/.local/share/tabg/tools/unity-2021.3.45f2 (UNITY_EDITOR overrides).
# The editor needs a Unity account licence (free Personal is fine): sign in once with Unity Hub
# on this machine, or see the error printed below.
set -euo pipefail
cd "$(dirname "$0")/.."

VER=2021.3.45f2; CS=88f88f591b2e
ROOT=$HOME/.local/share/tabg/tools/unity-$VER
UNITY=${UNITY_EDITOR:-$ROOT/Editor/Unity}

if [[ ! -x $UNITY ]]; then
  mkdir -p "$ROOT/dl"
  base=https://download.unity3d.com/download_unity/$CS
  # md5s from https://services.api.unity.com/unity/editor/release/v1/releases?version=$VER
  get() { # url, file, md5
    if [[ ! -f "$ROOT/dl/$2" ]] || ! echo "$3  $ROOT/dl/$2" | md5sum -c --quiet 2>/dev/null; then
      echo "downloading $2"; curl -fL -C - -o "$ROOT/dl/$2" "$1"
      echo "$3  $ROOT/dl/$2" | md5sum -c --quiet
    fi
  }
  get "$base/LinuxEditorInstaller/Unity-$VER.tar.xz" Unity.tar.xz 2362c3fff9c2740f3baa4ff9612a1cbd
  get "$base/MacEditorTargetInstaller/UnitySetup-Windows-Mono-Support-for-Editor-$VER.pkg" Windows-Mono.pkg 442ad6b756dc1385405555abe5916928
  echo "unpacking editor"
  tar -xJf "$ROOT/dl/Unity.tar.xz" -C "$ROOT"
  # The Windows module ships as a macOS .pkg for every OS: a xar archive with a cpio Payload.
  W=$ROOT/Editor/Data/PlaybackEngines/WindowsStandaloneSupport
  mkdir -p "$W"; tmp=$(mktemp -d)
  bsdtar -xf "$ROOT/dl/Windows-Mono.pkg" -C "$tmp" TargetSupport.pkg.tmp/Payload
  (cd "$W" && bsdtar -xf "$tmp/TargetSupport.pkg.tmp/Payload")
  rm -rf "$tmp"
fi

# Newer distros dropped libxml2.so.2, which the 2021.3 editor links against. Put a copy in
# $ROOT/lib (on Arch: bsdtar -xf the libxml2-legacy package, usr/lib/libxml2.so.2*).
export LD_LIBRARY_PATH="$ROOT/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
if ldd "$UNITY" | grep -q 'libxml2.so.2 => not found'; then
  echo "Unity needs libxml2.so.2: install your distro's legacy libxml2 (Arch: libxml2-legacy) or copy it into $ROOT/lib"
  exit 1
fi

# bee_backend (Unity's build backend) with --stdin-canary finishes its work but then never exits
# here: it waits for stdin to close while Unity waits for it to exit. Wrap it without that flag.
BEE=$(dirname "$UNITY")/Data/bee_backend
if [[ ! -e $BEE.real ]]; then
  mv "$BEE" "$BEE.real"
  cat > "$BEE" <<'WRAP'
#!/usr/bin/env bash
# tabg: drop --stdin-canary (deadlocks on exit on current Linux), see scripts/build-shaders.sh
args=(); for a in "$@"; do [[ $a == --stdin-canary || $a == -c ]] || args+=("$a"); done
exec "$0.real" "${args[@]}"
WRAP
  chmod +x "$BEE"
fi

# The editor build script is compiled with dotnet and dropped in as a precompiled DLL, so the
# Unity project itself has no .cs files to compile.
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
if [ -d "$HOME/.dotnet" ]; then export DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH"; fi
dotnet build -v q -c Release -p:UnityManaged="$(dirname "$UNITY")/Data/Managed" mods/unity/EditorTools >/dev/null \
  || { dotnet build -c Release mods/unity/EditorTools | grep -E " error " | sort -u; exit 1; }
mkdir -p mods/unity/Assets/Editor
cp mods/unity/EditorTools/bin/Release/netstandard2.0/TabgMods.EditorTools.dll mods/unity/Assets/Editor/

OUT=$PWD/target/bundles
LOG=$PWD/target/unity-build-shaders.log
mkdir -p "$OUT"
echo "building bundles with Unity $VER (log: $LOG)"
set +e
"$UNITY" -batchmode -nographics -quit -projectPath "$PWD/mods/unity" -buildTarget Win64 \
  -executeMethod TabgMods.BuildBundles.Build -tabgOut "$OUT" -logFile "$LOG"
rc=$?
set -e
grep -E '^\[tabg\]|Shader error|error CS|No valid Unity Editor license|License' "$LOG" | sort -u || true
if [[ $rc -ne 0 ]]; then
  if grep -qiE 'licen[cs]e' "$LOG"; then
    echo "Unity needs a licence (free Personal is fine). Open Unity Hub, sign in, then"
    echo "Preferences > Licenses > Add > Get a free personal license, and re-run this script."
    echo "Unity Hub without installing: download UnityHubSetup-*-amd64.deb from"
    echo "https://hub.unity3d.com/linux/repos/deb/, unpack it with bsdtar and run usr/lib/unityhub/unityhub."
  fi
  echo "Unity failed (exit $rc), see $LOG"; exit $rc
fi
ls -la "$OUT"
