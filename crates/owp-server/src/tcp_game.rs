use anyhow::{Context, Result};
use owp_protocol::{wire, Message, Welcome, OWP_PROTOCOL_VERSION};
use std::net::SocketAddr;
use tokio::net::{TcpListener, TcpStream};
use tracing::{info, warn};
use uuid::Uuid;

use crate::storage::WorldStore;

pub async fn serve(store: WorldStore, world_id: Uuid, listen: Option<String>) -> Result<()> {
    let world_dir = store.world_dir(world_id);
    if !world_dir.exists() {
        anyhow::bail!("world not found: {world_id}");
    }
    let manifest = store.read_manifest(&world_dir)?;

    let listen = match listen {
        Some(v) => v,
        None => format!("0.0.0.0:{}", manifest.ports.game_port),
    };
    let addr: SocketAddr = listen.parse().context("invalid listen addr")?;
    let listener = TcpListener::bind(addr).await.context("bind")?;
    info!("OWP game server listening on tcp://{addr} (world_id={world_id})");

    loop {
        let (stream, peer) = listener.accept().await.context("accept")?;
        let store = store.clone();
        tokio::spawn(async move {
            if let Err(e) = handle_connection(store, world_id, stream, peer).await {
                warn!("connection error from {peer}: {e:#}");
            }
        });
    }
}

async fn handle_connection(
    store: WorldStore,
    world_id: Uuid,
    mut stream: TcpStream,
    peer: SocketAddr,
) -> Result<()> {
    let msg = wire::read_message(&mut stream)
        .await
        .context("read hello")?;
    let (request_id, requested_world) = match msg {
        Message::Hello(h) => (h.request_id, h.world_id),
        other => {
            warn!("unexpected first message from {peer}: {other:?}");
            return Ok(());
        }
    };

    if let Some(w) = requested_world {
        if w != world_id {
            warn!("world_id mismatch from {peer}: requested={w} served={world_id}");
            let welcome = Message::Welcome(Welcome {
                protocol_version: OWP_PROTOCOL_VERSION.to_string(),
                request_id,
                world_id,
                token_mint: None,
                motd: Some("World id mismatch".to_string()),
                capabilities: vec![],
            });
            wire::write_message(&mut stream, &welcome).await?;
            return Ok(());
        }
    }

    let world_dir = store.world_dir(world_id);
    let manifest = store.read_manifest(&world_dir)?;
    let token_mint = manifest.token.as_ref().map(|t| t.mint.clone());

    let welcome = Message::Welcome(Welcome {
        protocol_version: OWP_PROTOCOL_VERSION.to_string(),
        request_id,
        world_id,
        token_mint,
        motd: Some("Welcome to OWP (handshake-only server)".to_string()),
        capabilities: vec!["handshake".to_string()],
    });
    wire::write_message(&mut stream, &welcome).await?;
    Ok(())
}
