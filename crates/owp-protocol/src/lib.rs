use serde::{Deserialize, Serialize};
use time::OffsetDateTime;
use uuid::Uuid;

pub const OWP_PROTOCOL_VERSION: &str = "0.1";

pub mod wire;

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

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AvatarSpecV1 {
    pub version: String,
    pub name: String,
    /// Hex color string like "#RRGGBB"
    pub primary_color: String,
    /// Hex color string like "#RRGGBB"
    pub secondary_color: String,
    /// Height multiplier for the placeholder avatar (0.5 - 2.0)
    pub height: f32,
    /// Freeform tags like "athletic", "cyberpunk", etc.
    #[serde(default)]
    pub tags: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum Message {
    Hello(Hello),
    Welcome(Welcome),
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Hello {
    pub protocol_version: String,
    pub request_id: Uuid,
    #[serde(default)]
    pub world_id: Option<Uuid>,
    #[serde(default)]
    pub client_name: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Welcome {
    pub protocol_version: String,
    pub request_id: Uuid,
    pub world_id: Uuid,
    #[serde(default)]
    pub token_mint: Option<String>,
    #[serde(default)]
    pub motd: Option<String>,
    #[serde(default)]
    pub capabilities: Vec<String>,
}
