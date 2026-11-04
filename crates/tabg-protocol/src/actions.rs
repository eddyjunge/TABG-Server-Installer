//! Smaller player actions: chat, the parrot (thrown chat), healing, throwing items, catchphrases,
//! spectating, projectile sync and admin commands.

use crate::codec::{DecodeError, Reader, Writer};

/// Text as TABG sends it in chat: a one-byte byte length and UTF-16LE.
pub fn write_utf16(w: &mut Writer, text: &str) {
    let bytes: Vec<u8> = text
        .encode_utf16()
        .take(127)
        .flat_map(u16::to_le_bytes)
        .collect();
    w.u8(bytes.len() as u8).bytes(&bytes);
}

pub fn read_utf16(r: &mut Reader<'_>) -> Result<String, DecodeError> {
    let len = usize::from(r.u8()?);
    let raw = r.bytes(len)?;
    let units: Vec<u16> = raw
        .chunks_exact(2)
        .map(|c| u16::from_le_bytes([c[0], c[1]]))
        .collect();
    Ok(String::from_utf16_lossy(&units))
}

/// `ChatMessage` (both directions): `[player, text]`. Relayed unchanged to players in the chunk.
#[derive(Clone, Debug, PartialEq)]
pub struct ChatMessage {
    pub player_index: u8,
    pub text: String,
}

impl ChatMessage {
    pub fn encode(&self) -> Vec<u8> {
        let mut w = Writer::new();
        w.u8(self.player_index);
        write_utf16(&mut w, &self.text);
        w.into_inner()
    }

    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        Ok(Self {
            player_index: r.u8()?,
            text: read_utf16(&mut r)?,
        })
    }
}

/// `ThrowChatMessage` (server → watchers): the parrot says something where the player stands.
/// `rest` is the client payload after its first byte (the text), passed through unchanged.
pub fn encode_throw_chat(
    player: u8,
    position: [f32; 3],
    rotation: [f32; 2],
    rest: &[u8],
) -> Vec<u8> {
    let mut w = Writer::new();
    w.u8(player)
        .vec3(position)
        .f32(rotation[0])
        .f32(rotation[1])
        .bytes(rest);
    w.into_inner()
}

/// `RequestHealing` (client → server): use a healing item.
#[derive(Clone, Debug, PartialEq)]
pub struct HealRequest {
    pub player_index: u8,
    pub item_id: i32,
}

impl HealRequest {
    pub fn encode(&self) -> Vec<u8> {
        let mut w = Writer::new();
        w.u8(self.player_index).i32(self.item_id);
        w.into_inner()
    }

    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        Ok(Self {
            player_index: r.u8()?,
            item_id: r.i32()?,
        })
    }
}

/// `RequestHealthState` (client → server): the client's new (higher) health, e.g. from a
/// healing grenade.
#[derive(Clone, Debug, PartialEq)]
pub struct HealthStateRequest {
    pub player_index: u8,
    pub health: f32,
}

impl HealthStateRequest {
    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        Ok(Self {
            player_index: r.u8()?,
            health: r.f32()?,
        })
    }
}

/// `PlayerHealed` / `PlayerHealthStateChanged` (server → watchers and team): `[player, health]`.
pub fn encode_player_health(player: u8, health: f32) -> Vec<u8> {
    let mut w = Writer::new();
    w.u8(player).f32(health);
    w.into_inner()
}

/// `RequestItemThrow` (client → server).
#[derive(Clone, Debug, PartialEq)]
pub struct ThrowRequest {
    pub player_index: u8,
    pub item_id: i32,
    pub quantity: i32,
    pub position: [f32; 3],
    pub direction: [f32; 3],
}

impl ThrowRequest {
    pub fn encode(&self) -> Vec<u8> {
        let mut w = Writer::new();
        w.u8(self.player_index)
            .i32(self.item_id)
            .i32(self.quantity)
            .vec3(self.position)
            .vec3(self.direction);
        w.into_inner()
    }

    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        let msg = Self {
            player_index: r.u8()?,
            item_id: r.i32()?,
            quantity: r.i32()?,
            position: r.vec3()?,
            direction: r.vec3()?,
        };
        r.finish_allow_zero_padding()?;
        Ok(msg)
    }
}

/// `ItemThrown` (server → watchers).
pub fn encode_item_thrown(
    player: u8,
    network_id: i32,
    item_id: i32,
    quantity: i32,
    position: [f32; 3],
    direction: [f32; 3],
) -> Vec<u8> {
    let mut w = Writer::new();
    w.u8(player)
        .i32(network_id)
        .i32(item_id)
        .i32(quantity)
        .vec3(position)
        .vec3(direction);
    w.into_inner()
}

/// The header of `RequestSyncProjectileEvent` (the rest is relayed unchanged as
/// `SyncProjectileEvent`).
#[derive(Clone, Debug, PartialEq)]
pub struct ProjectileSyncHeader {
    pub sync_index: i32,
    pub remove_after: bool,
    /// Send to everyone, not just nearby players.
    pub broadcast: bool,
    pub include_sender: bool,
    pub force: bool,
}

impl ProjectileSyncHeader {
    pub fn decode(payload: &[u8]) -> Result<Self, DecodeError> {
        let mut r = Reader::new(payload);
        Ok(Self {
            sync_index: r.i32()?,
            remove_after: r.bool()?,
            broadcast: r.bool()?,
            include_sender: r.bool()?,
            force: r.bool()?,
        })
    }
}

/// `AdminCommand` (client → server): a named command, accepted from admins only.
pub fn decode_admin_command(payload: &[u8]) -> Result<String, DecodeError> {
    Reader::new(payload).string()
}

/// The only built-in admin command: force the countdown to start.
pub const ADMIN_START_GAME: &str = "Start Game";

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn chat_uses_utf16_with_a_byte_length() {
        let msg = ChatMessage {
            player_index: 2,
            text: "/give 6 30".into(),
        };
        let bytes = msg.encode();
        assert_eq!(bytes[1], 20);
        assert_eq!(ChatMessage::decode(&bytes).unwrap(), msg);
    }

    #[test]
    fn item_messages_have_official_sizes() {
        let t = ThrowRequest {
            player_index: 1,
            item_id: 50,
            quantity: 1,
            position: [1.0; 3],
            direction: [0.0, 1.0, 0.0],
        };
        assert_eq!(t.encode().len(), 33);
        assert_eq!(ThrowRequest::decode(&t.encode()).unwrap(), t);
        assert_eq!(
            encode_item_thrown(1, 9, 50, 1, [0.0; 3], [0.0; 3]).len(),
            37
        );
        assert_eq!(encode_player_health(1, 65.0).len(), 5);
        assert_eq!(
            encode_throw_chat(1, [0.0; 3], [0.0; 2], &[2, b'h', 0]).len(),
            21 + 3
        );
        let h = HealRequest {
            player_index: 1,
            item_id: 3,
        };
        assert_eq!(HealRequest::decode(&h.encode()).unwrap(), h);
    }

    #[test]
    fn admin_command_is_a_dotnet_string() {
        let mut w = Writer::new();
        w.string(ADMIN_START_GAME);
        assert_eq!(decode_admin_command(&w.into_inner()).unwrap(), "Start Game");
    }
}
