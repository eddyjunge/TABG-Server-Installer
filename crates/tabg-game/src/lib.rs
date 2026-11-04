//! TABG game simulation.
//!
//! [`Room`] is pure: it takes player connections, packets and the current time, and returns the
//! packets to send. No sockets, threads or clocks, so every rule is unit-testable.
//!
//! Current scope (M1): accepting players, sending them the world, and replicating movement in the
//! lobby. Loot, damage, the ring and the match flow come next.

mod room;
mod settings;

pub use room::{Outgoing, Recipients, Room};
pub use settings::{RoomSettings, SpawnPoint};
