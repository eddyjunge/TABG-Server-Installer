//! The TABG network protocol.
//!
//! Every packet on the wire is `[event code: u8][payload]` (see [`Packet`]). Payloads are written
//! the way .NET's `BinaryWriter` writes them: little-endian numbers, 1-byte booleans and
//! 7-bit-length-prefixed UTF-8 strings ([`codec`]).
//!
//! Message layouts are implemented from protocol knowledge so that unmodified TABG clients can
//! talk to our server. Field comments say which side sends a message.

pub mod actions;
pub mod codec;
pub mod combat;
pub mod event;
pub mod loot;
pub mod match_flow;
pub mod messages;
pub mod vehicles;

pub use codec::{DecodeError, Reader, Writer};
pub use event::EventCode;

/// One TABG packet: an event code and its payload.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Packet {
    /// Raw event byte. Use [`Packet::event`] to interpret it; unknown codes are kept, not dropped.
    pub code: u8,
    pub payload: Vec<u8>,
}

impl Packet {
    pub fn new(event: EventCode, payload: Vec<u8>) -> Self {
        Self {
            code: event as u8,
            payload,
        }
    }

    pub fn event(&self) -> Option<EventCode> {
        EventCode::from_u8(self.code)
    }

    /// Split a raw ENet/UTP packet into code and payload. Empty packets have no code.
    pub fn from_wire(bytes: &[u8]) -> Option<Self> {
        let (&code, payload) = bytes.split_first()?;
        Some(Self {
            code,
            payload: payload.to_vec(),
        })
    }

    pub fn to_wire(&self) -> Vec<u8> {
        let mut out = Vec::with_capacity(1 + self.payload.len());
        out.push(self.code);
        out.extend_from_slice(&self.payload);
        out
    }
}
