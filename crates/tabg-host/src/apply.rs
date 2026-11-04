//! Change the config: validate, back up, write, then make the running server follow (reload in
//! place, or restart when the network or player limit changed).

use serde::Serialize;

use crate::backup;
use crate::config::{Problem, ServerToml};
use crate::paths::{Paths, write_atomic};
use crate::supervisor::Supervisor;

/// The config on disk, or the default one (written) on first run.
pub fn read_config(paths: &Paths) -> Result<ServerToml, String> {
    match std::fs::read_to_string(paths.config()) {
        Ok(text) => ServerToml::parse(&text).map_err(|e| format!("tabg.toml: {e}")),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
            let c = ServerToml::default();
            write_atomic(&paths.config(), c.to_toml().as_bytes()).map_err(|e| e.to_string())?;
            Ok(c)
        }
        Err(e) => Err(e.to_string()),
    }
}

/// Validate, back up the current files and write `config`.
pub fn write_config(paths: &Paths, config: &ServerToml, reason: &str) -> Result<(), String> {
    if let Some(p) = config.validate().first() {
        return Err(format!("{}: {}", p.field, p.message));
    }
    backup::create(paths, &format!("before {reason}")).map_err(|e| e.to_string())?;
    write_atomic(&paths.config(), config.to_toml().as_bytes()).map_err(|e| e.to_string())
}

#[derive(Clone, Debug, Serialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum Applied {
    /// Saved; the server isn't running.
    Saved,
    /// The running server took the new settings.
    Reloaded,
    /// The running server was restarted (network, name or player limit changed).
    Restarted,
}

#[derive(Clone, Debug, Serialize, PartialEq, Eq)]
#[serde(untagged)]
pub enum ApplyError {
    Invalid { problems: Vec<Problem> },
    Failed { error: String },
}

impl std::fmt::Display for ApplyError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Invalid { problems } => {
                let list: Vec<String> = problems
                    .iter()
                    .map(|p| format!("{}: {}", p.field, p.message))
                    .collect();
                write!(f, "{}", list.join("; "))
            }
            Self::Failed { error } => write!(f, "{error}"),
        }
    }
}

pub fn apply(
    paths: &Paths,
    supervisor: &Supervisor,
    next: &ServerToml,
    reason: &str,
) -> Result<Applied, ApplyError> {
    let problems = next.validate();
    if !problems.is_empty() {
        return Err(ApplyError::Invalid { problems });
    }
    let current = read_config(paths).ok();
    write_config(paths, next, reason).map_err(|error| ApplyError::Failed { error })?;
    supervisor.host_log(format!("config applied ({reason})"));
    if !supervisor.is_running() {
        return Ok(Applied::Saved);
    }
    if current.is_none_or(|c| c.needs_restart(next)) {
        supervisor.restart();
        Ok(Applied::Restarted)
    } else {
        supervisor
            .reload()
            .map_err(|error| ApplyError::Failed { error })?;
        Ok(Applied::Reloaded)
    }
}
