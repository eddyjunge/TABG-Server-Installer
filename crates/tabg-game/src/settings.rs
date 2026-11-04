use tabg_protocol::messages::{game_mode, match_mode};

/// A place where joining players appear, with their facing.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct SpawnPoint {
    pub position: [f32; 3],
    pub rotation: f32,
}

/// Room configuration. Defaults follow TABG's stock `game_settings.txt`.
#[derive(Clone, Debug, PartialEq)]
pub struct RoomSettings {
    pub game_mode: u8,
    pub match_mode: u8,
    pub max_players: u8,
    /// Empty means no password.
    pub password: String,
    /// Lobby spawn points. Real ones come from the map data extracted from the host's game files;
    /// until that exists, they are configured by hand.
    pub spawn_points: Vec<SpawnPoint>,
    pub time_of_day: f32,
    pub time_before_first_ring: f32,
    pub base_ring_time: f32,
    pub ring_sizes: Vec<f32>,
    pub ring_speeds: Vec<f32>,
    pub lives_per_team: i32,
    pub kills_to_win: u16,
    /// Movement updates per second (the official server uses 30).
    pub tick_rate: u32,
}

impl Default for RoomSettings {
    fn default() -> Self {
        Self {
            game_mode: game_mode::BATTLE_ROYALE,
            match_mode: match_mode::SQUAD,
            max_players: 70,
            password: String::new(),
            spawn_points: vec![SpawnPoint {
                position: [0.0, 100.0, 0.0],
                rotation: 0.0,
            }],
            time_of_day: 12.0,
            time_before_first_ring: 70.0,
            base_ring_time: 200.0,
            ring_sizes: vec![4240.0, 3450.0, 1710.0, 830.0, 360.0, 140.0],
            ring_speeds: vec![25.0, 3.0, 1.5, 1.5, 2.0, 2.0],
            lives_per_team: 256,
            kills_to_win: 20,
            tick_rate: 30,
        }
    }
}
