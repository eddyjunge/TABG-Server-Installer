//! A small, safe ENet host for TABG.
//!
//! One [`Host`] is either a server (bound to a port) or a client (unbound, connects out). It is
//! driven by calling [`Host::service`] from a single thread, like the official server does every
//! frame. Peers are identified by ENet's peer id, which TABG also uses as the player index.

use std::collections::HashMap;
use std::ffi::{CString, c_char};
use std::sync::Once;
use std::time::Duration;

use tabg_enet_sys as sys;
use thiserror::Error;

#[derive(Debug, Error)]
pub enum NetError {
    #[error("ENet failed to initialize")]
    Init,
    #[error("could not resolve address {0:?}")]
    Resolve(String),
    #[error("could not create ENet host (port in use?)")]
    CreateHost,
    #[error("could not start connecting to {0}")]
    Connect(String),
    #[error("unknown peer {0}")]
    UnknownPeer(PeerId),
    #[error("ENet refused to send to peer {0}")]
    Send(PeerId),
}

/// ENet peer id. TABG uses it directly as the player index.
pub type PeerId = u32;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum NetEvent {
    Connected {
        peer: PeerId,
        address: String,
    },
    Disconnected {
        peer: PeerId,
    },
    TimedOut {
        peer: PeerId,
    },
    Received {
        peer: PeerId,
        channel: u8,
        data: Vec<u8>,
    },
}

impl NetEvent {
    pub fn peer(&self) -> PeerId {
        match self {
            Self::Connected { peer, .. }
            | Self::Disconnected { peer }
            | Self::TimedOut { peer }
            | Self::Received { peer, .. } => *peer,
        }
    }
}

fn init() -> Result<(), NetError> {
    static INIT: Once = Once::new();
    static mut RESULT: i32 = -1;
    // SAFETY: RESULT is only written inside call_once, which synchronizes with later reads.
    unsafe {
        INIT.call_once(|| RESULT = sys::enet_initialize());
        if RESULT == 0 {
            Ok(())
        } else {
            Err(NetError::Init)
        }
    }
}

fn resolve(host: &str, port: u16) -> Result<sys::ENetAddress, NetError> {
    let c = CString::new(host).map_err(|_| NetError::Resolve(host.into()))?;
    let mut addr = sys::ENetAddress::default();
    // SAFETY: addr is a valid, writable ENetAddress; c is NUL-terminated.
    if unsafe { sys::enet_address_set_hostname(&mut addr, c.as_ptr()) } != 0 {
        return Err(NetError::Resolve(host.into()));
    }
    addr.port = port;
    Ok(addr)
}

/// An ENet host. Not thread-safe; use it from one thread (it may be moved between threads).
pub struct Host {
    raw: *mut sys::ENetHost,
    peers: HashMap<PeerId, *mut sys::ENetPeer>,
}

// SAFETY: ENet hosts have no thread affinity; we never share the pointer, only move the owner.
unsafe impl Send for Host {}

impl Host {
    /// A server bound to `bind_ip:port` accepting `max_peers` peers. Same arguments as TABG's
    /// `Host.Create(address, MaxPlayers + 1)`: default channels, no bandwidth limits.
    pub fn server(bind_ip: &str, port: u16, max_peers: usize) -> Result<Self, NetError> {
        init()?;
        let addr = resolve(bind_ip, port)?;
        // SAFETY: addr outlives the call; ENet copies it.
        let raw = unsafe { sys::enet_host_create(&addr, max_peers, 0, 0, 0, 0) };
        if raw.is_null() {
            return Err(NetError::CreateHost);
        }
        Ok(Self {
            raw,
            peers: HashMap::new(),
        })
    }

    /// An unbound client host with one peer slot, like TABG's `Host.Create()`.
    pub fn client() -> Result<Self, NetError> {
        init()?;
        // SAFETY: a null address asks ENet for an ephemeral client socket.
        let raw = unsafe { sys::enet_host_create(std::ptr::null(), 1, 0, 0, 0, 0) };
        if raw.is_null() {
            return Err(NetError::CreateHost);
        }
        Ok(Self {
            raw,
            peers: HashMap::new(),
        })
    }

    /// Start connecting; completion arrives as [`NetEvent::Connected`].
    pub fn connect(&mut self, host: &str, port: u16) -> Result<PeerId, NetError> {
        let addr = resolve(host, port)?;
        // SAFETY: self.raw is a live host; addr outlives the call.
        let peer = unsafe { sys::enet_host_connect(self.raw, &addr, 0, 0) };
        if peer.is_null() {
            return Err(NetError::Connect(format!("{host}:{port}")));
        }
        // SAFETY: peer was just returned by ENet and belongs to self.raw.
        let id = unsafe { sys::enet_peer_get_id(peer) };
        self.peers.insert(id, peer);
        Ok(id)
    }

    /// Wait up to `timeout` for one event. Returns `None` when nothing happened.
    pub fn service(&mut self, timeout: Duration) -> Option<NetEvent> {
        let mut ev = sys::ENetEvent::default();
        let ms = u32::try_from(timeout.as_millis()).unwrap_or(u32::MAX);
        // SAFETY: self.raw is live and ev is a valid out-parameter.
        if unsafe { sys::enet_host_service(self.raw, &mut ev, ms) } <= 0 {
            return None;
        }
        // SAFETY: for every non-NONE event ENet sets a valid peer.
        let id = unsafe { sys::enet_peer_get_id(ev.peer) };
        Some(match ev.kind {
            sys::ENET_EVENT_TYPE_CONNECT => {
                self.peers.insert(id, ev.peer);
                NetEvent::Connected {
                    peer: id,
                    address: peer_address(ev.peer),
                }
            }
            sys::ENET_EVENT_TYPE_RECEIVE => {
                // SAFETY: RECEIVE events carry a packet we own and must destroy.
                let data = unsafe {
                    let len = usize::try_from(sys::enet_packet_get_length(ev.packet)).unwrap_or(0);
                    let ptr = sys::enet_packet_get_data(ev.packet) as *const u8;
                    let data = if len == 0 {
                        Vec::new()
                    } else {
                        std::slice::from_raw_parts(ptr, len).to_vec()
                    };
                    sys::enet_packet_destroy(ev.packet);
                    data
                };
                NetEvent::Received {
                    peer: id,
                    channel: ev.channel_id,
                    data,
                }
            }
            sys::ENET_EVENT_TYPE_DISCONNECT_TIMEOUT => {
                self.peers.remove(&id);
                NetEvent::TimedOut { peer: id }
            }
            _ => {
                self.peers.remove(&id);
                NetEvent::Disconnected { peer: id }
            }
        })
    }

    /// Send one packet on channel 0, as TABG does for everything.
    pub fn send(&mut self, peer: PeerId, data: &[u8], reliable: bool) -> Result<(), NetError> {
        let raw_peer = *self.peers.get(&peer).ok_or(NetError::UnknownPeer(peer))?;
        let flags = if reliable {
            sys::ENET_PACKET_FLAG_RELIABLE
        } else {
            sys::ENET_PACKET_FLAG_NONE
        };
        // SAFETY: ENet copies `data` into a new packet; on success the peer owns it, on failure we free it.
        unsafe {
            let packet = sys::enet_packet_create(data.as_ptr().cast(), data.len(), flags);
            if sys::enet_peer_send(raw_peer, 0, packet) != 0 {
                sys::enet_packet_destroy(packet);
                return Err(NetError::Send(peer));
            }
        }
        Ok(())
    }

    pub fn connected_peers(&self) -> impl Iterator<Item = PeerId> + '_ {
        self.peers.keys().copied()
    }

    /// Ask a peer to disconnect gracefully (a [`NetEvent::Disconnected`] follows).
    pub fn disconnect(&mut self, peer: PeerId) {
        if let Some(&raw_peer) = self.peers.get(&peer) {
            // SAFETY: raw_peer belongs to this live host.
            unsafe { sys::enet_peer_disconnect(raw_peer, 0) };
        }
    }

    pub fn rtt_ms(&self, peer: PeerId) -> Option<u32> {
        // SAFETY: the peer pointer belongs to this live host.
        self.peers
            .get(&peer)
            .map(|&p| unsafe { sys::enet_peer_get_rtt(p) })
    }

    /// Send everything queued now instead of on the next service call.
    pub fn flush(&mut self) {
        // SAFETY: self.raw is live.
        unsafe { sys::enet_host_flush(self.raw) };
    }
}

impl Drop for Host {
    fn drop(&mut self) {
        // SAFETY: self.raw was created by enet_host_create and is destroyed exactly once.
        unsafe { sys::enet_host_destroy(self.raw) };
    }
}

fn peer_address(peer: *mut sys::ENetPeer) -> String {
    let mut buf = [0 as c_char; 64];
    // SAFETY: buf is large enough for any textual IPv6 address and ENet NUL-terminates it.
    unsafe {
        if sys::enet_peer_get_ip(peer, buf.as_mut_ptr(), buf.len()) != 0 {
            return String::from("?");
        }
        let ip = std::ffi::CStr::from_ptr(buf.as_ptr())
            .to_string_lossy()
            .into_owned();
        format!("{ip}:{}", sys::enet_peer_get_port(peer))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Instant;

    #[test]
    fn server_and_client_talk_through_the_safe_api() {
        let mut server = Host::server("127.0.0.1", 47_778, 4).unwrap();
        let mut client = Host::client().unwrap();
        let to_server = client.connect("127.0.0.1", 47_778).unwrap();

        let deadline = Instant::now() + Duration::from_secs(5);
        let mut sent = false;
        let mut got = None;
        while got.is_none() && Instant::now() < deadline {
            while let Some(ev) = client.service(Duration::ZERO) {
                if matches!(ev, NetEvent::Connected { .. }) {
                    client.send(to_server, &[202, 1, 2], true).unwrap();
                    sent = true;
                }
            }
            if let Some(NetEvent::Received { data, channel, .. }) =
                server.service(Duration::from_millis(10))
            {
                got = Some((channel, data));
            }
        }
        assert!(sent);
        assert_eq!(got, Some((0, vec![202, 1, 2])));
    }
}
