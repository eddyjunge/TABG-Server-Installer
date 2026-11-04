//! Where tabg keeps its data: `~/.local/share/tabg` on Linux, `%APPDATA%\tabg` on Windows, or
//! `$TABG_HOME`.

use std::path::{Path, PathBuf};

#[derive(Clone, Debug)]
pub struct Paths {
    pub root: PathBuf,
}

impl Paths {
    pub fn new(root: PathBuf) -> Self {
        Self { root }
    }

    pub fn default_root() -> PathBuf {
        if let Some(home) = std::env::var_os("TABG_HOME") {
            return PathBuf::from(home);
        }
        dirs::data_dir()
            .unwrap_or_else(|| PathBuf::from("."))
            .join("tabg")
    }

    pub fn from_env() -> Self {
        Self::new(Self::default_root())
    }

    /// The one server folder (v1 hosts one server).
    pub fn server(&self) -> PathBuf {
        self.root.join("servers").join("default")
    }
    pub fn config(&self) -> PathBuf {
        self.server().join("tabg.toml")
    }
    pub fn players(&self) -> PathBuf {
        self.server().join("players.json")
    }
    pub fn backups(&self) -> PathBuf {
        self.server().join("backups")
    }
    pub fn logs(&self) -> PathBuf {
        self.server().join("logs")
    }
    pub fn presets(&self) -> PathBuf {
        self.root.join("presets")
    }
    /// Map data dumped from the host's game, one file per game version.
    pub fn world_cache(&self) -> PathBuf {
        self.root.join("world")
    }
    /// Downloads (BepInEx, client mods), by sha256.
    pub fn downloads(&self) -> PathBuf {
        self.root.join("cache")
    }
    /// Scratch copy of the dedicated server used to run the map dumper.
    pub fn dumper_workdir(&self) -> PathBuf {
        self.root.join("dumper")
    }
    /// Machine-wide settings: where the game is, which world file to use.
    pub fn install(&self) -> PathBuf {
        self.root.join("install.json")
    }
    /// The running host's address and API token, for the CLI.
    pub fn host_file(&self) -> PathBuf {
        self.root.join("host.json")
    }
    /// A friend's modded TABG copy made by `tabg join`.
    pub fn client_copy(&self) -> PathBuf {
        self.root.join("client")
    }
}

/// Write a file atomically (write a temp file next to it, then rename).
pub fn write_atomic(path: &Path, data: &[u8]) -> std::io::Result<()> {
    if let Some(dir) = path.parent() {
        std::fs::create_dir_all(dir)?;
    }
    let tmp = path.with_extension("tmp");
    std::fs::write(&tmp, data)?;
    std::fs::rename(&tmp, path)
}

/// Seconds since the Unix epoch.
pub fn now() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |d| d.as_secs())
}
