//! Admin commands, shared by in-game chat (`/give …` from an admin) and the host's console, plus
//! the room's report to the host: events and a player list.

use serde::{Deserialize, Serialize};
use tabg_protocol::EventCode;
use tabg_protocol::actions::{ChatMessage, encode_player_health};
use tabg_protocol::loot::encode_item_drop;
use tabg_protocol::messages::game_state;

use super::{Outgoing, Recipients, Room};
use crate::settings::RoomSettings;

/// Something the host shows in its console or acts on (e.g. persisting a ban).
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum RoomEvent {
    Joined {
        index: u8,
        name: String,
        id: String,
    },
    Refused {
        name: String,
        id: String,
        reason: String,
    },
    Left {
        index: u8,
        name: String,
    },
    Chat {
        index: u8,
        name: String,
        text: String,
    },
    Killed {
        victim: String,
        killer: Option<String>,
        ring: bool,
    },
    /// `lobby`, `countdown` or `started`.
    MatchState {
        state: String,
    },
    Won {
        team: u8,
        players: Vec<String>,
    },
    /// Someone was banned from the console or chat; the host should remember it.
    Banned {
        name: String,
        id: String,
    },
}

/// One row of the host's player list.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct PlayerInfo {
    pub index: u8,
    pub name: String,
    pub id: String,
    pub team: u8,
    pub admin: bool,
    pub alive: bool,
    pub health: f32,
    pub kills: u16,
    pub position: [f32; 3],
}

/// Who ran a command.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Source {
    Player(u8),
    Console,
}

const HELP: &str = "commands: start, say <text>, players, kick <player>, ban <player>, \
                    give [player] <item> [qty], heal [player], vehicles, \
                    vehicle <name> (in game), fpv (in game)";

impl Room {
    /// What happened since the last call.
    pub fn take_events(&mut self) -> Vec<RoomEvent> {
        std::mem::take(&mut self.events)
    }

    pub fn players(&self) -> Vec<PlayerInfo> {
        self.players
            .values()
            .map(|p| PlayerInfo {
                index: p.index,
                name: p.name.clone(),
                id: p.id.clone(),
                team: p.group,
                admin: p.is_admin,
                alive: p.alive,
                health: p.health,
                kills: p.kills,
                position: p.position,
            })
            .collect()
    }

    /// Match phase for status displays.
    pub fn phase(&self) -> &'static str {
        match self.state {
            game_state::WAITING_FOR_PLAYERS => "lobby",
            game_state::COUNT_DOWN => "countdown",
            game_state::STARTED => "match",
            game_state::ENDED => "ended",
            _ => "unknown",
        }
    }

    /// Replace the settings of a running room. Lobby rules (password, admins, bans, start
    /// conditions, loadout) take effect at once; ring, loot and cars from the next match.
    pub fn apply_settings(&mut self, settings: RoomSettings) {
        self.settings = settings;
        let admins: Vec<(u8, bool)> = self
            .players
            .values()
            .map(|p| (p.index, self.is_admin_name(&p.name, &p.id)))
            .collect();
        for (index, admin) in admins {
            if let Some(p) = self.players.get_mut(&index) {
                p.is_admin = admin;
            }
        }
    }

    pub(super) fn is_admin_name(&self, name: &str, id: &str) -> bool {
        self.settings
            .admins
            .iter()
            .any(|a| a == "*" || a.eq_ignore_ascii_case(name) || a == id)
    }

    pub(super) fn is_banned(&self, name: &str, id: &str) -> bool {
        self.settings
            .banned
            .iter()
            .any(|b| b.eq_ignore_ascii_case(name) || b == id)
    }

    /// A line typed into the host's console (no leading `/` needed). Returns the packets to send
    /// and the reply to show.
    pub fn console(&mut self, line: &str) -> (Vec<Outgoing>, String) {
        let line = line.trim().trim_start_matches('/');
        self.command(Source::Console, line)
    }

    /// An admin's `/command` in chat; the reply goes back to them as a chat line.
    pub(super) fn player_command(&mut self, peer: u8, line: &str) -> Vec<Outgoing> {
        let (mut out, reply) = self.command(Source::Player(peer), line);
        out.push(Self::chat_line(peer, &reply));
        out
    }

    /// A chat line shown to `peer` (as said by themselves: TABG has no system chat).
    pub(super) fn chat_line(peer: u8, text: &str) -> Outgoing {
        Outgoing::reliable(
            Recipients::One(peer),
            EventCode::ChatMessage,
            ChatMessage {
                player_index: peer,
                text: format!("[tabg] {text}"),
            }
            .encode(),
        )
    }

    /// `name`, `#index` or a bare index.
    fn find_player(&self, query: &str) -> Option<u8> {
        let q = query.trim();
        if let Some(i) = q
            .strip_prefix('#')
            .unwrap_or(q)
            .parse::<u8>()
            .ok()
            .filter(|i| self.players.contains_key(i))
        {
            return Some(i);
        }
        self.players
            .values()
            .find(|p| p.name.eq_ignore_ascii_case(q) || p.id == q)
            .map(|p| p.index)
    }

    fn command(&mut self, source: Source, line: &str) -> (Vec<Outgoing>, String) {
        let mut words = line.split_whitespace();
        let verb = words.next().unwrap_or("").to_ascii_lowercase();
        let rest: Vec<&str> = words.collect();
        match verb.as_str() {
            "" | "help" => (Vec::new(), HELP.into()),
            "start" => {
                let out = self.force_start();
                let reply = if out.is_empty() {
                    "can only start from the lobby"
                } else {
                    "starting"
                };
                (out, reply.into())
            }
            "say" => {
                let text = rest.join(" ");
                if text.is_empty() {
                    return (Vec::new(), "usage: say <text>".into());
                }
                let out = self
                    .players
                    .keys()
                    .map(|&p| Self::chat_line(p, &text))
                    .collect();
                (out, format!("said: {text}"))
            }
            "players" | "list" => {
                let list: Vec<String> = self
                    .players
                    .values()
                    .map(|p| {
                        format!(
                            "#{} {}{}",
                            p.index,
                            p.name,
                            if p.is_admin { " (admin)" } else { "" }
                        )
                    })
                    .collect();
                let reply = if list.is_empty() {
                    "nobody is here".into()
                } else {
                    list.join(", ")
                };
                (Vec::new(), reply)
            }
            "kick" | "ban" => {
                let query = rest.join(" ");
                let Some(target) = self.find_player(&query) else {
                    return (Vec::new(), format!("no player {query:?}"));
                };
                let (name, id) = {
                    let p = &self.players[&target];
                    (p.name.clone(), p.id.clone())
                };
                if self.is_bot(target) {
                    self.forget_bot(target);
                    return (self.disconnect(target), format!("removed {name}"));
                }
                self.to_disconnect.push(target);
                if verb == "ban" {
                    if !self.settings.banned.contains(&id) {
                        self.settings.banned.push(id.clone());
                    }
                    self.events.push(RoomEvent::Banned {
                        name: name.clone(),
                        id,
                    });
                    (Vec::new(), format!("banned {name}"))
                } else {
                    (Vec::new(), format!("kicked {name}"))
                }
            }
            "give" => {
                // Chat: give <item> [qty]. Console: give <player> <item> [qty].
                let (target, args) = match source {
                    Source::Player(p) => match rest.first().and_then(|w| w.parse::<i32>().ok()) {
                        Some(_) => (Some(p), &rest[..]),
                        None => (
                            rest.first().and_then(|w| self.find_player(w)),
                            rest.get(1..).unwrap_or(&[]),
                        ),
                    },
                    Source::Console => (
                        rest.first().and_then(|w| self.find_player(w)),
                        rest.get(1..).unwrap_or(&[]),
                    ),
                };
                let item = args.first().and_then(|w| w.parse::<i32>().ok());
                let qty = args
                    .get(1)
                    .and_then(|w| w.parse::<i32>().ok())
                    .unwrap_or(1)
                    .clamp(1, 999);
                let (Some(target), Some(item)) = (target, item) else {
                    return (
                        Vec::new(),
                        "usage: give [player] <item id> [quantity]".into(),
                    );
                };
                let at = self.players[&target].position;
                let loot = self.loot.add(item, qty, at);
                let mut to = self.near(target);
                if !to.contains(&target) {
                    to.push(target);
                }
                let name = self.players[&target].name.clone();
                (
                    vec![Outgoing::reliable(
                        Recipients::Many(to),
                        EventCode::ItemDrop,
                        encode_item_drop(&loot),
                    )],
                    format!("gave {qty} × item {item} to {name}"),
                )
            }
            "heal" => {
                let target = match (source, rest.is_empty()) {
                    (Source::Player(p), true) => Some(p),
                    _ => self.find_player(&rest.join(" ")),
                };
                let Some(target) = target else {
                    return (Vec::new(), "usage: heal [player]".into());
                };
                let p = self.players.get_mut(&target).expect("found above");
                p.health = 100.0;
                let name = p.name.clone();
                (
                    vec![Outgoing::reliable(
                        Recipients::Many(self.near(target)),
                        EventCode::PlayerHealthStateChanged,
                        encode_player_health(target, 100.0),
                    )],
                    format!("healed {name}"),
                )
            }
            "vehicles" => (Vec::new(), self.vehicles_list()),
            "vehicle" => match source {
                Source::Player(p) => self.vehicle_command(p, &rest.join(" ")),
                Source::Console => (
                    Vec::new(),
                    "vehicle spawns need a player: use it in game".into(),
                ),
            },
            "fpv" => match source {
                Source::Player(p) => self.give_drone(p),
                Source::Console => (Vec::new(), "fpv needs a player: use it in game".into()),
            },
            other => (Vec::new(), format!("unknown command {other:?}; {HELP}")),
        }
    }
}
