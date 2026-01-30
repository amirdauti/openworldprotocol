# owp-unity

Unity client for OWP.

Planned MVP:
- Universe map (“planets”)
- Quick-join by connect string
- Handshake + chunk/entity streaming
- Basic in-world HUD (chat + currency UI hooks)
- Home/Character screen + avatar preview/customization
- Top-right AI companion “orb” for avatar/world creation (Codex/Claude)

Status: scaffold only.

## Run (developer)

1. Build the local server:

   - `cargo build -p owp-server`

2. Open `apps/owp-unity/` in Unity (2022.3 LTS recommended).

3. Press Play.

The client bootstraps:
- spawns `owp-server admin --listen 127.0.0.1:9333 --no-auth` from `../../target/debug/owp-server`
- prompts you to choose Codex/Claude (first run)
- lets you switch provider via the **Provider** button in the chat panel
- lets you type a freeform avatar description; it calls `POST /avatar/generate` and applies the returned JSON to a placeholder avatar.

### Server path override

Set `OWP_SERVER_PATH` in your environment to point Unity to the server binary if you want a custom location.
