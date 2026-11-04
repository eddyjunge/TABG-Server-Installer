use std::process::Command;
use std::sync::{Arc, Mutex};

use axum::body::Body;
use axum::http::{Request, StatusCode, header};
use http_body_util::BodyExt;
use tabg_host::{Paths, Supervisor};
use tower::ServiceExt;

use crate::{App, router};

fn app() -> (Arc<App>, tempfile_dir::Dir) {
    let dir = tempfile_dir::Dir::new();
    let paths = Paths::new(dir.path().to_owned());
    let supervisor = Supervisor::new(paths.clone(), Arc::new(|| Command::new("false")));
    (
        Arc::new(App {
            paths,
            supervisor,
            token: "secret".into(),
            port: 7780,
            setup_busy: Mutex::new(false),
            share: Mutex::new(None),
            sync_port: Mutex::new(None),
        }),
        dir,
    )
}

mod tempfile_dir {
    /// A temp dir removed on drop (std only).
    pub struct Dir(std::path::PathBuf);
    impl Dir {
        pub fn new() -> Self {
            let mut b = [0u8; 8];
            getrandom::fill(&mut b).unwrap();
            let p = std::env::temp_dir().join(format!(
                "tabg-web-test-{}",
                b.iter().map(|x| format!("{x:02x}")).collect::<String>()
            ));
            std::fs::create_dir_all(&p).unwrap();
            Self(p)
        }
        pub fn path(&self) -> &std::path::Path {
            &self.0
        }
    }
    impl Drop for Dir {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
}

fn req(method: &str, uri: &str) -> axum::http::request::Builder {
    Request::builder()
        .method(method)
        .uri(uri)
        .header(header::HOST, "127.0.0.1:7780")
}

async fn send(app: &Arc<App>, r: Request<Body>) -> (StatusCode, String, axum::http::HeaderMap) {
    let res = router(Arc::clone(app)).oneshot(r).await.unwrap();
    let status = res.status();
    let headers = res.headers().clone();
    let body = res.into_body().collect().await.unwrap().to_bytes();
    (status, String::from_utf8_lossy(&body).into_owned(), headers)
}

#[tokio::test]
async fn needs_our_host_header() {
    let (app, _d) = app();
    let r = Request::builder()
        .uri("/")
        .header(header::HOST, "evil.example:7780")
        .header(header::COOKIE, "tabg_session=secret")
        .body(Body::empty())
        .unwrap();
    assert_eq!(send(&app, r).await.0, StatusCode::MISDIRECTED_REQUEST);
}

#[tokio::test]
async fn needs_the_token() {
    let (app, _d) = app();
    let (status, ..) = send(&app, req("GET", "/").body(Body::empty()).unwrap()).await;
    assert_eq!(status, StatusCode::UNAUTHORIZED);
    let (status, ..) = send(
        &app,
        req("GET", "/")
            .header(header::COOKIE, "tabg_session=nope")
            .body(Body::empty())
            .unwrap(),
    )
    .await;
    assert_eq!(status, StatusCode::UNAUTHORIZED);
}

#[tokio::test]
async fn dashboard_link_sets_the_cookie() {
    let (app, _d) = app();
    let (status, _, headers) = send(
        &app,
        req("GET", "/?token=secret").body(Body::empty()).unwrap(),
    )
    .await;
    assert_eq!(status, StatusCode::SEE_OTHER);
    let cookie = headers[header::SET_COOKIE].to_str().unwrap();
    assert!(cookie.starts_with("tabg_session=secret;") && cookie.contains("HttpOnly"));
    let (status, body, _) = send(
        &app,
        req("GET", "/settings")
            .header(header::COOKIE, "tabg_session=secret")
            .body(Body::empty())
            .unwrap(),
    )
    .await;
    assert_eq!(status, StatusCode::OK);
    assert!(body.contains("Save and apply"));
}

#[tokio::test]
async fn cross_origin_posts_are_refused() {
    let (app, _d) = app();
    let (status, ..) = send(
        &app,
        req("POST", "/api/stop")
            .header(header::COOKIE, "tabg_session=secret")
            .header(header::ORIGIN, "https://evil.example")
            .body(Body::empty())
            .unwrap(),
    )
    .await;
    assert_eq!(status, StatusCode::FORBIDDEN);
}

#[tokio::test]
async fn cli_uses_bearer_and_config_round_trips() {
    let (app, _d) = app();
    let auth = |b: axum::http::request::Builder| b.header(header::AUTHORIZATION, "Bearer secret");
    let (status, body, _) = send(
        &app,
        auth(req("GET", "/api/status")).body(Body::empty()).unwrap(),
    )
    .await;
    assert_eq!(status, StatusCode::OK, "{body}");
    assert!(body.contains("\"ready\":false"));
    let (status, body, _) = send(
        &app,
        auth(req("POST", "/api/config/set"))
            .header(header::CONTENT_TYPE, "application/json")
            .body(Body::from(r#"{"key":"game.max_players","value":"12"}"#))
            .unwrap(),
    )
    .await;
    assert_eq!(status, StatusCode::OK, "{body}");
    assert!(body.contains("saved"));
    let (_, body, _) = send(
        &app,
        auth(req("GET", "/api/config/get?key=game.max_players"))
            .body(Body::empty())
            .unwrap(),
    )
    .await;
    assert!(body.contains("\"12\""), "{body}");
    let (status, body, _) = send(
        &app,
        auth(req("POST", "/api/config/set"))
            .header(header::CONTENT_TYPE, "application/json")
            .body(Body::from(
                r#"{"key":"game.players_to_start","value":"99"}"#,
            ))
            .unwrap(),
    )
    .await;
    assert_eq!(status, StatusCode::UNPROCESSABLE_ENTITY);
    assert!(body.contains("game.players_to_start"), "{body}");
    // A backup was made before the change.
    let (_, body, _) = send(
        &app,
        auth(req("GET", "/api/backups"))
            .body(Body::empty())
            .unwrap(),
    )
    .await;
    assert!(body.contains("before set game.max_players"), "{body}");
}

#[tokio::test]
async fn ui_forms_apply_settings_and_presets() {
    let (app, _d) = app();
    let cookie = |b: axum::http::request::Builder| {
        b.header(header::COOKIE, "tabg_session=secret")
            .header(header::CONTENT_TYPE, "application/x-www-form-urlencoded")
    };
    let (status, body, _) = send(
        &app,
        cookie(req("POST", "/ui/preset_apply"))
            .body(Body::from("id=quick-match"))
            .unwrap(),
    )
    .await;
    assert_eq!(status, StatusCode::OK, "{body}");
    let c = tabg_host::apply::read_config(&app.paths).unwrap();
    assert_eq!(c.preset, "quick-match");
    let (_, body, _) = send(
        &app,
        cookie(req("POST", "/ui/ring"))
            .body(Body::from("times=10,10&sizes=&speeds="))
            .unwrap(),
    )
    .await;
    assert!(body.contains("Saved"), "{body}");
    assert_eq!(
        tabg_host::apply::read_config(&app.paths)
            .unwrap()
            .ring
            .times,
        vec![10.0, 10.0]
    );
    let (_, body, _) = send(
        &app,
        cookie(req("POST", "/ui/loadout"))
            .body(Body::from(
                "item_0=160&qty_0=1&item_1=&qty_1=1&item_2=6&qty_2=240",
            ))
            .unwrap(),
    )
    .await;
    assert!(body.contains("Saved"), "{body}");
    assert_eq!(
        tabg_host::apply::read_config(&app.paths)
            .unwrap()
            .loadout
            .len(),
        2
    );
}
