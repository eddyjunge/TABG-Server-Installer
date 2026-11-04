//! The player side: join a relay-hosted server by join code, like TABG's
//! `UnityTransportClientNetworker.ConnectViaRelay`. Used by tests and the probe.

use std::collections::VecDeque;
use std::time::{Duration, Instant};

use tabg_net::{NetError, NetEvent, PeerId, Transport};

use crate::UtpError;
use crate::conn::{self, ConnEvent, Connection};
use crate::link::Link;
use crate::relay::{self, AllocationId, Incoming};
use crate::ugs::Session;

/// The server's peer id on a [`RelayClient`].
pub const SERVER: PeerId = 0;

pub struct RelayClient {
    link: Link,
    host: Option<AllocationId>,
    conn: Connection,
    last_attempt: Option<Instant>,
    events: VecDeque<NetEvent>,
    gone: bool,
}

impl RelayClient {
    /// Sign in as a new anonymous player, join `join_code` and start connecting. A
    /// [`NetEvent::Connected`] for [`SERVER`] follows from [`Transport::service`].
    pub fn join(project_id: &str, join_code: &str) -> Result<Self, UtpError> {
        let session = Session::sign_in(project_id)?;
        let allocation = session.join(join_code)?;
        let mut link = Link::new(allocation.server)?;
        link.bind_blocking(Duration::from_secs(10))?;
        let now = Instant::now();
        Ok(Self {
            link,
            host: None,
            conn: Connection::new(conn::random_token(), [0; 8], now),
            last_attempt: None,
            events: VecDeque::new(),
            gone: false,
        })
    }

    fn try_connect(&mut self, now: Instant) {
        if self.conn.connected || self.gone {
            return;
        }
        if self
            .last_attempt
            .is_some_and(|t| now.duration_since(t) < conn::CONNECT_TIMEOUT)
        {
            return;
        }
        self.last_attempt = Some(now);
        match self.host {
            None => {
                let m = self.link.server.connect_request_message();
                self.link.send_raw(&m);
            }
            Some(host) => {
                let p = conn::header_only(conn::CONNECTION_REQUEST, self.conn.receive_token);
                self.link.relay(&host, &p);
            }
        }
    }

    fn flush_conn(&mut self) {
        if let Some(host) = self.host {
            for p in std::mem::take(&mut self.conn.out) {
                self.link.relay(&host, &p);
            }
        }
    }

    fn handle(&mut self, data: &[u8], now: Instant) {
        match relay::parse(data) {
            Some(Incoming::Accepted { from, .. }) if self.host.is_none() => {
                self.host = Some(from);
                self.last_attempt = None;
                self.try_connect(now);
            }
            Some(Incoming::Relay { from, payload, .. }) if Some(from) == self.host => {
                let Some((header, body)) = conn::parse_header(payload) else {
                    return;
                };
                if header.token != self.conn.receive_token {
                    return;
                }
                match header.kind {
                    conn::CONNECTION_ACCEPT if !self.conn.connected => {
                        if body.len() == 8 {
                            self.conn.send_token.copy_from_slice(body);
                            self.conn.connected = true;
                            self.conn.last_receive = now;
                            self.events.push_back(NetEvent::Connected {
                                peer: SERVER,
                                address: "relay".into(),
                            });
                        }
                    }
                    conn::CONNECTION_REJECT => {
                        self.gone = true;
                        self.events
                            .push_back(NetEvent::Disconnected { peer: SERVER });
                    }
                    _ if self.conn.connected => {
                        for event in self.conn.receive(header, body, now) {
                            match event {
                                ConnEvent::Message(data) => {
                                    self.events.push_back(NetEvent::Received {
                                        peer: SERVER,
                                        channel: 0,
                                        data,
                                    });
                                }
                                ConnEvent::Disconnected => {
                                    self.gone = true;
                                    self.events
                                        .push_back(NetEvent::Disconnected { peer: SERVER });
                                }
                            }
                        }
                        self.flush_conn();
                    }
                    _ => {}
                }
            }
            Some(Incoming::Error { code, .. }) => {
                tracing::warn!(code, "relay error: {}", relay::error_text(code));
            }
            _ => {}
        }
    }

    fn update(&mut self, now: Instant) {
        self.link.maintain(now);
        self.try_connect(now);
        if self.conn.connected && self.conn.update(now) {
            self.gone = true;
            self.events.push_back(NetEvent::TimedOut { peer: SERVER });
        }
        self.flush_conn();
    }
}

impl Transport for RelayClient {
    fn service(&mut self, timeout: Duration) -> Option<NetEvent> {
        let deadline = Instant::now() + timeout;
        loop {
            if let Some(event) = self.events.pop_front() {
                return Some(event);
            }
            let left = deadline.saturating_duration_since(Instant::now());
            match self.link.recv(left.min(Duration::from_millis(20))) {
                Some(data) => self.handle(&data, Instant::now()),
                None => {
                    self.update(Instant::now());
                    if Instant::now() >= deadline {
                        return self.events.pop_front();
                    }
                }
            }
        }
    }

    fn send(&mut self, peer: PeerId, data: &[u8], reliable: bool) -> Result<(), NetError> {
        if peer != SERVER || !self.conn.connected {
            return Err(NetError::UnknownPeer(peer));
        }
        self.conn.send(data, reliable, Instant::now());
        self.flush_conn();
        Ok(())
    }

    fn disconnect(&mut self, _peer: PeerId) {
        if self.conn.connected
            && let Some(host) = self.host
        {
            let p = self.conn.disconnect_packet();
            self.link.relay(&host, &p);
            let m = self.link.server.disconnect_message(&host);
            self.link.send_raw(&m);
            self.conn.connected = false;
            self.gone = true;
            self.events
                .push_back(NetEvent::Disconnected { peer: SERVER });
        }
    }

    fn flush(&mut self) {
        self.update(Instant::now());
    }
}
