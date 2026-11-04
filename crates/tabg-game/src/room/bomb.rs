//! Bomb (the TABG_CS map), following the official `BombGameMode`: two teams, rounds with a buy
//! phase (the countdown), money, a bomb the T team plants and the CT team defuses, side switch
//! at half time, and the first team to `rounds_to_win` wins.
//!
//! Team 0 starts as T, team 1 as CT. A round ends when a team is wiped, the bomb explodes or is
//! defused, or time runs out without a plant (CT wins).

use std::collections::BTreeMap;

use tabg_protocol::combat::TeamDead;
use tabg_protocol::loot::{chunk_of, encode_item_drop};
use tabg_protocol::match_flow::GameStateChanged;
use tabg_protocol::messages::game_state;
use tabg_protocol::{EventCode, Packet, Reader, Writer};

use super::{NOBODY, Outgoing, Recipients, Room, RoomEvent};
use crate::settings::{BombRules, SpawnPoint};

const START_MONEY: i32 = 500;
const MONEY_FOR_KILL: i32 = 200;
const MONEY_FOR_PLANT: i32 = 300;
const MONEY_FOR_DEFUSE: i32 = 300;
const MONEY_FOR_ROUND_WIN: i32 = 3000;
const MONEY_FOR_ROUND_LOSS: i32 = 2000;
/// Pause between rounds.
const INTERMISSION: f32 = 10.0;
/// Weapons are handed out shortly after the respawn, like the official 0.2 s delay.
const GIVE_DELAY: f32 = 0.2;

const ROUND_OVER: u8 = 6;
const INTERMISSION_STATE: u8 = 7;

/// Why a round ended (`RoundOver` extra byte).
const BY_TIME: u8 = 0;
const BY_ELIMINATION: u8 = 1;
const BY_EXPLOSION: u8 = 2;
const BY_DEFUSE: u8 = 3;

#[derive(Debug, Default)]
pub(super) struct BombState {
    /// Team index currently playing CT.
    ct: u8,
    switched: bool,
    round: usize,
    scores: [u8; 2],
    /// Per round: did CT win?
    history: Vec<bool>,
    money: BTreeMap<u8, i32>,
    /// The gun a player keeps into the next round (bought or carried).
    weapons: BTreeMap<u8, i32>,
    round_started_at: f32,
    planted_at: Option<f32>,
    defuser: Option<(u8, f32)>,
    holder: Option<u8>,
    give_at: Option<f32>,
    next_round_at: Option<f32>,
}

fn raw_state(bytes: Vec<u8>) -> Outgoing {
    Outgoing {
        to: Recipients::All,
        packet: Packet::new(EventCode::GameStateChanged, bytes),
        reliable: true,
    }
}

fn all(event: EventCode, payload: Vec<u8>) -> Outgoing {
    Outgoing {
        to: Recipients::All,
        packet: Packet::new(event, payload),
        reliable: true,
    }
}

impl Room {
    pub(super) fn is_bomb(&self) -> bool {
        self.settings.game_mode == tabg_protocol::messages::game_mode::BOMB
            && self.settings.bomb.is_some()
    }

    fn bomb_rules(&self) -> &BombRules {
        self.settings.bomb.as_ref().expect("bomb mode")
    }

    /// New players go to the smaller team (official `GetNewGroupIndex`).
    pub(super) fn bomb_group(&self) -> u8 {
        let count = |g: u8| self.players.values().filter(|p| p.group == g).count();
        if count(0) <= count(1) { 0 } else { 1 }
    }

    /// Spawn for a team, minding the side switch.
    pub(super) fn bomb_spawn(&mut self, group: u8) -> Option<SpawnPoint> {
        let switched = self.bomb.as_ref().is_some_and(|b| b.switched);
        let side = usize::from(if switched {
            1 - group.min(1)
        } else {
            group.min(1)
        });
        let spawns = &self.settings.bomb.as_ref()?.team_spawns[side];
        if spawns.is_empty() {
            return None;
        }
        Some(spawns[self.rng.index(spawns.len())])
    }

    fn send_money(&self) -> Outgoing {
        let state = self.bomb.as_ref().expect("bomb running");
        let mut w = Writer::new();
        w.u8(u8::try_from(self.players.len()).unwrap_or(u8::MAX));
        for &p in self.players.keys() {
            w.u8(p).i32(state.money.get(&p).copied().unwrap_or(0));
        }
        all(EventCode::MoneyChanged, w.into_inner())
    }

    fn send_scores(&self) -> Outgoing {
        let state = self.bomb.as_ref().expect("bomb running");
        let len = usize::from(self.bomb_rules().rounds_to_win).max(1) * 2 - 1;
        let mut ct = vec![false; len];
        let mut t = vec![false; len];
        for (i, &ct_won) in state.history.iter().enumerate().take(len) {
            ct[i] = ct_won;
            t[i] = !ct_won;
        }
        let mut w = Writer::new();
        w.u8(2);
        for side in [ct, t] {
            w.u8(u8::try_from(side.len()).unwrap_or(u8::MAX));
            for b in side {
                w.bool(b);
            }
        }
        all(EventCode::ScoresChanged, w.into_inner())
    }

    fn give_money(&mut self, players: &[u8], amount: i32) {
        let state = self.bomb.as_mut().expect("bomb running");
        for p in players {
            *state.money.entry(*p).or_insert(START_MONEY) += amount;
        }
    }

    fn team(&self, group: u8) -> Vec<u8> {
        self.players
            .values()
            .filter(|p| p.group == group)
            .map(|p| p.index)
            .collect()
    }

    /// `GunPurchasedAccepted` (to everyone): `[player][f32 cost][weapon][ammo][ammo qty][n][…]`.
    fn weapon_pack(
        player: u8,
        cost: f32,
        weapon: i32,
        ammo: i32,
        quantity: i32,
        attachments: &[i32],
    ) -> Outgoing {
        let mut w = Writer::new();
        w.u8(player).f32(cost).i32(weapon).i32(ammo).i32(quantity);
        w.i32(i32::try_from(attachments.len()).unwrap_or(0));
        for &a in attachments {
            w.i32(a);
        }
        all(EventCode::GunPurchasedAccepted, w.into_inner())
    }

    /// Buy phase: respawn everyone at their side, put the bomb back, pay out.
    fn start_round(&mut self, now: f32) -> Vec<Outgoing> {
        let rules = self.bomb_rules().clone();
        let state = self.bomb.get_or_insert_with(|| BombState {
            ct: 1,
            ..Default::default()
        });
        state.planted_at = None;
        state.defuser = None;
        state.holder = None;
        state.next_round_at = None;
        state.give_at = Some(now + GIVE_DELAY);
        let mut out = vec![self.send_money()];
        self.state = game_state::COUNT_DOWN;
        self.countdown_left = self.settings.countdown;
        out.push(Self::state_changed(GameStateChanged::CountDown {
            seconds: self.countdown_left,
        }));
        // Everyone respawns at their side's spawns in one PlayerRespawn.
        let peers: Vec<u8> = self.players.keys().copied().collect();
        let mut w = Writer::new();
        w.u8(u8::try_from(peers.len()).unwrap_or(u8::MAX));
        for &peer in &peers {
            let group = self.players[&peer].group;
            let spawn = self
                .bomb_spawn(group)
                .unwrap_or_else(|| self.next_spawn_point());
            let p = self.players.get_mut(&peer).expect("listed");
            p.alive = true;
            p.downed = false;
            p.health = 100.0;
            p.dropped = true;
            p.landed = true;
            p.position = spawn.position;
            p.last_attacker = NOBODY;
            p.respawn_at = None;
            w.u8(peer).u8(peer).f32(100.0);
            w.f32(spawn.position[0])
                .f32(spawn.position[1])
                .f32(spawn.position[2]);
            w.f32(spawn.rotation).u8(u8::MAX);
        }
        out.push(all(EventCode::PlayerRespawn, w.into_inner()));
        // The bomb, back at its spawn.
        let mut bomb_ids = Vec::new();
        for item in self
            .loot
            .in_chunks(&tabg_protocol::loot::area_around(chunk_of(rules.spawn)))
        {
            if item.item_id == rules.bomb_item {
                bomb_ids.push(item.network_id);
            }
        }
        for id in bomb_ids {
            self.loot.take(id);
        }
        let bomb = self.loot.add(rules.bomb_item, 1, rules.spawn);
        out.push(all(EventCode::ItemDrop, encode_item_drop(&bomb)));
        for peer in peers {
            out.extend(self.force_chunk_entry(peer));
        }
        out
    }

    fn give_round_weapons(&mut self) -> Vec<Outgoing> {
        let start = self.bomb_rules().start_weapon;
        let state = self.bomb.as_ref().expect("bomb running");
        self.players
            .keys()
            .map(|&p| {
                let weapon = state.weapons.get(&p).copied().unwrap_or(start);
                Self::weapon_pack(p, 0.0, weapon, -1, 0, &[])
            })
            .collect()
    }

    fn round_over(&mut self, now: f32, winner_team: u8, reason: u8) -> Vec<Outgoing> {
        let rules = self.bomb_rules().clone();
        let ct = self.bomb.as_ref().expect("bomb running").ct;
        let ct_won = winner_team == ct;
        let mut out = vec![raw_state(vec![ROUND_OVER, winner_team, reason])];
        let (winners, losers) = (self.team(winner_team), self.team(1 - winner_team.min(1)));
        self.give_money(&winners, MONEY_FOR_ROUND_WIN);
        self.give_money(&losers, MONEY_FOR_ROUND_LOSS);
        let state = self.bomb.as_mut().expect("bomb running");
        state.scores[usize::from(winner_team.min(1))] += 1;
        state.history.push(ct_won);
        state.round += 1;
        state.planted_at = None;
        state.defuser = None;
        if !state.switched && state.round == usize::from(rules.rounds_to_win.saturating_sub(1)) {
            state.switched = true;
            state.ct = 1 - state.ct;
        }
        let scores = state.scores;
        out.push(self.send_money());
        out.push(self.send_scores());
        self.events.push(RoomEvent::MatchState {
            state: format!("round over: team {winner_team} {}-{}", scores[0], scores[1]),
        });
        if scores.iter().any(|&s| s >= rules.rounds_to_win) {
            let winner = if scores[0] > scores[1] { 0 } else { 1 };
            for team in [winner, 1 - winner] {
                out.push(all(
                    EventCode::TeamDead,
                    TeamDead {
                        group: team,
                        placement: if team == winner { 1 } else { 2 },
                        finished: Vec::new(),
                    }
                    .encode(),
                ));
            }
            self.state = game_state::ENDED;
            self.ended_at = Some(now);
            let players = self
                .players
                .values()
                .filter(|p| p.group == winner)
                .map(|p| p.name.clone())
                .collect();
            self.events.push(RoomEvent::Won {
                team: winner,
                players,
            });
            out.push(Self::state_changed(GameStateChanged::Ended { winner }));
            self.bomb = None;
        } else {
            self.state = INTERMISSION_STATE;
            if let Some(state) = self.bomb.as_mut() {
                state.next_round_at = Some(now + INTERMISSION);
            }
        }
        out
    }

    /// The Bomb match loop (everything but the end-of-match restart).
    pub(super) fn tick_bomb(&mut self, now: f32, dt: f32) -> Vec<Outgoing> {
        let mut out = Vec::new();
        if let Some(state) = self.bomb.as_mut()
            && state.give_at.is_some_and(|t| now >= t)
        {
            state.give_at = None;
            out.extend(self.give_round_weapons());
        }
        match self.state {
            game_state::WAITING_FOR_PLAYERS => {
                let enough = self.players.len()
                    >= usize::from(self.settings.players_to_start.max(2))
                    && !self.team(0).is_empty()
                    && !self.team(1).is_empty();
                if enough {
                    let mut state = BombState {
                        ct: 1,
                        ..Default::default()
                    };
                    for &p in self.players.keys() {
                        state.money.insert(p, START_MONEY);
                    }
                    self.bomb = Some(state);
                    self.events.push(RoomEvent::MatchState {
                        state: "countdown".into(),
                    });
                    out.extend(self.start_round(now));
                }
            }
            game_state::COUNT_DOWN => {
                self.countdown_left -= dt;
                if self.countdown_left <= 0.0 {
                    self.state = game_state::STARTED;
                    let round_time = self.bomb_rules().round_time;
                    if let Some(s) = self.bomb.as_mut() {
                        s.round_started_at = now;
                    }
                    // The official server sends `GameSettings.RoundTime`, an int.
                    let mut started = vec![game_state::STARTED];
                    started.extend_from_slice(&(round_time.round() as i32).to_le_bytes());
                    out.push(raw_state(started));
                    self.events.push(RoomEvent::MatchState {
                        state: "started".into(),
                    });
                }
            }
            game_state::STARTED => {
                let rules = self.bomb_rules().clone();
                let state = self.bomb.as_ref().expect("bomb running");
                let (ct, t) = (state.ct, 1 - state.ct);
                let (planted_at, defuser, round_started_at) =
                    (state.planted_at, state.defuser, state.round_started_at);
                let alive = |g: u8| self.players.values().any(|p| p.group == g && p.alive);
                let result = if !alive(t) && planted_at.is_none() {
                    Some((ct, BY_ELIMINATION))
                } else if !alive(ct) {
                    Some((t, BY_ELIMINATION))
                } else if let Some(at) = planted_at {
                    if now - at >= rules.bomb_time {
                        Some((t, BY_EXPLOSION))
                    } else if let Some((defuser, since)) = defuser
                        && now - since >= rules.defuse_time
                    {
                        self.give_money(&[defuser], MONEY_FOR_DEFUSE);
                        Some((ct, BY_DEFUSE))
                    } else {
                        None
                    }
                } else if now - round_started_at >= rules.round_time {
                    Some((ct, BY_TIME))
                } else {
                    None
                };
                if let Some((winner, reason)) = result {
                    out.extend(self.round_over(now, winner, reason));
                }
            }
            // Survivors keep the gun they hold into the next round.
            INTERMISSION_STATE
                if self
                    .bomb
                    .as_ref()
                    .and_then(|s| s.next_round_at)
                    .is_some_and(|t| now >= t) =>
            {
                out.extend(self.start_round(now));
            }
            _ => {}
        }
        if self.players.is_empty() && self.state != game_state::ENDED {
            self.bomb = None;
            self.reset();
        }
        out
    }

    /// A Bomb kill: money for the killer; the victim loses their gun.
    pub(super) fn bomb_kill(&mut self, victim: u8, killer: u8) -> Vec<Outgoing> {
        if self.bomb.is_none() {
            return Vec::new();
        }
        if killer != victim && killer != NOBODY && self.players.contains_key(&killer) {
            self.give_money(&[killer], MONEY_FOR_KILL);
        }
        let state = self.bomb.as_mut().expect("checked");
        state.weapons.remove(&victim);
        if state.holder == Some(victim) {
            state.holder = None;
        }
        if state.defuser.is_some_and(|(d, _)| d == victim) {
            state.defuser = None;
        }
        vec![self.send_money()]
    }

    /// CTs can't pick the bomb up; a T who does carries it.
    pub(super) fn bomb_pickup_allowed(&mut self, peer: u8, item_id: i32) -> bool {
        let Some(state) = self.bomb.as_ref() else {
            return true;
        };
        if item_id != self.bomb_rules().bomb_item {
            return true;
        }
        let ct = state.ct;
        if self.players.get(&peer).is_some_and(|p| p.group == ct) {
            return false;
        }
        if let Some(s) = self.bomb.as_mut() {
            s.holder = Some(peer);
        }
        true
    }

    pub(super) fn bomb_dropped(&mut self, peer: u8, item_id: i32) {
        if let Some(state) = self.bomb.as_mut()
            && self
                .settings
                .bomb
                .as_ref()
                .is_some_and(|b| b.bomb_item == item_id)
            && state.holder == Some(peer)
        {
            state.holder = None;
        }
    }

    /// `RequestPurchaseGun` (buy phase only): `[f32 cost][weapon][ammo][ammo qty][n][attachments]`.
    pub(super) fn bomb_purchase(&mut self, peer: u8, payload: &[u8]) -> Vec<Outgoing> {
        if self.state != game_state::COUNT_DOWN || self.bomb.is_none() {
            return Vec::new();
        }
        let mut r = Reader::new(payload);
        let parsed = (|| {
            let cost = r.f32()?;
            let weapon = r.i32()?;
            let ammo = r.i32()?;
            let quantity = r.i32()?;
            let n = r.i32()?.clamp(0, 16);
            let mut attachments = Vec::new();
            for _ in 0..n {
                attachments.push(r.i32()?);
            }
            Ok::<_, tabg_protocol::DecodeError>((cost, weapon, ammo, quantity, attachments))
        })();
        let Ok((cost, weapon, ammo, quantity, attachments)) = parsed else {
            return Vec::new();
        };
        let cost_int = cost.max(0.0) as i32;
        let state = self.bomb.as_mut().expect("checked");
        let money = state.money.entry(peer).or_insert(START_MONEY);
        if !self.players.contains_key(&peer) || *money < cost_int || weapon < 0 {
            return Vec::new();
        }
        *money -= cost_int;
        state.weapons.insert(peer, weapon);
        vec![
            self.send_money(),
            Self::weapon_pack(peer, cost, weapon, ammo, quantity, &attachments),
        ]
    }

    /// `RequestPlantBomb` `[x][y][z]`: only the bomb carrier, once per round.
    pub(super) fn bomb_plant(&mut self, peer: u8, payload: &[u8]) -> Vec<Outgoing> {
        let now = self.last_now.unwrap_or(0.0);
        if self.state != game_state::STARTED {
            return Vec::new();
        }
        let Some(state) = self.bomb.as_ref() else {
            return Vec::new();
        };
        if state.holder != Some(peer) || state.planted_at.is_some() {
            return Vec::new();
        }
        let mut r = Reader::new(payload);
        let Ok(pos) = r.vec3() else {
            return Vec::new();
        };
        let t = 1 - state.ct;
        let bomb_time = self.bomb_rules().bomb_time;
        let state = self.bomb.as_mut().expect("checked");
        state.planted_at = Some(now);
        state.holder = None;
        let team = self.team(t);
        self.give_money(&team, MONEY_FOR_PLANT);
        let mut w = Writer::new();
        w.u8(peer).vec3(pos).f32(bomb_time);
        self.events.push(RoomEvent::MatchState {
            state: "bomb planted".into(),
        });
        vec![
            all(EventCode::BombPlanted, w.into_inner()),
            self.send_money(),
        ]
    }

    /// `RequestDefuseBomb`: a living CT starts defusing a planted bomb.
    pub(super) fn bomb_defuse(&mut self, peer: u8) -> Vec<Outgoing> {
        let now = self.last_now.unwrap_or(0.0);
        let Some(state) = self.bomb.as_ref() else {
            return Vec::new();
        };
        let ok = state.planted_at.is_some()
            && state.defuser.is_none()
            && self
                .players
                .get(&peer)
                .is_some_and(|p| p.alive && p.group == state.ct);
        if !ok {
            return Vec::new();
        }
        let time = self.bomb_rules().defuse_time;
        self.bomb.as_mut().expect("checked").defuser = Some((peer, now));
        let mut w = Writer::new();
        w.u8(peer).f32(time);
        vec![all(EventCode::BombStartDefuse, w.into_inner())]
    }

    /// `RequestStopDefuse`.
    pub(super) fn bomb_stop_defuse(&mut self, peer: u8) -> Vec<Outgoing> {
        let Some(state) = self.bomb.as_mut() else {
            return Vec::new();
        };
        if state.defuser.is_some_and(|(d, _)| d == peer) {
            state.defuser = None;
            return vec![all(EventCode::BombStopDefuse, vec![peer])];
        }
        Vec::new()
    }
}
