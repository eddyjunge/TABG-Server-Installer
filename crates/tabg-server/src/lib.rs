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
#[derive(Clone, Debug, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum Status {
    /// Listening for direct connections.
    Listening { bind: String, port: u16 },
    /// Reachable through Unity Relay with this join code.
    JoinCode { code: String },
}

/// Instructions for a running server (from the host's console, dashboard or CLI).
#[derive(Clone, Debug)]
pub enum Command {
    /// A console line such as `kick bob` or `say hi`.
    Console(String),
    /// New settings; see [`Room::apply_settings`] for what takes effect when.
    Settings(Box<RoomSettings>),
    Stop,
}

/// What a running server reports.
#[derive(Clone, Debug, PartialEq, serde::Serialize, serde::Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum Event {
    Status(Status),
    Room(tabg_game::RoomEvent),
    /// The answer to a [`Command::Console`] line.
    Reply {
        line: String,
        reply: String,
    },
    /// Who is playing; sent when it changes and every few seconds.
    Players {
        phase: String,
        players: Vec<tabg_game::PlayerInfo>,
    },
}

/// The host side of a running server: commands in, events out. Both are non-blocking.
pub trait Control {
    fn poll(&mut self) -> Option<Command>;
    fn emit(&mut self, event: Event);
}

/// No host: nothing comes in, events are only logged.
pub struct Standalone;

impl Control for Standalone {
    fn poll(&mut self) -> Option<Command> {
        None
    }
    fn emit(&mut self, event: Event) {
        if let Event::Room(e) = event {
            info!(?e, "room");
        }
    }
}

/// How often [`Event::Players`] is sent even when nothing changed.
const PLAYERS_EVERY: Duration = Duration::from_secs(3);

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
    run_controlled(config, stop, &mut Standalone)
}

/// Like [`run`], driven by a host: see [`Control`].
pub fn run_controlled(
    config: ServerConfig,
    stop: Arc<AtomicBool>,
    control: &mut dyn Control,
) -> Result<(), NetError> {
    if let Some(relay) = config.relay.clone() {
        let host = RelayHost::host(relay).map_err(|e| NetError::Transport(e.to_string()))?;
        let code = host.join_code_handle();
        let mut last = String::new();
        let address = move |control: &mut dyn Control| {
            let current = code.lock().map(|c| c.clone()).unwrap_or_default();
            if current != last {
                info!(join_code = %current, "players can join with this code");
                control.emit(Event::Status(Status::JoinCode {
                    code: current.clone(),
                }));
                last.clone_from(&current);
            }
            Address::JoinCode(current)
        };
        return serve(host, config, &stop, control, address);
    }
    // Like TABG: one ENet peer per player plus one spare. Peer ids must fit TABG's byte player index.
    let max_peers = (usize::from(config.settings.max_players) + 1).min(255);
    let host = Host::server(&config.bind, config.port, max_peers)?;
    info!(bind = %config.bind, port = config.port, "server listening");
    control.emit(Event::Status(Status::Listening {
        bind: config.bind.clone(),
        port: config.port,
    }));
    let _forwarding = config.upnp.then(|| upnp::forward(config.port)).flatten();
    let port = config.port;
    serve(host, config, &stop, control, move |_: &mut dyn Control| {
        Address::Port(port)
    })
}

fn serve(
    mut host: impl Transport,
    config: ServerConfig,
    stop: &AtomicBool,
    control: &mut dyn Control,
    mut address: impl FnMut(&mut dyn Control) -> Address,
) -> Result<(), NetError> {
    let mut lister = config.listing.map(Lister::start);
    let mut room = Room::new(config.settings);
    let started = Instant::now();
    let mut players_sent: Option<Instant> = None;
    address(control);
    'run: while !stop.load(Ordering::Relaxed) {
        while let Some(command) = control.poll() {
            match command {
                Command::Console(line) => {
                    let (out, reply) = room.console(&line);
                    send_all(&mut host, &room, out);
                    control.emit(Event::Reply { line, reply });
                }
                Command::Settings(settings) => {
                    room.apply_settings(*settings);
                    control.emit(Event::Reply {
                        line: "apply".into(),
                        reply: "settings applied".into(),
                    });
                }
                Command::Stop => break 'run,
            }
        }
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
        let mut changed = false;
        for event in room.take_events() {
            changed |= matches!(
                event,
                tabg_game::RoomEvent::Joined { .. }
                    | tabg_game::RoomEvent::Left { .. }
                    | tabg_game::RoomEvent::Killed { .. }
                    | tabg_game::RoomEvent::MatchState { .. }
            );
            control.emit(Event::Room(event));
        }
        if changed || players_sent.is_none_or(|t| t.elapsed() >= PLAYERS_EVERY) {
            players_sent = Some(Instant::now());
            control.emit(Event::Players {
                phase: room.phase().to_owned(),
                players: room.players(),
            });
        }
        let address = address(control);
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
            if room.is_bot(peer) {
                continue;
            }
            if let Err(e) = host.send(u32::from(peer), &wire, o.reliable) {
                warn!(peer, error = %e, "send failed");
            }
        }
    }
}

/// Fill `settings` with the map data from a world dump: lobby spawns, loot, cars, healing items,
/// grenades and ring timing.
pub fn apply_world(settings: &mut RoomSettings, world: &tabg_world::World) -> Result<(), String> {
    use tabg_game::SpawnPoint;
    let lobby: Vec<_> = world
        .spawn_group(tabg_world::LOBBY_GROUP)
        .map(|p| SpawnPoint {
            position: p.position,
            rotation: p.rotation,
        })
        .collect();
    if lobby.is_empty() && world.brawl.is_none() && world.bomb.is_none() {
        return Err("the map data has no lobby spawn points".into());
    }
    settings.spawn_points = lobby;
    settings.loot = tabg_game::loot::LootRules {
        spots: world
            .loot_spots
            .iter()
            .filter(|s| s.has_preset)
            .map(|s| tabg_game::loot::Spot {
                position: s.position,
                force: s.force_spawn,
            })
            .collect(),
        modifiers: world
            .match_modifiers
            .iter()
            .map(|m| tabg_game::loot::Modifier {
                index: m.index,
                rarity: m.rarity,
                drops: m
                    .drops
                    .iter()
                    .map(|d| tabg_game::loot::Drop {
                        weight: d.spawn_rate,
                        items: d.items.iter().map(|&[i, q]| (i, q)).collect(),
                    })
                    .collect(),
            })
            .collect(),
    };
    settings.cars = world
        .cars
        .iter()
        .map(|c| tabg_game::cars::CarSpawn {
            car_type: c.car_type,
            car_index: c.car_index,
            position: c.position,
            rotation: c.rotation,
            seats: c
                .seats
                .iter()
                .map(|s| (s.network_index, s.driver))
                .collect(),
            parts: c
                .parts
                .iter()
                .map(|p| tabg_protocol::messages::CarPart {
                    index: p.index,
                    health: p.health,
                    name: p.name.clone(),
                })
                .collect(),
        })
        .collect();
    settings.heal_amounts = world
        .heal_amounts
        .iter()
        .filter_map(|(k, &v)| k.parse().ok().map(|k| (k, v)))
        .collect();
    settings.grenades = world.grenades.iter().copied().collect();
    if let Some(ring) = &world.ring {
        settings.ring.ring_times.clone_from(&ring.ring_times);
        settings.ring.base_time = ring.base_time_to_travel;
        settings.ring.hot_spots.clone_from(&ring.hot_spots);
    }
    if let Some(souls) = &world.souls {
        if let Some(interval) = souls.spell_drop_interval {
            let pool = |r: &str| souls.blessings.get(r).cloned().unwrap_or_default();
            settings.spell_drops = Some(tabg_game::SpellDrops {
                interval,
                rare: pool("rare"),
                epic: pool("epic"),
                legendary: pool("legendary"),
            });
        }
        settings.soul_item = Some(souls.soul_item);
        settings.curses = souls
            .curses
            .iter()
            .filter_map(|&c| u8::try_from(c).ok())
            .collect();
    }
    if let Some(b) = &world.brawl {
        settings.brawl = brawl_rules(b);
        settings.game_mode = tabg_protocol::messages::game_mode::BRAWL;
    }
    if let Some(b) = &world.bomb {
        let team = |g: i32| -> Vec<SpawnPoint> {
            world
                .spawn_group(g)
                .map(|p| SpawnPoint {
                    position: p.position,
                    rotation: p.rotation,
                })
                .collect()
        };
        // Official defaults (GameSettings): first to 13 rounds of 90 s, 30 s bomb, 5 s defuse.
        settings.bomb = Some(tabg_game::BombRules {
            spawn: b.spawn,
            bomb_item: b.bomb_item,
            start_weapon: b.start_weapon,
            team_spawns: [team(0), team(1)],
            rounds_to_win: 13,
            round_time: 90.0,
            bomb_time: 30.0,
            defuse_time: 5.0,
        });
        settings.game_mode = tabg_protocol::messages::game_mode::BOMB;
    }
    Ok(())
}

fn brawl_rules(b: &tabg_world::BrawlData) -> tabg_game::brawl::BrawlRules {
    use tabg_game::brawl::{BrawlRules, House, Preset, tier};
    let drops = |d: &[tabg_world::LootDrop]| -> Vec<tabg_game::loot::Drop> {
        d.iter()
            .map(|d| tabg_game::loot::Drop {
                weight: d.spawn_rate,
                items: d.items.iter().map(|&[i, q]| (i, q)).collect(),
            })
            .collect()
    };
    let houses = |hs: &[tabg_world::BrawlHouse]| -> Vec<House> {
        hs.iter()
            .map(|h| House {
                spawns: h
                    .spawns
                    .iter()
                    .map(|s| tabg_game::SpawnPoint {
                        position: s.position,
                        rotation: s.rotation,
                    })
                    .collect(),
                loot: h
                    .loot
                    .iter()
                    .filter_map(|l| tier(&l.tier).map(|t| (t, l.position)))
                    .collect(),
            })
            .collect()
    };
    BrawlRules {
        houses: houses(&b.team_houses),
        solo_houses: houses(&b.solo_houses),
        presets: b
            .presets
            .iter()
            .map(|p| Preset {
                tiers: [
                    drops(&p.high),
                    drops(&p.med),
                    drops(&p.low),
                    drops(&p.attach),
                ],
                redo_chance: p.redo_chance,
            })
            .collect(),
    }
}

/// The walkability grid the dumper writes next to a world file (`world.nav` for `world.json`),
/// if it exists. Bots need it.
pub fn load_nav(world_path: &std::path::Path) -> Option<std::sync::Arc<tabg_game::nav::NavGrid>> {
    use std::io::Read;
    let path = world_path.with_extension("nav");
    let file = std::fs::File::open(&path).ok()?;
    let mut bytes = Vec::new();
    flate2::read::GzDecoder::new(file)
        .read_to_end(&mut bytes)
        .ok()?;
    match tabg_game::nav::NavGrid::decode(&bytes) {
        Ok(grid) => Some(std::sync::Arc::new(grid)),
        Err(e) => {
            warn!("{}: {e}", path.display());
            None
        }
    }
}
