use anyhow::{Context, Result};
use base64::Engine;
use borsh::BorshDeserialize;
use owp_protocol::WorldDirectoryEntry;
use owp_registry::state::{read_fixed_string, WorldEntry};
use serde::Deserialize;
use serde_json::json;
use uuid::Uuid;

#[derive(Debug, Clone, Deserialize)]
struct RpcResponse<T> {
    result: T,
}

#[derive(Debug, Clone, Deserialize)]
struct ProgramAccount {
    #[allow(dead_code)]
    pubkey: String,
    account: ProgramAccountData,
}

#[derive(Debug, Clone, Deserialize)]
struct ProgramAccountData {
    data: (String, String),
}

/// Fetch all published worlds from a Solana RPC via `getProgramAccounts`.
pub async fn fetch_worlds_from_rpc(rpc_url: &str, registry_program_id: &str) -> Result<Vec<WorldDirectoryEntry>> {
    let client = reqwest::Client::new();

    let body = json!({
      "jsonrpc": "2.0",
      "id": 1,
      "method": "getProgramAccounts",
      "params": [
        registry_program_id,
        { "encoding": "base64" }
      ]
    });

    let resp = client
        .post(rpc_url)
        .json(&body)
        .send()
        .await
        .context("rpc request")?
        .error_for_status()
        .context("rpc status")?;

    let parsed: RpcResponse<Vec<ProgramAccount>> = resp.json().await.context("rpc parse")?;

    let mut out = Vec::new();
    for acc in parsed.result {
        let (data_b64, _encoding) = acc.account.data;
        let data = base64::engine::general_purpose::STANDARD
            .decode(data_b64)
            .context("base64 decode")?;

        let entry = match WorldEntry::try_from_slice(&data) {
            Ok(v) => v,
            Err(_) => continue,
        };

        let world_id = Uuid::from_bytes(entry.world_id);
        let name = read_fixed_string(&entry.name);
        let endpoint = read_fixed_string(&entry.endpoint);

        let token_mint = if entry.token_mint == [0u8; 32] {
            None
        } else {
            Some(bs58::encode(entry.token_mint).into_string())
        };
        let dbc_pool = if entry.dbc_pool == [0u8; 32] {
            None
        } else {
            Some(bs58::encode(entry.dbc_pool).into_string())
        };

        let world_pubkey = Some(bs58::encode(entry.authority).into_string());

        out.push(WorldDirectoryEntry {
            world_id,
            name,
            endpoint,
            port: entry.game_port,
            token_mint,
            dbc_pool,
            world_pubkey,
            last_seen: Some(entry.last_update_slot.to_string()),
        });
    }

    Ok(out)
}
