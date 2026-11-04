//! Map data exported from the host's own TABG install by `mods/src/WorldDumper`
//! (`scripts/dump-world.sh`). Never shipped with tabg; always generated locally.

use std::path::Path;

use serde::Deserialize;
use thiserror::Error;

/// The dumper's output format this crate understands.
pub const FORMAT: u32 = 1;

/// Spawn group TABG uses for the lobby / initial spawns.
pub const LOBBY_GROUP: i32 = 0;

#[derive(Debug, Error)]
pub enum WorldError {
    #[error("could not read {path}: {source}")]
    Io {
        path: String,
        source: std::io::Error,
    },
    #[error("invalid world file: {0}")]
    Json(#[from] serde_json::Error),
    #[error("world file format {0} is not supported (expected {FORMAT}); re-run the dumper")]
    Format(u32),
}

#[derive(Clone, Debug, Deserialize, PartialEq)]
pub struct SpawnPoint {
    pub group: i32,
    pub position: [f32; 3],
    /// Yaw in degrees.
    pub rotation: f32,
}

/// The map's ring prefab settings.
#[derive(Clone, Debug, Deserialize, PartialEq)]
pub struct RingData {
    /// Pause after ring `i` finishes shrinking (the server adds 20 s, and 40 s more after ring 1).
    pub ring_times: Vec<f32>,
    /// Size of the blue ring before the first shrink.
    pub start_blue_size: f32,
    pub base_time_to_travel: f32,
    /// Preferred ring centres for late rings (empty on maps without hot spots).
    #[serde(default)]
    pub hot_spots: Vec<[f32; 3]>,
}

/// A place where loot can spawn.
#[derive(Clone, Debug, Deserialize, PartialEq)]
pub struct LootSpot {
    pub position: [f32; 3],
    /// Always spawns, regardless of the strip percentage.
    #[serde(default)]
    pub force_spawn: bool,
    #[serde(default = "yes")]
    pub has_preset: bool,
}

fn yes() -> bool {
    true
}

/// One weighted entry of a loot preset: these items, spawned together.
#[derive(Clone, Debug, Deserialize, PartialEq)]
pub struct LootDrop {
    pub spawn_rate: f32,
    /// `[item id, quantity]`.
    pub items: Vec<[i32; 2]>,
}

/// A match modifier ("Normal game", "Medieval", …): its rarity and its loot preset.
#[derive(Clone, Debug, Deserialize, PartialEq)]
pub struct MatchModifier {
    pub index: u8,
    pub title: String,
    /// 0 common, 1 rare, 2 epic, 3 legendary.
    pub rarity: u8,
    pub drops: Vec<LootDrop>,
}

#[derive(Clone, Debug, Deserialize, PartialEq)]
pub struct CarSeat {
    pub network_index: i32,
    pub driver: bool,
}

#[derive(Clone, Debug, Deserialize, PartialEq)]
pub struct CarPart {
    pub index: u8,
    pub health: f32,
    pub name: String,
}

/// A car as the official server spawned it on this map.
#[derive(Clone, Debug, Deserialize, PartialEq)]
pub struct Car {
    pub car_type: i32,
    pub car_index: i32,
    pub position: [f32; 3],
    /// Quaternion `[x, y, z, w]`.
    pub rotation: [f32; 4],
    #[serde(default)]
    pub health: f32,
    pub seats: Vec<CarSeat>,
    pub parts: Vec<CarPart>,
}

#[derive(Clone, Debug, Deserialize, PartialEq)]
pub struct World {
    pub format: u32,
    pub unity_version: String,
    pub scenes: Vec<String>,
    pub spawn_points: Vec<SpawnPoint>,
    /// Missing in dumps made before the dumper exported it.
    #[serde(default)]
    pub ring: Option<RingData>,
    #[serde(default)]
    pub loot_spots: Vec<LootSpot>,
    #[serde(default)]
    pub match_modifiers: Vec<MatchModifier>,
    /// Item id (as a string key) → name.
    #[serde(default)]
    pub items: std::collections::BTreeMap<String, String>,
    #[serde(default)]
    pub cars: Vec<Car>,
    /// Item id (string key) → health restored by using it.
    #[serde(default)]
    pub heal_amounts: std::collections::BTreeMap<String, f32>,
    /// Item ids that are grenades (thrown, not left as loot).
    #[serde(default)]
    pub grenades: Vec<i32>,
}

impl World {
    pub fn parse(json: &str) -> Result<Self, WorldError> {
        let world: World = serde_json::from_str(json)?;
        if world.format != FORMAT {
            return Err(WorldError::Format(world.format));
        }
        Ok(world)
    }

    pub fn load(path: &Path) -> Result<Self, WorldError> {
        let json = std::fs::read_to_string(path).map_err(|source| WorldError::Io {
            path: path.display().to_string(),
            source,
        })?;
        Self::parse(&json)
    }

    pub fn spawn_group(&self, group: i32) -> impl Iterator<Item = &SpawnPoint> {
        self.spawn_points.iter().filter(move |p| p.group == group)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const SAMPLE: &str = r#"{
  "format": 1,
  "unity_version": "2021.3.22f1",
  "scenes": ["ServerTerrain"],
  "spawn_points": [
    {"group": 0, "position": [1.5, 2, -3], "rotation": 90},
    {"group": 3, "position": [0, 0, 0], "rotation": 0}
  ],
  "ring": {"ring_times": [40, 40, 30], "start_blue_size": 9000, "base_time_to_travel": 200, "hot_spots": []}
}"#;

    #[test]
    fn parses_dumper_output_and_filters_groups() {
        let world = World::parse(SAMPLE).unwrap();
        assert_eq!(world.scenes, ["ServerTerrain"]);
        let lobby: Vec<_> = world.spawn_group(LOBBY_GROUP).collect();
        assert_eq!(
            lobby,
            [&SpawnPoint {
                group: 0,
                position: [1.5, 2.0, -3.0],
                rotation: 90.0
            }]
        );
        assert_eq!(world.ring.unwrap().ring_times, [40.0, 40.0, 30.0]);
    }

    #[test]
    fn rejects_unknown_format() {
        assert!(matches!(
            World::parse(&SAMPLE.replace("\"format\": 1", "\"format\": 2")),
            Err(WorldError::Format(2))
        ));
    }
}
