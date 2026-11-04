//! Who has played on this server (ServerLogger's successor): one record per player id, kept in
//! `players.json`. Small enough for a friends' server; backed up with the config.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

use crate::paths::{now, write_atomic};

#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct PlayerRecord {
    pub id: String,
    /// Last name first.
    pub names: Vec<String>,
    pub first_seen: u64,
    pub last_seen: u64,
    pub joins: u32,
    pub kills: u32,
    pub deaths: u32,
    pub wins: u32,
}

#[derive(Debug)]
pub struct PlayerStore {
    path: PathBuf,
    players: BTreeMap<String, PlayerRecord>,
    dirty: bool,
}

impl PlayerStore {
    pub fn open(path: &Path) -> Self {
        let players = std::fs::read(path)
            .ok()
            .and_then(|b| serde_json::from_slice::<Vec<PlayerRecord>>(&b).ok())
            .unwrap_or_default()
            .into_iter()
            .map(|p| (p.id.clone(), p))
            .collect();
        Self {
            path: path.to_owned(),
            players,
            dirty: false,
        }
    }

    pub fn save(&mut self) -> std::io::Result<()> {
        if !self.dirty {
            return Ok(());
        }
        let list: Vec<&PlayerRecord> = self.players.values().collect();
        write_atomic(
            &self.path,
            &serde_json::to_vec_pretty(&list).expect("records serialize"),
        )?;
        self.dirty = false;
        Ok(())
    }

    fn by_name_mut(&mut self, name: &str) -> Option<&mut PlayerRecord> {
        self.players
            .values_mut()
            .filter(|p| p.names.first().is_some_and(|n| n == name))
            .max_by_key(|p| p.last_seen)
    }

    /// Record a room event (from the running server).
    pub fn record(&mut self, event: &tabg_game::RoomEvent) {
        use tabg_game::RoomEvent as E;
        let t = now();
        match event {
            E::Joined { name, id, .. } => {
                let p = self
                    .players
                    .entry(id.clone())
                    .or_insert_with(|| PlayerRecord {
                        id: id.clone(),
                        first_seen: t,
                        ..Default::default()
                    });
                p.names.retain(|n| n != name);
                p.names.insert(0, name.clone());
                p.names.truncate(10);
                p.last_seen = t;
                p.joins += 1;
            }
            E::Left { name, .. } => {
                if let Some(p) = self.by_name_mut(name) {
                    p.last_seen = t;
                }
            }
            E::Killed { victim, killer, .. } => {
                if let Some(p) = self.by_name_mut(victim) {
                    p.deaths += 1;
                }
                if let Some(k) = killer.as_deref().and_then(|k| self.by_name_mut(k)) {
                    k.kills += 1;
                }
            }
            E::Won { players, .. } => {
                for name in players {
                    if let Some(p) = self.by_name_mut(name) {
                        p.wins += 1;
                    }
                }
            }
            _ => return,
        }
        self.dirty = true;
    }

    /// Most recently seen first.
    pub fn list(&self) -> Vec<PlayerRecord> {
        let mut v: Vec<PlayerRecord> = self.players.values().cloned().collect();
        v.sort_by_key(|p| std::cmp::Reverse(p.last_seen));
        v
    }

    pub fn get(&self, id: &str) -> Option<&PlayerRecord> {
        self.players.get(id)
    }

    /// Find a player id by id or (latest) name, for bans and admin grants by name.
    pub fn resolve(&self, query: &str) -> Option<String> {
        if self.players.contains_key(query) {
            return Some(query.to_owned());
        }
        self.players
            .values()
            .filter(|p| p.names.iter().any(|n| n.eq_ignore_ascii_case(query)))
            .max_by_key(|p| p.last_seen)
            .map(|p| p.id.clone())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tabg_game::RoomEvent;

    #[test]
    fn records_history_and_persists() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("players.json");
        let mut s = PlayerStore::open(&path);
        s.record(&RoomEvent::Joined {
            index: 0,
            name: "alice".into(),
            id: "epic:1".into(),
        });
        s.record(&RoomEvent::Joined {
            index: 1,
            name: "bob".into(),
            id: "epic:2".into(),
        });
        s.record(&RoomEvent::Killed {
            victim: "bob".into(),
            killer: Some("alice".into()),
            ring: false,
        });
        s.record(&RoomEvent::Won {
            team: 0,
            players: vec!["alice".into()],
        });
        s.save().unwrap();
        let s = PlayerStore::open(&path);
        let alice = s.get("epic:1").unwrap();
        assert_eq!((alice.kills, alice.wins, alice.joins), (1, 1, 1));
        assert_eq!(s.get("epic:2").unwrap().deaths, 1);
        assert_eq!(s.resolve("ALICE").as_deref(), Some("epic:1"));
        assert_eq!(s.resolve("epic:2").as_deref(), Some("epic:2"));
        assert_eq!(s.resolve("carol"), None);
    }
}
