//! The road graph hover taxis drive on (exported from the map by the same code the
//! FlyingControls client uses), and the route follower ported from `Vehicles.Shared`.

use std::cmp::Ordering;
use std::collections::BinaryHeap;

use crate::vehicles::V3;

#[derive(Clone, Debug, Default, PartialEq)]
pub struct RoadGraph {
    pub points: Vec<V3>,
    edges: Vec<Vec<usize>>,
}

#[derive(PartialEq)]
struct Entry(f32, usize);
impl Eq for Entry {}
impl Ord for Entry {
    fn cmp(&self, other: &Self) -> Ordering {
        other.0.total_cmp(&self.0).then(self.1.cmp(&other.1))
    }
}
impl PartialOrd for Entry {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

fn dist3(a: V3, b: V3) -> f32 {
    ((a[0] - b[0]).powi(2) + (a[1] - b[1]).powi(2) + (a[2] - b[2]).powi(2)).sqrt()
}

impl RoadGraph {
    pub fn new(points: Vec<V3>, links: &[[usize; 2]]) -> Self {
        let mut edges = vec![Vec::new(); points.len()];
        for &[a, b] in links {
            if a != b && a < points.len() && b < points.len() {
                if !edges[a].contains(&b) {
                    edges[a].push(b);
                }
                if !edges[b].contains(&a) {
                    edges[b].push(a);
                }
            }
        }
        Self { points, edges }
    }

    pub fn nearest(&self, at: V3) -> Option<usize> {
        (0..self.points.len())
            .min_by(|&a, &b| dist3(self.points[a], at).total_cmp(&dist3(self.points[b], at)))
    }

    /// Shortest path (Dijkstra) between two points of the graph.
    pub fn route(&self, start: usize, end: usize) -> Option<Vec<usize>> {
        let n = self.points.len();
        if start >= n || end >= n {
            return None;
        }
        let mut distance = vec![f32::INFINITY; n];
        let mut previous = vec![usize::MAX; n];
        let mut queue = BinaryHeap::new();
        distance[start] = 0.0;
        queue.push(Entry(0.0, start));
        while let Some(Entry(d, node)) = queue.pop() {
            if node == end {
                let mut path = vec![end];
                let mut at = end;
                while previous[at] != usize::MAX {
                    at = previous[at];
                    path.push(at);
                }
                path.reverse();
                return Some(path);
            }
            if d > distance[node] {
                continue;
            }
            for &next in &self.edges[node] {
                let cost = d + dist3(self.points[node], self.points[next]);
                if cost < distance[next] {
                    distance[next] = cost;
                    previous[next] = node;
                    queue.push(Entry(cost, next));
                }
            }
        }
        None
    }

    /// Route from `start` to `destination` along the roads (plus the last bit off-road).
    pub fn route_between(&self, start: V3, destination: V3) -> Option<Vec<V3>> {
        let (a, b) = (self.nearest(start)?, self.nearest(destination)?);
        let mut points: Vec<V3> = self
            .route(a, b)?
            .into_iter()
            .map(|i| self.points[i])
            .collect();
        let last = *points.last()?;
        if (destination[0] - last[0]).powi(2)
            + (destination[1] - last[1]).powi(2)
            + (destination[2] - last[2]).powi(2)
            > 4.0
        {
            points.push(destination);
        }
        Some(points)
    }
}

const BRAKE: f32 = 12.0;
const LATERAL: f32 = 12.0;

fn flat(a: V3, b: V3) -> f32 {
    (a[0] - b[0]).hypot(a[2] - b[2])
}

/// Continuous progress along a polyline (a missed vertex is never a target to return to).
#[derive(Clone, Debug)]
pub struct RoadFollower {
    points: Vec<V3>,
    distance: Vec<f32>,
    progress: f32,
    segment: usize,
    pub arrived: bool,
}

impl RoadFollower {
    pub fn new(route: &[V3]) -> Option<Self> {
        let mut points: Vec<V3> = Vec::new();
        let mut distance: Vec<f32> = Vec::new();
        for &p in route {
            let length = points.last().map_or(0.0, |&l| flat(l, p));
            if !points.is_empty() && length < 0.01 {
                continue;
            }
            distance.push(distance.last().map_or(0.0, |&d| d + length));
            points.push(p);
        }
        (!points.is_empty()).then_some(Self {
            points,
            distance,
            progress: 0.0,
            segment: 0,
            arrived: false,
        })
    }

    fn length(&self) -> f32 {
        *self.distance.last().expect("not empty")
    }

    fn sample(&self, at: f32) -> V3 {
        let at = at.clamp(0.0, self.length());
        let i = match self.distance.binary_search_by(|d| d.total_cmp(&at)) {
            Ok(i) => i,
            Err(i) => i.saturating_sub(1),
        };
        if i + 1 >= self.points.len() {
            return self.points[i];
        }
        let t = (at - self.distance[i]) / (self.distance[i + 1] - self.distance[i]);
        let (a, b) = (self.points[i], self.points[i + 1]);
        [
            a[0] + (b[0] - a[0]) * t,
            a[1] + (b[1] - a[1]) * t,
            a[2] + (b[2] - a[2]) * t,
        ]
    }

    pub fn point_ahead(&self, distance: f32) -> V3 {
        self.sample(self.progress + distance)
    }

    fn project(&mut self, position: V3, speed: f32, dt: f32) {
        let horizon = self
            .length()
            .min(self.progress + (speed * dt * 3.0).max(8.0));
        let (mut best, mut progress) = (f32::MAX, self.progress);
        let mut i = self.segment;
        while i + 1 < self.points.len() && self.distance[i] <= horizon {
            let (a, b) = (self.points[i], self.points[i + 1]);
            let (dx, dz) = (b[0] - a[0], b[2] - a[2]);
            let length = self.distance[i + 1] - self.distance[i];
            let t = (((position[0] - a[0]) * dx + (position[2] - a[2]) * dz) / (length * length))
                .clamp(0.0, 1.0);
            let at = (self.distance[i] + t * length).clamp(self.progress, horizon);
            let d = flat(position, self.sample(at));
            if d < best {
                best = d;
                progress = at;
            }
            i += 1;
        }
        self.progress = progress;
        while self.segment + 1 < self.distance.len() - 1
            && self.distance[self.segment + 1] <= self.progress + 0.01
        {
            self.segment += 1;
        }
    }

    /// The horizontal velocity to drive at now (`[x, 0, z]`).
    pub fn step(
        &mut self,
        position: V3,
        velocity: V3,
        max_speed: f32,
        acceleration: f32,
        dt: f32,
    ) -> V3 {
        let current = velocity[0].hypot(velocity[2]);
        self.project(position, current, dt);
        let remaining = self.length() - self.progress;
        let end_distance = flat(position, *self.points.last().expect("not empty"));
        self.arrived = remaining < 2.0 && end_distance < 2.5;
        if self.arrived {
            return [0.0; 3];
        }
        let look = (4.0 + current * 0.3).clamp(6.0, 14.0);
        let target = self.sample(self.progress + look);
        let desired_angle = (target[2] - position[2]).atan2(target[0] - position[0]);
        let mut limit = max_speed;
        let preview = self
            .length()
            .min(self.progress + (current * current / (2.0 * BRAKE) + 12.0).max(20.0));
        let mut at = self.progress;
        while at <= preview {
            let (a, b, c) = (
                self.sample(at - 4.0),
                self.sample(at),
                self.sample(at + 4.0),
            );
            let (ab, bc, ac) = (flat(a, b), flat(b, c), flat(a, c));
            if ab >= 0.1 && bc >= 0.1 && ac >= 0.1 {
                let cross = ((b[0] - a[0]) * (c[2] - a[2]) - (b[2] - a[2]) * (c[0] - a[0])).abs();
                let curvature = 2.0 * cross / (ab * bc * ac);
                if curvature >= 0.001 {
                    let corner = (LATERAL / curvature).sqrt();
                    let allowed = (corner * corner
                        + 2.0 * BRAKE * (at - self.progress - 4.0).max(0.0))
                    .sqrt();
                    limit = limit.min(allowed);
                }
            }
            at += 3.0;
        }
        let stopping = remaining + (end_distance - remaining).max(0.0);
        limit = limit
            .min((2.0 * BRAKE * stopping).sqrt())
            .min((stopping * 1.5).max(2.0));
        let mut angle = if current > 0.1 {
            velocity[2].atan2(velocity[0])
        } else {
            desired_angle
        };
        let mut delta = desired_angle - angle;
        while delta > std::f32::consts::PI {
            delta -= std::f32::consts::TAU;
        }
        while delta < -std::f32::consts::PI {
            delta += std::f32::consts::TAU;
        }
        limit = limit.min(max_speed * (1.0 - delta.abs() / std::f32::consts::PI).max(0.2));
        let rate = if limit < current { BRAKE } else { acceleration };
        let speed = current + (limit - current).clamp(-rate * dt, rate * dt);
        let max_turn = (LATERAL / speed.max(4.0)).min(2.2) * dt;
        angle += delta.clamp(-max_turn, max_turn);
        [angle.cos() * speed, 0.0, angle.sin() * speed]
    }
}

/// The taxi's boost: twice the speed for 2 s, then 8 s to recharge.
#[derive(Clone, Debug, Default)]
pub struct HoverBoost {
    ends_at: f32,
    ready_at: f32,
}

impl HoverBoost {
    pub const DURATION: f32 = 2.0;
    pub const COOLDOWN: f32 = 8.0;
    pub const SPEED: f32 = 2.0;
    pub fn active(&self, now: f32) -> bool {
        now < self.ends_at
    }
    pub fn remaining(&self, now: f32) -> f32 {
        (self.ends_at - now).max(0.0)
    }
    pub fn cooldown(&self, now: f32) -> f32 {
        (self.ready_at - now).max(0.0)
    }
    pub fn try_start(&mut self, now: f32, can: bool) -> bool {
        if !can || now < self.ready_at {
            return false;
        }
        self.ends_at = now + Self::DURATION;
        self.ready_at = self.ends_at + Self::COOLDOWN;
        true
    }
    /// Stops the thrust; the cooldown stays.
    pub fn stop(&mut self) {
        self.ends_at = 0.0;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn routes_follow_the_roads() {
        // A square: 0-1-2-3, the direct 0-2 link is missing.
        let g = RoadGraph::new(
            vec![
                [0.0, 0.0, 0.0],
                [50.0, 0.0, 0.0],
                [50.0, 0.0, 50.0],
                [0.0, 0.0, 50.0],
            ],
            &[[0, 1], [1, 2], [2, 3]],
        );
        assert_eq!(g.route(0, 3), Some(vec![0, 1, 2, 3]));
        let r = g.route_between([1.0, 0.0, 1.0], [0.0, 0.0, 60.0]).unwrap();
        assert_eq!(r.len(), 5, "plus the destination off the road");
        // Drive it: arrives, never faster than allowed.
        let mut f = RoadFollower::new(&r).unwrap();
        let (mut pos, mut vel) = ([0.0, 0.0, 0.0], [0.0; 3]);
        for _ in 0..2000 {
            vel = f.step(pos, vel, 20.0, 12.0, 0.05);
            assert!(vel[0].hypot(vel[2]) <= 20.0 + 1e-3);
            pos = [pos[0] + vel[0] * 0.05, 0.0, pos[2] + vel[2] * 0.05];
            if f.arrived {
                break;
            }
        }
        assert!(f.arrived, "ended at {pos:?}");
        let mut b = HoverBoost::default();
        assert!(b.try_start(0.0, true) && b.active(1.0) && !b.active(2.5));
        assert!(!b.try_start(5.0, true), "cooldown");
        b.stop();
        assert!(b.try_start(10.0, true));
    }
}
