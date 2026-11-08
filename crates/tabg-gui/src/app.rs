//! The window's state and frame: top bar, tabs, status bar, dialogs, and everything that happens
//! off the UI thread.

use std::collections::VecDeque;
use std::path::PathBuf;
use std::sync::mpsc::{Receiver, Sender, channel};
use std::time::{Duration, Instant, SystemTime};

use chrono::{DateTime, Datelike, Local};
use eframe::egui::{
    self, Align, Context, CornerRadius, Frame, Id, Layout, Margin, Rect, RichText, Stroke, Ui,
    UiBuilder, ViewportCommand, pos2, vec2,
};
use tabg_game::PlayerInfo;
use tabg_host::ServerToml;
use tabg_host::apply::{self, Applied};
use tabg_host::backup::{self, Backup};
use tabg_host::paths::now;
use tabg_host::presets::{self, Preset};
use tabg_host::setup::Install;
use tabg_host::share::{self, CatalogEntry, Joined};
use tabg_host::store::PlayerRecord;
use tabg_host::supervisor::{HostStatus, LogLine, RunState, Update};
use tabg_web::api::Grant;
use tokio::sync::broadcast::{self, error::TryRecvError};

use crate::Host;
use crate::draft::{self, Items, Section};
use crate::tabs::{self, Tab, TabState};
use crate::theme::*;
use crate::widgets::*;
use crate::wizard::Wizard;

/// Log lines the window keeps.
const LOG_LINES: usize = 2000;
const NOTICE_FOR: Duration = Duration::from_secs(8);

pub enum Screen {
    Wizard(Wizard),
    Main,
}

/// Work that finished off the UI thread.
pub enum Msg {
    Applied(Result<Applied, String>),
    Failed(String),
    Done,
    LifecycleDone,
    Map(Box<MapData>),
    Catalog(Vec<CatalogEntry>),
    History(Vec<PlayerRecord>),
    Share(Result<serde_json::Value, String>, Vec<String>),
    Picked(Pick, Option<PathBuf>),
    JoinProgress(String),
    JoinDone(Result<Joined, String>),
}

/// What a file dialog was opened for.
#[derive(Clone, Copy, PartialEq, Eq)]
pub enum Pick {
    Dll,
    ServerFolder,
    ClientFolder,
    WizardServerFolder,
}

/// What the map data says (or the game's defaults before setup).
#[derive(Clone, Debug, Default)]
pub struct MapData {
    pub items: Items,
    pub times: Vec<f32>,
    pub sizes: Vec<f32>,
    pub speeds: Vec<f32>,
}

impl MapData {
    fn from_rules(items: Items, ring: &tabg_game::ring::RingRules) -> Self {
        Self {
            items,
            times: ring.ring_times.clone(),
            sizes: ring.sizes.clone(),
            speeds: ring.speeds.clone(),
        }
    }
}

fn load_map(world: Option<PathBuf>) -> MapData {
    let mut settings = tabg_game::RoomSettings::default();
    let mut items = Items::new();
    if let Some(world) = world.and_then(|p| tabg_world::World::load(&p).ok()) {
        items = world
            .items
            .iter()
            .filter_map(|(k, v)| Some((k.parse().ok()?, v.clone())))
            .collect();
        let _ = tabg_server::apply_world(&mut settings, &world);
    }
    MapData::from_rules(items, &settings.ring)
}

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum Tone {
    Normal,
    Bad,
}

pub struct Notice {
    pub text: String,
    pub tone: Tone,
    pub at: Instant,
}

pub enum Dialog {
    Stop,
    Restart,
    Ban {
        id: String,
        name: String,
    },
    SaveRestart {
        next: Box<ServerToml>,
        reason: String,
        body: String,
    },
    Restore {
        id: String,
        when: String,
    },
    CloseWindow,
    Folders {
        server: String,
        client: String,
    },
    Level(usize),
}

/// What the top bar shows.
#[derive(Clone, Debug, PartialEq)]
pub enum BarState {
    SetupNeeded,
    Refused(String),
    Stopped,
    Starting,
    Crashed,
    Lobby,
    Running,
}

pub fn bar_state(status: &HostStatus, ready: bool) -> BarState {
    match status.state {
        RunState::Stopped => match &status.error {
            Some(e) => BarState::Refused(e.clone()),
            None if !ready => BarState::SetupNeeded,
            None => BarState::Stopped,
        },
        RunState::Starting => BarState::Starting,
        RunState::Restarting => BarState::Crashed,
        RunState::Running if status.phase == "lobby" => BarState::Lobby,
        RunState::Running => BarState::Running,
    }
}

pub fn is_bot(p: &PlayerInfo) -> bool {
    p.id.starts_with("bot:")
}

pub fn short_id(id: &str) -> String {
    let chars: Vec<char> = id.chars().collect();
    if chars.len() <= 14 {
        return id.to_owned();
    }
    let head: String = chars[..9].iter().collect();
    let tail: String = chars[chars.len() - 3..].iter().collect();
    format!("{head}…{tail}")
}

fn local(at: u64) -> DateTime<Local> {
    DateTime::from_timestamp(i64::try_from(at).unwrap_or(0), 0)
        .unwrap_or_default()
        .with_timezone(&Local)
}

pub fn clock(at: u64) -> String {
    local(at).format("%H:%M:%S").to_string()
}

pub fn clock_short(at: u64) -> String {
    local(at).format("%H:%M").to_string()
}

/// "3 min ago", "Yesterday 22:14", "3 days ago".
pub fn ago(at: u64) -> String {
    let seconds = now().saturating_sub(at);
    let then = local(at);
    let today = Local::now();
    let days = today.num_days_from_ce() - then.num_days_from_ce();
    match (seconds, days) {
        (0..=59, _) => "Just now".into(),
        (60..=3599, _) => format!("{} min ago", seconds / 60),
        (_, 0) => format!("{} h ago", seconds / 3600),
        (_, 1) => format!("Yesterday {}", then.format("%H:%M")),
        _ => format!("{days} days ago"),
    }
}

pub fn uptime(started: u64) -> String {
    let s = now().saturating_sub(started);
    format!("{}:{:02}", s / 3600, (s % 3600) / 60)
}

fn minutes_seconds(s: u64) -> String {
    format!("{}:{:02}", s / 60, s % 60)
}

pub fn capitalized(s: &str) -> String {
    let mut c = s.chars();
    match c.next() {
        Some(first) => first.to_uppercase().collect::<String>() + c.as_str(),
        None => String::new(),
    }
}

fn config_stamp(path: &std::path::Path) -> Option<SystemTime> {
    std::fs::metadata(path).and_then(|m| m.modified()).ok()
}

pub struct Gui {
    pub host: Host,
    pub ctx: Context,
    tx: Sender<Msg>,
    rx: Receiver<Msg>,
    updates: broadcast::Receiver<Update>,
    pub status: HostStatus,
    /// Tests show made-up players instead of a live server.
    pub frozen_status: bool,
    pub install: Install,
    pub log: VecDeque<LogLine>,
    pub saved: ServerToml,
    pub draft: ServerToml,
    pub config_error: Option<String>,
    config_stamp: Option<SystemTime>,
    last_tick: Option<Instant>,
    pub presets: Vec<Preset>,
    pub backups: Vec<Backup>,
    pub history: Vec<PlayerRecord>,
    pub map: MapData,
    map_source: Option<Option<PathBuf>>,
    pub catalog: Option<Vec<CatalogEntry>>,
    pub screen: Screen,
    pub tab: Tab,
    pub notice: Option<Notice>,
    pub dialog: Option<Dialog>,
    pub applying: bool,
    lifecycle_busy: bool,
    pub password: String,
    closing: bool,
    pub t: TabState,
}

impl Gui {
    pub fn new(host: Host, ctx: Context) -> Self {
        let gui = Self::offline(host, ctx);
        gui.bg(|h| match share::load_catalog(&h.app.paths) {
            Ok((catalog, _)) => Msg::Catalog(catalog),
            // Offline: mods can't be checked against the catalog, which is fine until sharing.
            Err(_) => Msg::Done,
        });
        let wake = gui.host.app.supervisor.subscribe();
        let ctx = gui.ctx.clone();
        gui.host.runtime.spawn(async move {
            let mut wake = wake;
            while !matches!(wake.recv().await, Err(broadcast::error::RecvError::Closed)) {
                ctx.request_repaint();
            }
        });
        gui
    }

    /// Everything but the background work that reaches outside this machine.
    pub fn offline(host: Host, ctx: Context) -> Self {
        let (tx, rx) = channel();
        let supervisor = &host.app.supervisor;
        let updates = supervisor.subscribe();
        let status = supervisor.status();
        let log = supervisor.logs(LOG_LINES).into();
        let paths = host.app.paths.clone();
        let install = Install::load(&paths);
        let (saved, config_error) = match apply::read_config(&paths) {
            Ok(c) => (c, None),
            Err(e) => (ServerToml::default(), Some(e)),
        };
        let screen = if install.ready() {
            Screen::Main
        } else {
            Screen::Wizard(Wizard::welcome())
        };
        let password = saved.game.password.clone();
        let t = TabState {
            preset: saved.preset.clone(),
            ..Default::default()
        };
        let mut gui = Self {
            draft: saved.clone(),
            saved,
            config_error,
            config_stamp: config_stamp(&paths.config()),
            last_tick: None,
            presets: presets::all(&paths.presets()),
            backups: backup::list(&paths),
            history: Vec::new(),
            map: load_map(None),
            map_source: None,
            catalog: None,
            screen,
            tab: Tab::Overview,
            notice: None,
            dialog: None,
            applying: false,
            lifecycle_busy: false,
            password,
            closing: false,
            t,
            tx,
            rx,
            updates,
            status,
            frozen_status: false,
            install,
            log,
            host,
            ctx,
        };
        gui.refresh_map();
        gui
    }

    pub fn sender(&self) -> Sender<Msg> {
        self.tx.clone()
    }

    /// Run `f` off the UI thread; its message arrives in a later frame.
    pub fn bg(&self, f: impl FnOnce(&Host) -> Msg + Send + 'static) {
        let (tx, ctx, host) = (self.tx.clone(), self.ctx.clone(), self.host.clone());
        std::thread::spawn(move || {
            let msg = f(&host);
            let _ = tx.send(msg);
            ctx.request_repaint();
        });
    }

    pub fn tell(&mut self, text: impl Into<String>) {
        self.notice = Some(Notice {
            text: text.into(),
            tone: Tone::Normal,
            at: Instant::now(),
        });
    }

    pub fn complain(&mut self, text: impl Into<String>) {
        self.notice = Some(Notice {
            text: text.into(),
            tone: Tone::Bad,
            at: Instant::now(),
        });
    }

    fn refresh_map(&mut self) {
        let world = self.install.world.clone();
        if self.map_source.as_ref() == Some(&world) {
            return;
        }
        self.map_source = Some(world.clone());
        self.bg(move |_| Msg::Map(Box::new(load_map(world))));
    }

    /// Take in the config on disk; forms keep what the host is in the middle of editing.
    pub fn reload_config(&mut self) {
        let paths = &self.host.app.paths;
        self.config_stamp = config_stamp(&paths.config());
        match apply::read_config(paths) {
            Ok(new) => {
                for s in Section::ALL {
                    if draft::same(s, &self.saved, &self.draft) {
                        draft::copy_section(s, &new, &mut self.draft);
                    }
                }
                if self.saved.game.password != new.game.password || !self.t.password_focused {
                    self.password.clone_from(&new.game.password);
                }
                self.saved = new;
                self.config_error = None;
            }
            Err(e) => self.config_error = Some(e),
        }
        self.backups = backup::list(paths);
    }

    fn tick(&mut self) {
        if self
            .last_tick
            .is_some_and(|t| t.elapsed() < Duration::from_secs(1))
        {
            return;
        }
        self.last_tick = Some(Instant::now());
        let paths = self.host.app.paths.clone();
        if config_stamp(&paths.config()) != self.config_stamp {
            self.reload_config();
        }
        self.install = Install::load(&paths);
        self.refresh_map();
        if self.tab == Tab::Players {
            self.bg(|h| Msg::History(h.app.supervisor.player_history()));
        }
    }

    fn on_log(&mut self, line: LogLine) {
        if self.log.len() >= LOG_LINES {
            self.log.pop_front();
        }
        if line.source == "host"
            && line.text.starts_with("setup")
            && let Screen::Wizard(w) = &mut self.screen
        {
            w.on_setup_line(&line.text);
        }
        self.log.push_back(line);
    }

    fn poll(&mut self) {
        while let Ok(msg) = self.rx.try_recv() {
            self.on_msg(msg);
        }
        loop {
            match self.updates.try_recv() {
                Ok(Update::Log(line)) => self.on_log(line),
                Ok(Update::Status(_)) => {}
                Err(TryRecvError::Lagged(_)) => {
                    let seen = self.log.back().map_or(0, |l| l.seq);
                    for line in self.host.app.supervisor.logs_after(seen, LOG_LINES) {
                        self.on_log(line);
                    }
                }
                Err(_) => break,
            }
        }
        if !self.frozen_status
            && let Some(status) = self.host.app.supervisor.try_status()
        {
            self.status = status;
        }
        self.tick();
        if self
            .notice
            .as_ref()
            .is_some_and(|n| n.at.elapsed() > NOTICE_FOR)
        {
            self.notice = None;
        }
        self.ctx.request_repaint_after(Duration::from_millis(500));
    }

    fn on_msg(&mut self, msg: Msg) {
        match msg {
            Msg::Applied(result) => {
                self.applying = false;
                self.reload_config();
                match result {
                    Ok(Applied::Saved) => self.tell("Saved."),
                    Ok(Applied::Reloaded) => {
                        self.tell("Saved. The running server took the change.")
                    }
                    Ok(Applied::Restarted) => {
                        self.tell(
                            "Saved and restarted. Everyone was disconnected and can rejoin now.",
                        );
                    }
                    Err(e) => {
                        // Mods save on every click, so a refused one must not stay ticked.
                        self.discard_section(Section::Mods);
                        self.complain(e);
                    }
                }
            }
            Msg::Failed(e) => self.complain(e),
            Msg::Done => {}
            Msg::LifecycleDone => self.lifecycle_busy = false,
            Msg::Map(map) => self.map = *map,
            Msg::Catalog(catalog) => self.catalog = Some(catalog),
            Msg::History(history) => self.history = history,
            Msg::Share(result, mods) => self.on_share(result, mods),
            Msg::Picked(pick, path) => self.on_picked(pick, path),
            Msg::JoinProgress(line) => {
                if let Screen::Wizard(w) = &mut self.screen {
                    w.on_join_line(line);
                }
            }
            Msg::JoinDone(result) => {
                if let Screen::Wizard(w) = &mut self.screen {
                    w.on_join_done(result);
                }
            }
        }
    }

    pub fn pick(&self, what: Pick) {
        self.bg(move |_| {
            let dialog = rfd::FileDialog::new();
            let path = if what == Pick::Dll {
                dialog
                    .set_title("Add a DLL")
                    .add_filter("BepInEx plugin", &["dll"])
                    .pick_file()
            } else {
                dialog.set_title("Choose a folder").pick_folder()
            };
            Msg::Picked(what, path)
        });
    }

    fn on_picked(&mut self, pick: Pick, path: Option<PathBuf>) {
        let Some(path) = path else {
            return;
        };
        match pick {
            Pick::Dll => {
                self.draft.mods.client.push(path.display().to_string());
                self.save_section(Section::Mods);
            }
            Pick::ServerFolder | Pick::ClientFolder => {
                if let Some(Dialog::Folders { server, client }) = &mut self.dialog {
                    let slot = if pick == Pick::ServerFolder {
                        server
                    } else {
                        client
                    };
                    *slot = path.display().to_string();
                }
            }
            Pick::WizardServerFolder => self.wizard_server_folder(path),
        }
    }

    /// Apply `next` off the UI thread (a restart can take seconds).
    pub fn apply(&mut self, next: ServerToml, reason: impl Into<String>) {
        if self.config_error.is_some() {
            self.complain("Fix tabg.toml first, so tabg doesn't overwrite it.");
            return;
        }
        self.applying = true;
        let reason = reason.into();
        self.bg(move |h| {
            Msg::Applied(
                apply::apply(&h.app.paths, &h.app.supervisor, &next, &reason)
                    .map_err(|e| e.to_string()),
            )
        });
    }

    pub fn online(&self) -> usize {
        self.status.players.len()
    }

    /// Apply `next`, asking first when that throws players off.
    pub fn apply_confirmed(&mut self, next: ServerToml, reason: impl Into<String>, what: String) {
        if self.status.state != RunState::Stopped
            && self.saved.needs_restart(&next)
            && self.online() > 0
        {
            let n = self.online();
            self.dialog = Some(Dialog::SaveRestart {
                next: Box::new(next),
                reason: reason.into(),
                body: format!(
                    "{what} restarts the server. The {n} players online get disconnected and can \
                     rejoin right after."
                ),
            });
        } else {
            self.apply(next, reason);
        }
    }

    pub fn save_section(&mut self, section: Section) {
        let next = draft::merged(section, &self.draft, &self.saved);
        let labels = draft::section_changes(section, &self.saved, &next, &self.map.items);
        let what = format!("Changing {}", draft::sentence(&labels).to_lowercase());
        self.apply_confirmed(next, section.reason(), what);
    }

    pub fn discard_section(&mut self, section: Section) {
        draft::copy_section(section, &self.saved, &mut self.draft);
    }

    fn lifecycle(&mut self, f: impl FnOnce(&tabg_host::Supervisor) + Send + 'static) {
        if self.lifecycle_busy {
            return;
        }
        self.lifecycle_busy = true;
        self.bg(move |h| {
            f(&h.app.supervisor);
            Msg::LifecycleDone
        });
    }

    pub fn start_server(&mut self) {
        self.lifecycle(tabg_host::Supervisor::start);
    }

    pub fn stop_server(&mut self) {
        self.lifecycle(tabg_host::Supervisor::stop);
    }

    pub fn restart_server(&mut self) {
        self.lifecycle(tabg_host::Supervisor::restart);
    }

    /// Run a console line; `said` goes in the log as something the host did.
    pub fn console(&self, line: String, said: Option<String>) {
        self.bg(move |h| {
            let sup = &h.app.supervisor;
            if let Err(e) = sup.console(&line) {
                return Msg::Failed(capitalized(&e));
            }
            if let Some(said) = said {
                sup.host_log(said);
            }
            Msg::Done
        });
    }

    /// Whether taking a bot out has to go through the setting: in the lobby the server tops the
    /// bots back up to `game.bots`, so kicking one would only replace it.
    fn bots_refill(&self) -> bool {
        self.status.phase == "lobby" && self.saved.game.bots > 0
    }

    pub fn kick(&mut self, p: &PlayerInfo) {
        if is_bot(p) && self.bots_refill() {
            let mut next = self.saved.clone();
            next.game.bots -= 1;
            self.apply(next, format!("removing {}", p.name));
            return;
        }
        let verb = if is_bot(p) { "removed" } else { "kicked" };
        self.console(
            format!("kick #{}", p.index),
            Some(format!("You {verb} {}", p.name)),
        );
    }

    pub fn remove_all_bots(&mut self, bots: &[PlayerInfo]) {
        if self.bots_refill() {
            let mut next = self.saved.clone();
            next.game.bots = 0;
            self.apply(next, "removing all bots");
            return;
        }
        for b in bots {
            self.kick(b);
        }
    }

    /// Add or remove someone in the admin list.
    pub fn set_admin(&mut self, who: String, name: String, on: bool) {
        self.applying = true;
        self.bg(move |h| {
            let grant = Grant { who, on };
            let reason = if on {
                format!("making {name} an admin")
            } else {
                format!("removing admin {name}")
            };
            let result =
                tabg_web::api::grant_because(&h.app, &grant, |c| &mut c.players.admins, &reason);
            Msg::Applied(result)
        });
    }

    /// Add or remove someone in the ban list; a ban also throws them out now.
    pub fn set_ban(&mut self, who: String, name: String, on: bool) {
        self.applying = true;
        self.bg(move |h| {
            let grant = Grant {
                who: who.clone(),
                on,
            };
            let reason = if on {
                format!("banning {name}")
            } else {
                format!("unbanning {name}")
            };
            let result =
                tabg_web::api::grant_because(&h.app, &grant, |c| &mut c.players.banned, &reason);
            if on && result.is_ok() {
                let _ = h.app.supervisor.console(&format!("kick {who}"));
            }
            h.app.supervisor.host_log(if on {
                format!("You banned {name}")
            } else {
                format!("You unbanned {name}")
            });
            Msg::Applied(result)
        });
    }

    // ------------------------------------------------------------------------------------- UI

    pub fn draw(&mut self, ui: &mut Ui) {
        self.poll();
        let full = ui.max_rect();
        ui.painter().rect_filled(full, 0.0, BG);
        match &self.screen {
            Screen::Wizard(_) => self.wizard(ui),
            Screen::Main => self.main_screen(ui, full),
        }
        self.dialogs(ui);
        self.window_close(ui);
    }

    fn window_close(&mut self, ui: &Ui) {
        if !ui.ctx().input(|i| i.viewport().close_requested()) || self.closing {
            return;
        }
        if self.online() > 0 {
            ui.ctx().send_viewport_cmd(ViewportCommand::CancelClose);
            self.dialog = Some(Dialog::CloseWindow);
        }
    }

    fn main_screen(&mut self, ui: &mut Ui, full: Rect) {
        let top = Rect::from_min_size(full.min, vec2(full.width(), 54.0));
        let strip = Rect::from_min_max(
            pos2(full.left(), top.bottom()),
            pos2(full.right(), top.bottom() + 52.0),
        );
        let status = Rect::from_min_max(pos2(full.left(), full.bottom() - 32.0), full.max);
        self.top_bar(ui, top);
        self.tab_strip(ui, strip);
        self.status_bar(ui, status);

        let mut bottom = status.top();
        let form = match self.tab {
            Tab::Rules => Some(Section::Rules),
            Tab::Server => Some(Section::Server),
            _ => None,
        };
        if let Some(section) = form
            && !draft::same(section, &self.draft, &self.saved)
        {
            let bar =
                Rect::from_min_max(pos2(full.left(), bottom - 44.0), pos2(full.right(), bottom));
            bottom = bar.top();
            self.unsaved_bar(ui, bar, section);
        }
        let content = Rect::from_min_max(
            pos2(full.left(), strip.bottom()),
            pos2(full.right(), bottom),
        );
        let body = content.shrink2(vec2(12.0, 10.0));
        let enabled = self.config_error.is_none();
        within(ui, body, |ui| {
            ui.add_enabled_ui(enabled, |ui| {
                if let Some(e) = self.config_error.clone() {
                    ui.colored_label(DANGER, format!("tabg.toml can't be read: {e}"));
                }
                match self.tab {
                    Tab::Overview => tabs::overview::show(self, ui),
                    Tab::Players => tabs::players::show(self, ui),
                    Tab::GameMode => tabs::game_mode::show(self, ui),
                    Tab::Rules => tabs::rules::show(self, ui),
                    Tab::Loadout => tabs::loadout::show(self, ui),
                    Tab::Mods => tabs::mods::show(self, ui),
                    Tab::Console => tabs::console::show(self, ui),
                    Tab::Server => tabs::server::show(self, ui),
                }
            });
        });
    }

    /// The left half of the top bar: state in words.
    pub fn bar_text(&self) -> (Option<&'static str>, String) {
        let s = &self.status;
        let n = s.players.len();
        let max = self.saved.game.max_players;
        let up = s
            .started_at
            .map_or_else(String::new, |t| format!(" · up {}", uptime(t)));
        match bar_state(s, self.install.ready()) {
            BarState::SetupNeeded => (
                Some("Setup needed"),
                "Read the map from your game files before the first start".into(),
            ),
            BarState::Refused(e) => (Some("Couldn't start"), capitalized(&e)),
            BarState::Stopped => (Some("Stopped"), "Nobody can join".into()),
            BarState::Starting => (
                Some("Starting"),
                "Loading the map, friends can join in a moment".into(),
            ),
            BarState::Crashed => {
                let wait = s.retry_at.map_or(0, |t| t.saturating_sub(now()));
                (
                    Some("Crashed"),
                    format!("Restarting in {wait} s · restart {}", s.restarts),
                )
            }
            BarState::Lobby => {
                let g = &self.saved.game;
                let mut text = format!(
                    "Lobby · {n} / {max} players · starts with {}",
                    g.players_to_start
                );
                // The room counts from when the server came up.
                if g.force_start_after > 0.0
                    && let Some(started) = s.started_at
                {
                    let elapsed = now().saturating_sub(started);
                    let at = (g.force_start_after - 2.0).max(0.0) as u64;
                    if at > elapsed {
                        text += &format!(", or anyway in {}", minutes_seconds(at - elapsed));
                    }
                }
                (None, text)
            }
            BarState::Running => {
                let phase = match s.phase.as_str() {
                    "countdown" => "Countdown",
                    "ended" => "Match ended",
                    _ => "Match in progress",
                };
                (None, format!("{phase} · {n} / {max} players{up}"))
            }
        }
    }

    fn top_bar(&mut self, ui: &mut Ui, rect: Rect) {
        ui.painter().rect_filled(rect, 0.0, HEADER);
        ui.painter()
            .hline(rect.x_range(), rect.bottom() - 0.5, Stroke::new(1.0, LINE));
        let state = bar_state(&self.status, self.install.ready());
        let (bold, rest) = self.bar_text();
        let busy = self.lifecycle_busy;
        let confirm = self.online() > 0;
        within(ui, rect.shrink2(vec2(16.0, 0.0)), |ui| {
            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                ui.spacing_mut().item_spacing.x = 8.0;
                ui.add_enabled_ui(!busy, |ui| {
                    ui.with_layout(Layout::right_to_left(Align::Center), |ui| match &state {
                        BarState::SetupNeeded => {
                            if primary(ui, "Run setup") {
                                self.begin_setup(false);
                            }
                        }
                        BarState::Stopped => {
                            if primary(ui, "Start server") {
                                self.start_server();
                            }
                        }
                        BarState::Refused(e) => {
                            if plain(ui, "Start") {
                                self.start_server();
                            }
                            if e.contains("setup") && primary(ui, "Run setup again") {
                                self.begin_setup(false);
                            }
                        }
                        BarState::Starting => {
                            if warn(ui, "Stop") {
                                self.stop_server();
                            }
                        }
                        BarState::Crashed => {
                            if warn(ui, "Stop") {
                                self.stop_server();
                            }
                            if plain(ui, "Restart now") {
                                self.restart_server();
                            }
                        }
                        BarState::Lobby | BarState::Running => {
                            if warn(ui, "Stop") {
                                if confirm {
                                    self.dialog = Some(Dialog::Stop);
                                } else {
                                    self.stop_server();
                                }
                            }
                            if plain(ui, "Restart") {
                                if confirm {
                                    self.dialog = Some(Dialog::Restart);
                                } else {
                                    self.restart_server();
                                }
                            }
                            if state == BarState::Lobby && primary(ui, "Start match now") {
                                self.console("start".into(), Some("You started the match".into()));
                            }
                        }
                    });
                });
                let (line, _) = ui.allocate_exact_size(vec2(1.0, 22.0), egui::Sense::hover());
                ui.painter()
                    .vline(line.center().x, line.y_range(), Stroke::new(1.0, LINE));
                if plain(ui, "Copy") {
                    ui.ctx().copy_text(self.password.clone());
                    self.tell("Password copied.");
                }
                let edit = input(ui, &mut self.password, 140.0, "No password");
                self.t.password_focused = edit.has_focus();
                if edit.lost_focus() && self.password != self.saved.game.password {
                    let mut next = self.saved.clone();
                    next.game.password.clone_from(&self.password);
                    self.apply(next, "a password change");
                }
                ui.label(text("Password").color(MUTED));
                if let Some(code) = self.status.join_code.clone() {
                    if plain(ui, "Copy code") {
                        ui.ctx().copy_text(code.clone());
                        self.tell("Join code copied.");
                    }
                    ui.label(RichText::new(code).font(semi(16.0)).color(TEXT));
                    ui.label(text("Join code").color(MUTED));
                } else if let Some(direct) = self.status.direct.clone() {
                    ui.label(text(format!("Direct · {direct}")).color(MUTED));
                }
                ui.with_layout(Layout::left_to_right(Align::Center), |ui| {
                    if let Some(bold) = bold {
                        let color = if matches!(state, BarState::Refused(_) | BarState::Crashed) {
                            DANGER
                        } else {
                            TEXT
                        };
                        ui.label(RichText::new(bold).font(semi(15.0)).color(color));
                    }
                    ui.add(egui::Label::new(text(rest).color(MUTED)).truncate());
                    if state == BarState::Crashed
                        && let Some(c) = self.status.last_crash.clone()
                        && ui.link("Crash log").clicked()
                    {
                        ui.ctx()
                            .open_url(egui::OpenUrl::new_tab(format!("file://{}", c.log_file)));
                    }
                });
            });
        });
    }

    fn tab_strip(&mut self, ui: &mut Ui, rect: Rect) {
        ui.painter()
            .hline(rect.x_range(), rect.bottom() - 0.5, Stroke::new(1.0, LINE));
        within(ui, rect.shrink2(vec2(10.0, 0.0)), |ui| {
            ui.with_layout(Layout::left_to_right(Align::Center), |ui| {
                ui.spacing_mut().item_spacing.x = 4.0;
                for t in Tab::ALL {
                    if tab(ui, t.title(), self.tab == t).clicked() {
                        self.tab = t;
                        self.last_tick = None;
                    }
                }
            });
        });
    }

    fn status_bar(&mut self, ui: &mut Ui, rect: Rect) {
        ui.painter().rect_filled(rect, 0.0, HEADER);
        ui.painter()
            .hline(rect.x_range(), rect.top() + 0.5, Stroke::new(1.0, LINE));
        let (message, color) = match &self.notice {
            Some(n) => (
                n.text.clone(),
                if n.tone == Tone::Bad { DANGER } else { TEXT },
            ),
            None => (
                self.backups.first().map_or_else(
                    || "No backups yet".to_owned(),
                    |b| {
                        format!(
                            "Backup saved {}, {}",
                            ago(b.created).to_lowercase(),
                            b.reason
                        )
                    },
                ),
                MUTED,
            ),
        };
        let name = self.saved.name.clone();
        within(ui, rect.shrink2(vec2(16.0, 0.0)), |ui| {
            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                ui.label(muted(name));
                ui.with_layout(Layout::left_to_right(Align::Center), |ui| {
                    ui.add(
                        egui::Label::new(RichText::new(message).font(small()).color(color))
                            .truncate(),
                    );
                });
            });
        });
    }

    fn unsaved_bar(&mut self, ui: &mut Ui, rect: Rect, section: Section) {
        ui.painter().rect_filled(rect, 0.0, WARN_BG);
        ui.painter().hline(
            rect.x_range(),
            rect.top() + 0.5,
            Stroke::new(1.0, WARN_LINE),
        );
        let next = draft::merged(section, &self.draft, &self.saved);
        let labels = draft::section_changes(section, &self.saved, &next, &self.map.items);
        let found = next.validate();
        let problems = found.len();
        let restart = self.status.state != RunState::Stopped && self.saved.needs_restart(&next);
        let n = labels.len();
        let count = match (n, problems) {
            (1, 0) => "1 unsaved change".to_owned(),
            (n, 0) => format!("{n} unsaved changes"),
            (1, p) => format!("1 change, {p} to fix"),
            (n, p) => format!("{n} changes, {p} to fix"),
        };
        let online = self.online();
        let consequence = if restart {
            if online > 0 {
                format!("Saving restarts the server and disconnects the {online} players online.")
            } else {
                "Saving restarts the server.".to_owned()
            }
        } else if self.status.state == RunState::Stopped {
            "They apply when the server starts.".to_owned()
        } else if n == 1 {
            "It applies without a restart, from the next match.".to_owned()
        } else {
            "They apply without a restart, from the next match.".to_owned()
        };
        let detail = if let Some(p) = found.first() {
            format!("Fix this first: {}. {consequence}", p.message)
        } else {
            format!("{}. {consequence}", draft::sentence(&labels))
        };
        let applying = self.applying;
        within(ui, rect.shrink2(vec2(16.0, 0.0)), |ui| {
            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                let label = if restart { "Save and restart" } else { "Save" };
                ui.add_enabled_ui(problems == 0 && !applying, |ui| {
                    if primary(ui, label) {
                        self.save_section(section);
                    }
                });
                if plain(ui, "Discard") {
                    self.discard_section(section);
                }
                ui.with_layout(Layout::left_to_right(Align::Center), |ui| {
                    ui.label(RichText::new(count).font(semi(15.0)).color(TEXT));
                    ui.add_space(4.0);
                    ui.add(egui::Label::new(text(detail).color(MUTED)).truncate());
                });
            });
        });
    }

    fn dialogs(&mut self, ui: &mut Ui) {
        let Some(d) = self.dialog.take() else {
            return;
        };
        let keep = self.show_dialog(ui.ctx().clone(), d);
        // A click inside may have opened another one (the file picker's result, say).
        if self.dialog.is_none() {
            self.dialog = keep;
        }
    }

    fn show_dialog(&mut self, ctx: Context, d: Dialog) -> Option<Dialog> {
        match d {
            Dialog::Stop => {
                let n = self.online();
                confirm_dialog(
                    &ctx,
                    "Stop the server?",
                    &format!(
                        "The {n} players online get disconnected. They can rejoin with the same \
                         password when you start again."
                    ),
                    "Keep running",
                    "Stop server",
                )
                .run(|| self.stop_server(), Dialog::Stop)
            }
            Dialog::Restart => {
                let n = self.online();
                confirm_dialog(
                    &ctx,
                    "Restart the server?",
                    &format!("The {n} players online get disconnected and can rejoin right after."),
                    "Cancel",
                    "Restart",
                )
                .run(|| self.restart_server(), Dialog::Restart)
            }
            Dialog::Ban { id, name } => {
                let answer = confirm_dialog(
                    &ctx,
                    &format!("Ban {name}?"),
                    "tabg kicks them now and refuses them on every rejoin. You can unban them in \
                     Players.",
                    "Cancel",
                    "Ban",
                );
                match answer {
                    Answer::Yes => {
                        self.set_ban(id, name, true);
                        None
                    }
                    Answer::No => None,
                    Answer::Open => Some(Dialog::Ban { id, name }),
                }
            }
            Dialog::SaveRestart { next, reason, body } => {
                let answer = confirm_dialog(
                    &ctx,
                    "Save and restart?",
                    &body,
                    "Cancel",
                    "Save and restart",
                );
                match answer {
                    Answer::Yes => {
                        self.apply(*next, reason);
                        None
                    }
                    Answer::No => None,
                    Answer::Open => Some(Dialog::SaveRestart { next, reason, body }),
                }
            }
            Dialog::Restore { id, when } => {
                let answer = confirm_dialog(
                    &ctx,
                    "Restore this backup?",
                    &format!(
                        "The settings and player history go back to {}. tabg saves a backup of \
                         the current state first. This may restart the server.",
                        when.to_lowercase()
                    ),
                    "Cancel",
                    "Restore",
                );
                match answer {
                    Answer::Yes => {
                        self.restore_backup(id);
                        None
                    }
                    Answer::No => None,
                    Answer::Open => Some(Dialog::Restore { id, when }),
                }
            }
            Dialog::CloseWindow => {
                let n = self.online();
                confirm_dialog(
                    &ctx,
                    "Close tabg?",
                    &format!(
                        "Closing the window stops the server. The {n} players online get \
                         disconnected."
                    ),
                    "Keep running",
                    "Stop and close",
                )
                .run(
                    || {
                        self.closing = true;
                        ctx.send_viewport_cmd(ViewportCommand::Close);
                    },
                    Dialog::CloseWindow,
                )
            }
            Dialog::Folders { server, client } => self.folders_dialog(&ctx, server, client),
            Dialog::Level(level) => tabs::loadout::level_dialog(self, &ctx, level),
        }
    }

    fn restore_backup(&mut self, id: String) {
        self.applying = true;
        self.bg(move |h| {
            let (paths, sup) = (&h.app.paths, &h.app.supervisor);
            let before = apply::read_config(paths).ok();
            if let Err(e) = backup::restore(paths, &id) {
                return Msg::Applied(Err(e));
            }
            let result = apply::read_config(paths).and_then(|after| {
                if !sup.is_running() {
                    return Ok(Applied::Saved);
                }
                if before.is_none_or(|b| b.needs_restart(&after)) {
                    sup.restart();
                    return Ok(Applied::Restarted);
                }
                sup.reload().map(|()| Applied::Reloaded)
            });
            Msg::Applied(result)
        });
    }

    fn folders_dialog(
        &mut self,
        ctx: &Context,
        mut server: String,
        mut client: String,
    ) -> Option<Dialog> {
        let mut close = false;
        let mut save = false;
        let mut browse = None;
        dialog_frame(ctx, "Choose folders", 520.0, |ui, footer| match footer {
            None => {
                label(ui, text("Where tabg finds the game on this PC."));
                ui.add_space(4.0);
                ui.label(muted("TABG Dedicated Server"));
                ui.horizontal(|ui| {
                    input(ui, &mut server, 400.0, "Not found");
                    if plain(ui, "Browse…") {
                        browse = Some(Pick::ServerFolder);
                    }
                });
                ui.label(muted("TABG"));
                ui.horizontal(|ui| {
                    input(ui, &mut client, 400.0, "Not found");
                    if plain(ui, "Browse…") {
                        browse = Some(Pick::ClientFolder);
                    }
                });
            }
            Some(()) => {
                if primary(ui, "Save") {
                    save = true;
                }
                if plain(ui, "Cancel") {
                    close = true;
                }
            }
        });
        if let Some(what) = browse {
            self.pick(what);
        }
        if save {
            let mut install = Install::load(&self.host.app.paths);
            let path = |s: &str| (!s.trim().is_empty()).then(|| PathBuf::from(s.trim()));
            install.server_dir = path(&server);
            install.client_dir = path(&client);
            if let Some(d) = &install.server_dir
                && !tabg_host::steam::is_server(d)
            {
                self.complain(format!(
                    "{} isn't a TABG Dedicated Server folder.",
                    d.display()
                ));
                return Some(Dialog::Folders { server, client });
            }
            if let Err(e) = install.save(&self.host.app.paths) {
                self.complain(e.to_string());
                return Some(Dialog::Folders { server, client });
            }
            self.install = install;
            self.tell("Folders saved.");
            return None;
        }
        (!close).then_some(Dialog::Folders { server, client })
    }

    /// Open the folder dialog with what's known.
    pub fn open_folders(&mut self) {
        let show = |p: &Option<PathBuf>| {
            p.as_ref()
                .map_or_else(String::new, |d| d.display().to_string())
        };
        self.dialog = Some(Dialog::Folders {
            server: show(&self.install.server_dir),
            client: show(&self.install.client_dir),
        });
    }
}

impl eframe::App for Gui {
    fn ui(&mut self, ui: &mut Ui, _frame: &mut eframe::Frame) {
        self.draw(ui);
    }
}

pub enum Answer {
    Yes,
    No,
    Open,
}

impl Answer {
    fn run(self, yes: impl FnOnce(), keep: Dialog) -> Option<Dialog> {
        match self {
            Self::Yes => {
                yes();
                None
            }
            Self::No => None,
            Self::Open => Some(keep),
        }
    }
}

/// A modal card: `f` is called for the body with `None`, for the footer with `Some(())`.
pub fn dialog_frame(
    ctx: &Context,
    title: &str,
    width: f32,
    mut f: impl FnMut(&mut Ui, Option<()>),
) {
    let radius = CornerRadius::same(RADIUS);
    egui::Modal::new(Id::new(("tabg-dialog", title)))
        .backdrop_color(egui::Color32::from_black_alpha(70))
        .frame(
            Frame::new()
                .fill(PANEL)
                .corner_radius(radius)
                .stroke(Stroke::new(1.0, LINE))
                .shadow(ctx.global_style().visuals.popup_shadow),
        )
        .show(ctx, |ui| {
            ui.set_width(width);
            ui.spacing_mut().item_spacing.y = 0.0;
            Frame::new()
                .inner_margin(Margin {
                    left: 18,
                    right: 18,
                    top: 18,
                    bottom: 16,
                })
                .show(ui, |ui| {
                    ui.set_width(width - 36.0);
                    ui.spacing_mut().item_spacing.y = 8.0;
                    ui.label(RichText::new(title).font(semi(16.0)).color(TEXT));
                    f(ui, None);
                });
            let (bar, _) = ui.allocate_exact_size(vec2(width, 52.0), egui::Sense::hover());
            ui.painter().rect_filled(
                bar,
                CornerRadius {
                    nw: 0,
                    ne: 0,
                    sw: RADIUS,
                    se: RADIUS,
                },
                HEADER,
            );
            ui.painter()
                .hline(bar.x_range(), bar.top() + 0.5, Stroke::new(1.0, LINE));
            ui.scope_builder(
                UiBuilder::new().max_rect(bar.shrink2(vec2(14.0, 0.0))),
                |ui| {
                    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                        ui.spacing_mut().item_spacing.x = 8.0;
                        f(ui, Some(()));
                    });
                },
            );
        });
}

pub fn confirm_dialog(ctx: &Context, title: &str, body: &str, no: &str, yes: &str) -> Answer {
    let mut answer = Answer::Open;
    dialog_frame(ctx, title, 376.0, |ui, footer| match footer {
        None => {
            label(ui, text(body).color(MUTED));
        }
        Some(()) => {
            if button(ui, yes, Kind::Danger).clicked() {
                answer = Answer::Yes;
            }
            if plain(ui, no) {
                answer = Answer::No;
            }
        }
    });
    if ctx.input(|i| i.key_pressed(egui::Key::Escape)) {
        answer = Answer::No;
    }
    answer
}
