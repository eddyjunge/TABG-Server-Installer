//! Cars: seats, driving, pushing (temporary owners), damage, and passengers.

use crate::codec::{DecodeError, Reader, Writer};
use crate::messages::{PackedDirection, PackedQuaternion};

/// A car as listed in `ChunkEntry`.
#[derive(Clone, Debug, PartialEq)]
pub struct ChunkCar {
    pub car_index: i32,
    pub health: f32,
    pub position: [f32; 3],
    pub rotation: PackedQuaternion,
    /// Occupant player index per seat, 255 = empty.
    pub occupants: Vec<u8>,
}

impl ChunkCar {
    pub fn write(&self, w: &mut Writer) {
        w.i32(self.car_index)
            .f32(self.health)
            .vec3(self.position)
            .bytes(&self.rotation.0);
        for &o in &self.occupants {
            w.u8(o);
        }
    }
}

/// `SeatAction`.
pub const SEAT_GET_IN: u8 = 0;
pub const SEAT_GET_OUT: u8 = 1;

/// `RequestSeat` (client → server).
#[derive(Clone, Debug, PartialEq)]
pub struct SeatRequest {
    pub player_index: u8,
    pub car_index: i32,
    pub seat_index: i32,
    pub action: u8,
}

impl SeatRequest {
    pub fn encode(&self) -> Vec<u8> {
        let mut w = Writer::new();
        w.u8(self.player_index)
            .i32(self.car_index)
            .i32(self.seat_index)
            .u8(self.action);
        w.into_inner()
    }

    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        let msg = Self {
            player_index: r.u8()?,
            car_index: r.i32()?,
            seat_index: r.i32()?,
            action: r.u8()?,
        };
        r.finish_allow_zero_padding()?;
        Ok(msg)
    }
}

/// `SeatAccepted` (server → players near the car). When getting in, the car's pose is included.
pub fn encode_seat_accepted(
    player: u8,
    car_index: i32,
    seat_network_index: i32,
    action: u8,
    pose: Option<([f32; 3], &PackedQuaternion)>,
) -> Vec<u8> {
    let mut w = Writer::new();
    w.u8(player)
        .i32(car_index)
        .i32(seat_network_index)
        .u8(action);
    if let Some((position, rotation)) = pose {
        w.vec3(position).bytes(&rotation.0);
    }
    w.into_inner()
}

/// `CarUpdate` (driver → server).
#[derive(Clone, Debug, PartialEq)]
pub struct CarUpdate {
    pub player_index: u8,
    pub position: [f32; 3],
    pub rotation: PackedQuaternion,
    pub input: PackedDirection,
    pub driving_state: u8,
}

impl CarUpdate {
    pub fn encode(&self) -> Vec<u8> {
        let mut w = Writer::new();
        w.u8(self.player_index)
            .vec3(self.position)
            .bytes(&self.rotation.0)
            .bytes(&self.input.0)
            .u8(self.driving_state);
        w.into_inner()
    }

    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        let msg = Self {
            player_index: r.u8()?,
            position: r.vec3()?,
            rotation: PackedQuaternion::read(&mut r)?,
            input: PackedDirection(r.bytes(3)?.try_into().expect("3")),
            driving_state: r.u8()?,
        };
        r.finish_allow_zero_padding()?;
        Ok(msg)
    }
}

/// `CarTemporaryUpdate` (client → server): the last driver pushes an empty car around.
#[derive(Clone, Debug, PartialEq)]
pub struct CarTemporaryUpdate {
    pub player_index: u8,
    pub position: [f32; 3],
    pub rotation: PackedQuaternion,
    pub car_index: i32,
}

impl CarTemporaryUpdate {
    pub fn encode(&self) -> Vec<u8> {
        let mut w = Writer::new();
        w.u8(self.player_index)
            .vec3(self.position)
            .bytes(&self.rotation.0)
            .i32(self.car_index);
        w.into_inner()
    }

    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        let msg = Self {
            player_index: r.u8()?,
            position: r.vec3()?,
            rotation: PackedQuaternion::read(&mut r)?,
            car_index: r.i32()?,
        };
        r.finish_allow_zero_padding()?;
        Ok(msg)
    }
}

/// `CarDamage` (client → server): `[_, part, car, new part health]`.
#[derive(Clone, Debug, PartialEq)]
pub struct CarDamageReport {
    pub part: u8,
    pub car_index: i32,
    pub new_health: f32,
}

impl CarDamageReport {
    pub fn encode(&self, sender: u8) -> Vec<u8> {
        let mut w = Writer::new();
        w.u8(sender)
            .u8(self.part)
            .i32(self.car_index)
            .f32(self.new_health);
        w.into_inner()
    }

    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        let _ = r.u8()?;
        let msg = Self {
            part: r.u8()?,
            car_index: r.i32()?,
            new_health: r.f32()?,
        };
        r.finish_allow_zero_padding()?;
        Ok(msg)
    }
}

/// `CarDamage` (server → players at the car): `[part, car, part health]`.
pub fn encode_car_damage(part: u8, car_index: i32, health: f32) -> Vec<u8> {
    let mut w = Writer::new();
    w.u8(part).i32(car_index).f32(health);
    w.into_inner()
}

/// `PassangerUpdate` (passenger → server): look rotation and aiming.
#[derive(Clone, Debug, PartialEq)]
pub struct PassengerUpdate {
    pub player_index: u8,
    pub rotation: [f32; 2],
    pub aiming_down_sights: bool,
}

impl PassengerUpdate {
    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        let msg = Self {
            player_index: r.u8()?,
            rotation: [r.f32()?, r.f32()?],
            aiming_down_sights: r.bool()?,
        };
        r.finish_allow_zero_padding()?;
        Ok(msg)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn quaternions_pack_like_the_official_code() {
        assert_eq!(PackedQuaternion::pack([0.0, 0.0, 0.0, 1.0]).0, [7]);
        assert_eq!(PackedQuaternion::pack([0.0, 1.0, 0.0, 0.0]).0, [5]);
        let q = [0.103_396_48, 0.141_993_51, 0.054_843_504, 0.982_925_8];
        let p = PackedQuaternion::pack(q);
        assert_eq!((p.0.len(), p.0[0]), (7, 3));
        let back = p.unpack();
        for i in 0..4 {
            assert!((back[i] - q[i]).abs() < 2e-4, "{back:?} vs {q:?}");
        }
    }

    #[test]
    fn car_messages_round_trip() {
        let seat = SeatRequest {
            player_index: 1,
            car_index: 4,
            seat_index: 0,
            action: SEAT_GET_IN,
        };
        assert_eq!(SeatRequest::decode(&seat.encode()).unwrap(), seat);
        let rot = PackedQuaternion::pack([0.0, 0.0, 0.0, 1.0]);
        assert_eq!(
            encode_seat_accepted(1, 4, 0, SEAT_GET_IN, Some(([1.0, 2.0, 3.0], &rot))).len(),
            10 + 12 + 1
        );
        assert_eq!(encode_seat_accepted(1, 4, 0, SEAT_GET_OUT, None).len(), 10);
        let up = CarUpdate {
            player_index: 1,
            position: [1.0; 3],
            rotation: PackedQuaternion::pack([0.1, 0.2, 0.3, 0.927]),
            input: PackedDirection::ZERO,
            driving_state: 2,
        };
        assert_eq!(CarUpdate::decode(&up.encode()).unwrap(), up);
        let tmp = CarTemporaryUpdate {
            player_index: 1,
            position: [1.0; 3],
            rotation: rot.clone(),
            car_index: 9,
        };
        assert_eq!(CarTemporaryUpdate::decode(&tmp.encode()).unwrap(), tmp);
        let dmg = CarDamageReport {
            part: 2,
            car_index: 9,
            new_health: 30.0,
        };
        assert_eq!(CarDamageReport::decode(&dmg.encode(1)).unwrap(), dmg);
        assert_eq!(encode_car_damage(2, 9, 30.0).len(), 9);
    }
}
