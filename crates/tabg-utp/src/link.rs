//! Our UDP socket to the relay: binding, keep-alive pings and rebinding, like Unity's
//! `RelayNetworkProtocol.Update`.

use std::io::ErrorKind;
use std::net::UdpSocket;
use std::time::{Duration, Instant};

use crate::UtpError;
use crate::relay::{self, AllocationId, RelayServerData};

/// Unity's `RelayConnectionTimeMS` default: ping the relay when we sent nothing for this long.
pub const RELAY_KEEPALIVE: Duration = Duration::from_millis(3000);
/// Resend `Bind` this often until the relay answers (Unity's connect timeout).
pub const BIND_RETRY: Duration = Duration::from_millis(1000);

pub struct Link {
    socket: UdpSocket,
    pub server: RelayServerData,
    pub bound: bool,
    last_bind: Option<Instant>,
    last_send: Instant,
    last_receive: Instant,
    read_timeout: Option<Duration>,
    nonblocking: bool,
    buf: Vec<u8>,
}

impl Link {
    pub fn new(server: RelayServerData) -> Result<Self, UtpError> {
        let socket = UdpSocket::bind("0.0.0.0:0").map_err(UtpError::Io)?;
        let now = Instant::now();
        Ok(Self {
            socket,
            server,
            bound: false,
            last_bind: None,
            last_send: now,
            last_receive: now,
            read_timeout: None,
            nonblocking: false,
            buf: vec![0; 2048],
        })
    }

    pub fn send_raw(&mut self, datagram: &[u8]) {
        if let Err(e) = self.socket.send_to(datagram, self.server.endpoint) {
            tracing::debug!(error = %e, "relay: send failed");
        }
        self.last_send = Instant::now();
    }

    pub fn relay(&mut self, to: &AllocationId, payload: &[u8]) {
        let m = self.server.relay_message(to, payload);
        self.send_raw(&m);
    }

    /// Bind until bound, then keep the allocation alive.
    pub fn maintain(&mut self, now: Instant) {
        if !self.bound {
            if self
                .last_bind
                .is_none_or(|t| now.duration_since(t) >= BIND_RETRY)
            {
                self.last_bind = Some(now);
                let m = self.server.bind_message();
                self.send_raw(&m);
            }
            return;
        }
        if now.duration_since(self.last_send) >= RELAY_KEEPALIVE {
            let m = self.server.ping_message();
            self.send_raw(&m);
        }
        if now.duration_since(self.last_receive) >= RELAY_KEEPALIVE * 3 {
            // Unity rebinds when the relay went quiet, in case it forgot us.
            self.last_receive = now;
            let m = self.server.bind_message();
            self.send_raw(&m);
        }
    }

    /// Wait up to `timeout` for a datagram from the relay.
    pub fn recv(&mut self, timeout: Duration) -> Option<Vec<u8>> {
        let result = if timeout.is_zero() {
            if !self.nonblocking {
                self.socket.set_nonblocking(true).ok()?;
                self.nonblocking = true;
            }
            self.socket.recv_from(&mut self.buf)
        } else {
            if self.nonblocking {
                self.socket.set_nonblocking(false).ok()?;
                self.nonblocking = false;
            }
            if self.read_timeout != Some(timeout) {
                self.socket.set_read_timeout(Some(timeout)).ok()?;
                self.read_timeout = Some(timeout);
            }
            self.socket.recv_from(&mut self.buf)
        };
        match result {
            Ok((n, from)) if from == self.server.endpoint => {
                self.last_receive = Instant::now();
                let data = self.buf[..n].to_vec();
                if relay::parse(&data) == Some(relay::Incoming::BindReceived) {
                    self.bound = true;
                }
                Some(data)
            }
            Ok(_) => None,
            Err(e) if matches!(e.kind(), ErrorKind::WouldBlock | ErrorKind::TimedOut) => None,
            Err(e) => {
                tracing::debug!(error = %e, "relay: receive failed");
                None
            }
        }
    }

    /// Bind and wait for the relay's `BindReceived`.
    pub fn bind_blocking(&mut self, timeout: Duration) -> Result<(), UtpError> {
        let deadline = Instant::now() + timeout;
        while !self.bound {
            let now = Instant::now();
            if now >= deadline {
                return Err(UtpError::Relay(format!(
                    "the relay at {} did not answer our bind",
                    self.server.endpoint
                )));
            }
            self.maintain(now);
            if let Some(data) = self.recv(Duration::from_millis(100))
                && let Some(relay::Incoming::Error { code, .. }) = relay::parse(&data)
            {
                return Err(UtpError::Relay(format!(
                    "relay refused bind: {}",
                    relay::error_text(code)
                )));
            }
        }
        Ok(())
    }
}
