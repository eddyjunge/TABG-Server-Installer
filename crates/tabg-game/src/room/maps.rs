//! The Range (WilhelmTest) and DevTest maps from the RangeMap / DevTestMap client mods. Their
//! clients say hello (events 242 / 243) when joining; we accept, they load the map instead of the
//! Test scene, and their F6 menu asks for items, which go straight into the inventory.
//! Replaces the old RangeMap.Server / DevTestMap.Server BepInEx plugins.

use tabg_protocol::Packet;
use tabg_protocol::actions::encode_player_loot;
use tabg_protocol::{EventCode, Writer};

use super::{Outgoing, Recipients, Room};
use crate::settings::TestMap;

pub const RANGE: u8 = 242;
pub const DEV_TEST: u8 = 243;

const HELLO: u8 = 1;
const ACCEPTED: u8 = 2;
const GIVE_ITEM: u8 = 3;
const ITEM_GRANTED: u8 = 4;

struct Proto {
    code: u8,
    magic: &'static [u8; 4],
    version: u8,
}

const RANGE_PROTO: Proto = Proto {
    code: RANGE,
    magic: b"RNGE",
    version: 2,
};
const DEV_TEST_PROTO: Proto = Proto {
    code: DEV_TEST,
    magic: b"DVTM",
    version: 1,
};

fn proto(map: TestMap) -> &'static Proto {
    match map {
        TestMap::Range => &RANGE_PROTO,
        TestMap::DevTest => &DEV_TEST_PROTO,
    }
}

fn header(p: &Proto, op: u8) -> Writer {
    let mut w = Writer::new();
    for &b in p.magic {
        w.u8(b);
    }
    w.u8(p.version).u8(op);
    w
}

impl Room {
    /// A Range/DevTest message from `peer` (`code` is 242 or 243).
    pub(super) fn test_map(&mut self, peer: u8, code: u8, payload: &[u8]) -> Vec<Outgoing> {
        let Some(map) = self.settings.test_map else {
            return Vec::new();
        };
        let p = proto(map);
        if code != p.code
            || payload.len() < 6
            || &payload[..4] != p.magic
            || payload[4] != p.version
        {
            return Vec::new();
        }
        let reply = |w: Writer| Outgoing {
            to: Recipients::One(peer),
            packet: Packet {
                code: p.code,
                payload: w.into_inner(),
            },
            reliable: true,
        };
        match payload[5] {
            // Hello comes before RoomInit, so the peer may not be a player yet.
            HELLO => vec![reply(header(p, ACCEPTED))],
            GIVE_ITEM if payload.len() >= 11 && self.players.contains_key(&peer) => {
                let item = i32::from_le_bytes(payload[6..10].try_into().expect("4 bytes"));
                let quantity = payload[10].max(1);
                if item < 0 {
                    return Vec::new();
                }
                let mut out = vec![Outgoing {
                    to: Recipients::One(peer),
                    packet: Packet::new(
                        EventCode::PlayerLootRecieved,
                        encode_player_loot(&[(item, i32::from(quantity))]),
                    ),
                    reliable: true,
                }];
                if map == TestMap::Range {
                    let mut w = header(p, ITEM_GRANTED);
                    w.i32(item).u8(quantity);
                    out.push(reply(w));
                }
                out
            }
            _ => Vec::new(),
        }
    }
}
