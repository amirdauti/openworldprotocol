# Development (WIP)

This repo is early scaffolding. The goal is a monorepo containing:
- Rust world server + protocol crates (Cargo workspace)
- Unity client project
- Optional HTTP registry service (pnpm workspace)

## Prerequisites

- Rust toolchain (stable)
- Unity Hub + Unity **2022.3 LTS** (tested with **2022.3.62f3**)
- One (or both) local assistant CLIs:
  - Codex CLI (`codex`)
  - Claude Code CLI (`claude`)
- Node.js + pnpm (only if working on the optional registry or private launchpad)

## Local development

### Rust

- Build server: `cargo build -p owp-server`
- Run admin API (manual): `target/debug/owp-server admin --listen 127.0.0.1:9333 --no-auth`
- Run handshake-only game server (manual): `target/debug/owp-server run --world-id <world_id>`
- Run tests: `cargo test`

### Solana (optional)

On-chain discovery is provided by `programs/owp-registry/`.

- Host build: `cargo build --manifest-path programs/owp-registry/Cargo.toml`
- SBF build (requires Solana toolchain): `cargo build-sbf --manifest-path programs/owp-registry/Cargo.toml`

### Web

- Install deps: `pnpm install`
- Dev server: (TBD)

Note: the `openworldprotocol.com` landing + directory UI lives in a **private** Next.js app that is not committed here.

### Unity

- Open `apps/owp-unity/` in Unity
- Press Play (the client spawns `owp-server admin` automatically)
- Use the Companion panel to select provider/model and generate a procedural avatar

Environment variables (optional):
- `OWP_SERVER_PATH` — override server binary path for Unity to spawn
- `OWP_SOLANA_RPC_URL` + `OWP_REGISTRY_PROGRAM_ID` — enable on-chain world directory in the Worlds panel

## World workspace layout (planned)

World content is stored locally under:

`~/.owp/worlds/<world_id>/`

Subfolders:
- `manifest/`
- `chunks/`
- `assets/`
- `npcs/quests/`
- `snapshots/`
- `logs/`
