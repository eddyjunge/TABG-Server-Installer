//! A headless TABG client.
//!
//! It speaks the same join sequence as the real client (connect → `RoomInit` →
//! `RequestWorldState` → `PlayerUpdate`s) and records everything the server sends. Used by tests
//! against our server and, as ground truth, against Landfall's own server.

use std::collections::BTreeMap;
use std::time::{Duration, Instant};

use tabg_net::{Host, NetError, NetEvent, PeerId, Transport};
use tabg_protocol::messages::{
    ClientPlayerUpdate, EntityBody, PackedDirection, PlayerJoined, PlayerUpdateBatch,
    RoomInitRequest, RoomInitResponse, WorldState,
};
use tabg_protocol::{EventCode, Packet};

/// How many raw payloads [`Observed::payloads`] keeps.
pub const MAX_KEPT_PAYLOADS: usize = 500;

/// Everything the probe has learned so far.
#[derive(Debug, Default)]
pub struct Observed {
    pub connected: bool,
    pub disconnected: bool,
    pub response: Option<RoomInitResponse>,
    pub world: Option<WorldState>,
    pub joined: Vec<PlayerJoined>,
    /// Last known position of every other player, from `PlayerUpdate` batches.
    pub others: BTreeMap<u8, [f32; 3]>,
    /// `(seconds since start, event code, payload length)` for every packet received.
    pub packets: Vec<(f32, u8, usize)>,
    /// Payloads that failed to decode with our message types: `(event code, error)`.
    pub decode_errors: Vec<(u8, String)>,
    /// Raw payloads in arrival order, capped at [`MAX_KEPT_PAYLOADS`] (fixtures and debugging).
    pub payloads: Vec<(u8, Vec<u8>)>,
}

pub struct Probe {
    host: Box<dyn Transport + Send>,
    server: PeerId,
    request: RoomInitRequest,
    started: Instant,
    pub seen: Observed,
}

impl Probe {
    /// Start connecting to `addr:port` and join as `name` once connected.
    pub fn connect(addr: &str, port: u16, request: RoomInitRequest) -> Result<Self, NetError> {
        let mut host = Host::client()?;
        let server = host.connect(addr, port)?;
        Ok(Self::over(Box::new(host), server, request))
    }

    /// Join over any transport (e.g. a relay client) whose server peer is `server`.
    pub fn over(host: Box<dyn Transport + Send>, server: PeerId, request: RoomInitRequest) -> Self {
        Self {
            host,
            server,
            request,
            started: Instant::now(),
            seen: Observed::default(),
        }
    }

    pub fn named(addr: &str, port: u16, name: &str) -> Result<Self, NetError> {
        Self::connect(addr, port, Self::request_for(name))
    }

    /// The join request a solo real client sends.
    pub fn request_for(name: &str) -> RoomInitRequest {
        RoomInitRequest {
            player_name: name.into(),
            death_phrase: "gg".into(),
            // Solo players join as the host of a 1-player squad: the official server refuses
            // anyone without a squad booking (and drops the refusal, so the client just hangs).
            is_squad_host: true,
            squad_size: 1,
            squad_id: name.bytes().fold(0xcbf2_9ce4_8422_2325_u64, |h, b| {
                (h ^ u64::from(b)).wrapping_mul(0x100_0000_01b3)
            }),
            // The official server requires these to be NUL-terminated and throws otherwise.
            epic_id_token: vec![0],
            epic_product_user_id: vec![0],
            ..Default::default()
        }
    }

    pub fn player_index(&self) -> Option<u8> {
        match self.seen.response {
            Some(RoomInitResponse::Accepted { player_index, .. }) => Some(player_index),
            _ => None,
        }
    }

    fn game_mode(&self) -> u8 {
        match self.seen.response {
            Some(RoomInitResponse::Accepted { game_mode, .. }) => game_mode,
            _ => 0,
        }
    }

    pub fn send(
        &mut self,
        event: EventCode,
        payload: Vec<u8>,
        reliable: bool,
    ) -> Result<(), NetError> {
        self.host.send(
            self.server,
            &Packet::new(event, payload).to_wire(),
            reliable,
        )
    }

    /// Send one of our client mods' messages (event codes the game itself doesn't have).
    pub fn send_raw(&mut self, code: u8, payload: Vec<u8>, reliable: bool) -> Result<(), NetError> {
        self.host
            .send(self.server, &Packet { code, payload }.to_wire(), reliable)
    }

    /// Report our own position, like the real client does every frame.
    pub fn move_to(&mut self, position: [f32; 3]) -> Result<(), NetError> {
        let Some(player_index) = self.player_index() else {
            return Ok(());
        };
        let up = ClientPlayerUpdate {
            player_index,
            position,
            rotation: [0.0, 0.0],
            aiming_down_sights: false,
            direction: PackedDirection::ZERO,
            movement: 0,
        };
        self.send(EventCode::PlayerUpdate, up.encode(), false)
    }

    /// Report that we hit `victim`, leaving them at `new_health`.
    pub fn hit(&mut self, victim: u8, new_health: f32) -> Result<(), NetError> {
        let Some(attacker) = self.player_index() else {
            return Ok(());
        };
        let ev = tabg_protocol::combat::DamageEvent {
            attacker,
            victim,
            new_health,
            direction: [0.0, 0.0, 1.0],
            hit: None,
            exclude_victim: false,
        };
        self.send(EventCode::DamageEvent, ev.encode(), true)
    }

    /// Process network events for up to `budget`.
    pub fn pump(&mut self, budget: Duration) -> Result<(), NetError> {
        let deadline = Instant::now() + budget;
        loop {
            let left = deadline.saturating_duration_since(Instant::now());
            match self.host.service(left.min(Duration::from_millis(5))) {
                Some(event) => self.on_event(event)?,
                None if left.is_zero() => return Ok(()),
                None => {}
            }
        }
    }

    /// Pump until `done` returns true or `timeout` passes. Returns whether `done` became true.
    pub fn pump_until(
        &mut self,
        timeout: Duration,
        mut done: impl FnMut(&Observed) -> bool,
    ) -> Result<bool, NetError> {
        let deadline = Instant::now() + timeout;
        while Instant::now() < deadline {
            if done(&self.seen) {
                return Ok(true);
            }
            self.pump(Duration::from_millis(10))?;
        }
        Ok(done(&self.seen))
    }

    fn on_event(&mut self, event: NetEvent) -> Result<(), NetError> {
        match event {
            NetEvent::Connected { .. } => {
                self.seen.connected = true;
                self.send(EventCode::RoomInit, self.request.encode(), true)?;
            }
            NetEvent::Disconnected { .. } | NetEvent::TimedOut { .. } => {
                self.seen.disconnected = true
            }
            NetEvent::Received { data, .. } => {
                let Some(packet) = Packet::from_wire(&data) else {
                    return Ok(());
                };
                self.seen.packets.push((
                    self.started.elapsed().as_secs_f32(),
                    packet.code,
                    packet.payload.len(),
                ));
                if self.seen.payloads.len() < MAX_KEPT_PAYLOADS {
                    self.seen
                        .payloads
                        .push((packet.code, packet.payload.clone()));
                }
                self.on_packet(packet)?;
            }
        }
        Ok(())
    }

    fn on_packet(&mut self, packet: Packet) -> Result<(), NetError> {
        let code = packet.code;
        let fail = |seen: &mut Observed, e: tabg_protocol::DecodeError| {
            seen.decode_errors.push((code, e.to_string()))
        };
        match packet.event() {
            Some(EventCode::RoomInitRequestResponse) => {
                match RoomInitResponse::decode(&packet.payload) {
                    Ok(resp) => {
                        let accepted = matches!(resp, RoomInitResponse::Accepted { .. });
                        self.seen.response = Some(resp);
                        if accepted {
                            self.send(EventCode::RequestWorldState, Vec::new(), true)?;
                        }
                    }
                    Err(e) => fail(&mut self.seen, e),
                }
            }
            // Login is either our world state (our index, first time) or another player joining.
            Some(EventCode::Login) => {
                let ours = self.player_index().is_some()
                    && packet.payload.first().copied() == self.player_index()
                    && self.seen.world.is_none();
                if ours {
                    match WorldState::decode(&packet.payload, self.game_mode()) {
                        Ok(w) => self.seen.world = Some(w),
                        Err(e) => fail(&mut self.seen, e),
                    }
                } else {
                    match PlayerJoined::decode(&packet.payload) {
                        Ok(j) => self.seen.joined.push(j),
                        Err(e) => fail(&mut self.seen, e),
                    }
                }
            }
            Some(EventCode::PlayerUpdate) => match PlayerUpdateBatch::decode(&packet.payload) {
                Ok(batch) => {
                    for e in batch.entities {
                        if let EntityBody::OnFoot {
                            position: Some(p), ..
                        } = e.body
                        {
                            self.seen.others.insert(e.player_index, p);
                        }
                    }
                }
                Err(e) => fail(&mut self.seen, e),
            },
            _ => {}
        }
        Ok(())
    }
}
