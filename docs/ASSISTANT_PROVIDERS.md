# Assistant providers (Codex / Claude) (draft)

OWP supports world/content generation by spawning a local CLI inside a world workspace directory. The Unity client exposes this via an in-client AI companion UI.

## Provider model

Providers:
- `codex` (Codex CLI)
- `claude` (Claude Code CLI)

Where it runs:
- Always on the **host machine**
- Always scoped to a world workspace directory

Non-negotiable security:
- Remote clients cannot supply prompts that trigger host CLI execution.

## First-run selection

If no provider is configured, the client prompts the user to choose:
- “Codex” or “Claude”

The server stores this choice locally (example config file):
- `~/.owp/config.json`

Example shape:

```json
{
  "provider": "codex",
  "codex_model": "gpt-5.2-codex",
  "codex_reasoning_effort": "xhigh",
  "claude_model": "sonnet",
  "avatar_mesh_enabled": true
}
```

Notes:
- `codex_model` is passed to `codex exec --model <MODEL>` (string).
- `codex_reasoning_effort` is passed to Codex via `-c model_reasoning_effort="low|medium|high|xhigh"`.
  - The Unity UI uses `very_high` but stores/sends `xhigh`.
- `claude_model` is passed to Claude via `claude --model <MODEL>` (string).
  - Common aliases include `haiku`, `sonnet`, `opus` (availability varies by account).
- `avatar_mesh_enabled` enables the **hybrid “prompt anything” avatar pipeline**:
  - Companion prompt → provider generates OpenSCAD code (JSON schema output)
  - server runs `openscad` headlessly to render `avatar.stl`
  - Unity downloads the STL and displays it at runtime

Requirements (for mesh avatars):
- `openscad` must be installed and available on `PATH` on the host machine.
  - macOS (Apple Silicon): Homebrew currently installs an Intel build and requires Rosetta 2:
    - `brew install openscad`
    - `softwareupdate --install-rosetta --agree-to-license`

## Switching providers

Switching providers should:
- be explicit in Settings
- affect only future generation jobs
- keep existing worlds intact

## Provider health checks (recommended)

The local server should expose a host-only endpoint to report:
- is CLI installed?
- is auth configured?
- last run status

This is used by the Unity client to present “ready / not ready” status.

Current admin API:
- `GET /assistant/status` → current provider + install checks
- `POST /assistant/provider` → sets provider (`codex` or `claude`)
- `GET /assistant/config` → reads provider/model settings
- `POST /assistant/config` → updates provider/model settings
- `POST /assistant/chat` → companion chat backed by local CLI; returns `{ reply, avatar? }`
- `POST /avatar/mesh/generate` → (optional) generates avatar mesh directly from a prompt
- `GET /avatar/mesh?profile_id=...` → downloads STL bytes for the current avatar mesh

`POST /assistant/chat` also persists chat history under:
- `~/.owp/profiles/<profile_id>/companion_history.json`

## Execution constraints (stability)

When spawning a provider process:
- per-job timeout
- max stdout/stderr capture
- max file count/size written inside workspace
- serialized job queue (do not block simulation)
- per-job logs persisted under `~/.owp/worlds/<world_id>/logs/`

## “No prompt guides” UX rule

The UI should not force prompt templates or prompt guides.
Internally the server still writes structured outputs (manifest, chunks, assets), but the user experience remains freeform.
