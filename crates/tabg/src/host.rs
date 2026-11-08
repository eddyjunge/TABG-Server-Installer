//! The host process: window or browser dashboard, the local API on 127.0.0.1, and the game
//! server supervisor.

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
        #[cfg(windows)]
        {
            // The window has no console for the child to inherit; don't let it open its own.
            use std::os::windows::process::CommandExt;
            c.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
        }
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

/// A host that is set up but not serving yet.
struct Started {
    runtime: tokio::runtime::Runtime,
    listener: tokio::net::TcpListener,
    app: Arc<tabg_web::App>,
    url: String,
}

/// Claim the data dir: bind the local API and publish its address for the CLI.
fn start(paths: &Paths, port: u16) -> Started {
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
    Started {
        runtime,
        listener,
        app,
        url: dashboard_url(port, &token),
    }
}

fn running_url(paths: &Paths) -> Option<String> {
    Api::find(paths)?;
    let f: HostFile = serde_json::from_slice(&std::fs::read(paths.host_file()).unwrap_or_default())
        .unwrap_or_else(|e| die(e));
    Some(dashboard_url(f.port, &f.token))
}

fn finish(paths: &Paths, app: &tabg_web::App) {
    println!("stopping…");
    app.supervisor.stop();
    let _ = std::fs::remove_file(paths.host_file());
}

pub fn run(paths: &Paths, port: u16, browser: bool, autostart: bool) {
    // One host per data dir: if it's already running, just show it.
    if let Some(url) = running_url(paths) {
        println!("tabg is already running: {url}");
        if browser {
            open_browser(&url);
        }
        return;
    }
    let Started {
        runtime,
        listener,
        app,
        url,
    } = start(paths, port);
    println!("tabg dashboard: {url}");
    println!("data: {}", paths.root.display());
    println!("press Ctrl-C to stop");
    if browser {
        open_browser(&url);
    }
    let install = tabg_host::setup::Install::load(paths);
    if !install.ready() {
        app.supervisor
            .host_log("setup needed: run it from the dashboard or with `tabg setup`");
    } else if autostart {
        app.supervisor.start();
    }
    let shutdown = async {
        let _ = tokio::signal::ctrl_c().await;
    };
    if let Err(e) = runtime.block_on(tabg_web::serve(Arc::clone(&app), listener, shutdown)) {
        eprintln!("dashboard stopped: {e}");
    }
    finish(paths, &app);
}

/// The default: a native window. The local API keeps running behind it for the CLI.
pub fn run_window(paths: &Paths, port: u16) {
    if let Some(url) = running_url(paths) {
        if let Err(e) = tabg_gui::run_already_running(url.clone()) {
            println!("tabg is already running: {url} ({e})");
        }
        return;
    }
    let Started {
        runtime,
        listener,
        app,
        url,
    } = start(paths, port);
    let (stop, stopped) = tokio::sync::oneshot::channel::<()>();
    let served = Arc::clone(&app);
    let server = runtime.spawn(async move {
        let shutdown = async {
            let _ = stopped.await;
        };
        if let Err(e) = tabg_web::serve(served, listener, shutdown).await {
            eprintln!("local API stopped: {e}");
        }
    });
    let window = tabg_gui::run(tabg_gui::Host {
        app: Arc::clone(&app),
        runtime: runtime.handle().clone(),
    });
    if let Err(e) = window {
        // No usable graphics: hosting still works in the browser.
        eprintln!("couldn't open the window: {e}");
        println!("tabg dashboard: {url}");
        println!("press Ctrl-C to stop");
        open_browser(&url);
        let _ = runtime.block_on(tokio::signal::ctrl_c());
    }
    let _ = stop.send(());
    let _ = runtime.block_on(server);
    finish(paths, &app);
}
