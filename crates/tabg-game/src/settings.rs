use tabg_protocol::messages::{game_mode, match_mode};

use crate::cars::CarSpawn;
use crate::loot::LootRules;
use crate::ring::RingRules;

/// A place where joining players appear, with their facing.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct SpawnPoint {
    pub position: [f32; 3],
    pub rotation: f32,
}

/// Start the countdown anyway after `after_seconds` if at least `min_players` are in.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ForceStart {
    pub after_seconds: f32,
    pub min_players: u8,
}

/// Respawning instead of staying dead (deathmatch, gun game).
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Respawn {
    /// Seconds between death and respawn.
    pub delay: f32,
}

/// Room configuration. Defaults follow TABG's stock `game_settings.txt`.
#[derive(Clone, Debug, PartialEq)]
pub struct RoomSettings {
    pub game_mode: u8,
    pub match_mode: u8,
    pub max_players: u8,
    /// Empty means no password.
    pub password: String,
    /// Lobby spawn points (from the map data extracted from the host's game files).
    pub spawn_points: Vec<SpawnPoint>,
    /// The countdown starts once this many players are in.
    pub players_to_start: u8,
    /// Countdown length in seconds.
    pub countdown: f32,
    pub force_start: Option<ForceStart>,
    /// Time of day for lighting, 0..1.
    pub time_of_day: f32,
    /// Sent to clients; the official server doesn't use it for timing.
    pub time_before_first_ring: f32,
    pub ring: RingRules,
    /// Loot spots and match modifiers from the map data (empty: no loot).
    pub loot: LootRules,
    /// Share of loot spots used per match (`StripLootByPercentage`; the official default is 0.1).
    pub loot_keep: f32,
    /// Cars and where they stand at the start (from the map data).
    pub cars: Vec<CarSpawn>,
    /// Health restored per healing item id (from the map data).
    pub heal_amounts: std::collections::BTreeMap<i32, f32>,
    /// Item ids that are grenades (thrown ones don't become loot).
    pub grenades: std::collections::BTreeSet<i32>,
    /// Player names (case-insensitive) or player ids with admin rights; `*` makes everyone an
    /// admin.
    pub admins: Vec<String>,
    /// Player names (case-insensitive) or ids that may not join.
    pub banned: Vec<String>,
    /// Items `(id, quantity)` every player gets straight into their inventory on landing.
    pub loadout: Vec<(i32, i32)>,
    pub lives_per_team: i32,
    /// With respawns: the first team to this many kills wins (0 = no limit).
    pub kills_to_win: u16,
    /// Dead players come back (at a random loot spot inside the ring) instead of staying dead.
    pub respawn: Option<Respawn>,
    /// With respawns: the match ends after this many seconds (0 = no limit); most kills wins.
    pub time_limit: f32,
    /// Gun game: each kill moves the killer to the next level's items; finishing the last level
    /// wins. Empty = off.
    pub gun_game: Vec<Vec<(i32, i32)>>,
    /// Movement updates per second (the official server uses 30).
    pub tick_rate: u32,
    /// Seed for ring positions and the plane path.
    pub seed: u64,
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
            players_to_start: 2,
            countdown: 20.0,
            force_start: Some(ForceStart {
                after_seconds: 200.0,
                min_players: 2,
            }),
            time_of_day: 0.25,
            time_before_first_ring: 70.0,
            ring: RingRules::default(),
            loot: LootRules::default(),
            loot_keep: 0.1,
            cars: Vec::new(),
            heal_amounts: Default::default(),
            grenades: Default::default(),
            admins: Vec::new(),
            banned: Vec::new(),
            loadout: Vec::new(),
            lives_per_team: 256,
            kills_to_win: 20,
            respawn: None,
            time_limit: 0.0,
            gun_game: Vec::new(),
            tick_rate: 30,
            seed: 0x7ab6,
        }
    }
}
