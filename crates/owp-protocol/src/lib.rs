use serde::{Deserialize, Serialize};
use time::OffsetDateTime;
use uuid::Uuid;

pub const OWP_PROTOCOL_VERSION: &str = "0.1";

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WorldTokenInfo {
    pub network: String,
    pub mint: String,
    pub dbc_pool: Option<String>,
    pub tx_signatures: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WorldManifestV1 {
    pub protocol_version: String,
    pub world_id: Uuid,
    pub name: String,
    #[serde(with = "time::serde::rfc3339")]
    pub created_at: OffsetDateTime,
    pub world_authority_pubkey: Option<String>,
    pub ports: WorldPorts,
    pub token: Option<WorldTokenInfo>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WorldPorts {
    pub game_port: u16,
    pub asset_port: Option<u16>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WorldDirectoryEntry {
    pub world_id: Uuid,
    pub name: String,
    pub endpoint: String,
    pub port: u16,
    pub token_mint: Option<String>,
    pub dbc_pool: Option<String>,
    pub world_pubkey: Option<String>,
    #[serde(default)]
    pub last_seen: Option<String>,
}
