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
  "assistant": {
    "provider": "codex"
  }
}
```

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

