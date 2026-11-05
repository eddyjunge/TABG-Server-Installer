//! Rules for the vehicles the FlyingControls client mod brings back (helicopter, UFO, hover
//! taxis): ported from the old `Vehicles.Shared` code so server and client agree. Pure data and
//! maths; the room drives them (`room/vehicles.rs`).

use std::collections::HashSet;

use tabg_protocol::messages::CarPart;

pub type V3 = [f32; 3];

pub fn add(a: V3, b: V3) -> V3 {
    [a[0] + b[0], a[1] + b[1], a[2] + b[2]]
}
pub fn sub(a: V3, b: V3) -> V3 {
    [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
}
pub fn scale(a: V3, k: f32) -> V3 {
    [a[0] * k, a[1] * k, a[2] * k]
}
pub fn dot(a: V3, b: V3) -> f32 {
    a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
}
pub fn length(a: V3) -> f32 {
    dot(a, a).sqrt()
}
pub fn distance(a: V3, b: V3) -> f32 {
    length(sub(a, b))
}
/// Unit vector (forward when too short to tell).
pub fn normal(a: V3) -> V3 {
    let l = length(a);
    if l > 1e-5 {
        scale(a, 1.0 / l)
    } else {
        [0.0, 0.0, 1.0]
    }
}
pub fn lerp(a: V3, b: V3, t: f32) -> V3 {
    add(a, scale(sub(b, a), t))
}
pub fn finite(a: V3) -> bool {
    a.iter().all(|v| v.is_finite())
}

/// Rotate `v` by the quaternion `q` (`[x, y, z, w]`).
pub fn rotate(q: [f32; 4], v: V3) -> V3 {
    let u = [q[0], q[1], q[2]];
    let s = q[3];
    let uv = cross(u, v);
    let uuv = cross(u, uv);
    add(v, add(scale(uv, 2.0 * s), scale(uuv, 2.0)))
}
pub fn inverse(q: [f32; 4]) -> [f32; 4] {
    [-q[0], -q[1], -q[2], q[3]]
}
fn cross(a: V3, b: V3) -> V3 {
    [
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    ]
}
fn mul(a: [f32; 4], b: [f32; 4]) -> [f32; 4] {
    [
        a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
        a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
        a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
        a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2],
    ]
}
/// Unity's `Quaternion.Euler` (degrees; Z, then X, then Y).
pub fn euler(deg: V3) -> [f32; 4] {
    let axis = |i: usize, d: f32| {
        let (s, c) = (d.to_radians() * 0.5).sin_cos();
        let mut q = [0.0, 0.0, 0.0, c];
        q[i] = s;
        q
    };
    mul(mul(axis(1, deg[1]), axis(0, deg[0])), axis(2, deg[2]))
}
/// Unity's `eulerAngles` (degrees, 0..360).
pub fn euler_angles(q: [f32; 4]) -> V3 {
    let [x, y, z, w] = q;
    let sin_x = (2.0 * (w * x - y * z)).clamp(-1.0, 1.0);
    let ax = sin_x.asin();
    let (ay, az) = if sin_x.abs() < 0.9999 {
        (
            (2.0 * (w * y + x * z)).atan2(1.0 - 2.0 * (x * x + y * y)),
            (2.0 * (w * z + x * y)).atan2(1.0 - 2.0 * (x * x + z * z)),
        )
    } else {
        (
            (2.0 * (w * y - x * z)).atan2(1.0 - 2.0 * (y * y + z * z)),
            0.0,
        )
    };
    [ax, ay, az].map(|r| r.to_degrees().rem_euclid(360.0))
}

pub const AIRCRAFT_HEALTH: f32 = 30.0;
/// An aircraft resting on the ground is much tougher (official helicopter behaviour in the mod).
pub const GROUNDED_HEALTH: f32 = 300.0;
pub const START_PROTECTION: f32 = 2.0;
pub const CRASH_SPEED: f32 = 18.0;
/// Damage a crash does (destroys any aircraft in the air).
pub const CRASH_DAMAGE: f32 = 300.0;

fn has(name: &str, part: &str) -> bool {
    name.to_ascii_lowercase()
        .contains(&part.to_ascii_lowercase())
}
pub fn is_aircraft(name: &str) -> bool {
    has(name, "ufo") || has(name, "heli")
}
pub fn is_heli(name: &str) -> bool {
    name.replace("(Clone)", "")
        .trim()
        .eq_ignore_ascii_case("heli")
}
pub fn is_taxi(name: &str) -> bool {
    has(name, "hover_bike") || has(name, "hover_car")
}
/// What an occupant keeps when their aircraft explodes around them.
pub fn survivor_health(health: f32) -> f32 {
    if health <= 0.0 {
        health
    } else {
        health.min(1.0).max(health - 50.0)
    }
}

/// An aircraft's own health (separate from the car parts): low in the air, high when grounded.
#[derive(Clone, Debug, PartialEq)]
pub struct AircraftLife {
    pub health: f32,
    pub maximum: f32,
    pub destroyed: bool,
    started: bool,
    protected_until: f32,
}

impl AircraftLife {
    pub fn new(now: f32) -> Self {
        Self {
            health: AIRCRAFT_HEALTH,
            maximum: AIRCRAFT_HEALTH,
            destroyed: false,
            started: false,
            protected_until: now + START_PROTECTION,
        }
    }
    pub fn set_grounded(&mut self, grounded: bool) {
        if self.destroyed {
            return;
        }
        let next = if grounded {
            GROUNDED_HEALTH
        } else {
            AIRCRAFT_HEALTH
        };
        if next != self.maximum {
            self.health = self.health / self.maximum * next;
            self.maximum = next;
        }
    }
    /// The first pilot gets a moment of protection.
    pub fn start(&mut self, now: f32) {
        if !self.started {
            self.started = true;
            self.protected_until = now + START_PROTECTION;
        }
    }
    /// Returns whether this destroyed it.
    pub fn damage(&mut self, amount: f32, now: f32) -> bool {
        if self.destroyed || now < self.protected_until || !amount.is_finite() || amount <= 0.0 {
            return false;
        }
        self.health = (self.health - amount).max(0.0);
        self.destroyed = self.health == 0.0;
        self.destroyed
    }
}

/// A vehicle type from the map data.
#[derive(Clone, Debug, PartialEq)]
pub struct VehicleType {
    pub car_type: i32,
    pub name: String,
    /// `(network index, is driver seat)`.
    pub seats: Vec<(i32, bool)>,
    pub parts: Vec<CarPart>,
    pub hull: Hull,
}

/// A vehicle's collision boxes in its own frame (from the prefab).
#[derive(Clone, Debug, PartialEq)]
pub struct Hull {
    boxes: Vec<(V3, V3)>,
    /// Centre of the biggest box: where to aim.
    pub center: V3,
    /// How far the body reaches below the pivot.
    pub ground_clearance: f32,
    radius: f32,
}

impl Hull {
    /// `boxes` as `[min x, min y, min z, max x, max y, max z]`.
    pub fn new(boxes: &[[f32; 6]]) -> Self {
        let mut list: Vec<(V3, V3)> = boxes
            .iter()
            .map(|b| ([b[0], b[1], b[2]], [b[3], b[4], b[5]]))
            .collect();
        if list.is_empty() {
            list.push(([-1.0, -0.75, -1.5], [1.0, 0.75, 1.5]));
        }
        let (mut center, mut largest, mut clearance, mut radius) = ([0.0; 3], 0.0, 0.7f32, 0.0f32);
        for &(min, max) in &list {
            let size = sub(max, min);
            let volume = size[0] * size[1] * size[2];
            let c = scale(add(min, max), 0.5);
            if volume > largest {
                largest = volume;
                center = c;
            }
            clearance = clearance.max(-min[1] + 0.15);
            radius = radius.max(length(c) + length(scale(size, 0.5)));
        }
        Self {
            boxes: list,
            center,
            ground_clearance: clearance,
            radius,
        }
    }

    /// Lowest point of the body below the pivot (for placing it on the ground).
    pub fn bottom(&self) -> f32 {
        self.boxes
            .iter()
            .map(|(min, _)| -min[1])
            .fold(0.0, f32::max)
    }

    /// Horizontal footprint half extents (x, z).
    pub fn extents(&self) -> (f32, f32) {
        let (mut x, mut z) = (0.5f32, 0.5f32);
        for (min, max) in &self.boxes {
            x = x.max(min[0].abs()).max(max[0].abs());
            z = z.max(min[2].abs()).max(max[2].abs());
        }
        (x, z)
    }

    pub fn aim(&self, position: V3, rotation: [f32; 4]) -> V3 {
        add(position, rotate(rotation, self.center))
    }

    /// The point of each box closest to the line of sight from `observer` along `direction`.
    pub fn aim_points(
        &self,
        observer: V3,
        direction: V3,
        position: V3,
        rotation: [f32; 4],
    ) -> Vec<V3> {
        let inv = inverse(rotation);
        let local_observer = rotate(inv, sub(observer, position));
        let local_direction = normal(rotate(inv, direction));
        self.boxes
            .iter()
            .map(|&(min, max)| {
                let c = scale(add(min, max), 0.5);
                let along = dot(sub(c, local_observer), local_direction).max(0.0);
                let p = add(local_observer, scale(local_direction, along));
                let closest = [0, 1, 2].map(|i| p[i].clamp(min[i], max[i]));
                add(position, rotate(rotation, closest))
            })
            .collect()
    }

    /// Where along `start → end` (0..1) a sphere of `radius` first touches the hull.
    pub fn hit(
        &self,
        start: V3,
        end: V3,
        position: V3,
        rotation: [f32; 4],
        radius: f32,
    ) -> Option<f32> {
        let seg = sub(end, start);
        let len2 = dot(seg, seg);
        let t = if len2 > 0.0 {
            (dot(sub(position, start), seg) / len2).clamp(0.0, 1.0)
        } else {
            0.0
        };
        let broad = self.radius + radius * 1.733;
        let closest = add(start, scale(seg, t));
        if dot(sub(closest, position), sub(closest, position)) > broad * broad {
            return None;
        }
        let inv = inverse(rotation);
        let (a, b) = (
            rotate(inv, sub(start, position)),
            rotate(inv, sub(end, position)),
        );
        let pad = [radius; 3];
        self.boxes
            .iter()
            .filter_map(|&(min, max)| segment_box(a, b, sub(min, pad), add(max, pad)))
            .min_by(f32::total_cmp)
    }
}

/// Slab test; includes starting inside and touching at the end.
pub fn segment_box(start: V3, end: V3, min: V3, max: V3) -> Option<f32> {
    let (mut enter, mut leave) = (0.0f32, 1.0f32);
    let d = sub(end, start);
    for i in 0..3 {
        if d[i].abs() < 1e-6 {
            if start[i] < min[i] || start[i] > max[i] {
                return None;
            }
            continue;
        }
        let (a, b) = ((min[i] - start[i]) / d[i], (max[i] - start[i]) / d[i]);
        enter = enter.max(a.min(b));
        leave = leave.min(a.max(b));
        if enter > leave {
            return None;
        }
    }
    Some(enter)
}

/// Earliest contact of a swept segment with a sphere (stops fast missiles tunnelling).
pub fn segment_sphere(start: V3, end: V3, center: V3, radius: f32) -> Option<f32> {
    let delta = sub(end, start);
    let offset = sub(start, center);
    let c = dot(offset, offset) - radius * radius;
    if c <= 0.0 {
        return Some(0.0);
    }
    let (a, b) = (dot(delta, delta), dot(offset, delta));
    if a < 1e-6 || b >= 0.0 {
        return None;
    }
    let d = b * b - a * c;
    if d < 0.0 {
        return None;
    }
    let t = (-b - d.sqrt()) / a;
    (0.0..=1.0).contains(&t).then_some(t)
}

/// Turn `direction` towards `desired` by at most `max_angle` radians.
pub fn turn(direction: V3, desired: V3, max_angle: f32) -> V3 {
    let (direction, desired) = (normal(direction), normal(desired));
    let d = dot(direction, desired).clamp(-1.0, 1.0);
    if d.acos() <= max_angle {
        return desired;
    }
    let mut tangent = sub(desired, scale(direction, d));
    if length(tangent) < 1e-4 {
        let axis = if direction[1].abs() < 0.9 {
            [0.0, 1.0, 0.0]
        } else {
            [1.0, 0.0, 0.0]
        };
        tangent = sub(axis, scale(direction, dot(axis, direction)));
    }
    normal(add(
        scale(direction, max_angle.cos()),
        scale(normal(tangent), max_angle.sin()),
    ))
}

pub mod missile {
    pub const DAMAGE: f32 = 40.0;
    pub const COOLDOWN: f32 = 4.0;
    pub const LOCK_SECONDS: f32 = 0.65;
    pub const GUIDED_SPEED: f32 = 25.0;
    pub const STRAIGHT_SPEED: f32 = 55.0;
    pub const RANGE: f32 = 300.0;
    pub const TURN_RADIANS: f32 = 1.8;
    pub const BULLET_HITS: usize = 3;
    pub fn lifetime(guided: bool) -> f32 {
        if guided { 11.2 } else { 8.0 }
    }
    /// Missile targets: players by index, vehicles as 256 + car index.
    pub fn vehicle_target(car: i32) -> i32 {
        256 + car
    }
    pub fn is_vehicle_target(target: i32) -> bool {
        target >= 256
    }
    pub fn car_index(target: i32) -> i32 {
        target - 256
    }
}

/// The helicopter's missile launcher, per player (changing seats never reloads it).
#[derive(Clone, Debug)]
pub struct MissileWeapon {
    ready_at: f32,
    lock_start: f32,
    last_aim: f32,
    target: i32,
}

impl Default for MissileWeapon {
    fn default() -> Self {
        Self {
            ready_at: 0.0,
            lock_start: 0.0,
            last_aim: -100.0,
            target: -1,
        }
    }
}

impl MissileWeapon {
    pub fn new() -> Self {
        Self::default()
    }
    pub fn remaining(&self, now: f32) -> f32 {
        (self.ready_at - now).max(0.0)
    }
    /// Lock progress 0..1.
    pub fn aim(&mut self, now: f32, target: i32, valid: bool) -> f32 {
        if !valid || target < 0 {
            self.target = -1;
            self.last_aim = now;
            return 0.0;
        }
        if self.target != target || now - self.last_aim > 0.35 {
            self.lock_start = now;
        }
        self.target = target;
        self.last_aim = now;
        ((now - self.lock_start) / missile::LOCK_SECONDS).min(1.0)
    }
    fn locked(&self, now: f32, target: i32) -> bool {
        target >= 0
            && target == self.target
            && now - self.last_aim <= 0.35
            && now - self.lock_start >= missile::LOCK_SECONDS
    }
    pub fn try_fire(&mut self, now: f32, eligible: bool, guided: bool, target: i32) -> bool {
        if !eligible || self.remaining(now) > 0.0 || (guided && !self.locked(now, target)) {
            return false;
        }
        self.ready_at = now + missile::COOLDOWN;
        true
    }
}

/// Bullets that can shoot down a missile or drone: three distinct hits.
#[derive(Clone, Debug, Default)]
pub struct Armor {
    hits: HashSet<(u8, i32)>,
}

impl Armor {
    pub fn hit(&mut self, player: u8, bullet: i32) -> bool {
        self.hits.len() < missile::BULLET_HITS && self.hits.insert((player, bullet))
    }
    pub fn hits(&self) -> usize {
        self.hits.len()
    }
    pub fn destroyed(&self) -> bool {
        self.hits.len() >= missile::BULLET_HITS
    }
}

pub mod drone {
    /// The FPV drone's item id (registered by the FlyingControls mod).
    pub const ITEM: i32 = 30001;
    pub const LINK_TIMEOUT: f32 = 2.0;
    pub const RADIUS: f32 = 0.35;
    pub const FLIGHT_SECONDS: f32 = 60.0;
    pub const MAX_RANGE: f32 = 1000.0;
    pub fn remaining(born: f32, now: f32) -> f32 {
        (FLIGHT_SECONDS - (now - born).max(0.0)).max(0.0)
    }
    pub fn valid_step(distance: f32, elapsed: f32) -> bool {
        elapsed >= 0.0 && distance <= 50.0 * elapsed.min(LINK_TIMEOUT) + 2.0
    }
}

/// Blast damage (missiles: hand grenade; FPV: dynamite) for bots, which have no client to take
/// the explosion themselves.
pub mod blast {
    pub const DYNAMITE_DAMAGE: f32 = 100.0;
    pub const DYNAMITE_RADIUS: f32 = 16.0;
    pub const HAND_RADIUS: f32 = 10.0;
    /// Linear falloff to zero at the edge; walls block it.
    pub fn damage(base: f32, radius: f32, distance: f32, blocked: bool) -> f32 {
        if blocked || distance > radius || !distance.is_finite() {
            return 0.0;
        }
        base * (1.0 - distance / radius).clamp(0.0, 1.0)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn aircraft_life_follows_the_mod() {
        let mut life = AircraftLife::new(0.0);
        assert!(!life.damage(100.0, 1.0), "protected right after spawning");
        life.set_grounded(true);
        assert_eq!((life.health, life.maximum), (300.0, 300.0));
        assert!(!life.damage(100.0, 3.0));
        life.set_grounded(false);
        assert!((life.health - 20.0).abs() < 1e-4, "{}", life.health);
        life.start(3.0);
        assert!(!life.damage(5.0, 4.0), "the pilot's start protection");
        assert!(life.damage(25.0, 5.5));
        assert!(!life.damage(1.0, 6.0), "only once");
        assert!(is_aircraft("Heli") && is_aircraft("Ufo") && !is_aircraft("Bike"));
        assert!(is_heli("Heli(Clone)") && !is_heli("Ufo"));
        assert_eq!(survivor_health(80.0), 30.0);
        assert_eq!(survivor_health(20.0), 1.0);
    }

    #[test]
    fn hulls_are_hit_and_aimed_at() {
        let hull = Hull::new(&[[-1.0, -0.5, -2.0, 1.0, 1.0, 2.0]]);
        assert_eq!(hull.center, [0.0, 0.25, 0.0]);
        assert!((hull.ground_clearance - 0.7).abs() < 1e-6);
        // Rotated 90° about Y: the long side now lies along X.
        let q = euler([0.0, 90.0, 0.0]);
        let at = [10.0, 0.0, 0.0];
        let t = hull
            .hit([0.0, 0.0, 0.0], [20.0, 0.0, 0.0], at, q, 0.0)
            .expect("hits");
        assert!((t - 0.4).abs() < 1e-3, "{t}");
        assert!(
            hull.hit([0.0, 5.0, 0.0], [20.0, 5.0, 0.0], at, q, 0.0)
                .is_none()
        );
        let aim = hull.aim_points([0.0, 0.0, 0.0], [1.0, 0.0, 0.0], at, q);
        assert!((aim[0][0] - 10.0).abs() < 1e-3);
    }

    #[test]
    fn euler_round_trips() {
        for e in [[10.0, 20.0, 30.0], [0.0, 270.0, 0.0], [350.0, 45.0, 5.0]] {
            let back = euler_angles(euler(e));
            for i in 0..3 {
                let diff = (back[i] - e[i]).rem_euclid(360.0);
                assert!(diff < 0.01 || diff > 359.99, "{e:?} → {back:?}");
            }
        }
        let fwd = rotate(euler([0.0, 90.0, 0.0]), [0.0, 0.0, 1.0]);
        assert!((fwd[0] - 1.0).abs() < 1e-5, "{fwd:?}");
    }

    #[test]
    fn missiles_lock_turn_and_get_shot_down() {
        let mut w = MissileWeapon::new();
        assert_eq!(w.aim(0.0, 3, true), 0.0);
        w.aim(0.3, 3, true);
        assert!(!w.try_fire(0.3, true, true, 3), "not locked yet");
        w.aim(0.5, 3, true);
        assert!(w.aim(0.7, 3, true) >= 1.0);
        assert!(w.try_fire(0.7, true, true, 3));
        assert!(!w.try_fire(1.0, true, false, -1), "cooldown");
        assert!(
            w.try_fire(4.8, true, false, -1),
            "unguided after the cooldown"
        );
        let d = turn([0.0, 0.0, 1.0], [1.0, 0.0, 0.0], 0.1);
        assert!((dot(d, [0.0, 0.0, 1.0]) - 0.1f32.cos()).abs() < 1e-4);
        let mut armor = Armor::default();
        assert!(armor.hit(1, 7) && !armor.hit(1, 7) && armor.hit(2, 7) && armor.hit(1, 8));
        assert!(armor.destroyed() && !armor.hit(3, 1));
        assert_eq!(
            segment_sphere([0.0; 3], [10.0, 0.0, 0.0], [5.0, 0.0, 0.0], 1.0),
            Some(0.4)
        );
    }
}
