<div align="center">
<img src="logo/tabg-server-installer.svg" width="120" alt="">

# tabg

**Host a Totally Accurate Battlegrounds server for your friends.**

</div>

> **Status: rebuilding from scratch.** This repo is being rewritten as a single Rust program
> with its own TABG game server. Nothing is usable yet. The previous C# installer (v5.2.0 and
> earlier) is retired.

## The plan

- **`tabg`**: one small program for Windows and Linux. Double-click it, a dashboard opens in
  your browser, and a few minutes later you have a server and a join code for your friends.
- **Our own game server in Rust**: unmodified TABG clients can join; no Unity server, SteamCMD
  or BepInEx needed on the host.
- **Optional client mods** (proximity chat, vehicles, maps…): friends run `tabg join <code>` once
  and get exactly the mods the server uses.

The full design, decisions and milestones are in [docs/DESIGN.md](docs/DESIGN.md).

## Repo layout

```
crates/   Rust workspace: the tabg binary and (soon) the game server
mods/     C# BepInEx client mods (optional extras), built locally against your own TABG install
docs/     design documents
```

## Building

```bash
cargo build                 # the tabg tool
cargo run --release -p tabg-server   # dev: run our game server on UDP 7777
cargo run --release -p tabg-probe -- --host 127.0.0.1   # headless client that joins and reports

# map data: export spawn points from your own server install (offline), then use them
scripts/dump-world.sh ~/.local/share/Steam/steamapps/common/TotallyAccurateBattlegroundsDedicatedServer
cargo run --release -p tabg-server -- --world target/tabg-world.json

# ground truth: run Landfall's own server offline (network namespace) and probe it
scripts/reference-server.sh ~/.local/share/Steam/steamapps/common/TotallyAccurateBattlegroundsDedicatedServer

# client mods: point at a modded TABG client copy that has BepInEx installed
export TABG_CLIENT_DIR=~/.local/share/Steam/steamapps/common/TotallyAccurateBattlegrounds-Modded
dotnet build mods/Mods.sln -c Release
dotnet test mods/tests/ClientMods.Tests
dotnet run --project mods/tests/Vehicles.Tests
```

Game, Unity and BepInEx assemblies are never committed. The mods compile against your own install.

There is no CI. Run all checks locally before pushing:

```bash
scripts/check.sh          # Rust + vehicle rules
scripts/check.sh --mods   # also the C# client mods
```

## Legal

tabg is an unofficial fan project, not affiliated with Landfall Games. It contains no Landfall code
or assets; map data is extracted from your own copy of the game on your machine. MIT licensed.

Third-party code: `crates/tabg-enet-sys/vendor` contains ENet-CSharp 2.4.8 (MIT, © Stanislav Denisov and
the ENet authors).
