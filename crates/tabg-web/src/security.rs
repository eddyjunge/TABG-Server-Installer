//! The dashboard only listens on 127.0.0.1, but any web page the host visits could still send
//! requests to it. So (§5.5): the `Host` header must name us (no DNS rebinding), state-changing
//! requests must come from our own origin, and every request needs the session token: a cookie
//! set when opening the dashboard link, or `Authorization: Bearer` from the CLI.

use axum::extract::{Request, State};
use axum::http::{HeaderMap, Method, StatusCode, header};
use axum::middleware::Next;
use axum::response::{IntoResponse, Redirect, Response};

use crate::AppState;

pub const COOKIE: &str = "tabg_session";

pub fn new_token() -> String {
    let mut b = [0u8; 24];
    getrandom::fill(&mut b).expect("OS random source");
    b.iter().map(|x| format!("{x:02x}")).collect()
}

fn allowed_hosts(port: u16) -> [String; 2] {
    [format!("127.0.0.1:{port}"), format!("localhost:{port}")]
}

fn cookie_token(headers: &HeaderMap) -> Option<String> {
    headers
        .get_all(header::COOKIE)
        .iter()
        .filter_map(|v| v.to_str().ok())
        .flat_map(|v| v.split(';'))
        .filter_map(|kv| kv.trim().split_once('='))
        .find(|(k, _)| *k == COOKIE)
        .map(|(_, v)| v.to_owned())
}

fn bearer_token(headers: &HeaderMap) -> Option<String> {
    headers
        .get(header::AUTHORIZATION)?
        .to_str()
        .ok()?
        .strip_prefix("Bearer ")
        .map(str::to_owned)
}

/// Constant-time comparison.
fn same(a: &str, b: &str) -> bool {
    a.len() == b.len()
        && a.bytes()
            .zip(b.bytes())
            .fold(0u8, |acc, (x, y)| acc | (x ^ y))
            == 0
}

pub async fn guard(State(app): State<AppState>, req: Request, next: Next) -> Response {
    let headers = req.headers();
    let host = headers
        .get(header::HOST)
        .and_then(|h| h.to_str().ok())
        .unwrap_or("");
    if !allowed_hosts(app.port).iter().any(|h| h == host) {
        return (StatusCode::MISDIRECTED_REQUEST, "unknown host").into_response();
    }
    let state_changing = !matches!(*req.method(), Method::GET | Method::HEAD);
    if state_changing && let Some(origin) = headers.get(header::ORIGIN) {
        let ok = origin.to_str().ok().is_some_and(|o| {
            allowed_hosts(app.port)
                .iter()
                .any(|h| o == format!("http://{h}"))
        });
        if !ok {
            return (StatusCode::FORBIDDEN, "cross-origin request refused").into_response();
        }
    }
    // Opening the dashboard link: trade the token in the URL for a cookie.
    if req.method() == Method::GET
        && let Some(token) = req.uri().query().and_then(|q| {
            q.split('&')
                .filter_map(|kv| kv.split_once('='))
                .find(|(k, _)| *k == "token")
                .map(|(_, v)| v.to_owned())
        })
    {
        if !same(&token, &app.token) {
            return (
                StatusCode::UNAUTHORIZED,
                "wrong dashboard link; use the one tabg printed",
            )
                .into_response();
        }
        let mut response = Redirect::to(req.uri().path()).into_response();
        response.headers_mut().insert(
            header::SET_COOKIE,
            format!("{COOKIE}={token}; HttpOnly; SameSite=Strict; Path=/")
                .parse()
                .expect("valid header"),
        );
        return response;
    }
    let token = cookie_token(headers).or_else(|| bearer_token(headers));
    if !token.is_some_and(|t| same(&t, &app.token)) {
        return (
            StatusCode::UNAUTHORIZED,
            "open the dashboard with the link tabg printed (it contains a key)",
        )
            .into_response();
    }
    next.run(req).await
}
