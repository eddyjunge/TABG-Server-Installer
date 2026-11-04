//! Runs one [`tabg_game::Room`] over an ENet socket.
//!
//! The loop mirrors the official server's frame: drain network events into the room, advance the
//! room's clock, send what it produced, flush.

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

use tabg_game::{Outgoing, Recipients, Room, RoomSettings};
use tabg_net::{Host, NetError, NetEvent};
use tabg_protocol::Packet;
use tracing::{debug, info, warn};

#[derive(Clone, Debug)]
pub struct ServerConfig {
    pub bind: String,
    pub port: u16,
    pub settings: RoomSettings,
}

impl Default for ServerConfig {
    fn default() -> Self {
        Self {
            bind: "0.0.0.0".into(),
            port: 7777,
            settings: RoomSettings::default(),
        }
    }
}

/// Run until `stop` is set. Blocks the calling thread.
pub fn run(config: ServerConfig, stop: Arc<AtomicBool>) -> Result<(), NetError> {
    // Like TABG: one ENet peer per player plus one spare. Peer ids must fit TABG's byte player index.
    let max_peers = (usize::from(config.settings.max_players) + 1).min(255);
    let mut host = Host::server(&config.bind, config.port, max_peers)?;
    let mut room = Room::new(config.settings);
    let started = Instant::now();
    info!(bind = %config.bind, port = config.port, "server listening");

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
        host.flush();
    }
    info!("server stopped");
    Ok(())
}

fn send_all(host: &mut Host, room: &Room, out: Vec<Outgoing>) {
    for o in out {
        let wire = o.packet.to_wire();
        let targets: Vec<u8> = match o.to {
            Recipients::One(p) => vec![p],
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
