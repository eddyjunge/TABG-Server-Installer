//! The game server runs as a child process (`tabg serve`), so a crash in game logic never takes
//! the dashboard down. Commands go in on its stdin and events come out on its stdout, as JSON
//! lines; its log goes to stderr.

use std::io::{BufRead, Write};
use std::sync::Arc;
use std::sync::atomic::AtomicBool;
use std::sync::mpsc::{Receiver, TryRecvError};

use serde::{Deserialize, Serialize};
use tabg_game::RoomSettings;
use tabg_server::{Command, Control, Event, ServerConfig};

use crate::config::{NetMode, ServerToml};
use crate::paths::Paths;
use crate::setup::Install;

/// Host → server.
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(tag = "cmd", rename_all = "snake_case")]
pub enum ToServer {
    Console {
        line: String,
    },
    /// Re-read tabg.toml and apply what can change while running.
    Reload,
    Stop,
}

/// Everything a server needs, read from the data dir.
pub struct Loaded {
    pub config: ServerToml,
    pub install: Install,
    pub world: tabg_world::World,
}

pub fn load(paths: &Paths) -> Result<Loaded, String> {
    let text = std::fs::read_to_string(paths.config())
        .map_err(|e| format!("{}: {e}", paths.config().display()))?;
    let config = ServerToml::parse(&text)?;
    let problems = config.validate();
    if let Some(p) = problems.first() {
        return Err(format!("tabg.toml: {}: {}", p.field, p.message));
    }
    let install = Install::load(paths);
    let world_path = install
        .world
        .clone()
        .ok_or("no map data yet: run `tabg setup` first")?;
    let world = tabg_world::World::load(&world_path).map_err(|e| e.to_string())?;
    Ok(Loaded {
        config,
        install,
        world,
    })
}

/// Map data plus the config's rules.
pub fn room_settings(loaded: &Loaded) -> Result<RoomSettings, String> {
    let mut base = RoomSettings::default();
    tabg_server::apply_world(&mut base, &loaded.world)?;
    let mut s = loaded.config.room_settings(&base);
    s.seed = crate::paths::now() ^ 0x9e37_79b9_7f4a_7c15;
    Ok(s)
}

pub fn server_config(loaded: &Loaded) -> Result<ServerConfig, String> {
    let c = &loaded.config;
    let settings = room_settings(loaded)?;
    let relay = match c.network.mode {
        NetMode::Relay => {
            let dir = loaded
                .install
                .game_dir()
                .ok_or("join codes need TABG installed (for its Unity project id)")?;
            Some(tabg_utp::RelayConfig {
                project_id: tabg_utp::project_id_from_game(dir).map_err(|e| e.to_string())?,
                region: (!c.network.region.is_empty()).then(|| c.network.region.clone()),
                max_players: u32::from(c.game.max_players),
            })
        }
        NetMode::Direct => None,
    };
    let listing = c
        .network
        .public
        .then(|| tabg_server::listing::ListingConfig {
            name: c.name.clone(),
            description: c.description.clone(),
            version: loaded.world.app_version.clone(),
        });
    Ok(ServerConfig {
        bind: "0.0.0.0".into(),
        port: c.network.port,
        relay,
        listing,
        upnp: c.network.mode == NetMode::Direct && c.network.upnp,
        settings,
    })
}

struct StdioControl {
    commands: Receiver<ToServer>,
    paths: Paths,
}

impl Control for StdioControl {
    fn poll(&mut self) -> Option<Command> {
        match self.commands.try_recv() {
            Ok(ToServer::Console { line }) => Some(Command::Console(line)),
            Ok(ToServer::Reload) => match load(&self.paths).and_then(|l| room_settings(&l)) {
                Ok(s) => Some(Command::Settings(Box::new(s))),
                Err(e) => {
                    tracing::warn!("reload failed: {e}");
                    None
                }
            },
            Ok(ToServer::Stop) | Err(TryRecvError::Disconnected) => Some(Command::Stop),
            Err(TryRecvError::Empty) => None,
        }
    }

    fn emit(&mut self, event: Event) {
        let mut out = std::io::stdout().lock();
        if let Ok(line) = serde_json::to_string(&event) {
            let _ = writeln!(out, "{line}");
            let _ = out.flush();
        }
    }
}

/// `tabg serve`: run the server for the data dir, controlled over stdio.
pub fn serve_child(paths: &Paths) -> Result<(), String> {
    let loaded = load(paths)?;
    let config = server_config(&loaded)?;
    let (tx, rx) = std::sync::mpsc::channel();
    std::thread::spawn(move || {
        for line in std::io::stdin().lock().lines() {
            let Ok(line) = line else { break };
            match serde_json::from_str::<ToServer>(&line) {
                Ok(cmd) => {
                    if tx.send(cmd).is_err() {
                        break;
                    }
                }
                Err(e) => tracing::warn!("bad command {line:?}: {e}"),
            }
        }
        // stdin closed: the host is gone, so stop (the receiver sees Disconnected).
    });
    let mut control = StdioControl {
        commands: rx,
        paths: paths.clone(),
    };
    tabg_server::run_controlled(config, Arc::new(AtomicBool::new(false)), &mut control)
        .map_err(|e| e.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn commands_are_json_lines() {
        let c = ToServer::Console {
            line: "kick bob".into(),
        };
        let s = serde_json::to_string(&c).unwrap();
        assert_eq!(s, r#"{"cmd":"console","line":"kick bob"}"#);
        assert_eq!(serde_json::from_str::<ToServer>(&s).unwrap(), c);
        assert_eq!(
            serde_json::from_str::<ToServer>(r#"{"cmd":"reload"}"#).unwrap(),
            ToServer::Reload
        );
    }
}
