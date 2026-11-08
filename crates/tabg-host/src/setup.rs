//! First-run setup: find the game, extract the map data from the host's own TABG Dedicated
//! Server with the dumper plugin (§4.5), and remember where things are in `install.json`.

use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};

use crate::fetch;
use crate::paths::{Paths, write_atomic};
use crate::steam;

/// BepInEx 5.4.23.3 for the dumper run, pinned by hash.
#[cfg(not(windows))]
const BEPINEX: (&str, &str) = (
    "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.3/BepInEx_linux_x64_5.4.23.3.zip",
    "b9ef28b37676f18277cfd8dded01f675e934f207c30f65f2a4bb93c7e41abdbb",
);
#[cfg(windows)]
const BEPINEX: (&str, &str) = (
    "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.3/BepInEx_win_x64_5.4.23.3.zip",
    "41a089e5b1b1f0713b331346baf6677b1184c69eabebf51101097954e854c749",
);

const EMBEDDED_DUMPER: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/dumper.dll"));

/// Port the dump run's server listens on, far from the usual 7777.
const DUMP_PORT: u16 = 47_999;
const DUMP_TIMEOUT: Duration = Duration::from_secs(300);
/// How long the walkability grid may take after the map data is written.
const NAV_TIMEOUT: Duration = Duration::from_secs(60);

/// What tabg knows about this machine's TABG.
#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(default)]
pub struct Install {
    /// The game (for the Unity project id and for friends' modded copies).
    pub client_dir: Option<PathBuf>,
    /// TABG Dedicated Server (source of the map data).
    pub server_dir: Option<PathBuf>,
    /// Current map data file.
    pub world: Option<PathBuf>,
    /// Game version of that map data (`APP_VERSION`).
    pub app_version: String,
    /// Brawl map data (a second export with the server in Brawl mode).
    pub brawl_world: Option<PathBuf>,
    /// Bomb map data (TABG_CS, exported with the server in Bomb mode).
    pub bomb_world: Option<PathBuf>,
}

impl Install {
    pub fn load(paths: &Paths) -> Self {
        std::fs::read(paths.install())
            .ok()
            .and_then(|b| serde_json::from_slice(&b).ok())
            .unwrap_or_default()
    }

    pub fn save(&self, paths: &Paths) -> std::io::Result<()> {
        write_atomic(
            &paths.install(),
            &serde_json::to_vec_pretty(self).expect("install serializes"),
        )
    }

    /// Fill in game folders Steam knows about.
    pub fn detect(&mut self) {
        if self
            .client_dir
            .as_deref()
            .is_none_or(|d| !steam::is_client(d))
        {
            self.client_dir = steam::find_client();
        }
        if self
            .server_dir
            .as_deref()
            .is_none_or(|d| !steam::is_server(d))
        {
            self.server_dir = steam::find_server();
        }
    }

    /// A game folder to read the Unity project id from.
    pub fn game_dir(&self) -> Option<&Path> {
        self.client_dir.as_deref().or(self.server_dir.as_deref())
    }

    /// Ready to host: map data exists.
    pub fn ready(&self) -> bool {
        self.world.as_deref().is_some_and(Path::exists)
    }
}

/// The dumper plugin: embedded in the binary, or `TABG_DUMPER_DLL`.
fn dumper_dll() -> Result<Vec<u8>, String> {
    if let Some(p) = std::env::var_os("TABG_DUMPER_DLL") {
        return std::fs::read(&p).map_err(|e| format!("{}: {e}", PathBuf::from(p).display()));
    }
    if !EMBEDDED_DUMPER.is_empty() {
        return Ok(EMBEDDED_DUMPER.to_vec());
    }
    Err("this tabg build has no map dumper; build mods/src/WorldDumper (Release) and rebuild tabg, \
         or set TABG_DUMPER_DLL"
        .into())
}

/// Hard-link (instant, no space) or copy `src` into `dst`, recursively.
fn mirror(src: &Path, dst: &Path) -> std::io::Result<()> {
    if src.is_dir() {
        std::fs::create_dir_all(dst)?;
        for entry in std::fs::read_dir(src)? {
            let entry = entry?;
            mirror(&entry.path(), &dst.join(entry.file_name()))?;
        }
        Ok(())
    } else {
        if dst.exists() {
            std::fs::remove_file(dst)?;
        }
        std::fs::hard_link(src, dst).or_else(|_| std::fs::copy(src, dst).map(|_| ()))
    }
}

/// Files of the dedicated server we leave out of the dump copy (modding leftovers, backups).
const SKIP: &[&str] = &[
    "BepInEx",
    "doorstop_libs",
    "doorstop_config.ini",
    "winhttp.dll",
    "libdoorstop.so",
    ".doorstop_version",
    "run_bepinex.sh",
    "backup",
    "game_settings.txt",
    "tabg-world.json",
];

/// game_settings.txt for the dump run: a private, direct-IP server on an unusual port, in
/// `mode` (TABG's `GameMode` name).
fn dump_settings(original: &str, mode: &str) -> String {
    let overrides = [
        ("GameMode", mode.to_string()),
        ("ServerName", "tabg map export".to_string()),
        ("Port", DUMP_PORT.to_string()),
        ("Relay", "false".into()),
        ("AntiCheat", "false".into()),
        ("UsePlayFabStats", "false".into()),
        ("Password", "tabg-dump".into()),
    ];
    let mut out: Vec<String> = original
        .lines()
        .filter(|l| {
            let key = l.split('=').next().unwrap_or("").trim();
            !overrides.iter().any(|(k, _)| *k == key)
        })
        .map(str::to_owned)
        .collect();
    out.extend(overrides.iter().map(|(k, v)| format!("{k}={v}")));
    out.join("\n") + "\n"
}

/// Run the map dumper against the dedicated server at `server_dir` in game mode `mode`
/// (`BattleRoyale`, `Brawl`); returns the world file.
pub fn dump_world(
    paths: &Paths,
    server_dir: &Path,
    mode: &str,
    progress: &mut dyn FnMut(&str),
) -> Result<PathBuf, String> {
    if !steam::is_server(server_dir) {
        return Err(format!(
            "{} is not a TABG Dedicated Server folder (no TABG_Data)",
            server_dir.display()
        ));
    }
    let dll = dumper_dll()?;
    let work = paths.dumper_workdir().join("server");
    progress("preparing a copy of the dedicated server");
    let _ = std::fs::remove_dir_all(&work);
    std::fs::create_dir_all(&work).map_err(|e| e.to_string())?;
    for entry in std::fs::read_dir(server_dir)
        .map_err(|e| e.to_string())?
        .flatten()
    {
        let name = entry.file_name();
        if SKIP.iter().any(|s| name == *s) {
            continue;
        }
        mirror(&entry.path(), &work.join(&name)).map_err(|e| format!("copy {name:?}: {e}"))?;
    }
    let original =
        std::fs::read_to_string(server_dir.join("game_settings.txt")).unwrap_or_default();
    std::fs::write(
        work.join("game_settings.txt"),
        dump_settings(&original, mode),
    )
    .map_err(|e| e.to_string())?;

    progress("getting BepInEx 5.4.23.3");
    let zip = fetch::download(BEPINEX.0, BEPINEX.1, &paths.downloads())?;
    fetch::unzip(&zip, &work)?;
    let plugins = work.join("BepInEx/plugins");
    std::fs::create_dir_all(&plugins).map_err(|e| e.to_string())?;
    std::fs::write(plugins.join("Tabg.WorldDumper.dll"), dll).map_err(|e| e.to_string())?;

    let out = paths.dumper_workdir().join("world.json");
    let _ = std::fs::remove_file(&out);
    let _ = std::fs::remove_file(out.with_extension("nav"));
    progress("loading the map in the dedicated server (about a minute)");
    let mut cmd = Command::new(work.join("TABG.exe"));
    cmd.current_dir(&work)
        .args(["-batchmode", "-nographics", "-logFile"])
        .arg(work.join("server.log"))
        .env("TABG_WORLD_OUT", &out)
        .env("TABG_WORLD_QUIT", "1")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null());
    #[cfg(not(windows))]
    {
        let ld_path = format!(
            "{}:{}",
            work.display(),
            std::env::var("LD_LIBRARY_PATH").unwrap_or_default()
        );
        cmd.env("LD_PRELOAD", work.join("libdoorstop.so"))
            .env("LD_LIBRARY_PATH", ld_path)
            .env("DOORSTOP_ENABLED", "1")
            .env(
                "DOORSTOP_TARGET_ASSEMBLY",
                work.join("BepInEx/core/BepInEx.Preloader.dll"),
            );
        use std::os::unix::fs::PermissionsExt;
        let _ = std::fs::set_permissions(
            work.join("TABG.exe"),
            std::fs::Permissions::from_mode(0o755),
        );
    }
    let mut child = cmd.spawn().map_err(|e| format!("start TABG.exe: {e}"))?;
    let started = Instant::now();
    loop {
        if let Some(status) = child.try_wait().map_err(|e| e.to_string())? {
            tracing::info!(%status, "dump server exited");
            break;
        }
        if started.elapsed() > DUMP_TIMEOUT {
            let _ = child.kill();
            break;
        }
        // The plugin quits the game after writing, but don't wait for a slow shutdown. It writes
        // the JSON first and the bots' walkability grid after it, so wait for a whole grid too.
        if out.exists() && std::fs::metadata(&out).is_ok_and(|m| m.len() > 0) {
            let deadline = Instant::now() + NAV_TIMEOUT;
            while Instant::now() < deadline
                && tabg_server::load_nav(&out).is_none()
                && child.try_wait().ok().flatten().is_none()
            {
                std::thread::sleep(Duration::from_millis(250));
            }
            std::thread::sleep(Duration::from_secs(1));
            let _ = child.kill();
            break;
        }
        std::thread::sleep(Duration::from_millis(500));
    }
    let _ = child.wait();
    let world = tabg_world::World::load(&out).map_err(|e| {
        format!(
            "the map export produced no usable data ({e}); see {} and {}",
            work.join("server.log").display(),
            work.join("BepInEx/LogOutput.log").display()
        )
    })?;
    let version = if world.app_version.is_empty() {
        "unknown".to_owned()
    } else {
        world.app_version.clone()
    };
    let suffix = if mode == "BattleRoyale" {
        String::new()
    } else {
        format!("-{}", mode.to_ascii_lowercase())
    };
    let cached = paths.world_cache().join(format!("{version}{suffix}.json"));
    std::fs::create_dir_all(paths.world_cache()).map_err(|e| e.to_string())?;
    std::fs::copy(&out, &cached).map_err(|e| e.to_string())?;
    // The bots' walkability grid comes out next to the JSON.
    let nav = out.with_extension("nav");
    if nav.exists() {
        std::fs::copy(&nav, cached.with_extension("nav")).map_err(|e| e.to_string())?;
    }
    progress(&match &world.brawl {
        None if world.bomb.is_some() => "Bomb map data ready".to_owned(),
        Some(b) => format!(
            "Brawl map data ready: {} team houses, {} solo houses, {} loot presets",
            b.team_houses.len(),
            b.solo_houses.len(),
            b.presets.len()
        ),
        None => format!(
            "map data ready: {} spawn points, {} loot spots, {} cars",
            world.spawn_points.len(),
            world.loot_spots.len(),
            world.cars.len()
        ),
    });
    Ok(cached)
}

/// Detect the game, dump the map data if needed and save `install.json`.
pub fn run(paths: &Paths, force: bool, progress: &mut dyn FnMut(&str)) -> Result<Install, String> {
    let mut install = Install::load(paths);
    install.detect();
    match &install.client_dir {
        Some(d) => progress(&format!("TABG: {}", d.display())),
        None => {
            progress("TABG not found (only needed for join codes; set client_dir in install.json)")
        }
    }
    let Some(server) = install.server_dir.clone() else {
        return Err(
            "TABG Dedicated Server not found. Install it from Steam (Library → Tools → \
             \"TABG Dedicated Server\"), then run setup again."
                .into(),
        );
    };
    progress(&format!("TABG Dedicated Server: {}", server.display()));
    if force || !install.ready() {
        let world = dump_world(paths, &server, "BattleRoyale", progress)?;
        install.app_version = tabg_world::World::load(&world)
            .map(|w| w.app_version)
            .unwrap_or_default();
        install.world = Some(world);
    }
    let mut did_any = false;
    for (mode, slot) in [
        ("Brawl", &mut install.brawl_world),
        ("Bomb", &mut install.bomb_world),
    ] {
        if force || slot.as_deref().is_none_or(|p| !p.exists()) {
            did_any = true;
            progress(&format!("exporting the {mode} map too"));
            match dump_world(paths, &server, mode, progress) {
                Ok(w) => *slot = Some(w),
                Err(e) => progress(&format!(
                    "{mode} map export failed ({mode} won't be available): {e}"
                )),
            }
        }
    }
    if !did_any && !force {
        progress("map data already extracted (use --force to redo it)");
    }
    install.save(paths).map_err(|e| e.to_string())?;
    Ok(install)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dump_settings_override_network_keys() {
        let s = dump_settings(
            "ServerName=Mine\nPort=7777\nMaxPlayers=70\nGameMode=Test\n",
            "Brawl",
        );
        assert!(s.contains("GameMode=Brawl") && !s.contains("GameMode=Test"));
        assert!(s.contains("MaxPlayers=70"));
        assert!(s.contains("Port=47999"));
        assert!(s.contains("Relay=false"));
        assert!(!s.contains("Port=7777"));
        assert!(!s.contains("ServerName=Mine"));
    }
}
