//! Unity Relay's UDP messages (Unity Transport 1.x `RelayNetworkProtocol`).
//!
//! Every message starts with a 4-byte header: signature `0x72DA` (little-endian), version 0, type.
//! Game traffic travels in `Relay` messages addressed by 16-byte allocation ids; the 2-byte data
//! length of a `Relay` message is big-endian, everything else is little-endian.

use hmac::{Hmac, KeyInit, Mac};
use sha2::Sha256;

pub type AllocationId = [u8; 16];
pub type ConnectionData = [u8; 255];
pub type HmacKey = [u8; 64];

const SIGNATURE: [u8; 2] = [0xDA, 0x72];
const VERSION: u8 = 0;

pub const BIND: u8 = 0;
pub const BIND_RECEIVED: u8 = 1;
pub const PING: u8 = 2;
pub const CONNECT_REQUEST: u8 = 3;
pub const ACCEPTED: u8 = 6;
pub const DISCONNECT: u8 = 9;
pub const RELAY: u8 = 10;
pub const ERROR: u8 = 12;

/// Bytes a `Relay` message adds in front of the Unity Transport packet it carries.
pub const RELAY_HEADER_LEN: usize = 38;

fn header(kind: u8) -> [u8; 4] {
    [SIGNATURE[0], SIGNATURE[1], VERSION, kind]
}

/// What a client needs to talk to the relay: from an allocation (host) or a join (player).
#[derive(Clone)]
pub struct RelayServerData {
    pub endpoint: std::net::SocketAddr,
    pub allocation_id: AllocationId,
    pub connection_data: ConnectionData,
    /// The host's connection data; for the host itself it equals `connection_data`.
    pub host_connection_data: ConnectionData,
    pub key: HmacKey,
}

impl RelayServerData {
    /// `Bind` (295 bytes): accept mode 0, nonce 0, our connection data and its HMAC.
    pub fn bind_message(&self) -> Vec<u8> {
        let nonce: u16 = 0;
        let mut m = Vec::with_capacity(295);
        m.extend_from_slice(&header(BIND));
        m.push(0); // accept mode
        m.extend_from_slice(&nonce.to_le_bytes());
        m.push(255);
        m.extend_from_slice(&self.connection_data);
        m.extend_from_slice(&bind_hmac(&self.key, nonce, &self.connection_data));
        m
    }

    /// `Ping` (22 bytes): keeps our allocation alive. Unity always sends sequence number 1.
    pub fn ping_message(&self) -> Vec<u8> {
        let mut m = Vec::with_capacity(22);
        m.extend_from_slice(&header(PING));
        m.extend_from_slice(&self.allocation_id);
        m.extend_from_slice(&1u16.to_le_bytes());
        m
    }

    /// `ConnectRequest` (276 bytes): a player asks the relay to connect it to the host.
    pub fn connect_request_message(&self) -> Vec<u8> {
        let mut m = Vec::with_capacity(276);
        m.extend_from_slice(&header(CONNECT_REQUEST));
        m.extend_from_slice(&self.allocation_id);
        m.push(255);
        m.extend_from_slice(&self.host_connection_data);
        m
    }

    /// `Disconnect` (36 bytes): tell the relay we're done with `to`.
    pub fn disconnect_message(&self, to: &AllocationId) -> Vec<u8> {
        let mut m = Vec::with_capacity(36);
        m.extend_from_slice(&header(DISCONNECT));
        m.extend_from_slice(&self.allocation_id);
        m.extend_from_slice(to);
        m
    }

    /// `Relay`: forward a Unity Transport packet to `to`.
    pub fn relay_message(&self, to: &AllocationId, payload: &[u8]) -> Vec<u8> {
        let len = u16::try_from(payload.len()).unwrap_or(u16::MAX);
        let mut m = Vec::with_capacity(RELAY_HEADER_LEN + payload.len());
        m.extend_from_slice(&header(RELAY));
        m.extend_from_slice(&self.allocation_id);
        m.extend_from_slice(to);
        m.extend_from_slice(&len.to_be_bytes());
        m.extend_from_slice(payload);
        m
    }
}

/// HMAC-SHA256 over the first 8 bytes of the bind message followed by the connection data.
pub fn bind_hmac(key: &HmacKey, nonce: u16, connection_data: &ConnectionData) -> [u8; 32] {
    let mut mac = Hmac::<Sha256>::new_from_slice(key).expect("HMAC takes any key length");
    let n = nonce.to_le_bytes();
    mac.update(&[
        SIGNATURE[0],
        SIGNATURE[1],
        VERSION,
        BIND,
        0,
        n[0],
        n[1],
        255,
    ]);
    mac.update(connection_data);
    mac.finalize().into_bytes().into()
}

/// A message received from the relay.
#[derive(Debug, PartialEq, Eq)]
pub enum Incoming<'a> {
    BindReceived,
    Accepted {
        from: AllocationId,
        to: AllocationId,
    },
    Relay {
        from: AllocationId,
        to: AllocationId,
        payload: &'a [u8],
    },
    Error {
        allocation: AllocationId,
        code: u8,
    },
    Disconnect {
        from: AllocationId,
        to: AllocationId,
    },
    Other(u8),
}

fn id(bytes: &[u8]) -> AllocationId {
    bytes.try_into().expect("caller checked the length")
}

/// Parse a datagram from the relay. `None` for garbage (bad header or wrong length).
pub fn parse(data: &[u8]) -> Option<Incoming<'_>> {
    if data.len() < 4 || data[0..2] != SIGNATURE || data[2] != VERSION {
        return None;
    }
    Some(match data[3] {
        BIND_RECEIVED if data.len() == 4 => Incoming::BindReceived,
        ACCEPTED if data.len() == 36 => Incoming::Accepted {
            from: id(&data[4..20]),
            to: id(&data[20..36]),
        },
        DISCONNECT if data.len() == 36 => Incoming::Disconnect {
            from: id(&data[4..20]),
            to: id(&data[20..36]),
        },
        RELAY if data.len() >= RELAY_HEADER_LEN => {
            let len = usize::from(u16::from_be_bytes([data[36], data[37]]));
            if data.len() != RELAY_HEADER_LEN + len {
                return None;
            }
            Incoming::Relay {
                from: id(&data[4..20]),
                to: id(&data[20..36]),
                payload: &data[RELAY_HEADER_LEN..],
            }
        }
        ERROR if data.len() == 21 => Incoming::Error {
            allocation: id(&data[4..20]),
            code: data[20],
        },
        BIND_RECEIVED | ACCEPTED | DISCONNECT | RELAY | ERROR => return None,
        other => Incoming::Other(other),
    })
}

/// Unity's text for a relay error code.
pub fn error_text(code: u8) -> &'static str {
    match code {
        0 => "invalid protocol version",
        1 => "player timed out due to inactivity",
        2 => "unauthorized",
        3 => "allocation ID client mismatch",
        4 => "allocation ID not found",
        5 => "not connected",
        6 => "self-connect not allowed",
        _ => "unknown error",
    }
}

/// Errors after which the allocation is gone and a new one (with a new join code) is needed.
pub fn error_invalidates_allocation(code: u8) -> bool {
    matches!(code, 1 | 4)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn data() -> RelayServerData {
        let mut connection_data = [0u8; 255];
        connection_data[..4].copy_from_slice(&[1, 2, 3, 4]);
        RelayServerData {
            endpoint: "127.0.0.1:1".parse().unwrap(),
            allocation_id: [7; 16],
            connection_data,
            host_connection_data: [9; 255],
            key: [5; 64],
        }
    }

    #[test]
    fn message_sizes_match_unity() {
        let d = data();
        assert_eq!(d.bind_message().len(), 295);
        assert_eq!(d.ping_message().len(), 22);
        assert_eq!(d.connect_request_message().len(), 276);
        assert_eq!(d.disconnect_message(&[1; 16]).len(), 36);
        assert_eq!(d.relay_message(&[1; 16], &[0; 10]).len(), 48);
    }

    #[test]
    fn bind_layout_and_hmac() {
        let d = data();
        let m = d.bind_message();
        assert_eq!(&m[..8], &[0xDA, 0x72, 0, 0, 0, 0, 0, 255]);
        assert_eq!(&m[8..263], &d.connection_data);
        // The HMAC covers exactly the bytes in front of it.
        let mut mac = Hmac::<Sha256>::new_from_slice(&d.key).unwrap();
        mac.update(&m[..263]);
        let expect: [u8; 32] = mac.finalize().into_bytes().into();
        assert_eq!(&m[263..], &expect);
    }

    #[test]
    fn relay_round_trip() {
        let d = data();
        let m = d.relay_message(&[3; 16], b"hello");
        assert_eq!(&m[36..38], &[0, 5], "length is big-endian");
        assert_eq!(
            parse(&m),
            Some(Incoming::Relay {
                from: [7; 16],
                to: [3; 16],
                payload: b"hello"
            })
        );
        let mut bad = m.clone();
        bad.push(0);
        assert_eq!(parse(&bad), None);
    }

    #[test]
    fn parses_control_messages() {
        assert_eq!(parse(&[0xDA, 0x72, 0, 1]), Some(Incoming::BindReceived));
        let mut e = vec![0xDA, 0x72, 0, 12];
        e.extend_from_slice(&[4; 16]);
        e.push(4);
        assert_eq!(
            parse(&e),
            Some(Incoming::Error {
                allocation: [4; 16],
                code: 4
            })
        );
        assert_eq!(parse(&[0xDA, 0x72, 1, 1]), None, "wrong version");
    }
}
