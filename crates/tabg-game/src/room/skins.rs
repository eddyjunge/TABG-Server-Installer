//! Outfits from the CustomGameSkins client mod (our event 244), only when the host turns
//! `skins` on: authorized clients may wear any built-in clothing, which everyone sees through a
//! vanilla `GearChange`. Replaces the old CustomGameSkins.Server BepInEx plugin.

use tabg_protocol::{EventCode, Packet, Writer};

use super::{Outgoing, Recipients, Room};

pub const SKINS: u8 = 244;
const MAGIC: &[u8; 4] = b"SKIN";
const VERSION: u8 = 1;
const HELLO: u8 = 1;
const ACCEPTED: u8 = 2;
const APPLY_OUTFIT: u8 = 3;
const OUTFIT_APPLIED: u8 = 4;
const DENIED: u8 = 5;
const DENIED_DISABLED: u8 = 1;
const DENIED_NOT_AUTHORIZED: u8 = 2;
const DENIED_PLAYER_NOT_READY: u8 = 3;
const DENIED_INVALID_OUTFIT: u8 = 4;
const DENIED_RATE_LIMITED: u8 = 5;
/// Six slots of (gear item, colour).
const GEAR_VALUES: usize = 12;
/// Seconds between outfit changes.
const MIN_INTERVAL: f32 = 0.5;

fn header(op: u8) -> Writer {
    let mut w = Writer::new();
    for &b in MAGIC {
        w.u8(b);
    }
    w.u8(VERSION).u8(op);
    w
}

fn to(peer: u8, w: Writer) -> Outgoing {
    Outgoing {
        to: Recipients::One(peer),
        packet: Packet {
            code: SKINS,
            payload: w.into_inner(),
        },
        reliable: true,
    }
}

fn denied(peer: u8, reason: u8) -> Outgoing {
    let mut w = header(DENIED);
    w.u8(reason);
    to(peer, w)
}

/// Shape check: each slot is (-1, -1) for "nothing" or a gear item with a colour (-1 = default).
fn valid(gear: &[i32]) -> Option<Vec<i32>> {
    let mut out = Vec::with_capacity(GEAR_VALUES);
    for pair in gear.chunks(2) {
        let (item, colour) = (pair[0], pair[1]);
        if item == -1 {
            out.extend([-1, -1]);
        } else if (0..100_000).contains(&item) && (-1..10_000).contains(&colour) {
            out.extend([item, colour]);
        } else {
            return None;
        }
    }
    Some(out)
}

impl Room {
    pub(super) fn skins(&mut self, peer: u8, payload: &[u8]) -> Vec<Outgoing> {
        if payload.len() < 6 || &payload[..4] != MAGIC || payload[4] != VERSION {
            return Vec::new();
        }
        match payload[5] {
            HELLO => {
                if !self.settings.skins {
                    return vec![denied(peer, DENIED_DISABLED)];
                }
                if !self.skins_authorized.contains(&peer) {
                    self.skins_authorized.push(peer);
                }
                vec![to(peer, header(ACCEPTED))]
            }
            APPLY_OUTFIT => {
                if !self.settings.skins || !self.skins_authorized.contains(&peer) {
                    return vec![denied(peer, DENIED_NOT_AUTHORIZED)];
                }
                let now = self.last_now.unwrap_or(0.0);
                let Some(p) = self.players.get_mut(&peer) else {
                    return vec![denied(peer, DENIED_PLAYER_NOT_READY)];
                };
                if now - p.last_outfit < MIN_INTERVAL {
                    return vec![denied(peer, DENIED_RATE_LIMITED)];
                }
                let body = &payload[6..];
                if body.len() != 4 + GEAR_VALUES * 4
                    || i32::from_le_bytes(body[..4].try_into().expect("4 bytes"))
                        != GEAR_VALUES as i32
                {
                    return vec![denied(peer, DENIED_INVALID_OUTFIT)];
                }
                let requested: Vec<i32> = body[4..]
                    .chunks(4)
                    .map(|c| i32::from_le_bytes(c.try_into().expect("4 bytes")))
                    .collect();
                let Some(gear) = valid(&requested) else {
                    return vec![denied(peer, DENIED_INVALID_OUTFIT)];
                };
                p.last_outfit = now;
                p.gear.clone_from(&gear);
                // Vanilla GearChange: [player][i32 count][i32 values…].
                let mut change = Writer::new();
                change.u8(peer).i32(GEAR_VALUES as i32);
                let mut applied = header(OUTFIT_APPLIED);
                applied.i32(GEAR_VALUES as i32);
                for &g in &gear {
                    change.i32(g);
                    applied.i32(g);
                }
                vec![
                    Outgoing {
                        to: Recipients::All,
                        packet: Packet::new(EventCode::GearChange, change.into_inner()),
                        reliable: true,
                    },
                    to(peer, applied),
                ]
            }
            _ => Vec::new(),
        }
    }
}
