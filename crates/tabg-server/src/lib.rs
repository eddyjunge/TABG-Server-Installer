//! Runs one [`tabg_game::Room`] over ENet (direct IP) or Unity Relay (join code).
//!
//! The loop mirrors the official server's frame: drain network events into the room, advance the
//! room's clock, send what it produced, flush.

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

use tabg_game::{Outgoing, Recipients, Room, RoomSettings};
use tabg_net::{Host, NetError, NetEvent, Transport};
use tabg_protocol::Packet;
use tabg_utp::{RelayConfig, RelayHost};
use tracing::{debug, info, warn};

pub mod listing;
pub mod upnp;

use listing::{Address, Lister, ListingConfig};

#[derive(Clone, Debug)]
pub struct ServerConfig {
    pub bind: String,
    pub port: u16,
    /// Host through Unity Relay (players join by code) instead of listening on `bind:port`.
    pub relay: Option<RelayConfig>,
    /// Show the server in TABG's community server browser.
    pub listing: Option<ListingConfig>,
    /// Direct mode: ask the router (UPnP) to forward `port`.
    pub upnp: bool,
    pub settings: RoomSettings,
}

/// What a running server tells the outside world.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Status {
    /// Listening for direct connections.
    Listening { bind: String, port: u16 },
    /// Reachable through Unity Relay with this join code.
    JoinCode(String),
}

impl Default for ServerConfig {
    fn default() -> Self {
        Self {
            bind: "0.0.0.0".into(),
            port: 7777,
            relay: None,
            listing: None,
            upnp: false,
            settings: RoomSettings::default(),
        }
    }
}

/// Run until `stop` is set. Blocks the calling thread.
pub fn run(config: ServerConfig, stop: Arc<AtomicBool>) -> Result<(), NetError> {
    run_with_status(config, stop, |_| {})
}

/// Like [`run`], reporting how players can reach the server (and join code changes).
pub fn run_with_status(
    config: ServerConfig,
    stop: Arc<AtomicBool>,
    mut status: impl FnMut(Status),
) -> Result<(), NetError> {
    if let Some(relay) = config.relay.clone() {
        let host = RelayHost::host(relay).map_err(|e| NetError::Transport(e.to_string()))?;
        let code = host.join_code_handle();
        let mut last = String::new();
        let mut address = move || {
            let current = code.lock().map(|c| c.clone()).unwrap_or_default();
            if current != last {
                info!(join_code = %current, "players can join with this code");
                status(Status::JoinCode(current.clone()));
                last.clone_from(&current);
            }
            Address::JoinCode(current)
        };
        address();
        return serve(host, config, &stop, address);
    }
    // Like TABG: one ENet peer per player plus one spare. Peer ids must fit TABG's byte player index.
    let max_peers = (usize::from(config.settings.max_players) + 1).min(255);
    let host = Host::server(&config.bind, config.port, max_peers)?;
    info!(bind = %config.bind, port = config.port, "server listening");
    status(Status::Listening {
        bind: config.bind.clone(),
        port: config.port,
    });
    let _forwarding = config.upnp.then(|| upnp::forward(config.port)).flatten();
    let port = config.port;
    serve(host, config, &stop, move || Address::Port(port))
}

fn serve(
    mut host: impl Transport,
    config: ServerConfig,
    stop: &AtomicBool,
    mut address: impl FnMut() -> Address,
) -> Result<(), NetError> {
    let mut lister = config.listing.map(Lister::start);
    let mut room = Room::new(config.settings);
    let started = Instant::now();
    while !stop.load(Ordering::Relaxed) {
        // Wait briefly for the first event so an idle server doesn't spin, then drain the rest.
        let mut wait = Duration::from_millis(2);
        while let Some(event) = host.service(wait) {
            wait = Duration::ZERO;
            let Ok(peer) = u8::try_from(event.peer()) else {
                host.disconnect(event.peer());
                continue;
            };
            let out = match event {
                NetEvent::Connected { address, .. } => {
                    info!(peer, %address, "connected");
                    room.connect(peer);
                    Vec::new()
                }
                NetEvent::Disconnected { .. } | NetEvent::TimedOut { .. } => {
                    info!(peer, "disconnected");
                    room.disconnect(peer)
                }
                NetEvent::Received { data, .. } => match Packet::from_wire(&data) {
                    Some(packet) => {
                        debug!(peer, code = packet.code, event = ?packet.event(), len = packet.payload.len(), "recv");
                        room.handle(peer, &packet)
                    }
                    None => Vec::new(),
                },
            };
            send_all(&mut host, &room, out);
        }
        let out = room.tick(started.elapsed().as_secs_f32());
        send_all(&mut host, &room, out);
        for peer in room.take_disconnects() {
            host.disconnect(u32::from(peer));
        }
        host.flush();
        let address = address();
        if let Some(lister) = lister.as_mut() {
            let settings = room.settings();
            // Like `GameRoom.AcceptingPlayers`: while waiting or counting down.
            let accepting = matches!(
                room.state(),
                tabg_protocol::messages::game_state::WAITING_FOR_PLAYERS
                    | tabg_protocol::messages::game_state::COUNT_DOWN
            );
            lister.tick(
                &address,
                room.player_count(),
                settings.max_players,
                settings.match_mode,
                settings.game_mode,
                &settings.password,
                accepting,
            );
        }
    }
    info!("server stopped");
    Ok(())
}

fn send_all(host: &mut impl Transport, room: &Room, out: Vec<Outgoing>) {
    for o in out {
        if !matches!(
            o.packet.event(),
            Some(tabg_protocol::EventCode::PlayerUpdate | tabg_protocol::EventCode::PingRecieved)
        ) {
            debug!(to = ?o.to, event = ?o.packet.event(), len = o.packet.payload.len(), "send");
        }
        let wire = o.packet.to_wire();
        let targets: Vec<u8> = match o.to {
            Recipients::One(p) => vec![p],
            Recipients::Many(ps) => ps,
            Recipients::All => room.player_ids(),
            Recipients::AllExcept(skip) => room
                .player_ids()
                .into_iter()
                .filter(|&p| p != skip)
                .collect(),
        };
        for peer in targets {
            if let Err(e) = host.send(u32::from(peer), &wire, o.reliable) {
                warn!(peer, error = %e, "send failed");
            }
        }
    }
}
