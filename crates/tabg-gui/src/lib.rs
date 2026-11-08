//! The `tabg` window: a native front end for the host, Rust all the way down (eframe/egui, no
//! webview). It owns the same [`tabg_web::App`] the local API serves, so the CLI keeps working
//! next to it.

mod app;
mod draft;
mod tabs;
mod theme;
mod widgets;
mod wizard;

use std::sync::Arc;

use eframe::egui;

pub use app::Gui;

/// What the window drives.
#[derive(Clone)]
pub struct Host {
    pub app: Arc<tabg_web::App>,
    pub runtime: tokio::runtime::Handle,
}

/// Whether a window can open at all (a server over SSH can't).
pub fn display_available() -> bool {
    if cfg!(target_os = "linux") {
        std::env::var_os("DISPLAY").is_some() || std::env::var_os("WAYLAND_DISPLAY").is_some()
    } else {
        true
    }
}

fn options(size: [f32; 2], min: [f32; 2]) -> eframe::NativeOptions {
    let mut viewport = egui::ViewportBuilder::default()
        .with_title("tabg")
        .with_app_id("tabg")
        .with_inner_size(size)
        .with_min_inner_size(min);
    if let Ok(icon) = eframe::icon_data::from_png_bytes(include_bytes!("../assets/icon.png")) {
        viewport = viewport.with_icon(icon);
    }
    eframe::NativeOptions {
        viewport,
        ..Default::default()
    }
}

/// Open the window and run until it closes.
pub fn run(host: Host) -> Result<(), String> {
    eframe::run_native(
        "tabg",
        options([1370.0, 896.0], [1080.0, 640.0]),
        Box::new(move |cc| {
            theme::install(&cc.egui_ctx);
            Ok(Box::new(Gui::new(host, cc.egui_ctx.clone())))
        }),
    )
    .map_err(|e| e.to_string())
}

/// Another tabg already hosts from this data folder: say so instead of fighting over it.
pub fn run_already_running(url: String) -> Result<(), String> {
    struct Notice {
        url: String,
    }
    impl eframe::App for Notice {
        fn ui(&mut self, ui: &mut egui::Ui, _frame: &mut eframe::Frame) {
            wizard::already_running(ui, &self.url);
        }
    }
    eframe::run_native(
        "tabg",
        options([460.0, 300.0], [420.0, 260.0]),
        Box::new(move |cc| {
            theme::install(&cc.egui_ctx);
            Ok(Box::new(Notice { url }))
        }),
    )
    .map_err(|e| e.to_string())
}

#[cfg(test)]
mod tests;
