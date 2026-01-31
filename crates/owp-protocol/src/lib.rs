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
    /// Optional structured parts for procedural/kitbashed rendering.
    /// If empty, clients may fall back to interpreting `tags`.
    #[serde(default)]
    pub parts: Vec<AvatarPartV1>,
    /// Optional generated mesh representation (e.g. via OpenSCAD/Blender pipeline).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub mesh: Option<AvatarMeshV1>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AvatarMeshV1 {
    /// Mesh format identifier, e.g. "stl" or "gltf".
    pub format: String,
    /// URI to fetch the mesh from (typically a local admin endpoint).
    pub uri: String,
    /// Optional content hash for caching.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub sha256: Option<String>,
    /// Optional list of mesh parts (for multi-material looks when the container format is STL).
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub parts: Vec<AvatarMeshPartV1>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AvatarMeshPartV1 {
    /// Short identifier used for caching/debugging (e.g. "body", "hat", "staff").
    pub id: String,
    /// URI to fetch this part from (typically a local admin endpoint).
    pub uri: String,
    /// Optional content hash for caching.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub sha256: Option<String>,
    /// Optional material hint: "primary", "secondary", or "emissive".
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub material: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AvatarPartV1 {
    /// Freeform identifier, e.g. "horn_left", "glow_stripe_1"
    pub id: String,
    /// Attachment point, e.g. "body" or "head"
    pub attach: String,
    /// Primitive type: "sphere" | "capsule" | "cube" | "cylinder"
    pub primitive: String,
    /// Local position relative to `attach`
    pub position: [f32; 3],
    /// Local rotation in degrees (Euler XYZ) relative to `attach`
    pub rotation: [f32; 3],
    /// Local scale
    pub scale: [f32; 3],
    /// Base color hex like "#RRGGBB"
    pub color: String,
    /// Optional emission color hex like "#RRGGBB"
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub emission_color: Option<String>,
    /// Optional emission intensity (0 disables). Typical range 0-5.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub emission_strength: Option<f32>,
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
