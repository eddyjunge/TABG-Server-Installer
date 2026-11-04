//! Cars in the world: who sits where, where they are, and their damageable parts.

use tabg_protocol::messages::{CarPart, PackedDirection, PackedQuaternion, WorldCar};
use tabg_protocol::vehicles::ChunkCar;

#[derive(Clone, Debug, PartialEq)]
pub struct CarSeat {
    pub network_index: i32,
    pub driver: bool,
    pub occupant: Option<u8>,
}

/// How a car starts out (from the world dump).
#[derive(Clone, Debug, PartialEq)]
pub struct CarSpawn {
    pub car_type: i32,
    pub car_index: i32,
    pub position: [f32; 3],
    pub rotation: [f32; 4],
    /// `(network index, is driver seat)`.
    pub seats: Vec<(i32, bool)>,
    pub parts: Vec<CarPart>,
}

/// How long the last driver may keep pushing an empty car (official: 5 s).
pub const TEMPORARY_OWNER_SECONDS: f32 = 5.0;

#[derive(Clone, Debug, PartialEq)]
pub struct Car {
    pub index: i32,
    pub car_type: i32,
    pub position: [f32; 3],
    pub rotation: PackedQuaternion,
    pub input: PackedDirection,
    pub driving_state: u8,
    pub health: f32,
    pub seats: Vec<CarSeat>,
    pub parts: Vec<CarPart>,
    /// The last driver may move the empty car until this time.
    pub temporary_owner: Option<(u8, f32)>,
    /// Moved by its temporary owner since the last movement tick.
    pub temporary_changed: bool,
}

impl Car {
    pub fn spawn(s: &CarSpawn) -> Self {
        Self {
            index: s.car_index,
            car_type: s.car_type,
            position: s.position,
            rotation: PackedQuaternion::pack(s.rotation),
            input: PackedDirection::ZERO,
            driving_state: 0,
            health: 100.0,
            seats: s
                .seats
                .iter()
                .map(|&(network_index, driver)| CarSeat {
                    network_index,
                    driver,
                    occupant: None,
                })
                .collect(),
            parts: s.parts.clone(),
            temporary_owner: None,
            temporary_changed: false,
        }
    }

    pub fn world_entry(&self) -> WorldCar {
        WorldCar {
            car_type: self.car_type,
            car_index: self.index,
            seat_network_indices: self.seats.iter().map(|s| s.network_index).collect(),
            parts: self.parts.clone(),
        }
    }

    pub fn chunk_entry(&self) -> ChunkCar {
        ChunkCar {
            car_index: self.index,
            health: self.health,
            position: self.position,
            rotation: self.rotation.clone(),
            occupants: self
                .seats
                .iter()
                .map(|s| s.occupant.unwrap_or(u8::MAX))
                .collect(),
        }
    }

    /// The seat with this network index.
    pub fn seat_mut(&mut self, network_index: i32) -> Option<&mut CarSeat> {
        self.seats
            .iter_mut()
            .find(|s| s.network_index == network_index)
    }

    /// Remove `player` from whatever seat they hold. Returns whether they were in this car.
    pub fn eject(&mut self, player: u8) -> bool {
        let mut found = false;
        for s in &mut self.seats {
            if s.occupant == Some(player) {
                s.occupant = None;
                found = true;
            }
        }
        found
    }
}
