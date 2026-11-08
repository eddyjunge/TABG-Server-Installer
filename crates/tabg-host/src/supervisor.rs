//! Runs the game server child process: start/stop/restart, restarts after crashes with
//! exponential backoff (keeping a crash log), relays console commands, and turns the child's
//! events into host state, player history and a live log for the dashboard.

use std::collections::VecDeque;
use std::io::{BufRead, BufReader, Write};
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::{Arc, Mutex, MutexGuard};
use std::time::{Duration, Instant};

use serde::Serialize;
use tabg_game::{PlayerInfo, RoomEvent};
use tabg_server::{Event, Status};
use tokio::sync::broadcast;

use crate::config::ServerToml;
use crate::ipc::ToServer;
use crate::paths::{Paths, now};
use crate::store::PlayerStore;

/// Log lines kept in memory for the dashboard.
const LOG_LINES: usize = 2000;
const MAX_BACKOFF: Duration = Duration::from_secs(60);
/// A run this long resets the backoff.
const STABLE_AFTER: Duration = Duration::from_secs(300);

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum RunState {
    Stopped,
    Starting,
    Running,
    /// Crashed; restarting after a pause.
    Restarting,
}

#[derive(Clone, Debug, Serialize, PartialEq)]
pub struct Crash {
    pub at: u64,
    pub reason: String,
    pub log_file: String,
}

/// Everything the dashboard's overview shows.
#[derive(Clone, Debug, Serialize, PartialEq)]
pub struct HostStatus {
    pub state: RunState,
    /// Join code or `ip:port` once the server is reachable.
    pub join_code: Option<String>,
    pub direct: Option<String>,
    pub phase: String,
    pub players: Vec<PlayerInfo>,
    pub started_at: Option<u64>,
    pub restarts: u32,
    /// After a crash: when the next start happens.
    pub retry_at: Option<u64>,
    pub last_crash: Option<Crash>,
    /// A problem that stops the server from starting (bad config, no map data…).
    pub error: Option<String>,
}

#[derive(Clone, Debug, Serialize, PartialEq, Eq)]
pub struct LogLine {
    /// Increases by one per line (for followers).
    pub seq: u64,
    pub at: u64,
    /// `server` (the child's log), `game` (room events), `chat`, `console` (commands and replies),
    /// `host`.
    pub source: String,
    pub text: String,
}

/// Pushed to dashboard subscribers.
#[derive(Clone, Debug)]
pub enum Update {
    Status(Box<HostStatus>),
    Log(LogLine),
}

struct Inner {
    status: HostStatus,
    log: VecDeque<LogLine>,
    want_running: bool,
    child: Option<Child>,
    stdin: Option<ChildStdin>,
    /// Bumped per spawned child, so a dead child's threads can't touch its successor.
    generation: u64,
    backoff: Duration,
    run_started: Option<Instant>,
    store: PlayerStore,
    next_seq: u64,
}

/// Builds the child command (`tabg serve` in the real binary).
pub type Spawner = Arc<dyn Fn() -> Command + Send + Sync>;

#[derive(Clone)]
pub struct Supervisor {
    inner: Arc<Mutex<Inner>>,
    updates: broadcast::Sender<Update>,
    paths: Paths,
    spawner: Spawner,
}

fn describe(event: &RoomEvent) -> String {
    match event {
        RoomEvent::Joined { name, id, .. } => format!("{name} joined ({id})"),
        RoomEvent::Refused { name, reason, .. } => format!("{name} was refused: {reason}"),
        RoomEvent::Left { name, .. } => format!("{name} left"),
        RoomEvent::Chat { name, text, .. } => format!("{name}: {text}"),
        RoomEvent::Killed {
            victim,
            killer: Some(k),
            ..
        } => format!("{k} killed {victim}"),
        RoomEvent::Killed {
            victim, ring: true, ..
        } => format!("{victim} died in the ring"),
        RoomEvent::Killed { victim, .. } => format!("{victim} died"),
        RoomEvent::MatchState { state } => format!("match: {state}"),
        RoomEvent::Won { players, .. } if players.is_empty() => "the match ended".into(),
        RoomEvent::Won { players, .. } => format!("{} won!", players.join(" & ")),
        RoomEvent::Banned { name, id } => format!("{name} was banned ({id})"),
        RoomEvent::Inventory {
            name,
            action,
            item,
            wanted,
            carried,
            refused,
        } => format!(
            "inventory check: {name} wanted to {action} {wanted} × item {item} but carries {carried}{}",
            if *refused { " (refused)" } else { "" }
        ),
    }
}

impl Supervisor {
    pub fn new(paths: Paths, spawner: Spawner) -> Self {
        let (updates, _) = broadcast::channel(1024);
        let store = PlayerStore::open(&paths.players());
        Self {
            inner: Arc::new(Mutex::new(Inner {
                status: HostStatus {
                    state: RunState::Stopped,
                    join_code: None,
                    direct: None,
                    phase: "stopped".into(),
                    players: Vec::new(),
                    started_at: None,
                    restarts: 0,
                    retry_at: None,
                    last_crash: None,
                    error: None,
                },
                log: VecDeque::new(),
                want_running: false,
                child: None,
                stdin: None,
                generation: 0,
                backoff: Duration::from_secs(1),
                run_started: None,
                store,
                next_seq: 1,
            })),
            updates,
            paths,
            spawner,
        }
    }

    fn lock(&self) -> MutexGuard<'_, Inner> {
        self.inner.lock().unwrap_or_else(|e| e.into_inner())
    }

    pub fn subscribe(&self) -> broadcast::Receiver<Update> {
        self.updates.subscribe()
    }

    pub fn status(&self) -> HostStatus {
        self.lock().status.clone()
    }

    /// The status, unless the supervisor is busy right now (for callers that must not block,
    /// like a UI thread while the server loads its map).
    pub fn try_status(&self) -> Option<HostStatus> {
        match self.inner.try_lock() {
            Ok(inner) => Some(inner.status.clone()),
            Err(std::sync::TryLockError::Poisoned(e)) => Some(e.into_inner().status.clone()),
            Err(std::sync::TryLockError::WouldBlock) => None,
        }
    }

    pub fn logs(&self, last: usize) -> Vec<LogLine> {
        let inner = self.lock();
        let skip = inner.log.len().saturating_sub(last);
        inner.log.iter().skip(skip).cloned().collect()
    }

    /// Lines after `seq` (at most `max`).
    pub fn logs_after(&self, seq: u64, max: usize) -> Vec<LogLine> {
        let inner = self.lock();
        inner
            .log
            .iter()
            .filter(|l| l.seq > seq)
            .take(max)
            .cloned()
            .collect()
    }

    pub fn player_history(&self) -> Vec<crate::store::PlayerRecord> {
        self.lock().store.list()
    }

    pub fn resolve_player(&self, query: &str) -> Option<String> {
        self.lock().store.resolve(query)
    }

    fn log(&self, inner: &mut Inner, source: &str, text: String) {
        let seq = inner.next_seq;
        inner.next_seq += 1;
        let line = LogLine {
            seq,
            at: now(),
            source: source.into(),
            text,
        };
        if inner.log.len() >= LOG_LINES {
            inner.log.pop_front();
        }
        inner.log.push_back(line.clone());
        let _ = self.updates.send(Update::Log(line));
    }

    fn publish(&self, inner: &Inner) {
        let _ = self
            .updates
            .send(Update::Status(Box::new(inner.status.clone())));
    }

    pub fn host_log(&self, text: impl Into<String>) {
        let mut inner = self.lock();
        self.log(&mut inner, "host", text.into());
    }

    /// Start the server (no-op if it's running).
    pub fn start(&self) {
        let mut inner = self.lock();
        inner.want_running = true;
        if inner.child.is_none() {
            inner.backoff = Duration::from_secs(1);
            self.spawn(&mut inner);
        }
    }

    /// Stop the server and wait (up to 5 s) for it to exit.
    pub fn stop(&self) {
        let child = {
            let mut inner = self.lock();
            inner.want_running = false;
            if let Some(stdin) = inner.stdin.as_mut() {
                let _ = writeln!(
                    stdin,
                    "{}",
                    serde_json::to_string(&ToServer::Stop).expect("serializes")
                );
            }
            inner.stdin = None;
            inner.child.take()
        };
        if let Some(mut child) = child {
            let deadline = Instant::now() + Duration::from_secs(5);
            while Instant::now() < deadline {
                if child.try_wait().ok().flatten().is_some() {
                    break;
                }
                std::thread::sleep(Duration::from_millis(50));
            }
            let _ = child.kill();
            let _ = child.wait();
        }
        let mut inner = self.lock();
        inner.generation += 1;
        inner.status.state = RunState::Stopped;
        inner.status.join_code = None;
        inner.status.direct = None;
        inner.status.players.clear();
        inner.status.phase = "stopped".into();
        inner.status.started_at = None;
        inner.status.retry_at = None;
        self.log(&mut inner, "host", "server stopped".into());
        self.publish(&inner);
    }

    pub fn restart(&self) {
        self.stop();
        self.start();
    }

    fn send(&self, cmd: &ToServer) -> Result<(), String> {
        let mut inner = self.lock();
        let stdin = inner.stdin.as_mut().ok_or("the server is not running")?;
        writeln!(stdin, "{}", serde_json::to_string(cmd).expect("serializes"))
            .and_then(|()| stdin.flush())
            .map_err(|e| format!("the server is not listening: {e}"))
    }

    /// Run a console line in the server (the reply arrives as a `console` log line).
    pub fn console(&self, line: &str) -> Result<(), String> {
        {
            let mut inner = self.lock();
            self.log(&mut inner, "console", format!("> {line}"));
        }
        self.send(&ToServer::Console { line: line.into() })
    }

    /// Tell a running server to re-read tabg.toml.
    pub fn reload(&self) -> Result<(), String> {
        self.send(&ToServer::Reload)
    }

    pub fn is_running(&self) -> bool {
        self.lock().child.is_some()
    }

    fn spawn(&self, inner: &mut Inner) {
        inner.generation += 1;
        let generation = inner.generation;
        inner.status.retry_at = None;
        if let Err(e) = crate::ipc::load(&self.paths) {
            inner.status.error = Some(e.clone());
            inner.status.state = RunState::Stopped;
            inner.want_running = false;
            self.log(inner, "host", format!("can't start: {e}"));
            self.publish(inner);
            return;
        }
        inner.status.error = None;
        let mut cmd = (self.spawner)();
        cmd.stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped());
        let mut child = match cmd.spawn() {
            Ok(c) => c,
            Err(e) => {
                inner.status.error = Some(format!("could not start the server process: {e}"));
                inner.want_running = false;
                self.publish(inner);
                return;
            }
        };
        let stdout = child.stdout.take().expect("piped");
        let stderr = child.stderr.take().expect("piped");
        inner.stdin = child.stdin.take();
        inner.child = Some(child);
        inner.run_started = Some(Instant::now());
        inner.status.state = RunState::Starting;
        inner.status.started_at = Some(now());
        inner.status.phase = "starting".into();
        self.log(inner, "host", "server starting".into());
        self.publish(inner);

        let me = self.clone();
        std::thread::spawn(move || {
            for line in BufReader::new(stdout).lines() {
                let Ok(line) = line else { break };
                me.on_stdout(generation, &line);
            }
            me.on_exit(generation);
        });
        let me = self.clone();
        std::thread::spawn(move || {
            let log_path = me.paths.logs().join("server.log");
            let _ = std::fs::create_dir_all(me.paths.logs());
            let mut file = std::fs::OpenOptions::new()
                .create(true)
                .append(true)
                .open(&log_path)
                .ok();
            for line in BufReader::new(stderr).lines() {
                let Ok(line) = line else { break };
                if let Some(f) = file.as_mut() {
                    let _ = writeln!(f, "{line}");
                }
                let mut inner = me.lock();
                if inner.generation != generation {
                    continue;
                }
                me.log(&mut inner, "server", strip_timestamp(&strip_ansi(&line)));
            }
        });
    }

    fn on_stdout(&self, generation: u64, line: &str) {
        let Ok(event) = serde_json::from_str::<Event>(line) else {
            return;
        };
        let mut ban = None;
        {
            let mut inner = self.lock();
            if inner.generation != generation {
                return;
            }
            match event {
                Event::Status(Status::JoinCode { code }) => {
                    inner.status.state = RunState::Running;
                    self.log(&mut inner, "host", format!("join code: {code}"));
                    inner.status.join_code = Some(code);
                }
                Event::Status(Status::Listening { port, .. }) => {
                    inner.status.state = RunState::Running;
                    inner.status.direct = Some(format!("port {port}"));
                    self.log(&mut inner, "host", format!("listening on UDP port {port}"));
                }
                Event::Players { phase, players } => {
                    inner.status.phase = phase;
                    inner.status.players = players;
                }
                Event::Reply { line, reply } => {
                    let _ = line;
                    self.log(&mut inner, "console", reply);
                }
                Event::Room(e) => {
                    inner.store.record(&e);
                    let _ = inner.store.save();
                    if let RoomEvent::Banned { id, .. } = &e {
                        ban = Some(id.clone());
                    }
                    let source = if matches!(e, RoomEvent::Chat { .. }) {
                        "chat"
                    } else {
                        "game"
                    };
                    self.log(&mut inner, source, describe(&e));
                }
            }
            self.publish(&inner);
        }
        if let Some(id) = ban
            && let Err(e) = self.persist_ban(&id)
        {
            self.host_log(format!("could not save the ban: {e}"));
        }
    }

    /// Bans made in-game (`/ban`, console) are written into tabg.toml.
    fn persist_ban(&self, id: &str) -> Result<(), String> {
        let text = std::fs::read_to_string(self.paths.config()).map_err(|e| e.to_string())?;
        let mut config = ServerToml::parse(&text)?;
        if config.players.banned.iter().any(|b| b == id) {
            return Ok(());
        }
        config.players.banned.push(id.to_owned());
        crate::apply::write_config(&self.paths, &config, "ban")
    }

    fn on_exit(&self, generation: u64) {
        let mut inner = self.lock();
        if inner.generation != generation {
            return;
        }
        let status = inner
            .child
            .as_mut()
            .and_then(|c| c.wait().ok())
            .map_or_else(|| "unknown".to_owned(), |s| s.to_string());
        inner.child = None;
        inner.stdin = None;
        inner.status.players.clear();
        inner.status.join_code = None;
        inner.status.direct = None;
        if !inner.want_running {
            inner.status.state = RunState::Stopped;
            self.publish(&inner);
            return;
        }
        // Crash: keep the log, then restart after a pause that grows with repeated crashes.
        let stable = inner
            .run_started
            .is_some_and(|t| t.elapsed() >= STABLE_AFTER);
        if stable {
            inner.backoff = Duration::from_secs(1);
        }
        let at = now();
        let log_file = self.paths.logs().join(format!("crash-{at}.log"));
        let tail: Vec<String> = inner
            .log
            .iter()
            .rev()
            .take(200)
            .rev()
            .map(|l| format!("[{}] {}", l.source, l.text))
            .collect();
        let _ = std::fs::create_dir_all(self.paths.logs());
        let _ = std::fs::write(&log_file, tail.join("\n"));
        inner.status.restarts += 1;
        inner.status.last_crash = Some(Crash {
            at,
            reason: format!("server exited ({status})"),
            log_file: log_file.display().to_string(),
        });
        inner.status.state = RunState::Restarting;
        let wait = inner.backoff;
        inner.backoff = (inner.backoff * 2).min(MAX_BACKOFF);
        inner.status.retry_at = Some(at + wait.as_secs());
        self.log(
            &mut inner,
            "host",
            format!(
                "server exited ({status}); restarting in {}s",
                wait.as_secs()
            ),
        );
        self.publish(&inner);
        drop(inner);
        let me = self.clone();
        std::thread::spawn(move || {
            std::thread::sleep(wait);
            let mut inner = me.lock();
            if inner.want_running && inner.child.is_none() && inner.generation == generation {
                me.spawn(&mut inner);
            }
        });
    }
}

/// Drop the tracing timestamp at the start of a log line (the log view shows its own time).
fn strip_timestamp(s: &str) -> String {
    let b = s.as_bytes();
    if b.len() > 20
        && b[4] == b'-'
        && b[7] == b'-'
        && b[10] == b'T'
        && let Some(end) = s.find('Z')
    {
        return s[end + 1..].trim_start().to_owned();
    }
    s.to_owned()
}

fn strip_ansi(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    let mut chars = s.chars();
    while let Some(c) = chars.next() {
        if c == '\x1b' {
            for c in chars.by_ref() {
                if c.is_ascii_alphabetic() {
                    break;
                }
            }
        } else {
            out.push(c);
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn strips_colors_and_timestamps() {
        assert_eq!(strip_ansi("\x1b[32mINFO\x1b[0m hi"), "INFO hi");
        assert_eq!(
            strip_timestamp("2026-11-04T23:01:12.836746Z  INFO tabg_utp::host: signed in"),
            "INFO tabg_utp::host: signed in"
        );
        assert_eq!(strip_timestamp("plain"), "plain");
    }

    #[test]
    fn describes_events() {
        assert_eq!(
            describe(&RoomEvent::Killed {
                victim: "b".into(),
                killer: Some("a".into()),
                ring: false
            }),
            "a killed b"
        );
        assert_eq!(
            describe(&RoomEvent::Won {
                team: 1,
                players: vec!["a".into(), "c".into()]
            }),
            "a & c won!"
        );
    }
}
