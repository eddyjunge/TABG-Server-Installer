//! Messages that drive a match: game state changes, the plane, dropping, and the ring.

use crate::codec::{DecodeError, Reader, Writer};
use crate::messages::game_state;

/// `GameStateChanged` (server → everyone): `[state][extra]`.
#[derive(Clone, Debug, PartialEq)]
pub enum GameStateChanged {
    /// Countdown started; seconds left.
    CountDown {
        seconds: f32,
    },
    /// The plane took off: its path, the time of day and the match modifier index.
    Flying {
        start: [f32; 3],
        end: [f32; 3],
        time_of_day: f32,
        modifier: u8,
    },
    Started,
    /// Match over. `winner` is the winning team (group) index, or 255 if nobody won.
    Ended {
        winner: u8,
    },
    /// Any other state, without extra data.
    Other(u8),
}

impl GameStateChanged {
    pub const NO_WINNER: u8 = 255;

    pub fn encode(&self) -> Vec<u8> {
        let mut w = Writer::new();
        match self {
            Self::CountDown { seconds } => {
                w.u8(game_state::COUNT_DOWN).f32(*seconds);
            }
            Self::Flying {
                start,
                end,
                time_of_day,
                modifier,
            } => {
                w.u8(game_state::FLYING)
                    .vec3(*start)
                    .vec3(*end)
                    .f32(*time_of_day)
                    .u8(*modifier);
            }
            Self::Started => {
                w.u8(game_state::STARTED);
            }
            Self::Ended { winner } => {
                w.u8(game_state::ENDED).u8(*winner);
            }
            Self::Other(state) => {
                w.u8(*state);
            }
        }
        w.into_inner()
    }

    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        let msg = match r.u8()? {
            game_state::COUNT_DOWN => Self::CountDown { seconds: r.f32()? },
            game_state::FLYING => Self::Flying {
                start: r.vec3()?,
                end: r.vec3()?,
                time_of_day: r.f32()?,
                modifier: r.u8()?,
            },
            // `Started` may carry a round time in Bomb mode; we don't model that yet.
            game_state::STARTED => {
                return Ok(Self::Started);
            }
            game_state::ENDED => Self::Ended { winner: r.u8()? },
            other => {
                return Ok(Self::Other(other));
            }
        };
        r.finish()?;
        Ok(msg)
    }
}

/// `RingUpdate` (server → everyone).
#[derive(Clone, Debug, PartialEq)]
pub enum RingUpdate {
    /// Re-sync: seconds the blue ring has been travelling.
    Sync { time_travelled: f32 },
    /// The next (white) target ring.
    NextRing {
        ring_id: u8,
        center: [f32; 3],
        size: f32,
    },
    /// The blue ring starts shrinking toward the white one.
    StartedMoving,
}

impl RingUpdate {
    pub fn encode(&self) -> Vec<u8> {
        let mut w = Writer::new();
        match self {
            Self::Sync { time_travelled } => {
                w.u8(0).f32(*time_travelled);
            }
            Self::NextRing {
                ring_id,
                center,
                size,
            } => {
                w.u8(1).u8(*ring_id).vec3(*center).f32(*size);
            }
            Self::StartedMoving => {
                w.u8(2);
            }
        }
        w.into_inner()
    }

    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        let msg = match r.u8()? {
            0 => Self::Sync {
                time_travelled: r.f32()?,
            },
            1 => Self::NextRing {
                ring_id: r.u8()?,
                center: r.vec3()?,
                size: r.f32()?,
            },
            _ => Self::StartedMoving,
        };
        r.finish()?;
        Ok(msg)
    }
}

/// `PlaneUpdate` (server → everyone): plane progress 0..1, sent every 5 s.
pub fn encode_plane_update(progress: f32) -> Vec<u8> {
    let mut w = Writer::new();
    w.f32(progress);
    w.into_inner()
}

/// `RequestAirplaneDrop` (client → server): the player jumps, looking along `direction`.
#[derive(Clone, Debug, PartialEq)]
pub struct RequestAirplaneDrop {
    pub player_index: u8,
    pub direction: [f32; 3],
}

impl RequestAirplaneDrop {
    pub fn encode(&self) -> Vec<u8> {
        let mut w = Writer::new();
        w.u8(self.player_index).vec3(self.direction);
        w.into_inner()
    }

    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        let msg = Self {
            player_index: r.u8()?,
            direction: r.vec3()?,
        };
        r.finish_allow_zero_padding()?;
        Ok(msg)
    }
}

/// `PlayerAirplaneDropped` (server → everyone): a player left the plane at `position`.
#[derive(Clone, Debug, PartialEq)]
pub struct PlayerAirplaneDropped {
    pub player_index: u8,
    pub position: [f32; 3],
    pub direction: [f32; 3],
}

impl PlayerAirplaneDropped {
    pub fn encode(&self) -> Vec<u8> {
        let mut w = Writer::new();
        w.u8(self.player_index)
            .vec3(self.position)
            .vec3(self.direction);
        w.into_inner()
    }

    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        let msg = Self {
            player_index: r.u8()?,
            position: r.vec3()?,
            direction: r.vec3()?,
        };
        r.finish()?;
        Ok(msg)
    }
}

/// `AllDrop` (server → everyone still in the plane): the plane reached the end; jump at `position`.
pub fn encode_all_drop(position: [f32; 3]) -> Vec<u8> {
    let mut w = Writer::new();
    w.vec3(position);
    w.into_inner()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn game_state_changes_match_official_layouts() {
        let cd = GameStateChanged::CountDown { seconds: 20.0 };
        assert_eq!(cd.encode(), [&[1u8][..], &20.0f32.to_le_bytes()].concat());
        // Official GetPlaneExtraData: 6 floats, time of day, modifier byte (29 bytes) + state byte.
        let flying = GameStateChanged::Flying {
            start: [-1200.0, 200.0, 5.0],
            end: [1200.0, 200.0, -3.0],
            time_of_day: 0.25,
            modifier: 0,
        };
        assert_eq!(flying.encode().len(), 30);
        assert_eq!(GameStateChanged::Ended { winner: 3 }.encode(), [4, 3]);
        for msg in [
            cd,
            flying,
            GameStateChanged::Started,
            GameStateChanged::Ended { winner: 255 },
        ] {
            assert_eq!(GameStateChanged::decode(&msg.encode()).unwrap(), msg);
        }
    }

    #[test]
    fn ring_updates_match_official_layouts() {
        assert_eq!(
            RingUpdate::Sync {
                time_travelled: 1.0
            }
            .encode()
            .len(),
            5
        );
        assert_eq!(
            RingUpdate::NextRing {
                ring_id: 1,
                center: [0.0; 3],
                size: 3450.0
            }
            .encode()
            .len(),
            18
        );
        assert_eq!(RingUpdate::StartedMoving.encode(), [2]);
        for msg in [
            RingUpdate::Sync {
                time_travelled: 3.5,
            },
            RingUpdate::NextRing {
                ring_id: 2,
                center: [1.0, 2.0, 3.0],
                size: 1710.0,
            },
            RingUpdate::StartedMoving,
        ] {
            assert_eq!(RingUpdate::decode(&msg.encode()).unwrap(), msg);
        }
    }

    #[test]
    fn drop_messages_round_trip() {
        let req = RequestAirplaneDrop {
            player_index: 2,
            direction: [0.0, -0.5, 1.0],
        };
        assert_eq!(req.encode().len(), 13);
        assert_eq!(RequestAirplaneDrop::decode(&req.encode()).unwrap(), req);
        let dropped = PlayerAirplaneDropped {
            player_index: 2,
            position: [10.0, 195.0, 3.0],
            direction: req.direction,
        };
        assert_eq!(dropped.encode().len(), 25);
        assert_eq!(
            PlayerAirplaneDropped::decode(&dropped.encode()).unwrap(),
            dropped
        );
        assert_eq!(encode_all_drop([1.0, 2.0, 3.0]).len(), 12);
        assert_eq!(encode_plane_update(0.5).len(), 4);
    }
}
