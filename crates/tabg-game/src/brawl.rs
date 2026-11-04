//! Brawl rules: teams start in spawn houses, the doors open when the countdown ends, every house
//! gets the same loot (one preset per round, like the official `BrawlLootSelector`), a wiped
//! team respawns at its house, and the first team to `kills_to_win` wins.

use crate::loot::{Drop, LootWorld, pick_drop};
use crate::rng::Rng;
use crate::settings::SpawnPoint;

/// Loot spot tiers, in the order presets list them.
pub const HIGH: usize = 0;
pub const MED: usize = 1;
pub const LOW: usize = 2;
pub const ATTACH: usize = 3;

pub fn tier(name: &str) -> Option<usize> {
    match name {
        "High" => Some(HIGH),
        "Med" => Some(MED),
        "Low" => Some(LOW),
        "Attach" => Some(ATTACH),
        _ => None,
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct House {
    pub spawns: Vec<SpawnPoint>,
    /// `(tier, position)`; spot k is the same kind of spot in every house.
    pub loot: Vec<(usize, [f32; 3])>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct Preset {
    pub tiers: [Vec<Drop>; 4],
    pub redo_chance: f32,
}

#[derive(Clone, Debug, PartialEq, Default)]
pub struct BrawlRules {
    /// Team (duo/squad) houses.
    pub houses: Vec<House>,
    /// Solo houses (smaller, twice as many).
    pub solo_houses: Vec<House>,
    pub presets: Vec<Preset>,
}

impl BrawlRules {
    /// The houses for this match mode (solo has its own layout).
    pub fn layout(&self, solo: bool) -> &[House] {
        if solo && !self.solo_houses.is_empty() {
            &self.solo_houses
        } else {
            &self.houses
        }
    }

    /// The house a team plays from.
    pub fn house(&self, solo: bool, group: u8) -> Option<&House> {
        let houses = self.layout(solo);
        if houses.is_empty() {
            return None;
        }
        houses.get(usize::from(group) % houses.len())
    }

    /// This round's loot: one preset, one decision per spot, copied into every house.
    pub fn spawn_loot(&self, solo: bool, rng: &mut Rng) -> LootWorld {
        let mut world = LootWorld::default();
        let houses = self.layout(solo);
        if self.presets.is_empty() || houses.is_empty() {
            return world;
        }
        let mut preset = &self.presets[rng.index(self.presets.len())];
        for _ in 0..16 {
            if preset.redo_chance <= rng.unit() {
                break;
            }
            preset = &self.presets[rng.index(self.presets.len())];
        }
        let layout = &houses[0].loot;
        let decided: Vec<Option<Vec<(i32, i32)>>> = layout
            .iter()
            .map(|&(t, _)| pick_drop(&preset.tiers[t], rng).map(|d| d.items.clone()))
            .collect();
        for house in houses {
            for (k, &(_, position)) in house.loot.iter().enumerate() {
                for (item, quantity) in decided.get(k).cloned().flatten().unwrap_or_default() {
                    world.add(item, quantity, position);
                }
            }
        }
        world
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_house_gets_the_same_loot() {
        let house = |x: f32| House {
            spawns: vec![SpawnPoint {
                position: [x, 0.0, 0.0],
                rotation: 0.0,
            }],
            loot: vec![(HIGH, [x, 1.0, 0.0]), (LOW, [x, 2.0, 0.0])],
        };
        let rules = BrawlRules {
            houses: vec![house(0.0), house(100.0)],
            solo_houses: Vec::new(),
            presets: vec![Preset {
                tiers: [
                    vec![Drop {
                        weight: 1.0,
                        items: vec![(152, 1), (6, 90)],
                    }],
                    Vec::new(),
                    vec![Drop {
                        weight: 1.0,
                        items: vec![(131, 2)],
                    }],
                    Vec::new(),
                ],
                redo_chance: 0.0,
            }],
        };
        let world = rules.spawn_loot(false, &mut Rng::new(1));
        assert_eq!(world.len(), 6, "3 items in each of 2 houses");
        assert_eq!(
            rules.house(false, 3).unwrap().spawns[0].position[0],
            100.0,
            "groups wrap around"
        );
        assert_eq!(rules.layout(true).len(), 2, "no solo layout: team houses");
    }
}
