//! Proximity voice chat for the ProximityChat client mod (our event 240): 20 ms PCM frames from a
//! speaker go, unreliably, to everyone within `voice_range` metres, with the sender byte set by
//! the server. Replaces the old ProximityChat.Server BepInEx plugin.

use tabg_protocol::Packet;

use super::{Outgoing, Recipients, Room};

/// Our voice event code (codes ≥ 240 are tabg's own, §4.3).
pub const VOICE: u8 = 240;
const VERSION: u8 = 1;
const HEADER: usize = 10;
const MAX_PCM: usize = 1024;
/// 20 ms frames make 50 per second; allow some slack.
const MAX_PER_SECOND: u16 = 60;

impl Room {
    /// A voice frame from `peer`: `[version 1][sender][u16 seq][u16 16000][format 1][channels 1]
    /// [u16 320][PCM u8]`.
    pub(super) fn voice(&mut self, peer: u8, payload: &[u8]) -> Vec<Outgoing> {
        let range = self.settings.voice_range;
        if range <= 0.0
            || payload.len() <= HEADER
            || payload.len() > HEADER + MAX_PCM
            || payload[0] != VERSION
        {
            return Vec::new();
        }
        let now = self.last_now.unwrap_or(0.0);
        let Some(p) = self.players.get_mut(&peer) else {
            return Vec::new();
        };
        if !p.alive {
            return Vec::new();
        }
        if now - p.voice_window.0 >= 1.0 {
            p.voice_window = (now, 0);
        }
        p.voice_window.1 += 1;
        if p.voice_window.1 > MAX_PER_SECOND {
            return Vec::new();
        }
        let from = p.position;
        let to: Vec<u8> = self
            .players
            .values()
            .filter(|o| o.index != peer && o.in_world)
            .filter(|o| {
                let d = [
                    o.position[0] - from[0],
                    o.position[1] - from[1],
                    o.position[2] - from[2],
                ];
                d[0] * d[0] + d[1] * d[1] + d[2] * d[2] <= range * range
            })
            .map(|o| o.index)
            .collect();
        if to.is_empty() {
            return Vec::new();
        }
        let mut relayed = payload.to_vec();
        relayed[1] = peer;
        vec![Outgoing {
            to: Recipients::Many(to),
            packet: Packet {
                code: VOICE,
                payload: relayed,
            },
            reliable: false,
        }]
    }
}
