//! Unity Gaming Services REST calls TABG makes: anonymous sign-in and Relay allocations.
//!
//! Same requests as Unity's Authentication 2.4 and Relay 1.0.5 SDKs, against the project and
//! environment TABG was built with (read from the host's own game files, see
//! [`crate::project_id_from_game`]).

use std::net::{SocketAddr, ToSocketAddrs};
use std::time::{Duration, Instant};

use base64::Engine;
use base64::engine::general_purpose::STANDARD as B64;
use serde::Deserialize;
use serde_json::json;

use crate::UtpError;
use crate::relay::{AllocationId, ConnectionData, HmacKey, RelayServerData};

const AUTH_URL: &str = "https://player-auth.services.api.unity.com/v1/authentication/anonymous";
const RELAY_URL: &str = "https://relay-allocations.services.api.unity.com/v1";
pub const ENVIRONMENT: &str = "production";

fn agent() -> ureq::Agent {
    ureq::Agent::config_builder()
        .timeout_global(Some(Duration::from_secs(20)))
        .http_status_as_error(false)
        .build()
        .into()
}

/// A signed-in anonymous Unity player.
#[derive(Clone, Debug)]
pub struct Session {
    pub project_id: String,
    pub player_id: String,
    id_token: String,
    expires: Instant,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct SignInResponse {
    user_id: String,
    id_token: String,
    expires_in: u64,
}

#[derive(Deserialize)]
struct Envelope<T> {
    data: T,
}

#[derive(Deserialize)]
struct AllocationData<T> {
    allocation: T,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Endpoint {
    connection_type: String,
    host: String,
    port: u16,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RawAllocation {
    allocation_id: String,
    allocation_id_bytes: String,
    connection_data: String,
    #[serde(default)]
    host_connection_data: Option<String>,
    key: String,
    region: String,
    server_endpoints: Vec<Endpoint>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct JoinCodeData {
    join_code: String,
}

#[derive(Deserialize)]
struct RegionsData {
    regions: Vec<Region>,
}

/// A relay region, e.g. `europe-west4`.
#[derive(Clone, Debug, Deserialize)]
pub struct Region {
    pub id: String,
    pub description: String,
}

/// A relay allocation: the host's (from [`Session::allocate`]) or a player's (from
/// [`Session::join`]).
#[derive(Clone)]
pub struct Allocation {
    pub id: String,
    pub region: String,
    pub server: RelayServerData,
}

fn send(
    request: ureq::RequestBuilder<ureq::typestate::WithBody>,
    body: serde_json::Value,
    what: &str,
) -> Result<String, UtpError> {
    let mut response = request
        .header("Content-Type", "application/json")
        .send_json(body)
        .map_err(|e| UtpError::Http(format!("{what}: {e}")))?;
    let status = response.status();
    let text = response
        .body_mut()
        .read_to_string()
        .map_err(|e| UtpError::Http(format!("{what}: {e}")))?;
    if !status.is_success() {
        return Err(UtpError::Http(format!("{what}: HTTP {status}: {text}")));
    }
    Ok(text)
}

fn parse<T: serde::de::DeserializeOwned>(text: &str, what: &str) -> Result<T, UtpError> {
    serde_json::from_str(text).map_err(|e| UtpError::Http(format!("{what}: bad response: {e}")))
}

fn decode<const N: usize>(b64: &str, what: &str) -> Result<[u8; N], UtpError> {
    let bytes = B64
        .decode(b64)
        .map_err(|e| UtpError::Http(format!("{what}: bad base64: {e}")))?;
    let mut out = [0u8; N];
    if bytes.len() > N {
        return Err(UtpError::Http(format!(
            "{what}: {} bytes, expected {N}",
            bytes.len()
        )));
    }
    out[..bytes.len()].copy_from_slice(&bytes);
    Ok(out)
}

fn resolve(host: &str, port: u16) -> Result<SocketAddr, UtpError> {
    (host, port)
        .to_socket_addrs()
        .map_err(|e| UtpError::Http(format!("relay endpoint {host}: {e}")))?
        .find(SocketAddr::is_ipv4)
        .ok_or_else(|| UtpError::Http(format!("relay endpoint {host} has no IPv4 address")))
}

impl RawAllocation {
    fn into_allocation(self, host: bool) -> Result<Allocation, UtpError> {
        // TABG uses the plain "udp" endpoint (not dtls) on both sides.
        let endpoint = self
            .server_endpoints
            .iter()
            .find(|e| e.connection_type == "udp")
            .ok_or_else(|| UtpError::Http("allocation has no udp endpoint".into()))?;
        let allocation_id: AllocationId = decode(&self.allocation_id_bytes, "allocationIdBytes")?;
        let connection_data: ConnectionData = decode(&self.connection_data, "connectionData")?;
        let host_connection_data: ConnectionData = if host {
            connection_data
        } else {
            decode(
                self.host_connection_data.as_deref().unwrap_or_default(),
                "hostConnectionData",
            )?
        };
        let key: HmacKey = decode(&self.key, "key")?;
        Ok(Allocation {
            id: self.allocation_id,
            region: self.region,
            server: RelayServerData {
                endpoint: resolve(&endpoint.host, endpoint.port)?,
                allocation_id,
                connection_data,
                host_connection_data,
                key,
            },
        })
    }
}

impl Session {
    /// `SignInAnonymouslyAsync`: a fresh anonymous player in TABG's project.
    pub fn sign_in(project_id: &str) -> Result<Self, UtpError> {
        let text = send(
            agent()
                .post(AUTH_URL)
                .header("ProjectId", project_id)
                .header("Error-Version", "v1")
                .header("UnityEnvironment", ENVIRONMENT),
            json!({}),
            "Unity sign-in",
        )?;
        let r: SignInResponse = parse(&text, "Unity sign-in")?;
        Ok(Self {
            project_id: project_id.to_owned(),
            player_id: r.user_id,
            id_token: r.id_token,
            expires: Instant::now() + Duration::from_secs(r.expires_in.saturating_sub(60)),
        })
    }

    pub fn expired(&self) -> bool {
        Instant::now() >= self.expires
    }

    fn relay_post(
        &self,
        path: &str,
        body: serde_json::Value,
        what: &str,
    ) -> Result<String, UtpError> {
        send(
            agent()
                .post(format!("{RELAY_URL}/{path}"))
                .header("authorization", format!("Bearer {}", self.id_token)),
            body,
            what,
        )
    }

    /// `CreateAllocationAsync(maxConnections, region)`.
    pub fn allocate(
        &self,
        max_connections: u32,
        region: Option<&str>,
    ) -> Result<Allocation, UtpError> {
        let mut body = json!({ "maxConnections": max_connections });
        if let Some(region) = region {
            body["region"] = json!(region);
        }
        let text = self.relay_post("allocate", body, "relay allocate")?;
        let r: Envelope<AllocationData<RawAllocation>> = parse(&text, "relay allocate")?;
        r.data.allocation.into_allocation(true)
    }

    /// `GetJoinCodeAsync(allocationId)`.
    pub fn join_code(&self, allocation_id: &str) -> Result<String, UtpError> {
        let text = self.relay_post(
            "joincode",
            json!({ "allocationId": allocation_id }),
            "relay join code",
        )?;
        let r: Envelope<JoinCodeData> = parse(&text, "relay join code")?;
        Ok(r.data.join_code)
    }

    /// `JoinAllocationAsync(joinCode)`: what a player does with a join code.
    pub fn join(&self, join_code: &str) -> Result<Allocation, UtpError> {
        let text = self.relay_post("join", json!({ "joinCode": join_code }), "relay join")?;
        let r: Envelope<AllocationData<RawAllocation>> = parse(&text, "relay join")?;
        r.data.allocation.into_allocation(false)
    }

    /// The relay regions players can be allocated in.
    pub fn regions(&self) -> Result<Vec<Region>, UtpError> {
        let what = "relay regions";
        let mut response = agent()
            .get(format!("{RELAY_URL}/regions"))
            .header("authorization", format!("Bearer {}", self.id_token))
            .call()
            .map_err(|e| UtpError::Http(format!("{what}: {e}")))?;
        let status = response.status();
        let text = response
            .body_mut()
            .read_to_string()
            .map_err(|e| UtpError::Http(format!("{what}: {e}")))?;
        if !status.is_success() {
            return Err(UtpError::Http(format!("{what}: HTTP {status}: {text}")));
        }
        let r: Envelope<RegionsData> = parse(&text, "relay regions")?;
        Ok(r.data.regions)
    }
}
