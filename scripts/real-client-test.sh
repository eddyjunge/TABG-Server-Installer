#!/usr/bin/env bash
# Join our server with the REAL TABG client, automatically, and collect what happens.
#
#   scripts/real-client-test.sh [seconds]          (default 90)
#
# Uses a separate test copy of your modded client (btrfs reflink, so it costs ~nothing), with only
# AntiCheatBootErrorRemover + the DevAutoJoin plugin installed. Your own copies are not modified.
#   TABG_CLIENT_DIR   modded client with BepInEx (source of the test copy)
#   TABG_TEST_CLIENT  where the test copy lives (default ~/.local/share/tabg/test-client)
#   TABG_WORLD        world file for the server (default target/tabg-world.json)
#   TABG_TEST_PLUGINS extra client plugin DLLs to install (space-separated paths)
#   TABG_TEST_SERVER_ARGS extra tabg-server flags (e.g. "--loadout 160:1,6:240")
#   TABG_TEST_HEAL    item id the client heals with once it's hurt (e.g. 131 = bandage)
#   TABG_TEST_DROP    "item:count@seconds" the client drops (e.g. "132:1@80")
#   TABG_TEST_WINDOW=1 show the game window and play sound (default: no window, no sound, no
#                     screenshots: Unity -batchmode -nographics, Wine audio off)
#   TABG_TEST_RELAY=1 host through Unity Relay and join by code (needs internet) instead of 127.0.0.1
set -euo pipefail
cd "$(dirname "$0")/.."
SECONDS_TO_RUN=${1:-90}
SRC=${TABG_CLIENT_DIR:?set TABG_CLIENT_DIR to your modded TABG copy}
TEST=${TABG_TEST_CLIENT:-$HOME/.local/share/tabg/test-client}
WORLD=${TABG_WORLD:-target/tabg-world.json}
STEAM_ROOT="$HOME/.local/share/Steam"
PROTON="$STEAM_ROOT/steamapps/common/Proton - Experimental/proton"
OUT=target/real-client-test
[[ -x "$PROTON" ]] || { echo "Proton Experimental not found at $PROTON"; exit 1; }
pgrep -x steam >/dev/null || { echo "start Steam first"; exit 1; }

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
[ -d "$HOME/.dotnet" ] && export PATH="$HOME/.dotnet:$PATH"
dotnet build -v q -c Release mods/src/DevAutoJoin >/dev/null
cargo build -q --release -p tabg-server

if [[ ! -e "$TEST/TotallyAccurateBattlegrounds.exe" ]]; then
  echo "creating test client copy at $TEST"
  mkdir -p "$TEST"
  cp -a --reflink=auto "$SRC"/. "$TEST"/
fi
rm -rf "$TEST/BepInEx/plugins" "$TEST/BepInEx/config" "$TEST/BepInEx/LogOutput.log" "$TEST"/BepInEx/autojoin-*.png
mkdir -p "$TEST/BepInEx/plugins"
[[ -e "$SRC/BepInEx/plugins/AntiCheatBootErrorRemover.dll" ]] && cp "$SRC/BepInEx/plugins/AntiCheatBootErrorRemover.dll" "$TEST/BepInEx/plugins/"
cp mods/src/DevAutoJoin/bin/Release/netstandard2.0/Tabg.DevAutoJoin.dll "$TEST/BepInEx/plugins/"
for p in ${TABG_TEST_PLUGINS:-}; do cp "$p" "$TEST/BepInEx/plugins/"; done

# A previous game instance that is still shutting down makes the next one hang at startup.
for _ in $(seq 1 60); do pgrep -x TotallyAccurate >/dev/null || break; sleep 1; done
pgrep -x TotallyAccurate >/dev/null && { echo "TABG is still running; close it first"; exit 1; }
rm -rf "$OUT"; mkdir -p "$OUT"
WORLD_ARGS=(); [[ -s "$WORLD" ]] && WORLD_ARGS=(--world "$WORLD")
NET_ARGS=(--bind 127.0.0.1 --port 7777); JOIN=127.0.0.1:7777; BOT_NET=(--port 7777)
[[ -n "${TABG_TEST_RELAY:-}" ]] && NET_ARGS=(--relay --game-dir "$TEST")
RUST_LOG=debug,tabg_server=debug ./target/release/tabg-server "${NET_ARGS[@]}" --admin "*" ${TABG_TEST_SERVER_ARGS:-} "${WORLD_ARGS[@]}" > "$OUT/server.log" 2>&1 &
SERVER=$!
trap 'kill $SERVER 2>/dev/null || true' EXIT
if [[ -n "${TABG_TEST_RELAY:-}" ]]; then
  CODE=
  for _ in $(seq 1 30); do
    CODE=$(sed 's/\x1b\[[0-9;]*m//g' "$OUT/server.log" | grep -oP 'join_code=\K[A-Z0-9]+' | head -1 || true)
    [[ -n "$CODE" ]] && break; sleep 1
  done
  [[ -n "$CODE" ]] || { echo "server never got a join code:"; cat "$OUT/server.log"; exit 1; }
  echo "relay join code: $CODE"
  JOIN="code:$CODE"; BOT_NET=(--relay "$CODE" --game-dir "$TEST")
fi

(
  cd "$TEST"
  export STEAM_COMPAT_CLIENT_INSTALL_PATH="$STEAM_ROOT" STEAM_COMPAT_DATA_PATH="$STEAM_ROOT/steamapps/compatdata/823130"
  export STEAM_COMPAT_APP_ID=823130 SteamAppId=823130 SteamGameId=823130
  export WINEDLLOVERRIDES="winhttp=n,b" TABG_AUTOJOIN="$JOIN" TABG_AUTOJOIN_QUIT=$((SECONDS_TO_RUN - 20)) TABG_AUTOJOIN_SHOTS="${TABG_TEST_SHOTS:-10,30,50,60,70,85}" TABG_AUTOJOIN_JUMP="${TABG_TEST_JUMP:-}" TABG_AUTOJOIN_SEAT="${TABG_TEST_SEAT:-}" TABG_AUTOJOIN_CHAT="${TABG_TEST_CHAT:-}" TABG_AUTOJOIN_HEAL="${TABG_TEST_HEAL:-}" TABG_AUTOJOIN_DROP="${TABG_TEST_DROP:-}"
  GAME_ARGS=()
  if [[ -z "${TABG_TEST_WINDOW:-}" ]]; then
    GAME_ARGS=(-batchmode -nographics)
    export WINEDLLOVERRIDES="$WINEDLLOVERRIDES;winepulse.drv=d;winealsa.drv=d"
  fi
  exec "$PROTON" run "$TEST/TotallyAccurateBattlegrounds.exe" "${GAME_ARGS[@]}" > "$PWD/../client-proton.log" 2>&1
) &
CLIENT=$!
# A headless second player walks circles a few metres in front of the first lobby spawn, so the
# real client has someone to see. TABG_TEST_BOT_AT= (empty) disables it.
BOT_AT=${TABG_TEST_BOT_AT--414.9,167.13,-498.1}
if [[ -n "$BOT_AT" ]]; then
  cargo build -q --release -p tabg-probe
  ( sleep 45; exec ./target/release/tabg-probe "${BOT_NET[@]}" --name tabg-bot --around "$BOT_AT" --radius 2 --seconds $((SECONDS_TO_RUN - 55)) ${TABG_TEST_BOT_ARGS:-} > "$OUT/bot.log" 2>&1 ) &
fi
sleep "$SECONDS_TO_RUN"
pkill -f "$TEST/TotallyAccurateBattlegrounds.exe" 2>/dev/null || true
wait $CLIENT 2>/dev/null || true

cp "$TEST/BepInEx/LogOutput.log" "$OUT/client-bepinex.log" 2>/dev/null || true
cp "$TEST"/BepInEx/autojoin-*.png "$OUT/" 2>/dev/null || true
echo "== client (DevAutoJoin)"; grep -E 'auto-join|Console\] (Player|Not accepted|Join)' "$OUT/client-bepinex.log" | sed 's/^\[[^]]*\] *//' | head -60 || true
echo "== server"; sed 's/\x1b\[[0-9;]*m//g' "$OUT/server.log" | grep -vE 'code=2 .*PlayerUpdate' | tail -25
echo "== bot"; head -3 "$OUT/bot.log" 2>/dev/null | cut -c1-160 || true
echo "screenshots: $(ls "$OUT"/*.png 2>/dev/null | wc -l) in $OUT"
