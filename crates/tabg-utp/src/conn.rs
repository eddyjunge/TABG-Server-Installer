//! One Unity Transport 1.x connection: the UDP connection protocol (`UnityTransportProtocol`) and
//! TABG's two pipelines.
//!
//! TABG creates pipeline 1 = Fragmentation + ReliableSequenced (window 64) and pipeline 2 =
//! UnreliableSequenced, on both client and server. A data packet is
//! `[UdpCHeader 10][pipeline id][stage headers][payload][receive token 8, until data arrived]`.
//! For pipeline 1 the reliable header (16 bytes with window 64) is outermost, then the 2-byte
//! fragment header. Inside the pipeline TABG frames each message as `[u16 length][code][payload]`.

use std::collections::VecDeque;
use std::time::{Duration, Instant};

pub type Token = [u8; 8];

pub const CONNECTION_REQUEST: u8 = 0;
pub const CONNECTION_REJECT: u8 = 1;
pub const CONNECTION_ACCEPT: u8 = 2;
pub const DISCONNECT: u8 = 3;
pub const DATA: u8 = 4;
pub const PING: u8 = 5;
pub const PONG: u8 = 6;

pub const FLAG_CONNECT_TOKEN: u8 = 1;
pub const FLAG_PIPELINE: u8 = 2;

pub const HEADER_LEN: usize = 10;

pub const RELIABLE_PIPELINE: u8 = 1;
pub const UNRELIABLE_PIPELINE: u8 = 2;

const WINDOW: u16 = 64;
const RELIABLE_HEADER_LEN: usize = 16;
const ACK: u16 = 1;
/// Largest fragment we put in one datagram; with all headers the datagram stays well under
/// Unity's 1400-byte MTU.
pub const MAX_FRAGMENT: usize = 1200;
/// Largest unreliable message that fits one datagram (UnreliableSequenced doesn't fragment).
pub const MAX_UNRELIABLE: usize = MAX_FRAGMENT;
/// Our fragmentation receive capacity: TABG's server uses 81920 (its client only 20000).
pub const FRAGMENT_CAPACITY: usize = 81920;
/// What TABG's client can reassemble; bigger reliable messages are dropped on its side.
pub const CLIENT_FRAGMENT_CAPACITY: usize = 20000;

/// Unity Transport defaults (`NetworkParameterConstants`).
pub const DISCONNECT_TIMEOUT: Duration = Duration::from_millis(30_000);
pub const HEARTBEAT_TIMEOUT: Duration = Duration::from_millis(500);
pub const CONNECT_TIMEOUT: Duration = Duration::from_millis(1000);

const MIN_RESEND_MS: f32 = 64.0;
const MAX_RESEND_MS: f32 = 200.0;

/// Parsed `UdpCHeader`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Header {
    pub kind: u8,
    pub flags: u8,
    pub token: Token,
}

pub fn parse_header(packet: &[u8]) -> Option<(Header, &[u8])> {
    if packet.len() < HEADER_LEN {
        return None;
    }
    let header = Header {
        kind: packet[0],
        flags: packet[1],
        token: packet[2..10].try_into().ok()?,
    };
    if header.kind != DATA && header.flags & FLAG_PIPELINE != 0 {
        return None;
    }
    Some((header, &packet[HEADER_LEN..]))
}

pub fn header_only(kind: u8, token: Token) -> Vec<u8> {
    let mut p = Vec::with_capacity(HEADER_LEN);
    p.push(kind);
    p.push(0);
    p.extend_from_slice(&token);
    p
}

/// `GreaterThan16` from Unity's `SequenceHelpers`: is `lhs` newer than `rhs`, with wrap-around.
fn greater16(lhs: u16, rhs: u16) -> bool {
    (lhs > rhs && lhs - rhs <= 32767) || (lhs < rhs && rhs - lhs > 32767)
}

fn less16(lhs: u16, rhs: u16) -> bool {
    greater16(rhs, lhs)
}

struct Sent {
    seq: u16,
    /// Everything after the reliable header (fragment header + fragment).
    data: Vec<u8>,
    first_sent: Instant,
    last_sent: Instant,
    rtt_measured: bool,
}

/// ReliableSequenced, both directions (`ReliableUtility`).
struct Reliable {
    // Send side.
    next_seq: u16,
    remote_acked: u16,
    remote_acked_mask: u64,
    slots: Vec<Option<Sent>>,
    waiting: VecDeque<Vec<u8>>,
    smoothed_rtt: f32,
    smoothed_variance: f32,
    // Receive side.
    received: u16,
    received_mask: u64,
    acked_received: u16,
    last_acked_mask: u64,
    duplicates: u32,
    next_deliver: u16,
    pending: Vec<Option<(u16, Vec<u8>)>>,
    remote_receive_times: Vec<Option<(u16, Instant)>>,
}

struct RawHeader {
    kind: u16,
    processing_time: u16,
    seq: u16,
    acked: u16,
    acked_mask: u64,
}

impl RawHeader {
    fn read(b: &[u8]) -> Option<Self> {
        if b.len() < RELIABLE_HEADER_LEN {
            return None;
        }
        let u = |i: usize| u16::from_le_bytes([b[i], b[i + 1]]);
        Some(Self {
            kind: u(0),
            processing_time: u(2),
            seq: u(4),
            acked: u(6),
            acked_mask: u64::from_le_bytes(b[8..16].try_into().ok()?),
        })
    }

    fn write(&self, out: &mut Vec<u8>) {
        out.extend_from_slice(&self.kind.to_le_bytes());
        out.extend_from_slice(&self.processing_time.to_le_bytes());
        out.extend_from_slice(&self.seq.to_le_bytes());
        out.extend_from_slice(&self.acked.to_le_bytes());
        out.extend_from_slice(&self.acked_mask.to_le_bytes());
    }
}

impl Reliable {
    fn new() -> Self {
        Self {
            next_seq: 0,
            remote_acked: u16::MAX,
            remote_acked_mask: u64::MAX,
            slots: (0..WINDOW).map(|_| None).collect(),
            waiting: VecDeque::new(),
            smoothed_rtt: 50.0,
            smoothed_variance: 5.0,
            received: u16::MAX,
            received_mask: u64::MAX,
            acked_received: 0,
            last_acked_mask: u64::MAX,
            duplicates: 0,
            next_deliver: 0,
            pending: (0..WINDOW).map(|_| None).collect(),
            remote_receive_times: (0..WINDOW).map(|_| None).collect(),
        }
    }

    fn resend_after(&self) -> Duration {
        let timeout = self.smoothed_rtt + 4.0 * self.smoothed_variance;
        let ms = if timeout > MAX_RESEND_MS {
            MAX_RESEND_MS
        } else {
            timeout.max(MIN_RESEND_MS)
        };
        Duration::from_secs_f32(ms / 1000.0)
    }

    fn processing_time(&self, seq: u16, now: Instant) -> u16 {
        match self.remote_receive_times[usize::from(seq % WINDOW)] {
            Some((s, at)) if s == seq => {
                u16::try_from(now.duration_since(at).as_millis()).unwrap_or(u16::MAX)
            }
            _ => 0,
        }
    }

    /// The header for a payload or ack we're about to send; records that our acks went out.
    fn header(&mut self, kind: u16, seq: u16, now: Instant) -> RawHeader {
        let h = RawHeader {
            kind,
            processing_time: self.processing_time(self.received, now),
            seq,
            acked: self.received,
            acked_mask: self.received_mask,
        };
        self.acked_received = self.received;
        self.last_acked_mask = self.received_mask;
        self.duplicates = 0;
        h
    }

    fn frame(&mut self, kind: u16, seq: u16, data: &[u8], now: Instant) -> Vec<u8> {
        let mut out = Vec::with_capacity(1 + RELIABLE_HEADER_LEN + data.len());
        out.push(RELIABLE_PIPELINE);
        self.header(kind, seq, now).write(&mut out);
        out.extend_from_slice(data);
        out
    }

    /// Queue one fragment (fragment header included); returns pipeline packets to send now.
    fn send(&mut self, fragment: Vec<u8>, now: Instant, out: &mut Vec<Vec<u8>>) {
        self.waiting.push_back(fragment);
        self.drain_waiting(now, out);
    }

    fn drain_waiting(&mut self, now: Instant, out: &mut Vec<Vec<u8>>) {
        while !self.waiting.is_empty() {
            let slot = usize::from(self.next_seq % WINDOW);
            if self.slots[slot].is_some() {
                return; // window full until the peer acks
            }
            let data = self.waiting.pop_front().expect("checked non-empty");
            let seq = self.next_seq;
            self.next_seq = self.next_seq.wrapping_add(1);
            out.push(self.frame(0, seq, &data, now));
            self.slots[slot] = Some(Sent {
                seq,
                data,
                first_sent: now,
                last_sent: now,
                rtt_measured: false,
            });
        }
    }

    /// `ReadAckPacket`: learn what the peer received, release those packets, update the RTT.
    fn read_ack(&mut self, h: &RawHeader, now: Instant) {
        if let Some(sent) = self.slots[usize::from(h.acked % WINDOW)].as_mut()
            && sent.seq == h.acked
            && !sent.rtt_measured
        {
            sent.rtt_measured = true;
            let rtt = now.duration_since(sent.first_sent).as_secs_f32() * 1000.0
                - f32::from(h.processing_time);
            let delta = rtt.max(1.0) - self.smoothed_rtt;
            self.smoothed_rtt += delta / 8.0;
            self.smoothed_variance += (delta.abs() - self.smoothed_variance) / 4.0;
        }
        if !greater16(self.remote_acked, h.acked) {
            if self.remote_acked == h.acked {
                self.remote_acked_mask |= h.acked_mask;
            } else {
                self.remote_acked = h.acked;
                self.remote_acked_mask = h.acked_mask;
            }
        }
        for slot in &mut self.slots {
            if let Some(sent) = slot {
                let d = self.remote_acked.wrapping_sub(sent.seq);
                if d < WINDOW && self.remote_acked_mask & (1u64 << d) != 0 {
                    *slot = None;
                }
            }
        }
    }

    /// Handle one pipeline-1 packet (after the pipeline id); returns in-order fragments.
    fn receive(&mut self, packet: &[u8], now: Instant) -> Vec<Vec<u8>> {
        let Some(h) = RawHeader::read(packet) else {
            return Vec::new();
        };
        if h.kind == ACK {
            self.read_ack(&h, now);
            return Vec::new();
        }
        let body = &packet[RELIABLE_HEADER_LEN..];
        // `ReliableUtility.Read`.
        if less16(h.seq, self.received.wrapping_add(1).wrapping_sub(WINDOW)) {
            return Vec::new(); // stale
        }
        if greater16(h.seq, self.received) {
            let distance = h.seq.wrapping_sub(self.received);
            if distance > WINDOW - 1 {
                self.received_mask = 1;
            } else {
                self.received_mask = (self.received_mask << distance) | 1;
            }
            self.received = h.seq;
        } else {
            let distance = self.received.wrapping_sub(h.seq);
            if distance >= WINDOW {
                return Vec::new();
            }
            let bit = 1u64 << distance;
            if self.received_mask & bit != 0 {
                self.read_ack(&h, now);
                self.duplicates += 1;
                return Vec::new();
            }
            self.received_mask |= bit;
        }
        self.remote_receive_times[usize::from(h.seq % WINDOW)] = Some((h.seq, now));
        self.read_ack(&h, now);

        let mut delivered = Vec::new();
        if h.seq == self.next_deliver {
            delivered.push(body.to_vec());
            self.next_deliver = self.next_deliver.wrapping_add(1);
            loop {
                let slot = &mut self.pending[usize::from(self.next_deliver % WINDOW)];
                match slot.take() {
                    Some((seq, data)) if seq == self.next_deliver => {
                        delivered.push(data);
                        self.next_deliver = self.next_deliver.wrapping_add(1);
                    }
                    other => {
                        *slot = other;
                        break;
                    }
                }
            }
        } else if greater16(h.seq, self.next_deliver) {
            self.pending[usize::from(h.seq % WINDOW)] = Some((h.seq, body.to_vec()));
        }
        delivered
    }

    /// Resend what the peer hasn't acked in time, and ack what we received.
    fn update(&mut self, now: Instant, out: &mut Vec<Vec<u8>>) {
        let resend_after = self.resend_after();
        let mut resend = Vec::new();
        for sent in self.slots.iter_mut().flatten() {
            if now.duration_since(sent.last_sent) > resend_after {
                sent.last_sent = now;
                resend.push((sent.seq, sent.data.clone()));
            }
        }
        resend.sort_by(|a, b| {
            if less16(a.0, b.0) {
                std::cmp::Ordering::Less
            } else {
                std::cmp::Ordering::Greater
            }
        });
        for (seq, data) in resend {
            out.push(self.frame(0, seq, &data, now));
        }
        if less16(self.acked_received, self.received)
            || self.received_mask != self.last_acked_mask
            || self.duplicates >= 3
        {
            // Unity's ack packets carry one junk payload byte.
            out.push(self.frame(ACK, 0, &[0], now));
        }
    }

    fn in_flight(&self) -> usize {
        self.slots.iter().flatten().count() + self.waiting.len()
    }
}

/// FragmentationPipelineStage receive state.
struct Reassembly {
    sequence: u16,
    buffer: Vec<u8>,
    error: bool,
}

impl Reassembly {
    fn receive(&mut self, fragment: &[u8]) -> Option<Vec<u8>> {
        if fragment.len() < 2 {
            return None;
        }
        let header = u16::from_le_bytes([fragment[0], fragment[1]]);
        let seq = header & 0x3FFF;
        let first = header & 0x8000 != 0;
        let last = header & 0x4000 != 0;
        let data = &fragment[2..];
        let mut expected = self.sequence;
        if first {
            expected = seq;
            self.error = false;
            self.buffer.clear();
        }
        if seq != expected {
            self.error = true;
            self.buffer.clear();
        }
        let mut message = None;
        if !self.error {
            if !last || !self.buffer.is_empty() {
                if self.buffer.len() + data.len() > FRAGMENT_CAPACITY {
                    tracing::warn!("relay: fragmentation capacity exceeded, dropping message");
                    return None;
                }
                self.buffer.extend_from_slice(data);
            }
            if last {
                message = Some(if self.buffer.is_empty() {
                    data.to_vec()
                } else {
                    std::mem::take(&mut self.buffer)
                });
            }
        }
        if !last || self.error {
            message = None;
        }
        self.sequence = (seq + 1) & 0x3FFF;
        message
    }
}

/// What happened on a connection, for the host or client that owns it.
#[derive(Debug, PartialEq, Eq)]
pub enum ConnEvent {
    /// A TABG packet (`[code][payload]`).
    Message(Vec<u8>),
    Disconnected,
}

/// A Unity Transport connection to one remote endpoint. Produces raw Unity Transport packets in
/// `out` for the owner to wrap in relay messages.
pub struct Connection {
    pub receive_token: Token,
    pub send_token: Token,
    pub connected: bool,
    did_receive_data: bool,
    pub last_receive: Instant,
    last_non_data_send: Instant,
    reliable: Reliable,
    fragment_seq: u16,
    reassembly: Reassembly,
    unreliable_seq: u16,
    unreliable_received: u16,
    pub out: Vec<Vec<u8>>,
}

pub fn random_token() -> Token {
    let mut t = [0u8; 8];
    getrandom::fill(&mut t).expect("OS random source");
    t
}

impl Connection {
    pub fn new(receive_token: Token, send_token: Token, now: Instant) -> Self {
        Self {
            receive_token,
            send_token,
            connected: false,
            did_receive_data: false,
            last_receive: now,
            last_non_data_send: now,
            reliable: Reliable::new(),
            fragment_seq: 0,
            reassembly: Reassembly {
                sequence: 0,
                buffer: Vec::new(),
                error: false,
            },
            unreliable_seq: 0,
            unreliable_received: u16::MAX,
            out: Vec::new(),
        }
    }

    /// The server's answer to a `ConnectionRequest` (18 bytes while we haven't had data).
    pub fn connection_accept(&mut self, now: Instant) -> Vec<u8> {
        self.last_non_data_send = now;
        let mut p = header_only(CONNECTION_ACCEPT, self.send_token);
        if !self.did_receive_data {
            p[1] |= FLAG_CONNECT_TOKEN;
            p.extend_from_slice(&self.receive_token);
        }
        p
    }

    fn push_data(&mut self, pipeline_packet: Vec<u8>) {
        let mut p = Vec::with_capacity(HEADER_LEN + pipeline_packet.len() + 8);
        p.push(DATA);
        let mut flags = FLAG_PIPELINE;
        if !self.did_receive_data {
            flags |= FLAG_CONNECT_TOKEN;
        }
        p.push(flags);
        p.extend_from_slice(&self.send_token);
        p.extend_from_slice(&pipeline_packet);
        if !self.did_receive_data {
            p.extend_from_slice(&self.receive_token);
        }
        self.out.push(p);
    }

    /// Send one TABG packet. Reliable messages are fragmented; unreliable ones too big for one
    /// datagram go reliably instead.
    pub fn send(&mut self, message: &[u8], reliable: bool, now: Instant) {
        let len = u16::try_from(message.len()).unwrap_or(u16::MAX);
        let mut framed = Vec::with_capacity(2 + message.len());
        framed.extend_from_slice(&len.to_le_bytes());
        framed.extend_from_slice(message);
        if !reliable && framed.len() <= MAX_UNRELIABLE {
            let mut p = Vec::with_capacity(3 + framed.len());
            p.push(UNRELIABLE_PIPELINE);
            p.extend_from_slice(&self.unreliable_seq.to_le_bytes());
            p.extend_from_slice(&framed);
            self.unreliable_seq = self.unreliable_seq.wrapping_add(1);
            self.push_data(p);
            return;
        }
        if framed.len() > CLIENT_FRAGMENT_CAPACITY {
            tracing::warn!(
                len = framed.len(),
                "relay: message exceeds the TABG client's reassembly buffer"
            );
        }
        let chunks: Vec<&[u8]> = framed.chunks(MAX_FRAGMENT).collect();
        let mut packets = Vec::new();
        for (i, chunk) in chunks.iter().enumerate() {
            let mut header = self.fragment_seq & 0x3FFF;
            self.fragment_seq = self.fragment_seq.wrapping_add(1);
            if i == 0 {
                header |= 0x8000;
            }
            if i + 1 == chunks.len() {
                header |= 0x4000;
            }
            let mut fragment = Vec::with_capacity(2 + chunk.len());
            fragment.extend_from_slice(&header.to_le_bytes());
            fragment.extend_from_slice(chunk);
            self.reliable.send(fragment, now, &mut packets);
        }
        for p in packets {
            self.push_data(p);
        }
    }

    /// Handle a Unity Transport packet whose header token already matched this connection.
    pub fn receive(&mut self, header: Header, body: &[u8], now: Instant) -> Vec<ConnEvent> {
        self.last_receive = now;
        match header.kind {
            DATA => {
                self.did_receive_data = true;
                let mut body = body;
                if header.flags & FLAG_CONNECT_TOKEN != 0 {
                    if body.len() < 8 {
                        return Vec::new();
                    }
                    body = &body[..body.len() - 8];
                }
                if header.flags & FLAG_PIPELINE == 0 || body.is_empty() {
                    return Vec::new();
                }
                self.receive_pipeline(body[0], &body[1..], now)
            }
            PING => {
                self.did_receive_data = true;
                self.last_non_data_send = now;
                self.out.push(header_only(PONG, self.send_token));
                Vec::new()
            }
            PONG => {
                self.did_receive_data = true;
                Vec::new()
            }
            DISCONNECT => {
                self.connected = false;
                vec![ConnEvent::Disconnected]
            }
            _ => Vec::new(),
        }
    }

    fn receive_pipeline(&mut self, pipeline: u8, packet: &[u8], now: Instant) -> Vec<ConnEvent> {
        let mut framed = Vec::new();
        match pipeline {
            RELIABLE_PIPELINE => {
                for fragment in self.reliable.receive(packet, now) {
                    if let Some(message) = self.reassembly.receive(&fragment) {
                        framed.push(message);
                    }
                }
            }
            UNRELIABLE_PIPELINE if packet.len() >= 2 => {
                let seq = u16::from_le_bytes([packet[0], packet[1]]);
                if greater16(seq, self.unreliable_received) {
                    self.unreliable_received = seq;
                    framed.push(packet[2..].to_vec());
                }
            }
            _ => {}
        }
        framed
            .into_iter()
            .filter_map(|f| {
                let len = usize::from(u16::from_le_bytes([*f.first()?, *f.get(1)?]));
                f.get(2..2 + len).map(|m| ConnEvent::Message(m.to_vec()))
            })
            .collect()
    }

    /// Retransmits, acks and heartbeats. Returns `true` if the connection timed out.
    pub fn update(&mut self, now: Instant) -> bool {
        if self.connected && now.duration_since(self.last_receive) > DISCONNECT_TIMEOUT {
            self.connected = false;
            return true;
        }
        let mut packets = Vec::new();
        self.reliable.update(now, &mut packets);
        self.reliable.drain_waiting(now, &mut packets);
        for p in packets {
            self.push_data(p);
        }
        if self.connected
            && self.did_receive_data
            && now.duration_since(self.last_receive) > HEARTBEAT_TIMEOUT
            && now.duration_since(self.last_non_data_send) > HEARTBEAT_TIMEOUT
        {
            self.last_non_data_send = now;
            self.out.push(header_only(PING, self.send_token));
        }
        false
    }

    pub fn disconnect_packet(&self) -> Vec<u8> {
        header_only(DISCONNECT, self.send_token)
    }

    /// Reliable fragments sent but not yet acked, plus those waiting for window space.
    pub fn reliable_backlog(&self) -> usize {
        self.reliable.in_flight()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Two connected endpoints that exchange packets in memory.
    fn pair(now: Instant) -> (Connection, Connection) {
        let a_token = [1; 8];
        let b_token = [2; 8];
        let mut a = Connection::new(a_token, b_token, now);
        let mut b = Connection::new(b_token, a_token, now);
        a.connected = true;
        b.connected = true;
        (a, b)
    }

    /// Deliver `from`'s queued packets to `to`, optionally dropping some.
    fn deliver(
        from: &mut Connection,
        to: &mut Connection,
        now: Instant,
        mut drop: impl FnMut(usize) -> bool,
    ) -> Vec<Vec<u8>> {
        let mut messages = Vec::new();
        for (i, p) in std::mem::take(&mut from.out).into_iter().enumerate() {
            if drop(i) {
                continue;
            }
            let (h, body) = parse_header(&p).unwrap();
            assert_eq!(h.token, to.receive_token);
            for e in to.receive(h, body, now) {
                if let ConnEvent::Message(m) = e {
                    messages.push(m);
                }
            }
        }
        messages
    }

    #[test]
    fn greater16_matches_unity() {
        assert!(greater16(1, 0));
        assert!(greater16(0, 65535));
        assert!(!greater16(0, 0));
        assert!(!greater16(65535, 0));
        assert!(greater16(40000, 10000));
        assert!(
            !greater16(50000, 10000),
            "more than half the range back is newer"
        );
    }

    #[test]
    fn small_reliable_message_layout() {
        let now = Instant::now();
        let (mut a, _) = pair(now);
        a.send(&[7, 1, 2], true, now);
        let p = &a.out[0];
        assert_eq!(p[0], DATA);
        assert_eq!(
            p[1],
            FLAG_PIPELINE | FLAG_CONNECT_TOKEN,
            "no data received yet"
        );
        assert_eq!(&p[2..10], &[2; 8]);
        assert_eq!(p[10], RELIABLE_PIPELINE);
        assert_eq!(&p[11..13], &0u16.to_le_bytes(), "payload type");
        assert_eq!(&p[15..17], &0u16.to_le_bytes(), "sequence 0");
        assert_eq!(&p[17..19], &u16::MAX.to_le_bytes(), "nothing received yet");
        assert_eq!(
            &p[27..29],
            &0xC000u16.to_le_bytes(),
            "first|last fragment 0"
        );
        assert_eq!(&p[29..31], &3u16.to_le_bytes(), "TABG length prefix");
        assert_eq!(&p[31..34], &[7, 1, 2]);
        assert_eq!(&p[34..], &[1; 8], "receive token until data arrives");
        assert_eq!(p.len(), 42);
    }

    #[test]
    fn unreliable_round_trip_drops_old() {
        let now = Instant::now();
        let (mut a, mut b) = pair(now);
        a.send(&[1], false, now);
        a.send(&[2], false, now);
        let mut packets = std::mem::take(&mut a.out);
        packets.reverse();
        a.out = packets;
        // Newer first: the older one is then dropped, like UnreliableSequenced does.
        assert_eq!(deliver(&mut a, &mut b, now, |_| false), vec![vec![2]]);
    }

    #[test]
    fn reliable_large_message_survives_loss_and_reordering() {
        let t0 = Instant::now();
        let (mut a, mut b) = pair(t0);
        let big: Vec<u8> = (0..10_000u32).map(|i| i as u8).collect();
        a.send(&big, true, t0);
        a.send(&[9, 9], true, t0);
        assert_eq!(a.out.len(), 10, "9 fragments + 1");
        // Lose every third packet and deliver the rest backwards.
        let mut packets = std::mem::take(&mut a.out);
        packets.reverse();
        a.out = packets;
        let mut got = deliver(&mut a, &mut b, t0, |i| i % 3 == 0);
        assert!(got.is_empty(), "the first fragment was lost");
        // b acks, a resends what's missing after the resend timeout.
        b.update(t0);
        deliver(&mut b, &mut a, t0, |_| false);
        let later = t0 + Duration::from_millis(250);
        a.update(later);
        assert!(!a.out.is_empty());
        got.extend(deliver(&mut a, &mut b, later, |_| false));
        assert_eq!(got, vec![big, vec![9, 9]]);
        b.update(later);
        deliver(&mut b, &mut a, later, |_| false);
        assert_eq!(a.reliable_backlog(), 0, "everything acked");
    }

    #[test]
    fn window_full_queues_until_acked() {
        let t0 = Instant::now();
        let (mut a, mut b) = pair(t0);
        for i in 0..100u8 {
            a.send(&[i], true, t0);
        }
        assert_eq!(a.out.len(), 64, "window is 64");
        let mut got = deliver(&mut a, &mut b, t0, |_| false);
        b.update(t0);
        deliver(&mut b, &mut a, t0, |_| false);
        a.update(t0);
        got.extend(deliver(&mut a, &mut b, t0, |_| false));
        assert_eq!(got, (0..100u8).map(|i| vec![i]).collect::<Vec<_>>());
    }

    #[test]
    fn sequence_wraps() {
        let t0 = Instant::now();
        let (mut a, mut b) = pair(t0);
        let mut got = Vec::new();
        for round in 0..1100u32 {
            for i in 0..64u32 {
                a.send(&(round * 64 + i).to_le_bytes(), true, t0);
            }
            got.extend(deliver(&mut a, &mut b, t0, |_| false));
            b.update(t0);
            deliver(&mut b, &mut a, t0, |_| false);
            a.update(t0);
        }
        assert_eq!(got.len(), 1100 * 64);
        assert_eq!(got.last().unwrap(), &(1100u32 * 64 - 1).to_le_bytes());
        assert_eq!(a.reliable_backlog(), 0);
    }

    #[test]
    fn ping_gets_pong_and_tokens_flip_off_after_data() {
        let now = Instant::now();
        let (mut a, mut b) = pair(now);
        let ping = header_only(PING, b.receive_token);
        let (h, body) = parse_header(&ping).unwrap();
        b.receive(h, body, now);
        assert_eq!(b.out, vec![header_only(PONG, a.receive_token)]);
        b.out.clear();
        b.send(&[1], true, now);
        assert_eq!(
            b.out[0][1], FLAG_PIPELINE,
            "b has heard from a, so no token"
        );
        a.send(&[1], true, now);
        assert_eq!(a.out[0][1], FLAG_PIPELINE | FLAG_CONNECT_TOKEN);
    }

    #[test]
    fn accept_is_18_bytes() {
        let now = Instant::now();
        let mut c = Connection::new([1; 8], [2; 8], now);
        let p = c.connection_accept(now);
        assert_eq!(p.len(), 18);
        assert_eq!(p[0], CONNECTION_ACCEPT);
        assert_eq!(p[1], FLAG_CONNECT_TOKEN);
        assert_eq!(&p[2..10], &[2; 8]);
        assert_eq!(&p[10..], &[1; 8]);
    }
}
