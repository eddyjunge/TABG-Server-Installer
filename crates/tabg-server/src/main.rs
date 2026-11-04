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
    /// Lobby spawn point as x,y,z (repeatable). The default is the lobby spawn Landfall's server
    /// used on the default map in our reference run; real spawn lists will come from the map data
    /// tabg extracts from your own game files.
    #[arg(long = "spawn", value_parser = parse_vec3, allow_hyphen_values = true, default_value = "-35.7,127.3,-624.4")]
    spawns: Vec<[f32; 3]>,
}

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
    if !args.spawns.is_empty() {
        settings.spawn_points = args
            .spawns
            .into_iter()
            .map(|position| SpawnPoint {
                position,
                rotation: 0.0,
            })
            .collect();
    }
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
