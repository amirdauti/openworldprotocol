# Development (WIP)

This repo is early scaffolding. The goal is a monorepo containing:
- Rust world server + protocol crates (Cargo workspace)
- Unity client project
- Optional HTTP registry service (pnpm workspace)

## Prerequisites (planned)

- Rust toolchain (stable)
- Unity Hub + a pinned Unity version (TBD)
- Node.js + pnpm (TBD, only if working on the optional registry or private launchpad)

## Local development (planned)

### Rust

- Build server: `cargo build -p owp-server`
- Run server: `cargo run -p owp-server -- ...`
- Run handshake client: `cargo run -p owp-client-cli -- --connect owp://127.0.0.1:7777?world=<world_id>`
- Run tests: `cargo test`

### Web

- Install deps: `pnpm install`
- Dev server: (TBD)

Note: the `openworldprotocol.com` landing + directory UI lives in a **private** Next.js app that is not committed here.

### Unity

- Open `apps/owp-unity/` in Unity
- Connect to a world via connect string (see `docs/protocol/v0.1.md`)

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
