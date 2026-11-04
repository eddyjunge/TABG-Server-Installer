//! Find TABG installs through Steam's library folders (native and Flatpak Steam on Linux, the
//! registry or the default path on Windows).

use std::path::{Path, PathBuf};

/// TABG (the game).
pub const CLIENT_APP: u32 = 823_130;
/// TABG Dedicated Server (free tool; it holds the map files the dumper reads).
pub const SERVER_APP: u32 = 2_311_970;
pub const CLIENT_DIR: &str = "TotallyAccurateBattlegrounds";
pub const SERVER_DIR: &str = "TotallyAccurateBattlegroundsDedicatedServer";

fn steam_roots() -> Vec<PathBuf> {
    let mut roots = Vec::new();
    if let Some(p) = std::env::var_os("TABG_STEAM_DIR") {
        roots.push(PathBuf::from(p));
    }
    #[cfg(windows)]
    {
        use winreg::RegKey;
        use winreg::enums::HKEY_CURRENT_USER;
        if let Ok(key) = RegKey::predef(HKEY_CURRENT_USER).open_subkey("Software\\Valve\\Steam")
            && let Ok(path) = key.get_value::<String, _>("SteamPath")
        {
            roots.push(PathBuf::from(path));
        }
        roots.push(PathBuf::from("C:\\Program Files (x86)\\Steam"));
    }
    if let Some(home) = dirs::home_dir() {
        roots.push(home.join(".local/share/Steam"));
        roots.push(home.join(".steam/steam"));
        roots.push(home.join(".var/app/com.valvesoftware.Steam/.local/share/Steam"));
    }
    roots.retain(|r| r.join("steamapps").is_dir());
    roots
}

/// Every `"path"` in a `libraryfolders.vdf`.
pub fn parse_library_folders(vdf: &str) -> Vec<PathBuf> {
    vdf.lines()
        .filter_map(|line| {
            let mut parts = line.split('"').filter(|s| !s.trim().is_empty());
            (parts.next()? == "path").then(|| parts.next())?
        })
        .map(|p| PathBuf::from(p.replace("\\\\", "\\")))
        .collect()
}

/// All Steam library folders on this machine (each has a `steamapps/`).
pub fn libraries() -> Vec<PathBuf> {
    let mut libs: Vec<PathBuf> = Vec::new();
    for root in steam_roots() {
        let root = root.canonicalize().unwrap_or(root);
        if !libs.contains(&root) {
            libs.push(root.clone());
        }
        if let Ok(vdf) = std::fs::read_to_string(root.join("steamapps/libraryfolders.vdf")) {
            for lib in parse_library_folders(&vdf) {
                let lib = lib.canonicalize().unwrap_or(lib);
                if lib.join("steamapps").is_dir() && !libs.contains(&lib) {
                    libs.push(lib);
                }
            }
        }
    }
    libs
}

fn find(dir_name: &str, marker: impl Fn(&Path) -> bool) -> Option<PathBuf> {
    libraries()
        .into_iter()
        .map(|l| l.join("steamapps/common").join(dir_name))
        .find(|p| marker(p))
}

pub fn is_client(dir: &Path) -> bool {
    dir.join("TotallyAccurateBattlegrounds_Data").is_dir()
}

pub fn is_server(dir: &Path) -> bool {
    dir.join("TABG_Data").is_dir()
}

/// The TABG game install.
pub fn find_client() -> Option<PathBuf> {
    find(CLIENT_DIR, is_client)
}

/// The TABG Dedicated Server install.
pub fn find_server() -> Option<PathBuf> {
    find(SERVER_DIR, is_server)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_library_paths() {
        let vdf = r#""libraryfolders"
{
	"0"
	{
		"path"		"/home/me/.local/share/Steam"
		"label"		""
	}
	"1"
	{
		"path"		"D:\\SteamLibrary"
	}
}"#;
        assert_eq!(
            parse_library_folders(vdf),
            vec![
                PathBuf::from("/home/me/.local/share/Steam"),
                PathBuf::from("D:\\SteamLibrary")
            ]
        );
    }
}
