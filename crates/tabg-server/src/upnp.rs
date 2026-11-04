//! Open the game's UDP port on the home router with UPnP, so friends can join a direct-IP server
//! without manual port forwarding. Best effort: many routers have UPnP off.

use std::net::{IpAddr, SocketAddr, UdpSocket};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

use igd_next::{Gateway, PortMappingProtocol, SearchOptions};
use tracing::{info, warn};

const LEASE: Duration = Duration::from_secs(3600);
const DESCRIPTION: &str = "tabg server";

/// The LAN address the router should forward to: the one we route through to reach it.
fn local_ip_towards(gateway: SocketAddr) -> Option<IpAddr> {
    let s = UdpSocket::bind("0.0.0.0:0").ok()?;
    s.connect(gateway).ok()?;
    Some(s.local_addr().ok()?.ip())
}

fn map(gateway: &Gateway, port: u16) -> Result<IpAddr, String> {
    let local = local_ip_towards(gateway.addr).ok_or("no route to the router")?;
    gateway
        .add_port(
            PortMappingProtocol::UDP,
            port,
            SocketAddr::new(local, port),
            u32::try_from(LEASE.as_secs()).unwrap_or(u32::MAX),
            DESCRIPTION,
        )
        .map_err(|e| e.to_string())?;
    gateway.get_external_ip().map_err(|e| e.to_string())
}

/// An open port mapping; renewed in the background and removed on drop.
pub struct Forwarding {
    pub external_ip: IpAddr,
    stop: Arc<AtomicBool>,
    thread: Option<std::thread::JoinHandle<()>>,
}

impl Drop for Forwarding {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(t) = self.thread.take() {
            let _ = t.join();
        }
    }
}

/// Map `port` on the router. `None` (with a logged hint) if there's no UPnP router or it refused.
pub fn forward(port: u16) -> Option<Forwarding> {
    let gateway = match igd_next::search_gateway(SearchOptions {
        timeout: Some(Duration::from_secs(3)),
        ..Default::default()
    }) {
        Ok(g) => g,
        Err(e) => {
            warn!(error = %e, "UPnP: no router answered; forward UDP port {port} manually if friends can't join");
            return None;
        }
    };
    let external_ip = match map(&gateway, port) {
        Ok(ip) => ip,
        Err(e) => {
            warn!(error = %e, "UPnP: the router refused to open UDP port {port}; forward it manually");
            return None;
        }
    };
    info!(%external_ip, port, "UPnP: router forwards UDP port {port}; friends join {external_ip}:{port}");
    let stop = Arc::new(AtomicBool::new(false));
    let thread = {
        let stop = Arc::clone(&stop);
        std::thread::Builder::new()
            .name("upnp".into())
            .spawn(move || {
                let mut renewed = Instant::now();
                while !stop.load(Ordering::Relaxed) {
                    std::thread::sleep(Duration::from_millis(200));
                    if renewed.elapsed() >= LEASE / 2 {
                        renewed = Instant::now();
                        if let Err(e) = map(&gateway, port) {
                            warn!(error = %e, "UPnP: renewing the port mapping failed");
                        }
                    }
                }
                match gateway.remove_port(PortMappingProtocol::UDP, port) {
                    Ok(()) => info!(port, "UPnP: port mapping removed"),
                    Err(e) => warn!(error = %e, "UPnP: removing the port mapping failed"),
                }
            })
            .ok()
    };
    Some(Forwarding {
        external_ip,
        stop,
        thread,
    })
}
