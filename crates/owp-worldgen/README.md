# owp-worldgen

World/content generation orchestration.

Planned features:
- Claude CLI adapter
- Codex CLI adapter
- Job queue + timeouts + output size limits
- Writes generated content into a world workspace directory

Security requirement:
- Only the host can run generation jobs; remote clients must never be able to trigger them.

Status: scaffold only.

