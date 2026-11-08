//! The ring, what everyone lands with, and the gun game levels.

use eframe::egui::{self, Align, Context, Layout, Rect, Ui, pos2, vec2};
use tabg_host::config::LoadoutItem;

use crate::app::{Dialog, Gui, dialog_frame};
use crate::draft::{self, Section, item_name};
use crate::theme::*;
use crate::widgets::*;

/// The most items the loadout holds (the server's limit on what fits an inventory).
pub const MAX_LOADOUT: usize = 64;

/// `list[i]`, or what the map says while the list is empty.
fn value(list: &[f32], map: &[f32], i: usize) -> Option<f32> {
    if list.is_empty() {
        map.get(i).copied()
    } else {
        list.get(i).copied()
    }
}

/// Turn "use the map's values" into an explicit list, so one stage can change.
fn materialize(list: &mut Vec<f32>, map: &[f32]) {
    if list.is_empty() {
        list.extend_from_slice(map);
    }
}

fn ring_card(gui: &mut Gui, ui: &mut Ui) {
    let custom = !(gui.draft.ring.times.is_empty()
        && gui.draft.ring.sizes.is_empty()
        && gui.draft.ring.speeds.is_empty());
    let tag = if custom { "custom" } else { "map values" };
    card_fill(
        ui,
        "Ring",
        |ui| {
            ui.label(muted(tag));
        },
        |ui| {
            let stages = gui
                .draft
                .ring
                .times
                .len()
                .max(gui.draft.ring.sizes.len())
                .max(gui.draft.ring.speeds.len())
                .max(gui.map.sizes.len());
            ui.spacing_mut().item_spacing.y = 0.0;
            let (xw, xd, xs) = (150.0, 320.0, 515.0);
            row(ui, 38.0, None, |ui, rect| {
                cell(ui, rect, 16.0, 100.0, muted("Stage"));
                cell(ui, rect, xw, 100.0, muted("Wait, s"));
                cell(ui, rect, xd, 120.0, muted("Diameter, m"));
                cell(ui, rect, xs, 100.0, muted("Speed"));
            });
            hline(ui, LINE_SOFT);
            let map = gui.map.clone();
            for i in 0..stages {
                let selected = gui.t.ring_stage == i;
                let mut touched = false;
                let ring = &mut gui.draft.ring;
                let mut editable = |ui: &mut Ui,
                                    rect: Rect,
                                    x: f32,
                                    width: f32,
                                    step: f64,
                                    list: &mut Vec<f32>,
                                    map: &[f32],
                                    key: &str| {
                    let area = Rect::from_min_size(
                        pos2(rect.left() + x, rect.top() + 2.0),
                        vec2(width, rect.height() - 4.0),
                    );
                    within(ui, area, |ui| {
                        let mut v = value(list, map, i).unwrap_or(0.0);
                        if number(ui, (key, i), &mut v, width, step) {
                            materialize(list, map);
                            if list.len() <= i {
                                list.resize(i + 1, 0.0);
                            }
                            list[i] = v;
                            touched = true;
                        }
                    });
                };
                let r = row(ui, 34.0, selected.then_some(SELECTED), |ui, rect| {
                    cell(ui, rect, 16.0, 120.0, text(format!("Stage {}", i + 1)));
                    editable(ui, rect, xw, 76.0, 5.0, &mut ring.times, &map.times, "wait");
                    editable(
                        ui,
                        rect,
                        xd,
                        90.0,
                        10.0,
                        &mut ring.sizes,
                        &map.sizes,
                        "size",
                    );
                    editable(
                        ui,
                        rect,
                        xs,
                        70.0,
                        0.5,
                        &mut ring.speeds,
                        &map.speeds,
                        "speed",
                    );
                });
                if r.clicked() || touched {
                    gui.t.ring_stage = i;
                }
            }
            hline(ui, LINE_SOFT);
            let sizes: Vec<f32> = (0..stages)
                .filter_map(|i| value(&gui.draft.ring.sizes, &gui.map.sizes, i))
                .collect();
            let area = ui.available_rect_before_wrap();
            let footer_h = 62.0;
            let diagram =
                Rect::from_min_max(area.min, pos2(area.right(), area.bottom() - footer_h));
            ring_diagram(ui, diagram, &sizes, gui.t.ring_stage);
            let footer = Rect::from_min_max(pos2(area.left(), diagram.bottom()), area.max);
            ring_footer(gui, ui, footer);
        },
    );
}

fn ring_diagram(ui: &mut Ui, area: Rect, sizes: &[f32], selected: usize) {
    let Some(largest) = sizes.iter().copied().reduce(f32::max).filter(|v| *v > 0.0) else {
        return;
    };
    let side = (area.height() - 40.0).clamp(60.0, 300.0);
    let square = Rect::from_center_size(area.center(), vec2(side, side));
    ui.painter().rect_filled(square, 0.0, HEADER);
    let r_max = side / 2.0 - 12.0;
    for (i, size) in sizes.iter().enumerate() {
        let r = (size / largest * r_max).max(2.0);
        ring_circle(ui, square.center(), r, i == selected);
    }
}

fn ring_footer(gui: &mut Gui, ui: &mut Ui, rect: Rect) {
    let next = draft::merged(Section::Ring, &gui.draft, &gui.saved);
    let problem = next
        .validate()
        .into_iter()
        .find(|p| p.field.starts_with("ring."));
    let dirty = !draft::same(Section::Ring, &gui.draft, &gui.saved);
    let can = problem.is_none() && dirty && !gui.applying;
    ui.painter().hline(
        rect.x_range(),
        rect.top() + 0.5,
        egui::Stroke::new(1.0, LINE),
    );
    within(ui, rect.shrink2(vec2(16.0, 0.0)), |ui| {
        ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
            ui.add_enabled_ui(can, |ui| {
                if primary(ui, "Save ring") {
                    gui.save_section(Section::Ring);
                }
            });
            if plain(ui, "Use map values") {
                gui.draft.ring = Default::default();
            }
            ui.with_layout(Layout::left_to_right(Align::Center), |ui| {
                let note = match &problem {
                    Some(p) => text(crate::app::capitalized(&p.message)).color(DANGER),
                    None => muted("Each ring must be smaller than the one before. Applies from the next match."),
                };
                ui.add(egui::Label::new(note).wrap());
            });
        });
    });
}

fn add_item(items: &mut Vec<LoadoutItem>, item: i32) {
    if !items.iter().any(|i| i.item == item) {
        items.push(LoadoutItem { item, qty: 1 });
    }
}

fn loadout_card(gui: &mut Gui, ui: &mut Ui) {
    let count = format!("{} of {MAX_LOADOUT}", gui.draft.loadout.len());
    card(
        ui,
        "Landing loadout",
        |ui| {
            ui.label(muted(count));
        },
        |ui| {
            ui.spacing_mut().item_spacing.y = 0.0;
            padded(ui, |ui| {
                let items = gui.map.items.clone();
                let width = ui.available_width();
                let full = gui.draft.loadout.len() >= MAX_LOADOUT;
                let hint = if items.is_empty() {
                    "Run setup first: item names come from the map data"
                } else {
                    "Add an item"
                };
                ui.add_enabled_ui(!items.is_empty() && !full, |ui| {
                    let picked = ItemPicker {
                        id: "loadout",
                        query: &mut gui.t.loadout_query,
                        items: &items,
                        width,
                        hint,
                    }
                    .show(ui);
                    if let Some(item) = picked {
                        add_item(&mut gui.draft.loadout, item);
                        gui.t.loadout_query.clear();
                    }
                });
            });
            let names = gui.map.items.clone();
            let height = (gui.draft.loadout.len() as f32 * 34.0 + 34.0).min(150.0);
            egui::ScrollArea::vertical()
                .id_salt("loadout-list")
                .max_height(height)
                .auto_shrink([false, true])
                .show(ui, |ui| {
                    ui.spacing_mut().item_spacing.y = 0.0;
                    if !gui.draft.loadout.is_empty() {
                        row(ui, 30.0, None, |ui, rect| {
                            cell(ui, rect, 16.0, 200.0, muted("Item"));
                            cell(ui, rect, 345.0, 80.0, muted("Qty"));
                        });
                    }
                    let mut remove = None;
                    for (i, it) in gui.draft.loadout.iter_mut().enumerate() {
                        row(ui, 34.0, None, |ui, rect| {
                            cell(
                                ui,
                                rect,
                                16.0,
                                320.0,
                                name_with_id(&item_name(&names, it.item), it.item),
                            );
                            let area = Rect::from_min_size(
                                pos2(rect.left() + 345.0, rect.top() + 2.0),
                                vec2(80.0, 30.0),
                            );
                            within(ui, area, |ui| {
                                number(ui, ("qty", i), &mut it.qty, 80.0, 1.0);
                            });
                            cell_actions(ui, rect, 16.0, |ui| {
                                if plain(ui, "Remove") {
                                    remove = Some(i);
                                }
                            });
                        });
                    }
                    if let Some(i) = remove {
                        gui.draft.loadout.remove(i);
                    }
                });
            hline(ui, LINE_SOFT);
            let next = draft::merged(Section::Loadout, &gui.draft, &gui.saved);
            let problem = next
                .validate()
                .into_iter()
                .find(|p| p.field.starts_with("loadout"));
            let can = problem.is_none()
                && !draft::same(Section::Loadout, &gui.draft, &gui.saved)
                && !gui.applying;
            padded(ui, |ui| {
                right_row(ui, |ui| {
                    ui.add_enabled_ui(can, |ui| {
                        if primary(ui, "Save loadout") {
                            gui.save_section(Section::Loadout);
                        }
                    });
                    ui.with_layout(Layout::left_to_right(Align::Center), |ui| {
                        let note = match &problem {
                            Some(p) => text(crate::app::capitalized(&p.message)).color(DANGER),
                            None => muted("On landing and every respawn. Applies at once."),
                        };
                        ui.add(egui::Label::new(note).truncate());
                    });
                });
            });
        },
    );
}

fn name_with_id(name: &str, id: i32) -> egui::text::LayoutJob {
    let mut job = egui::text::LayoutJob::default();
    job.append(name, 0.0, egui::TextFormat::simple(body(), TEXT));
    job.append(
        &format!(" #{id}"),
        0.0,
        egui::TextFormat::simple(mono(), FAINT),
    );
    job
}

/// What a level hands out: `Hand Gun + ammo`-style.
fn level_text(level: &[LoadoutItem], gui: &Gui) -> String {
    let names: Vec<String> = level
        .iter()
        .map(|i| item_name(&gui.map.items, i.item))
        .collect();
    match names.split_first() {
        None => "Empty, pick an item".to_owned(),
        Some((first, [])) => first.clone(),
        Some((first, rest)) => {
            if rest.iter().all(|n| n.to_lowercase().contains("ammo")) {
                format!("{first} + ammo")
            } else {
                format!("{first} + {}", rest.join(" + "))
            }
        }
    }
}

fn gun_game_card(gui: &mut Gui, ui: &mut Ui) {
    let levels = gui.draft.rules.gun_game.len();
    let count = match levels {
        0 => "off".to_owned(),
        1 => "1 level".to_owned(),
        n => format!("{n} levels"),
    };
    card_fill(
        ui,
        "Gun game",
        |ui| {
            ui.label(muted(count));
        },
        |ui| {
            let area = ui.available_rect_before_wrap();
            let footer_h = 52.0;
            let list = Rect::from_min_max(area.min, pos2(area.right(), area.bottom() - footer_h));
            let footer = Rect::from_min_max(pos2(area.left(), list.bottom()), area.max);
            within(ui, list, |ui| {
                egui::ScrollArea::vertical()
                    .id_salt("gun-levels")
                    .auto_shrink([false, false])
                    .show(ui, |ui| {
                        ui.spacing_mut().item_spacing.y = 0.0;
                        if levels == 0 {
                            ui.add_space(12.0);
                            ui.horizontal(|ui| {
                                ui.add_space(16.0);
                                ui.label(muted(
                                    "No levels yet. Each kill moves a player to the next one.",
                                ));
                            });
                        }
                        let mut swap = None;
                        let mut edit = None;
                        for i in 0..levels {
                            let title = {
                                let level = &gui.draft.rules.gun_game[i];
                                let base = level_text(level, gui);
                                if i + 1 == levels {
                                    format!("{base} · kill wins")
                                } else {
                                    base
                                }
                            };
                            row(ui, 36.0, None, |ui, rect| {
                                cell(
                                    ui,
                                    rect,
                                    16.0,
                                    40.0,
                                    monospace((i + 1).to_string()).color(MUTED),
                                );
                                cell(ui, rect, 64.0, rect.width() - 230.0, text(title));
                                cell_actions(ui, rect, 16.0, |ui| {
                                    if plain(ui, "Edit") {
                                        edit = Some(i);
                                    }
                                    ui.add_enabled_ui(i + 1 < levels, |ui| {
                                        if plain(ui, "↓") {
                                            swap = Some((i, i + 1));
                                        }
                                    });
                                    ui.add_enabled_ui(i > 0, |ui| {
                                        if plain(ui, "↑") {
                                            swap = Some((i, i - 1));
                                        }
                                    });
                                });
                            });
                        }
                        if let Some((a, b)) = swap {
                            gui.draft.rules.gun_game.swap(a, b);
                        }
                        if let Some(i) = edit {
                            gui.t.level_query.clear();
                            gui.dialog = Some(Dialog::Level(i));
                        }
                    });
            });
            gun_footer(gui, ui, footer);
        },
    );
}

fn gun_footer(gui: &mut Gui, ui: &mut Ui, rect: Rect) {
    let next = draft::merged(Section::GunGame, &gui.draft, &gui.saved);
    let problem = next
        .validate()
        .into_iter()
        .find(|p| p.field.starts_with("rules.gun_game"));
    let can = problem.is_none()
        && !draft::same(Section::GunGame, &gui.draft, &gui.saved)
        && !gui.applying;
    let respawns = gui.saved.rules.respawn;
    let empty = gui.draft.rules.gun_game.is_empty();
    ui.painter().hline(
        rect.x_range(),
        rect.top() + 0.5,
        egui::Stroke::new(1.0, LINE),
    );
    within(ui, rect.shrink2(vec2(16.0, 0.0)), |ui| {
        ui.with_layout(Layout::left_to_right(Align::Center), |ui| {
            if plain(ui, "Add level") {
                let level = gui.draft.rules.gun_game.len();
                gui.draft.rules.gun_game.push(Vec::new());
                gui.t.level_query.clear();
                gui.dialog = Some(Dialog::Level(level));
            }
            let note = match (&problem, respawns, empty) {
                (Some(p), false, _) if p.field == "rules.gun_game" => {
                    text("Needs respawns, which are off").color(DANGER)
                }
                (Some(p), _, _) => text(crate::app::capitalized(&p.message)).color(DANGER),
                (None, true, false) => muted("Needs respawns, which are on"),
                (None, _, _) => muted("Needs respawns"),
            };
            ui.add(egui::Label::new(note).truncate());
            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                ui.add_enabled_ui(can, |ui| {
                    if primary(ui, "Save levels") {
                        gui.save_section(Section::GunGame);
                    }
                });
            });
        });
    });
}

/// Editing one gun game level.
pub fn level_dialog(gui: &mut Gui, ctx: &Context, index: usize) -> Option<Dialog> {
    if index >= gui.draft.rules.gun_game.len() {
        return None;
    }
    let mut close = false;
    let mut remove_level = false;
    let items = gui.map.items.clone();
    let title = format!("Level {}", index + 1);
    dialog_frame(ctx, &title, 460.0, |ui, footer| match footer {
        None => {
            let width = ui.available_width();
            let picked = ItemPicker {
                id: "level",
                query: &mut gui.t.level_query,
                items: &items,
                width,
                hint: "Add an item",
            }
            .show(ui);
            let level = &mut gui.draft.rules.gun_game[index];
            if let Some(item) = picked {
                add_item(level, item);
                gui.t.level_query.clear();
            }
            ui.spacing_mut().item_spacing.y = 0.0;
            ui.add_space(6.0);
            if level.is_empty() {
                ui.label(muted("Pick what a player holds on this level."));
            }
            let mut remove = None;
            for (i, it) in level.iter_mut().enumerate() {
                row(ui, 34.0, None, |ui, rect| {
                    cell(
                        ui,
                        rect,
                        0.0,
                        260.0,
                        name_with_id(&item_name(&items, it.item), it.item),
                    );
                    let area = Rect::from_min_size(
                        pos2(rect.left() + 268.0, rect.top() + 2.0),
                        vec2(80.0, 30.0),
                    );
                    within(ui, area, |ui| {
                        number(ui, ("level-qty", i), &mut it.qty, 80.0, 1.0);
                    });
                    cell_actions(ui, rect, 0.0, |ui| {
                        if plain(ui, "Remove") {
                            remove = Some(i);
                        }
                    });
                });
            }
            if let Some(i) = remove {
                level.remove(i);
            }
        }
        Some(()) => {
            if primary(ui, "Done") {
                close = true;
            }
            if warn(ui, "Delete level") {
                remove_level = true;
            }
        }
    });
    if remove_level {
        gui.draft.rules.gun_game.remove(index);
        return None;
    }
    if close || ctx.input(|i| i.key_pressed(egui::Key::Escape)) {
        // An untouched new level is nothing to keep.
        if gui.draft.rules.gun_game[index].is_empty() {
            gui.draft.rules.gun_game.remove(index);
        }
        return None;
    }
    Some(Dialog::Level(index))
}

pub fn show(gui: &mut Gui, ui: &mut Ui) {
    let area = ui.available_rect_before_wrap();
    let left_w = (area.width() * 0.495).floor();
    let left = Rect::from_min_size(area.min, vec2(left_w, area.height()));
    let right = Rect::from_min_max(pos2(left.right() + 10.0, area.top()), area.max);
    within(ui, left, |ui| ring_card(gui, ui));
    within(ui, right, |ui| {
        ui.spacing_mut().item_spacing.y = 10.0;
        loadout_card(gui, ui);
        gun_game_card(gui, ui);
    });
}
