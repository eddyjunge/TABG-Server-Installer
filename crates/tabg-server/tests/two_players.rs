//! End to end over real UDP: two headless clients join our server and see each other move.

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Duration;

use tabg_game::{RoomSettings, SpawnPoint};
use tabg_probe::Probe;
use tabg_server::{ServerConfig, run};

#[test]
fn two_probes_join_and_see_each_other_move() {
    let port = 47_801;
    let spawn = SpawnPoint {
        position: [100.0, 50.0, -20.0],
        rotation: 0.0,
    };
    let stop = Arc::new(AtomicBool::new(false));
    let server = {
        let stop = stop.clone();
        let config = ServerConfig {
            bind: "127.0.0.1".into(),
            port,
            relay: None,
            listing: None,
            upnp: false,
            settings: RoomSettings {
                spawn_points: vec![spawn],
                ..Default::default()
            },
        };
        std::thread::spawn(move || run(config, stop).unwrap())
    };

    let mut a = Probe::named("127.0.0.1", port, "alice").unwrap();
    assert!(
        a.pump_until(Duration::from_secs(5), |s| s.world.is_some())
            .unwrap(),
        "alice joins"
    );
    let mut b = Probe::named("127.0.0.1", port, "bob").unwrap();
    assert!(
        b.pump_until(Duration::from_secs(5), |s| s.world.is_some())
            .unwrap(),
        "bob joins"
    );

    assert_eq!(b.seen.world.as_ref().unwrap().position, spawn.position);
    assert_eq!(
        b.seen.world.as_ref().unwrap().players.len(),
        2,
        "bob's world lists both players"
    );
    a.pump(Duration::from_millis(50)).unwrap();
    assert!(
        a.seen.joined.iter().any(|j| j.player_name == "bob"),
        "alice is told bob joined"
    );

    let alice_index = a.player_index().unwrap();
    let target = [123.0, 50.0, -7.0];
    let saw_alice = (0..100).any(|_| {
        a.move_to(target).unwrap();
        a.pump(Duration::from_millis(10)).unwrap();
        b.pump(Duration::from_millis(10)).unwrap();
        b.seen.others.get(&alice_index) == Some(&target)
    });
    assert!(
        saw_alice,
        "bob sees alice at her new position: {:?}",
        b.seen.others
    );
    assert!(a.seen.decode_errors.is_empty() && b.seen.decode_errors.is_empty());

    stop.store(true, Ordering::Relaxed);
    server.join().unwrap();
}
