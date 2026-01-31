# owp-unity

Unity client for OWP.

Planned MVP:
- Universe map (“planets”)
- Quick-join by connect string
- Handshake + chunk/entity streaming
- Basic in-world HUD (chat + currency UI hooks)
- Home/Character screen + avatar preview/customization
- Top-right AI companion “orb” for avatar/world creation (Codex/Claude)

Status: early MVP scaffolding (runs in Editor).

What works today:
- Spawns the local Rust server (`owp-server`) automatically
- Lists + creates local worlds and can host a handshake-only game server for connectivity testing
- AI Companion chat backed by local Codex CLI or Claude Code CLI
- Provider selection + model settings (Codex model + reasoning effort, Claude model)
- Avatar preview: procedural placeholder with structured `parts` (primitive kitbash with optional emission)
- Built-in “starter pack” look: pattern textures + archetype styling inferred from `avatar.tags` (see `Assets/OWPStarterPack/`)

## Run (developer)

1. Build the local server:

   - `cargo build -p owp-server`

2. Open `apps/owp-unity/` in Unity (2022.3 LTS recommended; tested with 2022.3.62f3).

3. Press Play.

The client bootstraps:
- spawns `owp-server admin --listen 127.0.0.1:9333 --no-auth` from `../../target/debug/owp-server`
- prompts you to choose Codex/Claude (first run)
- lets you switch provider via the **Provider** button in the chat panel
- lets you chat with the Companion; it calls `POST /assistant/chat` and applies any returned avatar update
- lets you create a local world via the **Worlds** panel and run a handshake-only TCP server (`owp-server run`) for basic connectivity testing

Admin API endpoints used by the Unity client:
- `GET /assistant/status`
- `POST /assistant/provider`
- `GET /assistant/config`
- `POST /assistant/config`
- `POST /assistant/chat` (primary UX)
- `POST /avatar/generate` (legacy / back-compat)
- `GET /worlds`
- `POST /worlds`
- `GET /discovery/worlds` (optional)

## On-chain discovery (optional)

The Worlds panel can toggle its source to **On-chain**. This calls the local admin API `GET /discovery/worlds`, which reads the Solana registry via RPC.

To enable it, start Unity with these environment variables set (so the spawned `owp-server` inherits them):

- `OWP_SOLANA_RPC_URL` (example: `https://api.devnet.solana.com`)
- `OWP_REGISTRY_PROGRAM_ID` (the deployed `owp-registry` program id)

### Server path override

Set `OWP_SERVER_PATH` in your environment to point Unity to the server binary if you want a custom location.
