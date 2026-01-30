use anyhow::{Context, Result};
use axum::{
    extract::{Path, State},
    http::{HeaderMap, StatusCode},
    routing::{get, post},
    Json, Router,
};
use owp_protocol::{WorldDirectoryEntry, WorldManifestV1};
use serde::{Deserialize, Serialize};
use std::net::SocketAddr;
use tower_http::cors::{Any, CorsLayer};
use tracing::info;
use uuid::Uuid;

use crate::storage::WorldStore;

#[derive(Clone)]
pub enum AuthMode {
    Disabled,
    BearerToken(String),
}

#[derive(Clone)]
struct AppState {
    store: WorldStore,
    auth: AuthMode,
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

pub async fn serve(listen: String, store: WorldStore, auth: AuthMode) -> Result<()> {
    let addr: SocketAddr = listen.parse().context("parse listen addr")?;

    let cors = CorsLayer::new()
        .allow_methods(Any)
        .allow_headers(Any)
        .allow_origin(Any);

    let app = Router::new()
        .route("/health", get(health))
        .route("/worlds", get(list_worlds).post(create_world))
        .route("/worlds/:world_id/manifest", get(get_manifest))
        .route("/worlds/:world_id/publish-result", post(publish_result))
        .with_state(AppState { store, auth })
        .layer(cors);

    info!("OWP admin API listening on http://{addr}");
    axum::serve(tokio::net::TcpListener::bind(addr).await?, app).await?;
    Ok(())
}
