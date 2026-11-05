#!/usr/bin/env bash
# Run every check locally. There is no CI; run this before pushing.
#   scripts/check.sh            Rust + vehicle rules (no game files needed)
#   scripts/check.sh --mods     also build and test the C# client mods (needs TABG_CLIENT_DIR or mods/Local.props)
set -euo pipefail
cd "$(dirname "$0")/.."

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
[ -d "$HOME/.dotnet" ] && export PATH="$HOME/.dotnet:$PATH"

step() { printf '\n==> %s\n' "$*"; }

step "cargo fmt"
cargo fmt --all --check
step "cargo clippy"
cargo clippy --workspace --all-targets -- -D warnings
step "cargo test"
cargo test --workspace

step "vehicle rules"
dotnet test mods/tests/Vehicles.Tests -c Release

if [[ "${1:-}" == "--mods" ]]; then
  step "client mods build"
  dotnet build mods/Mods.sln -c Release
  step "client mods tests"
  dotnet test mods/tests/ClientMods.Tests -c Release --no-build
fi

printf '\nAll checks passed.\n'
