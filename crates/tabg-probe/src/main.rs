use std::collections::BTreeMap;
use std::time::Duration;

use clap::Parser;
use tabg_probe::Probe;
use tabg_protocol::EventCode;

/// Join a TABG server headlessly and report what it sends.
#[derive(Parser)]
#[command(version)]
struct Args {
    #[arg(long, default_value = "127.0.0.1")]
    host: String,
    #[arg(long, default_value_t = 7777)]
    port: u16,
    #[arg(long, default_value = "tabg-probe")]
    name: String,
    /// How long to stay connected after joining.
    #[arg(long, default_value_t = 5.0)]
    seconds: f32,
    /// Write every received payload into this directory (fixtures).
    #[arg(long)]
    dump: Option<std::path::PathBuf>,
    /// Walk in circles around this point (x,y,z) instead of around our spawn.
    #[arg(long, value_parser = parse_vec3, allow_hyphen_values = true)]
    around: Option<[f32; 3]>,
    /// Radius of the circle, in metres.
    #[arg(long, default_value_t = 1.0)]
    radius: f32,
    /// Say hello to the destruction relay (event 246) and report the blasts we are sent.
    #[arg(long)]
    destruction: bool,
    /// Attack this player index: after --hit-at seconds, report a hit that leaves them at 40
    /// health, and 8 s later a lethal one (exercises damage, death and the end of the match).
    #[arg(long)]
    hit_player: Option<u8>,
    #[arg(long, default_value_t = 60.0)]
    hit_at: f32,
    /// Join through Unity Relay with this join code instead of --host/--port.
    #[arg(long)]
    relay: Option<String>,
    /// TABG install to read the Unity project id from (for --relay).
    #[arg(long, env = "TABG_CLIENT_DIR")]
    game_dir: Option<std::path::PathBuf>,
}

fn parse_vec3(s: &str) -> Result<[f32; 3], String> {
    let parts: Vec<f32> = s
        .split(',')
        .map(|p| p.trim().parse::<f32>().map_err(|e| e.to_string()))
        .collect::<Result<_, _>>()?;
    parts.try_into().map_err(|_| "expected x,y,z".to_string())
}

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let args = Args::parse();
    let mut probe = match &args.relay {
        Some(code) => {
            let dir = args
                .game_dir
                .as_deref()
                .ok_or("--relay needs --game-dir (or TABG_CLIENT_DIR)")?;
            let project = tabg_utp::project_id_from_game(dir)?;
            let client = tabg_utp::RelayClient::join(&project, code)?;
            Probe::over(
                Box::new(client),
                tabg_utp::client::SERVER,
                Probe::request_for(&args.name),
            )
        }
        None => Probe::named(&args.host, args.port, &args.name)?,
    };
    let joined = probe.pump_until(Duration::from_secs(10), |s| {
        s.world.is_some()
            || s.disconnected
            || matches!(
                s.response,
                Some(tabg_protocol::messages::RoomInitResponse::Refused { .. })
            )
    })?;
    println!("connected: {}  joined: {joined}", probe.seen.connected);
    println!("response: {:?}", probe.seen.response);
    if let Some(w) = &probe.seen.world {
        println!(
            "spawn: {:?} rot {}  players: {}  cars: {}  rings: {:?}  phase: {:?}",
            w.position,
            w.rotation,
            w.players.len(),
            w.cars.len(),
            w.rings,
            w.phase
        );
    }
    if args.destruction {
        probe.send_raw(246, b"DSTR\x01\x01".to_vec(), true)?;
    }
    let start = args
        .around
        .or(probe.seen.world.as_ref().map(|w| w.position));
    let deadline = std::time::Instant::now() + Duration::from_secs_f32(args.seconds);
    let mut t = 0.0f32;
    let mut hits_sent = 0;
    while std::time::Instant::now() < deadline && !probe.seen.disconnected {
        if let Some(p) = start {
            t += 0.033;
            probe.move_to([
                p[0] + args.radius * t.sin(),
                p[1],
                p[2] + args.radius * t.cos(),
            ])?;
        }
        if let Some(victim) = args.hit_player {
            let elapsed = args.seconds
                - deadline
                    .saturating_duration_since(std::time::Instant::now())
                    .as_secs_f32();
            for (i, (at, health)) in [(args.hit_at, 40.0), (args.hit_at + 8.0, 0.0)]
                .into_iter()
                .enumerate()
            {
                if hits_sent == i && elapsed >= at {
                    probe.hit(victim, health)?;
                    println!("hit player {victim} -> {health} at {elapsed:.1}s");
                    hits_sent += 1;
                }
            }
        }
        probe.pump(Duration::from_millis(33))?;
    }

    if args.destruction {
        let reply = |op: u8| probe.seen.payloads.iter().filter(|(c, p)| *c == 246 && p.get(5) == Some(&op)).count();
        println!("destruction: accepted {}, denied {}", reply(2), reply(3));
        for (_, p) in probe.seen.payloads.iter().filter(|(c, p)| *c == 246 && p.get(5) == Some(&5)) {
            let f = |at: usize| f32::from_le_bytes([p[at], p[at + 1], p[at + 2], p[at + 3]]);
            let seq = u32::from_le_bytes([p[6], p[7], p[8], p[9]]);
            println!("destruction: blast {seq} at ({:.2}, {:.2}, {:.2}) radius {} power {}", f(10), f(14), f(18), f(22), f(26));
        }
    }

    let mut counts: BTreeMap<u8, (usize, usize)> = BTreeMap::new();
    for &(_, code, len) in &probe.seen.packets {
        let e = counts.entry(code).or_default();
        e.0 += 1;
        e.1 += len;
    }
    println!("received packets by event (count, bytes):");
    for (code, (n, bytes)) in &counts {
        println!(
            "  {code:>3} {:<28} {n:>5} {bytes:>8}",
            format!("{:?}", EventCode::from_u8(*code))
        );
    }
    println!("others seen moving: {:?}", probe.seen.others);
    println!("disconnected: {}", probe.seen.disconnected);
    if !probe.seen.decode_errors.is_empty() {
        println!("DECODE ERRORS: {:?}", probe.seen.decode_errors);
    }
    if let Some(dir) = args.dump {
        std::fs::create_dir_all(&dir)?;
        for (i, (code, payload)) in probe.seen.payloads.iter().enumerate() {
            let name = EventCode::from_u8(*code)
                .map_or_else(|| format!("code{code}"), |e| format!("{e:?}"));
            std::fs::write(dir.join(format!("{i:04}-{code:03}-{name}.bin")), payload)?;
        }
    }
    Ok(())
}
