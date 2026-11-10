# Contributing

Thanks for helping with tabg. This file covers how to get set up, how changes reach `main`, and the
style the code, the window and the dashboard follow. The design, all decisions and the milestones
are in [docs/DESIGN.md](docs/DESIGN.md). Read that first if you plan anything bigger than a fix.

If you work with a coding agent, it reads [AGENTS.md](AGENTS.md). Those rules apply to you too.

## How changes reach `main`

`main` is protected. Nobody pushes to it directly except the maintainer. Everything else goes
through a pull request:

1. Branch off the latest `main` (`git switch -c fix/ring-damage origin/main`), or fork the repo.
2. Make one change per PR. A bug fix and an unrelated refactor are two PRs.
3. Run `scripts/check.sh` locally and make sure it passes. **There is no CI.** The check script is
   the CI, so a PR without a passing run won't be merged.
4. Open the PR against `main` and fill in the template: what changed, why, and how you tested it.
5. The maintainer reviews it. Pushing new commits dismisses an earlier approval, and every review
   conversation has to be resolved before merging.

Talk to the maintainer before starting on a new feature, a new game mode, a new crate or a new
dependency. That saves you work if it doesn't fit the design.

## Setup

You need:

- [Rust](https://rustup.rs/) 1.85 or newer (edition 2024), with `rustfmt` and `clippy`.
- For the C# client mods: the .NET 8 SDK and a TABG client copy with BepInEx installed
  (`TABG_CLIENT_DIR`, or copy `mods/Local.props.example` to `mods/Local.props`).
- For real-client tests: a TABG install through Steam. Linux runs it through Proton.

```bash
cargo build -p tabg                 # the binary: target/debug/tabg
cargo run -p tabg                   # opens the window
scripts/check.sh                    # fmt, clippy, all Rust tests, vehicle rules
scripts/check.sh --mods             # also builds and tests the client mods
```

Most work needs no game files at all. `tabg-probe` is a headless client that speaks the protocol,
and the server, game and host crates are tested against it.

## Repo layout

| Path | What lives there |
|---|---|
| `crates/tabg` | The binary: CLI entry point, starts the window or a subcommand |
| `crates/tabg-gui` | The window (eframe/egui) |
| `crates/tabg-web` | Dashboard and local API (axum, server-rendered HTML with maud, htmx, SSE) |
| `crates/tabg-host` | Config, presets, supervisor, player store, backups, setup, sharing |
| `crates/tabg-server` | The game server: tick loop over ENet or Unity Relay |
| `crates/tabg-game` | Pure game simulation: rooms, players, loot, ring, modes. No IO |
| `crates/tabg-protocol` | Event codes, message structs, .NET BinaryReader/Writer-compatible codec |
| `crates/tabg-net`, `tabg-enet-sys` | ENet transport and the vendored ENet-CSharp FFI |
| `crates/tabg-utp` | Unity Transport and Unity Relay join codes |
| `crates/tabg-world` | Map data loaded from the world dumper's JSON |
| `crates/tabg-probe` | Headless test client |
| `mods/` | C# BepInEx client mods, shared code, tests, and the Unity project for shaders |
| `presets/` | Built-in presets (TOML) |
| `scripts/` | Build, release, check and test scripts |

## Rust

- **Formatting:** plain `cargo fmt`, no custom `rustfmt.toml`. Four spaces, LF, as in `.editorconfig`.
- **Lints:** `cargo clippy --workspace --all-targets -- -D warnings` must be clean. Don't silence a
  lint with `#[allow]` unless you add a comment explaining why the lint is wrong in that spot.
- **Errors:** library crates return typed errors made with `thiserror`. No `unwrap()` or `expect()`
  on anything that comes from the network, a file or the user. A malformed packet must never crash
  the server. `expect("why this can't fail")` is fine for real invariants; `unwrap()` is fine in tests.
- **Keep `tabg-game` pure.** It takes connections, packets and the time, and returns packets to
  send. No sockets, threads, clocks or file access. That keeps every rule unit-testable.
- **Logging** goes through `tracing` (`info!`, `warn!`, `debug!`), never `println!` outside binaries and build scripts.
- **`unsafe`** only in the FFI crates (`tabg-enet-sys`, `tabg-net`) and only with a `// SAFETY:`
  comment.
- **Dependencies:** add them to the crate that needs them, prefer well-known crates, use `rustls`
  over OpenSSL, and ask first before adding anything heavy. The Linux build targets glibc 2.31 via
  zig (`scripts/build-release.sh`), so no system libraries that break that.
- **Comments and docs:** every module starts with a `//!` line saying what it is for. Comment *why*,
  not *what*. Protocol details that you found out by testing the real client deserve a comment,
  because nobody can rediscover them from the code.
- **Naming:** clear full words (`player_count`, not `pc`). Config keys are `snake_case` in TOML and
  grouped by section (`game.destruction`, `server.port`).

### Tests

- Unit tests live next to the code in `#[cfg(test)] mod tests` (or a `tests.rs` module).
  Integration tests go in `crates/<crate>/tests/`.
- A fix comes with a test that fails without it, where that's possible.
- Tests that need the network, Unity's servers or game files are `#[ignore = "reason"]` and read
  their paths from env vars (`TABG_CLIENT_DIR`, ...). `scripts/check.sh` must pass on a machine
  without TABG installed.
- The window is tested headless with `egui_kittest` (`crates/tabg-gui/src/tests.rs`). UI changes
  get a test there.
- No sleeps to wait for things. Drive time explicitly (the game takes `now` as an argument) or
  wait on a channel.

## C# client mods

- Mods are BepInEx plugins under `mods/src/<Name>`, built against `netstandard2.0`. Shared settings
  are in `mods/src/Directory.Build.props`. Code shared with tests goes in `mods/shared/`.
- Follow `.editorconfig`: braces on new lines, `var` where the type is obvious, `_camelCase` private
  fields, `PascalCase` public members.
- Every mod must be optional for players: a player without it can still join and play.
- Mods that the server relies on talk to it through the event codes documented in `tabg-protocol`.
  Change both sides in the same PR.
- After changing a mod, mention it in the PR. The maintainer republishes the mod catalog
  (`scripts/release-mods.sh --upload`); don't do that yourself.

## Design and styling

tabg is for people who just want to host a game for their friends. The window and the dashboard
should feel calm, plain and obvious. When in doubt, leave it out.

### The window (`tabg-gui`)

- All colours, fonts, radii and sizes come from `crates/tabg-gui/src/theme.rs`. Don't put a
  `Color32::from_rgb` or a hard-coded size anywhere else; add a named constant to the theme instead.
- The look: light, neutral greys (`BG`, `PANEL`, `LINE`), one blue accent (`ACCENT`, `#2563eb`) for
  the primary action, red (`DANGER`) only for destructive actions, green (`GOOD`) for "running".
- Fonts: Inter for text (15 px body, 13.5 px small, SemiBold for headings), JetBrains Mono for
  codes, IDs and logs.
- Corners are 8 px for cards and 6 px for fields. Controls are 30 px tall.
- Build screens from the helpers in `widgets.rs` (`card`, `row`, `button`, `primary`, `input`,
  `check`, `select`, `chip`, ...). If you need something new, add it there so it's reusable.
- One primary button per view. Destructive actions say what they break ("Restarts the server,
  which disconnects the 3 players online.") and ask before they do it.

### The dashboard (`tabg-web`)

- Server-rendered HTML (maud) with htmx and SSE. No JavaScript frameworks, no build step, no CDN:
  everything the page loads is in `crates/tabg-web/assets` and works offline.
- The styling is the `CSS` block in `pages.rs`, built on the CSS variables in `:root`
  (`--bg`, `--panel`, `--text`, `--muted`, `--line`, `--accent`, ...). Use the variables, never raw
  colours, so dark mode (`prefers-color-scheme`) keeps working.
- Reuse the existing classes (`.card`, `.row`, `.grid`, `.pill`, `button.plain`, `button.danger`)
  before adding new ones.
- It must work on a phone: no fixed widths, no horizontal scrolling.

### Words

- Write for players, not programmers. "Your server is online", not "Supervisor state: Running".
- Short sentences, sentence case for labels and buttons ("Save preset", not "Save Preset").
- Buttons say what happens ("Start server", "Copy code"), never "OK" or "Submit".
- Errors say what went wrong and what to do next.
- The README stays user-facing. Developer details go here or in `docs/DESIGN.md`.

## Commits

- One logical change per commit, written so `git log --oneline` reads like a changelog.
- Subject in the imperative, about 70 characters, with an area prefix when it helps:
  `Destruction: grenades and rockets break buildings`, `GUI: show the share code after setup`.
- The body explains why, not what. Reference issues with `#123`.

## What never goes into the repo

- Game files: no Landfall, Unity, BepInEx or Sirenix DLLs, no extracted assets, no decompiled code.
  `.gitignore` blocks `*.dll`, `*.exe` and friends; don't force-add them.
- No code copied from TABG. Implement from protocol knowledge and testing.
- No secrets, tokens, Steam credentials or player data. Local settings go in `mods/Local.props`
  or `.env`, both gitignored.
- No build output, screenshots from tests or scratch notes. Put screenshots in the PR description.

## Reporting bugs

Open an issue with: what you did, what you expected, what happened, your OS, the tabg version
(`tabg --version`) and the log from the window's Console tab or `tabg logs`.

By contributing you agree that your work is released under the [MIT License](LICENSE).
