//! Colours, fonts and egui's style for the window.

use std::sync::Arc;

use eframe::egui::{
    self, Color32, Context, CornerRadius, FontData, FontDefinitions, FontFamily, FontId, Margin,
    Shadow, Stroke, TextStyle, Theme, Vec2, vec2,
};

pub const BG: Color32 = Color32::from_rgb(0xea, 0xea, 0xea);
pub const PANEL: Color32 = Color32::WHITE;
pub const HEADER: Color32 = Color32::from_rgb(0xf6, 0xf6, 0xf6);
pub const LINE: Color32 = Color32::from_rgb(0xdc, 0xdc, 0xdc);
pub const LINE_SOFT: Color32 = Color32::from_rgb(0xec, 0xec, 0xec);
pub const FIELD_LINE: Color32 = Color32::from_rgb(0xc4, 0xc4, 0xc4);
pub const TEXT: Color32 = Color32::from_rgb(0x1c, 0x1c, 0x1c);
pub const MUTED: Color32 = Color32::from_rgb(0x6b, 0x6b, 0x6b);
pub const FAINT: Color32 = Color32::from_rgb(0x94, 0x94, 0x94);
pub const ACCENT: Color32 = Color32::from_rgb(0x25, 0x63, 0xeb);
pub const ACCENT_DARK: Color32 = Color32::from_rgb(0x1d, 0x4e, 0xd8);
pub const SELECTED: Color32 = Color32::from_rgb(0xdb, 0xe6, 0xfd);
pub const HOVER: Color32 = Color32::from_rgb(0xf3, 0xf5, 0xf9);
pub const DANGER: Color32 = Color32::from_rgb(0xb9, 0x1c, 0x1c);
pub const DANGER_DARK: Color32 = Color32::from_rgb(0x99, 0x1b, 0x1b);
pub const GOOD: Color32 = Color32::from_rgb(0x16, 0x83, 0x3a);
pub const WARN_BG: Color32 = Color32::from_rgb(0xfe, 0xf3, 0xdc);
pub const WARN_LINE: Color32 = Color32::from_rgb(0xf0, 0xdd, 0xb0);
pub const CHIP: Color32 = Color32::from_rgb(0xee, 0xee, 0xee);

pub const RADIUS: u8 = 8;
pub const FIELD_RADIUS: u8 = 6;
pub const CONTROL_H: f32 = 30.0;

pub fn body() -> FontId {
    FontId::proportional(15.0)
}

pub fn small() -> FontId {
    FontId::proportional(13.5)
}

pub fn semi(size: f32) -> FontId {
    FontId::new(size, FontFamily::Name("semi".into()))
}

pub fn mono() -> FontId {
    FontId::monospace(13.5)
}

fn fonts() -> FontDefinitions {
    let mut f = FontDefinitions::default();
    let mut add = |name: &str, data: &'static [u8]| {
        f.font_data
            .insert(name.to_owned(), Arc::new(FontData::from_static(data)));
    };
    add("inter", include_bytes!("../assets/fonts/Inter-Regular.ttf"));
    add(
        "inter-semi",
        include_bytes!("../assets/fonts/Inter-SemiBold.ttf"),
    );
    add(
        "jetbrains",
        include_bytes!("../assets/fonts/JetBrainsMono-Regular.ttf"),
    );
    // egui's own fonts stay as fallbacks: player names can be in any script.
    let proportional = f.families.entry(FontFamily::Proportional).or_default();
    proportional.insert(0, "inter".to_owned());
    let fallback = proportional.clone();
    let mono = f.families.entry(FontFamily::Monospace).or_default();
    mono.insert(0, "jetbrains".to_owned());
    let mut semi = vec!["inter-semi".to_owned()];
    semi.extend(fallback);
    f.families.insert(FontFamily::Name("semi".into()), semi);
    f
}

fn visuals(v: &mut egui::Visuals) {
    let radius = CornerRadius::same(FIELD_RADIUS);
    v.dark_mode = false;
    v.override_text_color = None;
    v.panel_fill = BG;
    v.window_fill = PANEL;
    v.window_stroke = Stroke::new(1.0, LINE);
    v.window_corner_radius = CornerRadius::same(RADIUS);
    v.menu_corner_radius = CornerRadius::same(RADIUS);
    let shadow = Shadow {
        offset: [0, 6],
        blur: 22,
        spread: 0,
        color: Color32::from_black_alpha(46),
    };
    v.window_shadow = shadow;
    v.popup_shadow = shadow;
    v.extreme_bg_color = PANEL;
    v.text_edit_bg_color = Some(PANEL);
    v.faint_bg_color = HEADER;
    v.hyperlink_color = ACCENT;
    v.error_fg_color = DANGER;
    v.selection.bg_fill = SELECTED;
    v.selection.stroke = Stroke::new(1.5, ACCENT);
    v.text_cursor.stroke = Stroke::new(1.5, TEXT);

    let w = &mut v.widgets;
    w.noninteractive.fg_stroke = Stroke::new(1.0, TEXT);
    w.noninteractive.bg_stroke = Stroke::new(1.0, LINE);
    w.noninteractive.bg_fill = PANEL;
    w.noninteractive.weak_bg_fill = PANEL;
    w.noninteractive.corner_radius = radius;
    for state in [&mut w.inactive, &mut w.hovered, &mut w.active, &mut w.open] {
        state.fg_stroke = Stroke::new(1.0, TEXT);
        state.bg_fill = PANEL;
        state.weak_bg_fill = PANEL;
        state.bg_stroke = Stroke::new(1.0, FIELD_LINE);
        state.corner_radius = radius;
        state.expansion = 0.0;
    }
    w.hovered.weak_bg_fill = HOVER;
    w.hovered.bg_fill = HOVER;
    w.hovered.bg_stroke = Stroke::new(1.0, Color32::from_rgb(0xa8, 0xa8, 0xa8));
    w.active.weak_bg_fill = SELECTED;
    w.active.bg_fill = SELECTED;
}

pub fn install(ctx: &Context) {
    ctx.set_fonts(fonts());
    ctx.set_theme(Theme::Light);
    ctx.style_mut_of(Theme::Light, |style| {
        visuals(&mut style.visuals);
        style.text_styles = [
            (TextStyle::Small, small()),
            (TextStyle::Body, body()),
            (TextStyle::Button, body()),
            (TextStyle::Heading, semi(19.0)),
            (TextStyle::Monospace, mono()),
        ]
        .into();
        let s = &mut style.spacing;
        s.item_spacing = vec2(8.0, 6.0);
        s.button_padding = vec2(10.0, 5.0);
        s.interact_size = Vec2::new(40.0, CONTROL_H);
        s.menu_margin = Margin::same(4);
        s.combo_width = 120.0;
        style.interaction.selectable_labels = false;
    });
}
