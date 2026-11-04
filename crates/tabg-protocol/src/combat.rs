//! Damage, downing, death and revives. TABG is client-authoritative here: the shooter (or victim)
//! reports the victim's new health and the server applies the rules and tells everyone.

use crate::codec::{DecodeError, Reader, Writer};
use crate::messages::PackedDirection;

/// Where a hit landed (only some damage events carry it).
#[derive(Clone, Debug, PartialEq)]
pub struct HitInfo {
    pub point: [f32; 3],
    /// Two bytes the official server passes through unchanged (body part / effect ids).
    pub extra: [u8; 2],
}

/// `DamageEvent` (client → server).
#[derive(Clone, Debug, PartialEq)]
pub struct DamageEvent {
    pub attacker: u8,
    pub victim: u8,
    /// The victim's health after the hit, as computed by the reporting client.
    pub new_health: f32,
    pub direction: [f32; 3],
    pub hit: Option<HitInfo>,
    /// Without hit info: when the victim reports its own damage, don't echo it back to the victim.
    pub exclude_victim: bool,
}

impl DamageEvent {
    pub fn encode(&self) -> Vec<u8> {
        let mut w = Writer::new();
        w.u8(self.attacker)
            .u8(self.victim)
            .f32(self.new_health)
            .vec3(self.direction)
            .bool(self.hit.is_some());
        match &self.hit {
            Some(h) => {
                w.vec3(h.point).u8(h.extra[0]).u8(h.extra[1]);
            }
            None => {
                w.bool(self.exclude_victim);
            }
        }
        w.into_inner()
    }

    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        let (attacker, victim, new_health, direction) = (r.u8()?, r.u8()?, r.f32()?, r.vec3()?);
        let (hit, exclude_victim) = if r.bool()? {
            (
                Some(HitInfo {
                    point: r.vec3()?,
                    extra: [r.u8()?, r.u8()?],
                }),
                false,
            )
        } else {
            (None, r.bool()?)
        };
        r.finish_allow_zero_padding()?;
        Ok(Self {
            attacker,
            victim,
            new_health,
            direction,
            hit,
            exclude_victim,
        })
    }
}

/// `PlayerDamaged` (server → players nearby and the victim's team).
#[derive(Clone, Debug, PartialEq)]
pub struct PlayerDamaged {
    pub victim: u8,
    pub attacker: u8,
    pub health: f32,
    pub downed: bool,
    /// Whether any damage was actually applied.
    pub damaged: bool,
    pub direction: PackedDirection,
    pub hit: Option<HitInfo>,
}

impl PlayerDamaged {
    pub fn encode(&self) -> Vec<u8> {
        let flags =
            u8::from(self.downed) | u8::from(self.hit.is_some()) << 1 | u8::from(self.damaged) << 2;
        let mut w = Writer::new();
        w.u8(self.victim)
            .u8(self.attacker)
            .f32(self.health)
            .u8(flags)
            .bytes(&self.direction.0);
        if let Some(h) = &self.hit {
            w.vec3(h.point).u8(h.extra[0]).u8(h.extra[1]);
        }
        w.into_inner()
    }

    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        let (victim, attacker, health, flags) = (r.u8()?, r.u8()?, r.f32()?, r.u8()?);
        let direction = PackedDirection(r.bytes(3)?.try_into().expect("3 bytes"));
        let hit = if flags & 2 != 0 {
            Some(HitInfo {
                point: r.vec3()?,
                extra: [r.u8()?, r.u8()?],
            })
        } else {
            None
        };
        r.finish()?;
        Ok(Self {
            victim,
            attacker,
            health,
            downed: flags & 1 != 0,
            damaged: flags & 4 != 0,
            direction,
            hit,
        })
    }
}

/// `PlayerDead` (server → everyone).
#[derive(Clone, Debug, PartialEq)]
pub struct PlayerDead {
    pub victim: u8,
    /// 255 if nobody hurt them.
    pub last_attacker: u8,
    /// A living teammate to spectate, 255 for none (254 sends them to the respawn minigame).
    pub spectate: u8,
    pub victim_name: String,
    /// The killer's weapon item id, or `i32::MAX`.
    pub killer_weapon: i32,
    pub ring_death: bool,
}

impl PlayerDead {
    pub fn encode(&self) -> Vec<u8> {
        // The name is UTF-16LE with a one-byte byte-length prefix.
        let utf16: Vec<u8> = self
            .victim_name
            .encode_utf16()
            .take(127)
            .flat_map(u16::to_le_bytes)
            .collect();
        let mut w = Writer::new();
        w.u8(self.victim)
            .u8(self.last_attacker)
            .u8(self.spectate)
            .u8(utf16.len() as u8)
            .bytes(&utf16)
            .i32(self.killer_weapon)
            .bool(self.ring_death);
        w.into_inner()
    }

    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        let (victim, last_attacker, spectate) = (r.u8()?, r.u8()?, r.u8()?);
        let len = usize::from(r.u8()?);
        let raw = r.bytes(len)?;
        let units: Vec<u16> = raw
            .chunks_exact(2)
            .map(|c| u16::from_le_bytes([c[0], c[1]]))
            .collect();
        let victim_name = String::from_utf16_lossy(&units);
        let msg = Self {
            victim,
            last_attacker,
            spectate,
            victim_name,
            killer_weapon: r.i32()?,
            ring_death: r.bool()?,
        };
        r.finish()?;
        Ok(msg)
    }
}

/// `TeamDead` (server → everyone): a team is out, with its placement and who finished its downed
/// players.
#[derive(Clone, Debug, PartialEq)]
pub struct TeamDead {
    pub group: u8,
    pub placement: u8,
    /// `(downed player, their last attacker)`.
    pub finished: Vec<(u8, u8)>,
}

impl TeamDead {
    pub fn encode(&self) -> Vec<u8> {
        let mut w = Writer::new();
        w.u8(self.group)
            .u8(self.placement)
            .u8(self.finished.len() as u8);
        for &(p, a) in &self.finished {
            w.u8(p).u8(a);
        }
        w.into_inner()
    }

    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        let (group, placement) = (r.u8()?, r.u8()?);
        let finished = (0..r.u8()?)
            .map(|_| Ok((r.u8()?, r.u8()?)))
            .collect::<Result<_, DecodeError>>()?;
        r.finish()?;
        Ok(Self {
            group,
            placement,
            finished,
        })
    }
}

/// `ReviveState` stage.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ReviveStage {
    Start,
    Stop,
    Finished,
}

/// `ReviveState` (client → server): `[stage, reviver, _, revived]`.
#[derive(Clone, Debug, PartialEq)]
pub struct ReviveRequest {
    pub stage: ReviveStage,
    pub reviver: u8,
    pub revived: u8,
}

impl ReviveRequest {
    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        let stage = match r.u8()? {
            0 => ReviveStage::Start,
            1 => ReviveStage::Stop,
            _ => ReviveStage::Finished,
        };
        let reviver = r.u8()?;
        let _ = r.u8()?;
        let revived = r.u8()?;
        Ok(Self {
            stage,
            reviver,
            revived,
        })
    }

    pub fn encode(&self) -> Vec<u8> {
        vec![self.stage as u8, self.reviver, 0, self.revived]
    }
}

/// `ReviveState` (server → everyone): `[stage, revived, reviver, health…]`. The health is one byte
/// when starting and a float when finished, exactly as the official server writes it.
pub fn encode_revive_state(stage: ReviveStage, revived: u8, reviver: u8, health: f32) -> Vec<u8> {
    let mut w = Writer::new();
    w.u8(stage as u8).u8(revived).u8(reviver);
    match stage {
        ReviveStage::Start => {
            w.u8(health.clamp(0.0, 255.0) as u8);
        }
        ReviveStage::Stop => {}
        ReviveStage::Finished => {
            w.f32(health);
        }
    }
    w.into_inner()
}

/// `FiringMode` flags at byte 1 of `PlayerFire`.
pub mod firing_mode {
    pub const BURST: u8 = 2;
    pub const FULL_AUTO_STOP: u8 = 8;
    pub const WANTS_TO_BE_SYNCED: u8 = 0x40;
}

/// The header of `PlayerFire` (client → server); the rest is relayed unchanged.
#[derive(Clone, Debug, PartialEq)]
pub struct PlayerFireHeader {
    pub player_index: u8,
    pub mode: u8,
    pub ammo_type: i32,
    /// Shots fired, for burst / full-auto-stop (otherwise 1).
    pub shots: i32,
}

impl PlayerFireHeader {
    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        let (player_index, mode, ammo_type) = (r.u8()?, r.u8()?, r.i32()?);
        let shots = if mode & (firing_mode::FULL_AUTO_STOP | firing_mode::BURST) != 0 {
            r.i32()?
        } else {
            1
        };
        Ok(Self {
            player_index,
            mode,
            ammo_type,
            shots,
        })
    }

    /// `PlayerFireSyncReturn` payload for a fire packet that wants to be synced: the client's
    /// projectile index (last 4 bytes) twice.
    pub fn sync_return(payload: &[u8]) -> Option<Vec<u8>> {
        let tail = payload.get(payload.len().checked_sub(4)?..)?;
        Some([tail, tail].concat())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn damage_round_trips_with_and_without_hit_info() {
        let hit = DamageEvent {
            attacker: 1,
            victim: 2,
            new_health: 55.5,
            direction: [0.0, 0.0, 1.0],
            hit: Some(HitInfo {
                point: [1.0, 2.0, 3.0],
                extra: [4, 5],
            }),
            exclude_victim: false,
        };
        let plain = DamageEvent {
            hit: None,
            exclude_victim: true,
            ..hit.clone()
        };
        for msg in [hit, plain] {
            assert_eq!(DamageEvent::decode(&msg.encode()).unwrap(), msg);
        }
    }

    #[test]
    fn player_damaged_has_the_official_size() {
        let msg = PlayerDamaged {
            victim: 2,
            attacker: 1,
            health: 40.0,
            downed: false,
            damaged: true,
            direction: PackedDirection::ZERO,
            hit: None,
        };
        assert_eq!(msg.encode().len(), 10);
        let with_hit = PlayerDamaged {
            hit: Some(HitInfo {
                point: [0.0; 3],
                extra: [1, 2],
            }),
            downed: true,
            ..msg
        };
        assert_eq!(with_hit.encode().len(), 24);
        assert_eq!(PlayerDamaged::decode(&with_hit.encode()).unwrap(), with_hit);
    }

    #[test]
    fn player_dead_uses_utf16_names() {
        let msg = PlayerDead {
            victim: 3,
            last_attacker: 1,
            spectate: 255,
            victim_name: "Jön".into(),
            killer_weapon: i32::MAX,
            ring_death: true,
        };
        let bytes = msg.encode();
        assert_eq!(bytes[3], 6, "3 UTF-16 code units = 6 bytes");
        assert_eq!(bytes.len(), 5 + 6 + 4);
        assert_eq!(PlayerDead::decode(&bytes).unwrap(), msg);
    }

    #[test]
    fn team_dead_and_revive_layouts() {
        let td = TeamDead {
            group: 4,
            placement: 3,
            finished: vec![(4, 1)],
        };
        assert_eq!(td.encode(), [4, 3, 1, 4, 1]);
        assert_eq!(TeamDead::decode(&td.encode()).unwrap(), td);
        assert_eq!(
            encode_revive_state(ReviveStage::Start, 2, 5, 100.0),
            [0, 2, 5, 100]
        );
        assert_eq!(encode_revive_state(ReviveStage::Stop, 2, 5, 0.0), [1, 2, 5]);
        assert_eq!(
            encode_revive_state(ReviveStage::Finished, 2, 5, 15.0).len(),
            7
        );
        let req = ReviveRequest {
            stage: ReviveStage::Finished,
            reviver: 5,
            revived: 2,
        };
        assert_eq!(ReviveRequest::decode(&req.encode()).unwrap(), req);
    }

    #[test]
    fn fire_header_and_sync_return() {
        let mut p = vec![3, firing_mode::BURST | firing_mode::WANTS_TO_BE_SYNCED];
        p.extend(7i32.to_le_bytes());
        p.extend(3i32.to_le_bytes());
        p.extend(99i32.to_le_bytes());
        let h = PlayerFireHeader::decode(&p).unwrap();
        assert_eq!((h.player_index, h.ammo_type, h.shots), (3, 7, 3));
        assert_eq!(
            PlayerFireHeader::sync_return(&p).unwrap(),
            [99i32.to_le_bytes(), 99i32.to_le_bytes()].concat()
        );
    }
}
