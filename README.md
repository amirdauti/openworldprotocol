# OWP — The Open World Protocol

OWP is an open protocol + reference implementation for a “multiverse” of user-hosted worlds:

- A user runs an **OWP World Server** locally (Rust)
- Their server uses **local Claude CLI and/or Codex CLI** to generate the world + content inside a world workspace directory
- Other users join via **direct connection** (no relay by default) on known ports
- Each world can auto-launch a **world-specific Solana token** via **Meteora Dynamic Bonding Curve (DBC)** (implemented in a **private**, gitignored launchpad repo)

Project status: early scaffolding. Track progress in `STATUS.md`.

## Core components

- `apps/owp-unity/` — Unity client (OWP Client)
  - Universe map (“planets”), connect by address, chunk/entity streaming, basic gameplay loop + chat/UI hooks
  - Avatar “prompt anything” (hybrid): Codex/Claude generates OpenSCAD → server renders STL → Unity displays mesh (requires `openscad` installed)
- `crates/owp-server/` — Rust local world server (OWP World)
  - Simulation + persistence + networking; runs generation jobs (Claude/Codex) inside a world workspace directory
- `crates/owp-protocol/` — Protocol types + encoding/decoding shared by client/server
- `programs/owp-registry/` — Solana on-chain registry program (world directory)
- `crates/owp-discovery/` — Discovery clients (on-chain registry client + optional HTTP registry client)
- `apps/owp-launchpad-private/` — **PRIVATE** Next.js app for `openworldprotocol.com` (landing + world/token directory; gitignored; see placeholder instructions)

## Connect string (draft)

```
owp://<ip_or_dns>:<port>?world=<world_id>&mint=<token_mint>&pubkey=<world_pubkey>
```

## Host authority (non-negotiable)

OWP supports “no scanning / no prompt guides” UX, but it still enforces a hard trust boundary:

- **Only the host** can trigger world/content generation jobs.
- Remote players can request streaming content, but **cannot** cause the host machine to run CLI actions.

Details: `SECURITY.md`.

## Repo docs

- `ROADMAP.md` — milestone plan (A→E)
- `STATUS.md` — deliverables checklist + what’s currently in progress
- `docs/ARCHITECTURE.md` — system architecture overview
- `docs/protocol/v0.1.md` — protocol spec (draft)
- `docs/LAUNCHPAD_DIRECTORY.md` — directory + token-launch integration boundary (draft)
- `docs/PUBLISH_FLOW.md` — host publish flow (draft)
- `docs/CLIENT_UX.md` — client onboarding + assistant UX (draft)
- `docs/ASSISTANT_PROVIDERS.md` — Codex/Claude provider selection + execution (draft)
- `docs/REGISTRY_ONCHAIN.md` — on-chain world registry (draft)
- `docs/WALLET_SIGNING.md` — wallet signing model + security notes (draft)
- `CONTRIBUTING.md` — contribution guidelines

## Private launchpad boundary

This open-source repo defines the interface boundary for “token-per-world” launch flows, but the actual Meteora DBC launchpad implementation lives in a private repo that is intentionally **not** committed here.

See `apps/owp-launchpad-private.placeholder/README.md`.
