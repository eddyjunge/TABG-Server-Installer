//! The live log and the command line.

use eframe::egui::{
    self, Align, Color32, CornerRadius, Frame, Key, Layout, Margin, Modifiers, Order, Rect,
    RichText, Sense, Ui, pos2, vec2,
};
use tabg_host::supervisor::LogLine;

use crate::app::{Gui, clock};
use crate::theme::*;
use crate::widgets::*;

use super::LogFilter;
use super::overview::tidy;

/// Console commands: (name, arguments, what it does).
const COMMANDS: [(&str, &str, &str); 9] = [
    ("start", "", "Starts the match from the lobby"),
    ("say", "text", "Sends a message to everyone"),
    ("players", "", "Lists who's online"),
    ("kick", "player", "Throws a player out"),
    ("ban", "player", "Throws a player out for good"),
    (
        "give",
        "player item-id [qty]",
        "Drops an item next to a player",
    ),
    ("heal", "player", "Heals to full"),
    (
        "weather",
        "[name|auto]",
        "Sets the weather for everyone with Visuals",
    ),
    ("vehicles", "", "Lists the vehicles on the map"),
];

fn suggestions(line: &str) -> Vec<usize> {
    let line = line.trim_start();
    if line.is_empty() || line.contains(' ') {
        return Vec::new();
    }
    let lower = line.to_ascii_lowercase();
    COMMANDS
        .iter()
        .enumerate()
        .filter(|(_, (name, _, _))| name.starts_with(&lower) && *name != lower)
        .map(|(i, _)| i)
        .collect()
}

pub fn passes(filter: LogFilter, l: &LogLine) -> bool {
    let yours = l.source == "host" && l.text.starts_with("You ");
    match filter {
        LogFilter::All => true,
        LogFilter::Game => l.source == "game",
        LogFilter::Chat => l.source == "chat",
        LogFilter::Actions => yours,
        LogFilter::Commands => l.source == "console",
        LogFilter::Technical => l.source == "server" || (l.source == "host" && !yours),
    }
}

fn tag(l: &LogLine) -> &str {
    match l.source.as_str() {
        "console" => "cmd",
        "server" => "tech",
        other => other,
    }
}

fn toolbar(gui: &mut Gui, ui: &mut Ui) {
    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
        input(ui, &mut gui.t.log_search, 200.0, "Search");
        ui.spacing_mut().item_spacing.x = 2.0;
        for (filter, name) in [
            (LogFilter::Technical, "Technical"),
            (LogFilter::Commands, "Commands"),
            (LogFilter::Actions, "Your actions"),
            (LogFilter::Chat, "Chat"),
            (LogFilter::Game, "Game"),
        ] {
            let on = gui.t.log_filter == filter;
            if chip(ui, name, on).clicked() {
                gui.t.log_filter = if on { LogFilter::All } else { filter };
            }
        }
    });
}

fn log_view(gui: &Gui, ui: &mut Ui) {
    let search = gui.t.log_search.trim().to_lowercase();
    let lines: Vec<&LogLine> = gui
        .log
        .iter()
        .filter(|l| passes(gui.t.log_filter, l))
        .filter(|l| search.is_empty() || l.text.to_lowercase().contains(&search))
        .collect();
    let height = 26.0;
    egui::ScrollArea::vertical()
        .id_salt("log")
        .auto_shrink([false, false])
        .stick_to_bottom(true)
        .show_rows(ui, height, lines.len(), |ui, range| {
            ui.spacing_mut().item_spacing.y = 0.0;
            for l in &lines[range] {
                let (rect, _) =
                    ui.allocate_exact_size(vec2(ui.available_width(), height), Sense::hover());
                if l.text.starts_with("inventory check") {
                    ui.painter()
                        .rect_filled(rect.shrink2(vec2(12.0, 0.0)), 0.0, WARN_BG);
                }
                cell(ui, rect, 16.0, 80.0, monospace(clock(l.at)).color(MUTED));
                cell(ui, rect, 102.0, 60.0, monospace(tag(l)).color(MUTED));
                cell(
                    ui,
                    rect,
                    168.0,
                    rect.width() - 180.0,
                    monospace(tidy(&l.text)),
                );
            }
        });
}

fn command_line(gui: &mut Gui, ui: &mut Ui, rect: Rect) {
    let suggested = suggestions(&gui.t.line);
    let mut selected = gui.t.suggestion.min(suggested.len().saturating_sub(1));
    let edit_id = ui.id().with("console-line");
    let focused = ui.ctx().memory(|m| m.has_focus(edit_id));
    if focused {
        ui.input_mut(|i| {
            if !suggested.is_empty() {
                if i.consume_key(Modifiers::NONE, Key::ArrowDown) {
                    selected = (selected + 1).min(suggested.len() - 1);
                }
                if i.consume_key(Modifiers::NONE, Key::ArrowUp) {
                    selected = selected.saturating_sub(1);
                }
                if i.consume_key(Modifiers::NONE, Key::Tab) {
                    gui.t.line = format!("{} ", COMMANDS[suggested[selected]].0);
                }
            } else {
                let history = gui.t.sent.len();
                if i.consume_key(Modifiers::NONE, Key::ArrowUp) && history > 0 {
                    let pos = gui.t.sent_pos.map_or(history - 1, |p| p.saturating_sub(1));
                    gui.t.sent_pos = Some(pos);
                    gui.t.line.clone_from(&gui.t.sent[pos]);
                }
                if i.consume_key(Modifiers::NONE, Key::ArrowDown)
                    && let Some(p) = gui.t.sent_pos
                {
                    if p + 1 < history {
                        gui.t.sent_pos = Some(p + 1);
                        gui.t.line.clone_from(&gui.t.sent[p + 1]);
                    } else {
                        gui.t.sent_pos = None;
                        gui.t.line.clear();
                    }
                }
            }
        });
    }
    gui.t.suggestion = selected;
    let mut response_rect = rect;
    let mut submit = false;
    within(ui, rect.shrink2(vec2(12.0, 0.0)), |ui| {
        ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
            let can = !gui.t.line.trim().is_empty();
            submit = ui.add_enabled_ui(can, |ui| primary(ui, "Run")).inner;
            ui.with_layout(Layout::left_to_right(Align::Center), |ui| {
                ui.label(monospace(">").color(MUTED));
                let width = ui.available_width() - 2.0;
                let edit = ui.add(
                    egui::TextEdit::singleline(&mut gui.t.line)
                        .id(edit_id)
                        .desired_width(width)
                        .min_size(vec2(width, CONTROL_H))
                        .margin(Margin::symmetric(8, 6))
                        .vertical_align(Align::Center)
                        .font(mono()),
                );
                response_rect = edit.rect;
                if edit.lost_focus() && ui.input(|i| i.key_pressed(Key::Enter)) {
                    submit = true;
                    edit.request_focus();
                }
            });
        });
    });
    let line = gui.t.line.trim().to_owned();
    if submit && !line.is_empty() {
        gui.t.sent.retain(|l| l != &line);
        gui.t.sent.push(line.clone());
        gui.t.sent_pos = None;
        gui.t.line.clear();
        gui.console(line, None);
    }
    if !suggested.is_empty() {
        let row_h = 30.0;
        let height = row_h * suggested.len() as f32 + 8.0;
        let top = response_rect.top() - height - 6.0;
        let mut picked = None;
        egui::Area::new(ui.id().with("console-suggestions"))
            .order(Order::Foreground)
            .fixed_pos(pos2(rect.left() + 16.0, top))
            .show(ui.ctx(), |ui| {
                Frame::popup(ui.style())
                    .fill(PANEL)
                    .corner_radius(CornerRadius::same(FIELD_RADIUS))
                    .inner_margin(Margin::same(3))
                    .show(ui, |ui| {
                        ui.spacing_mut().item_spacing.y = 0.0;
                        ui.set_width(rect.width() - 40.0);
                        for (i, c) in suggested.iter().enumerate() {
                            let (name, args, what) = COMMANDS[*c];
                            let bg = if i == selected { SELECTED } else { PANEL };
                            let r = row(ui, row_h, Some(bg), |ui, rect| {
                                let mut job = egui::text::LayoutJob::default();
                                let bold = egui::TextFormat::simple(semi(13.5), Color32::BLACK);
                                job.append(name, 0.0, bold);
                                job.append(
                                    &format!(" {args}"),
                                    0.0,
                                    egui::TextFormat::simple(mono(), MUTED),
                                );
                                cell(ui, rect, 10.0, rect.width() * 0.6, job);
                                let (x, label) = (
                                    rect.width() - 12.0,
                                    RichText::new(what).font(small()).color(MUTED),
                                );
                                let area = Rect::from_min_max(
                                    pos2(rect.left() + rect.width() * 0.5, rect.top()),
                                    pos2(rect.left() + x, rect.bottom()),
                                );
                                within(ui, area, |ui| {
                                    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                                        ui.label(label);
                                    });
                                });
                            });
                            if r.clicked() {
                                picked = Some(*c);
                            }
                        }
                    });
            });
        if let Some(c) = picked {
            gui.t.line = format!("{} ", COMMANDS[c].0);
            ui.ctx().memory_mut(|m| m.request_focus(edit_id));
        }
    }
}

pub fn show(gui: &mut Gui, ui: &mut Ui) {
    card_fill(
        ui,
        "",
        |_| {},
        |ui| {
            ui.spacing_mut().item_spacing.y = 0.0;
            card_header(ui, |ui| {
                ui.label(RichText::new("Log").font(semi(14.0)).color(MUTED));
                toolbar(gui, ui);
            });
            let area = ui.available_rect_before_wrap();
            let foot = 52.0;
            let list = Rect::from_min_max(area.min, pos2(area.right(), area.bottom() - foot));
            within(ui, list, |ui| {
                ui.add_space(8.0);
                log_view(gui, ui);
            });
            let footer = Rect::from_min_max(pos2(area.left(), list.bottom()), area.max);
            ui.painter().hline(
                footer.x_range(),
                footer.top() + 0.5,
                egui::Stroke::new(1.0, LINE_SOFT),
            );
            command_line(gui, ui, footer);
        },
    );
}

#[cfg(test)]
mod tests {
    use super::*;

    fn line(source: &str, text: &str) -> LogLine {
        LogLine {
            seq: 1,
            at: 0,
            source: source.into(),
            text: text.into(),
        }
    }

    #[test]
    fn filters_sort_lines_the_way_the_labels_say() {
        let yours = line("host", "You kicked griefer2");
        let tech = line("host", "server starting");
        let chat = line("chat", "Mika: who took my scope");
        assert!(passes(LogFilter::Actions, &yours));
        assert!(!passes(LogFilter::Technical, &yours));
        assert!(passes(LogFilter::Technical, &tech));
        assert!(passes(LogFilter::Technical, &line("server", "INFO hello")));
        assert!(passes(LogFilter::Chat, &chat));
        assert!(!passes(LogFilter::Game, &chat));
        assert!(passes(LogFilter::All, &chat));
    }

    #[test]
    fn suggestions_stop_once_the_command_is_typed() {
        let names =
            |s: &str| -> Vec<&str> { suggestions(s).into_iter().map(|i| COMMANDS[i].0).collect() };
        assert_eq!(names("g"), ["give"]);
        assert!(names("give").is_empty());
        assert!(names("give me").is_empty());
        assert!(names("").is_empty());
        assert_eq!(names("s"), ["start", "say"]);
    }
}
