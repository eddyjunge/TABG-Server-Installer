//! Loot in the world: spawning it at match start the way the official server does, and keeping it
//! indexed by chunk so each player only hears about loot near them.

use std::collections::{BTreeMap, HashMap};

use tabg_protocol::loot::{LootItem, chunk_of};

use crate::rng::Rng;

/// One weighted entry of a preset: these `(item id, quantity)` pairs spawn together.
#[derive(Clone, Debug, PartialEq)]
pub struct Drop {
    pub weight: f32,
    pub items: Vec<(i32, i32)>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct Modifier {
    pub index: u8,
    /// 0 common, 1 rare, 2 epic, 3 legendary.
    pub rarity: u8,
    pub drops: Vec<Drop>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct Spot {
    pub position: [f32; 3],
    pub force: bool,
}

/// The map's loot spots and match modifiers (from the world dump), plus the strip setting.
#[derive(Clone, Debug, PartialEq, Default)]
pub struct LootRules {
    pub spots: Vec<Spot>,
    pub modifiers: Vec<Modifier>,
}

impl LootRules {
    /// Official modifier weights: common 10, rare 0.05, epic 0.01, legendary 0.001.
    fn rarity_weight(rarity: u8) -> f32 {
        match rarity {
            0 => 10.0,
            1 => 0.05,
            2 => 0.01,
            _ => 0.001,
        }
    }

    pub fn pick_modifier(&self, rng: &mut Rng) -> Option<&Modifier> {
        let total: f32 = self
            .modifiers
            .iter()
            .map(|m| Self::rarity_weight(m.rarity))
            .sum();
        let mut roll = rng.range(0.0, total);
        for m in &self.modifiers {
            roll -= Self::rarity_weight(m.rarity);
            if roll <= 0.0 {
                return Some(m);
            }
        }
        self.modifiers.last()
    }
}

fn pick_drop<'a>(drops: &'a [Drop], rng: &mut Rng) -> Option<&'a Drop> {
    let total: f32 = drops.iter().map(|d| d.weight.max(0.0)).sum();
    let roll = rng.range(0.0, total);
    let mut acc = 0.0;
    for d in drops {
        if acc <= roll && acc + d.weight > roll {
            return Some(d);
        }
        acc += d.weight.max(0.0);
    }
    drops.last()
}

/// All loot currently lying in the world.
#[derive(Clone, Debug, Default)]
pub struct LootWorld {
    items: BTreeMap<i32, LootItem>,
    by_chunk: HashMap<(i8, i8), Vec<i32>>,
    next_id: i32,
}

impl LootWorld {
    /// Match start: pick a modifier, keep each spot with probability `keep` (unless forced), and
    /// spawn one weighted drop from the modifier's preset at it, items spread 0.7 m apart.
    /// Returns the modifier index for the plane announcement.
    pub fn spawn(rules: &LootRules, keep: f32, rng: &mut Rng) -> (u8, Self) {
        let mut world = Self::default();
        let Some(modifier) = rules.pick_modifier(rng) else {
            return (0, world);
        };
        for spot in &rules.spots {
            if !spot.force && rng.unit() > keep {
                continue;
            }
            let Some(drop) = pick_drop(&modifier.drops, rng) else {
                continue;
            };
            let yaw = rng.range(0.0, std::f32::consts::TAU);
            for (i, &(item_id, quantity)) in drop.items.iter().enumerate() {
                let d = i as f32 * 0.7;
                let p = [
                    spot.position[0] + yaw.cos() * d,
                    spot.position[1],
                    spot.position[2] - yaw.sin() * d,
                ];
                world.add(item_id, quantity, p);
            }
        }
        (modifier.index, world)
    }

    pub fn len(&self) -> usize {
        self.items.len()
    }

    pub fn is_empty(&self) -> bool {
        self.items.is_empty()
    }

    pub fn add(&mut self, item_id: i32, quantity: i32, position: [f32; 3]) -> LootItem {
        let item = LootItem {
            network_id: self.next_id,
            item_id,
            quantity,
            position,
        };
        self.next_id += 1;
        self.by_chunk
            .entry(chunk_of(position))
            .or_default()
            .push(item.network_id);
        self.items.insert(item.network_id, item.clone());
        item
    }

    pub fn get(&self, network_id: i32) -> Option<&LootItem> {
        self.items.get(&network_id)
    }

    /// Remove an item (picked up). Returns it if it was there.
    pub fn take(&mut self, network_id: i32) -> Option<LootItem> {
        let item = self.items.remove(&network_id)?;
        if let Some(ids) = self.by_chunk.get_mut(&chunk_of(item.position)) {
            ids.retain(|&i| i != network_id);
        }
        Some(item)
    }

    pub fn in_chunks(&self, chunks: &[(i8, i8)]) -> Vec<LootItem> {
        chunks
            .iter()
            .filter_map(|c| self.by_chunk.get(c))
            .flatten()
            .filter_map(|id| self.items.get(id).cloned())
            .collect()
    }

    pub fn ids_in_chunks(&self, chunks: &[(i8, i8)]) -> Vec<i32> {
        chunks
            .iter()
            .filter_map(|c| self.by_chunk.get(c))
            .flatten()
            .copied()
            .collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn rules() -> LootRules {
        LootRules {
            spots: (0..1000)
                .map(|i| Spot {
                    position: [i as f32 * 10.0, 0.0, 0.0],
                    force: i == 0,
                })
                .collect(),
            modifiers: vec![
                Modifier {
                    index: 0,
                    rarity: 0,
                    drops: vec![Drop {
                        weight: 1.0,
                        items: vec![(6, 30), (1, 20)],
                    }],
                },
                Modifier {
                    index: 3,
                    rarity: 3,
                    drops: vec![Drop {
                        weight: 1.0,
                        items: vec![(99, 1)],
                    }],
                },
            ],
        }
    }

    #[test]
    fn spawns_about_the_kept_fraction_with_whole_drops() {
        let (modifier, world) = LootWorld::spawn(&rules(), 0.1, &mut Rng::new(5));
        assert_eq!(modifier, 0, "common modifiers dominate");
        let spots = world.len() / 2;
        assert!(
            (60..=140).contains(&spots),
            "~10% of 1000 spots, got {spots}"
        );
        assert!(world.get(0).is_some(), "the forced spot always spawns");
        assert_eq!(world.get(1).unwrap().item_id, 1);
    }

    #[test]
    fn chunk_index_follows_adds_and_takes() {
        let mut world = LootWorld::default();
        let a = world.add(6, 30, [10.0, 0.0, 10.0]);
        let b = world.add(6, 30, [300.0, 0.0, 10.0]);
        assert_eq!(world.in_chunks(&[(0, 0)]), vec![a.clone()]);
        assert_eq!(world.ids_in_chunks(&[(2, 0)]), vec![b.network_id]);
        assert_eq!(world.take(a.network_id), Some(a.clone()));
        assert!(world.take(a.network_id).is_none());
        assert!(world.in_chunks(&[(0, 0)]).is_empty());
    }
}
