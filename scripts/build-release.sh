#!/usr/bin/env bash
# Build release binaries of tabg for Linux and Windows into target/dist/, with the map dumper
# plugin embedded. Local only (no CI): needs the .NET SDK and TABG_CLIENT_DIR for the plugin, and
# zig + cargo-zigbuild for the Windows cross build and the portable Linux build (`cargo install cargo-zigbuild`).
#
#   scripts/build-release.sh
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
[ -d "$HOME/.dotnet" ] && export PATH="$HOME/.dotnet:$PATH"
[ -d "$HOME/.local/bin" ] && export PATH="$HOME/.local/bin:$PATH"
: "${TABG_CLIENT_DIR:?set TABG_CLIENT_DIR to a TABG client with BepInEx (to build the dumper plugin)}"

DUMPER=mods/src/WorldDumper/bin/Release/netstandard2.0/Tabg.WorldDumper.dll
dotnet build -v q -c Release mods/src/WorldDumper >/dev/null || { dotnet build -c Release mods/src/WorldDumper | grep -E " error " | sort -u; exit 1; }
export TABG_EMBED_DUMPER="$PWD/$DUMPER"

VERSION=$(cargo metadata --no-deps --format-version 1 | python3 -c 'import sys,json; print(next(p["version"] for p in json.load(sys.stdin)["packages"] if p["name"]=="tabg"))')
DIST=target/dist
rm -rf "$DIST"; mkdir -p "$DIST"

# Linked against glibc 2.31 (zig), so it runs on older distros too (Ubuntu 20.04+, Debian 11+),
# not only on the glibc of the build machine.
cargo zigbuild --release -p tabg --target x86_64-unknown-linux-gnu.2.31
cp target/x86_64-unknown-linux-gnu/release/tabg "$DIST/tabg-$VERSION-linux-x86_64"

cargo zigbuild --release -p tabg --target x86_64-pc-windows-gnu
cp target/x86_64-pc-windows-gnu/release/tabg.exe "$DIST/tabg-$VERSION-windows-x86_64.exe"

(cd "$DIST" && sha256sum tabg-* > SHA256SUMS)
ls -l "$DIST"
cat "$DIST/SHA256SUMS"
