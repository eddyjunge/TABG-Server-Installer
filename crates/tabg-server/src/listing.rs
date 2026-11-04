//! The community server list in TABG's server browser: the official server posts a heartbeat
//! every 5 s to Landfall's community backend (`CommunityBackendAPI.GameServerHeartbeat`).
//!
//! Posting happens on a background thread so a slow backend never stalls the game loop.

use std::sync::mpsc::{self, Receiver, SyncSender, TrySendError};
use std::time::{Duration, Instant};

use serde::Serialize;
use tracing::{debug, info, warn};

const BACKEND: &str = "https://tabgcommunitybackend.azurewebsites.net/GameServerHeartbeat";
const PUBLIC_IP: &str = "https://ipv4.icanhazip.com";
/// The official server's heartbeat period.
pub const INTERVAL: Duration = Duration::from_secs(5);

/// How the server appears in the browser.
#[derive(Clone, Debug)]
pub struct ListingConfig {
    pub name: String,
    pub description: String,
    /// TABG's `APP_VERSION` (from the world dump); the browser only shows matching servers.
    pub version: String,
}

/// `GameServerHeartbeatRequest`, field for field.
#[derive(Clone, Debug, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct Heartbeat {
    pub server_name: String,
    /// Relay join code, or `ip:port` for direct servers.
    pub join_code: String,
    pub players_on_server: i32,
    pub max_players: i32,
    pub version: String,
    pub description: String,
    pub squad_mode: String,
    pub game_mode: String,
    /// The backend checks it when someone asks for a passworded server's join code.
    pub password: String,
    pub accepting_players: bool,
    pub server_type: String,
}

/// `MatchMode` enum names.
pub fn squad_mode_name(match_mode: u8) -> &'static str {
    match match_mode {
        1 => "SQUAD",
        2 => "DUO",
        _ => "SOLO",
    }
}

/// `GameMode` enum names.
pub fn game_mode_name(game_mode: u8) -> &'static str {
    match game_mode {
        1 => "Brawl",
        2 => "Test",
        3 => "Bomb",
        4 => "Deception",
        _ => "BattleRoyale",
    }
}

/// Where players reach us, as the browser's join code.
#[derive(Clone, Debug)]
pub enum Address {
    JoinCode(String),
    /// Direct: our public IP is looked up once, like the official server does.
    Port(u16),
}

pub struct Lister {
    config: ListingConfig,
    tx: SyncSender<(Heartbeat, Address)>,
    last: Option<Instant>,
}

impl Lister {
    pub fn start(config: ListingConfig) -> Self {
        let (tx, rx) = mpsc::sync_channel(1);
        std::thread::Builder::new()
            .name("server-list".into())
            .spawn(move || post_loop(rx))
            .expect("spawn heartbeat thread");
        info!(name = %config.name, "listing the server in TABG's community server browser");
        Self {
            config,
            tx,
            last: None,
        }
    }

    /// Call every tick; sends a heartbeat when one is due.
    #[allow(clippy::too_many_arguments)]
    pub fn tick(
        &mut self,
        address: &Address,
        players: usize,
        max_players: u8,
        match_mode: u8,
        game_mode: u8,
        password: &str,
        accepting: bool,
    ) {
        if self.last.is_some_and(|t| t.elapsed() < INTERVAL) {
            return;
        }
        self.last = Some(Instant::now());
        let beat = Heartbeat {
            server_name: self.config.name.clone(),
            join_code: String::new(),
            players_on_server: i32::try_from(players).unwrap_or(i32::MAX),
            max_players: i32::from(max_players),
            version: self.config.version.clone(),
            description: self.config.description.clone(),
            squad_mode: squad_mode_name(match_mode).into(),
            game_mode: game_mode_name(game_mode).into(),
            password: password.into(),
            accepting_players: accepting,
            server_type: "dedicated".into(),
        };
        match self.tx.try_send((beat, address.clone())) {
            Ok(()) | Err(TrySendError::Full(_)) => {}
            Err(TrySendError::Disconnected(_)) => warn!("server list thread stopped"),
        }
    }
}

fn agent() -> ureq::Agent {
    ureq::Agent::config_builder()
        .timeout_global(Some(Duration::from_secs(10)))
        .http_status_as_error(false)
        .build()
        .into()
}

fn public_ip(agent: &ureq::Agent) -> Option<String> {
    let mut r = agent.get(PUBLIC_IP).call().ok()?;
    let ip = r.body_mut().read_to_string().ok()?.trim().to_owned();
    ip.parse::<std::net::Ipv4Addr>().ok().map(|_| ip)
}

fn post_loop(rx: Receiver<(Heartbeat, Address)>) {
    let agent = agent();
    let mut ip: Option<String> = None;
    let mut failing = false;
    while let Ok((mut beat, address)) = rx.recv() {
        beat.join_code = match address {
            Address::JoinCode(code) => code,
            Address::Port(port) => {
                if ip.is_none() {
                    ip = public_ip(&agent);
                }
                let Some(ip) = &ip else {
                    warn!("server list: could not find our public IP; not listed");
                    continue;
                };
                format!("{ip}:{port}")
            }
        };
        let result = agent
            .post(BACKEND)
            .header("Content-Type", "application/json")
            .send_json(&beat)
            .map_err(|e| e.to_string())
            .and_then(|mut r| {
                let status = r.status();
                let body = r.body_mut().read_to_string().map_err(|e| e.to_string())?;
                if !status.is_success() {
                    return Err(format!("HTTP {status}: {body}"));
                }
                let v: serde_json::Value =
                    serde_json::from_str(&body).map_err(|e| e.to_string())?;
                if v["success"].as_bool() == Some(true) {
                    Ok(())
                } else {
                    Err(v["errorMessage"].as_str().unwrap_or(&body).to_owned())
                }
            });
        match result {
            Ok(()) => {
                if failing {
                    info!("server list heartbeat works again");
                }
                failing = false;
                debug!(code = %beat.join_code, "server list heartbeat sent");
            }
            Err(e) if !failing => {
                failing = true;
                warn!(error = %e, "server list heartbeat failed");
            }
            Err(_) => {}
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn heartbeat_json_matches_unity_field_names() {
        let beat = Heartbeat {
            server_name: "n".into(),
            join_code: "ABC123".into(),
            players_on_server: 1,
            max_players: 70,
            version: "v".into(),
            description: "d".into(),
            squad_mode: squad_mode_name(0).into(),
            game_mode: game_mode_name(0).into(),
            password: String::new(),
            accepting_players: true,
            server_type: "dedicated".into(),
        };
        assert_eq!(
            serde_json::to_string(&beat).unwrap(),
            r#"{"serverName":"n","joinCode":"ABC123","playersOnServer":1,"maxPlayers":70,"version":"v","description":"d","squadMode":"SOLO","gameMode":"BattleRoyale","password":"","acceptingPlayers":true,"serverType":"dedicated"}"#
        );
    }
}
