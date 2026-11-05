//! What each player carries, as the official server tracks it: pickups and handouts add, drops,
//! throws, healing and firing (ammo) remove. When a player dies their loot falls to the ground
//! (`PlayerDeathLootDrop`) together with a soul (`SoulDrop`). Souls cleanse curses (3) and bring
//! a dead teammate back (1, with a random curse). Blessings are chosen by the client and
//! broadcast.

use tabg_protocol::loot::{LootItem, chunk_of};
use tabg_protocol::{EventCode, Packet, Reader, Writer};

use super::{NOBODY, Outgoing, Recipients, Room};

/// Souls a curse cleanse costs (official `PlayerRequestCleanseCurseCommand`).
const CLEANSE_COST: i32 = 3;
/// A respawned teammate drops in from this high above the reviver.
const REVIVE_HEIGHT: f32 = 20.0;

impl Room {
    pub(super) fn inventory_add(&mut self, peer: u8, item: i32, quantity: i32) {
        if item < 0 || quantity <= 0 {
            return;
        }
        if let Some(p) = self.players.get_mut(&peer) {
            *p.inventory.entry(item).or_default() += quantity;
        }
    }

    pub(super) fn inventory_remove(&mut self, peer: u8, item: i32, quantity: i32) {
        if let Some(p) = self.players.get_mut(&peer)
            && let Some(have) = p.inventory.get_mut(&item)
        {
            *have -= quantity.max(0);
            if *have <= 0 {
                p.inventory.remove(&item);
            }
        }
    }

    pub(super) fn inventory_add_all(&mut self, peer: u8, items: &[(i32, i32)]) {
        for &(item, quantity) in items {
            self.inventory_add(peer, item, quantity);
        }
    }

    /// `game.inventory_check` for a drop, throw or heal of `wanted` × `item`: the quantity to go
    /// ahead with, or `None` to refuse. A shortfall is reported once per player and item. Ammo,
    /// bots, Bomb (bought guns aren't tracked) and a missing player are never checked.
    pub(super) fn inventory_checked(
        &mut self,
        peer: u8,
        action: &str,
        item: i32,
        wanted: i32,
    ) -> Option<i32> {
        use crate::settings::InventoryCheck;
        let mode = self.settings.inventory_check;
        if mode == InventoryCheck::Off
            || self.settings.ammo.contains(&item)
            || self.is_bot(peer)
            || self.is_bomb()
        {
            return Some(wanted);
        }
        let carried = self.carried(peer, item);
        if carried >= wanted {
            return Some(wanted);
        }
        let enforce = mode == InventoryCheck::Enforce;
        let p = self.players.get_mut(&peer)?;
        if p.inventory_reported.insert(item) {
            let name = p.name.clone();
            self.events.push(super::RoomEvent::Inventory {
                name,
                action: action.to_owned(),
                item,
                wanted,
                carried,
                refused: enforce && carried <= 0,
            });
        }
        match (enforce, carried) {
            (false, _) => Some(wanted),
            (true, c) if c > 0 => Some(c),
            (true, _) => None,
        }
    }

    /// How many of `item` a player has.
    pub fn carried(&self, peer: u8, item: i32) -> i32 {
        self.players
            .get(&peer)
            .and_then(|p| p.inventory.get(&item).copied())
            .unwrap_or(0)
    }

    /// A dead (or leaving) player's loot and soul fall where they stood.
    pub(super) fn drop_inventory(&mut self, peer: u8) -> Vec<Outgoing> {
        let Some(p) = self.players.get_mut(&peer) else {
            return Vec::new();
        };
        let position = p.position;
        let items: Vec<(i32, i32)> = std::mem::take(&mut p.inventory).into_iter().collect();
        let watchers = {
            let mut w = self.watchers(chunk_of(position));
            w.retain(|&i| i != peer);
            w
        };
        let mut out = Vec::new();
        if !items.is_empty() {
            let dropped: Vec<LootItem> = items
                .iter()
                .map(|&(item, quantity)| self.loot.add(item, quantity, position))
                .collect();
            // [u16 count][position]([i32 network id][i32 item][i32 quantity])*
            let mut w = Writer::new();
            w.u16(u16::try_from(dropped.len()).unwrap_or(u16::MAX))
                .vec3(position);
            for d in &dropped {
                w.i32(d.network_id).i32(d.item_id).i32(d.quantity);
            }
            if !watchers.is_empty() {
                out.push(Outgoing::reliable(
                    Recipients::Many(watchers.clone()),
                    EventCode::PlayerDeathLootDrop,
                    w.into_inner(),
                ));
            }
        }
        if let Some(soul) = self.settings.soul_item {
            let item = self.loot.add(soul, 1, position);
            self.souls.insert(item.network_id, peer);
            // [player][position][i32 network id]
            let mut w = Writer::new();
            w.u8(peer).vec3(position).i32(item.network_id);
            if !watchers.is_empty() {
                out.push(Outgoing::reliable(
                    Recipients::Many(watchers),
                    EventCode::SoulDrop,
                    w.into_inner(),
                ));
            }
        }
        out
    }

    /// `RequestBlessing` `[u8][i32 ×3]`: the player's three blessings, told to everyone.
    pub(super) fn blessing(&mut self, peer: u8, payload: &[u8]) -> Vec<Outgoing> {
        let mut r = Reader::new(payload);
        let parsed =
            (|| Ok::<_, tabg_protocol::DecodeError>((r.u8()?, r.i32()?, r.i32()?, r.i32()?)))();
        let Ok((_, a, b, c)) = parsed else {
            return Vec::new();
        };
        if !self.players.contains_key(&peer) {
            return Vec::new();
        }
        let mut w = Writer::new();
        w.u8(peer).i32(a).i32(b).i32(c);
        vec![Self::broadcast(EventCode::BlessingRecieved, w.into_inner())]
    }

    /// `RequestCurseCleanse` `[u8][curse]`: costs three souls.
    pub(super) fn cleanse_curse(&mut self, peer: u8, payload: &[u8]) -> Vec<Outgoing> {
        let (Some(&curse), Some(soul)) = (payload.get(1), self.settings.soul_item) else {
            return Vec::new();
        };
        if !self.players.contains_key(&peer) {
            return Vec::new();
        }
        self.inventory_remove(peer, soul, CLEANSE_COST);
        vec![Self::broadcast(EventCode::CurseCleansed, vec![peer, curse])]
    }

    /// `RequestRespawnTeamMate` `[i32 soul network id][u8 player]`: a soul brings its dead owner
    /// back above the reviver, with a random curse.
    pub(super) fn respawn_teammate(&mut self, peer: u8, payload: &[u8]) -> Vec<Outgoing> {
        let mut r = Reader::new(payload);
        let parsed = (|| Ok::<_, tabg_protocol::DecodeError>((r.i32()?, r.u8()?)))();
        let Ok((soul_id, named)) = parsed else {
            return Vec::new();
        };
        let target = self.souls.remove(&soul_id).unwrap_or(named);
        let Some(reviver) = self.players.get(&peer).filter(|p| p.alive) else {
            return Vec::new();
        };
        let (group, base) = (reviver.group, reviver.position);
        let ok = self
            .players
            .get(&target)
            .is_some_and(|t| !t.alive && t.group == group);
        let Some(soul) = self.settings.soul_item.filter(|_| ok) else {
            return Vec::new();
        };
        self.inventory_remove(peer, soul, 1);
        let curse = if self.settings.curses.is_empty() {
            u8::MAX
        } else {
            self.settings.curses[self.rng.index(self.settings.curses.len())]
        };
        let position = [base[0], base[1] + REVIVE_HEIGHT, base[2]];
        let p = self.players.get_mut(&target).expect("checked");
        p.alive = true;
        p.downed = false;
        p.health = 100.0;
        p.respawn_at = None;
        p.dropped = true;
        p.landed = true;
        p.position = position;
        p.last_attacker = NOBODY;
        let mut w = Writer::new();
        w.u8(1).u8(target).u8(target).f32(100.0);
        w.vec3(position).f32(0.0).u8(curse);
        let mut out = vec![Self::broadcast(EventCode::PlayerRespawn, w.into_inner())];
        out.extend(self.chunk_changed(target, chunk_of(position)));
        out
    }

    /// A handout straight into the inventory (`PlayerLootRecieved`), tracked.
    pub(super) fn grant(&mut self, peer: u8, items: &[(i32, i32)]) -> Outgoing {
        self.inventory_add_all(peer, items);
        Outgoing {
            to: Recipients::One(peer),
            packet: Packet::new(
                EventCode::PlayerLootRecieved,
                tabg_protocol::actions::encode_player_loot(items),
            ),
            reliable: true,
        }
    }
}
