//! Bots (FakePlayers' successor): server-side players that join the lobby, ride the plane, jump,
//! land with a gun, walk the walkability grid towards loot and into the ring, and fight whoever
//! they can see. To clients they are ordinary players; their shots arrive as `PlayerFire` and
//! hits as `PlayerDamaged`. Players damage and kill them the normal way.

use std::collections::BTreeMap;
use std::sync::Arc;

use tabg_protocol::combat::DamageEvent;
use tabg_protocol::loot::chunk_of;
use tabg_protocol::match_flow::RequestAirplaneDrop;
use tabg_protocol::messages::{
    PackedDirection, PackedQuaternion, PlayerJoined, game_mode, game_state,
};
use tabg_protocol::{EventCode, Writer};

use super::{Outgoing, Recipients, Room};
use crate::nav::{EYE, NavGrid};

/// How often a bot thinks (seconds).
const THINK: f32 = 0.1;
const WALK_SPEED: f32 = 5.0;
const FALL_SPEED: f32 = 35.0;
/// How far a bot looks for someone to fight.
const SIGHT: f32 = 70.0;
const PATH_BUDGET: usize = 8000;
/// After landing a bot holds fire this long (players need a moment to grab a gun).
const LANDING_GRACE: f32 = 5.0;
/// Seconds before a bot opens fire on someone it just spotted (min, extra random).
const REACTION: (f32, f32) = (0.5, 0.7);
/// Bot damage relative to the gun's damage, and the best hit chance at any range.
const DAMAGE_SCALE: f32 = 0.7;
const MAX_HIT_CHANCE: f32 = 0.5;
/// Storm damage per second for bots outside the ring, by ring number (players' own clients
/// apply theirs; bots have none).
const RING_DAMAGE: [f32; 7] = [1.0, 1.0, 2.0, 3.0, 5.0, 8.0, 10.0];

const NAMES: &[&str] = &[
    "Bot Alva",
    "Bot Bertil",
    "Bot Cissi",
    "Bot Dante",
    "Bot Edda",
    "Bot Frans",
    "Bot Greta",
    "Bot Hugo",
    "Bot Iris",
    "Bot Jonte",
    "Bot Kaj",
    "Bot Lova",
    "Bot Malte",
    "Bot Nora",
    "Bot Otto",
    "Bot Pia",
    "Bot Ruben",
    "Bot Saga",
    "Bot Tage",
    "Bot Ulla",
    "Bot Vide",
    "Bot Wilma",
    "Bot Yngve",
    "Bot Zelda",
];

/// A gun a bot can carry: item, its ammo, damage per hit, seconds between shots, hit chance at
/// 10 m (falling off with distance).
#[derive(Clone, Copy, Debug)]
struct Gun {
    item: i32,
    ammo: i32,
    damage: f32,
    interval: f32,
    accuracy: f32,
}

const GUNS: &[Gun] = &[
    Gun {
        item: 160,
        ammo: 6,
        damage: 14.0,
        interval: 0.25,
        accuracy: 0.55,
    }, // M16
    Gun {
        item: 152,
        ammo: 1,
        damage: 18.0,
        interval: 0.3,
        accuracy: 0.5,
    }, // AK-47
    Gun {
        item: 310,
        ammo: 9,
        damage: 10.0,
        interval: 0.12,
        accuracy: 0.45,
    }, // MP5-K
    Gun {
        item: 297,
        ammo: 8,
        damage: 40.0,
        interval: 1.0,
        accuracy: 0.6,
    }, // Mossberg-500
    Gun {
        item: 277,
        ammo: 9,
        damage: 16.0,
        interval: 0.45,
        accuracy: 0.5,
    }, // m1911
];

#[derive(Debug)]
pub(super) struct Brain {
    /// Fraction of the flight after which to jump.
    jump_at: f32,
    gun: Option<Gun>,
    path: Vec<[f32; 3]>,
    think_at: f32,
    next_shot: f32,
    target: Option<u8>,
    /// Give up on a goal after this.
    goal_until: f32,
    /// Holds fire until then (landing grace, reaction time).
    calm_until: f32,
}

impl Room {
    pub fn is_bot(&self, peer: u8) -> bool {
        self.bots.contains_key(&peer)
    }

    /// Bots play Battle Royale on the island, where there's a walkability grid.
    fn bots_wanted(&self) -> usize {
        let island =
            self.settings.game_mode == game_mode::BATTLE_ROYALE && self.settings.test_map.is_none();
        if self.settings.nav.is_none() || !island {
            return 0;
        }
        let humans = self.players.keys().filter(|p| !self.is_bot(**p)).count();
        if humans == 0 {
            return 0;
        }
        usize::from(self.settings.bots)
    }

    /// Bot player indices count down from 253, above every network peer id.
    fn free_bot_index(&self) -> Option<u8> {
        let lowest = u16::from(self.settings.max_players) + 2;
        (lowest..=253)
            .rev()
            .map(|i| i as u8)
            .find(|i| !self.players.contains_key(i) && !self.pending.contains(i))
    }

    /// Add or remove bots to match the setting (only in the lobby).
    pub(super) fn manage_bots(&mut self) -> Vec<Outgoing> {
        if self.state != game_state::WAITING_FOR_PLAYERS {
            return Vec::new();
        }
        let wanted = self.bots_wanted();
        let mut out = Vec::new();
        while self.bots.len() > wanted {
            let Some(&bot) = self.bots.keys().next() else {
                break;
            };
            self.bots.remove(&bot);
            out.extend(self.disconnect(bot));
        }
        while self.bots.len() < wanted {
            let Some(index) = self.free_bot_index() else {
                break;
            };
            let name = NAMES[usize::from(253 - index) % NAMES.len()].to_owned();
            let squad = 0xB07_0000 + u64::from(index);
            let group = self.group_for(index, squad);
            let spawn = self.spawn_for(group);
            let mut p =
                super::Player::new(index, group, name.clone(), format!("bot:{index}"), squad);
            p.in_world = true;
            p.position = spawn.position;
            p.rotation = [0.0, spawn.rotation];
            p.chunk = chunk_of(spawn.position);
            self.players.insert(index, p);
            self.bots.insert(
                index,
                Brain {
                    jump_at: 0.0,
                    gun: None,
                    path: Vec::new(),
                    think_at: 0.0,
                    next_shot: 0.0,
                    target: None,
                    goal_until: 0.0,
                    calm_until: 0.0,
                },
            );
            out.push(Outgoing::reliable(
                Recipients::All,
                EventCode::Login,
                PlayerJoined {
                    player_index: index,
                    group_index: group,
                    player_name: name,
                    gear: Vec::new(),
                    is_dev: false,
                    colour: 0,
                }
                .encode(),
            ));
        }
        out
    }

    /// A new match: every bot picks when to jump and drops its gun.
    pub(super) fn bots_board(&mut self) {
        let jumps: Vec<(u8, f32)> = self
            .bots
            .keys()
            .map(|&b| {
                (
                    b,
                    self.settings
                        .bot_jump
                        .unwrap_or(0.15 + 0.7 * self.rng.unit()),
                )
            })
            .collect();
        for (bot, at) in jumps {
            if let Some(brain) = self.bots.get_mut(&bot) {
                brain.jump_at = at;
                brain.gun = None;
                brain.path.clear();
                brain.target = None;
                brain.calm_until = 0.0;
            }
        }
    }

    fn nav(&self) -> Option<Arc<NavGrid>> {
        self.settings.nav.clone()
    }

    pub(super) fn tick_bots(&mut self, now: f32, dt: f32) -> Vec<Outgoing> {
        let mut out = self.manage_bots();
        if self.state != game_state::STARTED || self.bots.is_empty() {
            return out;
        }
        let Some(nav) = self.nav() else {
            return out;
        };
        let bots: Vec<u8> = self.bots.keys().copied().collect();
        for bot in bots {
            out.extend(self.tick_bot(bot, now, dt, &nav));
        }
        if now >= self.bot_ring_at {
            self.bot_ring_at = now + 1.0;
            out.extend(self.bot_ring_damage(now));
        }
        out
    }

    /// Bots outside the storm's edge lose health every second, and die a ring death at 0.
    fn bot_ring_damage(&mut self, now: f32) -> Vec<Outgoing> {
        if self.settings.game_mode != game_mode::BATTLE_ROYALE || self.settings.test_map.is_some() {
            return Vec::new();
        }
        let (centre, size) = self.ring.current(now);
        let per_second = RING_DAMAGE[self.ring.id.min(RING_DAMAGE.len() - 1)];
        let outside: Vec<u8> = self
            .bots
            .keys()
            .copied()
            .filter(|b| {
                self.players.get(b).is_some_and(|p| {
                    p.alive
                        && p.landed
                        && (p.position[0] - centre[0]).hypot(p.position[2] - centre[2]) > size * 0.5
                })
            })
            .collect();
        let mut out = Vec::new();
        for bot in outside {
            let p = self.players.get_mut(&bot).expect("filtered");
            p.health = (p.health - per_second).max(0.0);
            let health = p.health;
            if health <= 0.0 {
                out.extend(self.kill(bot, true));
            } else {
                out.push(Outgoing::reliable(
                    Recipients::Many(self.near(bot)),
                    EventCode::PlayerHealthStateChanged,
                    tabg_protocol::actions::encode_player_health(bot, health),
                ));
            }
        }
        out
    }

    fn tick_bot(&mut self, bot: u8, now: f32, dt: f32, nav: &NavGrid) -> Vec<Outgoing> {
        let Some(p) = self.players.get(&bot) else {
            return Vec::new();
        };
        if !p.alive || p.downed {
            return Vec::new();
        }
        let mut out = Vec::new();
        // In the plane: jump when it's time.
        if !p.dropped {
            let progress = self.plane.as_ref().map_or(1.0, |plane| plane.progress(now));
            // Jump at the chosen moment, but only over land (else wait; the plane drops
            // everyone at the end anyway).
            let over_land = self.plane.as_ref().is_some_and(|plane| {
                let at = plane.drop_position(now);
                nav.height(at[0], at[2]).is_some()
            });
            if progress >= self.bots[&bot].jump_at && over_land {
                let req = RequestAirplaneDrop {
                    player_index: bot,
                    direction: [0.0, -1.0, 0.0],
                };
                out.extend(self.airplane_drop(bot, req));
            }
            return out;
        }
        // Falling: straight down to the ground, then land and take out the gun.
        if !p.landed {
            let pos = p.position;
            let ground = nav.height(pos[0], pos[2]).unwrap_or(109.0);
            let y = (pos[1] - FALL_SPEED * dt).max(ground);
            let p = self.players.get_mut(&bot).expect("checked");
            p.position[1] = y;
            p.dirty = true;
            if y <= ground + 0.01 {
                p.landed = true;
                let mut w = Writer::new();
                w.u8(bot).vec3(p.position);
                out.push(Outgoing::reliable(
                    Recipients::All,
                    EventCode::PlayerLand,
                    w.into_inner(),
                ));
                if let Some(b) = self.bots.get_mut(&bot) {
                    b.calm_until = now + LANDING_GRACE;
                }
                out.extend(self.bot_arm(bot));
            }
            return out;
        }
        // Started on the ground (Test mode, respawns): take out a gun first.
        if self.bots[&bot].gun.is_none() {
            out.extend(self.bot_arm(bot));
        }
        if now < self.bots[&bot].think_at {
            return out;
        }
        if let Some(b) = self.bots.get_mut(&bot) {
            b.think_at = now + THINK;
        }
        let step = THINK.max(dt);
        let me = self.players[&bot].position;
        // Fight whoever is closest and visible.
        let enemy = self
            .players
            .values()
            .filter(|o| {
                o.index != bot && o.alive && o.dropped && o.group != self.players[&bot].group
            })
            .map(|o| (o.index, o.position, dist(me, o.position)))
            .filter(|(_, _, d)| *d < SIGHT)
            .min_by(|a, b| a.2.total_cmp(&b.2))
            .filter(|(_, pos, _)| nav.line_of_sight(me, *pos));
        if let Some((target, pos, d)) = enemy {
            self.face(bot, pos);
            let reaction = REACTION.0 + REACTION.1 * self.rng.unit();
            if let Some(b) = self.bots.get_mut(&bot)
                && b.target != Some(target)
            {
                b.target = Some(target);
                b.calm_until = b.calm_until.max(now + reaction);
            }
            if now < self.bots[&bot].calm_until {
                return out;
            }
            out.extend(self.bot_shoot(bot, target, pos, d, now));
            return out;
        }
        if let Some(b) = self.bots.get_mut(&bot) {
            b.target = None;
        }
        // In the water: swim to the nearest shore.
        if nav.height(me[0], me[2]).is_none() {
            out.extend(self.bot_swim(bot, step, nav));
            return out;
        }
        // Otherwise walk: into the ring if outside it, else somewhere nearby.
        let needs_goal = {
            let b = &self.bots[&bot];
            b.path.is_empty() || now > b.goal_until
        };
        if needs_goal {
            let goal = self.bot_goal(me, nav);
            let path = goal
                .and_then(|g| nav.path(me, g, PATH_BUDGET))
                .unwrap_or_default();
            if let Some(b) = self.bots.get_mut(&bot) {
                b.path = path;
                b.goal_until = now + 30.0;
                b.target = None;
            }
        }
        out.extend(self.bot_walk(bot, step, nav));
        out
    }

    fn bot_goal(&mut self, me: [f32; 3], nav: &NavGrid) -> Option<[f32; 3]> {
        let (centre, size) = (self.ring.white_center, self.ring.white_size * 0.45);
        let outside = dist(me, [centre[0], me[1], centre[2]]) > size;
        let target = if outside {
            // A step towards the ring centre (paths are local; long trips go in legs).
            let dir = norm([centre[0] - me[0], 0.0, centre[2] - me[2]]);
            [me[0] + dir[0] * 60.0, me[1], me[2] + dir[2] * 60.0]
        } else {
            me
        };
        nav.random_point_near(target, if outside { 20.0 } else { 50.0 }, &mut self.rng)
    }

    fn bot_walk(&mut self, bot: u8, step: f32, nav: &NavGrid) -> Vec<Outgoing> {
        let Some(&next) = self.bots.get(&bot).and_then(|b| b.path.first()) else {
            return Vec::new();
        };
        let me = self.players[&bot].position;
        let to = [next[0] - me[0], 0.0, next[2] - me[2]];
        let d = to[0].hypot(to[2]);
        let travel = WALK_SPEED * step;
        let pos = if d <= travel {
            if let Some(b) = self.bots.get_mut(&bot) {
                b.path.remove(0);
            }
            next
        } else {
            let k = travel / d;
            let (x, z) = (me[0] + to[0] * k, me[2] + to[2] * k);
            [x, nav.height(x, z).unwrap_or(me[1]), z]
        };
        self.face(bot, next);
        let old_chunk = self.players[&bot].chunk;
        let p = self.players.get_mut(&bot).expect("bot");
        p.position = pos;
        p.direction = PackedDirection::pack([0.0, 0.0, 1.0]);
        p.dirty = true;
        let chunk = chunk_of(pos);
        if chunk != old_chunk {
            return self.chunk_changed(bot, chunk);
        }
        Vec::new()
    }

    fn bot_swim(&mut self, bot: u8, step: f32, nav: &NavGrid) -> Vec<Outgoing> {
        let me = self.players[&bot].position;
        // Nearest land on rings of growing radius.
        let mut shore = None;
        'search: for r in (4..400).step_by(4) {
            let r = r as f32;
            for k in 0..32 {
                let a = k as f32 / 32.0 * std::f32::consts::TAU;
                let (x, z) = (me[0] + r * a.sin(), me[2] + r * a.cos());
                if let Some(y) = nav.height(x, z) {
                    shore = Some([x, y, z]);
                    break 'search;
                }
            }
        }
        let Some(shore) = shore else {
            return Vec::new();
        };
        let dir = norm([shore[0] - me[0], 0.0, shore[2] - me[2]]);
        let travel = WALK_SPEED * 0.6 * step;
        let pos = [me[0] + dir[0] * travel, 109.0, me[2] + dir[2] * travel];
        self.face(bot, shore);
        let old_chunk = self.players[&bot].chunk;
        let p = self.players.get_mut(&bot).expect("bot");
        p.position = pos;
        p.direction = PackedDirection::pack([0.0, 0.0, 1.0]);
        p.dirty = true;
        if let Some(b) = self.bots.get_mut(&bot) {
            b.path.clear();
        }
        let chunk = chunk_of(pos);
        if chunk != old_chunk {
            return self.chunk_changed(bot, chunk);
        }
        Vec::new()
    }

    fn face(&mut self, bot: u8, at: [f32; 3]) {
        let me = self.players[&bot].position;
        let yaw = (at[0] - me[0])
            .atan2(at[2] - me[2])
            .to_degrees()
            .rem_euclid(360.0);
        let flat = (at[0] - me[0]).hypot(at[2] - me[2]).max(0.01);
        let pitch = -((at[1] - me[1]) / flat).atan().to_degrees();
        if let Some(p) = self.players.get_mut(&bot) {
            p.rotation = [pitch, yaw];
            p.direction = PackedDirection::ZERO;
            p.dirty = true;
        }
    }

    /// On landing: a random gun (and its ammo) in hand.
    fn bot_arm(&mut self, bot: u8) -> Vec<Outgoing> {
        let gun = GUNS[self.rng.index(GUNS.len())];
        if let Some(b) = self.bots.get_mut(&bot) {
            b.gun = Some(gun);
        }
        self.inventory_add(bot, gun.item, 1);
        self.inventory_add(bot, gun.ammo, 120);
        // WeaponChanged: [player][slot][w1][w1 attachment][w2][w2 attachment][w3][n attachments]
        let mut w = Writer::new();
        w.u8(bot).u8(1);
        w.u16(gun.item as u16)
            .u16(u16::MAX)
            .u16(u16::MAX)
            .u16(u16::MAX)
            .u16(u16::MAX)
            .u8(0);
        let payload = w.into_inner();
        self.track_equipment(bot, &payload);
        vec![Outgoing::reliable(
            Recipients::All,
            EventCode::WeaponChanged,
            payload,
        )]
    }

    fn bot_shoot(&mut self, bot: u8, target: u8, at: [f32; 3], d: f32, now: f32) -> Vec<Outgoing> {
        let Some(gun) = self.bots[&bot].gun else {
            return Vec::new();
        };
        if now < self.bots[&bot].next_shot {
            return Vec::new();
        }
        if let Some(b) = self.bots.get_mut(&bot) {
            b.next_shot = now + gun.interval * (0.8 + 0.4 * self.rng.unit());
        }
        let me = self.players[&bot].position;
        let eye = [me[0], me[1] + EYE, me[2]];
        let aim = norm([at[0] - eye[0], at[1] + 1.2 - eye[1], at[2] - eye[2]]);
        // PlayerFire: [player][mode Semi|ContainsDirection][i32 ammo][position][rotation]
        let mut w = Writer::new();
        w.u8(bot).u8(0x01 | 0x10).i32(gun.ammo).vec3(eye);
        let rotation = look_rotation(aim);
        w.bytes(&PackedQuaternion::pack(rotation).0);
        let mut to = self.near(bot);
        to.retain(|&p| p != bot);
        let mut out = vec![Outgoing {
            to: Recipients::Many(to),
            packet: tabg_protocol::Packet::new(EventCode::PlayerFire, w.into_inner()),
            reliable: false,
        }];
        let chance = (gun.accuracy * (10.0 / d.max(10.0)).powf(0.6)).clamp(0.05, MAX_HIT_CHANCE);
        if self.rng.unit() < chance {
            let health = self.players.get(&target).map_or(0.0, |p| p.health);
            let ev = DamageEvent {
                attacker: bot,
                victim: target,
                new_health: (health - gun.damage * DAMAGE_SCALE).max(0.0),
                direction: aim,
                hit: None,
                exclude_victim: false,
            };
            out.extend(self.damage(bot, ev));
        }
        out
    }

    /// What a bot is up to, for the radar: state, where it's walking, whom it's fighting, and
    /// its gun.
    pub(super) fn bot_view(&self, bot: u8) -> Option<BotView> {
        let (b, p) = (self.bots.get(&bot)?, self.players.get(&bot)?);
        let swimming = self
            .settings
            .nav
            .as_ref()
            .is_some_and(|n| n.height(p.position[0], p.position[2]).is_none());
        let state = if !p.dropped {
            BotState::Plane
        } else if !p.landed {
            BotState::Falling
        } else if b.target.is_some() {
            BotState::Fighting
        } else if swimming {
            BotState::Swimming
        } else {
            BotState::Walking
        };
        Some(BotView {
            state,
            goal: b.path.last().copied(),
            target: b.target,
            gun: b.gun.map(|g| g.item),
        })
    }

    /// Forget bots that left (they are removed with the room's players on reset).
    pub(super) fn forget_bot(&mut self, peer: u8) {
        self.bots.remove(&peer);
    }
}

fn dist(a: [f32; 3], b: [f32; 3]) -> f32 {
    ((a[0] - b[0]).powi(2) + (a[1] - b[1]).powi(2) + (a[2] - b[2]).powi(2)).sqrt()
}

fn norm(v: [f32; 3]) -> [f32; 3] {
    let l = (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]).sqrt().max(1e-6);
    [v[0] / l, v[1] / l, v[2] / l]
}

/// Quaternion (x, y, z, w) looking along `dir` (Unity's LookRotation with up = Y).
fn look_rotation(dir: [f32; 3]) -> [f32; 4] {
    let yaw = dir[0].atan2(dir[2]);
    let pitch = -dir[1].asin();
    let (sy, cy) = (yaw * 0.5).sin_cos();
    let (sp, cp) = (pitch * 0.5).sin_cos();
    // yaw about Y, then pitch about X
    [cy * sp, sy * cp, -sy * sp, cy * cp]
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
pub(super) enum BotState {
    Plane = 0,
    Falling = 1,
    Walking = 2,
    Fighting = 3,
    Swimming = 4,
}

pub(super) struct BotView {
    pub state: BotState,
    pub goal: Option<[f32; 3]>,
    pub target: Option<u8>,
    pub gun: Option<i32>,
}

pub(super) type Brains = BTreeMap<u8, Brain>;
