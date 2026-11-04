//! Join codes for tabg: Unity Relay and Unity Transport 1.x, compatible with unmodified TABG
//! clients.
//!
//! TABG's "join code" box calls `ConnectToRelayServer(code)`: the client signs in to Unity
//! anonymously, joins the code's Relay allocation and talks Unity Transport through the relay.
//! [`RelayHost`] is the server side of that, as a [`tabg_net::Transport`]:
//!
//! 1. [`ugs`]: anonymous sign-in and Relay REST calls (allocate, join code).
//! 2. [`relay`]: the relay's UDP messages (bind with HMAC, ping, relay framing).
//! 3. [`conn`]: Unity Transport's connection protocol and TABG's two pipelines.

pub mod client;
pub mod conn;
pub mod host;
mod link;
pub mod relay;
pub mod ugs;

use std::path::Path;

pub use client::RelayClient;
pub use host::{RelayConfig, RelayHost};
use thiserror::Error;

#[derive(Debug, Error)]
pub enum UtpError {
    #[error("{0}")]
    Http(String),
    #[error("{0}")]
    Relay(String),
    #[error("socket: {0}")]
    Io(std::io::Error),
    #[error("{0}")]
    ProjectId(String),
}

/// Read TABG's Unity Cloud project id from a game install (client or dedicated server).
///
/// It lives in `*_Data/globalgamemanagers` (PlayerSettings.cloudProjectId) as a length-prefixed
/// UUID string. We read it from the host's own files instead of shipping it.
pub fn project_id_from_game(game_dir: &Path) -> Result<String, UtpError> {
    let data_dirs = std::fs::read_dir(game_dir)
        .map_err(|e| UtpError::ProjectId(format!("{}: {e}", game_dir.display())))?;
    for entry in data_dirs.flatten() {
        let name = entry.file_name();
        if !name.to_string_lossy().ends_with("_Data") {
            continue;
        }
        let file = entry.path().join("globalgamemanagers");
        if let Ok(bytes) = std::fs::read(&file)
            && let Some(id) = find_project_id(&bytes)
        {
            return Ok(id);
        }
    }
    Err(UtpError::ProjectId(format!(
        "no Unity project id found under {} (expected *_Data/globalgamemanagers)",
        game_dir.display()
    )))
}

fn is_uuid(s: &[u8]) -> bool {
    s.len() == 36
        && s.iter().enumerate().all(|(i, &c)| match i {
            8 | 13 | 18 | 23 => c == b'-',
            _ => c.is_ascii_hexdigit() && !c.is_ascii_uppercase(),
        })
}

/// The first UUID stored as a Unity string (`u32` length 36, then the text).
pub fn find_project_id(bytes: &[u8]) -> Option<String> {
    let prefix = 36u32.to_le_bytes();
    bytes.windows(40).find_map(|w| {
        (w[..4] == prefix && is_uuid(&w[4..]))
            .then(|| String::from_utf8_lossy(&w[4..]).into_owned())
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn finds_length_prefixed_uuid() {
        let mut b = b"junk\x24\x00\x00\x00not-a-uuid-but-36-characters-long!!".to_vec();
        b.extend_from_slice(&36u32.to_le_bytes());
        b.extend_from_slice(b"01234567-89ab-cdef-0123-456789abcdef");
        b.extend_from_slice(b"\x00\x00\x00\x00");
        assert_eq!(
            find_project_id(&b).as_deref(),
            Some("01234567-89ab-cdef-0123-456789abcdef")
        );
        assert_eq!(find_project_id(b"nothing here"), None);
    }
}
