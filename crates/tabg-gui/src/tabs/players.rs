//! Everyone online or ever seen, one player's actions, and the admin and ban lists.

use eframe::egui::{self, Align, Layout, Rect, RichText, Sense, Ui, pos2, vec2};
use tabg_game::PlayerInfo;
use tabg_host::store::PlayerRecord;

use crate::app::{Dialog, Gui, ago, clock_short, is_bot, short_id};
use crate::theme::*;
use crate::widgets::*;

use super::overview::{health_text, suffix};

const ROW: f32 = 32.0;

fn matches_filter(filter: &str, name: &str, id: &str) -> bool {
    let f = filter.trim().to_lowercase();
    f.is_empty() || name.to_lowercase().contains(&f) || id.to_lowercase().contains(&f)
}

fn online_table(gui: &mut Gui, ui: &mut Ui) {
    let players: Vec<PlayerInfo> = gui
        .status
        .players
        .iter()
        .filter(|p| matches_filter(&gui.t.filter, &p.name, &p.id))
        .cloned()
        .collect();
    row(ui, 38.0, None, |ui, rect| {
        cell(ui, rect, 16.0, 40.0, muted("#"));
        cell(ui, rect, 100.0, 120.0, muted("Player"));
        cell(ui, rect, 322.0, 60.0, muted("Squad"));
        cell_right(ui, rect, 520.0, muted("Health"));
        cell_right(ui, rect, 608.0, muted("Kills"));
        cell(ui, rect, 640.0, 100.0, muted("Id"));
    });
    hline(ui, LINE_SOFT);
    if players.is_empty() {
        ui.add_space(14.0);
        ui.horizontal(|ui| {
            ui.add_space(16.0);
            ui.label(muted("Nobody online."));
        });
        return;
    }
    egui::ScrollArea::vertical()
        .id_salt("online-table")
        .auto_shrink([false, false])
        .show(ui, |ui| {
            ui.spacing_mut().item_spacing.y = 0.0;
            for p in &players {
                let selected = gui.t.selected.as_deref() == Some(p.id.as_str());
                let color = if p.alive { TEXT } else { FAINT };
                let r = row(ui, ROW, selected.then_some(SELECTED), |ui, rect| {
                    cell(
                        ui,
                        rect,
                        16.0,
                        60.0,
                        monospace(format!("#{}", p.index)).color(MUTED),
                    );
                    cell(ui, rect, 100.0, 215.0, name_with(&p.name, suffix(p), color));
                    cell(
                        ui,
                        rect,
                        322.0,
                        60.0,
                        text(format!("{}", p.team + 1)).color(MUTED),
                    );
                    cell_right(ui, rect, 520.0, health_text(p));
                    cell_right(ui, rect, 608.0, monospace(p.kills.to_string()));
                    cell(
                        ui,
                        rect,
                        640.0,
                        rect.width() - 650.0,
                        monospace(short_id(&p.id)).color(MUTED),
                    );
                });
                if r.clicked() {
                    gui.t.selected = Some(p.id.clone());
                }
                hline(ui, LINE_SOFT);
            }
        });
}

fn history_table(gui: &mut Gui, ui: &mut Ui) {
    let records: Vec<PlayerRecord> = gui
        .history
        .iter()
        .filter(|r| {
            matches_filter(
                &gui.t.filter,
                r.names.first().map_or("", String::as_str),
                &r.id,
            )
        })
        .cloned()
        .collect();
    row(ui, 38.0, None, |ui, rect| {
        cell(ui, rect, 16.0, 120.0, muted("Player"));
        cell(ui, rect, 230.0, 120.0, muted("Last seen"));
        cell_right(ui, rect, 440.0, muted("Joins"));
        cell_right(ui, rect, 510.0, muted("Kills"));
        cell_right(ui, rect, 590.0, muted("Deaths"));
        cell_right(ui, rect, 650.0, muted("Wins"));
        cell(ui, rect, 672.0, 100.0, muted("Id"));
    });
    hline(ui, LINE_SOFT);
    if records.is_empty() {
        ui.add_space(14.0);
        ui.horizontal(|ui| {
            ui.add_space(16.0);
            ui.label(muted("Nobody has played yet."));
        });
        return;
    }
    egui::ScrollArea::vertical()
        .id_salt("history-table")
        .auto_shrink([false, false])
        .show(ui, |ui| {
            ui.spacing_mut().item_spacing.y = 0.0;
            for p in &records {
                let selected = gui.t.selected.as_deref() == Some(p.id.as_str());
                let r = row(ui, ROW, selected.then_some(SELECTED), |ui, rect| {
                    let name = p.names.first().map_or("", String::as_str);
                    cell(ui, rect, 16.0, 208.0, text(name));
                    cell(ui, rect, 230.0, 130.0, text(ago(p.last_seen)).color(MUTED));
                    cell_right(ui, rect, 440.0, monospace(p.joins.to_string()));
                    cell_right(ui, rect, 510.0, monospace(p.kills.to_string()));
                    cell_right(ui, rect, 590.0, monospace(p.deaths.to_string()));
                    cell_right(ui, rect, 650.0, monospace(p.wins.to_string()));
                    cell(
                        ui,
                        rect,
                        672.0,
                        rect.width() - 680.0,
                        monospace(short_id(&p.id)).color(MUTED),
                    );
                });
                if r.clicked() {
                    gui.t.selected = Some(p.id.clone());
                }
                hline(ui, LINE_SOFT);
            }
        });
}

fn table_card(gui: &mut Gui, ui: &mut Ui) {
    card_fill(
        ui,
        "",
        |_| {},
        |ui| {
            ui.spacing_mut().item_spacing.y = 0.0;
            card_header(ui, |ui| {
                ui.spacing_mut().item_spacing.x = 6.0;
                let online = format!("Online {}", gui.status.players.len());
                if chip(ui, &online, !gui.t.everyone).clicked() {
                    gui.t.everyone = false;
                }
                let everyone = format!("Everyone who played {}", gui.history.len());
                if chip(ui, &everyone, gui.t.everyone).clicked() {
                    gui.t.everyone = true;
                }
                ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                    let bots: Vec<PlayerInfo> = gui
                        .status
                        .players
                        .iter()
                        .filter(|p| is_bot(p))
                        .cloned()
                        .collect();
                    ui.add_enabled_ui(!bots.is_empty(), |ui| {
                        if plain(ui, "Remove all bots") {
                            gui.remove_all_bots(&bots);
                        }
                    });
                    input(ui, &mut gui.t.filter, 200.0, "Filter");
                });
            });
            if gui.t.everyone {
                history_table(gui, ui);
            } else {
                online_table(gui, ui);
            }
        },
    );
}

fn fact(ui: &mut Ui, name: &str, value: RichText) {
    ui.horizontal(|ui| {
        let (rect, _) = ui.allocate_exact_size(vec2(124.0, 24.0), Sense::hover());
        ui.painter().text(
            pos2(rect.left(), rect.center().y),
            egui::Align2::LEFT_CENTER,
            name,
            body(),
            MUTED,
        );
        ui.label(value);
    });
}

fn selected_player(gui: &mut Gui, ui: &mut Ui) {
    let id = gui.t.selected.clone().unwrap_or_default();
    let online = gui.status.players.iter().find(|p| p.id == id).cloned();
    let record = gui.history.iter().find(|r| r.id == id).cloned();
    let name = online
        .as_ref()
        .map(|p| p.name.clone())
        .or_else(|| record.as_ref().and_then(|r| r.names.first().cloned()));
    let Some(name) = name else {
        ui.add_space(4.0);
        ui.label(muted("Pick someone in the list."));
        return;
    };
    ui.label(RichText::new(&name).font(semi(18.0)).color(TEXT));
    let mut where_ = short_id(&id);
    if let Some(p) = &online {
        where_ += &format!(" · #{} · Squad {}", p.index, p.team + 1);
    }
    ui.label(monospace(where_).color(MUTED));
    ui.add_space(6.0);
    if let Some(p) = &online {
        fact(ui, "Health", health_text(p));
        fact(ui, "Kills", monospace(p.kills.to_string()));
    }
    if let Some(r) = &record {
        let times = r.joins.saturating_sub(1);
        let joined = if online.is_some() {
            format!("{}, {times} times before", clock_short(r.last_seen))
        } else {
            format!(
                "{} times, last {}",
                r.joins,
                ago(r.last_seen).to_lowercase()
            )
        };
        fact(ui, "Joined", monospace(joined));
    }
    ui.add_space(4.0);
    let bot = online.as_ref().is_some_and(is_bot);
    let admin = gui
        .saved
        .players
        .admins
        .iter()
        .any(|a| a == &id || a.eq_ignore_ascii_case(&name));
    let banned = gui
        .saved
        .players
        .banned
        .iter()
        .any(|a| a == &id || a.eq_ignore_ascii_case(&name));
    ui.horizontal(|ui| {
        if let Some(p) = &online
            && plain(ui, "Heal")
        {
            gui.console(
                format!("heal #{}", p.index),
                Some(format!("You healed {}", p.name)),
            );
        }
        if !bot && plain(ui, if admin { "Remove admin" } else { "Make admin" }) {
            gui.set_admin(id.clone(), name.clone(), !admin);
        }
        if plain(ui, "Copy id") {
            ui.ctx().copy_text(id.clone());
            gui.tell("Id copied.");
        }
    });
    if let Some(p) = online.clone() {
        ui.add_space(8.0);
        hline(ui, LINE_SOFT);
        ui.add_space(8.0);
        ui.label(text("Give an item").color(MUTED));
        ui.horizontal(|ui| {
            let width = ui.available_width() - 150.0;
            let items = gui.map.items.clone();
            let picked = ItemPicker {
                id: "give",
                query: &mut gui.t.give_query,
                items: &items,
                width,
                hint: "Search items",
            }
            .show(ui);
            if let Some(item) = picked {
                gui.t.give_item = Some(item);
                gui.t.give_query = crate::draft::item_name(&items, item);
            }
            if gui.t.give_qty == 0 {
                gui.t.give_qty = 1;
            }
            number(ui, "give-qty", &mut gui.t.give_qty, 70.0, 1.0);
            ui.add_enabled_ui(
                gui.t.give_item.is_some() && !gui.t.give_query.is_empty(),
                |ui| {
                    if plain(ui, "Give") {
                        let item = gui.t.give_item.unwrap_or_default();
                        let qty = gui.t.give_qty;
                        let item_name = crate::draft::item_name(&items, item);
                        gui.console(
                            format!("give #{} {item} {qty}", p.index),
                            Some(format!("You gave {} {qty} × {item_name}", p.name)),
                        );
                    }
                },
            );
        });
        ui.add_space(8.0);
        hline(ui, LINE_SOFT);
        ui.add_space(8.0);
    }
    ui.horizontal(|ui| {
        if let Some(p) = &online
            && plain(ui, if bot { "Remove" } else { "Kick" })
        {
            gui.kick(p);
        }
        if !bot {
            if banned {
                if plain(ui, "Unban") {
                    gui.set_ban(id.clone(), name.clone(), false);
                }
            } else if warn(ui, "Ban…") {
                gui.dialog = Some(Dialog::Ban {
                    id: id.clone(),
                    name: name.clone(),
                });
            }
        }
    });
}

/// The name we know for a list entry (entries are names or ids).
fn entry_name<'a>(gui: &'a Gui, entry: &'a str) -> &'a str {
    gui.history
        .iter()
        .find(|r| r.id == entry)
        .and_then(|r| r.names.first())
        .map_or(entry, String::as_str)
}

enum ListKind {
    Admins,
    Banned,
}

fn people_list(gui: &mut Gui, ui: &mut Ui, kind: &ListKind) {
    let entries = match kind {
        ListKind::Admins => gui.saved.players.admins.clone(),
        ListKind::Banned => gui.saved.players.banned.clone(),
    };
    let (action, empty) = match kind {
        ListKind::Admins => ("Remove", "No admins yet."),
        ListKind::Banned => ("Unban", "Nobody is banned."),
    };
    ui.spacing_mut().item_spacing.y = 0.0;
    if entries.is_empty() {
        row(ui, 34.0, None, |ui, rect| {
            cell(ui, rect, 16.0, 300.0, muted(empty));
        });
    }
    for entry in &entries {
        let name = entry_name(gui, entry).to_owned();
        row(ui, 34.0, None, |ui, rect| {
            let width = cell_name(ui, rect, &name);
            if name != *entry {
                cell(
                    ui,
                    rect,
                    16.0 + width,
                    150.0,
                    monospace(short_id(entry)).color(MUTED),
                );
            }
            cell_actions(ui, rect, 16.0, |ui| {
                if plain(ui, action) {
                    match kind {
                        ListKind::Admins => gui.set_admin(entry.clone(), name.clone(), false),
                        ListKind::Banned => gui.set_ban(entry.clone(), name.clone(), false),
                    }
                }
            });
        });
    }
    hline(ui, LINE_SOFT);
    padded(ui, |ui| {
        right_row(ui, |ui| {
            let (label_, query) = match kind {
                ListKind::Admins => ("Add", &mut gui.t.add_admin),
                ListKind::Banned => ("Ban", &mut gui.t.add_ban),
            };
            let can = !query.trim().is_empty();
            let clicked = ui.add_enabled_ui(can, |ui| plain(ui, label_)).inner;
            let width = ui.available_width() - 2.0;
            input(ui, query, width, "Name or id");
            if clicked {
                let who = std::mem::take(query).trim().to_owned();
                match kind {
                    ListKind::Admins => gui.set_admin(who.clone(), who, true),
                    ListKind::Banned => {
                        gui.dialog = Some(Dialog::Ban {
                            id: who.clone(),
                            name: who,
                        })
                    }
                }
            }
        });
    });
}

fn cell_name(ui: &mut Ui, rect: Rect, name: &str) -> f32 {
    let width = ui
        .painter()
        .layout_no_wrap(name.to_owned(), body(), TEXT)
        .size()
        .x
        + 8.0;
    cell(ui, rect, 16.0, width.min(200.0), text(name));
    width.min(200.0)
}

pub fn show(gui: &mut Gui, ui: &mut Ui) {
    let area = ui.available_rect_before_wrap();
    let right_w = 495.0_f32.min(area.width() * 0.38);
    let left = Rect::from_min_max(area.min, pos2(area.right() - right_w - 10.0, area.bottom()));
    let right = Rect::from_min_max(pos2(area.right() - right_w, area.top()), area.max);
    within(ui, left, |ui| table_card(gui, ui));
    within(ui, right, |ui| {
        egui::ScrollArea::vertical()
            .id_salt("players-right")
            .auto_shrink([false, false])
            .show(ui, |ui| {
                ui.spacing_mut().item_spacing.y = 10.0;
                ui.set_width(ui.available_width());
                card(
                    ui,
                    "Selected player",
                    |_| {},
                    |ui| {
                        padded(ui, |ui| selected_player(gui, ui));
                    },
                );
                card(
                    ui,
                    "Admins",
                    |_| {},
                    |ui| people_list(gui, ui, &ListKind::Admins),
                );
                card(
                    ui,
                    "Banned",
                    |_| {},
                    |ui| people_list(gui, ui, &ListKind::Banned),
                );
            });
    });
}
