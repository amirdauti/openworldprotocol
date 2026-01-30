# On-chain world registry (draft)

OWP needs a discovery source so a fresh client can render a “universe map” of worlds without relying on a centralized directory.

This repo ships an **optional** Solana program (`programs/owp-registry/`) that stores world listings on-chain.

## What the registry stores

The registry stores **structured fields**, not an opaque connect string:

- `world_id` (UUID bytes)
- `authority` (wallet pubkey bytes; only this key can update/delist)
- `name`
- `endpoint` (DNS or IP)
- `game_port` (+ optional `asset_port`)
- `token_mint` (+ optional `dbc_pool`)
- `metadata_uri` (off-chain JSON pointer)
- `last_update_slot`

Clients derive the connect string from these fields:

`owp://<endpoint>:<game_port>?world=<world_id>&mint=<token_mint>&pubkey=<authority>`

## Program design

- One PDA account per world (`WorldEntry`)
- PDA seeds: `["world", <world_id_16_bytes>]`
- Fixed-size account layout (Borsh). Optional values use sentinel encodings:
  - `asset_port == 0` means “none”
  - `token_mint` / `dbc_pool` all-zero pubkey bytes mean “none”

## Write flow (recommended)

Register/update the world in the registry **after** the token launch succeeds.

Typical flow:
1. Host creates a world locally (Rust server)
2. Private launchpad app creates the DBC pool (wallet-signed) → obtains `{mint, pool}`
3. Private launchpad app submits an `UpdateWorld` (or `RegisterWorld`) tx to the registry
4. Unity clients read the registry and display the world

## Read flow

- Rust: `crates/owp-discovery/` can read the registry via Solana JSON-RPC `getProgramAccounts`
- Web (private launchpad): read via Solana RPC and render directory UI

## Notes / caveats

- The registry provides discovery only. It does **not** solve NAT/port-forwarding.
- Storing a heartbeat (`last_seen`) on-chain is expensive; prefer updating only on publish/major updates.
- Program id is configured at deploy time; treat it as a configurable value in clients.

