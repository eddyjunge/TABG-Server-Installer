//! Raw FFI bindings to ENet-CSharp 2.4.8 (the ENet fork TABG uses).
//!
//! Only the functions the server needs are declared. Safe wrappers live in `tabg-net`.
#![allow(non_camel_case_types)]

use std::ffi::{c_char, c_int, c_void};

/// Opaque `ENetHost`.
#[repr(C)]
pub struct ENetHost {
    _private: [u8; 0],
}

/// Opaque `ENetPeer`.
#[repr(C)]
pub struct ENetPeer {
    _private: [u8; 0],
}

/// Opaque `ENetPacket`.
#[repr(C)]
pub struct ENetPacket {
    _private: [u8; 0],
}

/// `ENetAddress`: a 16-byte IPv6 (or IPv4-mapped) address followed by the port.
///
/// The C struct is 18 bytes (align 2) on Windows and 20 bytes (align 4) on Unix; the port is at
/// offset 16 on both. We only pass it by pointer, so this 20-byte, 4-aligned layout is valid for
/// both. Fill it with [`enet_address_set_ip`] / [`enet_address_set_hostname`].
#[repr(C, align(4))]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ENetAddress {
    pub ip: [u8; 16],
    pub port: u16,
}

pub type ENetEventType = c_int;
pub const ENET_EVENT_TYPE_NONE: ENetEventType = 0;
pub const ENET_EVENT_TYPE_CONNECT: ENetEventType = 1;
pub const ENET_EVENT_TYPE_DISCONNECT: ENetEventType = 2;
pub const ENET_EVENT_TYPE_RECEIVE: ENetEventType = 3;
pub const ENET_EVENT_TYPE_DISCONNECT_TIMEOUT: ENetEventType = 4;

pub const ENET_PACKET_FLAG_NONE: u32 = 0;
pub const ENET_PACKET_FLAG_RELIABLE: u32 = 1 << 0;
pub const ENET_PACKET_FLAG_UNSEQUENCED: u32 = 1 << 1;

#[repr(C)]
#[derive(Debug)]
pub struct ENetEvent {
    pub kind: ENetEventType,
    pub peer: *mut ENetPeer,
    pub channel_id: u8,
    pub data: u32,
    pub packet: *mut ENetPacket,
}

impl Default for ENetEvent {
    fn default() -> Self {
        Self {
            kind: ENET_EVENT_TYPE_NONE,
            peer: std::ptr::null_mut(),
            channel_id: 0,
            data: 0,
            packet: std::ptr::null_mut(),
        }
    }
}

unsafe extern "C" {
    pub fn enet_initialize() -> c_int;
    pub fn enet_deinitialize();
    pub fn enet_linked_version() -> u32;

    pub fn enet_host_create(
        address: *const ENetAddress,
        peer_limit: usize,
        channel_limit: usize,
        incoming_bandwidth: u32,
        outgoing_bandwidth: u32,
        buffer_size: c_int,
    ) -> *mut ENetHost;
    pub fn enet_host_destroy(host: *mut ENetHost);
    pub fn enet_host_connect(
        host: *mut ENetHost,
        address: *const ENetAddress,
        channel_count: usize,
        data: u32,
    ) -> *mut ENetPeer;
    pub fn enet_host_service(host: *mut ENetHost, event: *mut ENetEvent, timeout_ms: u32) -> c_int;
    pub fn enet_host_flush(host: *mut ENetHost);

    pub fn enet_packet_create(data: *const c_void, length: usize, flags: u32) -> *mut ENetPacket;
    pub fn enet_packet_destroy(packet: *mut ENetPacket);
    pub fn enet_packet_get_data(packet: *const ENetPacket) -> *mut c_void;
    pub fn enet_packet_get_length(packet: *const ENetPacket) -> c_int;

    pub fn enet_peer_send(peer: *mut ENetPeer, channel_id: u8, packet: *mut ENetPacket) -> c_int;
    pub fn enet_peer_disconnect(peer: *mut ENetPeer, data: u32);
    pub fn enet_peer_disconnect_now(peer: *mut ENetPeer, data: u32);
    pub fn enet_peer_get_id(peer: *const ENetPeer) -> u32;
    pub fn enet_peer_get_ip(peer: *const ENetPeer, ip: *mut c_char, length: usize) -> c_int;
    pub fn enet_peer_get_port(peer: *const ENetPeer) -> u16;
    pub fn enet_peer_get_rtt(peer: *const ENetPeer) -> u32;

    pub fn enet_address_set_ip(address: *mut ENetAddress, ip: *const c_char) -> c_int;
    pub fn enet_address_set_hostname(address: *mut ENetAddress, hostname: *const c_char) -> c_int;
}

/// The ENet version this crate vendors, packed like `ENET_VERSION_CREATE`.
pub const VENDORED_VERSION: u32 = (2 << 16) | (4 << 8) | 8;
