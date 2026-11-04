//! The drop plane (Landfall's `Dropper`): flies a straight line for 45 s, then everyone still on
//! board is dropped.

use crate::rng::Rng;

/// How long the plane flies, in seconds.
pub const FLIGHT_TIME: f32 = 45.0;
/// How often the server re-syncs the plane's progress.
pub const SYNC_INTERVAL: f32 = 5.0;
const ALTITUDE: f32 = 200.0;
const HALF_LENGTH: f32 = 1200.0;
const SPREAD: f32 = 1000.0;

#[derive(Clone, Debug, PartialEq)]
pub struct Plane {
    pub start: [f32; 3],
    pub end: [f32; 3],
    pub took_off_at: f32,
    pub last_sync: f32,
    pub finished: bool,
}

impl Plane {
    /// A path across the map through the first ring's centre line, along a random axis and
    /// direction, like the official server.
    pub fn take_off(ring_center: [f32; 3], now: f32, rng: &mut Rng) -> Self {
        let (start, end) = if rng.unit() > 0.5 {
            let (from, to) = if rng.unit() > 0.5 {
                (-HALF_LENGTH, HALF_LENGTH)
            } else {
                (HALF_LENGTH, -HALF_LENGTH)
            };
            let z0 = rng.range(-SPREAD, SPREAD) + ring_center[2];
            let z1 = rng.range(-SPREAD, SPREAD) + ring_center[2];
            ([from, ALTITUDE, z0], [to, ALTITUDE, z1])
        } else {
            let (from, to) = if rng.unit() > 0.5 {
                (HALF_LENGTH, -HALF_LENGTH)
            } else {
                (-HALF_LENGTH, HALF_LENGTH)
            };
            let x0 = rng.range(-SPREAD, SPREAD) + ring_center[0];
            let x1 = rng.range(-SPREAD, SPREAD) + ring_center[0];
            ([x0, ALTITUDE, from], [x1, ALTITUDE, to])
        };
        Self {
            start,
            end,
            took_off_at: now,
            last_sync: now,
            finished: false,
        }
    }

    /// 0 at take-off, 1 at the end of the path.
    pub fn progress(&self, now: f32) -> f32 {
        ((now - self.took_off_at) / FLIGHT_TIME).clamp(0.0, 1.0)
    }

    /// Where a player who jumps now appears: the plane's position, 5 m higher (as the official
    /// `GetDropPosition` computes it).
    pub fn drop_position(&self, now: f32) -> [f32; 3] {
        let t = self.progress(now);
        let lerp = |a: f32, b: f32| a + (b - a) * t;
        [
            lerp(self.start[0], self.end[0]),
            lerp(self.start[1], self.end[1]) + 5.0,
            lerp(self.start[2], self.end[2]),
        ]
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn path_crosses_the_ring_line_at_altitude() {
        let mut rng = Rng::new(7);
        for _ in 0..50 {
            let p = Plane::take_off([100.0, 0.0, -50.0], 10.0, &mut rng);
            assert_eq!((p.start[1], p.end[1]), (200.0, 200.0));
            let along_x = p.start[0].abs() == HALF_LENGTH;
            if along_x {
                assert_eq!(p.start[0], -p.end[0]);
                assert!((p.start[2] + 50.0).abs() <= SPREAD);
            } else {
                assert_eq!(p.start[2], -p.end[2]);
                assert!((p.start[0] - 100.0).abs() <= SPREAD);
            }
        }
    }

    #[test]
    fn progress_and_drop_position() {
        let p = Plane {
            start: [-1200.0, 200.0, 0.0],
            end: [1200.0, 200.0, 0.0],
            took_off_at: 0.0,
            last_sync: 0.0,
            finished: false,
        };
        assert_eq!(p.progress(22.5), 0.5);
        assert_eq!(p.drop_position(22.5), [0.0, 205.0, 0.0]);
        assert_eq!(p.progress(100.0), 1.0);
    }
}
