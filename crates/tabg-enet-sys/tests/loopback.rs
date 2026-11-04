//! Builds and links the vendored C, then round-trips one reliable packet over localhost.

use std::ffi::CString;
use std::time::{Duration, Instant};
use tabg_enet_sys::*;

#[test]
fn reports_vendored_version() {
    assert_eq!(unsafe { enet_linked_version() }, VENDORED_VERSION);
}

#[test]
fn client_and_server_exchange_a_reliable_packet() {
    unsafe {
        assert_eq!(enet_initialize(), 0);

        let ip = CString::new("127.0.0.1").unwrap();
        let mut bind = ENetAddress::default();
        assert_eq!(enet_address_set_ip(&mut bind, ip.as_ptr()), 0);
        bind.port = 47_777;

        // Same arguments TABG's Host.Create(address, peerLimit) / Host.Create() / Connect(address) pass.
        let server = enet_host_create(&bind, 4, 0, 0, 0, 0);
        assert!(!server.is_null(), "server host");
        let client = enet_host_create(std::ptr::null(), 1, 0, 0, 0, 0);
        assert!(!client.is_null(), "client host");
        let peer = enet_host_connect(client, &bind, 0, 0);
        assert!(!peer.is_null(), "connect");

        let payload = [7u8, 1, 2, 3];
        let mut sent = false;
        let mut received = None;
        let deadline = Instant::now() + Duration::from_secs(5);
        while received.is_none() && Instant::now() < deadline {
            let mut ev = ENetEvent::default();
            while enet_host_service(client, &mut ev, 0) > 0 {
                if ev.kind == ENET_EVENT_TYPE_CONNECT && !sent {
                    let p = enet_packet_create(
                        payload.as_ptr().cast(),
                        payload.len(),
                        ENET_PACKET_FLAG_RELIABLE,
                    );
                    assert_eq!(enet_peer_send(peer, 0, p), 0);
                    sent = true;
                }
            }
            while enet_host_service(server, &mut ev, 10) > 0 {
                if ev.kind == ENET_EVENT_TYPE_RECEIVE {
                    let len = enet_packet_get_length(ev.packet) as usize;
                    let data = std::slice::from_raw_parts(
                        enet_packet_get_data(ev.packet) as *const u8,
                        len,
                    );
                    received = Some((ev.channel_id, data.to_vec()));
                    enet_packet_destroy(ev.packet);
                }
            }
        }

        enet_host_destroy(client);
        enet_host_destroy(server);
        enet_deinitialize();

        assert_eq!(received, Some((0, payload.to_vec())));
    }
}
