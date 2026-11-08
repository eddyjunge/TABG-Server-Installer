//! The window's tabs.

pub mod console;
pub mod game_mode;
pub mod loadout;
pub mod mods;
pub mod overview;
pub mod players;
pub mod rules;
pub mod server;

use std::collections::BTreeSet;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Tab {
    Overview,
    Players,
    GameMode,
    Rules,
    Loadout,
    Mods,
    Console,
    Server,
}

impl Tab {
    pub const ALL: [Self; 8] = [
        Self::Overview,
        Self::Players,
        Self::GameMode,
        Self::Rules,
        Self::Loadout,
        Self::Mods,
        Self::Console,
        Self::Server,
    ];

    pub fn title(self) -> &'static str {
        match self {
            Self::Overview => "Overview",
            Self::Players => "Players",
            Self::GameMode => "Game mode",
            Self::Rules => "Rules",
            Self::Loadout => "Loadout and ring",
            Self::Mods => "Mods",
            Self::Console => "Console",
            Self::Server => "Server",
        }
    }
}

/// Which log lines the Console tab shows.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
pub enum LogFilter {
    #[default]
    All,
    Game,
    Chat,
    Actions,
    Commands,
    Technical,
}

#[derive(Default)]
pub struct ShareState {
    pub code: Option<String>,
    /// The mods and join code the code was made for.
    pub mods: Vec<String>,
    pub join: Option<String>,
    pub busy: bool,
    pub error: Option<String>,
}

/// What each tab remembers between frames.
#[derive(Default)]
pub struct TabState {
    pub collapsed: BTreeSet<String>,
    pub password_focused: bool,
    pub wizard_left: bool,
    // Overview.
    pub message: String,
    // Players.
    pub everyone: bool,
    pub filter: String,
    pub selected: Option<String>,
    pub give_query: String,
    pub give_item: Option<i32>,
    pub give_qty: u16,
    pub add_admin: String,
    pub add_ban: String,
    // Game mode.
    pub preset: String,
    pub preset_name: String,
    // Loadout and ring.
    pub ring_stage: usize,
    pub loadout_query: String,
    pub level_query: String,
    // Mods.
    pub share: ShareState,
    // Console.
    pub log_filter: LogFilter,
    pub log_search: String,
    pub line: String,
    pub sent: Vec<String>,
    pub sent_pos: Option<usize>,
    pub suggestion: usize,
}
