# tabg

tabg hosts [Totally Accurate Battlegrounds](https://store.steampowered.com/app/823130/) servers for
friends. It is one Rust program for Windows and Linux: a window, a browser dashboard, a CLI, and its
own game server that speaks TABG's network protocol. Optional C# BepInEx client mods add features
on the players' side.

These notes are for coding agents working in this repo (Claude Code, Codex, Cursor and others).
They are good defaults, not laws: if one fights the task in front of you, say so plainly and let
the developer decide. Everything in [CONTRIBUTING.md](CONTRIBUTING.md) applies to you as well,
especially the Rust, design and styling rules. Read it before you change code.

## What makes tabg tabg

- **One program, no fuss.** A player who has never hosted a server should get one running in a few
  minutes. Every feature is judged by whether it keeps that true.
- **The game server is ours.** `tabg-server` and `tabg-game` reimplement what the client expects.
  The real TABG client is the ground truth; the code is only right if a real client agrees.
- **Clean of Landfall's property.** No Landfall code, DLLs, assets or decompiled output ever goes into
  git. Implement from protocol knowledge and testing.
- **Works offline and locally.** No CI, no CDN, no telemetry. Everything builds and tests on the
  developer's machine.

The design, every decision and the milestones are in [docs/DESIGN.md](docs/DESIGN.md). Treat its
decisions as settled unless the developer reopens them.

## Ways to hurt yourself

- **Never kill processes by pattern.** No `pkill -f tabg` or `killall`: the pattern also matches
  your own shell and other people's work. Kill a PID you captured when you started it.
- **Never touch the developer's real installs.** The Steam TABG folders and the tabg data dir are
  read-only for you. Tests use temp dirs, and `scripts/real-client-test.sh` uses its own copy of the
  client (`TABG_TEST_CLIENT`).
- **Never put a game window on the developer's screen.** Real-client tests run headless by default.
  Only set `TABG_TEST_WINDOW` when you were asked for screenshots, and keep the window off the
  developer's active output.
- **Never publish.** No `scripts/release-mods.sh --upload`, no GitHub releases, no tags unless the
  developer explicitly asks.

## Verifying

- Run `scripts/check.sh` before you call anything done. It is the whole CI: `cargo fmt --check`,
  clippy with `-D warnings`, all workspace tests and the vehicle rules. Add `--mods` when you
  touched `mods/`.
- While iterating, scope it: `cargo test -p tabg-game`, `cargo clippy -p tabg-gui`.
- Build and test locally. Install a missing toolchain instead of saying "CI will check it".
- Behaviour the client sees needs proof from a client: `tabg-probe` in tests, and
  `scripts/real-client-test.sh` when the change is about what players see or do. Say which you ran.
- UI changes get an `egui_kittest` test in `crates/tabg-gui/src/tests.rs`.
- Report results honestly. If something failed or you skipped a step, say so with the output.

## Pull requests and commits

- `main` is protected. Work on a branch and open a PR against `main`, unless the maintainer told
  you to push directly.
- Only commit, push or open a PR when the developer asks.
- One problem per PR. Fill in `.github/pull_request_template.md`: what, why, and how you tested it.
- Commit subjects follow the existing log: imperative, short, with an area prefix when it helps
  (`Destruction: grenades and rockets break buildings`).
- Don't add AI attribution lines (`Co-Authored-By`, "Generated with") to commits or PRs.

## Where code lives

| Crate | Role |
|---|---|
| `tabg` | The binary and CLI |
| `tabg-gui` | The window (egui). Styling only through `theme.rs` and `widgets.rs` |
| `tabg-web` | Dashboard (axum, maud, htmx, SSE). Styling only through the CSS variables in `pages.rs` |
| `tabg-host` | Config (`tabg.toml`), presets, supervisor, player store, backups, setup, share codes |
| `tabg-server` | Tick loop: network + game + world |
| `tabg-game` | Pure simulation. No IO, no clocks: time comes in as an argument |
| `tabg-protocol` | Event codes and the wire codec. Changes here must stay byte-compatible with the client |
| `tabg-net`, `tabg-enet-sys` | ENet. The only crates with `unsafe` |
| `tabg-utp` | Unity Relay and join codes |
| `tabg-world` | Map data from the world dumper |
| `tabg-probe` | Headless test client |

`mods/` holds the C# client mods. A mod and the server code that talks to it change in the same PR.

## Taste

- The simplest thing that works. No abstraction for one caller, no option nobody asked for.
- New settings get a sensible default, so a host who never opens the settings still gets a good
  game. Risky features (like `game.destruction`) are off by default.
- Players can be anyone. Never trust a packet: validate, clamp, and drop what doesn't fit, but
  don't crash and don't kick for honest lag.
- UI text is for players: short, plain, sentence case, buttons that say what happens.
- The README is user-facing and keeps its current look (banner, tables, Quick Start). Developer
  details belong in CONTRIBUTING.md or docs/DESIGN.md.
- Comments explain why. Protocol facts you learned from testing the real client are worth a
  comment, because they can't be rediscovered from the code.
- Don't commit plans, notes, screenshots or scratch files.
