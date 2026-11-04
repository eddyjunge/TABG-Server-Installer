//! Config backups: before every apply, `tabg.toml` and `players.json` are copied into
//! `backups/<unix time>[-n]/`. Restoring copies them back (after backing up the current state).
//! Game files are never copied.

use std::path::{Path, PathBuf};

use serde::Serialize;

use crate::paths::{Paths, now};

/// How many backups to keep.
pub const KEEP: usize = 50;
const FILES: &[&str] = &["tabg.toml", "players.json"];

#[derive(Clone, Debug, Serialize, PartialEq, Eq)]
pub struct Backup {
    pub id: String,
    pub created: u64,
    /// Why it was made (e.g. "before apply", "before restore").
    pub reason: String,
}

fn dirs(paths: &Paths) -> Vec<PathBuf> {
    let mut v: Vec<PathBuf> = std::fs::read_dir(paths.backups())
        .map(|r| {
            r.flatten()
                .map(|e| e.path())
                .filter(|p| p.is_dir())
                .collect()
        })
        .unwrap_or_default();
    v.sort();
    v
}

/// Snapshot the current files. Returns `None` when there's nothing to back up yet.
pub fn create(paths: &Paths, reason: &str) -> std::io::Result<Option<Backup>> {
    let server = paths.server();
    if !FILES.iter().any(|f| server.join(f).exists()) {
        return Ok(None);
    }
    let created = now();
    let mut id = format!("{created:012}");
    let mut n = 1;
    while paths.backups().join(&id).exists() {
        n += 1;
        id = format!("{created:012}-{n}");
    }
    let dir = paths.backups().join(&id);
    std::fs::create_dir_all(&dir)?;
    for f in FILES {
        let src = server.join(f);
        if src.exists() {
            std::fs::copy(&src, dir.join(f))?;
        }
    }
    std::fs::write(dir.join("reason.txt"), reason)?;
    let all = dirs(paths);
    if all.len() > KEEP {
        for old in &all[..all.len() - KEEP] {
            let _ = std::fs::remove_dir_all(old);
        }
    }
    Ok(Some(Backup {
        id,
        created,
        reason: reason.into(),
    }))
}

fn read(dir: &Path) -> Option<Backup> {
    let id = dir.file_name()?.to_str()?.to_owned();
    let created = id.split('-').next()?.parse().ok()?;
    let reason = std::fs::read_to_string(dir.join("reason.txt")).unwrap_or_default();
    Some(Backup {
        id,
        created,
        reason,
    })
}

/// Newest first.
pub fn list(paths: &Paths) -> Vec<Backup> {
    let mut v: Vec<Backup> = dirs(paths).iter().filter_map(|d| read(d)).collect();
    v.reverse();
    v
}

/// Put a backup's files back. The current state is backed up first.
pub fn restore(paths: &Paths, id: &str) -> Result<(), String> {
    if id.contains(['/', '\\', '.']) {
        return Err("bad backup id".into());
    }
    let dir = paths.backups().join(id);
    if !dir.is_dir() {
        return Err(format!("no backup {id}"));
    }
    create(paths, &format!("before restoring {id}")).map_err(|e| e.to_string())?;
    for f in FILES {
        let src = dir.join(f);
        if src.exists() {
            std::fs::copy(&src, paths.server().join(f)).map_err(|e| e.to_string())?;
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn backup_list_restore() {
        let dir = tempfile::tempdir().unwrap();
        let paths = Paths::new(dir.path().to_owned());
        assert_eq!(create(&paths, "x").unwrap(), None, "nothing to back up");
        std::fs::create_dir_all(paths.server()).unwrap();
        std::fs::write(paths.config(), "name = \"one\"").unwrap();
        let first = create(&paths, "before apply").unwrap().unwrap();
        std::fs::write(paths.config(), "name = \"two\"").unwrap();
        restore(&paths, &first.id).unwrap();
        assert_eq!(
            std::fs::read_to_string(paths.config()).unwrap(),
            "name = \"one\""
        );
        let all = list(&paths);
        assert_eq!(all.len(), 2);
        assert!(all[0].reason.starts_with("before restoring"));
        assert!(restore(&paths, "../etc").is_err());
    }
}
