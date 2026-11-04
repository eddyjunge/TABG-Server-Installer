//! The tabg host: everything around the game server that the `tabg` binary and its dashboard
//! use. One server per data dir in v1.

pub mod apply;
pub mod backup;
pub mod config;
pub mod fetch;
pub mod ipc;
pub mod paths;
pub mod presets;
pub mod setup;
pub mod steam;
pub mod store;
pub mod supervisor;

pub use config::ServerToml;
pub use paths::Paths;
pub use supervisor::Supervisor;
