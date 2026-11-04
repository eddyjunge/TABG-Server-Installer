//! Downloads pinned by sha256, cached in the data dir by hash.

use std::io::Read;
use std::path::{Path, PathBuf};
use std::time::Duration;

use sha2::{Digest, Sha256};

pub fn sha256_hex(data: &[u8]) -> String {
    Sha256::digest(data)
        .iter()
        .map(|b| format!("{b:02x}"))
        .collect()
}

pub fn sha256_file(path: &Path) -> std::io::Result<String> {
    Ok(sha256_hex(&std::fs::read(path)?))
}

/// Download `url` unless `cache/<sha256>` already exists; fail if the hash doesn't match.
pub fn download(url: &str, sha256: &str, cache: &Path) -> Result<PathBuf, String> {
    let path = cache.join(sha256);
    if path.exists() && sha256_file(&path).ok().as_deref() == Some(sha256) {
        return Ok(path);
    }
    let agent: ureq::Agent = ureq::Agent::config_builder()
        .timeout_global(Some(Duration::from_secs(120)))
        .build()
        .into();
    let mut response = agent.get(url).call().map_err(|e| format!("{url}: {e}"))?;
    let mut data = Vec::new();
    response
        .body_mut()
        .as_reader()
        .take(256 << 20)
        .read_to_end(&mut data)
        .map_err(|e| format!("{url}: {e}"))?;
    let got = sha256_hex(&data);
    if got != sha256 {
        return Err(format!("{url}: expected sha256 {sha256}, got {got}"));
    }
    crate::paths::write_atomic(&path, &data).map_err(|e| e.to_string())?;
    Ok(path)
}

/// Extract a zip into `dest`, refusing entries that would escape it.
pub fn unzip(zip_path: &Path, dest: &Path) -> Result<(), String> {
    let file = std::fs::File::open(zip_path).map_err(|e| e.to_string())?;
    let mut archive = zip::ZipArchive::new(file).map_err(|e| e.to_string())?;
    for i in 0..archive.len() {
        let mut entry = archive.by_index(i).map_err(|e| e.to_string())?;
        let Some(rel) = entry.enclosed_name() else {
            return Err(format!("unsafe path in {}", zip_path.display()));
        };
        let out = dest.join(rel);
        if entry.is_dir() {
            std::fs::create_dir_all(&out).map_err(|e| e.to_string())?;
            continue;
        }
        if let Some(parent) = out.parent() {
            std::fs::create_dir_all(parent).map_err(|e| e.to_string())?;
        }
        let mut f = std::fs::File::create(&out).map_err(|e| e.to_string())?;
        std::io::copy(&mut entry, &mut f).map_err(|e| e.to_string())?;
        #[cfg(unix)]
        if let Some(mode) = entry.unix_mode() {
            use std::os::unix::fs::PermissionsExt;
            let _ = std::fs::set_permissions(&out, std::fs::Permissions::from_mode(mode));
        }
    }
    Ok(())
}
