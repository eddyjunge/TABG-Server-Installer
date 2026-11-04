//! The host process: dashboard + local API on 127.0.0.1, and the game server supervisor.

use std::process::Command;
use std::sync::{Arc, Mutex};

use tabg_host::{Paths, Supervisor};

use crate::client::{Api, HostFile, die};

pub fn init_logging(child: bool) {
    let default = if child { "info" } else { "info,tabg_web=info" };
    tracing_subscriber::fmt()
        .with_writer(std::io::stderr)
        .with_ansi(!child)
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| default.into()),
        )
        .init();
}

/// How the supervisor starts the game server: this binary, `tabg serve`.
pub fn spawner(paths: &Paths) -> tabg_host::supervisor::Spawner {
    let root = paths.root.clone();
    Arc::new(move || {
        let exe = std::env::current_exe().unwrap_or_else(|_| "tabg".into());
        let mut c = Command::new(exe);
        c.arg("serve").env("TABG_HOME", &root);
        c
    })
}

fn open_browser(url: &str) {
    #[cfg(windows)]
    let r = Command::new("cmd").args(["/C", "start", "", url]).spawn();
    #[cfg(target_os = "macos")]
    let r = Command::new("open").arg(url).spawn();
    #[cfg(all(unix, not(target_os = "macos")))]
    let r = Command::new("xdg-open").arg(url).spawn();
    if r.is_err() {
        println!("open {url} in your browser");
    }
}

fn dashboard_url(port: u16, token: &str) -> String {
    format!("http://127.0.0.1:{port}/?token={token}")
}

pub fn run(paths: &Paths, port: u16, browser: bool, autostart: bool) {
    // One host per data dir: if it's already running, just show it.
    if Api::find(paths).is_some() {
        let f: HostFile =
            serde_json::from_slice(&std::fs::read(paths.host_file()).unwrap_or_default())
                .unwrap_or_else(|e| die(e));
        let url = dashboard_url(f.port, &f.token);
        println!("tabg is already running: {url}");
        if browser {
            open_browser(&url);
        }
        return;
    }
    init_logging(false);
    let runtime = tokio::runtime::Runtime::new().unwrap_or_else(|e| die(e));
    let (listener, port) = runtime.block_on(async {
        for p in port..port.saturating_add(10) {
            if let Ok(l) = tokio::net::TcpListener::bind(("127.0.0.1", p)).await {
                return (l, p);
            }
        }
        die(format!("ports {port}–{} are all busy", port + 9))
    });
    let token = tabg_web::security::new_token();
    let supervisor = Supervisor::new(paths.clone(), spawner(paths));
    let app = Arc::new(tabg_web::App {
        paths: paths.clone(),
        supervisor: supervisor.clone(),
        token: token.clone(),
        port,
        setup_busy: Mutex::new(false),
        share: Mutex::new(None),
        sync_port: Mutex::new(None),
    });
    let host_file = HostFile {
        port,
        token: token.clone(),
        pid: std::process::id(),
    };
    let _ = std::fs::create_dir_all(&paths.root);
    tabg_host::paths::write_atomic(
        &paths.host_file(),
        &serde_json::to_vec(&host_file).expect("serializes"),
    )
    .unwrap_or_else(|e| die(e));
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        let _ = std::fs::set_permissions(paths.host_file(), std::fs::Permissions::from_mode(0o600));
    }
    // Make sure there's a config to edit.
    if let Err(e) = tabg_host::apply::read_config(paths) {
        supervisor.host_log(format!("tabg.toml: {e}"));
    }
    let url = dashboard_url(port, &token);
    println!("tabg dashboard: {url}");
    println!("data: {}", paths.root.display());
    println!("press Ctrl-C to stop");
    if browser {
        open_browser(&url);
    }
    let install = tabg_host::setup::Install::load(paths);
    if !install.ready() {
        supervisor.host_log("setup needed: run it from the dashboard or with `tabg setup`");
    } else if autostart {
        supervisor.start();
    }
    let shutdown = async {
        let _ = tokio::signal::ctrl_c().await;
    };
    if let Err(e) = runtime.block_on(tabg_web::serve(app, listener, shutdown)) {
        eprintln!("dashboard stopped: {e}");
    }
    println!("stopping…");
    supervisor.stop();
    let _ = std::fs::remove_file(paths.host_file());
}
