use std::collections::BTreeMap;

use tabg_protocol::messages::{
    ClientPlayerUpdate, EntityUpdate, NO_WEAPON, PackedDirection, PlayerJoined, PlayerUpdateBatch,
    RoomInitRequest, RoomInitResponse, WorldPhase, WorldPlayer, WorldState, game_state,
    server_response,
};
use tabg_protocol::{EventCode, Packet};

use crate::settings::{RoomSettings, SpawnPoint};

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
}

impl Room {
    pub fn new(settings: RoomSettings) -> Self {
        Self {
            settings,
            pending: Vec::new(),
            players: BTreeMap::new(),
            state: game_state::WAITING_FOR_PLAYERS,
            next_spawn: 0,
            last_tick: 0.0,
        }
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
        let group = self.group_for(peer, req.squad_id, req.ungrouped);
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
    fn group_for(&self, peer: u8, squad_id: u64, ungrouped: bool) -> u8 {
        if squad_id != 0 && !ungrouped {
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
            base_ring_time: s.base_ring_time,
            rings: s
                .ring_sizes
                .iter()
                .copied()
                .zip(s.ring_speeds.iter().copied())
                .collect(),
            lives_per_team: s.lives_per_team,
            kills_to_win: s.kills_to_win,
            phase: WorldPhase::Plain(self.state),
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

    /// Advance to `now` (seconds since the room started). At the tick rate, sends every in-world
    /// player a `PlayerUpdate` batch with everyone else's movement.
    pub fn tick(&mut self, now: f32) -> Vec<Outgoing> {
        let interval = 1.0 / self.settings.tick_rate.max(1) as f32;
        if now - self.last_tick < interval {
            return Vec::new();
        }
        self.last_tick = now;

        let movers: Vec<EntityUpdate> = self
            .players
            .values()
            .filter(|p| p.in_world && p.dirty)
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
    use tabg_protocol::messages::{EntityBody, game_mode};

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
        let mut room = Room::new(RoomSettings::default());
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
        let mut room = Room::new(RoomSettings::default());
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
        let mut room = Room::new(RoomSettings::default());
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
        let mut room = Room::new(RoomSettings::default());
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
        let mut room = Room::new(RoomSettings::default());
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
}
