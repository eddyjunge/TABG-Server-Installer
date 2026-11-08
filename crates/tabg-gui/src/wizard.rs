//! First run: what do you want to do, read the map, or join a friend.

use std::path::PathBuf;

use eframe::egui::{
    self, Align, Color32, CornerRadius, Frame, Layout, Margin, Rect, RichText, Sense, Shape,
    Stroke, TextEdit, Ui, pos2, vec2,
};
use tabg_host::setup::Install;
use tabg_host::share::{self, Joined};

use crate::app::{Gui, Msg, Pick, Screen};
use crate::theme::*;
use crate::widgets::*;

const CARD: [f32; 2] = [426.0, 598.0];

/// The steps of the map export, in order: (a phrase in tabg's progress line, name, detail).
const STEPS: [(&str, &str, &str); 5] = [
    ("TABG Dedicated Server:", "Found TABG Dedicated Server", ""),
    (
        "preparing a copy",
        "Copied the server",
        "Your Steam install stays as it is",
    ),
    (
        "getting BepInEx",
        "Getting BepInEx",
        "Downloading the mod loader",
    ),
    ("loading the map", "Loading the map", ""),
    ("exporting the", "Exporting the Brawl and Bomb maps", ""),
];

/// Which step a progress line starts, if any.
pub fn step_of(line: &str) -> Option<usize> {
    STEPS
        .iter()
        .position(|(phrase, _, _)| line.contains(phrase))
}

pub struct Setup {
    /// Highest step reached.
    step: usize,
    lines: Vec<String>,
    show_log: bool,
    done: bool,
    error: Option<String>,
    force: bool,
}

pub enum Wizard {
    Welcome {
        join: bool,
    },
    Preparing(Setup),
    Missing {
        client_found: bool,
    },
    Join {
        code: String,
        lines: Vec<String>,
        busy: bool,
        error: Option<String>,
        joined: Option<Joined>,
    },
}

impl Wizard {
    pub fn welcome() -> Self {
        Self::Welcome { join: false }
    }

    pub fn join() -> Self {
        Self::Join {
            code: String::new(),
            lines: Vec::new(),
            busy: false,
            error: None,
            joined: None,
        }
    }

    pub fn on_setup_line(&mut self, text: &str) {
        let Self::Preparing(s) = self else {
            return;
        };
        if let Some(e) = text.strip_prefix("setup failed: ") {
            s.error = Some(e.to_owned());
            s.lines.push(format!("failed: {e}"));
            return;
        }
        let Some(line) = text.strip_prefix("setup: ") else {
            return;
        };
        s.lines.push(line.to_owned());
        if line.starts_with("done") {
            s.done = true;
        } else if let Some(step) = step_of(line) {
            s.step = s.step.max(step);
        }
    }

    pub fn on_join_line(&mut self, line: String) {
        if let Self::Join { lines, .. } = self {
            lines.push(line);
        }
    }

    pub fn on_join_done(&mut self, result: Result<Joined, String>) {
        if let Self::Join {
            busy,
            error,
            joined,
            ..
        } = self
        {
            *busy = false;
            match result {
                Ok(j) => *joined = Some(j),
                Err(e) => *error = Some(e),
            }
        }
    }
}

/// A centred card with a footer of buttons; `f` gets `None` for the body, `Some(())` for the
/// footer.
fn card_window(ui: &mut Ui, mut f: impl FnMut(&mut Ui, Option<()>)) {
    let full = ui.max_rect();
    let size = vec2(CARD[0], CARD[1].min(full.height() - 24.0));
    let rect = Rect::from_center_size(full.center(), size);
    let shadow = ui.visuals().popup_shadow;
    ui.painter()
        .add(shadow.as_shape(rect, CornerRadius::same(RADIUS)));
    ui.painter().rect(
        rect,
        CornerRadius::same(RADIUS),
        Color32::from_rgb(0xec, 0xec, 0xec),
        Stroke::new(1.0, FIELD_LINE),
        egui::StrokeKind::Inside,
    );
    let footer = Rect::from_min_max(pos2(rect.left(), rect.bottom() - 58.0), rect.max);
    ui.painter().rect_filled(
        footer.shrink(1.0),
        CornerRadius {
            nw: 0,
            ne: 0,
            sw: RADIUS - 1,
            se: RADIUS - 1,
        },
        HEADER,
    );
    ui.painter().hline(
        footer.x_range(),
        footer.top() + 0.5,
        Stroke::new(1.0, FIELD_LINE),
    );
    let body =
        Rect::from_min_max(rect.min, pos2(rect.right(), footer.top())).shrink2(vec2(21.0, 20.0));
    within(ui, body, |ui| {
        ui.set_clip_rect(body.expand(4.0));
        f(ui, None);
    });
    within(ui, footer.shrink2(vec2(21.0, 0.0)), |ui| {
        ui.with_layout(Layout::right_to_left(Align::Center), |ui| f(ui, Some(())));
    });
}

fn heading(ui: &mut Ui, title: &str) {
    ui.label(RichText::new(title).font(semi(19.0)).color(TEXT));
}

fn sub(ui: &mut Ui, t: &str) {
    label(ui, text(t).color(MUTED));
}

/// One choice on the first screen. Returns true when clicked.
fn choice(ui: &mut Ui, title: &str, detail: &str, selected: bool) -> bool {
    let (rect, response) = ui.allocate_exact_size(vec2(ui.available_width(), 72.0), Sense::click());
    response.widget_info(|| {
        egui::WidgetInfo::selected(egui::WidgetType::RadioButton, true, selected, title)
    });
    let (fill, line) = if selected {
        (
            Color32::from_rgb(0xf1, 0xf6, 0xff),
            Stroke::new(1.5, ACCENT),
        )
    } else if response.hovered() {
        (HOVER, Stroke::new(1.0, FIELD_LINE))
    } else {
        (PANEL, Stroke::new(1.0, FIELD_LINE))
    };
    let p = ui.painter();
    p.rect(
        rect,
        CornerRadius::same(RADIUS),
        fill,
        line,
        egui::StrokeKind::Inside,
    );
    let x = rect.left() + 56.0;
    p.text(
        pos2(x, rect.top() + 24.0),
        egui::Align2::LEFT_CENTER,
        title,
        semi(15.0),
        TEXT,
    );
    p.text(
        pos2(x, rect.top() + 48.0),
        egui::Align2::LEFT_CENTER,
        detail,
        small(),
        MUTED,
    );
    // The radio dot.
    let c = pos2(rect.left() + 28.0, rect.center().y);
    p.circle(
        c,
        8.0,
        PANEL,
        Stroke::new(1.5, if selected { ACCENT } else { FIELD_LINE }),
    );
    if selected {
        p.circle_filled(c, 4.0, ACCENT);
    }
    response.clicked()
}

fn step_row(ui: &mut Ui, state: StepState, title: &str, detail: &str) {
    let height = if detail.is_empty() || state == StepState::Waiting {
        30.0
    } else {
        46.0
    };
    let (rect, _) = ui.allocate_exact_size(vec2(ui.available_width(), height), Sense::hover());
    let c = pos2(rect.left() + 8.0, rect.top() + 14.0);
    let p = ui.painter();
    match state {
        StepState::Done => {
            p.add(Shape::line(
                vec![
                    pos2(c.x - 5.0, c.y),
                    pos2(c.x - 1.5, c.y + 3.5),
                    pos2(c.x + 5.0, c.y - 4.0),
                ],
                Stroke::new(1.6, GOOD),
            ));
        }
        StepState::Active => {
            p.circle_filled(c, 6.0, ACCENT);
        }
        StepState::Failed => {
            p.circle_filled(c, 6.0, DANGER);
        }
        StepState::Waiting => {
            p.circle_stroke(c, 5.0, Stroke::new(1.3, MUTED));
        }
    }
    p.text(
        pos2(rect.left() + 26.0, c.y),
        egui::Align2::LEFT_CENTER,
        title,
        body(),
        if state == StepState::Waiting {
            FAINT
        } else {
            TEXT
        },
    );
    if !detail.is_empty() && state != StepState::Waiting {
        p.text(
            pos2(rect.left() + 26.0, c.y + 20.0),
            egui::Align2::LEFT_CENTER,
            detail,
            small(),
            MUTED,
        );
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum StepState {
    Done,
    Active,
    Failed,
    Waiting,
}

fn log_box(ui: &mut Ui, lines: &[String], height: f32) {
    Frame::new()
        .fill(PANEL)
        .stroke(Stroke::new(1.0, LINE))
        .corner_radius(CornerRadius::same(FIELD_RADIUS))
        .inner_margin(Margin::same(8))
        .show(ui, |ui| {
            ui.set_width(ui.available_width());
            egui::ScrollArea::vertical()
                .max_height(height)
                .auto_shrink([false, false])
                .stick_to_bottom(true)
                .show(ui, |ui| {
                    for l in lines {
                        ui.add(egui::Label::new(muted(l.clone())).wrap());
                    }
                });
        });
}

impl Gui {
    /// Run the map export and show its progress (from `Screen::Main` too, after a game update).
    pub fn begin_setup(&mut self, force: bool) {
        let mut install = Install::load(&self.host.app.paths);
        install.detect();
        if install.server_dir.is_none() {
            self.screen = Screen::Wizard(Wizard::Missing {
                client_found: install.client_dir.is_some(),
            });
            return;
        }
        let _ = install.save(&self.host.app.paths);
        self.install = install;
        match self.host.app.start_setup(force) {
            Ok(()) => {
                self.screen = Screen::Wizard(Wizard::Preparing(Setup {
                    step: 0,
                    lines: Vec::new(),
                    show_log: false,
                    done: false,
                    error: None,
                    force,
                }));
            }
            Err(e) => self.complain(e),
        }
    }

    pub fn wizard_server_folder(&mut self, path: PathBuf) {
        if !tabg_host::steam::is_server(&path) {
            self.complain(format!(
                "{} isn't a TABG Dedicated Server folder.",
                path.display()
            ));
            return;
        }
        let mut install = Install::load(&self.host.app.paths);
        install.server_dir = Some(path);
        if let Err(e) = install.save(&self.host.app.paths) {
            self.complain(e.to_string());
            return;
        }
        self.begin_setup(false);
    }

    fn finish_wizard(&mut self) {
        self.install = Install::load(&self.host.app.paths);
        self.screen = Screen::Main;
    }

    fn start_join(&mut self, code: String) {
        let tx_paths = self.host.app.paths.clone();
        let (lines_tx, lines_rx) = std::sync::mpsc::channel::<String>();
        let forward = self.sender();
        let ctx = self.ctx.clone();
        std::thread::spawn(move || {
            for line in lines_rx {
                let _ = forward.send(Msg::JoinProgress(line));
                ctx.request_repaint();
            }
        });
        self.bg(move |_| {
            let result = share::join(&tx_paths, &code, &mut |m| {
                let _ = lines_tx.send(m.to_owned());
            });
            Msg::JoinDone(result)
        });
    }

    pub fn wizard(&mut self, ui: &mut Ui) {
        let Screen::Wizard(wizard) = std::mem::replace(&mut self.screen, Screen::Main) else {
            return;
        };
        let next = match wizard {
            Wizard::Welcome { join } => self.welcome(ui, join),
            Wizard::Preparing(s) => self.preparing(ui, s),
            Wizard::Missing { client_found } => self.missing(ui, client_found),
            Wizard::Join {
                code,
                lines,
                busy,
                error,
                joined,
            } => self.join_screen(ui, code, lines, busy, error, joined),
        };
        // A button may already have moved on (setup started, say); that decision wins.
        if matches!(self.screen, Screen::Main)
            && let Some(next) = next
            && !self.t.wizard_left
        {
            self.screen = Screen::Wizard(next);
        }
        self.t.wizard_left = false;
        if let Some(n) = &self.notice
            && matches!(self.screen, Screen::Wizard(_))
        {
            let rect = ui.max_rect();
            let bar = Rect::from_min_max(pos2(rect.left(), rect.bottom() - 32.0), rect.max);
            let color = if n.tone == crate::app::Tone::Bad {
                DANGER
            } else {
                TEXT
            };
            ui.painter().text(
                pos2(bar.left() + 16.0, bar.center().y),
                egui::Align2::LEFT_CENTER,
                n.text.clone(),
                small(),
                color,
            );
        }
    }

    fn welcome(&mut self, ui: &mut Ui, mut join: bool) -> Option<Wizard> {
        let mut go = false;
        card_window(ui, |ui, footer| match footer {
            None => {
                heading(ui, "Welcome to tabg");
                sub(ui, "What do you want to do?");
                ui.add_space(8.0);
                if choice(
                    ui,
                    "Host a server",
                    "Run a private TABG server for your friends",
                    !join,
                ) {
                    join = false;
                }
                ui.add_space(4.0);
                if choice(
                    ui,
                    "Join a friend",
                    "Use the share code a friend sent you",
                    join,
                ) {
                    join = true;
                }
                let bottom = ui.max_rect().bottom();
                let space = (bottom - ui.cursor().top() - 22.0).max(0.0);
                ui.add_space(space);
                ui.label(muted("Unofficial. Not affiliated with Landfall Games."));
            }
            Some(()) => {
                if primary(ui, "Continue") {
                    go = true;
                }
            }
        });
        if go {
            if join {
                return Some(Wizard::join());
            }
            self.begin_setup(false);
            if !matches!(self.screen, Screen::Main) {
                self.t.wizard_left = true;
                return None;
            }
        }
        Some(Wizard::Welcome { join })
    }

    fn preparing(&mut self, ui: &mut Ui, mut s: Setup) -> Option<Wizard> {
        let mut finished = false;
        let mut retry = false;
        let failed = s.error.is_some();
        let done = s.done;
        let step = s.step;
        card_window(ui, |ui, footer| match footer {
            None => {
                heading(ui, "Getting the map ready");
                sub(
                    ui,
                    "tabg reads the map from your game files once per game version.",
                );
                ui.add_space(8.0);
                for (i, (_, title, detail)) in STEPS.iter().enumerate() {
                    let state = if done || i < step {
                        StepState::Done
                    } else if i > step {
                        StepState::Waiting
                    } else if failed {
                        StepState::Failed
                    } else {
                        StepState::Active
                    };
                    step_row(
                        ui,
                        state,
                        title,
                        if state == StepState::Active || i == 1 {
                            detail
                        } else {
                            ""
                        },
                    );
                }
                ui.add_space(4.0);
                let (bar, _) =
                    ui.allocate_exact_size(vec2(ui.available_width(), 5.0), Sense::hover());
                ui.painter().rect_filled(bar, CornerRadius::same(3), LINE);
                let fraction = if done {
                    1.0
                } else {
                    (step as f32 + 0.5) / STEPS.len() as f32
                };
                let mut filled = bar;
                filled.set_width(bar.width() * fraction);
                ui.painter()
                    .rect_filled(filled, CornerRadius::same(3), ACCENT);
                if let Some(e) = &s.error {
                    ui.add_space(8.0);
                    label(ui, text(e.clone()).color(DANGER));
                }
                if s.show_log {
                    ui.add_space(8.0);
                    log_box(ui, &s.lines, 120.0);
                }
                if !done && !failed {
                    ui.ctx()
                        .request_repaint_after(std::time::Duration::from_millis(250));
                }
            }
            Some(()) => {
                ui.add_enabled_ui(done, |ui| {
                    if primary(ui, "Done") {
                        finished = true;
                    }
                });
                if failed && plain(ui, "Try again") {
                    retry = true;
                }
                if plain(ui, if s.show_log { "Hide log" } else { "Show log" }) {
                    s.show_log = !s.show_log;
                }
            }
        });
        if finished {
            self.finish_wizard();
            self.t.wizard_left = true;
            return None;
        }
        if retry {
            self.begin_setup(s.force);
            self.t.wizard_left = true;
            return None;
        }
        Some(Wizard::Preparing(s))
    }

    fn missing(&mut self, ui: &mut Ui, client_found: bool) -> Option<Wizard> {
        let mut retry = false;
        let mut choose = false;
        card_window(ui, |ui, footer| match footer {
            None => {
                heading(ui, "TABG Dedicated Server is missing");
                label(
                    ui,
                    text("tabg reads the map from this free Steam tool. It isn't the game itself."),
                );
                ui.add_space(6.0);
                for (i, line) in [
                    "In Steam, open Library and set the filter to Tools",
                    "Install TABG Dedicated Server",
                    "Come back and press Try again",
                ]
                .iter()
                .enumerate()
                {
                    ui.horizontal_top(|ui| {
                        ui.add_space(6.0);
                        ui.label(text(format!("{}.", i + 1)));
                        ui.add(egui::Label::new(text(*line)).wrap());
                    });
                }
                ui.add_space(6.0);
                if !client_found {
                    sub(
                        ui,
                        "TABG itself wasn't found either. That's fine for hosting.",
                    );
                }
            }
            Some(()) => {
                if primary(ui, "Try again") {
                    retry = true;
                }
                if plain(ui, "Choose folder…") {
                    choose = true;
                }
            }
        });
        if choose {
            self.pick(Pick::WizardServerFolder);
        }
        if retry {
            self.begin_setup(false);
            if !matches!(self.screen, Screen::Main) {
                self.t.wizard_left = true;
                return None;
            }
        }
        Some(Wizard::Missing { client_found })
    }

    #[allow(clippy::too_many_arguments)]
    fn join_screen(
        &mut self,
        ui: &mut Ui,
        mut code: String,
        lines: Vec<String>,
        mut busy: bool,
        mut error: Option<String>,
        joined: Option<Joined>,
    ) -> Option<Wizard> {
        let mut go = false;
        let mut launch = false;
        let mut back = false;
        let trimmed = code.trim().to_owned();
        let plain_code = !trimmed.is_empty() && !trimmed.starts_with("TABG1-");
        card_window(ui, |ui, footer| match footer {
            None => {
                if let Some(j) = &joined {
                    heading(ui, "You're ready");
                    sub(
                        ui,
                        "tabg made a modded copy of your TABG. Your Steam install stays as it is.",
                    );
                    ui.add_space(8.0);
                    match &j.join {
                        Some(c) => {
                            ui.label(muted("In the game, join with"));
                            ui.horizontal(|ui| {
                                ui.label(RichText::new(c.clone()).font(semi(20.0)).color(TEXT));
                                if plain(ui, "Copy") {
                                    ui.ctx().copy_text(c.clone());
                                }
                            });
                        }
                        None => sub(ui, "Ask your friend for the join code."),
                    }
                    ui.add_space(8.0);
                    sub(
                        ui,
                        "Keep Steam running, and start this copy from here rather than from Steam.",
                    );
                } else {
                    heading(ui, "Join a friend");
                    sub(ui, "Paste the share code your friend sent you.");
                    ui.add_space(8.0);
                    ui.add_enabled(
                        !busy,
                        TextEdit::multiline(&mut code)
                            .desired_rows(5)
                            .desired_width(f32::INFINITY)
                            .hint_text("TABG1-…")
                            .font(mono()),
                    );
                    if plain_code {
                        ui.add_space(6.0);
                        label(ui, text("That looks like a plain join code. You don't need tabg for it: open TABG and type it into the join code box.").color(MUTED));
                    }
                    if let Some(e) = &error {
                        ui.add_space(6.0);
                        label(ui, text(crate::app::capitalized(e)).color(DANGER));
                    }
                    if !lines.is_empty() {
                        ui.add_space(8.0);
                        log_box(ui, &lines, 180.0);
                    }
                }
            }
            Some(()) => {
                if joined.is_some() {
                    if primary(ui, "Start TABG") {
                        launch = true;
                    }
                } else {
                    ui.add_enabled_ui(!busy && trimmed.starts_with("TABG1-"), |ui| {
                        if primary(ui, if busy { "Working…" } else { "Continue" }) {
                            go = true;
                        }
                    });
                }
                ui.add_enabled_ui(!busy, |ui| {
                    if plain(ui, "Back") {
                        back = true;
                    }
                });
            }
        });
        if launch {
            let paths = self.host.app.paths.clone();
            self.bg(move |_| match share::launch(&paths) {
                Ok(()) => Msg::Done,
                Err(e) => Msg::Failed(crate::app::capitalized(&e)),
            });
            self.tell("Starting TABG. Keep Steam running.");
        }
        if go {
            busy = true;
            error = None;
            self.start_join(trimmed);
        }
        if back {
            if self.install.ready() {
                self.finish_wizard();
                self.t.wizard_left = true;
                return None;
            }
            return Some(Wizard::welcome());
        }
        Some(Wizard::Join {
            code,
            lines: if go { Vec::new() } else { lines },
            busy,
            error,
            joined,
        })
    }
}

pub fn already_running(ui: &mut Ui, url: &str) {
    let full = ui.max_rect();
    ui.painter().rect_filled(full, 0.0, BG);
    let area = full.shrink(24.0);
    within(ui, area, |ui| {
        ui.spacing_mut().item_spacing.y = 10.0;
        heading(ui, "tabg is already running");
        sub(
            ui,
            "Another tabg window or host is using this data folder, and there can only be one. Close that one first, or open its dashboard in your browser.",
        );
        ui.add_space(8.0);
        ui.horizontal(|ui| {
            if primary(ui, "Open in browser") {
                ui.ctx().open_url(egui::OpenUrl::new_tab(url));
            }
            if plain(ui, "Close") {
                ui.ctx().send_viewport_cmd(egui::ViewportCommand::Close);
            }
        });
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    /// tabg-host's progress lines drive the steps; a reworded line must not stall the screen.
    #[test]
    fn progress_lines_map_to_steps() {
        for (line, step) in [
            ("TABG Dedicated Server: /games/tabg", Some(0)),
            ("preparing a copy of the dedicated server", Some(1)),
            ("getting BepInEx 5.4.23.3", Some(2)),
            (
                "loading the map in the dedicated server (about a minute)",
                Some(3),
            ),
            ("exporting the Brawl map too", Some(4)),
            ("TABG: /games/tabg", None),
        ] {
            assert_eq!(step_of(line), step, "{line}");
        }
    }

    #[test]
    fn the_steps_only_move_forward() {
        let mut w = Wizard::Preparing(Setup {
            step: 0,
            lines: Vec::new(),
            show_log: false,
            done: false,
            error: None,
            force: false,
        });
        w.on_setup_line("setup: exporting the Brawl map too");
        w.on_setup_line("setup: preparing a copy of the dedicated server");
        let Wizard::Preparing(s) = &w else {
            panic!("still preparing")
        };
        assert_eq!(s.step, 4, "the Brawl export copies the server again");
        w.on_setup_line("setup failed: no such folder");
        let Wizard::Preparing(s) = &w else {
            panic!("still preparing")
        };
        assert_eq!(s.error.as_deref(), Some("no such folder"));
    }
}
