//! Hover taxis (FlyingControls): the driver picks a destination on the map and the server flies
//! the Hover_Bike / Hover_Car along the roads, hopping over obstacles, with a 2 s boost. Their
//! motion comes only from here (client car updates for taxis are ignored).

use std::collections::BTreeMap;

use tabg_protocol::Reader;
use tabg_protocol::messages::PackedDirection;
use tabg_protocol::messages::PackedQuaternion;

use super::vehicles::{kind, message, read_v3};
use super::{Outgoing, Recipients, Room};
use crate::roads::{HoverBoost, RoadFollower};
use crate::vehicles::{self as rules, V3, add, euler, euler_angles, length, scale, sub};

const STEP: f32 = 0.05;
const ROAD_SPEED: f32 = 20.0;
/// Hops go at most this far above the normal hover height.
const CEILING: f32 = 60.0;

/// Taxi status for the driver's HUD.
mod status {
    pub const IDLE: u8 = 0;
    pub const DRIVING: u8 = 1;
    pub const HOPPING: u8 = 2;
    pub const BLOCKED: u8 = 3;
    pub const ARRIVED: u8 = 4;
    pub const NO_ROUTE: u8 = 5;
}

#[derive(Debug)]
pub(super) struct Taxi {
    driver: Option<u8>,
    velocity: V3,
    route: Option<RoadFollower>,
    boost: HoverBoost,
    ground: f32,
    ground_at: f32,
    send_at: f32,
    probe_at: f32,
    hop_until: f32,
    hop_y: f32,
    blocked: f32,
    request_at: f32,
    status: u8,
}

#[derive(Debug, Default)]
pub(super) struct TaxiState {
    taxis: BTreeMap<i32, Taxi>,
    next: f32,
    last: Option<f32>,
}

fn move_towards(from: f32, to: f32, max: f32) -> f32 {
    from + (to - from).clamp(-max, max)
}

fn move_towards3(from: V3, to: V3, max: f32) -> V3 {
    let d = sub(to, from);
    let l = length(d);
    if l <= max || l < 1e-6 {
        to
    } else {
        add(from, scale(d, max / l))
    }
}

impl Room {
    pub(super) fn is_taxi(&self, car: i32) -> bool {
        rules::is_taxi(self.car_name(car))
    }

    fn taxi_driver(&self, car: i32) -> Option<u8> {
        self.cars
            .get(&car)?
            .seats
            .iter()
            .find(|s| s.driver)
            .and_then(|s| s.occupant)
    }

    /// The taxi's state, reset when the driver changes.
    fn taxi(&mut self, car: i32) -> &mut Taxi {
        let driver = self.taxi_driver(car);
        let y = self.cars.get(&car).map_or(0.0, |c| c.position[1]);
        if let Some(c) = self.cars.get_mut(&car) {
            c.temporary_owner = None;
        }
        let t = self.taxis.taxis.entry(car).or_insert_with(|| Taxi {
            driver,
            velocity: [0.0; 3],
            route: None,
            boost: HoverBoost::default(),
            ground: y - 2.0,
            ground_at: 0.0,
            send_at: 0.0,
            probe_at: 0.0,
            hop_until: 0.0,
            hop_y: 0.0,
            blocked: 0.0,
            request_at: 0.0,
            status: status::IDLE,
        });
        if t.driver != driver {
            t.driver = driver;
            t.route = None;
            t.boost.stop();
            t.status = status::IDLE;
            t.hop_until = 0.0;
        }
        t
    }

    /// Client car updates may not move a taxi: the server drives it.
    pub(super) fn taxi_owns_motion(&self, car: i32) -> bool {
        self.is_taxi(car)
            && (self.taxis.taxis.contains_key(&car) || self.taxi_driver(car).is_some())
    }

    pub(super) fn taxi_message(&mut self, peer: u8, k: u8, body: &[u8]) -> Vec<Outgoing> {
        let Some((car, _, true)) = self.players.get(&peer).and_then(|p| p.seat) else {
            return Vec::new();
        };
        if !self.is_taxi(car)
            || !self
                .players
                .get(&peer)
                .is_some_and(|p| p.alive && !p.downed)
        {
            return Vec::new();
        }
        let now = self.last_now.unwrap_or(0.0);
        let position = self.cars[&car].position;
        if k == kind::TAXI_BOOST {
            if !body.is_empty() {
                return Vec::new();
            }
            let t = self.taxi(car);
            let moving = t.route.is_some() && t.velocity[0].hypot(t.velocity[2]) > 1.0;
            if t.boost.try_start(now, moving) {
                t.send_at = 0.0;
            }
            return Vec::new();
        }
        let mut r = Reader::new(body);
        let Ok(active) = r.bool() else {
            return Vec::new();
        };
        let goal = if active { read_v3(&mut r) } else { None };
        if r.remaining() != 0 || (active && goal.is_none()) {
            return Vec::new();
        }
        let roads = self.settings.vehicles.roads.clone();
        let t = self.taxi(car);
        let Some(goal) = goal else {
            t.route = None;
            t.boost.stop();
            t.status = status::IDLE;
            t.hop_until = 0.0;
            return Vec::new();
        };
        if now < t.request_at || length(sub(position, goal)) > 10_000.0 {
            return Vec::new();
        }
        t.request_at = now + 0.4;
        t.blocked = 0.0;
        t.hop_until = 0.0;
        t.ground_at = 0.0;
        let route = roads.as_ref().and_then(|g| g.route_between(position, goal));
        t.route = match route.filter(|r| r.len() >= 2) {
            Some(mut points) => {
                points.insert(0, position);
                RoadFollower::new(&points)
            }
            None => None,
        };
        t.status = if t.route.is_some() {
            status::DRIVING
        } else {
            status::NO_ROUTE
        };
        Vec::new()
    }

    fn surface(&self, x: f32, z: f32) -> Option<f32> {
        self.settings.nav.as_ref().and_then(|n| n.height(x, z))
    }

    /// Is the way from `from` to `to` (at `from`'s height) blocked by something taller than the
    /// taxi's underside? Returns the obstacle's top.
    fn taxi_blocked(&self, from: V3, to: V3, clearance: f32) -> Option<f32> {
        let d = sub(to, from);
        let steps = (length(d) / 1.0).ceil() as usize;
        let mut top: Option<f32> = None;
        for i in 1..=steps {
            let p = add(from, scale(d, i as f32 / steps as f32));
            if let Some(h) = self.surface(p[0], p[2])
                && h > p[1] - clearance + 0.3
            {
                top = Some(top.map_or(h, |t: f32| t.max(h)));
            }
        }
        top
    }

    pub(super) fn tick_taxis(&mut self, now: f32) -> Vec<Outgoing> {
        if now < self.taxis.next {
            return Vec::new();
        }
        self.taxis.next = now + STEP;
        let dt = self.taxis.last.map_or(STEP, |l| (now - l).clamp(0.0, 0.2));
        self.taxis.last = Some(now);
        let driven: Vec<i32> = self
            .cars
            .keys()
            .copied()
            .filter(|&c| self.is_taxi(c) && self.taxi_driver(c).is_some_and(|d| !self.is_bot(d)))
            .collect();
        for c in driven {
            self.taxi(c);
        }
        let mut out = Vec::new();
        let known: Vec<i32> = self.taxis.taxis.keys().copied().collect();
        for car in known {
            if !self.cars.contains_key(&car) {
                self.taxis.taxis.remove(&car);
                continue;
            }
            out.extend(self.tick_taxi(car, now, dt));
        }
        out
    }

    fn tick_taxi(&mut self, car: i32, now: f32, dt: f32) -> Vec<Outgoing> {
        let clearance = self
            .cars
            .get(&car)
            .and_then(|c| self.vehicle_type(c.car_type))
            .map_or(1.5, |t| t.hull.ground_clearance);
        let position = self.cars[&car].position;
        self.taxi(car);
        let driver_alive = self
            .taxi_driver(car)
            .is_some_and(|d| self.players.get(&d).is_some_and(|p| p.alive));
        let surface_here = self.surface(position[0], position[2]);
        let t = self.taxis.taxis.get_mut(&car).expect("tracked");
        if !driver_alive {
            t.route = None;
            t.boost.stop();
            t.status = status::IDLE;
            t.hop_until = 0.0;
        }
        if now >= t.ground_at {
            t.ground_at = now + 0.2;
            if let Some(g) = surface_here {
                t.ground = g;
            }
        }
        let mut flat = [t.velocity[0], 0.0, t.velocity[2]];
        let mut desired = [0.0; 3];
        let boosting = t.boost.active(now);
        if let Some(route) = t.route.as_mut() {
            let speed = ROAD_SPEED * if boosting { HoverBoost::SPEED } else { 1.0 };
            desired = route.step(
                position,
                flat,
                speed,
                if boosting { 45.0 } else { 12.0 },
                dt,
            );
            if route.arrived {
                t.route = None;
                t.boost.stop();
                t.status = status::ARRIVED;
            }
        }
        let normal = t.ground + (clearance + 0.5).clamp(1.2, 4.0);
        let probe = t.route.is_some() && length(desired) > 1.0 && now >= t.probe_at;
        let ahead = t.route.as_ref().map(|r| {
            let look = (length(flat) * 1.1).max(8.0);
            let mut a = r.point_ahead(look);
            a[1] = position[1];
            a
        });
        if probe && let Some(ahead) = ahead {
            let obstacle = self.taxi_blocked(position, ahead, clearance);
            let t = self.taxis.taxis.get_mut(&car).expect("tracked");
            t.probe_at = now + 0.2;
            match obstacle {
                Some(top) => {
                    let top = top + 2.0;
                    let over = [ahead[0], top + clearance, ahead[2]];
                    let clear_above = top <= normal + CEILING
                        && self
                            .taxi_blocked(
                                [position[0], top + clearance, position[2]],
                                over,
                                clearance,
                            )
                            .is_none();
                    let t = self.taxis.taxis.get_mut(&car).expect("tracked");
                    if clear_above {
                        t.hop_y = top;
                        t.hop_until = now + 1.2;
                        t.blocked = 0.0;
                        t.status = status::HOPPING;
                    } else {
                        t.blocked += 0.2;
                        t.status = status::BLOCKED;
                    }
                }
                None => {
                    t.blocked = 0.0;
                    if now >= t.hop_until {
                        t.status = status::DRIVING;
                    }
                }
            }
        }
        let t = self.taxis.taxis.get_mut(&car).expect("tracked");
        let target = if now < t.hop_until {
            t.hop_y.min(normal + CEILING).max(normal)
        } else {
            normal
        };
        if t.status == status::BLOCKED || target - position[1] > 1.4 {
            desired = [0.0; 3];
        }
        flat = move_towards3(flat, desired, if boosting { 45.0 } else { 20.0 } * dt);
        let vertical = move_towards(
            t.velocity[1],
            ((target - position[1]) * 3.0).clamp(-6.0, 8.0),
            20.0 * dt,
        );
        t.velocity = [flat[0], vertical, flat[2]];
        let mut step = scale(t.velocity, dt);
        if t.blocked > 10.0 {
            t.route = None;
            t.status = status::BLOCKED;
            t.boost.stop();
        }
        let next = add(position, step);
        if self
            .taxi_blocked(position, next, clearance)
            .is_some_and(|h| h > next[1] - 0.2)
        {
            let t = self.taxis.taxis.get_mut(&car).expect("tracked");
            t.velocity = [0.0; 3];
            step = [0.0; 3];
        }
        let c = self.cars.get_mut(&car).expect("exists");
        c.position = add(position, step);
        if flat[0].hypot(flat[2]) > 1.0 {
            let want = flat[0].atan2(flat[2]).to_degrees();
            let have = euler_angles(c.rotation.unpack())[1];
            let mut delta = (want - have).rem_euclid(360.0);
            if delta > 180.0 {
                delta -= 360.0;
            }
            let yaw = have + delta.clamp(-135.0 * dt, 135.0 * dt);
            c.rotation = PackedQuaternion::pack(euler([0.0, yaw, 0.0]));
        }
        c.input = PackedDirection::ZERO;
        c.driving_state = 0;
        c.temporary_changed = true;
        let (at, rotation) = (c.position, c.rotation.unpack());
        let occupants: Vec<u8> = c.seats.iter().filter_map(|s| s.occupant).collect();
        for o in occupants {
            if let Some(p) = self.players.get_mut(&o) {
                p.position = add(at, [0.0, 1.0, 0.0]);
                p.dirty = true;
            }
        }
        let t = self.taxis.taxis.get_mut(&car).expect("tracked");
        let mut out = Vec::new();
        if now >= t.send_at {
            t.send_at = now + 0.1;
            let (status, remaining, cooldown, driver) = (
                t.status,
                t.boost.remaining(now),
                t.boost.cooldown(now),
                t.driver,
            );
            let mut w = message(kind::AIRCRAFT_FALL);
            w.i32(car).u8(1).vec3(at).vec3(euler_angles(rotation));
            let to = self.vehicle_clients();
            if !to.is_empty() {
                out.push(Outgoing {
                    to: Recipients::Many(to),
                    packet: tabg_protocol::Packet {
                        code: super::vehicles::VEHICLES,
                        payload: w.into_inner(),
                    },
                    reliable: false,
                });
            }
            if let Some(d) = driver {
                let mut w = message(kind::TAXI_STATUS);
                w.i32(car).u8(status).f32(remaining).f32(cooldown);
                out.push(Self::to_one(d, w, false));
            }
        }
        out
    }
}
