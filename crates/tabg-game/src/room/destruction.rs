//! Destructible buildings for the Destruction client mod (our event 246, `DSTR` v1). The server
//! never simulates buildings: clients report blasts, the server keeps one ordered list and every
//! modded client applies it, so everyone ends up with the same holes. Each player sees an
//! explosion locally and reports it, so reports of the same blast are merged. Players who join
//! later get the list when they say hello.

use tabg_protocol::{Packet, Reader, Writer};

use super::{Outgoing, Recipients, Room};

pub const DESTRUCTION: u8 = 246;
const MAGIC: &[u8; 4] = b"DSTR";
const VERSION: u8 = 1;
const HELLO: u8 = 1;
const ACCEPTED: u8 = 2;
const DENIED: u8 = 3;
const REPORT: u8 = 4;
const BLAST: u8 = 5;
const RESET: u8 = 6;

/// Reports this close in space and time are the same blast seen by different players.
const MERGE_DISTANCE: f32 = 3.0;
const MERGE_SECONDS: f32 = 0.5;
const MAX_RADIUS: f32 = 40.0;
const MAX_POWER: f32 = 1000.0;
/// The map is about 2 km across; nothing legitimate happens far outside it.
const MAX_COORDINATE: f32 = 4000.0;
/// Oldest blasts are forgotten past this, so a very long match can't grow without bound.
const MAX_LOG: usize = 5000;
/// Reports one peer may send per second (a burst of air-strike shells stays under this).
const MAX_REPORTS_PER_SECOND: u32 = 60;

#[derive(Clone, Copy, Debug, PartialEq)]
struct Blast {
    seq: u32,
    position: [f32; 3],
    radius: f32,
    power: f32,
    at: f32,
}

#[derive(Debug, Default)]
pub(super) struct DestructionState {
    /// Peers whose mod said hello.
    clients: Vec<u8>,
    log: Vec<Blast>,
    next_seq: u32,
    /// (peer, window start, reports in the window).
    rate: Vec<(u8, f32, u32)>,
    /// A new match begins, so clients should rebuild the map.
    pub(super) reset_pending: bool,
}

fn header(op: u8) -> Writer {
    let mut w = Writer::new();
    for &b in MAGIC {
        w.u8(b);
    }
    w.u8(VERSION).u8(op);
    w
}

fn packet(w: Writer) -> Packet {
    Packet {
        code: DESTRUCTION,
        payload: w.into_inner(),
    }
}

fn blast_packet(b: &Blast) -> Packet {
    let mut w = header(BLAST);
    w.u32(b.seq).vec3(b.position).f32(b.radius).f32(b.power);
    packet(w)
}

impl DestructionState {
    fn allow_report(&mut self, peer: u8, now: f32) -> bool {
        let entry = match self.rate.iter().position(|r| r.0 == peer) {
            Some(i) => &mut self.rate[i],
            None => {
                self.rate.push((peer, now, 0));
                self.rate.last_mut().expect("just pushed")
            }
        };
        if now - entry.1 >= 1.0 {
            entry.1 = now;
            entry.2 = 0;
        }
        entry.2 += 1;
        entry.2 <= MAX_REPORTS_PER_SECOND
    }

    fn is_repeat(&self, position: [f32; 3], now: f32) -> bool {
        self.log
            .iter()
            .rev()
            .take_while(|b| now - b.at <= MERGE_SECONDS)
            .any(|b| {
                let d: f32 = (0..3).map(|i| (b.position[i] - position[i]).powi(2)).sum();
                d <= MERGE_DISTANCE * MERGE_DISTANCE
            })
    }
}

impl Room {
    pub(super) fn destruction_message(&mut self, peer: u8, payload: &[u8]) -> Vec<Outgoing> {
        if payload.len() < 6 || &payload[..4] != MAGIC || payload[4] != VERSION {
            return Vec::new();
        }
        match payload[5] {
            HELLO => self.destruction_hello(peer),
            REPORT => self.destruction_report(peer, &payload[6..]),
            _ => Vec::new(),
        }
    }

    fn destruction_hello(&mut self, peer: u8) -> Vec<Outgoing> {
        if !self.settings.destruction {
            return vec![Outgoing {
                to: Recipients::One(peer),
                packet: packet(header(DENIED)),
                reliable: true,
            }];
        }
        if !self.destruction.clients.contains(&peer) {
            self.destruction.clients.push(peer);
        }
        let mut out = vec![Outgoing {
            to: Recipients::One(peer),
            packet: packet(header(ACCEPTED)),
            reliable: true,
        }];
        out.extend(self.destruction.log.iter().map(|b| Outgoing {
            to: Recipients::One(peer),
            packet: blast_packet(b),
            reliable: true,
        }));
        out
    }

    fn destruction_report(&mut self, peer: u8, payload: &[u8]) -> Vec<Outgoing> {
        if !self.settings.destruction || !self.destruction.clients.contains(&peer) {
            return Vec::new();
        }
        let now = self.last_now.unwrap_or(0.0);
        if !self.destruction.allow_report(peer, now) {
            return Vec::new();
        }
        let mut r = Reader::new(payload);
        let (Ok(position), Ok(radius), Ok(power)) = (r.vec3(), r.f32(), r.f32()) else {
            return Vec::new();
        };
        let valid = position
            .iter()
            .all(|c| c.is_finite() && c.abs() <= MAX_COORDINATE)
            && radius.is_finite()
            && radius > 0.0
            && radius <= MAX_RADIUS
            && power.is_finite()
            && power > 0.0
            && power <= MAX_POWER;
        if !valid || self.destruction.is_repeat(position, now) {
            return Vec::new();
        }
        let blast = Blast {
            seq: self.destruction.next_seq,
            position,
            radius,
            power,
            at: now,
        };
        self.destruction.next_seq += 1;
        if self.destruction.log.len() >= MAX_LOG {
            self.destruction.log.remove(0);
        }
        self.destruction.log.push(blast);
        vec![Outgoing {
            to: Recipients::Many(self.destruction.clients.clone()),
            packet: blast_packet(&blast),
            reliable: true,
        }]
    }

    pub(super) fn destruction_forget(&mut self, peer: u8) {
        self.destruction.clients.retain(|&p| p != peer);
        self.destruction.rate.retain(|r| r.0 != peer);
    }

    /// A new match on the same map: every building is whole again.
    pub(super) fn tick_destruction(&mut self) -> Vec<Outgoing> {
        if !std::mem::take(&mut self.destruction.reset_pending) {
            return Vec::new();
        }
        self.destruction.log.clear();
        if self.destruction.clients.is_empty() {
            return Vec::new();
        }
        vec![Outgoing {
            to: Recipients::Many(self.destruction.clients.clone()),
            packet: packet(header(RESET)),
            reliable: true,
        }]
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::RoomSettings;

    fn hello() -> Packet {
        packet(header(HELLO))
    }

    fn report(pos: [f32; 3]) -> Packet {
        let mut w = header(REPORT);
        w.vec3(pos).f32(3.0).f32(120.0);
        packet(w)
    }

    fn room() -> Room {
        Room::new(RoomSettings::default())
    }

    fn blasts(out: &[Outgoing]) -> usize {
        out.iter()
            .filter(|o| o.packet.code == DESTRUCTION && o.packet.payload[5] == BLAST)
            .count()
    }

    #[test]
    fn a_blast_goes_to_every_modded_client_once() {
        let mut room = room();
        room.handle(1, &hello());
        room.handle(2, &hello());
        let out = room.handle(1, &report([10.0, 5.0, 10.0]));
        assert_eq!(blasts(&out), 1);
        assert!(matches!(&out[0].to, Recipients::Many(p) if p == &vec![1, 2]));
        // The other player saw the same explosion a moment later.
        assert!(room.handle(2, &report([11.0, 5.0, 10.5])).is_empty());
        // A different place is a different blast.
        assert_eq!(blasts(&room.handle(2, &report([50.0, 5.0, 10.0]))), 1);
    }

    #[test]
    fn repeats_are_only_merged_for_a_moment() {
        let mut room = room();
        room.handle(1, &hello());
        room.tick(10.0);
        assert_eq!(blasts(&room.handle(1, &report([0.0, 0.0, 0.0]))), 1);
        room.tick(11.0);
        assert_eq!(
            blasts(&room.handle(1, &report([0.0, 0.0, 0.0]))),
            1,
            "same spot, a second later"
        );
    }

    #[test]
    fn latecomers_get_the_blasts_so_far_in_order() {
        let mut room = room();
        room.handle(1, &hello());
        room.handle(1, &report([0.0, 0.0, 0.0]));
        room.handle(1, &report([100.0, 0.0, 0.0]));
        let out = room.handle(2, &hello());
        assert_eq!(out[0].packet.payload[5], ACCEPTED);
        assert_eq!(blasts(&out), 2);
        assert!(out[1].packet.payload[6..10] == 0u32.to_le_bytes());
        assert!(out[2].packet.payload[6..10] == 1u32.to_le_bytes());
    }

    #[test]
    fn nonsense_is_refused() {
        let mut room = room();
        room.handle(1, &hello());
        assert!(room.handle(1, &report([f32::NAN, 0.0, 0.0])).is_empty());
        assert!(room.handle(1, &report([9e6, 0.0, 0.0])).is_empty());
        let mut w = header(REPORT);
        w.vec3([0.0; 3]).f32(4000.0).f32(1.0);
        assert!(room.handle(1, &packet(w)).is_empty(), "radius");
        assert!(
            room.handle(9, &report([0.0; 3])).is_empty(),
            "never said hello"
        );
    }

    #[test]
    fn it_can_be_turned_off() {
        let mut room = Room::new(RoomSettings {
            destruction: false,
            ..Default::default()
        });
        let out = room.handle(1, &hello());
        assert_eq!(out[0].packet.payload[5], DENIED);
        assert!(room.handle(1, &report([0.0; 3])).is_empty());
    }

    #[test]
    fn a_new_match_starts_with_whole_buildings() {
        let mut room = room();
        room.handle(1, &hello());
        room.handle(1, &report([0.0; 3]));
        room.destruction.reset_pending = true;
        let out = room.tick_destruction();
        assert_eq!(out[0].packet.payload[5], RESET);
        assert!(room.destruction.log.is_empty());
        assert!(room.tick_destruction().is_empty());
    }
}
