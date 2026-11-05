//! The bot radar for the AdminRadar client mod (our event 241, `ARDR` v2): a client says hello
//! when it joins; if the host turned `bot_radar` on (for admins or everyone) it gets the bots'
//! positions and what they are doing four times a second. Only bots are ever sent, so the radar
//! can't be used to find real players.

use tabg_protocol::{Packet, Writer};

use super::{Outgoing, Recipients, Room};
use crate::settings::BotRadar;

pub const RADAR: u8 = 241;
const MAGIC: &[u8; 4] = b"ARDR";
const VERSION: u8 = 2;
const HELLO: u8 = 1;
const ACCEPTED: u8 = 2;
const SNAPSHOT: u8 = 3;
const DENIED: u8 = 4;
const DENIED_DISABLED: u8 = 1;
const DENIED_NOT_ADMIN: u8 = 2;
/// Seconds between snapshots.
const INTERVAL: f32 = 0.25;
/// Snapshot entry flags.
const ALIVE: u8 = 1;
const HAS_GOAL: u8 = 2;

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
        code: RADAR,
        payload: w.into_inner(),
    }
}

fn string16(w: &mut Writer, s: &str) {
    let bytes = &s.as_bytes()[..s.len().min(64)];
    w.u16(bytes.len() as u16).bytes(bytes);
}

impl Room {
    /// May this peer see the radar right now?
    fn radar_allowed(&self, peer: u8) -> Result<(), u8> {
        match self.settings.bot_radar {
            BotRadar::Off => Err(DENIED_DISABLED),
            BotRadar::Everyone => Ok(()),
            // Admin rights are known once the player is in; before that, accept and check on send.
            BotRadar::Admins => match self.players.get(&peer) {
                Some(p) if !p.is_admin => Err(DENIED_NOT_ADMIN),
                _ => Ok(()),
            },
        }
    }

    pub(super) fn radar_hello(&mut self, peer: u8, payload: &[u8]) -> Vec<Outgoing> {
        if payload.len() < 6
            || &payload[..4] != MAGIC
            || payload[4] != VERSION
            || payload[5] != HELLO
        {
            return Vec::new();
        }
        // Remembered even when denied: permission is checked on every snapshot, so turning the
        // radar on (or making someone an admin) takes effect without a rejoin.
        if !self.radar_watchers.contains(&peer) {
            self.radar_watchers.push(peer);
        }
        let reply = match self.radar_allowed(peer) {
            Ok(()) => header(ACCEPTED),
            Err(reason) => {
                let mut w = header(DENIED);
                w.u8(reason);
                w
            }
        };
        vec![Outgoing {
            to: Recipients::One(peer),
            packet: packet(reply),
            reliable: true,
        }]
    }

    /// `[u8 count]` then per bot: `[index][group][state][flags][f32 health][position][goal]
    /// [u8 target, 255 = none][i32 gun item, -1 = none][u16 length][name]`.
    pub(super) fn radar_snapshot(&self) -> Vec<u8> {
        let mut w = header(SNAPSHOT);
        let bots: Vec<u8> = self.bots.keys().copied().collect();
        w.u8(bots.len().min(255) as u8);
        for &bot in bots.iter().take(255) {
            let (Some(p), Some(v)) = (self.players.get(&bot), self.bot_view(bot)) else {
                // Keep the count right: an empty, dead entry.
                w.u8(bot).u8(0).u8(0).u8(0).f32(0.0);
                w.vec3([0.0; 3]).vec3([0.0; 3]).u8(u8::MAX).i32(-1);
                string16(&mut w, "");
                continue;
            };
            let flags =
                if p.alive { ALIVE } else { 0 } | if v.goal.is_some() { HAS_GOAL } else { 0 };
            w.u8(bot)
                .u8(p.group)
                .u8(v.state as u8)
                .u8(flags)
                .f32(p.health);
            w.vec3(p.position).vec3(v.goal.unwrap_or([0.0; 3]));
            w.u8(v.target.unwrap_or(u8::MAX)).i32(v.gun.unwrap_or(-1));
            string16(&mut w, &p.name);
        }
        w.into_inner()
    }

    pub(super) fn tick_radar(&mut self, now: f32) -> Vec<Outgoing> {
        if self.radar_watchers.is_empty() || self.bots.is_empty() || now < self.next_radar_at {
            return Vec::new();
        }
        self.next_radar_at = now + INTERVAL;
        let to: Vec<u8> = self
            .radar_watchers
            .iter()
            .copied()
            .filter(|&peer| {
                self.players.get(&peer).is_some_and(|p| p.in_world)
                    && self.radar_allowed(peer).is_ok()
            })
            .collect();
        if to.is_empty() {
            return Vec::new();
        }
        vec![Outgoing {
            to: Recipients::Many(to),
            packet: Packet {
                code: RADAR,
                payload: self.radar_snapshot(),
            },
            reliable: false,
        }]
    }
}
