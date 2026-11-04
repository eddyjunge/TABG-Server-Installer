use std::collections::BTreeMap;

use tabg_protocol::match_flow::{
    GameStateChanged, PlayerAirplaneDropped, RequestAirplaneDrop, RingUpdate, encode_all_drop,
    encode_plane_update,
};
use tabg_protocol::messages::{
    ClientPlayerUpdate, EntityUpdate, NO_WEAPON, PackedDirection, PlayerJoined, PlayerUpdateBatch,
    RoomInitRequest, RoomInitResponse, WorldPhase, WorldPlayer, WorldState, game_state,
    server_response,
};
use tabg_protocol::{EventCode, Packet};

use crate::plane::{self, Plane};
use crate::ring::{Ring, RingEvent};
use crate::rng::Rng;
use crate::settings::{RoomSettings, SpawnPoint};

/// After a match ends, players are disconnected this long later and the room resets (the official
/// server terminates 15 s after the end).
pub const RESTART_AFTER_END: f32 = 15.0;

/// Who a packet goes to. Player ids are ENet peer ids (TABG's player index).
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Recipients {
    One(u8),
    All,
    AllExcept(u8),
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Outgoing {
    pub to: Recipients,
    pub packet: Packet,
    pub reliable: bool,
}

impl Outgoing {
    fn reliable(to: Recipients, event: EventCode, payload: Vec<u8>) -> Self {
        Self {
            to,
            packet: Packet::new(event, payload),
            reliable: true,
        }
    }
}

#[derive(Clone, Debug)]
struct Player {
    index: u8,
    group: u8,
    name: String,
    gear: Vec<i32>,
    colour: i32,
    squad_id: u64,
    /// Received the world state and is in the game.
    in_world: bool,
    position: [f32; 3],
    rotation: [f32; 2],
    ads: bool,
    direction: PackedDirection,
    movement: u8,
    /// Moved since the last tick.
    dirty: bool,
    /// Left the plane (or never had to: not in a match yet).
    dropped: bool,
    landed: bool,
    alive: bool,
}

/// One TABG match.
#[derive(Debug)]
pub struct Room {
    settings: RoomSettings,
    /// Connected peers that have not been accepted yet.
    pending: Vec<u8>,
    players: BTreeMap<u8, Player>,
    state: u8,
    next_spawn: usize,
    last_tick: f32,
    rng: Rng,
    /// Time of the previous `tick`, to advance timers.
    last_now: Option<f32>,
    countdown_left: f32,
    plane: Option<Plane>,
    ring: Ring,
    ended_at: Option<f32>,
    /// Peers the transport should disconnect (taken by [`Room::take_disconnects`]).
    to_disconnect: Vec<u8>,
}

impl Room {
    pub fn new(settings: RoomSettings) -> Self {
        let mut rng = Rng::new(settings.seed);
        let ring = Ring::new(settings.ring.clone(), &mut rng);
        Self {
            settings,
            pending: Vec::new(),
            players: BTreeMap::new(),
            state: game_state::WAITING_FOR_PLAYERS,
            next_spawn: 0,
            last_tick: 0.0,
            rng,
            last_now: None,
            countdown_left: 0.0,
            plane: None,
            ring,
            ended_at: None,
            to_disconnect: Vec::new(),
        }
    }

    /// Current `GameState` byte.
    pub fn state(&self) -> u8 {
        self.state
    }

    /// Peers to disconnect now (e.g. after a match ended). The room already forgot them.
    pub fn take_disconnects(&mut self) -> Vec<u8> {
        std::mem::take(&mut self.to_disconnect)
    }

    pub fn settings(&self) -> &RoomSettings {
        &self.settings
    }

    pub fn player_count(&self) -> usize {
        self.players.len()
    }

    /// Accepted players. [`Recipients::All`] means exactly these (pending peers get nothing).
    pub fn player_ids(&self) -> Vec<u8> {
        self.players.keys().copied().collect()
    }

    pub fn player_names(&self) -> impl Iterator<Item = (u8, &str)> {
        self.players.values().map(|p| (p.index, p.name.as_str()))
    }

    /// A transport-level connection was made. The player is added once it sends `RoomInit`.
    pub fn connect(&mut self, peer: u8) {
        if !self.pending.contains(&peer) {
            self.pending.push(peer);
        }
    }

    /// A peer disconnected or timed out.
    pub fn disconnect(&mut self, peer: u8) -> Vec<Outgoing> {
        self.pending.retain(|&p| p != peer);
        if self.players.remove(&peer).is_none() {
            return Vec::new();
        }
        // PlayerLeft: [player index, 1 if they left the game for good].
        vec![Outgoing::reliable(
            Recipients::All,
            EventCode::PlayerLeft,
            vec![peer, 1],
        )]
    }

    /// Handle one packet from `peer`. Unknown or malformed packets are ignored.
    pub fn handle(&mut self, peer: u8, packet: &Packet) -> Vec<Outgoing> {
        match packet.event() {
            Some(EventCode::RoomInit) => match RoomInitRequest::decode(&packet.payload) {
                Ok(req) => self.room_init(peer, req),
                Err(_) => Vec::new(),
            },
            Some(EventCode::RequestWorldState) => self.world_state(peer),
            Some(EventCode::RequestAirplaneDrop) => {
                match RequestAirplaneDrop::decode(&packet.payload) {
                    Ok(req) => self.airplane_drop(peer, req),
                    Err(_) => Vec::new(),
                }
            }
            // Landing after skydiving: remembered and relayed unchanged, like the official server.
            Some(EventCode::PlayerLand) => match self.players.get_mut(&peer) {
                Some(p) if p.dropped && !p.landed => {
                    p.landed = true;
                    vec![Outgoing::reliable(
                        Recipients::All,
                        EventCode::PlayerLand,
                        packet.payload.clone(),
                    )]
                }
                _ => Vec::new(),
            },
            Some(EventCode::PlayerUpdate) => {
                if let Ok(up) = ClientPlayerUpdate::decode(&packet.payload) {
                    self.player_update(peer, up);
                }
                Vec::new()
            }
            // TABGPing is echoed back unchanged as PingRecieved.
            Some(EventCode::TABGPing) => {
                vec![Outgoing::reliable(
                    Recipients::One(peer),
                    EventCode::PingRecieved,
                    packet.payload.clone(),
                )]
            }
            _ => Vec::new(),
        }
    }

    fn room_init(&mut self, peer: u8, req: RoomInitRequest) -> Vec<Outgoing> {
        if self.players.contains_key(&peer) {
            return Vec::new();
        }
        let refuse = |response: u8, room: &Self| {
            let msg = RoomInitResponse::Refused {
                response,
                players_in_room: u8::try_from(room.players.len()).unwrap_or(u8::MAX),
                squad_size: req.squad_size,
                reserved_slots: 0,
            };
            vec![Outgoing::reliable(
                Recipients::One(peer),
                EventCode::RoomInitRequestResponse,
                msg.encode(),
            )]
        };
        if !matches!(
            self.state,
            game_state::WAITING_FOR_PLAYERS | game_state::COUNT_DOWN
        ) {
            return refuse(
                server_response::ACCEPTED | server_response::MATCH_ALREADY_STARTED,
                self,
            );
        }
        if self.players.len() >= usize::from(self.settings.max_players) {
            return refuse(
                server_response::ACCEPTED | server_response::SQUAD_DONT_FIT,
                self,
            );
        }
        if !self.settings.password.is_empty()
            && req.password.as_deref() != Some(self.settings.password.as_str())
        {
            return refuse(
                server_response::ACCEPTED | server_response::WRONG_PASSWORD,
                self,
            );
        }

        let name = clean_name(&req.player_name);
        let group = self.group_for(peer, req.squad_id);
        self.pending.retain(|&p| p != peer);
        self.players.insert(
            peer,
            Player {
                index: peer,
                group,
                name: name.clone(),
                gear: req.gear.clone(),
                colour: req.colour,
                squad_id: req.squad_id,
                in_world: false,
                position: [0.0; 3],
                rotation: [0.0; 2],
                ads: false,
                direction: PackedDirection::ZERO,
                movement: 0,
                dirty: false,
                dropped: true,
                landed: true,
                alive: true,
            },
        );

        // Same order as the official server: announce the player to everyone, then accept them.
        let joined = PlayerJoined {
            player_index: peer,
            group_index: group,
            player_name: name.clone(),
            gear: req.gear,
            is_dev: false,
            colour: req.colour,
        };
        let accepted = RoomInitResponse::Accepted {
            game_mode: self.settings.game_mode,
            match_mode: self.settings.match_mode,
            player_index: peer,
            group_index: group,
            player_name: name,
        };
        vec![
            Outgoing::reliable(Recipients::All, EventCode::Login, joined.encode()),
            Outgoing::reliable(
                Recipients::One(peer),
                EventCode::RoomInitRequestResponse,
                accepted.encode(),
            ),
        ]
    }

    /// Squad members (same non-zero squad id) share a group; everyone else gets their own.
    fn group_for(&self, peer: u8, squad_id: u64) -> u8 {
        if squad_id != 0 {
            if let Some(mate) = self.players.values().find(|p| p.squad_id == squad_id) {
                return mate.group;
            }
        }
        peer
    }

    fn world_state(&mut self, peer: u8) -> Vec<Outgoing> {
        let spawn = self.next_spawn_point();
        let Some(player) = self.players.get_mut(&peer) else {
            return Vec::new();
        };
        if player.in_world {
            return Vec::new();
        }
        player.in_world = true;
        player.position = spawn.position;
        player.rotation = [0.0, spawn.rotation];
        let (index, group, name) = (player.index, player.group, player.name.clone());

        let players = self
            .players
            .values()
            .map(|p| WorldPlayer {
                player_index: p.index,
                group_index: p.group,
                player_name: p.name.clone(),
                weapon_type: NO_WEAPON,
                gear: p.gear.clone(),
                is_admin: false,
                colour: p.colour,
            })
            .collect();
        let s = &self.settings;
        let world = WorldState {
            player_index: index,
            group_index: group,
            player_name: name,
            is_admin: false,
            position: spawn.position,
            rotation: spawn.rotation,
            is_dead: false,
            is_downed: false,
            health: 100.0,
            car_seat: None,
            deception_colour: None,
            players,
            weapons: Vec::new(),
            cars: Vec::new(),
            time_of_day: s.time_of_day,
            time_before_first_ring: s.time_before_first_ring,
            base_ring_time: s.ring.base_time,
            rings: s
                .ring
                .sizes
                .iter()
                .copied()
                .zip(s.ring.speeds.iter().copied())
                .collect(),
            lives_per_team: s.lives_per_team,
            kills_to_win: s.kills_to_win,
            // Late joiners are refused once the match started, so only these two matter.
            phase: if self.state == game_state::COUNT_DOWN {
                WorldPhase::CountDown {
                    tick: self.countdown_left,
                }
            } else {
                WorldPhase::Plain(self.state)
            },
        };
        vec![Outgoing::reliable(
            Recipients::One(peer),
            EventCode::Login,
            world.encode(s.game_mode),
        )]
    }

    fn next_spawn_point(&mut self) -> SpawnPoint {
        let points = &self.settings.spawn_points;
        if points.is_empty() {
            return SpawnPoint {
                position: [0.0, 100.0, 0.0],
                rotation: 0.0,
            };
        }
        let p = points[self.next_spawn % points.len()];
        self.next_spawn = self.next_spawn.wrapping_add(1);
        p
    }

    fn player_update(&mut self, peer: u8, up: ClientPlayerUpdate) {
        // Clients name themselves in the packet; only trust the transport's peer id.
        let Some(p) = self.players.get_mut(&peer) else {
            return;
        };
        if !p.in_world
            || up.player_index != peer
            || !up
                .position
                .iter()
                .chain(&up.rotation)
                .all(|v| v.is_finite())
        {
            return;
        }
        p.position = up.position;
        p.rotation = up.rotation;
        p.ads = up.aiming_down_sights;
        p.direction = up.direction;
        p.movement = up.movement;
        p.dirty = true;
    }

    fn airplane_drop(&mut self, peer: u8, req: RequestAirplaneDrop) -> Vec<Outgoing> {
        let now = self.last_now.unwrap_or(0.0);
        let Some(plane) = self.plane.as_ref().filter(|p| !p.finished) else {
            return Vec::new();
        };
        let position = plane.drop_position(now);
        let Some(p) = self.players.get_mut(&peer) else {
            return Vec::new();
        };
        if req.player_index != peer || p.dropped || !req.direction.iter().all(|v| v.is_finite()) {
            return Vec::new();
        }
        p.dropped = true;
        p.position = position;
        let msg = PlayerAirplaneDropped {
            player_index: peer,
            position,
            direction: req.direction,
        };
        vec![Outgoing::reliable(
            Recipients::All,
            EventCode::PlayerAirplaneDropped,
            msg.encode(),
        )]
    }

    fn broadcast(event: EventCode, payload: Vec<u8>) -> Outgoing {
        Outgoing::reliable(Recipients::All, event, payload)
    }

    fn state_changed(msg: GameStateChanged) -> Outgoing {
        Self::broadcast(EventCode::GameStateChanged, msg.encode())
    }

    fn ring_events(events: Vec<RingEvent>) -> impl Iterator<Item = Outgoing> {
        events.into_iter().map(|e| {
            let msg = match e {
                RingEvent::NextRing {
                    ring_id,
                    center,
                    size,
                } => RingUpdate::NextRing {
                    ring_id,
                    center,
                    size,
                },
                RingEvent::StartedMoving => RingUpdate::StartedMoving,
            };
            Self::broadcast(EventCode::RingUpdate, msg.encode())
        })
    }

    /// Groups that still have a living player.
    fn alive_teams(&self) -> Vec<u8> {
        let mut teams: Vec<u8> = self
            .players
            .values()
            .filter(|p| p.alive)
            .map(|p| p.group)
            .collect();
        teams.sort_unstable();
        teams.dedup();
        teams
    }

    /// Advance the match: countdown, plane, ring, end of match, restart.
    fn tick_match(&mut self, now: f32, dt: f32) -> Vec<Outgoing> {
        let mut out = Vec::new();
        match self.state {
            game_state::WAITING_FOR_PLAYERS => {
                let players = self.players.len();
                let enough = players >= usize::from(self.settings.players_to_start.max(1));
                let forced = self.settings.force_start.is_some_and(|f| {
                    now + 2.0 > f.after_seconds && players >= usize::from(f.min_players.max(1))
                });
                if enough || forced {
                    self.state = game_state::COUNT_DOWN;
                    self.countdown_left = self.settings.countdown;
                    out.push(Self::state_changed(GameStateChanged::CountDown {
                        seconds: self.countdown_left,
                    }));
                }
            }
            game_state::COUNT_DOWN => {
                self.countdown_left -= dt;
                if self.countdown_left <= 0.0 {
                    out.extend(self.start_match(now));
                }
            }
            game_state::STARTED => {
                if let Some(plane) = self.plane.as_mut().filter(|p| !p.finished) {
                    if now - plane.last_sync >= plane::SYNC_INTERVAL {
                        plane.last_sync = now;
                        out.push(Self::broadcast(
                            EventCode::PlaneUpdate,
                            encode_plane_update(plane.progress(now)),
                        ));
                    }
                    if now - plane.took_off_at >= plane::FLIGHT_TIME {
                        plane.finished = true;
                        let position = plane.drop_position(now);
                        let still_aboard: Vec<u8> = self
                            .players
                            .values()
                            .filter(|p| !p.dropped)
                            .map(|p| p.index)
                            .collect();
                        for &i in &still_aboard {
                            let p = self.players.get_mut(&i).expect("listed above");
                            p.dropped = true;
                            p.position = position;
                        }
                        out.extend(still_aboard.into_iter().map(|i| {
                            Outgoing::reliable(
                                Recipients::One(i),
                                EventCode::AllDrop,
                                encode_all_drop(position),
                            )
                        }));
                    }
                }
                let ring_events = self.ring.tick(now, &mut self.rng);
                out.extend(Self::ring_events(ring_events));
                let teams = self.alive_teams();
                if teams.len() <= 1 {
                    let winner = teams
                        .first()
                        .copied()
                        .unwrap_or(GameStateChanged::NO_WINNER);
                    self.state = game_state::ENDED;
                    self.ended_at = Some(now);
                    out.push(Self::state_changed(GameStateChanged::Ended { winner }));
                }
            }
            game_state::ENDED => {
                if self.ended_at.is_some_and(|t| now - t >= RESTART_AFTER_END) {
                    self.to_disconnect.extend(self.players.keys().copied());
                    self.players.clear();
                }
                if self.players.is_empty() {
                    self.reset();
                }
            }
            _ => {}
        }
        out
    }

    /// Countdown over: everyone boards the plane and the ring starts.
    fn start_match(&mut self, now: f32) -> Vec<Outgoing> {
        for p in self.players.values_mut() {
            p.dropped = false;
            p.landed = false;
            p.alive = true;
        }
        let plane = Plane::take_off(self.ring.white_center, now, &mut self.rng);
        let flying = GameStateChanged::Flying {
            start: plane.start,
            end: plane.end,
            time_of_day: self.settings.time_of_day,
            modifier: 0,
        };
        self.plane = Some(plane);
        // The official server is internally `Started` while players are still in the plane.
        self.state = game_state::STARTED;
        let ring_events = self.ring.start(now);
        let mut out = vec![Self::state_changed(flying)];
        out.extend(Self::ring_events(ring_events));
        out
    }

    /// Back to the lobby for the next match.
    fn reset(&mut self) {
        self.state = game_state::WAITING_FOR_PLAYERS;
        self.plane = None;
        self.ended_at = None;
        self.countdown_left = 0.0;
        self.ring = Ring::new(self.settings.ring.clone(), &mut self.rng);
    }

    /// Advance to `now` (seconds since the room started): run the match, and at the tick rate send
    /// every in-world player a `PlayerUpdate` batch with everyone else's movement.
    pub fn tick(&mut self, now: f32) -> Vec<Outgoing> {
        let dt = self.last_now.map_or(0.0, |t| (now - t).max(0.0));
        self.last_now = Some(now);
        let mut out = self.tick_match(now, dt);
        out.extend(self.tick_movement(now));
        out
    }

    fn tick_movement(&mut self, now: f32) -> Vec<Outgoing> {
        let interval = 1.0 / self.settings.tick_rate.max(1) as f32;
        if now - self.last_tick < interval {
            return Vec::new();
        }
        self.last_tick = now;

        // During a match, players still in the plane (or dead) are not replicated.
        let in_match = self.state == game_state::STARTED;
        let movers: Vec<EntityUpdate> = self
            .players
            .values()
            .filter(|p| p.in_world && p.dirty && (!in_match || (p.dropped && p.alive)))
            .map(|p| {
                EntityUpdate::on_foot(
                    p.index,
                    p.position,
                    p.rotation,
                    p.ads,
                    p.direction,
                    p.movement,
                    0,
                )
            })
            .collect();
        if movers.is_empty() {
            return Vec::new();
        }
        for p in self.players.values_mut() {
            p.dirty = false;
        }

        // TODO(M3): only send what is near each receiver, like the official chunk system.
        self.players
            .values()
            .filter(|r| r.in_world)
            .filter_map(|receiver| {
                let entities: Vec<_> = movers
                    .iter()
                    .filter(|e| e.player_index != receiver.index)
                    .cloned()
                    .collect();
                (!entities.is_empty()).then(|| Outgoing {
                    to: Recipients::One(receiver.index),
                    packet: Packet::new(
                        EventCode::PlayerUpdate,
                        PlayerUpdateBatch {
                            timestamp: now,
                            entities,
                            cars: Vec::new(),
                        }
                        .encode(),
                    ),
                    reliable: false,
                })
            })
            .collect()
    }
}

/// TABG strips surrounding `<`/`>` (rich-text tags) and falls back to "Player".
fn clean_name(raw: &str) -> String {
    let trimmed = raw.trim_matches(|c| c == '<' || c == '>').trim();
    if trimmed.is_empty() {
        "Player".into()
    } else {
        trimmed.chars().take(32).collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tabg_protocol::match_flow::{GameStateChanged, PlayerAirplaneDropped, RingUpdate};
    use tabg_protocol::messages::{EntityBody, game_mode};

    /// Settings for lobby-only tests: the match never starts.
    fn lobby() -> RoomSettings {
        RoomSettings {
            players_to_start: 100,
            force_start: None,
            ..Default::default()
        }
    }

    fn events(out: &[Outgoing], event: EventCode) -> Vec<&Outgoing> {
        out.iter()
            .filter(|o| o.packet.event() == Some(event))
            .collect()
    }

    /// Two players in the world of a room that starts with 2 players after a 10 s countdown.
    fn match_room() -> Room {
        let mut room = Room::new(RoomSettings {
            countdown: 10.0,
            ..Default::default()
        });
        for (peer, name) in [(0, "a"), (1, "b")] {
            join(&mut room, peer, name);
            enter(&mut room, peer);
        }
        room
    }

    /// Tick from `from` to `to` in 0.1 s steps and collect everything sent.
    fn run(room: &mut Room, from: f32, to: f32) -> Vec<Outgoing> {
        let mut out = Vec::new();
        let mut t = from;
        while t <= to + 1e-4 {
            out.extend(room.tick(t));
            t += 0.1;
        }
        out
    }

    fn join(room: &mut Room, peer: u8, name: &str) -> Vec<Outgoing> {
        room.connect(peer);
        let req = RoomInitRequest {
            player_name: name.into(),
            squad_size: 1,
            ..Default::default()
        };
        room.handle(peer, &Packet::new(EventCode::RoomInit, req.encode()))
    }

    fn enter(room: &mut Room, peer: u8) -> WorldState {
        let out = room.handle(peer, &Packet::new(EventCode::RequestWorldState, vec![]));
        assert_eq!(out.len(), 1);
        assert_eq!(out[0].to, Recipients::One(peer));
        WorldState::decode(&out[0].packet.payload, game_mode::BATTLE_ROYALE).unwrap()
    }

    #[test]
    fn join_announces_then_accepts_like_the_official_server() {
        let mut room = Room::new(lobby());
        let out = join(&mut room, 3, "<Jonas>");
        assert_eq!(out.len(), 2);
        assert_eq!(
            (out[0].to.clone(), out[0].packet.event()),
            (Recipients::All, Some(EventCode::Login))
        );
        assert_eq!(
            PlayerJoined::decode(&out[0].packet.payload)
                .unwrap()
                .player_name,
            "Jonas"
        );
        match RoomInitResponse::decode(&out[1].packet.payload).unwrap() {
            RoomInitResponse::Accepted { player_index, .. } => assert_eq!(player_index, 3),
            other => panic!("refused: {other:?}"),
        }
    }

    #[test]
    fn wrong_password_and_full_room_are_refused() {
        let mut room = Room::new(RoomSettings {
            password: "pw".into(),
            max_players: 1,
            ..Default::default()
        });
        let out = join(&mut room, 1, "a");
        assert!(
            matches!(RoomInitResponse::decode(&out[0].packet.payload).unwrap(), RoomInitResponse::Refused { response, .. } if response & server_response::WRONG_PASSWORD != 0)
        );

        room.connect(1);
        let ok = RoomInitRequest {
            player_name: "a".into(),
            password: Some("pw".into()),
            unity_id: Some(String::new()),
            ..Default::default()
        };
        assert_eq!(
            room.handle(1, &Packet::new(EventCode::RoomInit, ok.encode()))
                .len(),
            2
        );
        let full = join(&mut room, 2, "b");
        assert!(matches!(
            RoomInitResponse::decode(&full[0].packet.payload).unwrap(),
            RoomInitResponse::Refused { .. }
        ));
    }

    #[test]
    fn world_state_spawns_the_player_and_lists_everyone() {
        let spawn = SpawnPoint {
            position: [1.0, 2.0, 3.0],
            rotation: 45.0,
        };
        let mut room = Room::new(RoomSettings {
            spawn_points: vec![spawn],
            ..Default::default()
        });
        join(&mut room, 0, "a");
        join(&mut room, 1, "b");
        let world = enter(&mut room, 1);
        assert_eq!(
            (world.player_index, world.position, world.rotation),
            (1, spawn.position, 45.0)
        );
        assert_eq!(world.players.len(), 2);
        assert_eq!(world.rings.len(), 6);
        assert!(
            room.handle(1, &Packet::new(EventCode::RequestWorldState, vec![]))
                .is_empty(),
            "only once"
        );
    }

    #[test]
    fn movement_reaches_other_players_only() {
        let mut room = Room::new(lobby());
        for (peer, name) in [(0, "a"), (1, "b")] {
            join(&mut room, peer, name);
            enter(&mut room, peer);
        }
        let up = ClientPlayerUpdate {
            player_index: 0,
            position: [5.0, 6.0, 7.0],
            rotation: [0.0, 90.0],
            aiming_down_sights: false,
            direction: PackedDirection::ZERO,
            movement: 0,
        };
        room.handle(0, &Packet::new(EventCode::PlayerUpdate, up.encode()));

        let out = room.tick(1.0);
        assert_eq!(out.len(), 1);
        assert_eq!(out[0].to, Recipients::One(1));
        assert!(!out[0].reliable);
        let batch = PlayerUpdateBatch::decode(&out[0].packet.payload).unwrap();
        assert!(matches!(
            &batch.entities[..],
            [EntityUpdate {
                player_index: 0,
                body: EntityBody::OnFoot {
                    position: Some([5.0, 6.0, 7.0]),
                    ..
                },
                ..
            }]
        ));

        assert!(room.tick(1.01).is_empty(), "rate limited");
        assert!(room.tick(2.0).is_empty(), "nothing moved");
    }

    #[test]
    fn spoofed_or_broken_updates_are_ignored() {
        let mut room = Room::new(lobby());
        for peer in [0, 1] {
            join(&mut room, peer, "x");
            enter(&mut room, peer);
        }
        let spoof = ClientPlayerUpdate {
            player_index: 1,
            position: [9.0; 3],
            rotation: [0.0; 2],
            aiming_down_sights: false,
            direction: PackedDirection::ZERO,
            movement: 0,
        };
        room.handle(0, &Packet::new(EventCode::PlayerUpdate, spoof.encode()));
        let nan = ClientPlayerUpdate {
            player_index: 0,
            position: [f32::NAN, 0.0, 0.0],
            ..spoof
        };
        room.handle(0, &Packet::new(EventCode::PlayerUpdate, nan.encode()));
        room.handle(0, &Packet::new(EventCode::PlayerUpdate, vec![1, 2, 3]));
        assert!(room.tick(1.0).is_empty());
    }

    #[test]
    fn leaving_is_broadcast_and_ping_is_echoed() {
        let mut room = Room::new(lobby());
        join(&mut room, 4, "a");
        assert_eq!(
            room.disconnect(4),
            vec![Outgoing::reliable(
                Recipients::All,
                EventCode::PlayerLeft,
                vec![4, 1]
            )]
        );
        assert!(room.disconnect(4).is_empty());
        let ping = room.handle(2, &Packet::new(EventCode::TABGPing, vec![9, 9]));
        assert_eq!(
            ping,
            vec![Outgoing::reliable(
                Recipients::One(2),
                EventCode::PingRecieved,
                vec![9, 9]
            )]
        );
    }

    #[test]
    fn squad_members_share_a_group() {
        let mut room = Room::new(lobby());
        for peer in [0, 1] {
            room.connect(peer);
            let req = RoomInitRequest {
                player_name: format!("p{peer}"),
                squad_id: 77,
                ..Default::default()
            };
            room.handle(peer, &Packet::new(EventCode::RoomInit, req.encode()));
        }
        let groups: Vec<_> = (0..2).map(|p| enter(&mut room, p).group_index).collect();
        assert_eq!(groups[0], groups[1]);
    }

    #[test]
    fn countdown_starts_with_enough_players_then_the_plane_takes_off() {
        let mut room = match_room();
        let out = run(&mut room, 0.0, 0.0);
        let cd = events(&out, EventCode::GameStateChanged);
        assert_eq!(
            GameStateChanged::decode(&cd[0].packet.payload).unwrap(),
            GameStateChanged::CountDown { seconds: 10.0 }
        );
        assert_eq!(room.state(), game_state::COUNT_DOWN);

        // Joining during the countdown is still allowed, with the countdown in the world state.
        join(&mut room, 2, "late");
        assert!(matches!(
            enter(&mut room, 2).phase,
            WorldPhase::CountDown { .. }
        ));

        let out = run(&mut room, 0.1, 10.5);
        let changes: Vec<_> = events(&out, EventCode::GameStateChanged)
            .iter()
            .map(|o| GameStateChanged::decode(&o.packet.payload).unwrap())
            .collect();
        let [GameStateChanged::Flying { start, end, .. }] = changes[..] else {
            panic!("{changes:?}")
        };
        assert_eq!((start[1], end[1]), (200.0, 200.0));
        let rings: Vec<_> = events(&out, EventCode::RingUpdate)
            .iter()
            .map(|o| RingUpdate::decode(&o.packet.payload).unwrap())
            .collect();
        assert!(
            matches!(
                rings[..],
                [
                    RingUpdate::NextRing { ring_id: 1, .. },
                    RingUpdate::StartedMoving
                ]
            ),
            "{rings:?}"
        );
        assert_eq!(room.state(), game_state::STARTED);

        // Too late now.
        let refused = join(&mut room, 3, "too late");
        assert!(
            matches!(RoomInitResponse::decode(&refused[0].packet.payload).unwrap(), RoomInitResponse::Refused { response, .. } if response & server_response::MATCH_ALREADY_STARTED != 0)
        );
    }

    #[test]
    fn players_jump_or_are_dropped_when_the_plane_ends() {
        let mut room = match_room();
        run(&mut room, 0.0, 10.5);
        // Player 0 jumps; player 1 stays aboard.
        let req = RequestAirplaneDrop {
            player_index: 0,
            direction: [0.0, -1.0, 0.0],
        };
        let out = room.handle(
            0,
            &Packet::new(EventCode::RequestAirplaneDrop, req.encode()),
        );
        let dropped = PlayerAirplaneDropped::decode(&out[0].packet.payload).unwrap();
        assert_eq!(
            (out[0].to.clone(), dropped.player_index, dropped.position[1]),
            (Recipients::All, 0, 205.0)
        );
        assert!(
            room.handle(
                0,
                &Packet::new(EventCode::RequestAirplaneDrop, req.encode())
            )
            .is_empty(),
            "only once"
        );
        // A spoofed request for someone else is ignored.
        let spoof = RequestAirplaneDrop {
            player_index: 1,
            ..req
        };
        assert!(
            room.handle(
                0,
                &Packet::new(EventCode::RequestAirplaneDrop, spoof.encode())
            )
            .is_empty()
        );

        let out = run(&mut room, 10.6, 10.5 + plane::FLIGHT_TIME + 0.5);
        assert!(
            !events(&out, EventCode::PlaneUpdate).is_empty(),
            "plane progress is synced"
        );
        let all_drop = events(&out, EventCode::AllDrop);
        assert_eq!(all_drop.len(), 1);
        assert_eq!(all_drop[0].to, Recipients::One(1));
    }

    #[test]
    fn players_in_the_plane_are_not_replicated() {
        let mut room = match_room();
        run(&mut room, 0.0, 10.5);
        let up = ClientPlayerUpdate {
            player_index: 0,
            position: [1.0; 3],
            rotation: [0.0; 2],
            aiming_down_sights: false,
            direction: PackedDirection::ZERO,
            movement: 0,
        };
        room.handle(0, &Packet::new(EventCode::PlayerUpdate, up.encode()));
        assert!(events(&room.tick(11.0), EventCode::PlayerUpdate).is_empty());
        let req = RequestAirplaneDrop {
            player_index: 0,
            direction: [0.0, -1.0, 0.0],
        };
        room.handle(
            0,
            &Packet::new(EventCode::RequestAirplaneDrop, req.encode()),
        );
        room.handle(0, &Packet::new(EventCode::PlayerUpdate, up.encode()));
        assert_eq!(events(&room.tick(11.2), EventCode::PlayerUpdate).len(), 1);
    }

    #[test]
    fn last_team_standing_wins_and_the_room_restarts() {
        let mut room = match_room();
        run(&mut room, 0.0, 10.5);
        let out = room.disconnect(1);
        assert_eq!(events(&out, EventCode::PlayerLeft).len(), 1);
        let out = run(&mut room, 10.6, 10.8);
        let ended: Vec<_> = events(&out, EventCode::GameStateChanged)
            .iter()
            .map(|o| GameStateChanged::decode(&o.packet.payload).unwrap())
            .collect();
        assert_eq!(ended, [GameStateChanged::Ended { winner: 0 }]);
        assert_eq!(room.state(), game_state::ENDED);

        run(&mut room, 10.9, 10.8 + RESTART_AFTER_END + 0.2);
        assert_eq!(room.take_disconnects(), [0]);
        assert_eq!(room.state(), game_state::WAITING_FOR_PLAYERS);
        assert_eq!(room.player_count(), 0);
    }

    #[test]
    fn timed_force_start_needs_its_minimum() {
        let settings = RoomSettings {
            players_to_start: 10,
            force_start: Some(crate::ForceStart {
                after_seconds: 30.0,
                min_players: 2,
            }),
            ..Default::default()
        };
        let mut room = Room::new(settings);
        join(&mut room, 0, "a");
        run(&mut room, 0.0, 40.0);
        assert_eq!(
            room.state(),
            game_state::WAITING_FOR_PLAYERS,
            "one player is not enough"
        );
        join(&mut room, 1, "b");
        run(&mut room, 40.1, 40.2);
        assert_eq!(room.state(), game_state::COUNT_DOWN);
    }
}
