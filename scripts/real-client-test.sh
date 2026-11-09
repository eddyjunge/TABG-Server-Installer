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
#   TABG_TEST_WINDOW  unset: no graphics at all (Unity -batchmode -nographics, no screenshots)
#                     1: real rendering and screenshots, but out of your way on Hyprland: the game
#                        lives on a temporary invisible output, never takes focus or the mouse, makes
#                        no sound and is capped at 30 fps (TABG_TEST_FPS)
#                     visible: a normal window with sound
#   TABG_TEST_COMPARE "plugin.guid/Section/Key[=v1,v2,..]": each screenshot once per value (bool: off, on)
#   TABG_TEST_GOTO    "Layer@seconds": teleport the client onto that terrain layer (Deser, Snow, Grass)
#   TABG_TEST_SEQUENCE "A,B@seconds,distance,shot1,shot2": spawn these effects in turn (seq-NAME-1/2.png)
#   TABG_TEST_SETTLE  seconds to let each TABG_TEST_COMPARE value take effect before its screenshot
#   TABG_TEST_BENCH   "plugin.guid/Section/Key=v1,v2@start,seconds,rounds": FPS per value ([bench] in the log)
#   TABG_TEST_EXPLODE "Prefab@seconds,distance": spawn a game explosion or grenade effect in front of the
#                     camera (boom-*.png); Prefab is e.g. Ex_MissileExpl, Ex_Stun or "Smoke Grenade"
#   TABG_TEST_TARGET  "Name@shots": stand in front of a building whose name starts with Name and put the
#                     TABG_TEST_EXPLODE blasts on its wall (destruction testing)
#   TABG_TEST_BURST   "0.05,0.5,2,...": seconds after the explosion to capture (default: just the flash)
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
BUILD_OUT=$(dotnet build -v q -c Release mods/src/DevAutoJoin 2>&1) || { echo "$BUILD_OUT"; exit 1; }
cargo build -q --release -p tabg-server

if [[ ! -e "$TEST/TotallyAccurateBattlegrounds.exe" ]]; then
  echo "creating test client copy at $TEST"
  mkdir -p "$TEST"
  cp -a --reflink=auto "$SRC"/. "$TEST"/
fi
rm -rf "$TEST/BepInEx/plugins" "$TEST/BepInEx/config" "$TEST/BepInEx/LogOutput.log" "$TEST"/BepInEx/autojoin-*.png "$TEST"/BepInEx/boom-*.png "$TEST"/BepInEx/seq-*.png
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
HIDDEN_OUTPUT=
cleanup() {
  kill $SERVER 2>/dev/null || true
  [[ -n "$HIDDEN_OUTPUT" ]] || return 0
  pkill -f "$TEST/TotallyAccurateBattlegrounds.exe" 2>/dev/null || true
  [[ -n "${TABG_TEST_KEEP_OUTPUT:-}" ]] && return 0
  # Removing an output moves focus to the first monitor; put it back where it was.
  local was; was=$(focused_monitor)
  hyprctl -q output remove "$HIDDEN_OUTPUT" 2>/dev/null || true
  sleep 0.3
  [[ -n "$was" && "$(focused_monitor)" != "$was" ]] && hyprctl -q dispatch "hl.dsp.focus({ monitor = '$was' })" || true
}
focused_monitor() { hyprctl monitors -j | python3 -c 'import json,sys; print(next((m["name"] for m in json.load(sys.stdin) if m["focused"]), ""))'; }
monitor_workspace() { hyprctl monitors -j | python3 -c 'import json,sys; print(next((m["activeWorkspace"]["id"] for m in json.load(sys.stdin) if m["name"] == sys.argv[1]), ""))' "$1"; }
trap cleanup EXIT
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

# Far from the real monitors, so the cursor can never wander onto it.
HIDDEN_WORKSPACE=
if [[ "${TABG_TEST_WINDOW:-}" == 1 && -n "${HYPRLAND_INSTANCE_SIGNATURE:-}" ]]; then
  HIDDEN_OUTPUT=TABGTEST
  [[ -n "$(monitor_workspace "$HIDDEN_OUTPUT")" ]] || hyprctl -q output create headless "$HIDDEN_OUTPUT"
  # (hl.monitor applies, then hyprctl complains that it is not a dispatcher.)
  hyprctl dispatch "hl.monitor({ output = '$HIDDEN_OUTPUT', mode = '1920x1080@60', position = '30000x30000', scale = 1 })" >/dev/null 2>&1 || true
  sleep 0.5
  HIDDEN_WORKSPACE=$(monitor_workspace "$HIDDEN_OUTPUT")
fi

(
  cd "$TEST"
  export STEAM_COMPAT_CLIENT_INSTALL_PATH="$STEAM_ROOT" STEAM_COMPAT_DATA_PATH="$STEAM_ROOT/steamapps/compatdata/823130"
  export STEAM_COMPAT_APP_ID=823130 SteamAppId=823130 SteamGameId=823130
  export WINEDLLOVERRIDES="winhttp=n,b" TABG_AUTOJOIN="$JOIN" TABG_AUTOJOIN_QUIT=$((SECONDS_TO_RUN - 20)) TABG_AUTOJOIN_SHOTS="${TABG_TEST_SHOTS:-10,30,50,60,70,85}" TABG_AUTOJOIN_JUMP="${TABG_TEST_JUMP:-}" TABG_AUTOJOIN_SEAT="${TABG_TEST_SEAT:-}" TABG_AUTOJOIN_CHAT="${TABG_TEST_CHAT:-}" TABG_AUTOJOIN_HEAL="${TABG_TEST_HEAL:-}" TABG_AUTOJOIN_DROP="${TABG_TEST_DROP:-}" TABG_AUTOJOIN_COMPARE="${TABG_TEST_COMPARE:-}" TABG_AUTOJOIN_BENCH="${TABG_TEST_BENCH:-}" TABG_AUTOJOIN_EXPLODE="${TABG_TEST_EXPLODE:-}" TABG_AUTOJOIN_BURST="${TABG_TEST_BURST:-}" TABG_AUTOJOIN_SETTLE="${TABG_TEST_SETTLE:-}" TABG_AUTOJOIN_GOTO="${TABG_TEST_GOTO:-}" TABG_AUTOJOIN_TARGET="${TABG_TEST_TARGET:-}" TABG_AUTOJOIN_SEQUENCE="${TABG_TEST_SEQUENCE:-}"
  GAME_ARGS=()
  [[ "${TABG_TEST_WINDOW:-}" == visible ]] && exec "$PROTON" run "$TEST/TotallyAccurateBattlegrounds.exe" > "$PWD/../client-proton.log" 2>&1
  [[ -z "${TABG_TEST_WINDOW:-}" ]] && GAME_ARGS=(-batchmode -nographics)
  export WINEDLLOVERRIDES="$WINEDLLOVERRIDES;winepulse.drv=d;winealsa.drv=d"
  if [[ -n "${TABG_TEST_WINDOW:-}" && -n "${HYPRLAND_INSTANCE_SIGNATURE:-}" ]]; then
    export TABG_AUTOJOIN_FPS="${TABG_TEST_FPS:-30}"
    # hyprctl spawns from the compositor, so the launcher carries this environment over.
    LAUNCH="$OLDPWD/$OUT/launch.sh"
    { export -p; printf 'cd %q\nexec %q run %q > %q 2>&1\n' "$TEST" "$PROTON" "$TEST/TotallyAccurateBattlegrounds.exe" "$TEST/../client-proton.log"; } > "$LAUNCH"
    hyprctl -q dispatch "hl.dsp.exec_cmd('bash $LAUNCH', { workspace = '$HIDDEN_WORKSPACE silent' })"
    exit 0
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
if [[ -n "$HIDDEN_OUTPUT" ]]; then for _ in $(seq 1 20); do pgrep -x TotallyAccurate >/dev/null || break; sleep 1; done; fi

cp "$TEST/BepInEx/LogOutput.log" "$OUT/client-bepinex.log" 2>/dev/null || true
cp "$TEST"/BepInEx/autojoin-*.png "$TEST"/BepInEx/boom-*.png "$TEST"/BepInEx/seq-*.png "$OUT/" 2>/dev/null || true
echo "== client (DevAutoJoin)"; grep -E 'auto-join|Console\] (Player|Not accepted|Join)' "$OUT/client-bepinex.log" | sed 's/^\[[^]]*\] *//' | head -60 || true
echo "== server"; sed 's/\x1b\[[0-9;]*m//g' "$OUT/server.log" | grep -vE 'code=2 .*PlayerUpdate' | tail -25
echo "== bot"; head -3 "$OUT/bot.log" 2>/dev/null | cut -c1-160 || true
echo "screenshots: $(ls "$OUT"/*.png 2>/dev/null | wc -l) in $OUT"
