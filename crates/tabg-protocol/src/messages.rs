//! Message payloads for joining, the initial world state and movement.
//!
//! Each type documents its event code and direction. `encode` produces exactly the bytes the
//! official server/client write; `decode` accepts them.

use crate::codec::{DecodeError, Reader, Writer};

/// `GameMode` byte.
pub mod game_mode {
    pub const BATTLE_ROYALE: u8 = 0;
    pub const BRAWL: u8 = 1;
    pub const TEST: u8 = 2;
    pub const BOMB: u8 = 3;
    pub const DECEPTION: u8 = 4;
}

/// `MatchMode` byte.
pub mod match_mode {
    pub const SOLO: u8 = 0;
    pub const SQUAD: u8 = 1;
    pub const DUO: u8 = 2;
}

/// `GameState` byte.
pub mod game_state {
    pub const WAITING_FOR_PLAYERS: u8 = 0;
    pub const COUNT_DOWN: u8 = 1;
    pub const FLYING: u8 = 2;
    pub const STARTED: u8 = 3;
    pub const ENDED: u8 = 4;
    /// Brawl: the spawn house doors open and the round begins.
    pub const OPEN_DOORS: u8 = 5;
}

/// `ServerResponse` flags in a refused [`RoomInitResponse`].
pub mod server_response {
    pub const ERROR: u8 = 0;
    pub const ACCEPTED: u8 = 1;
    pub const SQUAD_DONT_FIT: u8 = 2;
    pub const SQUAD_BIGGER_THAN_TEAM_SIZE: u8 = 4;
    pub const DONT_HAVE_BOOKING: u8 = 8;
    pub const MATCH_ALREADY_STARTED: u8 = 0x10;
    pub const WRONG_PASSWORD: u8 = 0x20;
}

/// `RoomInit` (client → server): the join request a client sends right after connecting.
#[derive(Clone, Debug, PartialEq, Eq, Default)]
pub struct RoomInitRequest {
    pub player_name: String,
    pub death_phrase: String,
    /// Squad/lobby identifier used for squad bookings.
    pub squad_id: u64,
    pub is_squad_host: bool,
    pub squad_size: u8,
    /// Cosmetic gear item ids.
    pub gear: Vec<i32>,
    pub steam_ticket: String,
    pub is_rejoin: bool,
    pub playfab_id: String,
    /// Epic Online Services id token (JWT) as a NUL-terminated byte string: the official server
    /// throws unless the last byte is 0. It verifies the token with Epic; ours does not.
    pub epic_id_token: Vec<u8>,
    /// Epic product user id, NUL-terminated like `epic_id_token`.
    pub epic_product_user_id: Vec<u8>,
    pub colour: i32,
    /// The client's "fill up squad" matchmaking option (join random players into my squad).
    pub fill_up_squad: bool,
    /// Unity Authentication player id; the current client always sends it (possibly empty).
    pub unity_id: Option<String>,
    /// Only sent when the player typed one.
    pub password: Option<String>,
}

impl RoomInitRequest {
    pub fn encode(&self) -> Vec<u8> {
        let mut w = Writer::new();
        w.string(&self.player_name)
            .string(&self.death_phrase)
            .u64(self.squad_id)
            .bool(self.is_squad_host)
            .u8(self.squad_size)
            .i32_array(&self.gear)
            .string(&self.steam_ticket)
            .bool(self.is_rejoin)
            .string(&self.playfab_id)
            .i32_prefixed_bytes(&self.epic_id_token)
            .i32_prefixed_bytes(&self.epic_product_user_id)
            .i32(self.colour)
            .bool(self.fill_up_squad);
        // The password can only be sent after a unity id, as on the official client.
        if self.unity_id.is_some() || self.password.is_some() {
            w.string(self.unity_id.as_deref().unwrap_or(""));
        }
        if let Some(p) = &self.password {
            w.string(p);
        }
        w.into_inner()
    }

    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        let mut msg = Self {
            player_name: r.string()?,
            death_phrase: r.string()?,
            squad_id: r.u64()?,
            is_squad_host: r.bool()?,
            squad_size: r.u8()?,
            gear: r.i32_array()?,
            steam_ticket: r.string()?,
            is_rejoin: r.bool()?,
            playfab_id: r.string()?,
            epic_id_token: r.i32_prefixed_bytes()?.to_vec(),
            epic_product_user_id: r.i32_prefixed_bytes()?.to_vec(),
            colour: r.i32()?,
            fill_up_squad: r.bool()?,
            unity_id: None,
            password: None,
        };
        if !r.is_empty() {
            msg.unity_id = Some(r.string()?);
        }
        if !r.is_empty() {
            msg.password = Some(r.string()?);
        }
        // The real client sizes this buffer from an estimate and sends the zero tail.
        r.finish_allow_zero_padding()?;
        Ok(msg)
    }
}

/// `RoomInitRequestResponse` (server → joining client).
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum RoomInitResponse {
    Accepted {
        game_mode: u8,
        match_mode: u8,
        player_index: u8,
        group_index: u8,
        player_name: String,
    },
    /// `response` holds [`server_response`] flags.
    Refused {
        response: u8,
        players_in_room: u8,
        squad_size: u8,
        reserved_slots: u8,
    },
}

impl RoomInitResponse {
    pub fn encode(&self) -> Vec<u8> {
        let mut w = Writer::new();
        match self {
            // The official server writes the name as int32 length + bytes here (the client reads it
            // as a 7-bit-prefixed string and discards it). We match the server byte for byte.
            Self::Accepted {
                game_mode,
                match_mode,
                player_index,
                group_index,
                player_name,
            } => {
                w.u8(server_response::ACCEPTED)
                    .u8(*game_mode)
                    .u8(*match_mode)
                    .u8(*player_index)
                    .u8(*group_index)
                    .i32_prefixed_string(player_name);
            }
            Self::Refused {
                response,
                players_in_room,
                squad_size,
                reserved_slots,
            } => {
                w.u8(server_response::ERROR)
                    .u8(*response)
                    .u8(*players_in_room)
                    .u8(*squad_size)
                    .u8(*reserved_slots);
            }
        }
        w.into_inner()
    }

    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        let msg = match r.u8()? {
            server_response::ERROR => Self::Refused {
                response: r.u8()?,
                players_in_room: r.u8()?,
                squad_size: r.u8()?,
                reserved_slots: r.u8()?,
            },
            _ => Self::Accepted {
                game_mode: r.u8()?,
                match_mode: r.u8()?,
                player_index: r.u8()?,
                group_index: r.u8()?,
                player_name: r.i32_prefixed_string()?,
            },
        };
        r.finish()?;
        Ok(msg)
    }
}

/// `Login` broadcast (server → everyone) when a player has been accepted.
///
/// The same event code carries [`WorldState`]; clients tell them apart by player index.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct PlayerJoined {
    pub player_index: u8,
    pub group_index: u8,
    pub player_name: String,
    pub gear: Vec<i32>,
    pub is_dev: bool,
    pub colour: i32,
}

impl PlayerJoined {
    pub fn encode(&self) -> Vec<u8> {
        let mut w = Writer::new();
        w.u8(self.player_index)
            .u8(self.group_index)
            .i32_prefixed_string(&self.player_name)
            .i32_array(&self.gear)
            .bool(self.is_dev)
            .i32(self.colour);
        w.into_inner()
    }

    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        let msg = Self {
            player_index: r.u8()?,
            group_index: r.u8()?,
            player_name: r.i32_prefixed_string()?,
            gear: r.i32_array()?,
            is_dev: r.bool()?,
            colour: r.i32()?,
        };
        r.finish()?;
        Ok(msg)
    }
}

/// `weapon_type` of a player holding nothing (seen from the official server).
pub const NO_WEAPON: i32 = i32::MAX;

#[derive(Clone, Debug, PartialEq)]
pub struct WorldPlayer {
    pub player_index: u8,
    pub group_index: u8,
    pub player_name: String,
    /// Held weapon item id, or [`NO_WEAPON`].
    pub weapon_type: i32,
    pub gear: Vec<i32>,
    pub is_admin: bool,
    pub colour: i32,
}

#[derive(Clone, Debug, PartialEq)]
pub struct WorldWeapon {
    /// Network id of this loot instance (`NetworkGun.Index`).
    pub network_id: i32,
    /// Item database id (`NetworkGun.UniqueIdentifier`, despite the name).
    pub item_id: i32,
    pub quantity: i32,
    pub position: [f32; 3],
}

#[derive(Clone, Debug, PartialEq)]
pub struct CarPart {
    pub index: u8,
    pub health: f32,
    pub name: String,
}

#[derive(Clone, Debug, PartialEq)]
pub struct WorldCar {
    pub car_type: i32,
    pub car_index: i32,
    pub seat_network_indices: Vec<i32>,
    pub parts: Vec<CarPart>,
}

/// Game-state specific tail of [`WorldState`].
#[derive(Clone, Debug, PartialEq)]
pub enum WorldPhase {
    /// `WaitingForPlayers`, `Ended`, or `Started` in the Test mode: nothing extra.
    Plain(u8),
    CountDown {
        tick: f32,
    },
    /// `Flying`, or `Started` outside Test: whether this client should ride the plane, and its path.
    Plane {
        state: u8,
        should_fly: bool,
        start: [f32; 3],
        end: [f32; 3],
    },
}

impl WorldPhase {
    pub fn state(&self) -> u8 {
        match self {
            Self::Plain(s) => *s,
            Self::CountDown { .. } => game_state::COUNT_DOWN,
            Self::Plane { state, .. } => *state,
        }
    }
}

/// `Login` to the joining client only, in answer to `RequestWorldState`: where it spawns and what
/// the world looks like.
#[derive(Clone, Debug, PartialEq)]
pub struct WorldState {
    pub player_index: u8,
    pub group_index: u8,
    pub player_name: String,
    pub is_admin: bool,
    pub position: [f32; 3],
    pub rotation: f32,
    pub is_dead: bool,
    pub is_downed: bool,
    pub health: f32,
    /// `(car index, seat network index)` if the player is inside a car.
    pub car_seat: Option<(i32, i32)>,
    /// Only present in the Deception mode.
    pub deception_colour: Option<i32>,
    pub players: Vec<WorldPlayer>,
    /// Always empty in Battle Royale (loot arrives through map chunks instead).
    pub weapons: Vec<WorldWeapon>,
    pub cars: Vec<WorldCar>,
    pub time_of_day: f32,
    pub time_before_first_ring: f32,
    pub base_ring_time: f32,
    /// `(size, speed)` per ring.
    pub rings: Vec<(f32, f32)>,
    pub lives_per_team: i32,
    pub kills_to_win: u16,
    pub phase: WorldPhase,
}

impl WorldState {
    /// `game_mode` decides whether the Deception colour is written; it is known from the
    /// [`RoomInitResponse`].
    pub fn encode(&self, game_mode: u8) -> Vec<u8> {
        let mut w = Writer::new();
        w.u8(self.player_index)
            .u8(self.group_index)
            .i32_prefixed_string(&self.player_name)
            .bool(self.is_admin)
            .vec3(self.position)
            .f32(self.rotation)
            .bool(self.is_dead)
            .bool(self.is_downed)
            .f32(self.health)
            .bool(self.car_seat.is_some());
        if let Some((car, seat)) = self.car_seat {
            w.i32(car).i32(seat);
        }
        if game_mode == game_mode::DECEPTION {
            w.i32(self.deception_colour.unwrap_or(0));
        }
        w.u8(u8::try_from(self.players.len()).expect("at most 255 players"));
        for p in &self.players {
            w.u8(p.player_index)
                .u8(p.group_index)
                .i32_prefixed_string(&p.player_name)
                .i32(p.weapon_type)
                .i32_array(&p.gear)
                .bool(p.is_admin)
                .i32(p.colour);
        }
        w.i32(i32::try_from(self.weapons.len()).expect("too many weapons"));
        for g in &self.weapons {
            w.i32(g.network_id)
                .i32(g.item_id)
                .i32(g.quantity)
                .vec3(g.position);
        }
        w.i32(i32::try_from(self.cars.len()).expect("too many cars"));
        for c in &self.cars {
            w.i32(c.car_type)
                .i32(c.car_index)
                .i32_array(&c.seat_network_indices);
            w.u8(u8::try_from(c.parts.len()).expect("at most 255 parts"));
            for part in &c.parts {
                w.u8(part.index).f32(part.health).string(&part.name);
            }
        }
        w.f32(self.time_of_day)
            .f32(self.time_before_first_ring)
            .f32(self.base_ring_time)
            .u8(u8::try_from(self.rings.len()).expect("at most 255 rings"));
        for &(size, speed) in &self.rings {
            w.f32(size).f32(speed);
        }
        w.i32(self.lives_per_team)
            .u16(self.kills_to_win)
            .u8(self.phase.state());
        match &self.phase {
            WorldPhase::Plain(_) => {}
            WorldPhase::CountDown { tick } => {
                w.f32(*tick);
            }
            WorldPhase::Plane {
                should_fly,
                start,
                end,
                ..
            } => {
                w.bool(*should_fly).vec3(*start).vec3(*end);
            }
        }
        w.into_inner()
    }

    pub fn decode(payload: &[u8], game_mode: u8) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        let player_index = r.u8()?;
        let group_index = r.u8()?;
        let player_name = r.i32_prefixed_string()?;
        let is_admin = r.bool()?;
        let position = r.vec3()?;
        let rotation = r.f32()?;
        let is_dead = r.bool()?;
        let is_downed = r.bool()?;
        let health = r.f32()?;
        let car_seat = if r.bool()? {
            Some((r.i32()?, r.i32()?))
        } else {
            None
        };
        let deception_colour = if game_mode == game_mode::DECEPTION {
            Some(r.i32()?)
        } else {
            None
        };
        let players = (0..r.u8()?)
            .map(|_| {
                Ok(WorldPlayer {
                    player_index: r.u8()?,
                    group_index: r.u8()?,
                    player_name: r.i32_prefixed_string()?,
                    weapon_type: r.i32()?,
                    gear: r.i32_array()?,
                    is_admin: r.bool()?,
                    colour: r.i32()?,
                })
            })
            .collect::<Result<_, DecodeError>>()?;
        let weapons = (0..r.i32()?.max(0))
            .map(|_| {
                Ok(WorldWeapon {
                    network_id: r.i32()?,
                    item_id: r.i32()?,
                    quantity: r.i32()?,
                    position: r.vec3()?,
                })
            })
            .collect::<Result<_, DecodeError>>()?;
        let cars = (0..r.i32()?.max(0))
            .map(|_| {
                let car_type = r.i32()?;
                let car_index = r.i32()?;
                let seat_network_indices = r.i32_array()?;
                let parts = (0..r.u8()?)
                    .map(|_| {
                        Ok(CarPart {
                            index: r.u8()?,
                            health: r.f32()?,
                            name: r.string()?,
                        })
                    })
                    .collect::<Result<_, DecodeError>>()?;
                Ok(WorldCar {
                    car_type,
                    car_index,
                    seat_network_indices,
                    parts,
                })
            })
            .collect::<Result<_, DecodeError>>()?;
        let time_of_day = r.f32()?;
        let time_before_first_ring = r.f32()?;
        let base_ring_time = r.f32()?;
        let rings = (0..r.u8()?)
            .map(|_| Ok((r.f32()?, r.f32()?)))
            .collect::<Result<_, DecodeError>>()?;
        let lives_per_team = r.i32()?;
        let kills_to_win = r.u16()?;
        let state = r.u8()?;
        let phase = match state {
            game_state::COUNT_DOWN => WorldPhase::CountDown { tick: r.f32()? },
            game_state::FLYING => WorldPhase::Plane {
                state,
                should_fly: r.bool()?,
                start: r.vec3()?,
                end: r.vec3()?,
            },
            game_state::STARTED if game_mode != game_mode::TEST => WorldPhase::Plane {
                state,
                should_fly: r.bool()?,
                start: r.vec3()?,
                end: r.vec3()?,
            },
            other => WorldPhase::Plain(other),
        };
        // The official server sends this buffer at its estimated size, zero-padded.
        r.finish_allow_zero_padding()?;
        Ok(Self {
            player_index,
            group_index,
            player_name,
            is_admin,
            position,
            rotation,
            is_dead,
            is_downed,
            health,
            car_seat,
            deception_colour,
            players,
            weapons,
            cars,
            time_of_day,
            time_before_first_ring,
            base_ring_time,
            rings,
            lives_per_team,
            kills_to_win,
            phase,
        })
    }
}

/// A movement direction packed into 3 bytes: each axis is `axis * 100 + 100`, truncated to a byte.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
pub struct PackedDirection(pub [u8; 3]);

impl PackedDirection {
    pub const ZERO: Self = Self([100, 100, 100]);

    pub fn pack(dir: [f32; 3]) -> Self {
        // C# `(byte)(float)` truncates toward zero and wraps; clamp first so we never wrap.
        let axis = |v: f32| (v * 100.0 + 100.0).clamp(0.0, 255.0) as u8;
        Self([axis(dir[0]), axis(dir[1]), axis(dir[2])])
    }

    pub fn unpack(self) -> [f32; 3] {
        self.0.map(|b| (f32::from(b) - 100.0) / 100.0)
    }
}

/// `PlayerUpdate` (client → server): the client's own movement, sent unreliably every frame.
#[derive(Clone, Debug, PartialEq)]
pub struct ClientPlayerUpdate {
    pub player_index: u8,
    pub position: [f32; 3],
    /// Look rotation (pitch, yaw).
    pub rotation: [f32; 2],
    pub aiming_down_sights: bool,
    pub direction: PackedDirection,
    /// Movement flags; bit 4 = crouching, bit 5 = prone.
    pub movement: u8,
}

impl ClientPlayerUpdate {
    pub fn encode(&self) -> Vec<u8> {
        let mut w = Writer::new();
        w.u8(self.player_index)
            .vec3(self.position)
            .f32(self.rotation[0])
            .f32(self.rotation[1])
            .bool(self.aiming_down_sights)
            .bytes(&self.direction.0)
            .u8(self.movement);
        w.into_inner()
    }

    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        let msg = Self {
            player_index: r.u8()?,
            position: r.vec3()?,
            rotation: [r.f32()?, r.f32()?],
            aiming_down_sights: r.bool()?,
            direction: PackedDirection(r.bytes(3)?.try_into().expect("3 bytes")),
            movement: r.u8()?,
        };
        r.finish()?;
        Ok(msg)
    }
}

/// `PacketContainerFlags`: which fields an [`EntityUpdate`] carries. The server always sends `ALL`.
pub mod container_flags {
    pub const PLAYER_POSITION: u8 = 1;
    pub const PLAYER_ROTATION: u8 = 2;
    pub const PLAYER_DIRECTION: u8 = 4;
    pub const CAR_POSITION: u8 = 8;
    pub const CAR_ROTATION: u8 = 0x10;
    pub const CAR_INPUT: u8 = 0x20;
    pub const ALL: u8 = 0x40;

    pub fn has(flags: u8, bit: u8) -> bool {
        flags == ALL || flags & bit != 0
    }
}

/// `DrivingState` flags.
pub mod driving_state {
    pub const INSIDE_CAR: u8 = 1;
    pub const DRIVING: u8 = 2;
}

/// A compressed quaternion: one selector byte, plus 6 bytes when the selector is below 4.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct PackedQuaternion(pub Vec<u8>);

impl PackedQuaternion {
    /// Pack `[x, y, z, w]` exactly like TABG's `OptimizeQuaternion` (largest component dropped,
    /// the rest as `i16 × 10000`; a single byte for unit axes). Note the official quirk kept here:
    /// when `x` is the largest, its sign is not applied to the others.
    pub fn pack(q: [f32; 4]) -> Self {
        let mut index = 0u8;
        let mut largest = q[0].abs();
        let mut sign = 1.0f32;
        for (i, &v) in q.iter().enumerate().skip(1) {
            if v.abs() > largest {
                index = i as u8;
                largest = v;
                sign = if v < 0.0 { -1.0 } else { 1.0 };
            }
        }
        if (largest - 1.0).abs() <= f32::EPSILON * 8.0 {
            return Self(vec![index + 4]);
        }
        let mut out = vec![index];
        for (i, &v) in q.iter().enumerate() {
            if i != usize::from(index) {
                let packed = (v * sign * 10000.0) as i16;
                out.extend_from_slice(&packed.to_le_bytes());
            }
        }
        Self(out)
    }

    /// Unpack to `[x, y, z, w]` (official `ConstructQuaternion`).
    pub fn unpack(&self) -> [f32; 4] {
        let Some(&index) = self.0.first() else {
            return [0.0, 0.0, 0.0, 1.0];
        };
        if index >= 4 || self.0.len() < 7 {
            let mut q = [0.0; 4];
            q[usize::from(index.saturating_sub(4)).min(3)] = 1.0;
            return q;
        }
        let v = |i: usize| {
            f32::from(i16::from_le_bytes([self.0[1 + 2 * i], self.0[2 + 2 * i]])) / 10000.0
        };
        let (a, b, c) = (v(0), v(1), v(2));
        let big = (1.0 - (a * a + b * b + c * c)).max(0.0).sqrt();
        match index {
            0 => [big, a, b, c],
            1 => [a, big, b, c],
            2 => [a, b, big, c],
            _ => [a, b, c, big],
        }
    }

    pub fn read(r: &mut Reader<'_>) -> Result<Self, DecodeError> {
        let selector = r.u8()?;
        let mut bytes = vec![selector];
        if selector < 4 {
            bytes.extend_from_slice(r.bytes(6)?);
        }
        Ok(Self(bytes))
    }
}

#[derive(Clone, Debug, PartialEq)]
pub enum EntityBody {
    OnFoot {
        position: Option<[f32; 3]>,
        rotation: Option<[f32; 2]>,
        aiming_down_sights: bool,
        direction: Option<PackedDirection>,
        movement: u8,
    },
    Driving {
        car_position: Option<[f32; 3]>,
        car_rotation: Option<PackedQuaternion>,
        car_input: Option<PackedDirection>,
        rotation: Option<[f32; 2]>,
        car_driving_state: u8,
    },
}

/// One player's state inside a [`PlayerUpdateBatch`].
#[derive(Clone, Debug, PartialEq)]
pub struct EntityUpdate {
    pub player_index: u8,
    pub flags: u8,
    pub driving_state: u8,
    pub body: EntityBody,
}

impl EntityUpdate {
    /// What the official server sends for a player on foot.
    pub fn on_foot(
        player_index: u8,
        position: [f32; 3],
        rotation: [f32; 2],
        ads: bool,
        direction: PackedDirection,
        movement: u8,
        driving_state: u8,
    ) -> Self {
        Self {
            player_index,
            flags: container_flags::ALL,
            driving_state,
            body: EntityBody::OnFoot {
                position: Some(position),
                rotation: Some(rotation),
                aiming_down_sights: ads,
                direction: Some(direction),
                movement,
            },
        }
    }

    fn write(&self, w: &mut Writer) {
        use container_flags::*;
        w.u8(self.player_index)
            .u8(self.flags)
            .u8(self.driving_state);
        match &self.body {
            EntityBody::OnFoot {
                position,
                rotation,
                aiming_down_sights,
                direction,
                movement,
            } => {
                if let Some(p) = position {
                    w.vec3(*p);
                }
                if let Some(r) = rotation {
                    w.f32(r[0]).f32(r[1]);
                }
                w.bool(*aiming_down_sights);
                if has(self.flags, PLAYER_DIRECTION) {
                    w.bytes(&direction.unwrap_or(PackedDirection::ZERO).0);
                }
                w.u8(*movement);
            }
            EntityBody::Driving {
                car_position,
                car_rotation,
                car_input,
                rotation,
                car_driving_state,
            } => {
                if let Some(p) = car_position {
                    w.vec3(*p);
                }
                if let Some(q) = car_rotation {
                    w.bytes(&q.0);
                }
                if let Some(i) = car_input {
                    w.bytes(&i.0);
                }
                if let Some(r) = rotation {
                    w.f32(r[0]).f32(r[1]);
                }
                w.u8(*car_driving_state);
            }
        }
    }

    fn read(r: &mut Reader<'_>) -> Result<Self, DecodeError> {
        use container_flags::*;
        let player_index = r.u8()?;
        let flags = r.u8()?;
        let driving_state = r.u8()?;
        let body = if driving_state & driving_state::DRIVING != 0 {
            EntityBody::Driving {
                car_position: if has(flags, CAR_POSITION) {
                    Some(r.vec3()?)
                } else {
                    None
                },
                car_rotation: if has(flags, CAR_ROTATION) {
                    Some(PackedQuaternion::read(r)?)
                } else {
                    None
                },
                car_input: if has(flags, CAR_INPUT) {
                    Some(PackedDirection(r.bytes(3)?.try_into().expect("3")))
                } else {
                    None
                },
                rotation: if has(flags, PLAYER_ROTATION) {
                    Some([r.f32()?, r.f32()?])
                } else {
                    None
                },
                car_driving_state: r.u8()?,
            }
        } else {
            EntityBody::OnFoot {
                position: if has(flags, PLAYER_POSITION) {
                    Some(r.vec3()?)
                } else {
                    None
                },
                rotation: if has(flags, PLAYER_ROTATION) {
                    Some([r.f32()?, r.f32()?])
                } else {
                    None
                },
                aiming_down_sights: r.bool()?,
                direction: if has(flags, PLAYER_DIRECTION) {
                    Some(PackedDirection(r.bytes(3)?.try_into().expect("3")))
                } else {
                    None
                },
                movement: r.u8()?,
            }
        };
        Ok(Self {
            player_index,
            flags,
            driving_state,
            body,
        })
    }
}

/// A car moved by a temporary owner, inside a [`PlayerUpdateBatch`].
#[derive(Clone, Debug, PartialEq)]
pub struct CarUpdate {
    pub car_index: i32,
    pub position: [f32; 3],
    pub rotation: PackedQuaternion,
}

/// `PlayerUpdate` (server → one client): everything that moved near that client since the last
/// tick. Sent unreliably; clients drop batches whose timestamp is older than the last one.
#[derive(Clone, Debug, PartialEq)]
pub struct PlayerUpdateBatch {
    /// Server time in seconds (Unity `Time.unscaledTime` on the official server).
    pub timestamp: f32,
    pub entities: Vec<EntityUpdate>,
    pub cars: Vec<CarUpdate>,
}

impl PlayerUpdateBatch {
    pub fn encode(&self) -> Vec<u8> {
        let mut w = Writer::new();
        w.f32(self.timestamp)
            .u8(u8::try_from(self.entities.len()).expect("at most 255 entries"));
        for e in &self.entities {
            e.write(&mut w);
        }
        w.u8(u8::try_from(self.cars.len()).expect("at most 255 cars"));
        for c in &self.cars {
            w.i32(c.car_index).vec3(c.position).bytes(&c.rotation.0);
        }
        w.into_inner()
    }

    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        let timestamp = r.f32()?;
        let entities = (0..r.u8()?)
            .map(|_| EntityUpdate::read(&mut r))
            .collect::<Result<_, _>>()?;
        let cars = (0..r.u8()?)
            .map(|_| {
                Ok(CarUpdate {
                    car_index: r.i32()?,
                    position: r.vec3()?,
                    rotation: PackedQuaternion::read(&mut r)?,
                })
            })
            .collect::<Result<_, DecodeError>>()?;
        r.finish()?;
        Ok(Self {
            timestamp,
            entities,
            cars,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample_request() -> RoomInitRequest {
        RoomInitRequest {
            player_name: "Jonas".into(),
            death_phrase: "gg".into(),
            squad_id: 42,
            is_squad_host: true,
            squad_size: 1,
            gear: vec![3, 17],
            steam_ticket: "ticket".into(),
            is_rejoin: false,
            playfab_id: "ABC123".into(),
            epic_id_token: b"jwt".to_vec(),
            epic_product_user_id: b"puid".to_vec(),
            colour: 5,
            fill_up_squad: false,
            unity_id: Some("unity".into()),
            password: Some("secret".into()),
        }
    }

    #[test]
    fn room_init_request_round_trips_with_and_without_optional_tail() {
        let full = sample_request();
        assert_eq!(RoomInitRequest::decode(&full.encode()).unwrap(), full);

        let short = RoomInitRequest {
            unity_id: None,
            password: None,
            ..sample_request()
        };
        assert_eq!(RoomInitRequest::decode(&short.encode()).unwrap(), short);
    }

    #[test]
    fn room_init_response_matches_official_layout() {
        let accepted = RoomInitResponse::Accepted {
            game_mode: 0,
            match_mode: 1,
            player_index: 3,
            group_index: 7,
            player_name: "ab".into(),
        };
        assert_eq!(
            accepted.encode(),
            vec![1, 0, 1, 3, 7, 2, 0, 0, 0, b'a', b'b']
        );
        assert_eq!(
            RoomInitResponse::decode(&accepted.encode()).unwrap(),
            accepted
        );

        let refused = RoomInitResponse::Refused {
            response: server_response::WRONG_PASSWORD | 1,
            players_in_room: 4,
            squad_size: 1,
            reserved_slots: 0,
        };
        assert_eq!(refused.encode(), vec![0, 0x21, 4, 1, 0]);
        assert_eq!(
            RoomInitResponse::decode(&refused.encode()).unwrap(),
            refused
        );
    }

    #[test]
    fn player_joined_round_trips() {
        let msg = PlayerJoined {
            player_index: 2,
            group_index: 2,
            player_name: "Bob".into(),
            gear: vec![1, 2, 3],
            is_dev: false,
            colour: 9,
        };
        assert_eq!(PlayerJoined::decode(&msg.encode()).unwrap(), msg);
    }

    fn sample_world(phase: WorldPhase) -> WorldState {
        WorldState {
            player_index: 1,
            group_index: 1,
            player_name: "Jonas".into(),
            is_admin: false,
            position: [10.0, 20.0, 30.0],
            rotation: 90.0,
            is_dead: false,
            is_downed: false,
            health: 100.0,
            car_seat: None,
            deception_colour: None,
            players: vec![WorldPlayer {
                player_index: 1,
                group_index: 1,
                player_name: "Jonas".into(),
                weapon_type: 0,
                gear: vec![],
                is_admin: false,
                colour: 0,
            }],
            weapons: vec![],
            cars: vec![WorldCar {
                car_type: 2,
                car_index: 0,
                seat_network_indices: vec![0, 1],
                parts: vec![CarPart {
                    index: 0,
                    health: 50.0,
                    name: "Wheel".into(),
                }],
            }],
            time_of_day: 12.0,
            time_before_first_ring: 70.0,
            base_ring_time: 200.0,
            rings: vec![(4240.0, 25.0), (3450.0, 3.0)],
            lives_per_team: 256,
            kills_to_win: 20,
            phase,
        }
    }

    #[test]
    fn world_state_round_trips_in_every_phase() {
        for phase in [
            WorldPhase::Plain(game_state::WAITING_FOR_PLAYERS),
            WorldPhase::CountDown { tick: 12.5 },
            WorldPhase::Plane {
                state: game_state::FLYING,
                should_fly: true,
                start: [0.0, 300.0, 0.0],
                end: [100.0, 300.0, 100.0],
            },
        ] {
            let world = sample_world(phase);
            let bytes = world.encode(game_mode::BATTLE_ROYALE);
            assert_eq!(
                WorldState::decode(&bytes, game_mode::BATTLE_ROYALE).unwrap(),
                world
            );
        }
        let deception = WorldState {
            deception_colour: Some(3),
            car_seat: Some((0, 1)),
            ..sample_world(WorldPhase::Plain(0))
        };
        assert_eq!(
            WorldState::decode(
                &deception.encode(game_mode::DECEPTION),
                game_mode::DECEPTION
            )
            .unwrap(),
            deception
        );
    }

    #[test]
    fn player_update_layouts() {
        let up = ClientPlayerUpdate {
            player_index: 4,
            position: [1.0, 2.0, 3.0],
            rotation: [0.5, 180.0],
            aiming_down_sights: true,
            direction: PackedDirection::pack([1.0, 0.0, -1.0]),
            movement: 0x10,
        };
        let bytes = up.encode();
        assert_eq!(bytes.len(), 26);
        assert_eq!(ClientPlayerUpdate::decode(&bytes).unwrap(), up);
        assert_eq!(up.direction, PackedDirection([200, 100, 0]));

        let batch = PlayerUpdateBatch {
            timestamp: 3.25,
            entities: vec![EntityUpdate::on_foot(
                4,
                up.position,
                up.rotation,
                true,
                up.direction,
                0x10,
                0,
            )],
            cars: vec![CarUpdate {
                car_index: 1,
                position: [0.0; 3],
                rotation: PackedQuaternion(vec![1, 2, 3, 4, 5, 6, 7]),
            }],
        };
        let bytes = batch.encode();
        // timestamp + count + (3 header + 12 pos + 8 rot + 1 ads + 3 dir + 1 move) + car count + (4 + 12 + 7)
        assert_eq!(bytes.len(), 4 + 1 + 28 + 1 + 23);
        assert_eq!(PlayerUpdateBatch::decode(&bytes).unwrap(), batch);
    }

    #[test]
    fn packed_direction_is_clamped_not_wrapped() {
        assert_eq!(
            PackedDirection::pack([5.0, -5.0, 0.0]),
            PackedDirection([255, 0, 100])
        );
        assert_eq!(PackedDirection::ZERO.unpack(), [0.0, 0.0, 0.0]);
    }
}
