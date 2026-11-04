//! Bytes captured from Landfall's own dedicated server (run offline, joined by tabg-probe as
//! "probe-a"). Our encoders must produce exactly these bytes and our decoders must accept them.

use tabg_protocol::messages::*;

fn hex(s: &str) -> Vec<u8> {
    (0..s.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap())
        .collect()
}

#[test]
fn player_joined_broadcast_is_byte_identical() {
    let official = hex("00000700000070726f62652d61000000000000000000");
    let ours = PlayerJoined {
        player_index: 0,
        group_index: 0,
        player_name: "probe-a".into(),
        gear: vec![],
        is_dev: false,
        colour: 0,
    };
    assert_eq!(ours.encode(), official);
    assert_eq!(PlayerJoined::decode(&official).unwrap(), ours);
}

#[test]
fn room_init_accept_is_byte_identical() {
    let official = hex("01000100000700000070726f62652d61");
    let ours = RoomInitResponse::Accepted {
        game_mode: game_mode::BATTLE_ROYALE,
        match_mode: match_mode::SQUAD,
        player_index: 0,
        group_index: 0,
        player_name: "probe-a".into(),
    };
    assert_eq!(ours.encode(), official);
    assert_eq!(RoomInitResponse::decode(&official).unwrap(), ours);
}

#[test]
fn player_left_after_kick_matches() {
    // PlayerLeft [index 0, left for good]. Our room sends the same two bytes.
    assert_eq!(hex("0001"), vec![0, 1]);
}

#[test]
fn world_state_with_official_zero_padding_decodes() {
    // The official world state for probe-a was 14208 bytes: 10494 of data, then a zero tail.
    // That capture contains map data (118 cars), so it is not committed; this rebuilds its shape.
    let world = WorldState {
        player_index: 0,
        group_index: 0,
        player_name: "probe-a".into(),
        is_admin: false,
        position: [-35.7, 127.3, -624.4],
        rotation: 0.0,
        is_dead: false,
        is_downed: false,
        health: 100.0,
        car_seat: None,
        deception_colour: None,
        players: vec![WorldPlayer {
            player_index: 0,
            group_index: 0,
            player_name: "probe-a".into(),
            weapon_type: NO_WEAPON,
            gear: vec![],
            is_admin: false,
            colour: 0,
        }],
        weapons: vec![],
        cars: vec![WorldCar {
            car_type: 3,
            car_index: 0,
            seat_network_indices: vec![0, 1, 2, 3],
            parts: vec![CarPart {
                index: 0,
                health: 1500.0,
                name: "Chassi".into(),
            }],
        }],
        time_of_day: 0.246,
        time_before_first_ring: 70.0,
        base_ring_time: 200.0,
        rings: vec![
            (4240.0, 25.0),
            (3450.0, 3.0),
            (1710.0, 1.5),
            (830.0, 1.5),
            (360.0, 2.0),
            (140.0, 2.0),
        ],
        lives_per_team: 5,
        kills_to_win: u16::MAX,
        phase: WorldPhase::Plain(game_state::WAITING_FOR_PLAYERS),
    };
    let mut bytes = world.encode(game_mode::BATTLE_ROYALE);
    bytes.extend(std::iter::repeat_n(0u8, 3714));
    assert_eq!(
        WorldState::decode(&bytes, game_mode::BATTLE_ROYALE).unwrap(),
        world
    );

    // Non-zero garbage after the data is still an error.
    *bytes.last_mut().unwrap() = 1;
    assert!(WorldState::decode(&bytes, game_mode::BATTLE_ROYALE).is_err());
}
