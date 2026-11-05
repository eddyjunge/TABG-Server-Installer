//! Battle Royale extras from the official server: spell drops (`Spelldrop_Server`) and the
//! respawn minigame (the boss fight, `AllowRespawnMinigame`).

use tabg_protocol::{EventCode, Reader, Writer};

use super::{NOBODY, Outgoing, Room};

/// Rarity bytes (`Curse.Rarity`).
const RARE: u8 = 1;
const EPIC: u8 = 2;
const LEGENDARY: u8 = 3;
/// Spectate value in `PlayerDead` that sends the victim into the boss fight.
pub(super) const BOSS_FIGHT: u8 = 254;

#[derive(Clone, Debug, PartialEq, Default)]
pub struct SpellDrops {
    /// Seconds between drops (official `Spelldrop_Server.min/max`).
    pub interval: [f32; 2],
    pub rare: Vec<i32>,
    pub epic: Vec<i32>,
    pub legendary: Vec<i32>,
}

#[derive(Debug)]
pub(super) struct Drop {
    position: [f32; 3],
    rarity: u8,
    opened: bool,
}

impl Room {
    /// First drop 60–100 s after the ring starts (official), then every `interval`.
    pub(super) fn schedule_first_drop(&mut self, now: f32) {
        self.drops.clear();
        self.next_drop_at = self
            .settings
            .spell_drops
            .as_ref()
            .filter(|d| !d.rare.is_empty() || !d.epic.is_empty() || !d.legendary.is_empty())
            .map(|_| now + self.rng.range(60.0, 100.0));
    }

    pub(super) fn tick_spell_drops(&mut self, now: f32) -> Vec<Outgoing> {
        let (Some(at), Some(rules)) = (self.next_drop_at, self.settings.spell_drops.clone()) else {
            return Vec::new();
        };
        if now < at {
            return Vec::new();
        }
        let mut wait = self
            .rng
            .range(rules.interval[0], rules.interval[1].max(rules.interval[0]));
        if self.rng.unit() < 0.1 {
            wait *= self.rng.range(0.01, 1.0);
        }
        self.next_drop_at = Some(now + wait.max(5.0));
        // On the ground inside the white ring, above the water line (official: y > 110).
        let (c, r) = (self.ring.white_center, self.ring.white_size * 0.45);
        let spots: Vec<[f32; 3]> = self
            .settings
            .loot
            .spots
            .iter()
            .map(|s| s.position)
            .filter(|p| p[1] > 110.0 && (p[0] - c[0]).hypot(p[2] - c[2]) < r)
            .collect();
        if spots.is_empty() {
            return Vec::new();
        }
        let position = spots[self.rng.index(spots.len())];
        let roll = self.rng.unit();
        let rarity = if roll < 0.15 {
            LEGENDARY
        } else if roll < 0.5 {
            EPIC
        } else {
            RARE
        };
        let index = i32::try_from(self.drops.len()).unwrap_or(i32::MAX);
        self.drops.push(Drop {
            position,
            rarity,
            opened: false,
        });
        // [i32 index][position][u8 rarity]
        let mut w = Writer::new();
        w.i32(index).vec3(position).u8(rarity);
        vec![Self::broadcast(EventCode::DropSpawned, w.into_inner())]
    }

    /// `RequestClickInteract` `[u8][i32 drop]`: opening a spell drop spills blessings (3 rare,
    /// 2 epic or 1 legendary) — `DropOpened` `[i32 drop][u8 n]([item][network id][qty])*`.
    pub(super) fn open_drop(&mut self, peer: u8, payload: &[u8]) -> Vec<Outgoing> {
        let mut r = Reader::new(payload);
        let parsed = (|| Ok::<_, tabg_protocol::DecodeError>((r.u8()?, r.i32()?)))();
        let Ok((_, index)) = parsed else {
            return Vec::new();
        };
        let Some(rules) = self.settings.spell_drops.clone() else {
            return Vec::new();
        };
        if !self.players.contains_key(&peer) {
            return Vec::new();
        }
        let Some(drop) = usize::try_from(index)
            .ok()
            .and_then(|i| self.drops.get_mut(i))
        else {
            return Vec::new();
        };
        if drop.opened {
            return Vec::new();
        }
        drop.opened = true;
        let (position, rarity) = (drop.position, drop.rarity);
        let (pool, count) = match rarity {
            LEGENDARY => (&rules.legendary, 1),
            EPIC => (&rules.epic, 2),
            _ => (&rules.rare, 3),
        };
        let mut w = Writer::new();
        let items: Vec<i32> = (0..count)
            .filter(|_| !pool.is_empty())
            .map(|_| pool[self.rng.index(pool.len())])
            .collect();
        w.i32(index).u8(u8::try_from(items.len()).unwrap_or(0));
        for item in items {
            let loot = self.loot.add(item, 1, position);
            w.i32(item).i32(loot.network_id).i32(1);
        }
        vec![Self::broadcast(EventCode::DropOpened, w.into_inner())]
    }

    /// Whether a Battle Royale death goes to the boss fight (official rules: the setting is
    /// on, the player hasn't had one this match, and the ring isn't in its last two circles).
    pub(super) fn enters_boss_fight(&self, victim: u8) -> bool {
        self.settings.respawn_minigame
            && self.settings.game_mode == tabg_protocol::messages::game_mode::BATTLE_ROYALE
            && self.settings.respawn.is_none()
            && self.players.get(&victim).is_some_and(|p| !p.boss_done)
            && self.ring.id + 2 < self.settings.ring.sizes.len()
    }

    /// `BossFightResult` `[won][bonus]`: winners come back (bonus 0: with a random curse;
    /// 2: with a random blessing); losers stay dead, and their team may now be out.
    pub(super) fn boss_fight_result(&mut self, peer: u8, payload: &[u8]) -> Vec<Outgoing> {
        let (Some(&won), Some(&bonus)) = (payload.first(), payload.get(1)) else {
            return Vec::new();
        };
        let Some(p) = self.players.get_mut(&peer).filter(|p| p.boss_fight) else {
            return Vec::new();
        };
        p.boss_fight = false;
        let group = p.group;
        if won != 1 {
            return self.team_dead_if_out(group);
        }
        let curse = if bonus == 0 && !self.settings.curses.is_empty() {
            self.settings.curses[self.rng.index(self.settings.curses.len())]
        } else {
            u8::MAX
        };
        let p = self.players.get_mut(&peer).expect("checked");
        p.alive = true;
        p.downed = false;
        p.health = 100.0;
        p.dropped = true;
        p.landed = false;
        p.last_attacker = NOBODY;
        // The client puts its own player where it stood in the boss arena; others get this.
        let position = [0.0, -100.0, 0.0];
        p.position = position;
        let mut w = Writer::new();
        w.u8(1)
            .u8(peer)
            .u8(peer)
            .f32(100.0)
            .vec3(position)
            .f32(0.0)
            .u8(curse);
        let mut out = vec![Self::broadcast(
            EventCode::PlayerRespawnFromBoss,
            w.into_inner(),
        )];
        if bonus == 2
            && let Some(blessing) = self.settings.spell_drops.as_ref().and_then(|d| {
                let all: Vec<i32> = d
                    .rare
                    .iter()
                    .chain(&d.epic)
                    .chain(&d.legendary)
                    .copied()
                    .collect();
                (!all.is_empty()).then(|| all[self.rng.index(all.len())])
            })
        {
            out.push(self.grant(peer, &[(blessing, 1)]));
        }
        out
    }
}
