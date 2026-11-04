//! Loot and the chunk system: what a player can see (the 3×3 chunks of 128 m around them), loot
//! pickups and drops.

use crate::codec::{DecodeError, Reader, Writer};
use crate::vehicles::ChunkCar;

/// Side length of a chunk in metres.
pub const CHUNK_SIZE: f32 = 128.0;

/// Number of `i16` equipment slots per player (two weapons with attachments, a third weapon, and
/// the active slot).
pub const EQUIPMENT_LEN: usize = 6;

/// Equipment of a player who carries nothing.
pub const NO_EQUIPMENT: [i16; EQUIPMENT_LEN] = [-1, -1, -1, -1, -1, 1];

/// A loot item lying in the world.
#[derive(Clone, Debug, PartialEq)]
pub struct LootItem {
    /// Unique per match; how clients and the server refer to this instance.
    pub network_id: i32,
    /// What it is (item database index).
    pub item_id: i32,
    pub quantity: i32,
    pub position: [f32; 3],
}

impl LootItem {
    fn write(&self, w: &mut Writer) {
        w.i32(self.network_id)
            .i32(self.item_id)
            .i32(self.quantity)
            .vec3(self.position);
    }

    fn read(r: &mut Reader<'_>) -> Result<Self, DecodeError> {
        Ok(Self {
            network_id: r.i32()?,
            item_id: r.i32()?,
            quantity: r.i32()?,
            position: r.vec3()?,
        })
    }
}

/// A player as described in `ChunkEntry` / `PlayerEnteredChunk`.
#[derive(Clone, Debug, PartialEq)]
pub struct ChunkPlayer {
    pub player_index: u8,
    pub state: u8,
    pub weapon_type: i32,
    pub health: f32,
    pub downed: bool,
    pub skydiving: bool,
    pub gear: Vec<i32>,
    pub equipment: [i16; EQUIPMENT_LEN],
    pub attachments: Vec<i16>,
}

impl ChunkPlayer {
    fn write(&self, w: &mut Writer) {
        w.u8(self.player_index)
            .u8(self.state)
            .i32(self.weapon_type)
            .f32(self.health)
            .bool(self.downed)
            .bool(self.skydiving)
            .i32_array(&self.gear);
        w.u8(EQUIPMENT_LEN as u8);
        for &e in &self.equipment {
            w.bytes(&e.to_le_bytes());
        }
        w.u8(self.attachments.len() as u8);
        for &a in &self.attachments {
            w.bytes(&a.to_le_bytes());
        }
    }

    fn read(r: &mut Reader<'_>) -> Result<Self, DecodeError> {
        let (player_index, state, weapon_type, health, downed, skydiving) =
            (r.u8()?, r.u8()?, r.i32()?, r.f32()?, r.bool()?, r.bool()?);
        let gear = r.i32_array()?;
        let mut equipment = NO_EQUIPMENT;
        let values = (0..r.u8()?)
            .map(|_| Ok(i16::from_le_bytes(r.bytes(2)?.try_into().expect("2"))))
            .collect::<Result<Vec<_>, DecodeError>>()?;
        for (slot, v) in equipment.iter_mut().zip(values) {
            *slot = v;
        }
        let attachments = (0..r.u8()?)
            .map(|_| Ok(i16::from_le_bytes(r.bytes(2)?.try_into().expect("2"))))
            .collect::<Result<_, DecodeError>>()?;
        Ok(Self {
            player_index,
            state,
            weapon_type,
            health,
            downed,
            skydiving,
            gear,
            equipment,
            attachments,
        })
    }
}

/// `ChunkEntry` (server → one player): players, loot and cars in chunks that just came into view.
#[derive(Clone, Debug, PartialEq)]
pub struct ChunkEntry {
    pub player_index: u8,
    pub players: Vec<ChunkPlayer>,
    pub loot: Vec<LootItem>,
    /// Not decoded (their size depends on each car's seat count); encoded when present.
    pub cars: Vec<ChunkCar>,
}

impl ChunkEntry {
    pub fn encode(&self) -> Vec<u8> {
        let mut w = Writer::new();
        w.u8(self.player_index)
            .u8(self.players.len() as u8)
            .u16(self.loot.len() as u16)
            .u16(self.cars.len() as u16);
        for p in &self.players {
            p.write(&mut w);
        }
        for l in &self.loot {
            l.write(&mut w);
        }
        for c in &self.cars {
            c.write(&mut w);
        }
        w.into_inner()
    }

    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        let (player_index, n_players, n_loot, _n_cars) = (r.u8()?, r.u8()?, r.u16()?, r.u16()?);
        let players = (0..n_players)
            .map(|_| ChunkPlayer::read(&mut r))
            .collect::<Result<_, _>>()?;
        let loot = (0..n_loot)
            .map(|_| LootItem::read(&mut r))
            .collect::<Result<_, _>>()?;
        // Car records follow when present; the official buffer is also zero-padded.
        Ok(Self {
            player_index,
            players,
            loot,
            cars: Vec::new(),
        })
    }
}

/// `ChunkExit` (server → one player): what left view, by player index and loot network id.
#[derive(Clone, Debug, PartialEq)]
pub struct ChunkExit {
    pub player_index: u8,
    pub players: Vec<u8>,
    pub loot: Vec<i32>,
    pub cars: Vec<i32>,
}

impl ChunkExit {
    pub fn encode(&self) -> Vec<u8> {
        let mut w = Writer::new();
        w.u8(self.player_index)
            .u8(self.players.len() as u8)
            .u16(self.loot.len() as u16)
            .u16(self.cars.len() as u16);
        for &p in &self.players {
            w.u8(p);
        }
        for &l in &self.loot {
            w.i32(l);
        }
        for &c in &self.cars {
            w.i32(c);
        }
        w.into_inner()
    }

    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        let (player_index, n_players, n_loot, n_cars) = (r.u8()?, r.u8()?, r.u16()?, r.u16()?);
        let players = (0..n_players).map(|_| r.u8()).collect::<Result<_, _>>()?;
        let loot = (0..n_loot).map(|_| r.i32()).collect::<Result<_, _>>()?;
        let cars = (0..n_cars).map(|_| r.i32()).collect::<Result<_, _>>()?;
        Ok(Self {
            player_index,
            players,
            loot,
            cars,
        })
    }
}

/// `PlayerEnteredChunk` (server → players watching that chunk), for a player on foot.
pub fn encode_player_entered_chunk(player: &ChunkPlayer, blessings: [i32; 3]) -> Vec<u8> {
    let mut w = Writer::new();
    player.write(&mut w);
    w.i32(blessings[0])
        .i32(blessings[1])
        .i32(blessings[2])
        .u8(0);
    w.into_inner()
}

/// `PlayerLeftChunk` (server → watchers of the old chunk): `[player, n, players who lost sight…]`.
pub fn encode_player_left_chunk(player: u8, lost_sight: &[u8]) -> Vec<u8> {
    let mut out = vec![player, lost_sight.len() as u8];
    out.extend_from_slice(lost_sight);
    out
}

/// `RequestWeaponPickUp` (client → server).
#[derive(Clone, Debug, PartialEq)]
pub struct PickupRequest {
    pub player_index: u8,
    pub network_id: i32,
    /// Inventory slot the client wants it in; echoed back.
    pub slot: u8,
}

impl PickupRequest {
    pub fn encode(&self) -> Vec<u8> {
        let mut w = Writer::new();
        w.u8(self.player_index).i32(self.network_id).u8(self.slot);
        w.into_inner()
    }

    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        let msg = Self {
            player_index: r.u8()?,
            network_id: r.i32()?,
            slot: r.u8()?,
        };
        r.finish_allow_zero_padding()?;
        Ok(msg)
    }
}

/// `WeaponPickUpAccepted` (server → players watching): `player` took the item.
pub fn encode_pickup_accepted(player: u8, item: &LootItem, slot: u8) -> Vec<u8> {
    let mut w = Writer::new();
    w.u8(player)
        .i32(item.network_id)
        .i32(item.item_id)
        .i32(item.quantity)
        .u8(slot);
    w.into_inner()
}

/// `RequestItemDrop` (client → server): drop `quantity` of `item_id` at `position`.
#[derive(Clone, Debug, PartialEq)]
pub struct DropRequest {
    pub player_index: u8,
    pub item_id: i32,
    pub quantity: i32,
    pub position: [f32; 3],
}

impl DropRequest {
    pub fn encode(&self) -> Vec<u8> {
        let mut w = Writer::new();
        w.u8(self.player_index)
            .i32(self.item_id)
            .i32(self.quantity)
            .vec3(self.position);
        w.into_inner()
    }

    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        let msg = Self {
            player_index: r.u8()?,
            item_id: r.i32()?,
            quantity: r.i32()?,
            position: r.vec3()?,
        };
        r.finish_allow_zero_padding()?;
        Ok(msg)
    }
}

/// `ItemDrop` (server → players watching): new loot lies at `position`.
pub fn encode_item_drop(item: &LootItem) -> Vec<u8> {
    let mut w = Writer::new();
    item.write(&mut w);
    w.into_inner()
}

/// The chunk a world position belongs to (as signed bytes, like the official server).
pub fn chunk_of(position: [f32; 3]) -> (i8, i8) {
    let cell = |v: f32| (v / CHUNK_SIZE).floor().clamp(-128.0, 127.0) as i8;
    (cell(position[0]), cell(position[2]))
}

/// The 3×3 chunks a player at `center` can see.
pub fn area_around(center: (i8, i8)) -> [(i8, i8); 9] {
    let mut out = [(0, 0); 9];
    let mut i = 0;
    for dx in -1i8..=1 {
        for dz in -1i8..=1 {
            out[i] = (center.0.saturating_add(dx), center.1.saturating_add(dz));
            i += 1;
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    fn player() -> ChunkPlayer {
        ChunkPlayer {
            player_index: 2,
            state: 0,
            weapon_type: i32::MAX,
            health: 80.0,
            downed: false,
            skydiving: true,
            gear: vec![1, 2],
            equipment: NO_EQUIPMENT,
            attachments: vec![7],
        }
    }

    #[test]
    fn chunk_entry_and_exit_round_trip() {
        let entry = ChunkEntry {
            player_index: 0,
            players: vec![player()],
            loot: vec![LootItem {
                network_id: 10,
                item_id: 6,
                quantity: 30,
                position: [1.0, 2.0, 3.0],
            }],
            cars: vec![],
        };
        let mut bytes = entry.encode();
        assert_eq!(bytes.len(), 6 + (12 + 4 + 8 + 1 + 12 + 1 + 2) + 24);
        bytes.push(0); // the official buffer has a spare zero byte
        assert_eq!(ChunkEntry::decode(&bytes).unwrap(), entry);
        let exit = ChunkExit {
            player_index: 0,
            players: vec![2, 3],
            loot: vec![10, 11],
            cars: vec![7],
        };
        assert_eq!(ChunkExit::decode(&exit.encode()).unwrap(), exit);
    }

    #[test]
    fn player_entered_chunk_adds_blessings_and_driving_state() {
        let bytes = encode_player_entered_chunk(&player(), [-1, -1, -1]);
        assert_eq!(bytes.len(), (12 + 4 + 8 + 1 + 12 + 1 + 2) + 12 + 1);
        assert_eq!(encode_player_left_chunk(4, &[1, 2]), [4, 2, 1, 2]);
    }

    #[test]
    fn pickup_and_drop_layouts() {
        let req = PickupRequest {
            player_index: 1,
            network_id: 99,
            slot: 2,
        };
        assert_eq!(PickupRequest::decode(&req.encode()).unwrap(), req);
        let item = LootItem {
            network_id: 99,
            item_id: 6,
            quantity: 30,
            position: [0.0; 3],
        };
        assert_eq!(encode_pickup_accepted(1, &item, 2).len(), 14);
        let drop = DropRequest {
            player_index: 1,
            item_id: 6,
            quantity: 10,
            position: [1.0, 2.0, 3.0],
        };
        assert_eq!(DropRequest::decode(&drop.encode()).unwrap(), drop);
        assert_eq!(encode_item_drop(&item).len(), 24);
    }

    #[test]
    fn chunks_and_areas() {
        assert_eq!(chunk_of([0.0, 0.0, 0.0]), (0, 0));
        assert_eq!(chunk_of([-0.5, 0.0, 255.9]), (-1, 1));
        assert_eq!(chunk_of([1e6, 0.0, -1e6]), (127, -128));
        let area = area_around((0, 0));
        assert!(area.contains(&(-1, -1)) && area.contains(&(1, 1)) && area.contains(&(0, 0)));
    }
}
