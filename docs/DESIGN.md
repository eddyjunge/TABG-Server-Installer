# tabg — Design

Status: agreed 2026-11-04. This replaces everything in the old repo (three GUIs, TabgInstaller.Core, BepInEx server mods).

## Decisions

| Topic | Decision |
|---|---|
| Audience | Friends hosting a server on their own PC (Windows or Linux) |
| Game server | Our own server written in Rust, replacing Landfall's Unity dedicated server. Unmodified TABG clients connect to it |
| Tool | One Rust binary, `tabg`: CLI + local web dashboard |
| Runtime | Console window + browser. The CLI talks to the running host over localhost |
| Dashboard | Server-rendered HTML + htmx, live updates over SSE, served by the binary |
| Joining | Real join codes via Unity Relay, the same way the official server gets them. Direct IP (UPnP) as fallback |
| Client mods | Optional extras only; the base game needs no mods. Auto-sync via share code: catalog mods from GitHub Releases, custom DLLs from the host |
| v1 scope | Setup, start/stop, live console, mods, share/join, settings + presets, players & admins, loadout & ring editor, crash recovery + backups |
| Mods | All groups kept. Server-side mods become native Rust features; client mods stay C#/BepInEx |
| Builds | Everything is built and tested locally (`scripts/check.sh`); no CI. Mod releases come from a local release script |
| Repo | Same repo, old code deleted, proprietary DLLs purged from history |

## 1. What tabg is

**Goal:** one person double-clicks `tabg`, gets a working TABG server in a few minutes, and sends friends a join code. Friends join with their normal TABG. If the host uses client-side extras (proximity chat, vehicles, maps), friends run `tabg join <code>` once and get exactly the right mods.

**Non-goals for v1:** public 24/7 hosting, server clusters, a mod marketplace for third-party mods, Steam ticket verification.

## 2. User flows

**Host**
1. Download `tabg` (one file, about 10–15 MB) and run it. The dashboard opens at `http://localhost:<port>`.
2. Setup: tabg finds the TABG install and extracts the map data from it once (§4.5). Then you pick a preset (Battle Royale, Gun Game, Deathmatch…).
3. Start. The dashboard shows the join code, players, live console, settings, loadouts and ring, presets, and backups.
4. Share: if client mods are in use, the dashboard shows a share code for friends.

**Friend**
- Base game: enter the join code in TABG. Nothing to install.
- With client extras: `tabg join TABG-…` finds the friend's TABG, makes a modded copy, installs the exact client mods (hash-checked), and offers a Launch button.

## 3. System overview

```
 host PC
 ┌───────────────────────────────────────────────────────────────┐
 │ tabg (host process)                                           │
 │  ├─ dashboard  (axum, localhost only)  ◄── browser            │
 │  ├─ local API  ◄── `tabg …` CLI                               │
 │  ├─ sync endpoint (read-only, content-addressed) ◄── friends  │
 │  └─ supervisor ──spawns──► tabg server-process (child)        │
 │                            ├─ game simulation                 │
 │                            ├─ ENet transport (direct IP)      │
 │                            └─ Unity Transport over Unity Relay│
 └───────────────────────────────────────────────────────────────┘
        ▲ JSON-lines IPC on stdio (commands in, events out)
 friends: unmodified TABG client ──relay/IP──► game server
          optional modded copy (C# client mods) for extras
```

The game server runs as a **child process** of the host, so a crash in game logic never takes down the dashboard. The supervisor restarts it with backoff and records why it crashed.

## 4. The game server

### 4.1 Compatibility target
- Target: the current Steam TABG client. The server must match the client's version hash (`APP_VERSION` in Landfall's `ServerClient`). tabg reads the hash from the host's install at setup, so a TABG update shows a clear "client version X not supported yet" error instead of broken joins.
- We implement Landfall's behaviour from protocol knowledge. **No Landfall code is copied and no Landfall assets are redistributed** (§10).

### 4.2 Transports
Both carry the same message layer (§4.3).

1. **ENet (direct IP).** Landfall uses ENet-CSharp, a fork of ENet whose wire format may differ from upstream ENet. We vendor the fork's MIT-licensed C source and call it through FFI (`tabg-enet-sys`); porting it to pure Rust can come later. Clients connect with `ConnectToServerIP(ip, port)`.
2. **Unity Transport over Unity Relay (join codes).** The official server signs in anonymously to Unity Gaming Services (Landfall's project), creates a relay allocation and gets a join code. Clients join with `ConnectToRelayServer(code)` over Unity Transport. We implement:
   - the UGS anonymous sign-in, allocation and join-code REST calls;
   - the relay bind/ping/relay protocol;
   - the Unity Transport framing and reliability that the client uses.

   We use it exactly as the official server does: one allocation per running server.

Relay is the default. Direct IP is the fallback: tabg tries UPnP to open the UDP port, and the dashboard explains manual port forwarding if UPnP fails.

### 4.3 Message layer
- Frame: `[event code: u8][payload]` on channel 0, reliable or unreliable per message.
- 126 event codes; the server handles 48 of them as inbound (Join, RoomInit, RequestWorldState, PlayerUpdate, PlayerFire, DamageEvent, RequestWeaponPickUp, RequestItemDrop, CarUpdate, RequestSeat, RequestAirplaneDrop, ReviveState, ChatMessage, AdminCommand…).
- Payloads are little-endian binary in .NET `BinaryWriter` style. Each message gets a Rust struct with `encode`/`decode` in `tabg-protocol`, plus round-trip tests against **recorded real traffic** (§9).
- Our own client-mod protocols use event codes ≥ 240 (240 voice, 241 radar, 242 range, 243 devtest, 244 skins, 245 vehicles). New features go through one code with a channel byte, plus a capability handshake (§7).

### 4.4 Game simulation
- Landfall's server is mostly a referee. Clients send their own movement, shots and damage. The server validates them, owns match state (players, teams, inventory, loot, ring, plane, cars, game phase) and broadcasts. We copy this model first and tighten validation later.
- `tabg-game` is pure logic with no IO and no clock: `state + input → state + outgoing messages`. That makes it unit-testable and deterministic.
- Game modes: Battle Royale first, then Brawl/Deathmatch, Gun Game (loadout progression), Bomb, Deception, and Test.

### 4.5 World data
Loot spawners, player and car spawns, networked interactables (doors etc., with the IDs the client expects), map bounds, loot tables, plane paths, and later the navmesh and collision geometry all live in Landfall's Unity scene files.

- **Extraction:** a small C# BepInEx dumper mod runs once in batch mode against the host's own TABG files. It loads each map and writes `world.json` (and `navmesh.bin` later). It runs at setup, is cached per game-version hash, and its output is never redistributed.
- The dumper is the only place left where we run Landfall's Unity code. Whether to use the client install or the dedicated-server install is decided in M1.

### 4.6 Server list
The official server sends `GameServerHeartbeat` to Landfall's community backend (name, join code, player count, mode, password flag…). We send the same heartbeat so the server shows up in TABG's community server list. This is on by default and can be turned off for private servers.

### 4.7 Identity, admins and commands
- v1 does not verify Steam tickets. Players are identified by the IDs the client sends on join (Steam/Epic/PlayFab, as ServerLogger records them).
- Admins, bans and permissions are native, stored by player ID.
- Chat commands are implemented natively, with the command set people know from Citruslib: `/give`, `/bring`, `/goto`, `/send_pos`, `/start`, kick/ban… The dashboard console runs the same commands as "console".

### 4.8 Old server mods → native features

| Old BepInEx server mod | In the Rust server |
|---|---|
| AntiCheatBypass, Citruslib, EOS DLL copy, SteamCMD | not needed |
| MatchCore (loadouts, rings, votes, drops, spell drops, win rules) | core game rules + settings |
| ServerLogger | player history store |
| SoloTesting | "allow solo start" setting |
| ProximityChat.Server | voice relay for event 240 (needs client mod) |
| RangeMap / DevTestMap servers | "Range" and "DevTest" modes (need the Maps client mod) |
| CustomGameSkins.Server | skins authorization, **off unless the host enables it** |
| AdminRadar.Server | debug telemetry; dummies only by default |
| FakePlayers (bots) | native bots on the extracted navmesh (M6) |
| UnusedVehicles + Vehicles.Shared rules | native vehicle/FPV rules; the pure rule code ports over as-is (M6) |
| PerformanceServer | the server is efficient by design; delta replication only if a client mod needs it later |

## 5. The tabg tool

### 5.1 Process model and CLI
- `tabg` with no arguments starts the host process and opens the dashboard. Other commands talk to a running host over its local API, starting one if needed.
- Commands: `setup`, `start`, `stop`, `restart`, `status`, `console`, `say`, `kick`, `ban`, `admins`, `config get|set`, `preset list|apply|save`, `mods`, `share`, `join <code>`, `backup list|restore`, `logs`.

### 5.2 State and the declarative model
- Each server is a folder with `tabg.toml`: preset, mode, game settings, loadouts, ring, admins, the client mod pack, and the network mode. The dashboard and CLI only edit this file, then **apply** it. The running server hot-reloads what it can and says when a restart is needed.
- A preset is a partial `tabg.toml`. The built-in presets ship as data.
- **Import:** an existing `game_settings.txt` / `TheStarterPack.txt` / `PlayerPerms.json` can be imported once. The format rules from the old code are kept for this: 0/1 keys, the starter-pack loadout syntax, and cross-field rules such as `KillsToWin` only for Brawl.
- Data lives in the OS app-data dir (`~/.local/share/tabg`, `%APPDATA%\tabg`): servers, extracted world cache, download cache (by sha256), backups, logs, and the player store (SQLite).

### 5.3 Dashboard
Overview (status, join code, players, quick actions) · Console · Players (kick/ban/admin, history) · Settings (typed forms, validation) · Loadouts & ring editor · Presets · Client mods & share · Backups · Logs.

### 5.4 Reliability
- Crash recovery: the supervisor restarts the server with exponential backoff, keeps the crash log, and shows it in the dashboard.
- Backups: `tabg.toml` and the player store, snapshotted before every apply and restorable with one click. They are small; game files are never copied.

### 5.5 Security
- The dashboard and local API listen only on `127.0.0.1`. They check the `Host` header against DNS rebinding and `Origin` on every state-changing request, and require a per-session token, so websites open in your browser can't control the server.
- The sync endpoint is a separate port. It is read-only and serves only files in the current share code, by sha256, and never lists directories.

## 6. Client side

- **Base game:** unmodified client, nothing to install.
- **Modded copy** (only when the server uses client extras):
  - Copy the TABG install without `EasyAntiCheat/` and write `steam_appid.txt` = `823130`.
  - Install BepInEx 5.4.23.3 (win x64, Doorstop 4 config) and PopupBlocker; the EAC boot-error remover is pinned by version and hash.
  - Launch the exe directly while Steam is running. On Linux, run under Proton with `WINEDLLOVERRIDES="winhttp=n,b"`.
  - The copy has its own `tabg.toml` with the pack, and `apply` keeps it exactly in sync.
- **Share code:** a self-contained encoding of the client pack (mod id@version for catalog mods; sha256 plus the host's sync address for custom DLLs). No central server is involved.
- **`tabg join`:**
  1. Decode the share code.
  2. Fetch catalog mods from GitHub Releases and custom files from the host.
  3. Verify every hash.
  4. Apply.
  5. Launch.

## 7. Client mods (C#)

- **Kept:** ProximityChat.Client, ModSettings, PopupBlocker, FlyingControls, CustomGrenades (client), CoordsDisplay, PerformanceClient (client-side optimizations only), Maps.Client (RangeMap + DevTestMap merged), CustomGameSkins.Client, AdminRadar.Client (local-player ESP fallback removed).
- **New:** the world dumper (§4.5) and **Tabg.Net**, a shared library for client mods:
  - one receive dispatcher;
  - framed sub-channels;
  - a capability handshake on join (the client announces mods and versions, and the server enables matching features per player).
- **Dropped:** EnhancedClient, StarterPack, TestMod/GiveMod, WeaponSpawnConfig, and all server-side BepInEx mods (replaced by §4.8).
- **Build:**
  - One solution, `mods/Mods.sln`. `mods/src/Directory.Build.props` sets everything; the only input is `TabgClientDir`, a modded TABG client copy with BepInEx installed (env `TABG_CLIENT_DIR`, gitignored `mods/Local.props`, or `-p:`). Game, Unity, Sirenix and BepInEx assemblies all resolve from there, and projects list extra modules as `<GameRef Include="UnityEngine.AudioModule" />`.
  - Output DLLs are named `Tabg.<Mod>.dll`.
  - No DLLs in git (`.gitignore` blocks them).
  - Deterministic builds with `PathMap`, and no post-build copies into tracked folders.
  - `mods/tests/Vehicles.Tests` is still the old console runner; converting it to xUnit is open work.
- **Release:** `scripts/release-mods` builds Release, packs each mod, writes `catalog.json` (id, version, sha256, size, dependencies) and uploads to a GitHub Release. tabg pins catalog versions.

## 8. Repo layout

```
crates/
  tabg-protocol    event codes, message structs, codec, version hash
  tabg-enet-sys    vendored ENet-CSharp C source + FFI
  tabg-utp         Unity Transport framing + Unity Relay + UGS auth client
  tabg-net         transport trait over ENet and UTP
  tabg-game        pure simulation: rooms, players, loot, ring, modes, commands
  tabg-world       world.json model and loader (navmesh later)
  tabg-server      tick loop: net + game + world, IPC to the host
  tabg-host        supervisor, state/apply, player store, backups, sync, UPnP, heartbeat
  tabg-web         dashboard + local API (axum, htmx, SSE)
  tabg             the binary: CLI entry point
  tabg-probe       headless test client that speaks the protocol (tests + debugging)
mods/              C# client mods, dumper, Tabg.Net, recorder (dev only)
presets/           built-in presets (TOML)
scripts/           release-mods, record-session
docs/DESIGN.md
```

Rust stack: tokio, axum, maud or askama + htmx, clap, serde + toml, reqwest (rustls), igd-next (UPnP), rusqlite, sha2, zip, tracing.

## 9. Testing

- **Recorded ground truth:** a dev-only C# recorder mod on Landfall's own server (on the dev machine) logs every inbound and outbound event with timestamps while real clients play. The recordings become fixtures for:
  - codec round-trip tests: decode → encode gives the same bytes;
  - behaviour tests: same inputs, then compare our outgoing messages with Landfall's.
- **Game logic:** unit tests in `tabg-game`, plus property tests for codecs.
- **Integration:** `tabg-probe` clients join our server in tests (no game files needed) and play scripted scenarios.
- **Manual:** a real TABG client joins at the end of each milestone.
- Everything runs locally via `scripts/check.sh` (`--mods` adds the C# client mods). There is no CI.

## 10. Risks

| Risk | Mitigation |
|---|---|
| ENet-CSharp wire format differs from upstream ENet | Vendor the fork's C source; verify in M1 |
| Unity Relay / Unity Transport reimplementation is large and could be blocked by Unity/Landfall; ToS risk | Use it exactly like the official server; direct IP + UPnP fallback always works |
| Behaviour the client expects that isn't visible in code (timing, ordering) | Recorded sessions + `tabg-probe` comparisons |
| TABG update changes the protocol | Version hash check at setup; record a new session and diff |
| Unmodified client with EAC joining a server without EAC | Verify in M1 (the official server runs fine with `AntiCheat=false`) |
| Bots and vehicles need navmesh/collision data | Dumper exports them (M6); bots come last |
| Legal | Implement from protocol knowledge, copy no Landfall code, redistribute no Landfall files or extracted data, keep Landfall/Unity/Sirenix DLLs out of git. Landfall's terms may still object; that's an accepted risk |

## 11. Milestones

- **M0 – Reset.** Mirror backup → delete old code → purge proprietary binaries from history → force-push → new workspace skeleton. *(Done 2026-11-04: fresh single-commit history; old repo, tags, PR refs and all release downloads are backed up outside the repo.)*
- **M1 – Proof.**
  - ~~Recorder mod and one recorded session.~~ Replaced by `scripts/reference-server.sh`: Landfall's own server runs offline in a network namespace and `tabg-probe` joins it, so we capture its real replies without a mod or a human player.
  - ~~`tabg-protocol` for the join/spawn/movement messages.~~ Done; the join broadcast and accept reply match Landfall's server byte for byte, and its full world state (118 cars) decodes.
  - ~~ENet transport.~~ Done (`tabg-enet-sys` + `tabg-net`); Landfall's server accepts our ENet connections.
  - Our server (`tabg-server`) accepts players, sends the world, and replicates movement; two probes see each other move (end-to-end test).
  - ~~A real client joins by IP.~~ Done 2026-11-04, fully automated: `scripts/real-client-test.sh` launches a test copy of the real client under Proton with the `DevAutoJoin` dev plugin, which joins `127.0.0.1:7777` from the main menu. The unmodified game logic loaded `MainWorld_Base`, spawned at our lobby spawn, showed "WAITING FOR PLAYERS / 1 ALIVE / 100 HP", and kept pinging and sending movement.
  - ~~The real client sees a second player move.~~ Done 2026-11-04: with a `tabg-probe` bot walking circles in front of it, the real client showed "2 ALIVE" and rendered "tabg-bot" moving (2160 movement batches received). **M1 complete.**
  - ~~Dumper v0.~~ `mods/src/WorldDumper` + `scripts/dump-world.sh` export the lobby spawn points (6 on `ServerTerrain`, identical to what Landfall's server hands out) from the host's own dedicated-server install, offline; `tabg-server --world` uses them. Interactables, loot spawners and cars come with M3.

  Findings from the reference server (2026-11-04):
  - The Epic token and product-user-id fields in `RoomInit` must be NUL-terminated or the server throws.
  - Solo clients must send `RoomInit` as the host of a 1-player squad; otherwise Landfall's server refuses for lack of a booking, and since the refusal goes to a not-yet-accepted peer it is never delivered (the client just times out). Our server doesn't require bookings.
  - The world state is sent at an estimated size with a zero-filled tail (~3.7 KB extra).
  - A player with no weapon has `weapon_type = i32::MAX`.
  - After the world state the official server also sends `RecievedSteamID` (1 byte); we don't yet.
  - The client has a direct-connect UI (`IP[:port]`, default port 7777) besides join codes.
  - The real client zero-pads its `RoomInit` too, and its last flag is `FillUpSquad`.
  - Joining before the client's Epic login completes fails with "Failed generating token" (the client fetches an EOS id token right after the ENet connect).
- ~~**M2 – Join codes.**~~ Done 2026-11-04. UGS sign-in, relay allocation, Unity Transport, server-list heartbeat, UPnP fallback.
  - ~~Relay hosting.~~ Done 2026-11-04 (`crates/tabg-utp`): anonymous UGS sign-in with the project id read from the host's `globalgamemanagers`, relay allocation and join code, the relay UDP protocol (bind with HMAC, keep-alive pings, rebind, reallocation when the relay drops us), Unity Transport's connection protocol (session tokens, ping/pong, timeouts) and TABG's pipelines (Fragmentation + ReliableSequenced window 64, UnreliableSequenced) with TABG's `[u16 length]` framing. `tabg-server --relay` prints the join code; `tabg-probe --relay CODE` joins like a player. A live test (`cargo test -p tabg-utp -- --ignored`) hosts and joins through Unity's relay; the real client joined by code (`TABG_TEST_RELAY=1 scripts/real-client-test.sh`) and played through countdown and plane with a relay bot.
  - ~~Server list.~~ Done 2026-11-04: opt-in `--list` posts `GameServerHeartbeat` to Landfall's community backend every 5 s (on its own thread), with the game version taken from the world dump (`app_version`, so it follows game updates). Join code is the relay code, or `public-ip:port` for direct servers. Verified: our server appeared in `GetServerList`. The backend has a word filter on names and descriptions (it refused "(ignore)" and "automated"); its error is logged.
  - ~~UPnP.~~ Done 2026-11-04: `--upnp` maps the UDP port (1 h lease, renewed, removed on Ctrl-C). Not verifiable here: this network has no UPnP router, so we only saw the fallback warning.
  - Direct IP needs only ENet: the client resets to its ENet networker on every return to the main menu, so it never uses Unity Transport for a direct join.
- **M3 – Battle Royale playable.** Loot, inventory, weapons, damage, death/revive, ring, plane drop, cars, match flow, win.
  - ~~Match flow.~~ Done 2026-11-04: countdown (players-to-start, timed force start), plane (45 s path through the first ring, 5 s progress sync, jump requests, auto-drop), landing relay, ring cycle with the map's `ring_times` (dumped), last-team-standing win, refusal of late joiners, restart after the end. In the real client: "GAME STARTS IN 3", plane ride, jump, landing, "RING PROGRESS 44 %".
  - ~~Damage and death.~~ Done 2026-11-04: client-reported damage with the official rules (only in the match, only after jumping, 1 s grace after going down, downed damage capped at 50 × 0.12), down-before-death, whole-squad-down kills the squad, revive (to 15 HP), self-reported fall/ring deaths, `PlayerDead`/`TeamDead` with placement, kill counting, and relays for fire (with projectile sync), weapon/gear changes, effects, markers and player state. In the real client: the bot's hit took the player to 40/100, the lethal one triggered "Local player network die", "Handling Team Dead" and the switch to `Ended`.
  - ~~Loot.~~ Done 2026-11-04: the dumper exports 44,247 loot spots, 14 match modifiers with their weighted presets and 247 item names; at match start a modifier is drawn by rarity, ~10 % of spots are kept (`StripLootByPercentage`), and each gets one weighted drop. The chunk system (128 m chunks, 3×3 view) sends `ChunkEntry` on jumping and entry/exit diffs on crossing chunks, `PlayerEnteredChunk`/`PlayerLeftChunk` to watchers, and movement only to watchers. Pickups and drops work; equipment is tracked from weapon changes. In the real client: landed in town, picked up an M16 and two pistols and fired them.
  - ~~Cars.~~ Done 2026-11-04: the dumper exports the 118 cars the official server spawns (type, pose, seats with driver flag, damageable parts); the world state lists them, chunk entries/exits include them (not in the lobby, like the official server), seats are exclusive, the driver's `CarUpdate` moves car and occupants (with chunk changes), the last driver may push the empty car for 5 s, car parts take damage, and downed/dead/leaving players are ejected. Drivers are replicated with the driving body, passengers as inside-car. In the real client (auto-jump + auto-seat from `DevAutoJoin`): cars spawned around the landing, the player got on a Bike and drove.
  - ~~Healing, throws, chat and the rest.~~ Done 2026-11-04: healing items with amounts from the dump (bandage 15, med kit 50), health-state increases, thrown items (grenades don't become loot), proximity chat, the parrot (rate-limited), catchphrases, spectating, projectile sync, the generic player package relay, the built-in "Start Game" admin command, and native admin chat commands (`/start`, `/give <item> [qty]`, `/heal`, `/kick <name>`; admins via `--admin`). In the real client: `/give 132 2` dropped two med kits and replied.
  - Not yet: server-side inventory/ammo validation (clients are trusted); souls/blessings/curses, the respawn minigame and spell drops (Battle Royale extras); other game modes (M5).
- ~~**M4 – tabg v1.**~~ Done 2026-11-05. Dashboard, settings and presets, players/admins/console, loadout & ring editor, crash recovery, backups, share/join with client sync, Windows + Linux builds.
  - ~~Host, dashboard and CLI.~~ Done 2026-11-05 (`crates/tabg-host`, `crates/tabg-web`, `crates/tabg`). `tabg` starts the host: the dashboard (Overview with join code, Console, Players, Settings, Loadout & Ring, Presets, Backups; htmx + SSE) and the JSON API on 127.0.0.1 (Host/Origin checks, session token via the printed link or `Authorization: Bearer` from `host.json`). The game server runs as `tabg serve`, a child process with JSON-lines IPC; the supervisor restarts it with exponential backoff and writes a crash log (tested with `kill -9`: back with a new join code after 1 s). CLI: `setup`, `start` (starts a background host if needed), `stop`, `restart`, `status`, `console`, `say`, `kick`, `ban`/`unban`, `admins`, `config show|get|set|path`, `preset list|apply|save`, `backup list|restore`, `logs [-f]`. Without a running host, file-only commands use a temporary in-process API, so there's one code path.
  - ~~Setup.~~ Done: Steam library detection (native, Flatpak, Windows registry), then the map export from the TABG Dedicated Server (app 2311970): a hard-linked copy, BepInEx 5.4.23.3 pinned by sha256, the dumper plugin embedded in the binary, and a private `game_settings.txt` (port 47999, no relay). The dumper switches the export server's heartbeat off. It takes about 8 s and is cached per game version.
  - ~~Config, presets, players.~~ Done: `tabg.toml` (unknown keys are errors, validation by field, Landfall's MIT word list for public names), 5 built-in presets that use only working features (Battle Royale, Solo Royale, Quick Match, Loot Frenzy, Armed Drop) plus saved ones, a backup before every change (50 kept, one-click restore), hot reload for lobby rules and a restart for network/name/player-limit changes. Admins and bans work by name or player id (`epic:<product user id>`); in-game `/ban` is written back to `tabg.toml`. The landing loadout is sent as `PlayerLootRecieved`, straight into the inventory. Player history (joins, kills, deaths, wins, names) lives in `players.json`, not SQLite: that is enough for a friends' server and has no C dependency.
  - ~~Landing loadout in the real client.~~ Verified 2026-11-05 (`TABG_TEST_SERVER_ARGS="--loadout 160:1,6:240,131:5,132:2"`): after landing the player held the M16 with "30 | 210" ammo, 2 med kits and 5 bandages.
  - ~~Windows build.~~ Done 2026-11-05: `scripts/build-release.sh` builds the dumper plugin, embeds it, and builds `tabg` for Linux and (with zig + cargo-zigbuild, `x86_64-pc-windows-gnu`) Windows into `target/dist/` with SHA256SUMS. About 8 MB each. The Windows build passed a smoke test under Proton's Wine (`--version`, presets, `config set/get` through the in-process API).
  - ~~Share/join with client sync.~~ Done 2026-11-05: `[mods] client` lists catalog mods (`id@version`) and the host's own DLLs. `tabg share` (or the Share page) makes a `TABG1-…` code with the join code, catalog mods pinned by sha256 (plus their hard BepInEx dependencies), and own DLLs by sha256 with the host's sync address. The sync endpoint is read-only `GET /f/<sha256>` on TCP 7781, started only when needed; `TABG_SHARE_HOST` overrides the address for LANs. `tabg join <code>` makes the modded copy (hard links, no `EasyAntiCheat/`, `steam_appid.txt`, BepInEx 5.4.23.3 and AntiCheatBootErrorRemover 1.0, both pinned) with exactly the shared plugins, and removes what an earlier join installed. `tabg launch` starts it (Proton Experimental on Linux). `scripts/release-mods.sh` builds the 11 client mods into `target/mods-dist/` with `catalog.json`; `--upload` would publish them to the `mods` GitHub release, which hasn't been done yet. Verified end to end: a friend data dir joined a share code (local catalog plus a host-served DLL) in 0.7 s, launched, BepInEx loaded every plugin, and the real client joined our server by relay code.
- **M5 – Modes & extras.** Brawl, Gun Game, Bomb, Deception, Test; MatchCore rules; proximity chat relay; Range/DevTest; skins (opt-in); radar.
  - ~~Respawns, Deathmatch and Gun Game (MatchCore rules).~~ Done 2026-11-05: `[rules]` in `tabg.toml` (`respawn`, `respawn_delay`, `kills_to_win`, `time_limit`, `gun_game` levels). Dead players get `PlayerRespawn` (`[count]` plus 23 bytes each: index, new index, health, position, rotation, curse 255) at a random loot spot inside the white ring (loot spots sit on floors and terrain), a chunk teleport, and the loadout plus their gun game level. No team eliminations with respawns. The match ends at the kill limit, past the last gun game level, at the time limit (most kills wins), or when everyone left. Presets "Deathmatch" and "Gun Game" (10 weapon levels, ammo in the loadout); the dashboard has a Rules card and a level editor. In the real client: killed by the bot, back 5 s later in a town with 100 HP and a new M16 ("30 | 210"), "2 ALIVE".
  - ~~Proximity voice relay.~~ Done 2026-11-05 (`room/voice.rs`): event 240 frames from the ProximityChat client mod are checked (version, size), rate-limited to 60/s per speaker, stamped with the real sender and sent unreliably to living players within `game.voice_range` (50 m by default, 0 = off). Unit-tested. A real-client check needs two players with microphones and is still to do.
  - ~~Test mode, Range and DevTest maps.~~ Done 2026-11-05: `game.mode = "test"` starts without plane or ring (everyone where they stand, chunk entry and loadout at the start, like the official `TestGameMode`). `game.map = "range" | "dev_test"` plays the RangeMap / DevTestMap client mods' maps: their hello (events 242 `RNGE` v2 / 243 `DVTM` v1) is accepted, F6 item requests go straight into the inventory (Range also gets `ItemGranted`), the island's loot and cars are left out, and the map's own spawns are used. Validation requires the map's client mod in the share. Presets "Shooting Range" and "Island Gun Game". In the real client: the handshake was accepted, WilhelmTest loaded, Test mode started ("#1 : 0 Kills"), and the M16 loadout arrived.
  - ~~Skins (opt-in).~~ Done 2026-11-05 (`room/skins.rs`): with `game.skins = true`, a CustomGameSkins client's hello (event 244, `SKIN` v1) is accepted; outfits (6 slots of item and colour) are rate-limited (0.5 s), shape-checked, stored as the player's gear and broadcast as a vanilla `GearChange`. Off by default: the hello is answered "disabled". Unit-tested. Unlike the old plugin we can't check items against the game's gear database (it isn't dumped).
  - ~~Brawl.~~ Done 2026-11-05: setup also exports the map in Brawl mode (`<version>-brawl.json`): 6 team and 12 solo spawn houses (5 spawns and 34/22 tiered loot spots each, read from the unspawned `BrawlLootSelector` prefab, because the live copy uses up its spots) and the 14 loot presets. `game.mode = "brawl"` plays it like the official `BrawlGameMode`: teams spawn in their house, at the end of the countdown one preset is drawn and the same loot goes into every house, the doors open (`GameStateChanged` 5), a wiped team respawns at its house, and the first team to `kills_to_win` (or the last team left) wins. Brawl preset; Mode/Map selectors and the Rules card on the Settings page. In the real client: the CastleFight map loaded, the player stood in the spawn house with the round's loot on the walls (e.g. Liberating Barrett), and the scoreboard showed both teams.
  - Fixed 2026-11-05: rustfmt had reformatted the built-in preset table, so later text edits silently missed it, and only the first 5 presets were built in. All 10 are now built in, and a test checks that every file in `presets/` is.
  - Radar waits for M6: the design shows only bots on it, and there are no bots yet.
- **M6 – Bots & vehicles.** Navmesh bots (FakePlayers successor) and vehicle/FPV rules.

## 12. Carried over from the old repo
Knowledge worth keeping, rewritten rather than copied:
- the client modded-copy recipe (§6);
- Steam library detection (`libraryfolders.vdf`, registry on Windows, native and Flatpak Steam paths on Linux);
- server names must use Landfall's word list (`landfallgames/tabg-word-list`), or the server-list heartbeat rejects them;
- the item ID database (now dumped from the game: e.g. 6 = Normal Ammo);
- the preset contents;
- the game_settings/TheStarterPack format rules (for import);
- the pure rule code in Vehicles.Shared;
- the tests for ProximityChat frame processing and MatchCore config parsing.
