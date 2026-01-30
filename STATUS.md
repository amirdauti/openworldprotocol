# Status

Current focus: **Milestone A — Foundations**

## Deliverables checklist

### Monorepo scaffolding
- [ ] Cargo workspace root
- [ ] pnpm workspace + monorepo orchestrator (Turbo/Nx)
- [x] Progress docs (`README.md`, `ROADMAP.md`, `STATUS.md`, protocol/architecture/security docs)

### Protocol + SDK
- [ ] Protocol spec v0.1 (handshake + core messages)
- [ ] Rust `owp-protocol` crate (types + encoding/decoding)
- [ ] Compatibility tests across versions

### Rust world server
- [ ] Server CLI: create world / run server / config
- [ ] Persistence layout under `~/.owp/worlds/<world_id>/`
- [ ] Listen on configured port(s)
- [ ] Chunk streaming skeleton
- [ ] Host-only admin permissions (generation jobs)

### Unity client
- [ ] Home screen + quick-join connect string
- [ ] Universe map (local list first)
- [ ] Handshake implementation
- [ ] Chunk/entity streaming + basic rendering loop
- [ ] Chat

### Generation (local Claude/Codex)
- [ ] World workspace layout + manifest format
- [ ] Claude CLI adapter (cwd-based)
- [ ] Codex CLI adapter (cwd-based)
- [ ] Job queue + timeouts + output size limits

### Discovery
- [ ] On-chain registry program (recommended)
- [ ] Registry client reader (Unity + Rust)
- [ ] Optional HTTP registry (self-hostable)

### Directory (private web app)
- [ ] Directory UI for worlds/tokens at `openworldprotocol.com` (private repo)

### Solana + token-per-world
- [ ] Public interface boundary + mock provider
- [ ] Private Meteora DBC launchpad repo integration (gitignored)

## Working notes

- “No scanning / no prompt guides” refers to UX and moderation policy; it does **not** remove basic security controls.
- Remote players must never be able to influence prompts that trigger host CLI execution.
