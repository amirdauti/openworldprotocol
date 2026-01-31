use anyhow::{Context, Result};
use axum::{
    extract::{Path, State},
    http::{HeaderMap, StatusCode},
    response::IntoResponse,
    routing::{get, post},
    Json, Router,
};
use owp_discovery;
use owp_protocol::{AvatarSpecV1, WorldDirectoryEntry, WorldManifestV1};
use serde::{Deserialize, Serialize};
use std::net::SocketAddr;
use tower_http::cors::{Any, CorsLayer};
use tracing::{error, info};
use uuid::Uuid;

use crate::assistant::{self, AssistantProviderId};
use crate::avatar as avatar_mod;
use crate::avatar_mesh as avatar_mesh_mod;
use crate::storage::WorldStore;
use crate::world_plan as world_plan_mod;

#[derive(Clone)]
pub enum AuthMode {
    Disabled,
    BearerToken(String),
}

#[derive(Clone)]
struct AppState {
    store: WorldStore,
    auth: AuthMode,
    discovery: DiscoveryConfig,
}

fn require_auth(headers: &HeaderMap, auth: &AuthMode) -> Result<(), StatusCode> {
    match auth {
        AuthMode::Disabled => Ok(()),
        AuthMode::BearerToken(expected) => {
            let Some(value) = headers.get(axum::http::header::AUTHORIZATION) else {
                return Err(StatusCode::UNAUTHORIZED);
            };
            let Ok(value) = value.to_str() else {
                return Err(StatusCode::UNAUTHORIZED);
            };
            let Some(token) = value.strip_prefix("Bearer ") else {
                return Err(StatusCode::UNAUTHORIZED);
            };
            if token != expected {
                return Err(StatusCode::FORBIDDEN);
            }
            Ok(())
        }
    }
}

#[derive(Debug, Clone)]
pub struct DiscoveryConfig {
    pub solana_rpc_url: Option<String>,
    pub registry_program_id: Option<String>,
}

#[derive(Debug, Serialize)]
struct HealthResponse {
    ok: bool,
    version: &'static str,
}

async fn health() -> Json<HealthResponse> {
    Json(HealthResponse {
        ok: true,
        version: "0.1.0",
    })
}

async fn list_worlds(
    State(st): State<AppState>,
    headers: HeaderMap,
) -> Result<Json<Vec<WorldDirectoryEntry>>, StatusCode> {
    require_auth(&headers, &st.auth)?;

    let manifests = st
        .store
        .list_worlds()
        .map_err(|_| StatusCode::INTERNAL_SERVER_ERROR)?;
    let out = manifests
        .into_iter()
        .map(|m| WorldDirectoryEntry {
            world_id: m.world_id,
            name: m.name,
            endpoint: "127.0.0.1".to_string(),
            port: m.ports.game_port,
            token_mint: m.token.as_ref().map(|t| t.mint.clone()),
            dbc_pool: m.token.as_ref().and_then(|t| t.dbc_pool.clone()),
            world_pubkey: m.world_authority_pubkey.clone(),
            last_seen: None,
        })
        .collect();
    Ok(Json(out))
}

#[derive(Debug, Deserialize)]
struct CreateWorldRequest {
    name: String,
    #[serde(default = "default_game_port")]
    game_port: u16,
}

fn default_game_port() -> u16 {
    7777
}

async fn create_world(
    State(st): State<AppState>,
    headers: HeaderMap,
    Json(req): Json<CreateWorldRequest>,
) -> Result<Json<WorldManifestV1>, StatusCode> {
    require_auth(&headers, &st.auth)?;
    let manifest = st
        .store
        .create_world(&req.name, req.game_port)
        .map_err(|_| StatusCode::INTERNAL_SERVER_ERROR)?;
    Ok(Json(manifest))
}

async fn get_manifest(
    State(st): State<AppState>,
    headers: HeaderMap,
    Path(world_id): Path<String>,
) -> Result<Json<WorldManifestV1>, StatusCode> {
    require_auth(&headers, &st.auth)?;
    let world_id = Uuid::parse_str(&world_id).map_err(|_| StatusCode::BAD_REQUEST)?;
    let dir = st.store.world_dir(world_id);
    if !dir.exists() {
        return Err(StatusCode::NOT_FOUND);
    }
    let manifest = st
        .store
        .read_manifest(&dir)
        .map_err(|_| StatusCode::INTERNAL_SERVER_ERROR)?;
    Ok(Json(manifest))
}

#[derive(Debug, Deserialize)]
struct PublishResultRequest {
    network: String,
    mint: String,
    #[serde(default)]
    dbc_pool: Option<String>,
    #[serde(default)]
    tx_signatures: Vec<String>,
}

async fn publish_result(
    State(st): State<AppState>,
    headers: HeaderMap,
    Path(world_id): Path<String>,
    Json(req): Json<PublishResultRequest>,
) -> Result<Json<WorldManifestV1>, StatusCode> {
    require_auth(&headers, &st.auth)?;
    let world_id = Uuid::parse_str(&world_id).map_err(|_| StatusCode::BAD_REQUEST)?;
    let manifest = st
        .store
        .set_token_info(
            world_id,
            req.network,
            req.mint,
            req.dbc_pool,
            req.tx_signatures,
        )
        .map_err(|e| {
            if e.to_string().contains("not found") {
                StatusCode::NOT_FOUND
            } else {
                StatusCode::INTERNAL_SERVER_ERROR
            }
        })?;
    Ok(Json(manifest))
}

async fn assistant_status(
    State(st): State<AppState>,
    headers: HeaderMap,
) -> Result<Json<assistant::AssistantStatus>, StatusCode> {
    require_auth(&headers, &st.auth)?;
    let status = assistant::status(&st.store)
        .await
        .map_err(|_| StatusCode::INTERNAL_SERVER_ERROR)?;
    Ok(Json(status))
}

#[derive(Debug, Serialize)]
struct AssistantConfigResponse {
    #[serde(skip_serializing_if = "Option::is_none")]
    provider: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    codex_model: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    codex_reasoning_effort: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    claude_model: Option<String>,
    avatar_mesh_enabled: bool,
}

async fn get_assistant_config(
    State(st): State<AppState>,
    headers: HeaderMap,
) -> Result<Json<AssistantConfigResponse>, StatusCode> {
    require_auth(&headers, &st.auth)?;
    let cfg = assistant::load_config(&st.store).map_err(|_| StatusCode::INTERNAL_SERVER_ERROR)?;
    Ok(Json(AssistantConfigResponse {
        provider: cfg.provider.map(|p| p.as_str().to_string()),
        codex_model: cfg.codex_model,
        codex_reasoning_effort: cfg.codex_reasoning_effort,
        claude_model: cfg.claude_model,
        avatar_mesh_enabled: cfg.avatar_mesh_enabled,
    }))
}

#[derive(Debug, Deserialize)]
struct SetAssistantConfigRequest {
    #[serde(default)]
    provider: Option<String>,
    #[serde(default)]
    codex_model: Option<String>,
    #[serde(default)]
    codex_reasoning_effort: Option<String>,
    #[serde(default)]
    claude_model: Option<String>,
    #[serde(default)]
    avatar_mesh_enabled: Option<bool>,
}

fn normalize_optional_string(v: Option<String>) -> Option<String> {
    v.and_then(|s| {
        let t = s.trim().to_string();
        if t.is_empty() || t == "default" {
            None
        } else {
            Some(t)
        }
    })
}

async fn set_assistant_config(
    State(st): State<AppState>,
    headers: HeaderMap,
    Json(req): Json<SetAssistantConfigRequest>,
) -> Result<Json<AssistantConfigResponse>, StatusCode> {
    require_auth(&headers, &st.auth)?;

    let mut cfg =
        assistant::load_config(&st.store).map_err(|_| StatusCode::INTERNAL_SERVER_ERROR)?;

    if let Some(p) = req.provider {
        cfg.provider = match p.as_str() {
            "" => None,
            "codex" => Some(AssistantProviderId::Codex),
            "claude" => Some(AssistantProviderId::Claude),
            _ => return Err(StatusCode::BAD_REQUEST),
        };
    }

    if req.codex_model.is_some() {
        cfg.codex_model = normalize_optional_string(req.codex_model);
    }
    if req.codex_reasoning_effort.is_some() {
        let v = normalize_optional_string(req.codex_reasoning_effort);
        if let Some(ref e) = v {
            match e.as_str() {
                "low" | "medium" | "high" | "very_high" | "xhigh" => {}
                _ => return Err(StatusCode::BAD_REQUEST),
            }
        }
        cfg.codex_reasoning_effort = v.map(|e| {
            if e == "very_high" {
                "xhigh".to_string()
            } else {
                e
            }
        });
    }
    if req.claude_model.is_some() {
        cfg.claude_model = normalize_optional_string(req.claude_model);
    }
    if let Some(v) = req.avatar_mesh_enabled {
        cfg.avatar_mesh_enabled = v;
    }

    assistant::save_config(&st.store, &cfg).map_err(|_| StatusCode::INTERNAL_SERVER_ERROR)?;

    Ok(Json(AssistantConfigResponse {
        provider: cfg.provider.map(|p| p.as_str().to_string()),
        codex_model: cfg.codex_model,
        codex_reasoning_effort: cfg.codex_reasoning_effort,
        claude_model: cfg.claude_model,
        avatar_mesh_enabled: cfg.avatar_mesh_enabled,
    }))
}

#[derive(Debug, Deserialize)]
struct SetProviderRequest {
    provider: String,
}

async fn set_provider(
    State(st): State<AppState>,
    headers: HeaderMap,
    Json(req): Json<SetProviderRequest>,
) -> Result<StatusCode, StatusCode> {
    require_auth(&headers, &st.auth)?;

    let provider = match req.provider.as_str() {
        "codex" => AssistantProviderId::Codex,
        "claude" => AssistantProviderId::Claude,
        _ => return Err(StatusCode::BAD_REQUEST),
    };

    let mut cfg =
        assistant::load_config(&st.store).map_err(|_| StatusCode::INTERNAL_SERVER_ERROR)?;
    cfg.provider = Some(provider);
    assistant::save_config(&st.store, &cfg).map_err(|_| StatusCode::INTERNAL_SERVER_ERROR)?;
    Ok(StatusCode::NO_CONTENT)
}

#[derive(Debug, Deserialize)]
struct AssistantChatRequest {
    message: String,
    #[serde(default)]
    profile_id: Option<String>,
}

#[derive(Debug, Serialize)]
struct AssistantChatResponse {
    reply: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    avatar: Option<AvatarSpecV1>,
}

async fn assistant_chat(
    State(st): State<AppState>,
    headers: HeaderMap,
    Json(req): Json<AssistantChatRequest>,
) -> Result<Json<AssistantChatResponse>, StatusCode> {
    require_auth(&headers, &st.auth)?;

    let cfg = assistant::load_config(&st.store).map_err(|_| StatusCode::INTERNAL_SERVER_ERROR)?;
    if cfg.provider.is_none() {
        return Err(StatusCode::PRECONDITION_FAILED);
    };

    let profile_id = req.profile_id.as_deref().unwrap_or("local");
    let out = assistant::companion_chat(&st.store, &cfg, profile_id, &req.message)
        .await
        .map_err(|e| {
            error!("assistant chat failed: {e:#}");
            StatusCode::INTERNAL_SERVER_ERROR
        })?;

    Ok(Json(AssistantChatResponse {
        reply: out.reply,
        avatar: out.avatar,
    }))
}

#[derive(Debug, Deserialize)]
struct AvatarGenerateRequest {
    prompt: String,
    #[serde(default)]
    profile_id: Option<String>,
}

#[derive(Debug, Serialize)]
struct AvatarGenerateResponse {
    avatar: AvatarSpecV1,
}

async fn get_avatar(
    State(st): State<AppState>,
    headers: HeaderMap,
) -> Result<Json<Option<AvatarSpecV1>>, StatusCode> {
    require_auth(&headers, &st.auth)?;
    let avatar = avatar_mod::load_avatar(&st.store, "local")
        .map_err(|_| StatusCode::INTERNAL_SERVER_ERROR)?;
    Ok(Json(avatar))
}

async fn generate_avatar(
    State(st): State<AppState>,
    headers: HeaderMap,
    Json(req): Json<AvatarGenerateRequest>,
) -> Result<Json<AvatarGenerateResponse>, StatusCode> {
    require_auth(&headers, &st.auth)?;

    let cfg = assistant::load_config(&st.store).map_err(|_| StatusCode::INTERNAL_SERVER_ERROR)?;
    if cfg.provider.is_none() {
        return Err(StatusCode::PRECONDITION_FAILED);
    };

    let avatar = avatar_mod::generate_avatar(&st.store, &cfg, &req.prompt)
        .await
        .map_err(|e| {
            error!("avatar generation failed: {e:#}");
            StatusCode::INTERNAL_SERVER_ERROR
        })?;

    let profile_id = req.profile_id.as_deref().unwrap_or("local");
    avatar_mod::save_avatar(&st.store, profile_id, &avatar).map_err(|e| {
        error!("saving avatar failed: {e:#}");
        StatusCode::INTERNAL_SERVER_ERROR
    })?;

    Ok(Json(AvatarGenerateResponse { avatar }))
}

#[derive(Debug, Deserialize)]
struct AvatarMeshGenerateRequest {
    prompt: String,
    #[serde(default)]
    profile_id: Option<String>,
}

#[derive(Debug, Serialize)]
struct AvatarMeshGenerateResponse {
    avatar: AvatarSpecV1,
}

async fn generate_avatar_mesh(
    State(st): State<AppState>,
    headers: HeaderMap,
    Json(req): Json<AvatarMeshGenerateRequest>,
) -> Result<Json<AvatarMeshGenerateResponse>, StatusCode> {
    require_auth(&headers, &st.auth)?;

    let cfg = assistant::load_config(&st.store).map_err(|_| StatusCode::INTERNAL_SERVER_ERROR)?;
    if cfg.provider.is_none() {
        return Err(StatusCode::PRECONDITION_FAILED);
    };

    let profile_id = req.profile_id.as_deref().unwrap_or("local");

    let avatar = avatar_mesh_mod::generate_avatar_mesh(&st.store, &cfg, profile_id, &req.prompt)
        .await
        .map_err(|e| {
            error!("avatar mesh generation failed: {e:#}");
            StatusCode::INTERNAL_SERVER_ERROR
        })?;

    Ok(Json(AvatarMeshGenerateResponse { avatar }))
}

#[derive(Debug, Deserialize)]
struct WorldPlanRequest {
    prompt: String,
}

#[derive(Debug, Serialize)]
struct WorldPlanResponse {
    plan: world_plan_mod::WorldPlanV1,
}

async fn generate_world_plan(
    State(st): State<AppState>,
    headers: HeaderMap,
    Json(req): Json<WorldPlanRequest>,
) -> Result<Json<WorldPlanResponse>, StatusCode> {
    require_auth(&headers, &st.auth)?;

    let cfg = assistant::load_config(&st.store).map_err(|_| StatusCode::INTERNAL_SERVER_ERROR)?;
    if cfg.provider.is_none() {
        return Err(StatusCode::PRECONDITION_FAILED);
    };

    let plan = world_plan_mod::generate_world_plan(&st.store, &cfg, &req.prompt)
        .await
        .map_err(|e| {
            error!("world plan generation failed: {e:#}");
            StatusCode::INTERNAL_SERVER_ERROR
        })?;

    Ok(Json(WorldPlanResponse { plan }))
}

#[derive(Debug, Deserialize)]
struct AvatarMeshQuery {
    #[serde(default)]
    profile_id: Option<String>,
    #[serde(default)]
    part: Option<String>,
}

async fn get_avatar_mesh(
    State(st): State<AppState>,
    headers: HeaderMap,
    axum::extract::Query(q): axum::extract::Query<AvatarMeshQuery>,
) -> Result<axum::response::Response, StatusCode> {
    require_auth(&headers, &st.auth)?;
    let profile_id = q.profile_id.as_deref().unwrap_or("local");
    let part = q.part.as_deref();
    let exists = match part {
        None => avatar_mesh_mod::avatar_mesh_exists(&st.store, profile_id),
        Some("body") => avatar_mesh_mod::avatar_mesh_exists(&st.store, profile_id),
        Some(p) => avatar_mesh_mod::avatar_mesh_part_exists(&st.store, profile_id, p),
    };
    if !exists {
        return Err(StatusCode::NOT_FOUND);
    }
    let bytes = avatar_mesh_mod::read_mesh_bytes(&st.store, profile_id, part)
        .map_err(|_| StatusCode::INTERNAL_SERVER_ERROR)?;

    Ok((
        StatusCode::OK,
        [(axum::http::header::CONTENT_TYPE, "application/octet-stream")],
        bytes,
    )
        .into_response())
}

pub async fn serve(
    listen: String,
    store: WorldStore,
    auth: AuthMode,
    discovery: DiscoveryConfig,
) -> Result<()> {
    let addr: SocketAddr = listen.parse().context("parse listen addr")?;

    let cors = CorsLayer::new()
        .allow_methods(Any)
        .allow_headers(Any)
        .allow_origin(Any);

    let app = Router::new()
        .route("/health", get(health))
        .route("/assistant/status", get(assistant_status))
        .route("/assistant/provider", post(set_provider))
        .route(
            "/assistant/config",
            get(get_assistant_config).post(set_assistant_config),
        )
        .route("/assistant/chat", post(assistant_chat))
        .route("/avatar", get(get_avatar))
        .route("/avatar/generate", post(generate_avatar))
        .route("/avatar/mesh", get(get_avatar_mesh))
        .route("/avatar/mesh/generate", post(generate_avatar_mesh))
        .route("/world/plan", post(generate_world_plan))
        .route("/worlds", get(list_worlds).post(create_world))
        .route("/discovery/worlds", get(discovery_worlds))
        .route("/worlds/:world_id/manifest", get(get_manifest))
        .route("/worlds/:world_id/publish-result", post(publish_result))
        .with_state(AppState {
            store,
            auth,
            discovery,
        })
        .layer(cors);

    info!("OWP admin API listening on http://{addr}");
    axum::serve(tokio::net::TcpListener::bind(addr).await?, app).await?;
    Ok(())
}

async fn discovery_worlds(
    State(st): State<AppState>,
    headers: HeaderMap,
) -> Result<Json<Vec<WorldDirectoryEntry>>, StatusCode> {
    require_auth(&headers, &st.auth)?;

    let Some(rpc_url) = st.discovery.solana_rpc_url.as_deref() else {
        return Err(StatusCode::PRECONDITION_FAILED);
    };
    let Some(program_id) = st.discovery.registry_program_id.as_deref() else {
        return Err(StatusCode::PRECONDITION_FAILED);
    };

    let worlds = owp_discovery::fetch_worlds_from_rpc(rpc_url, program_id)
        .await
        .map_err(|e| {
            error!("discovery fetch failed: {e:#}");
            StatusCode::INTERNAL_SERVER_ERROR
        })?;

    Ok(Json(worlds))
}
