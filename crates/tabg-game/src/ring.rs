//! The ring (Landfall's `TheRing`), reduced to what the server decides: where each white ring is
//! and when the blue ring starts shrinking toward it. Clients animate the shrink themselves.
//!
//! Timeline, as on the official server:
//! - Match start: announce ring 1 (picked around the map centre) and start shrinking toward it.
//! - A shrink takes `base_time / speed[id]` seconds.
//! - After a shrink: 25 s later the next ring is announced; the next shrink starts once
//!   `ring_times[id] + 20 s` (+40 s after ring 1) have passed since the previous one stopped.

use crate::rng::Rng;

#[derive(Clone, Debug, PartialEq)]
pub struct RingRules {
    pub sizes: Vec<f32>,
    pub speeds: Vec<f32>,
    /// Pause after each ring (from the map's ring prefab).
    pub ring_times: Vec<f32>,
    pub base_time: f32,
    /// Where ring placement starts (the map origin on TABG's island).
    pub center: [f32; 3],
    /// Preferred centres for rings after the 2nd (may be empty).
    pub hot_spots: Vec<[f32; 3]>,
    /// Every ring is centred here instead of at a random spot (a fixed arena).
    pub fixed_center: Option<[f32; 3]>,
}

impl Default for RingRules {
    fn default() -> Self {
        Self {
            sizes: vec![4240.0, 3450.0, 1710.0, 830.0, 360.0, 140.0],
            speeds: vec![25.0, 3.0, 1.5, 1.5, 2.0, 2.0],
            ring_times: vec![40.0, 40.0, 30.0, 30.0, 20.0, 20.0],
            base_time: 200.0,
            center: [0.0; 3],
            hot_spots: Vec::new(),
            fixed_center: None,
        }
    }
}

/// What the ring wants announced.
#[derive(Clone, Debug, PartialEq)]
pub enum RingEvent {
    NextRing {
        ring_id: u8,
        center: [f32; 3],
        size: f32,
    },
    StartedMoving,
}

#[derive(Clone, Debug, PartialEq)]
enum Phase {
    Idle,
    Moving {
        since: f32,
        duration: f32,
    },
    /// Stopped at `since`; `next_ring_announced` once 25 s have passed.
    Paused {
        since: f32,
        wait: f32,
        next_ring_announced: bool,
    },
    Done,
}

#[derive(Clone, Debug)]
pub struct Ring {
    rules: RingRules,
    pub id: usize,
    pub white_center: [f32; 3],
    pub white_size: f32,
    /// The ring the storm currently stands at (it shrinks from here to the white ring).
    blue_center: [f32; 3],
    blue_size: f32,
    phase: Phase,
}

impl Ring {
    /// Ring 1 is chosen up front, as the official server does when it spawns the ring.
    pub fn new(rules: RingRules, rng: &mut Rng) -> Self {
        let first_size = rules.sizes.first().copied().unwrap_or(4000.0);
        let mut ring = Self {
            white_center: rules.center,
            white_size: first_size,
            blue_center: rules.center,
            blue_size: first_size,
            id: 0,
            rules,
            phase: Phase::Idle,
        };
        ring.advance(rng);
        ring
    }

    /// Where the storm's edge is now: centre and diameter (moving linearly towards the white
    /// ring while it shrinks, like the client draws it).
    pub fn current(&self, now: f32) -> ([f32; 3], f32) {
        match self.phase {
            Phase::Moving { since, duration } => {
                let t = ((now - since) / duration.max(0.01)).clamp(0.0, 1.0);
                let lerp = |a: f32, b: f32| a + (b - a) * t;
                (
                    [
                        lerp(self.blue_center[0], self.white_center[0]),
                        lerp(self.blue_center[1], self.white_center[1]),
                        lerp(self.blue_center[2], self.white_center[2]),
                    ],
                    lerp(self.blue_size, self.white_size),
                )
            }
            _ => (self.blue_center, self.blue_size),
        }
    }

    pub fn is_moving(&self) -> bool {
        matches!(self.phase, Phase::Moving { .. })
    }

    pub fn announcement(&self) -> RingEvent {
        RingEvent::NextRing {
            ring_id: self.id as u8,
            center: self.white_center,
            size: self.white_size,
        }
    }

    /// Match start: shrink toward the current white ring right away.
    pub fn start(&mut self, now: f32) -> Vec<RingEvent> {
        vec![self.announcement(), self.start_moving(now)]
    }

    fn start_moving(&mut self, now: f32) -> RingEvent {
        let speed = self
            .rules
            .speeds
            .get(self.id)
            .copied()
            .unwrap_or(1.0)
            .max(0.01);
        self.phase = Phase::Moving {
            since: now,
            duration: self.rules.base_time / speed,
        };
        RingEvent::StartedMoving
    }

    /// Seconds the current shrink has been running (for `RingUpdate::Sync`).
    pub fn time_travelled(&self, now: f32) -> Option<f32> {
        match self.phase {
            Phase::Moving { since, .. } => Some(now - since),
            _ => None,
        }
    }

    pub fn tick(&mut self, now: f32, rng: &mut Rng) -> Vec<RingEvent> {
        let mut events = Vec::new();
        match self.phase.clone() {
            Phase::Moving { since, duration } if now - since >= duration => {
                self.blue_center = self.white_center;
                self.blue_size = self.white_size;
                let mut wait = self.rules.ring_times.get(self.id).copied().unwrap_or(20.0) + 20.0;
                if self.id == 1 {
                    wait += 40.0;
                }
                self.phase = if self.id + 1 >= self.rules.sizes.len() {
                    Phase::Done
                } else {
                    Phase::Paused {
                        since: now,
                        wait,
                        next_ring_announced: false,
                    }
                };
            }
            Phase::Paused {
                since,
                wait,
                next_ring_announced,
            } => {
                if !next_ring_announced && now - since >= 25.0 {
                    self.advance(rng);
                    events.push(self.announcement());
                    self.phase = Phase::Paused {
                        since,
                        wait,
                        next_ring_announced: true,
                    };
                }
                if now - since > wait
                    && matches!(
                        self.phase,
                        Phase::Paused {
                            next_ring_announced: true,
                            ..
                        }
                    )
                {
                    events.push(self.start_moving(now));
                }
            }
            _ => {}
        }
        events
    }

    /// Pick the next white ring inside the current one (official `NextRing` + `GetNewRingPosition`).
    fn advance(&mut self, rng: &mut Rng) {
        if self.id + 1 >= self.rules.sizes.len() {
            return;
        }
        self.id += 1;
        let new_size = self.rules.sizes[self.id];
        let radius = (self.white_size * 0.5 - new_size * 0.5 - 100.0).abs();
        let center = if let Some(fixed) = self.rules.fixed_center {
            fixed
        } else if self.id == 5 {
            [
                self.white_center[0],
                self.white_center[1],
                self.white_center[2] + 1.0,
            ]
        } else if self.id > 2 && !self.rules.hot_spots.is_empty() && rng.unit() > 0.25 {
            self.hot_spot_within(radius, rng)
                .unwrap_or_else(|| self.random_within(radius, rng))
        } else {
            self.random_within(radius, rng)
        };
        self.white_center = center;
        self.white_size = new_size;
    }

    fn random_within(&self, radius: f32, rng: &mut Rng) -> [f32; 3] {
        let [x, z] = rng.inside_unit_circle();
        [
            self.white_center[0] + x * radius,
            self.white_center[1],
            self.white_center[2] + z * radius,
        ]
    }

    fn hot_spot_within(&self, radius: f32, rng: &mut Rng) -> Option<[f32; 3]> {
        let candidates: Vec<_> = self
            .rules
            .hot_spots
            .iter()
            .filter(|h| {
                ((h[0] - self.white_center[0]).powi(2) + (h[2] - self.white_center[2]).powi(2))
                    .sqrt()
                    <= radius
            })
            .collect();
        (!candidates.is_empty()).then(|| *candidates[rng.index(candidates.len())])
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn first_ring_is_ring_one_near_the_centre() {
        let ring = Ring::new(RingRules::default(), &mut Rng::new(1));
        assert_eq!((ring.id, ring.white_size), (1, 3450.0));
        let d = (ring.white_center[0].powi(2) + ring.white_center[2].powi(2)).sqrt();
        assert!(d <= 2120.0 - 1725.0 - 100.0 + 0.01, "{d}");
    }

    #[test]
    fn follows_the_official_timeline() {
        let mut rng = Rng::new(2);
        let mut ring = Ring::new(RingRules::default(), &mut rng);
        assert_eq!(ring.start(0.0).len(), 2);
        // Ring 1 shrinks in 200 / 3 s.
        assert!(ring.tick(66.0, &mut rng).is_empty());
        ring.tick(66.7, &mut rng);
        assert!(!ring.is_moving());
        // 25 s later ring 2 is announced, inside ring 1.
        let ev = ring.tick(66.7 + 25.0, &mut rng);
        let RingEvent::NextRing {
            ring_id: 2,
            size,
            center,
        } = ev[0]
        else {
            panic!("{ev:?}")
        };
        assert_eq!(size, 1710.0);
        assert!(((center[0] - 0.0).abs()) < 3450.0);
        // The shrink starts after ring_times[1] + 20 + 40 = 100 s.
        assert!(ring.tick(66.7 + 99.0, &mut rng).is_empty());
        assert_eq!(
            ring.tick(66.7 + 100.5, &mut rng),
            vec![RingEvent::StartedMoving]
        );
    }

    #[test]
    fn a_fixed_ring_closes_fast_around_its_centre_and_stays() {
        let mut rng = Rng::new(4);
        let rules = RingRules {
            sizes: vec![4240.0, 320.0],
            speeds: vec![25.0, 20.0],
            fixed_center: Some([125.0, 0.0, -97.0]),
            ..RingRules::default()
        };
        let mut ring = Ring::new(rules, &mut rng);
        assert_eq!(ring.white_center, [125.0, 0.0, -97.0]);
        assert_eq!(ring.white_size, 320.0);
        ring.start(0.0);
        // 200 / 20 = 10 s to close in.
        ring.tick(10.1, &mut rng);
        assert!(!ring.is_moving());
        let mut t = 10.1;
        while t < 3000.0 {
            t += 1.0;
            assert!(
                ring.tick(t, &mut rng).is_empty(),
                "the ring never moves again"
            );
        }
        assert_eq!(ring.current(t), ([125.0, 0.0, -97.0], 320.0));
    }

    #[test]
    fn last_ring_ends_the_cycle() {
        let mut rng = Rng::new(3);
        let mut ring = Ring::new(RingRules::default(), &mut rng);
        ring.start(0.0);
        let mut t = 0.0;
        let mut announced = vec![1];
        while t < 5000.0 {
            t += 0.5;
            for e in ring.tick(t, &mut rng) {
                if let RingEvent::NextRing { ring_id, .. } = e {
                    announced.push(ring_id);
                }
            }
        }
        assert_eq!(announced, [1, 2, 3, 4, 5]);
        assert!(!ring.is_moving());
    }
}
