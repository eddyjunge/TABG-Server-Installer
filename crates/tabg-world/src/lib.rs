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

#[derive(Clone, Debug, Deserialize, PartialEq)]
pub struct World {
    pub format: u32,
    pub unity_version: String,
    pub scenes: Vec<String>,
    pub spawn_points: Vec<SpawnPoint>,
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
  ]
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
    }

    #[test]
    fn rejects_unknown_format() {
        assert!(matches!(
            World::parse(&SAMPLE.replace("\"format\": 1", "\"format\": 2")),
            Err(WorldError::Format(2))
        ));
    }
}
