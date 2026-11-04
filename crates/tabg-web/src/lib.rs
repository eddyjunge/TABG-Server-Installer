//! The dashboard (server-rendered HTML + htmx, live updates over SSE) and the local JSON API the
//! `tabg` CLI uses. Both listen on 127.0.0.1 only; see [`security`].

mod api;
mod pages;
pub mod security;

use std::collections::BTreeMap;
use std::convert::Infallible;
use std::sync::{Arc, Mutex};

use axum::Router;
use axum::extract::State;
use axum::http::header;
use axum::response::sse::{Event, KeepAlive, Sse};
use axum::response::{IntoResponse, Response};
use axum::routing::{get, post};
use tabg_host::supervisor::Update;
use tabg_host::{Paths, Supervisor};
use tokio_stream::StreamExt;
use tokio_stream::wrappers::BroadcastStream;

pub struct App {
    pub paths: Paths,
    pub supervisor: Supervisor,
    pub token: String,
    pub port: u16,
    /// A setup (map export) is running.
    pub setup_busy: Mutex<bool>,
}

pub type AppState = Arc<App>;

impl App {
    /// Item id → name from the map data, for loadout editing.
    pub fn items(&self) -> BTreeMap<i32, String> {
        tabg_host::setup::Install::load(&self.paths)
            .world
            .and_then(|w| tabg_world::World::load(&w).ok())
            .map(|w| {
                w.items
                    .into_iter()
                    .filter_map(|(k, v)| k.parse().ok().map(|k| (k, v)))
                    .collect()
            })
            .unwrap_or_default()
    }

    /// Run the map export in the background, reporting progress to the console log.
    pub fn start_setup(self: &Arc<Self>, force: bool) -> Result<(), String> {
        {
            let mut busy = self.setup_busy.lock().unwrap_or_else(|e| e.into_inner());
            if *busy {
                return Err("setup is already running".into());
            }
            *busy = true;
        }
        let app = Arc::clone(self);
        std::thread::spawn(move || {
            let sup = app.supervisor.clone();
            let result = tabg_host::setup::run(&app.paths, force, &mut |msg| {
                sup.host_log(format!("setup: {msg}"));
            });
            match result {
                Ok(_) => sup.host_log("setup: done, you can start the server"),
                Err(e) => sup.host_log(format!("setup failed: {e}")),
            }
            *app.setup_busy.lock().unwrap_or_else(|e| e.into_inner()) = false;
        });
        Ok(())
    }
}

const HTMX: &str = include_str!("../assets/htmx.min.js");
const HTMX_SSE: &str = include_str!("../assets/htmx-sse.js");

async fn script(body: &'static str) -> Response {
    (
        [
            (header::CONTENT_TYPE, "text/javascript; charset=utf-8"),
            (header::CACHE_CONTROL, "max-age=3600"),
        ],
        body,
    )
        .into_response()
}

/// Live updates: `status`, `players` and `log` events carrying HTML fragments.
async fn events(State(app): State<AppState>) -> impl IntoResponse {
    let first = {
        let status = app.supervisor.status();
        vec![
            Ok::<_, Infallible>(
                Event::default()
                    .event("status")
                    .data(pages::status_card(&status, &app).into_string()),
            ),
            Ok(Event::default()
                .event("players")
                .data(pages::online_table(&status).into_string())),
        ]
    };
    let app2 = Arc::clone(&app);
    let live = BroadcastStream::new(app.supervisor.subscribe()).filter_map(move |u| {
        let u = u.ok()?;
        Some(Ok(match u {
            Update::Status(s) => Event::default()
                .event("status")
                .data(pages::status_card(&s, &app2).into_string() + "\n"),
            Update::Log(line) => Event::default()
                .event("log")
                .data(pages::log_line(&line).into_string()),
        }))
    });
    // Player tables ride along with status updates.
    let players = BroadcastStream::new(app.supervisor.subscribe()).filter_map(|u| {
        let Ok(Update::Status(s)) = u else {
            return None;
        };
        Some(Ok(Event::default()
            .event("players")
            .data(pages::online_table(&s).into_string())))
    });
    let stream = tokio_stream::iter(first).chain(live.merge(players));
    Sse::new(stream).keep_alive(KeepAlive::default())
}

pub fn router(app: AppState) -> Router {
    Router::new()
        .route("/", get(pages::overview))
        .route("/console", get(pages::console))
        .route("/players", get(pages::players))
        .route("/settings", get(pages::settings))
        .route("/loadout", get(pages::loadout))
        .route("/presets", get(pages::presets))
        .route("/backups", get(pages::backups))
        .route("/events", get(events))
        .route("/assets/htmx.js", get(|| script(HTMX)))
        .route("/assets/htmx-sse.js", get(|| script(HTMX_SSE)))
        .route("/ui/{action}", post(pages::action))
        .nest("/api", api::routes())
        .layer(axum::middleware::from_fn_with_state(
            Arc::clone(&app),
            security::guard,
        ))
        .with_state(app)
}

/// Serve the dashboard on `listener` until `shutdown` resolves.
pub async fn serve(
    app: AppState,
    listener: tokio::net::TcpListener,
    shutdown: impl std::future::Future<Output = ()> + Send + 'static,
) -> std::io::Result<()> {
    axum::serve(listener, router(app))
        .with_graceful_shutdown(shutdown)
        .await
}

#[cfg(test)]
mod tests;
