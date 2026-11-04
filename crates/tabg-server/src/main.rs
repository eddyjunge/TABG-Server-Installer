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
    /// Host through Unity Relay: players type the printed join code into TABG's join box. No port
    /// forwarding needed. Needs the Unity project id, read from --game-dir.
    #[arg(long)]
    relay: bool,
    /// Relay region (e.g. europe-west4); Unity picks one if unset. `--relay-region list` lists them.
    #[arg(long)]
    relay_region: Option<String>,
    /// A TABG install (client or dedicated server) to read the Unity project id from.
    #[arg(long, env = "TABG_CLIENT_DIR")]
    game_dir: Option<std::path::PathBuf>,
    /// Show the server in TABG's community server browser (needs --world for the game version).
    #[arg(long)]
    list: bool,
    /// Server name in the browser.
    #[arg(long, default_value = "tabg server")]
    name: String,
    /// Server description in the browser.
    #[arg(long, default_value = "")]
    description: String,
    /// Direct mode: open the port on the router with UPnP.
    #[arg(long)]
    upnp: bool,
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
    let mut app_version = String::new();
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
        app_version.clone_from(&world.app_version);
        if let Err(e) = tabg_server::apply_world(&mut settings, &world) {
            fail(format!("{}: {e}", path.display()));
        }
        tracing::info!(
            spawns = settings.spawn_points.len(),
            loot_spots = settings.loot.spots.len(),
            cars = settings.cars.len(),
            "loaded map data"
        );
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

    let relay = if args.relay || args.relay_region.is_some() {
        let dir = args
            .game_dir
            .as_deref()
            .unwrap_or_else(|| fail("--relay needs --game-dir (or TABG_CLIENT_DIR)"));
        let project_id = tabg_utp::project_id_from_game(dir).unwrap_or_else(|e| fail(e));
        if args.relay_region.as_deref() == Some("list") {
            let session = tabg_utp::ugs::Session::sign_in(&project_id).unwrap_or_else(|e| fail(e));
            for r in session.regions().unwrap_or_else(|e| fail(e)) {
                println!("{:24} {}", r.id, r.description);
            }
            return;
        }
        Some(tabg_utp::RelayConfig {
            project_id,
            region: args.relay_region,
            max_players: u32::from(args.max_players),
        })
    } else {
        None
    };

    let listing = args.list.then(|| {
        if app_version.is_empty() {
            fail(
                "--list needs the game version from a --world dump (re-run scripts/dump-world.sh)",
            );
        }
        tabg_server::listing::ListingConfig {
            name: args.name.clone(),
            description: args.description.clone(),
            version: app_version.clone(),
        }
    });

    let stop = Arc::new(AtomicBool::new(false));
    {
        let stop = Arc::clone(&stop);
        // Stop cleanly on Ctrl-C so the UPnP mapping is removed.
        let _ = ctrlc::set_handler(move || stop.store(true, std::sync::atomic::Ordering::Relaxed));
    }
    if let Err(e) = run(
        ServerConfig {
            bind: args.bind,
            port: args.port,
            relay,
            listing,
            upnp: args.upnp,
            settings,
        },
        stop,
    ) {
        fail(e);
    }
}
