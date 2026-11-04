//! The server side: host a TABG room behind a Unity Relay join code, like Landfall's
//! `UnityTransportServer.HostRelay`.

use std::collections::VecDeque;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use tabg_net::{NetError, NetEvent, PeerId, Transport};
use tracing::{info, warn};

use crate::UtpError;
use crate::conn::{self, ConnEvent, Connection};
use crate::link::Link;
use crate::relay::{self, AllocationId, Incoming};
use crate::ugs::{Allocation, Session};

/// How to get a relay allocation.
#[derive(Clone, Debug)]
pub struct RelayConfig {
    /// TABG's Unity Cloud project id (see [`crate::project_id_from_game`]).
    pub project_id: String,
    /// Relay region like `europe-west4`; `None` lets Unity pick, as TABG does.
    pub region: Option<String>,
    /// Players the allocation accepts.
    pub max_players: u32,
}

struct Peer {
    allocation: AllocationId,
    conn: Connection,
}

/// A relay-hosted server. Peer ids are TABG player indices (lowest free first, like TABG).
pub struct RelayHost {
    config: RelayConfig,
    session: Session,
    allocation: Allocation,
    link: Link,
    join_code: Arc<Mutex<String>>,
    peers: Vec<Option<Peer>>,
    events: VecDeque<NetEvent>,
    invalid: bool,
}

fn hex(id: &AllocationId) -> String {
    id.iter().map(|b| format!("{b:02x}")).collect()
}

impl RelayHost {
    /// Sign in, allocate a relay, bind to it and get a join code. Blocks for a few seconds.
    pub fn host(config: RelayConfig) -> Result<Self, UtpError> {
        let session = Session::sign_in(&config.project_id)?;
        info!(player = %session.player_id, "signed in to Unity");
        let (allocation, link, code) = Self::allocate(&session, &config)?;
        let max = usize::try_from(config.max_players).unwrap_or(255).min(255);
        Ok(Self {
            config,
            session,
            allocation,
            link,
            join_code: Arc::new(Mutex::new(code)),
            peers: (0..max).map(|_| None).collect(),
            events: VecDeque::new(),
            invalid: false,
        })
    }

    fn allocate(
        session: &Session,
        config: &RelayConfig,
    ) -> Result<(Allocation, Link, String), UtpError> {
        let allocation = session.allocate(config.max_players, config.region.as_deref())?;
        info!(id = %allocation.id, region = %allocation.region, relay = %allocation.server.endpoint, "relay allocated");
        let mut link = Link::new(allocation.server.clone())?;
        link.bind_blocking(Duration::from_secs(10))?;
        let code = session.join_code(&allocation.id)?;
        info!(join_code = %code, "relay ready");
        Ok((allocation, link, code))
    }

    /// The current join code. It changes if the relay drops our allocation and we get a new one.
    pub fn join_code(&self) -> String {
        self.join_code.lock().map(|c| c.clone()).unwrap_or_default()
    }

    /// A handle that always reads the current join code (for status displays).
    pub fn join_code_handle(&self) -> Arc<Mutex<String>> {
        Arc::clone(&self.join_code)
    }

    pub fn region(&self) -> &str {
        &self.allocation.region
    }

    fn peer_by(&self, allocation: &AllocationId) -> Option<usize> {
        self.peers
            .iter()
            .position(|p| p.as_ref().is_some_and(|p| &p.allocation == allocation))
    }

    fn flush_peer(&mut self, index: usize) {
        if let Some(peer) = self.peers[index].as_mut() {
            let to = peer.allocation;
            for packet in std::mem::take(&mut peer.conn.out) {
                self.link.relay(&to, &packet);
            }
        }
    }

    fn drop_peer(&mut self, index: usize, notify_remote: bool) -> bool {
        let Some(peer) = self.peers[index].take() else {
            return false;
        };
        if notify_remote {
            let p = peer.conn.disconnect_packet();
            self.link.relay(&peer.allocation, &p);
        }
        let m = self.link.server.disconnect_message(&peer.allocation);
        self.link.send_raw(&m);
        true
    }

    fn handle_datagram(&mut self, data: &[u8], now: Instant) {
        match relay::parse(data) {
            Some(Incoming::Relay { from, payload, .. }) => self.handle_relayed(from, payload, now),
            Some(Incoming::Error { code, .. }) => {
                warn!(code, "relay error: {}", relay::error_text(code));
                if relay::error_invalidates_allocation(code) {
                    self.invalid = true;
                }
            }
            _ => {}
        }
    }

    fn handle_relayed(&mut self, from: AllocationId, packet: &[u8], now: Instant) {
        let Some((header, body)) = conn::parse_header(packet) else {
            return;
        };
        let existing = self.peer_by(&from);
        if header.kind == conn::CONNECTION_REQUEST {
            if let Some(i) = existing {
                let peer = self.peers[i].as_mut().expect("found above");
                if peer.conn.send_token == header.token {
                    // A retry: the client didn't get our accept yet.
                    let accept = peer.conn.connection_accept(now);
                    self.link.relay(&from, &accept);
                    return;
                }
                // Same allocation, new connection: the old one is gone.
                self.drop_peer(i, false);
                self.events
                    .push_back(NetEvent::Disconnected { peer: i as PeerId });
            }
            let Some(i) = self.peers.iter().position(Option::is_none) else {
                warn!("relay: server full, rejecting a connection");
                let reject = conn::header_only(conn::CONNECTION_REJECT, header.token);
                self.link.relay(&from, &reject);
                return;
            };
            let mut c = Connection::new(conn::random_token(), header.token, now);
            c.connected = true;
            let accept = c.connection_accept(now);
            self.link.relay(&from, &accept);
            self.peers[i] = Some(Peer {
                allocation: from,
                conn: c,
            });
            self.events.push_back(NetEvent::Connected {
                peer: i as PeerId,
                address: format!("relay:{}", hex(&from)),
            });
            return;
        }
        let Some(i) = existing else {
            return;
        };
        let peer = self.peers[i].as_mut().expect("found above");
        if peer.conn.receive_token != header.token {
            return;
        }
        for event in peer.conn.receive(header, body, now) {
            match event {
                ConnEvent::Message(data) => self.events.push_back(NetEvent::Received {
                    peer: i as PeerId,
                    channel: 0,
                    data,
                }),
                ConnEvent::Disconnected => {
                    self.drop_peer(i, false);
                    self.events
                        .push_back(NetEvent::Disconnected { peer: i as PeerId });
                    return;
                }
            }
        }
        self.flush_peer(i);
    }

    fn update(&mut self, now: Instant) {
        if self.invalid {
            self.reallocate();
        }
        self.link.maintain(now);
        for i in 0..self.peers.len() {
            let Some(peer) = self.peers[i].as_mut() else {
                continue;
            };
            if peer.conn.update(now) {
                self.drop_peer(i, false);
                self.events
                    .push_back(NetEvent::TimedOut { peer: i as PeerId });
            } else {
                self.flush_peer(i);
            }
        }
    }

    /// The relay forgot our allocation: everyone is gone, get a new allocation and join code.
    fn reallocate(&mut self) {
        warn!("relay allocation lost; allocating a new one (the join code changes)");
        for i in 0..self.peers.len() {
            if self.peers[i].take().is_some() {
                self.events
                    .push_back(NetEvent::Disconnected { peer: i as PeerId });
            }
        }
        if self.session.expired() {
            match Session::sign_in(&self.config.project_id) {
                Ok(s) => self.session = s,
                Err(e) => {
                    warn!(error = %e, "Unity sign-in failed; retrying later");
                    return;
                }
            }
        }
        match Self::allocate(&self.session, &self.config) {
            Ok((allocation, link, code)) => {
                self.allocation = allocation;
                self.link = link;
                if let Ok(mut c) = self.join_code.lock() {
                    *c = code;
                }
                self.invalid = false;
            }
            Err(e) => warn!(error = %e, "relay allocation failed; retrying later"),
        }
    }
}

impl Transport for RelayHost {
    fn service(&mut self, timeout: Duration) -> Option<NetEvent> {
        let deadline = Instant::now() + timeout;
        loop {
            if let Some(event) = self.events.pop_front() {
                return Some(event);
            }
            let now = Instant::now();
            let left = deadline.saturating_duration_since(now);
            match self.link.recv(left) {
                Some(data) => self.handle_datagram(&data, Instant::now()),
                None => {
                    self.update(Instant::now());
                    return self.events.pop_front();
                }
            }
        }
    }

    fn send(&mut self, peer: PeerId, data: &[u8], reliable: bool) -> Result<(), NetError> {
        let index = usize::try_from(peer).unwrap_or(usize::MAX);
        let Some(p) = self.peers.get_mut(index).and_then(Option::as_mut) else {
            return Err(NetError::UnknownPeer(peer));
        };
        p.conn.send(data, reliable, Instant::now());
        self.flush_peer(index);
        Ok(())
    }

    fn disconnect(&mut self, peer: PeerId) {
        let index = usize::try_from(peer).unwrap_or(usize::MAX);
        if index < self.peers.len() && self.drop_peer(index, true) {
            self.events.push_back(NetEvent::Disconnected { peer });
        }
    }

    fn flush(&mut self) {
        self.update(Instant::now());
    }
}
