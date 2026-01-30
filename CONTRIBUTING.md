# Contributing

Thanks for helping build OWP — The Open World Protocol.

## What belongs in this repo

This is the open-source monorepo for:
- the Unity client
- the Rust world server + protocol crates
- discovery/registry code
- **interfaces + mocks** for token-per-world launch flows

The Meteora DBC launchpad implementation is **private** and intentionally gitignored.

## How to contribute

1. Open an issue describing the change (or pick an existing one).
2. Keep PRs small and focused.
3. Update docs when behavior changes (`README.md`, `docs/`, `STATUS.md`).

## Security boundary (important)

Generation is executed by spawning local CLIs (Claude/Codex). That is powerful and dangerous if exposed.

- Do not add code paths that allow remote players to trigger generation jobs.
- Prefer explicit host-only admin commands.
- Add timeouts, size limits, and a job queue whenever invoking external tools.

## Repository layout

See `README.md` and `docs/ARCHITECTURE.md`.
