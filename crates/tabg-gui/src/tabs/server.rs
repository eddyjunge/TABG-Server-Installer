//! The server's identity and network, its backups, and where the game data comes from.

use eframe::egui::{self, Rect, Ui, pos2, vec2};
use tabg_host::config::{NetMode, Problem, unlisted_words};

use crate::app::{Dialog, Gui, ago, capitalized};
use crate::draft::{self, Section};
use crate::theme::*;
use crate::widgets::*;
use crate::wizard::Wizard;

const LABEL: f32 = 268.0;

fn field_error(ui: &mut Ui, problems: &[Problem], field: &str, message: Option<String>) {
    if problems.iter().any(|p| p.field == field) {
        let m = message.unwrap_or_else(|| {
            problems
                .iter()
                .find(|p| p.field == field)
                .map_or_else(String::new, |p| capitalized(&p.message))
        });
        hint(ui, LABEL, text(m).color(DANGER));
    }
}

/// In the design's words: which word is the problem.
fn word_message(text: &str) -> Option<String> {
    let bad = unlisted_words(text);
    let first = bad.first()?;
    Some(format!(
        "“{}” isn't on TABG's word list. Public servers may only use those words.",
        capitalized(first)
    ))
}

fn tag(ui: &mut Ui, s: &str) {
    ui.label(muted(s));
}

fn server_card(gui: &mut Gui, ui: &mut Ui) {
    let next = draft::merged(Section::Server, &gui.draft, &gui.saved);
    let problems = next.validate();
    let mut d = std::mem::take(&mut gui.draft);
    card_fill(
        ui,
        "Server",
        |_| {},
        |ui| {
            egui::ScrollArea::vertical()
            .id_salt("server-form")
            .auto_shrink([false, false])
            .show(ui, |ui| {
                padded(ui, |ui| {
                    let bad = |f: &str| problems.iter().any(|p| p.field == f);
                    let w = LABEL;
                    let wide = (ui.available_width() - w - 150.0).clamp(160.0, 290.0);
                    field(ui, "Name", w, |ui| {
                        input_with(ui, &mut d.name, wide, "", bad("name"), body());
                        tag(ui, "restarts the server");
                    });
                    field_error(ui, &problems, "name", word_message(&d.name).filter(|_| d.network.public));
                    field(ui, "Description", w, |ui| {
                        input_with(ui, &mut d.description, wide, "", bad("description"), body());
                        tag(ui, "restarts the server");
                    });
                    field_error(
                        ui,
                        &problems,
                        "description",
                        word_message(&d.description).filter(|_| d.network.public),
                    );
                    field(ui, "Password", w, |ui| {
                        input(ui, &mut d.game.password, wide, "");
                        tag(ui, "applies at once");
                    });
                    hint(ui, w, muted("Friends need it to join. Leave it empty to let anyone in."));
                    field(ui, "Max players", w, |ui| {
                        number(ui, "max", &mut d.game.max_players, 88.0, 1.0);
                        tag(ui, "restarts the server");
                    });
                    field_error(ui, &problems, "game.max_players", None);
                    field(ui, "Show in server browser", w, |ui| {
                        check(ui, "Show in server browser", &mut d.network.public);
                    });
                    hint(ui, w, muted("Anyone can find it. Name and description must use TABG's word list."));
                    ui.add_space(6.0);
                    hline(ui, LINE_SOFT);
                    ui.add_space(6.0);
                    field(ui, "How friends join", w, |ui| {
                        select(
                            ui,
                            "net",
                            &mut d.network.mode,
                            &[
                                (NetMode::Relay, "Join code"),
                                (NetMode::Direct, "Direct IP"),
                            ],
                            120.0,
                        );
                        tag(ui, "restarts the server");
                    });
                    if d.network.mode == NetMode::Relay {
                        hint(ui, w, muted("No port forwarding. Friends type the code into TABG."));
                        field(ui, "Relay region", w, |ui| {
                            input(ui, &mut d.network.region, 160.0, "Automatic");
                        });
                    } else {
                        hint(ui, w, muted("Friends connect to your IP. The UDP port must be open."));
                        field(ui, "UDP port", w, |ui| {
                            number(ui, "port", &mut d.network.port, 100.0, 1.0);
                        });
                        field_error(ui, &problems, "network.port", None);
                        field(ui, "Open the port with UPnP", w, |ui| {
                            check(ui, "Open the port with UPnP", &mut d.network.upnp);
                        });
                    }
                });
            });
        },
    );
    gui.draft = d;
}

fn backups_card(gui: &mut Gui, ui: &mut Ui) {
    card_fill(
        ui,
        "Backups",
        |_| {},
        |ui| {
            ui.spacing_mut().item_spacing.y = 0.0;
            let area = ui.available_rect_before_wrap();
            let foot = 36.0;
            let list = Rect::from_min_max(area.min, pos2(area.right(), area.bottom() - foot));
            let footer = Rect::from_min_max(pos2(area.left(), list.bottom()), area.max);
            within(ui, list, |ui| {
                ui.spacing_mut().item_spacing.y = 0.0;
                row(ui, 36.0, None, |ui, rect| {
                    cell(ui, rect, 16.0, 100.0, muted("When"));
                    cell(ui, rect, 195.0, 100.0, muted("Why"));
                });
                hline(ui, LINE_SOFT);
                let backups = gui.backups.clone();
                if backups.is_empty() {
                    ui.add_space(12.0);
                    ui.horizontal(|ui| {
                        ui.add_space(16.0);
                        ui.label(muted("Nothing yet. tabg saves one before every change."));
                    });
                }
                egui::ScrollArea::vertical()
                    .id_salt("backups")
                    .auto_shrink([false, false])
                    .show(ui, |ui| {
                        ui.spacing_mut().item_spacing.y = 0.0;
                        for b in &backups {
                            let when = ago(b.created);
                            row(ui, 36.0, None, |ui, rect| {
                                cell(ui, rect, 16.0, 170.0, text(&when));
                                cell(
                                    ui,
                                    rect,
                                    195.0,
                                    rect.width() - 300.0,
                                    text(capitalized(&b.reason)).color(MUTED),
                                );
                                cell_actions(ui, rect, 16.0, |ui| {
                                    if plain(ui, "Restore") {
                                        gui.dialog = Some(Dialog::Restore {
                                            id: b.id.clone(),
                                            when: when.clone(),
                                        });
                                    }
                                });
                            });
                        }
                    });
            });
            ui.painter().hline(
                footer.x_range(),
                footer.top() + 0.5,
                egui::Stroke::new(1.0, LINE_SOFT),
            );
            within(ui, footer.shrink2(vec2(16.0, 0.0)), |ui| {
                ui.with_layout(egui::Layout::left_to_right(egui::Align::Center), |ui| {
                    ui.label(muted(format!(
                        "One before every change, last {} kept. Restoring may restart the server.",
                        tabg_host::backup::KEEP
                    )));
                });
            });
        },
    );
}

fn state_text(ready: bool) -> egui::RichText {
    if ready {
        text("Ready").color(GOOD)
    } else {
        text("Missing").color(DANGER)
    }
}

fn data_card(gui: &mut Gui, ui: &mut Ui) {
    card(
        ui,
        "Game data",
        |_| {},
        |ui| {
            padded(ui, |ui| {
                let w = 132.0;
                let exists = |p: &Option<std::path::PathBuf>| {
                    p.as_deref().is_some_and(std::path::Path::exists)
                };
                field(ui, "Map data", w, |ui| {
                    ui.label(state_text(gui.install.ready()));
                });
                field(ui, "Brawl and Bomb", w, |ui| {
                    ui.label(state_text(
                        exists(&gui.install.brawl_world) && exists(&gui.install.bomb_world),
                    ));
                });
                ui.horizontal_top(|ui| {
                    let (rect, _) = ui.allocate_exact_size(vec2(w, 20.0), egui::Sense::hover());
                    ui.painter().text(
                        pos2(rect.left(), rect.center().y),
                        egui::Align2::LEFT_CENTER,
                        "Server folder",
                        body(),
                        TEXT,
                    );
                    let folder = gui
                        .install
                        .server_dir
                        .as_ref()
                        .map_or_else(|| "Not found".to_owned(), |d| d.display().to_string());
                    ui.add(egui::Label::new(monospace(folder)).wrap());
                });
                ui.add_space(6.0);
                ui.horizontal(|ui| {
                    if plain(ui, "Run setup again") {
                        gui.begin_setup(true);
                    }
                    if plain(ui, "Choose folders…") {
                        gui.open_folders();
                    }
                    if plain(ui, "Join a friend…") {
                        gui.screen = crate::app::Screen::Wizard(Wizard::join());
                    }
                });
                ui.label(muted("Run setup again after a game update"));
            });
        },
    );
}

pub fn show(gui: &mut Gui, ui: &mut Ui) {
    let area = ui.available_rect_before_wrap();
    let left_w = (area.width() * 0.54).floor();
    let left = Rect::from_min_size(area.min, vec2(left_w, area.height()));
    let right = Rect::from_min_max(pos2(left.right() + 10.0, area.top()), area.max);
    within(ui, left, |ui| server_card(gui, ui));
    let data_h = 240.0;
    let backups = Rect::from_min_max(
        right.min,
        pos2(right.right(), right.bottom() - data_h - 10.0),
    );
    let data = Rect::from_min_max(pos2(right.left(), backups.bottom() + 10.0), right.max);
    within(ui, backups, |ui| backups_card(gui, ui));
    within(ui, data, |ui| data_card(gui, ui));
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn names_the_offending_word() {
        let m = word_message("zzzz drops").unwrap();
        assert!(m.starts_with("“Zzzz” isn't"), "{m}");
        assert_eq!(word_message("tabg dev test"), None);
    }
}
