//! The window's look: buttons, fields, cards and table rows.

use std::collections::BTreeMap;
use std::hash::Hash;

use eframe::egui::{
    self, Align, Align2, Color32, CornerRadius, Event, FontId, Frame, Id, Key, Label, Layout,
    Margin, Order, Pos2, Rect, Response, RichText, Sense, Shape, Stroke, StrokeKind, TextEdit, Ui,
    UiBuilder, Vec2, WidgetInfo, WidgetText, WidgetType, pos2, text::LayoutJob, vec2,
};

use crate::theme::*;

/// Run `f` in a child `Ui` confined to `rect`.
pub fn within<R>(ui: &mut Ui, rect: Rect, f: impl FnOnce(&mut Ui) -> R) -> R {
    ui.scope_builder(UiBuilder::new().max_rect(rect), f).inner
}

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum Kind {
    Plain,
    Primary,
    /// White with red text.
    Warn,
    /// Solid red, for the confirming button of a dialog.
    Danger,
}

pub fn button_sized(ui: &mut Ui, label: &str, kind: Kind, min_width: f32) -> Response {
    let enabled = ui.is_enabled();
    let galley = ui
        .painter()
        .layout_no_wrap(label.to_owned(), body(), Color32::PLACEHOLDER);
    let size = vec2((galley.size().x + 24.0).max(min_width), CONTROL_H);
    let (rect, response) = ui.allocate_exact_size(size, Sense::click());
    response.widget_info(|| WidgetInfo::labeled(WidgetType::Button, enabled, label));
    if !ui.is_rect_visible(rect) {
        return response;
    }
    let hot = response.hovered() && enabled;
    let down = response.is_pointer_button_down_on() && enabled;
    let (fill, stroke, text) = match (kind, enabled) {
        (Kind::Primary, true) => (
            if hot { ACCENT_DARK } else { ACCENT },
            Stroke::NONE,
            Color32::WHITE,
        ),
        (Kind::Danger, true) => (
            if hot { DANGER_DARK } else { DANGER },
            Stroke::NONE,
            Color32::WHITE,
        ),
        (Kind::Warn, true) => (
            if down {
                SELECTED
            } else if hot {
                Color32::from_rgb(0xfd, 0xf1, 0xf1)
            } else {
                PANEL
            },
            Stroke::new(1.0, FIELD_LINE),
            DANGER,
        ),
        (Kind::Plain, true) => (
            if down {
                SELECTED
            } else if hot {
                HOVER
            } else {
                PANEL
            },
            Stroke::new(1.0, FIELD_LINE),
            TEXT,
        ),
        (Kind::Plain, false) => (HEADER, Stroke::new(1.0, LINE), FAINT),
        (_, false) => (
            Color32::from_rgb(0xf8, 0xee, 0xe6),
            Stroke::new(1.0, Color32::from_rgb(0xd6, 0xb4, 0xa0)),
            Color32::from_rgb(0xbf, 0xa8, 0x9a),
        ),
    };
    let painter = ui.painter();
    painter.rect(
        rect,
        CornerRadius::same(FIELD_RADIUS),
        fill,
        stroke,
        StrokeKind::Inside,
    );
    painter.text(rect.center(), Align2::CENTER_CENTER, label, body(), text);
    if enabled {
        response.on_hover_cursor(egui::CursorIcon::PointingHand)
    } else {
        response
    }
}

pub fn button(ui: &mut Ui, label: &str, kind: Kind) -> Response {
    button_sized(ui, label, kind, 0.0)
}

pub fn plain(ui: &mut Ui, label: &str) -> bool {
    button(ui, label, Kind::Plain).clicked()
}

pub fn primary(ui: &mut Ui, label: &str) -> bool {
    button(ui, label, Kind::Primary).clicked()
}

pub fn warn(ui: &mut Ui, label: &str) -> bool {
    button(ui, label, Kind::Warn).clicked()
}

pub fn text(s: impl Into<String>) -> RichText {
    RichText::new(s).font(body()).color(TEXT)
}

pub fn muted(s: impl Into<String>) -> RichText {
    RichText::new(s).font(small()).color(MUTED)
}

pub fn monospace(s: impl Into<String>) -> RichText {
    RichText::new(s).font(mono()).color(TEXT)
}

pub fn label(ui: &mut Ui, t: impl Into<WidgetText>) -> Response {
    ui.add(Label::new(t).wrap())
}

/// A white card with a grey title strip.
pub fn card<R>(
    ui: &mut Ui,
    title: &str,
    aside: impl FnOnce(&mut Ui),
    body: impl FnOnce(&mut Ui) -> R,
) -> R {
    Frame::new()
        .fill(PANEL)
        .stroke(Stroke::new(1.0, LINE))
        .corner_radius(CornerRadius::same(RADIUS))
        .show(ui, |ui| {
            ui.set_width(ui.available_width());
            ui.spacing_mut().item_spacing.y = 0.0;
            card_header(ui, |ui| {
                ui.label(RichText::new(title).font(semi(14.0)).color(MUTED));
                ui.with_layout(Layout::right_to_left(Align::Center), aside);
            });
            ui.spacing_mut().item_spacing.y = 6.0;
            body(ui)
        })
        .inner
}

pub fn card_header(ui: &mut Ui, f: impl FnOnce(&mut Ui)) {
    let (rect, _) = ui.allocate_exact_size(vec2(ui.available_width(), 42.0), Sense::hover());
    let r = RADIUS;
    ui.painter().rect_filled(
        rect,
        CornerRadius {
            nw: r,
            ne: r,
            sw: 0,
            se: 0,
        },
        HEADER,
    );
    ui.painter().hline(
        rect.x_range(),
        rect.bottom() - 0.5,
        Stroke::new(1.0, LINE_SOFT),
    );
    within(ui, rect.shrink2(vec2(16.0, 0.0)), |ui| {
        ui.with_layout(Layout::left_to_right(Align::Center), f);
    });
}

/// A card that takes all the height `ui` has left.
pub fn card_fill<R>(
    ui: &mut Ui,
    title: &str,
    aside: impl FnOnce(&mut Ui),
    body: impl FnOnce(&mut Ui) -> R,
) -> R {
    let rect = ui.available_rect_before_wrap();
    let out = within(ui, rect, |ui| {
        ui.set_min_height(rect.height());
        Frame::new()
            .fill(PANEL)
            .stroke(Stroke::new(1.0, LINE))
            .corner_radius(CornerRadius::same(RADIUS))
            .show(ui, |ui| {
                // A very short window (or egui's sizing pass) can leave no room at all.
                let inner = (rect.size() - Vec2::splat(2.0)).max(Vec2::ZERO);
                ui.set_min_size(inner);
                ui.set_max_size(inner);
                ui.spacing_mut().item_spacing.y = 0.0;
                if !title.is_empty() {
                    card_header(ui, |ui| {
                        ui.label(RichText::new(title).font(semi(14.0)).color(MUTED));
                        ui.with_layout(Layout::right_to_left(Align::Center), aside);
                    });
                }
                ui.spacing_mut().item_spacing.y = 6.0;
                body(ui)
            })
            .inner
    });
    ui.advance_cursor_after_rect(rect);
    out
}

/// Inner padding for a card's body.
pub fn padded<R>(ui: &mut Ui, f: impl FnOnce(&mut Ui) -> R) -> R {
    Frame::new()
        .inner_margin(Margin::symmetric(16, 12))
        .show(ui, |ui| {
            ui.set_width(ui.available_width());
            f(ui)
        })
        .inner
}

/// One line of controls packed from the right edge, only as tall as a control.
pub fn right_row<R>(ui: &mut Ui, f: impl FnOnce(&mut Ui) -> R) -> R {
    let size = vec2(ui.available_width(), CONTROL_H);
    ui.allocate_ui_with_layout(size, Layout::right_to_left(Align::Center), f)
        .inner
}

/// A full-width table row; `f` gets its rect and places the cells inside.
pub fn row(
    ui: &mut Ui,
    height: f32,
    bg: Option<Color32>,
    f: impl FnOnce(&mut Ui, Rect),
) -> Response {
    let (rect, response) =
        ui.allocate_exact_size(vec2(ui.available_width(), height), Sense::click());
    if !ui.is_rect_visible(rect) {
        return response;
    }
    if let Some(bg) = bg {
        ui.painter().rect_filled(rect, 0.0, bg);
    }
    within(ui, rect, |ui| f(ui, rect));
    response
}

pub fn hline(ui: &mut Ui, color: Color32) {
    let (rect, _) = ui.allocate_exact_size(vec2(ui.available_width(), 1.0), Sense::hover());
    ui.painter()
        .hline(rect.x_range(), rect.center().y, Stroke::new(1.0, color));
}

/// Text in a cell, left-aligned at `x` (relative to `rect`) and cut off at `width`.
pub fn cell(ui: &mut Ui, rect: Rect, x: f32, width: f32, t: impl Into<WidgetText>) {
    let area = Rect::from_min_size(
        pos2(rect.left() + x, rect.top()),
        vec2(width, rect.height()),
    );
    within(ui, area, |ui| {
        ui.with_layout(Layout::left_to_right(Align::Center), |ui| {
            ui.add(Label::new(t).truncate());
        });
    });
}

/// Text in a cell, right-aligned so it ends at `x` (relative to `rect`).
pub fn cell_right(ui: &mut Ui, rect: Rect, x: f32, t: impl Into<WidgetText>) {
    let area = Rect::from_min_max(
        pos2(rect.left() + x - 120.0, rect.top()),
        pos2(rect.left() + x, rect.bottom()),
    );
    within(ui, area, |ui| {
        ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
            ui.add(Label::new(t).truncate());
        });
    });
}

/// Buttons packed against the right edge of a row.
pub fn cell_actions(ui: &mut Ui, rect: Rect, right_pad: f32, f: impl FnOnce(&mut Ui)) {
    let area = Rect::from_min_max(rect.min, pos2(rect.right() - right_pad, rect.bottom()));
    within(ui, area, |ui| {
        ui.spacing_mut().item_spacing.x = 5.0;
        ui.with_layout(Layout::right_to_left(Align::Center), f);
    });
}

/// `name · suffix`, the suffix muted.
pub fn name_with(name: &str, suffix: Option<&str>, name_color: Color32) -> LayoutJob {
    let mut job = LayoutJob::default();
    let format = |color| egui::TextFormat::simple(body(), color);
    job.append(name, 0.0, format(name_color));
    if let Some(s) = suffix {
        job.append(&format!(" · {s}"), 0.0, format(MUTED));
    }
    job
}

pub fn input(ui: &mut Ui, value: &mut String, width: f32, hint: &str) -> Response {
    input_with(ui, value, width, hint, false, body())
}

pub fn input_with(
    ui: &mut Ui,
    value: &mut String,
    width: f32,
    hint: &str,
    error: bool,
    font: FontId,
) -> Response {
    let mut edit = TextEdit::singleline(value)
        .desired_width(width)
        .min_size(vec2(width, CONTROL_H))
        .margin(Margin::symmetric(8, 6))
        .vertical_align(Align::Center)
        .font(font)
        .hint_text(RichText::new(hint).color(FAINT));
    if error {
        edit = edit.frame(
            Frame::new()
                .fill(PANEL)
                .stroke(Stroke::new(1.5, DANGER))
                .corner_radius(CornerRadius::same(FIELD_RADIUS))
                .inner_margin(Margin::symmetric(8, 6)),
        );
    }
    ui.add(edit)
}

pub trait Num: Copy {
    const WHOLE: bool;
    fn to_f64(self) -> f64;
    fn from_f64(v: f64) -> Self;
}

macro_rules! whole_numbers {
    ($($t:ty),*) => {$(
        impl Num for $t {
            const WHOLE: bool = true;
            fn to_f64(self) -> f64 {
                f64::from(self)
            }
            fn from_f64(v: f64) -> Self {
                // `as` saturates, which is what a spinner wants at the type's limits.
                v.round() as $t
            }
        }
    )*};
}
whole_numbers!(u8, u16, u32, i32);

impl Num for f32 {
    const WHOLE: bool = false;
    fn to_f64(self) -> f64 {
        f64::from(self)
    }
    fn from_f64(v: f64) -> Self {
        v as f32
    }
}

pub fn format_number(v: f64) -> String {
    let rounded = (v * 100.0).round() / 100.0;
    if rounded.fract() == 0.0 {
        format!("{rounded:.0}")
    } else {
        format!("{rounded}")
    }
}

/// A number field with up/down arrows. Returns true when the value changed.
pub fn number<T: Num>(
    ui: &mut Ui,
    id: impl Hash + std::fmt::Debug,
    value: &mut T,
    width: f32,
    step: f64,
) -> bool {
    let (rect, _) = ui.allocate_exact_size(vec2(width, CONTROL_H), Sense::hover());
    let id = ui.id().with(("number", id));
    let edit_id = id.with("edit");
    let focused = ui.ctx().memory(|m| m.has_focus(edit_id));
    let mut buffer = if focused {
        ui.data(|d| d.get_temp::<String>(id))
            .unwrap_or_else(|| format_number(value.to_f64()))
    } else {
        format_number(value.to_f64())
    };
    let mut changed = false;
    let edit = within(ui, rect, |ui| {
        ui.add(
            TextEdit::singleline(&mut buffer)
                .id(edit_id)
                .desired_width(width)
                .min_size(vec2(width, CONTROL_H))
                .margin(Margin {
                    left: 6,
                    right: 22,
                    top: 6,
                    bottom: 6,
                })
                .vertical_align(Align::Center)
                .horizontal_align(Align::RIGHT)
                .font(mono()),
        )
    });
    if edit.changed() {
        ui.data_mut(|d| d.insert_temp(id, buffer.clone()));
        let parsed = buffer.trim().replace(',', ".").parse::<f64>();
        if let Ok(v) = parsed
            && v >= 0.0
            && (!T::WHOLE || v.fract() == 0.0)
        {
            *value = T::from_f64(v);
            changed = true;
        }
    }
    // The arrows.
    let arrows = Rect::from_min_max(
        pos2(rect.right() - 20.0, rect.top() + 4.0),
        pos2(rect.right() - 3.0, rect.bottom() - 4.0),
    );
    ui.painter()
        .rect_filled(arrows, CornerRadius::same(3), CHIP);
    let enabled = ui.is_enabled();
    for (i, up) in [(0, true), (1, false)] {
        let half = Rect::from_min_size(
            pos2(
                arrows.left(),
                arrows.top() + i as f32 * arrows.height() / 2.0,
            ),
            vec2(arrows.width(), arrows.height() / 2.0),
        );
        let r = ui.interact(half, id.with(i), Sense::click());
        let c = half.center();
        let color = if r.hovered() && enabled { TEXT } else { MUTED };
        let d = if up { -1.5 } else { 1.5 };
        ui.painter().add(Shape::line(
            vec![
                pos2(c.x - 3.5, c.y - d),
                pos2(c.x, c.y + d),
                pos2(c.x + 3.5, c.y - d),
            ],
            Stroke::new(1.2, color),
        ));
        if r.clicked() {
            let next = value.to_f64() + if up { step } else { -step };
            *value = T::from_f64(next.max(0.0));
            ui.memory_mut(|m| m.surrender_focus(edit_id));
            changed = true;
        }
    }
    changed
}

pub fn check(ui: &mut Ui, label: &str, value: &mut bool) -> Response {
    let (rect, mut response) = ui.allocate_exact_size(vec2(20.0, 20.0), Sense::click());
    let enabled = ui.is_enabled();
    if response.clicked() {
        *value = !*value;
        response.mark_changed();
    }
    let v = *value;
    response.widget_info(|| WidgetInfo::selected(WidgetType::Checkbox, enabled, v, label));
    let color = if enabled { ACCENT } else { FAINT };
    let painter = ui.painter();
    if v {
        painter.rect_filled(rect, CornerRadius::same(4), color);
        let c = rect.center();
        painter.add(Shape::line(
            vec![
                pos2(c.x - 4.5, c.y + 0.3),
                pos2(c.x - 1.2, c.y + 3.6),
                pos2(c.x + 4.8, c.y - 3.4),
            ],
            Stroke::new(2.0, Color32::WHITE),
        ));
    } else {
        let line = if response.hovered() && enabled {
            ACCENT
        } else {
            FIELD_LINE
        };
        painter.rect(
            rect,
            CornerRadius::same(4),
            PANEL,
            Stroke::new(1.5, line),
            StrokeKind::Inside,
        );
    }
    response
}

/// A 0..1 slider.
pub fn slider(ui: &mut Ui, id_label: &str, value: &mut f32, width: f32) -> Response {
    let (rect, mut response) =
        ui.allocate_exact_size(vec2(width, CONTROL_H), Sense::click_and_drag());
    let radius = 9.0;
    let rail = rect.shrink2(vec2(radius, 0.0));
    if let Some(p) = response.interact_pointer_pos()
        && ui.is_enabled()
    {
        let v = ((p.x - rail.left()) / rail.width()).clamp(0.0, 1.0);
        let v = (v * 100.0).round() / 100.0;
        if v != *value {
            *value = v;
            response.mark_changed();
        }
    }
    let v = *value;
    response.widget_info(|| WidgetInfo::slider(ui.is_enabled(), f64::from(v), id_label));
    let x = rail.left() + rail.width() * value.clamp(0.0, 1.0);
    let y = rect.center().y;
    let painter = ui.painter();
    let bar = |a: f32, b: f32, color| {
        painter.rect_filled(
            Rect::from_min_max(pos2(a, y - 2.5), pos2(b, y + 2.5)),
            CornerRadius::same(3),
            color,
        );
    };
    bar(rail.left() - radius, rail.right() + radius, LINE);
    bar(rail.left() - radius, x, ACCENT);
    painter.circle(
        pos2(x, y),
        radius,
        PANEL,
        Stroke::new(
            1.5,
            if response.dragged() {
                ACCENT
            } else {
                FIELD_LINE
            },
        ),
    );
    response
}

/// A drop-down over `options`. Returns true when the value changed.
pub fn select<T: PartialEq + Copy>(
    ui: &mut Ui,
    id: impl Hash + std::fmt::Debug,
    value: &mut T,
    options: &[(T, &str)],
    width: f32,
) -> bool {
    let current = options
        .iter()
        .find(|(v, _)| v == value)
        .map_or("", |(_, name)| name);
    let before = *value;
    egui::ComboBox::from_id_salt(id)
        .selected_text(RichText::new(current).font(body()).color(TEXT))
        .width(width)
        .show_ui(ui, |ui| {
            for (v, name) in options {
                ui.selectable_value(value, *v, RichText::new(*name).font(body()).color(TEXT));
            }
        });
    *value != before
}

/// The tab strip's pill.
pub fn tab(ui: &mut Ui, label: &str, selected: bool) -> Response {
    let galley = ui
        .painter()
        .layout_no_wrap(label.to_owned(), body(), Color32::PLACEHOLDER);
    let size = vec2(galley.size().x + 30.0, 37.0);
    let (rect, response) = ui.allocate_exact_size(size, Sense::click());
    response.widget_info(|| WidgetInfo::selected(WidgetType::Button, true, selected, label));
    let painter = ui.painter();
    if selected {
        painter.rect(
            rect,
            CornerRadius::same(RADIUS),
            PANEL,
            Stroke::new(1.0, LINE),
            StrokeKind::Inside,
        );
    } else if response.hovered() {
        painter.rect_filled(rect, CornerRadius::same(RADIUS), LINE_SOFT);
    }
    painter.text(
        rect.center(),
        Align2::CENTER_CENTER,
        label,
        body(),
        if selected { TEXT } else { MUTED },
    );
    response.on_hover_cursor(egui::CursorIcon::PointingHand)
}

/// A small toggle in a toolbar (log filters, online / everyone).
pub fn chip(ui: &mut Ui, label: &str, selected: bool) -> Response {
    let font = if selected { semi(14.0) } else { small() };
    let galley = ui
        .painter()
        .layout_no_wrap(label.to_owned(), font.clone(), Color32::PLACEHOLDER);
    let size = vec2(galley.size().x + 22.0, 31.0);
    let (rect, response) = ui.allocate_exact_size(size, Sense::click());
    response.widget_info(|| WidgetInfo::selected(WidgetType::Button, true, selected, label));
    let painter = ui.painter();
    if selected {
        painter.rect(
            rect,
            CornerRadius::same(FIELD_RADIUS),
            PANEL,
            Stroke::new(1.0, LINE),
            StrokeKind::Inside,
        );
    } else if response.hovered() {
        painter.rect_filled(rect, CornerRadius::same(FIELD_RADIUS), LINE_SOFT);
    }
    painter.text(
        rect.center(),
        Align2::CENTER_CENTER,
        label,
        font,
        if selected { TEXT } else { MUTED },
    );
    response.on_hover_cursor(egui::CursorIcon::PointingHand)
}

/// A collapsible group of form rows.
pub fn section(
    ui: &mut Ui,
    collapsed: &mut std::collections::BTreeSet<String>,
    title: &str,
    first: bool,
    body: impl FnOnce(&mut Ui),
) {
    if !first {
        hline(ui, LINE_SOFT);
    }
    let is_open = !collapsed.contains(title);
    let response = row(ui, 36.0, None, |ui, rect| {
        let c = pos2(rect.left() + 24.0, rect.center().y);
        let points = if is_open {
            vec![
                pos2(c.x - 4.5, c.y - 3.0),
                pos2(c.x + 4.5, c.y - 3.0),
                pos2(c.x, c.y + 3.5),
            ]
        } else {
            vec![
                pos2(c.x - 3.0, c.y - 4.5),
                pos2(c.x + 3.5, c.y),
                pos2(c.x - 3.0, c.y + 4.5),
            ]
        };
        ui.painter()
            .add(Shape::convex_polygon(points, MUTED, Stroke::NONE));
        cell(ui, rect, 42.0, 300.0, text(title));
    });
    if response.clicked() {
        if is_open {
            collapsed.insert(title.to_owned());
        } else {
            collapsed.remove(title);
        }
    }
    if is_open {
        Frame::new()
            .inner_margin(Margin {
                left: 42,
                right: 12,
                top: 2,
                bottom: 10,
            })
            .show(ui, |ui| {
                ui.set_width(ui.available_width());
                body(ui);
            });
    }
}

/// A form row: a label, then widgets.
pub fn field(ui: &mut Ui, label: &str, label_width: f32, f: impl FnOnce(&mut Ui)) {
    ui.horizontal(|ui| {
        let (rect, _) = ui.allocate_exact_size(vec2(label_width, CONTROL_H), Sense::hover());
        ui.painter().text(
            pos2(rect.left(), rect.center().y),
            Align2::LEFT_CENTER,
            label,
            body(),
            TEXT,
        );
        f(ui);
    });
}

/// A line of small text under a form row's widget.
pub fn hint(ui: &mut Ui, label_width: f32, t: impl Into<WidgetText>) {
    ui.horizontal(|ui| {
        ui.add_space(label_width);
        ui.add(Label::new(t).wrap());
    });
}

/// `Bandage #52`-style item search with a drop-down of matches. Returns the item the user picked.
pub struct ItemPicker<'a> {
    pub id: &'a str,
    pub query: &'a mut String,
    pub items: &'a BTreeMap<i32, String>,
    pub width: f32,
    pub hint: &'a str,
}

fn matches<'i>(items: &'i BTreeMap<i32, String>, query: &str) -> Vec<(i32, &'i str)> {
    let q = query.trim().to_lowercase();
    if q.is_empty() {
        return Vec::new();
    }
    let number = q.trim_start_matches('#').parse::<i32>().ok();
    let mut found: Vec<(i32, &str)> = items
        .iter()
        .filter(|(id, name)| Some(**id) == number || name.to_lowercase().contains(&q))
        .map(|(id, name)| (*id, name.as_str()))
        .collect();
    found.sort_by_key(|(_, name)| (!name.to_lowercase().starts_with(&q), name.len()));
    found.truncate(8);
    found
}

impl ItemPicker<'_> {
    pub fn show(self, ui: &mut Ui) -> Option<i32> {
        let id = Id::new(("item-picker", self.id));
        let edit = input(ui, self.query, self.width, self.hint);
        let hover_id = id.with("hover");
        let selected_id = id.with("selected");
        let popup_hovered = ui.data(|d| d.get_temp::<bool>(hover_id)).unwrap_or(false);
        let found = matches(self.items, self.query);
        let exact = found.len() == 1 && found[0].1.eq_ignore_ascii_case(self.query.trim());
        if !(edit.has_focus() || popup_hovered) || found.is_empty() || exact {
            ui.data_mut(|d| d.insert_temp(hover_id, false));
            return None;
        }
        let mut selected = ui
            .data(|d| d.get_temp::<usize>(selected_id))
            .unwrap_or(0)
            .min(found.len() - 1);
        let mut picked = None;
        ui.input_mut(|i| {
            if i.consume_key(egui::Modifiers::NONE, Key::ArrowDown) {
                selected = (selected + 1).min(found.len() - 1);
            }
            if i.consume_key(egui::Modifiers::NONE, Key::ArrowUp) {
                selected = selected.saturating_sub(1);
            }
            if i.events.iter().any(|e| {
                matches!(
                    e,
                    Event::Key {
                        key: Key::Enter,
                        pressed: true,
                        ..
                    }
                )
            }) {
                picked = Some(found[selected].0);
            }
        });
        let area = egui::Area::new(id.with("popup"))
            .order(Order::Foreground)
            .fixed_pos(edit.rect.left_bottom() + vec2(0.0, 3.0))
            .show(ui.ctx(), |ui| {
                Frame::popup(ui.style())
                    .fill(PANEL)
                    .corner_radius(CornerRadius::same(FIELD_RADIUS))
                    .inner_margin(Margin::same(3))
                    .show(ui, |ui| {
                        ui.spacing_mut().item_spacing.y = 0.0;
                        ui.set_width(edit.rect.width() - 8.0);
                        for (i, (item, name)) in found.iter().enumerate() {
                            let r = row(
                                ui,
                                29.0,
                                Some(if i == selected { SELECTED } else { PANEL }),
                                |ui, rect| {
                                    cell(ui, rect, 10.0, rect.width() - 70.0, text(*name));
                                    cell_right(
                                        ui,
                                        rect,
                                        rect.width() - 10.0,
                                        muted(format!("#{item}")),
                                    );
                                },
                            );
                            if r.clicked() {
                                picked = Some(*item);
                            }
                            if r.hovered() {
                                selected = i;
                            }
                        }
                    });
            });
        let hovered = area.response.contains_pointer();
        ui.data_mut(|d| {
            d.insert_temp(hover_id, hovered && picked.is_none());
            d.insert_temp(selected_id, selected);
        });
        if picked.is_some() {
            edit.request_focus();
        }
        picked
    }
}

/// Lets callers place a circle diagram without pulling in painter details.
pub fn ring_circle(ui: &Ui, center: Pos2, radius: f32, highlighted: bool) {
    let stroke = if highlighted {
        Stroke::new(2.5, ACCENT)
    } else {
        Stroke::new(1.2, FAINT)
    };
    ui.painter().circle_stroke(center, radius, stroke);
}
