use std::sync::Arc;
use std::sync::atomic::AtomicBool;

use clap::Parser;
use tabg_game::{RoomSettings, SpawnPoint};
use tabg_server::{ServerConfig, run};

/// Run a tabg game server (development entry point; `tabg` will manage this for you).
#[derive(Parser)]
#[command(version)]
struct Args {
    #[arg(long, default_value = "0.0.0.0")]
    bind: String,
    #[arg(long, default_value_t = 7777)]
    port: u16,
    #[arg(long, default_value_t = 70)]
    max_players: u8,
    #[arg(long, default_value = "")]
    password: String,
    /// Map data from `scripts/dump-world.sh`; its lobby spawn points are used.
    #[arg(long)]
    world: Option<std::path::PathBuf>,
    /// Lobby spawn point as x,y,z (repeatable); overrides --world.
    #[arg(long = "spawn", value_parser = parse_vec3, allow_hyphen_values = true)]
    spawns: Vec<[f32; 3]>,
}

/// Used when neither --world nor --spawn is given: one lobby spawn of the default map, as seen from
/// Landfall's server in our reference run.
const FALLBACK_SPAWN: [f32; 3] = [-35.7, 127.3, -624.4];

fn parse_vec3(s: &str) -> Result<[f32; 3], String> {
    let parts: Vec<f32> = s
        .split(',')
        .map(|p| p.trim().parse::<f32>().map_err(|e| e.to_string()))
        .collect::<Result<_, _>>()?;
    parts.try_into().map_err(|_| "expected x,y,z".to_string())
}

fn main() {
    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env().unwrap_or_else(|_| "info".into()),
        )
        .init();
    let args = Args::parse();
    let mut settings = RoomSettings {
        max_players: args.max_players,
        password: args.password,
        ..Default::default()
    };
    settings.spawn_points = if !args.spawns.is_empty() {
        args.spawns
            .into_iter()
            .map(|position| SpawnPoint {
                position,
                rotation: 0.0,
            })
            .collect()
    } else if let Some(path) = &args.world {
        let world = match tabg_world::World::load(path) {
            Ok(w) => w,
            Err(e) => {
                eprintln!("error: {e}");
                std::process::exit(1);
            }
        };
        let lobby: Vec<_> = world
            .spawn_group(tabg_world::LOBBY_GROUP)
            .map(|p| SpawnPoint {
                position: p.position,
                rotation: p.rotation,
            })
            .collect();
        if lobby.is_empty() {
            eprintln!("error: {} has no lobby spawn points", path.display());
            std::process::exit(1);
        }
        tracing::info!(count = lobby.len(), scenes = ?world.scenes, "loaded lobby spawn points");
        lobby
    } else {
        vec![SpawnPoint {
            position: FALLBACK_SPAWN,
            rotation: 0.0,
        }]
    };
    let stop = Arc::new(AtomicBool::new(false));
    if let Err(e) = run(
        ServerConfig {
            bind: args.bind,
            port: args.port,
            settings,
        },
        stop,
    ) {
        eprintln!("error: {e}");
        std::process::exit(1);
    }
}
