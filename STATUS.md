# Status

Current focus: **Milestone A — Foundations**

## Deliverables checklist

### Monorepo scaffolding
- [x] Cargo workspace root
- [ ] pnpm workspace + monorepo orchestrator (Turbo/Nx)
- [x] Progress docs (`README.md`, `ROADMAP.md`, `STATUS.md`, protocol/architecture/security docs)

### Protocol + SDK
- [~] Protocol spec v0.1 (handshake + core messages)
- [~] Rust `owp-protocol` crate (manifest + directory types scaffolded)
- [ ] Compatibility tests across versions
- [x] Handshake message framing + HELLO/WELCOME

### Rust world server
- [~] Server CLI: create world / run server / config
- [x] Host admin API (local-only) for listing worlds + attaching token info + assistant endpoints
- [~] Persistence layout under `~/.owp/worlds/<world_id>/`
- [~] Listen on configured port(s)
- [x] Game port TCP listener (handshake-only)
- [ ] Chunk streaming skeleton
- [~] Host-only admin permissions (generation jobs)

### Unity client
- [ ] Home screen + quick-join connect string
- [ ] Universe map (local list first)
- [x] Handshake implementation
- [ ] Chunk/entity streaming + basic rendering loop
- [~] Chat (Companion chat implemented; in-world chat TBD)
- [x] Spawn local `owp-server` process (one-click host)
- [~] Avatar preview + customization scene (procedural kit + starter pack textures)
- [~] Avatar mesh rendering (STL download + runtime loader; OpenSCAD pipeline)
- [~] World plan + scene assembly (LLM world plan → prefab catalog → runtime scene)
- [x] AI companion (“orb”) UI + provider selection/settings (models + reasoning effort)

### Generation (local Claude/Codex)
- [ ] World workspace layout + manifest format
- [~] World plan generation (structured output) for runtime scene assembly
- [~] Claude CLI adapter (structured output; avatar + companion chat)
- [~] Codex CLI adapter (structured output; avatar + companion chat)
- [~] OpenSCAD avatar mesh generation (structured OpenSCAD → headless render → STL)
- [ ] Job queue + timeouts + output size limits

### Discovery
- [~] On-chain registry program (recommended)
- [~] Registry client reader (Unity + Rust)
- [ ] Optional HTTP registry (self-hostable)

### Directory (private web app)
- [ ] Directory UI for worlds/tokens at `openworldprotocol.com` (private repo)

### Solana + token-per-world
- [ ] Public interface boundary + mock provider
- [ ] Private Meteora DBC launchpad repo integration (gitignored)

## Working notes

- “No scanning / no prompt guides” refers to UX and moderation policy; it does **not** remove basic security controls.
- Remote players must never be able to influence prompts that trigger host CLI execution.
