//! TABG game simulation.
//!
//! [`Room`] is pure: it takes player connections, packets and the current time, and returns the
//! packets to send. No sockets, threads or clocks, so every rule is unit-testable.
//!
//! Current scope: accepting players, sending them the world, replicating movement, and the match
//! flow (countdown, plane, drops, ring, end). Loot, weapons and damage come next.

pub mod cars;
pub mod loot;
pub mod plane;
pub mod ring;
pub mod rng;
mod room;
mod settings;

pub use ring::RingRules;
pub use room::{Outgoing, RESTART_AFTER_END, Recipients, Room};
pub use settings::{ForceStart, RoomSettings, SpawnPoint};
