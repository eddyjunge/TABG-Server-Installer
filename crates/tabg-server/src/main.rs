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
    /// Map data from `scripts/dump-world.sh`: lobby spawn points and ring timing.
    #[arg(long)]
    world: Option<std::path::PathBuf>,
    /// Lobby spawn point as x,y,z (repeatable); overrides --world's spawn points.
    #[arg(long = "spawn", value_parser = parse_vec3, allow_hyphen_values = true)]
    spawns: Vec<[f32; 3]>,
    /// Players needed before the countdown starts.
    #[arg(long, default_value_t = 2)]
    players_to_start: u8,
    /// Countdown length in seconds.
    #[arg(long, default_value_t = 20.0)]
    countdown: f32,
    /// Player name with admin rights (repeatable; `*` = everyone). Admins can force-start and
    /// use chat commands: /start, /give <item> [qty], /heal, /kick <name>.
    #[arg(long = "admin")]
    admins: Vec<String>,
    /// Seed for ring positions and the plane path (random by default).
    #[arg(long)]
    seed: Option<u64>,
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

fn fail(msg: impl std::fmt::Display) -> ! {
    eprintln!("error: {msg}");
    std::process::exit(1);
}

fn main() {
    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env().unwrap_or_else(|_| "info".into()),
        )
        .init();
    let args = Args::parse();
    let seed = args.seed.unwrap_or_else(|| {
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(1, |d| d.as_nanos() as u64)
    });
    let mut settings = RoomSettings {
        max_players: args.max_players,
        password: args.password,
        players_to_start: args.players_to_start,
        countdown: args.countdown,
        seed,
        admins: args.admins.clone(),
        spawn_points: vec![SpawnPoint {
            position: FALLBACK_SPAWN,
            rotation: 0.0,
        }],
        ..Default::default()
    };

    if let Some(path) = &args.world {
        let world = tabg_world::World::load(path).unwrap_or_else(|e| fail(e));
        let lobby: Vec<_> = world
            .spawn_group(tabg_world::LOBBY_GROUP)
            .map(|p| SpawnPoint {
                position: p.position,
                rotation: p.rotation,
            })
            .collect();
        if lobby.is_empty() {
            fail(format!("{} has no lobby spawn points", path.display()));
        }
        tracing::info!(count = lobby.len(), scenes = ?world.scenes, "loaded lobby spawn points");
        settings.spawn_points = lobby;
        settings.loot = tabg_game::loot::LootRules {
            spots: world
                .loot_spots
                .iter()
                .filter(|s| s.has_preset)
                .map(|s| tabg_game::loot::Spot {
                    position: s.position,
                    force: s.force_spawn,
                })
                .collect(),
            modifiers: world
                .match_modifiers
                .iter()
                .map(|m| tabg_game::loot::Modifier {
                    index: m.index,
                    rarity: m.rarity,
                    drops: m
                        .drops
                        .iter()
                        .map(|d| tabg_game::loot::Drop {
                            weight: d.spawn_rate,
                            items: d.items.iter().map(|&[i, q]| (i, q)).collect(),
                        })
                        .collect(),
                })
                .collect(),
        };
        tracing::info!(
            spots = settings.loot.spots.len(),
            modifiers = settings.loot.modifiers.len(),
            "loaded loot tables"
        );
        settings.cars = world
            .cars
            .iter()
            .map(|c| tabg_game::cars::CarSpawn {
                car_type: c.car_type,
                car_index: c.car_index,
                position: c.position,
                rotation: c.rotation,
                seats: c
                    .seats
                    .iter()
                    .map(|s| (s.network_index, s.driver))
                    .collect(),
                parts: c
                    .parts
                    .iter()
                    .map(|p| tabg_protocol::messages::CarPart {
                        index: p.index,
                        health: p.health,
                        name: p.name.clone(),
                    })
                    .collect(),
            })
            .collect();
        tracing::info!(cars = settings.cars.len(), "loaded cars");
        settings.heal_amounts = world
            .heal_amounts
            .iter()
            .filter_map(|(k, &v)| k.parse().ok().map(|k| (k, v)))
            .collect();
        settings.grenades = world.grenades.iter().copied().collect();
        if let Some(ring) = world.ring {
            settings.ring.ring_times = ring.ring_times;
            settings.ring.base_time = ring.base_time_to_travel;
            settings.ring.hot_spots = ring.hot_spots;
            tracing::info!(ring_times = ?settings.ring.ring_times, "loaded ring timing");
        }
    }
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
        fail(e);
    }
}
