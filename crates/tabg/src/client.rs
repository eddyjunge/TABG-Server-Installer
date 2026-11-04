//! Talking to the host's local API. Without a running host, commands that only touch files get a
//! temporary in-process API (the same code the dashboard uses) on a random local port.

use std::process::{Command, Stdio};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};
use serde_json::Value;
use tabg_host::{Paths, Supervisor};

/// What a running host writes to `host.json` for the CLI.
#[derive(Serialize, Deserialize)]
pub struct HostFile {
    pub port: u16,
    pub token: String,
    pub pid: u32,
}

pub fn die(e: impl std::fmt::Display) -> ! {
    eprintln!("error: {e}");
    std::process::exit(1);
}

#[derive(Clone)]
pub struct Api {
    base: String,
    token: String,
    agent: ureq::Agent,
}

impl Api {
    fn new(port: u16, token: String) -> Self {
        Self {
            base: format!("http://127.0.0.1:{port}/api/"),
            token,
            agent: ureq::Agent::config_builder()
                .timeout_global(Some(Duration::from_secs(30)))
                .http_status_as_error(false)
                .build()
                .into(),
        }
    }

    /// The running host, if there is one and it answers.
    pub fn find(paths: &Paths) -> Option<Self> {
        let f: HostFile = serde_json::from_slice(&std::fs::read(paths.host_file()).ok()?).ok()?;
        let api = Self::new(f.port, f.token);
        let ok = api
            .agent
            .get(format!("{}status", api.base))
            .header("Authorization", format!("Bearer {}", api.token))
            .call()
            .is_ok_and(|r| r.status().is_success());
        ok.then_some(api)
    }

    pub fn running(paths: &Paths) -> Self {
        Self::find(paths).unwrap_or_else(|| {
            die("no tabg host is running; start it with `tabg` (or `tabg start`)")
        })
    }

    /// The running host, or start one in the background.
    pub fn ensure_host(paths: &Paths) -> Self {
        if let Some(api) = Self::find(paths) {
            return api;
        }
        let exe = std::env::current_exe().unwrap_or_else(|e| die(e));
        let _ = std::fs::create_dir_all(paths.logs());
        let log = std::fs::File::create(paths.logs().join("host.log")).unwrap_or_else(|e| die(e));
        // Detached on purpose: the host outlives this CLI process (which exits right after).
        #[allow(clippy::zombie_processes)]
        Command::new(exe)
            .args(["host", "--no-browser", "--no-start"])
            .env("TABG_HOME", &paths.root)
            .stdin(Stdio::null())
            .stdout(log.try_clone().unwrap_or_else(|e| die(e)))
            .stderr(log)
            .spawn()
            .unwrap_or_else(|e| die(format!("could not start the host: {e}")));
        let deadline = Instant::now() + Duration::from_secs(15);
        while Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(200));
            if let Some(api) = Self::find(paths) {
                println!("started the tabg host in the background");
                return api;
            }
        }
        die(format!(
            "the host didn't come up; see {}",
            paths.logs().join("host.log").display()
        ))
    }

    fn answer(r: Result<ureq::http::Response<ureq::Body>, ureq::Error>) -> Value {
        let mut r = r.unwrap_or_else(|e| die(format!("the host didn't answer: {e}")));
        let status = r.status();
        let v: Value = r.body_mut().read_json().unwrap_or(Value::Null);
        if !status.is_success() {
            let mut msg = v["error"].as_str().unwrap_or("request failed").to_owned();
            for p in v["problems"].as_array().into_iter().flatten() {
                msg.push_str(&format!(
                    "\n  {}: {}",
                    p["field"].as_str().unwrap_or(""),
                    p["message"].as_str().unwrap_or("")
                ));
            }
            die(msg);
        }
        v
    }

    pub fn get(&self, path: &str) -> Value {
        Self::answer(
            self.agent
                .get(format!("{}{path}", self.base))
                .header("Authorization", format!("Bearer {}", self.token))
                .call(),
        )
    }

    pub fn post(&self, path: &str, body: Value) -> Value {
        Self::answer(
            self.agent
                .post(format!("{}{path}", self.base))
                .header("Authorization", format!("Bearer {}", self.token))
                .send_json(body),
        )
    }
}

/// Run `f` against the running host, or against a temporary local API over the same files.
pub fn with_host_or_local(paths: &Paths, f: impl FnOnce(&Api) -> Value) -> Value {
    if let Some(api) = Api::find(paths) {
        return f(&api);
    }
    let runtime = tokio::runtime::Runtime::new().unwrap_or_else(|e| die(e));
    let listener = runtime
        .block_on(tokio::net::TcpListener::bind("127.0.0.1:0"))
        .unwrap_or_else(|e| die(e));
    let port = listener.local_addr().map(|a| a.port()).unwrap_or(0);
    let token = tabg_web::security::new_token();
    let app = Arc::new(tabg_web::App {
        paths: paths.clone(),
        supervisor: Supervisor::new(paths.clone(), crate::host::spawner(paths)),
        token: token.clone(),
        port,
        setup_busy: Mutex::new(false),
        share: Mutex::new(None),
        sync_port: Mutex::new(None),
    });
    let (stop_tx, stop_rx) = tokio::sync::oneshot::channel::<()>();
    let server = runtime.spawn(tabg_web::serve(app, listener, async {
        let _ = stop_rx.await;
    }));
    let api = Api::new(port, token);
    let out = f(&api);
    let _ = stop_tx.send(());
    let _ = runtime.block_on(server);
    out
}
