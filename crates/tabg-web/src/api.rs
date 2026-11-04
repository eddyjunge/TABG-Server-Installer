//! JSON API for the `tabg` CLI (same token as the dashboard, sent as a Bearer header).
// Handlers return `Result<Json, Response>` (axum's usual error shape); boxing it buys nothing here.
#![allow(clippy::result_large_err)]

use std::sync::Arc;

use axum::Json;
use axum::Router;
use axum::extract::{Query, State};
use axum::http::StatusCode;
use axum::response::{IntoResponse, Response};
use axum::routing::{get, post};
use serde::Deserialize;
use serde_json::{Value, json};
use tabg_host::apply::{self, ApplyError};
use tabg_host::{ServerToml, backup, config, presets};

use crate::AppState;

type ApiResult = Result<Json<Value>, Response>;

fn err(status: StatusCode, message: impl std::fmt::Display) -> Response {
    (status, Json(json!({ "error": message.to_string() }))).into_response()
}

fn bad(message: impl std::fmt::Display) -> Response {
    err(StatusCode::BAD_REQUEST, message)
}

/// Run blocking host work (process control, file IO) off the async runtime.
async fn blocking<T: Send + 'static>(f: impl FnOnce() -> T + Send + 'static) -> T {
    tokio::task::spawn_blocking(f).await.expect("task ran")
}

pub fn routes() -> Router<AppState> {
    Router::new()
        .route("/status", get(status))
        .route("/start", post(start))
        .route("/stop", post(stop))
        .route("/restart", post(restart))
        .route("/console", post(console))
        .route("/logs", get(logs))
        .route("/config", get(get_config).post(put_config))
        .route("/config/get", get(get_key))
        .route("/config/set", post(set_key))
        .route("/presets", get(list_presets))
        .route("/presets/apply", post(apply_preset))
        .route("/presets/save", post(save_preset))
        .route("/backups", get(list_backups))
        .route("/backups/restore", post(restore_backup))
        .route("/players", get(players))
        .route("/players/admin", post(set_admin))
        .route("/players/ban", post(set_ban))
        .route("/setup", post(setup))
}

pub fn apply_result(r: Result<apply::Applied, ApplyError>) -> ApiResult {
    match r {
        Ok(applied) => Ok(Json(json!({ "applied": applied }))),
        Err(ApplyError::Invalid { problems }) => Err((
            StatusCode::UNPROCESSABLE_ENTITY,
            Json(json!({ "error": "invalid config", "problems": problems })),
        )
            .into_response()),
        Err(ApplyError::Failed { error }) => Err(bad(error)),
    }
}

async fn status(State(app): State<AppState>) -> Json<Value> {
    let install = tabg_host::setup::Install::load(&app.paths);
    Json(json!({
        "server": app.supervisor.status(),
        "setup": {
            "ready": install.ready(),
            "busy": *app.setup_busy.lock().unwrap_or_else(|e| e.into_inner()),
            "app_version": install.app_version,
            "client_dir": install.client_dir,
            "server_dir": install.server_dir,
        },
        "data_dir": app.paths.root,
    }))
}

async fn start(State(app): State<AppState>) -> Json<Value> {
    let sup = app.supervisor.clone();
    blocking(move || sup.start()).await;
    Json(json!({ "server": app.supervisor.status() }))
}

async fn stop(State(app): State<AppState>) -> Json<Value> {
    let sup = app.supervisor.clone();
    blocking(move || sup.stop()).await;
    Json(json!({ "server": app.supervisor.status() }))
}

async fn restart(State(app): State<AppState>) -> Json<Value> {
    let sup = app.supervisor.clone();
    blocking(move || sup.restart()).await;
    Json(json!({ "server": app.supervisor.status() }))
}

#[derive(Deserialize)]
struct Line {
    line: String,
}

async fn console(State(app): State<AppState>, Json(body): Json<Line>) -> ApiResult {
    app.supervisor.console(&body.line).map_err(bad)?;
    Ok(Json(json!({ "ok": true })))
}

#[derive(Deserialize)]
struct LogsQuery {
    #[serde(default = "default_lines")]
    n: usize,
    /// Only lines after this sequence number.
    after: Option<u64>,
}

fn default_lines() -> usize {
    100
}

async fn logs(State(app): State<AppState>, Query(q): Query<LogsQuery>) -> Json<Value> {
    let lines = match q.after {
        Some(seq) => app.supervisor.logs_after(seq, q.n),
        None => app.supervisor.logs(q.n),
    };
    Json(json!({ "lines": lines }))
}

async fn get_config(State(app): State<AppState>) -> ApiResult {
    let c = apply::read_config(&app.paths).map_err(bad)?;
    Ok(Json(
        json!({ "toml": c.to_toml(), "config": c, "problems": c.validate() }),
    ))
}

#[derive(Deserialize)]
struct ConfigText {
    toml: String,
}

async fn put_config(State(app): State<AppState>, Json(body): Json<ConfigText>) -> ApiResult {
    let next = ServerToml::parse(&body.toml).map_err(bad)?;
    let app2 = Arc::clone(&app);
    apply_result(blocking(move || apply::apply(&app2.paths, &app2.supervisor, &next, "edit")).await)
}

#[derive(Deserialize)]
struct Key {
    key: String,
}

async fn get_key(State(app): State<AppState>, Query(q): Query<Key>) -> ApiResult {
    let c = apply::read_config(&app.paths).map_err(bad)?;
    Ok(Json(
        json!({ "value": config::get_key(&c, &q.key).map_err(bad)? }),
    ))
}

#[derive(Deserialize)]
struct KeyValue {
    key: String,
    value: String,
}

async fn set_key(State(app): State<AppState>, Json(body): Json<KeyValue>) -> ApiResult {
    let c = apply::read_config(&app.paths).map_err(bad)?;
    let next = config::set_key(&c, &body.key, &body.value).map_err(bad)?;
    let app2 = Arc::clone(&app);
    let reason = format!("set {}", body.key);
    apply_result(
        blocking(move || apply::apply(&app2.paths, &app2.supervisor, &next, &reason)).await,
    )
}

async fn list_presets(State(app): State<AppState>) -> Json<Value> {
    Json(json!({ "presets": presets::all(&app.paths.presets()) }))
}

#[derive(Deserialize)]
struct Id {
    id: String,
}

async fn apply_preset(State(app): State<AppState>, Json(body): Json<Id>) -> ApiResult {
    let preset = presets::find(&app.paths.presets(), &body.id)
        .ok_or_else(|| bad(format!("no preset {:?}", body.id)))?;
    let c = apply::read_config(&app.paths).map_err(bad)?;
    let next = presets::apply(&c, &preset).map_err(bad)?;
    let app2 = Arc::clone(&app);
    let reason = format!("preset {}", body.id);
    apply_result(
        blocking(move || apply::apply(&app2.paths, &app2.supervisor, &next, &reason)).await,
    )
}

#[derive(Deserialize)]
struct SavePreset {
    id: String,
    #[serde(default)]
    title: String,
    #[serde(default)]
    description: String,
}

async fn save_preset(State(app): State<AppState>, Json(body): Json<SavePreset>) -> ApiResult {
    let c = apply::read_config(&app.paths).map_err(bad)?;
    let title = if body.title.is_empty() {
        body.id.clone()
    } else {
        body.title
    };
    presets::save(
        &app.paths.presets(),
        &body.id,
        &title,
        &body.description,
        &c,
    )
    .map_err(bad)?;
    Ok(Json(json!({ "saved": body.id })))
}

async fn list_backups(State(app): State<AppState>) -> Json<Value> {
    Json(json!({ "backups": backup::list(&app.paths) }))
}

async fn restore_backup(State(app): State<AppState>, Json(body): Json<Id>) -> ApiResult {
    backup::restore(&app.paths, &body.id).map_err(bad)?;
    // Make a running server follow the restored config.
    let c = apply::read_config(&app.paths).map_err(bad)?;
    let app2 = Arc::clone(&app);
    let reason = format!("restore {}", body.id);
    apply_result(blocking(move || apply::apply(&app2.paths, &app2.supervisor, &c, &reason)).await)
}

async fn players(State(app): State<AppState>) -> ApiResult {
    let c = apply::read_config(&app.paths).map_err(bad)?;
    Ok(Json(json!({
        "online": app.supervisor.status().players,
        "history": app.supervisor.player_history(),
        "admins": c.players.admins,
        "banned": c.players.banned,
    })))
}

#[derive(Deserialize)]
pub struct Grant {
    pub who: String,
    pub on: bool,
}

/// Add or remove `who` (a name or id; names resolve to the id when known) in a player list.
pub fn grant(
    app: &AppState,
    grant: &Grant,
    list: fn(&mut ServerToml) -> &mut Vec<String>,
    what: &str,
) -> Result<apply::Applied, String> {
    let who = grant.who.trim();
    if who.is_empty() {
        return Err("who?".into());
    }
    let id = app
        .supervisor
        .resolve_player(who)
        .unwrap_or_else(|| who.to_owned());
    let mut c = apply::read_config(&app.paths)?;
    let entries = list(&mut c);
    entries.retain(|e| e != &id && !e.eq_ignore_ascii_case(who));
    if grant.on {
        entries.push(id.clone());
    }
    let verb = if grant.on { "add" } else { "remove" };
    apply::apply(
        &app.paths,
        &app.supervisor,
        &c,
        &format!("{verb} {what} {id}"),
    )
    .map_err(|e| e.to_string())
}

async fn set_admin(State(app): State<AppState>, Json(body): Json<Grant>) -> ApiResult {
    let app2 = Arc::clone(&app);
    let r = blocking(move || grant(&app2, &body, |c| &mut c.players.admins, "admin")).await;
    Ok(Json(json!({ "applied": r.map_err(bad)? })))
}

async fn set_ban(State(app): State<AppState>, Json(body): Json<Grant>) -> ApiResult {
    let kick = body.on.then(|| body.who.clone());
    let app2 = Arc::clone(&app);
    let r = blocking(move || grant(&app2, &body, |c| &mut c.players.banned, "ban")).await;
    let applied = r.map_err(bad)?;
    if let Some(who) = kick {
        let _ = app.supervisor.console(&format!("kick {who}"));
    }
    Ok(Json(json!({ "applied": applied })))
}

#[derive(Deserialize)]
struct SetupBody {
    #[serde(default)]
    force: bool,
}

async fn setup(State(app): State<AppState>, Json(body): Json<SetupBody>) -> ApiResult {
    app.start_setup(body.force).map_err(bad)?;
    Ok(Json(json!({ "started": true })))
}
