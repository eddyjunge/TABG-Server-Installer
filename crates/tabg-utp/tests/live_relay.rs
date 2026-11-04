//! Talks to Unity's real relay. Needs TABG's project id, from either `TABG_UGS_PROJECT` or a game
//! install in `TABG_CLIENT_DIR`; skipped otherwise. Run with `cargo test -p tabg-utp -- --ignored`.

use std::time::{Duration, Instant};

use tabg_net::{NetEvent, Transport};
use tabg_utp::{RelayClient, RelayConfig, RelayHost};

fn project_id() -> Option<String> {
    if let Ok(id) = std::env::var("TABG_UGS_PROJECT") {
        return Some(id);
    }
    let dir = std::env::var("TABG_CLIENT_DIR").ok()?;
    tabg_utp::project_id_from_game(dir.as_ref()).ok()
}

fn pump(
    host: &mut RelayHost,
    client: &mut RelayClient,
    until: impl Fn(&[NetEvent], &[NetEvent]) -> bool,
) -> (Vec<NetEvent>, Vec<NetEvent>) {
    let (mut h, mut c) = (Vec::new(), Vec::new());
    let deadline = Instant::now() + Duration::from_secs(20);
    while Instant::now() < deadline && !until(&h, &c) {
        while let Some(e) = host.service(Duration::from_millis(2)) {
            h.push(e);
        }
        while let Some(e) = client.service(Duration::from_millis(2)) {
            c.push(e);
        }
        host.flush();
        client.flush();
    }
    (h, c)
}

fn received(events: &[NetEvent]) -> Vec<Vec<u8>> {
    events
        .iter()
        .filter_map(|e| match e {
            NetEvent::Received { data, .. } => Some(data.clone()),
            _ => None,
        })
        .collect()
}

#[test]
#[ignore = "talks to Unity's servers"]
fn host_and_join_through_unity_relay() {
    let Some(project_id) = project_id() else {
        eprintln!("no TABG project id; skipping");
        return;
    };
    let mut host = RelayHost::host(RelayConfig {
        project_id: project_id.clone(),
        region: None,
        max_players: 4,
    })
    .expect("host");
    let code = host.join_code();
    eprintln!("join code {code} in {}", host.region());
    let mut client = RelayClient::join(&project_id, &code).expect("join");

    let (h, c) = pump(&mut host, &mut client, |h, c| {
        h.iter().any(|e| matches!(e, NetEvent::Connected { .. }))
            && c.iter().any(|e| matches!(e, NetEvent::Connected { .. }))
    });
    let peer = h
        .iter()
        .find_map(|e| match e {
            NetEvent::Connected { peer, .. } => Some(*peer),
            _ => None,
        })
        .expect("host saw the connection");
    assert!(c.iter().any(|e| matches!(e, NetEvent::Connected { .. })));
    assert_eq!(peer, 0);

    // Both directions, reliable and unreliable, small and fragmented.
    let big: Vec<u8> = (0..15_000u32).map(|i| (i % 251) as u8).collect();
    client.send(0, &[3, 1, 2, 3], true).unwrap();
    client.send(0, &big, true).unwrap();
    host.send(peer, &[9, 9], false).unwrap();
    host.send(peer, &big, true).unwrap();
    let (h, c) = pump(&mut host, &mut client, |h, c| {
        received(h).len() >= 2 && received(c).len() >= 2
    });
    assert_eq!(received(&h), vec![vec![3, 1, 2, 3], big.clone()]);
    assert!(received(&c).contains(&big));

    client.disconnect(0);
    let (h, _) = pump(&mut host, &mut client, |h, _| {
        h.iter().any(|e| matches!(e, NetEvent::Disconnected { .. }))
    });
    assert!(
        h.iter()
            .any(|e| matches!(e, NetEvent::Disconnected { peer: 0 }))
    );
}
