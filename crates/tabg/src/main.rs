//! `tabg` — host a TABG server for your friends.
//!
//! `tabg` alone starts the host (dashboard + server supervisor) and opens the dashboard. Other
//! commands talk to a running host over its local API, or work on the files directly when no
//! host is running.

mod client;
mod host;

use std::time::Duration;

use clap::{Parser, Subcommand};
use serde_json::{Value, json};
use tabg_host::Paths;

use client::{Api, die};

#[derive(Parser)]
#[command(
    name = "tabg",
    version,
    about = "Host a Totally Accurate Battlegrounds server for your friends"
)]
struct Cli {
    #[command(subcommand)]
    command: Option<Cmd>,
}

#[derive(Subcommand)]
enum Cmd {
    /// Start the host and open the dashboard (the default).
    Host {
        /// Don't open a browser.
        #[arg(long)]
        no_browser: bool,
        /// Dashboard port (on 127.0.0.1).
        #[arg(long, default_value_t = 7780)]
        port: u16,
        /// Don't start the game server automatically.
        #[arg(long)]
        no_start: bool,
    },
    /// Find TABG and extract the map data from your TABG Dedicated Server.
    Setup {
        /// Extract the map data again even if it exists.
        #[arg(long)]
        force: bool,
        /// Path to TABG Dedicated Server, if Steam detection doesn't find it.
        #[arg(long)]
        server_dir: Option<std::path::PathBuf>,
        /// Path to TABG (for join codes), if Steam detection doesn't find it.
        #[arg(long)]
        client_dir: Option<std::path::PathBuf>,
    },
    /// Start the game server (and the host, if it isn't running).
    Start,
    /// Stop the game server.
    Stop,
    /// Restart the game server.
    Restart,
    /// Show the server status, join code and players.
    Status,
    /// Type console commands (start, say, kick, ban, give, heal, players).
    Console,
    /// Say something to everyone in the game.
    Say { text: Vec<String> },
    /// Kick a player (name or #index).
    Kick { who: Vec<String> },
    /// Ban a player (name or id) and kick them.
    Ban { who: Vec<String> },
    /// Lift a ban.
    Unban { who: Vec<String> },
    /// List admins, or add/remove one.
    Admins {
        #[command(subcommand)]
        action: Option<AdminCmd>,
    },
    /// Read or change tabg.toml.
    Config {
        #[command(subcommand)]
        action: ConfigCmd,
    },
    /// Built-in and saved presets.
    Preset {
        #[command(subcommand)]
        action: PresetCmd,
    },
    /// Config backups.
    Backup {
        #[command(subcommand)]
        action: BackupCmd,
    },
    /// Show the server log.
    Logs {
        #[arg(short, default_value_t = 50)]
        n: usize,
        /// Keep printing new lines.
        #[arg(short, long)]
        follow: bool,
    },
    /// Run the game server in this process (used by the host).
    #[command(hide = true)]
    Serve,
}

#[derive(Subcommand)]
enum AdminCmd {
    Add { who: Vec<String> },
    Remove { who: Vec<String> },
}

#[derive(Subcommand)]
enum ConfigCmd {
    /// Print tabg.toml.
    Show,
    /// Print where tabg.toml is.
    Path,
    /// Read one key, e.g. `game.max_players`.
    Get { key: String },
    /// Set one key and apply it, e.g. `tabg config set game.teams solo`.
    Set { key: String, value: Vec<String> },
}

#[derive(Subcommand)]
enum PresetCmd {
    List,
    /// Apply a preset's rules.
    Apply {
        id: String,
    },
    /// Save the current rules as a preset.
    Save {
        id: String,
        #[arg(long, default_value = "")]
        title: String,
        #[arg(long, default_value = "")]
        description: String,
    },
}

#[derive(Subcommand)]
enum BackupCmd {
    List,
    Restore { id: String },
}

fn words(w: &[String]) -> String {
    w.join(" ")
}

fn print_status(v: &Value) {
    let s = &v["server"];
    println!("server: {}", s["state"].as_str().unwrap_or("?"));
    if let Some(code) = s["join_code"].as_str() {
        println!("join code: {code}");
    }
    if let Some(d) = s["direct"].as_str() {
        println!("direct: {d}");
    }
    if let Some(e) = s["error"].as_str() {
        println!("error: {e}");
    }
    println!("match: {}", s["phase"].as_str().unwrap_or("?"));
    let players = s["players"].as_array().cloned().unwrap_or_default();
    println!("players: {}", players.len());
    for p in players {
        println!(
            "  #{} {} ({}){}",
            p["index"],
            p["name"].as_str().unwrap_or(""),
            p["id"].as_str().unwrap_or(""),
            if p["admin"].as_bool() == Some(true) {
                " admin"
            } else {
                ""
            }
        );
    }
    if let Some(setup) = v.get("setup")
        && setup["ready"].as_bool() == Some(false)
    {
        println!("setup: not done yet; run `tabg setup`");
    }
}

fn print_applied(v: &Value) {
    match v["applied"].as_str() {
        Some("saved") => println!("saved"),
        Some("reloaded") => println!("saved; the running server took the change"),
        Some("restarted") => println!("saved; the server restarted to apply it"),
        _ => println!("{v}"),
    }
}

/// Print log lines; returns the last sequence number seen.
fn print_log(v: &Value, last: u64) -> u64 {
    let mut last = last;
    for l in v["lines"].as_array().into_iter().flatten() {
        println!(
            "{:>7} {}",
            l["source"].as_str().unwrap_or(""),
            l["text"].as_str().unwrap_or("")
        );
        last = l["seq"].as_u64().unwrap_or(last);
    }
    last
}

fn main() {
    let cli = Cli::parse();
    let paths = Paths::from_env();
    let command = cli.command.unwrap_or(Cmd::Host {
        no_browser: false,
        port: 7780,
        no_start: false,
    });
    match command {
        Cmd::Serve => {
            host::init_logging(true);
            if let Err(e) = tabg_host::ipc::serve_child(&paths) {
                eprintln!("error: {e}");
                std::process::exit(1);
            }
        }
        Cmd::Host {
            no_browser,
            port,
            no_start,
        } => host::run(&paths, port, !no_browser, !no_start),
        Cmd::Setup {
            force,
            server_dir,
            client_dir,
        } => {
            let mut install = tabg_host::setup::Install::load(&paths);
            if server_dir.is_some() {
                install.server_dir = server_dir;
            }
            if client_dir.is_some() {
                install.client_dir = client_dir;
            }
            install.save(&paths).unwrap_or_else(|e| die(e));
            match tabg_host::setup::run(&paths, force, &mut |m| println!("{m}")) {
                Ok(_) => println!("setup done; run `tabg` to host"),
                Err(e) => die(e),
            }
        }
        Cmd::Start => {
            let api = Api::ensure_host(&paths);
            print_status(&api.post("start", json!({})));
            // The join code arrives a moment later.
            for _ in 0..40 {
                std::thread::sleep(Duration::from_millis(250));
                let v = api.get("status");
                if v["server"]["join_code"].is_string() || v["server"]["direct"].is_string() {
                    print_status(&v);
                    return;
                }
                if v["server"]["error"].is_string() {
                    print_status(&v);
                    std::process::exit(1);
                }
            }
        }
        Cmd::Stop => print_status(&Api::running(&paths).post("stop", json!({}))),
        Cmd::Restart => print_status(&Api::running(&paths).post("restart", json!({}))),
        Cmd::Status => match Api::find(&paths) {
            Some(api) => print_status(&api.get("status")),
            None => {
                println!("no tabg host running (start one with `tabg` or `tabg start`)");
                let install = tabg_host::setup::Install::load(&paths);
                if !install.ready() {
                    println!("setup: not done yet; run `tabg setup`");
                }
            }
        },
        Cmd::Console => {
            let api = Api::running(&paths);
            println!("console: type commands, Ctrl-D to leave (help for a list)");
            let follow = api.clone();
            std::thread::spawn(move || {
                let mut since = follow.get("logs?n=1")["lines"][0]["seq"]
                    .as_u64()
                    .unwrap_or(0);
                loop {
                    std::thread::sleep(Duration::from_millis(300));
                    let v = follow.get(&format!("logs?n=500&after={since}"));
                    for l in v["lines"].as_array().into_iter().flatten() {
                        since = l["seq"].as_u64().unwrap_or(since);
                        if l["source"] != "server" {
                            println!("{}", l["text"].as_str().unwrap_or(""));
                        }
                    }
                }
            });
            for line in std::io::stdin().lines() {
                let Ok(line) = line else { break };
                if !line.trim().is_empty() {
                    api.post("console", json!({ "line": line }));
                }
            }
        }
        Cmd::Say { text } => {
            Api::running(&paths).post(
                "console",
                json!({ "line": format!("say {}", words(&text)) }),
            );
        }
        Cmd::Kick { who } => {
            Api::running(&paths).post(
                "console",
                json!({ "line": format!("kick {}", words(&who)) }),
            );
        }
        Cmd::Ban { who } => print_applied(&client::with_host_or_local(&paths, |api| {
            api.post("players/ban", json!({ "who": words(&who), "on": true }))
        })),
        Cmd::Unban { who } => print_applied(&client::with_host_or_local(&paths, |api| {
            api.post("players/ban", json!({ "who": words(&who), "on": false }))
        })),
        Cmd::Admins { action } => {
            let (who, on) = match action {
                None => {
                    let v = client::with_host_or_local(&paths, |api| api.get("players"));
                    for a in v["admins"].as_array().into_iter().flatten() {
                        println!("{}", a.as_str().unwrap_or(""));
                    }
                    return;
                }
                Some(AdminCmd::Add { who }) => (words(&who), true),
                Some(AdminCmd::Remove { who }) => (words(&who), false),
            };
            print_applied(&client::with_host_or_local(&paths, |api| {
                api.post("players/admin", json!({ "who": who, "on": on }))
            }));
        }
        Cmd::Config { action } => match action {
            ConfigCmd::Path => println!("{}", paths.config().display()),
            ConfigCmd::Show => {
                let v = client::with_host_or_local(&paths, |api| api.get("config"));
                print!("{}", v["toml"].as_str().unwrap_or(""));
            }
            ConfigCmd::Get { key } => {
                let v = client::with_host_or_local(&paths, |api| {
                    api.get(&format!("config/get?key={key}"))
                });
                println!("{}", v["value"].as_str().unwrap_or(""));
            }
            ConfigCmd::Set { key, value } => {
                print_applied(&client::with_host_or_local(&paths, |api| {
                    api.post("config/set", json!({ "key": key, "value": words(&value) }))
                }))
            }
        },
        Cmd::Preset { action } => match action {
            PresetCmd::List => {
                let v = client::with_host_or_local(&paths, |api| api.get("presets"));
                for p in v["presets"].as_array().into_iter().flatten() {
                    println!(
                        "{:16} {} — {}",
                        p["id"].as_str().unwrap_or(""),
                        p["title"].as_str().unwrap_or(""),
                        p["description"].as_str().unwrap_or("")
                    );
                }
            }
            PresetCmd::Apply { id } => print_applied(&client::with_host_or_local(&paths, |api| {
                api.post("presets/apply", json!({ "id": id }))
            })),
            PresetCmd::Save {
                id,
                title,
                description,
            } => {
                client::with_host_or_local(&paths, |api| {
                    api.post(
                        "presets/save",
                        json!({ "id": id, "title": title, "description": description }),
                    )
                });
                println!("saved preset {id}");
            }
        },
        Cmd::Backup { action } => match action {
            BackupCmd::List => {
                let v = client::with_host_or_local(&paths, |api| api.get("backups"));
                for b in v["backups"].as_array().into_iter().flatten() {
                    println!(
                        "{:16} {}",
                        b["id"].as_str().unwrap_or(""),
                        b["reason"].as_str().unwrap_or("")
                    );
                }
            }
            BackupCmd::Restore { id } => {
                print_applied(&client::with_host_or_local(&paths, |api| {
                    api.post("backups/restore", json!({ "id": id }))
                }))
            }
        },
        Cmd::Logs { n, follow } => {
            let api = Api::running(&paths);
            let mut last = print_log(&api.get(&format!("logs?n={n}")), 0);
            if follow {
                loop {
                    std::thread::sleep(Duration::from_millis(500));
                    last = print_log(&api.get(&format!("logs?n=500&after={last}")), last);
                }
            }
        }
    }
}
