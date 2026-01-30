# Launchpad directory + token launch flow (draft)

The domain `openworldprotocol.com` is intended to run a single Next.js app that acts as:
- a landing page for OWP
- a directory/explorer of worlds and their world-tokens

This web app is **private** and not committed in the open-source repo.

## Launchpad UX (directory, not “self-serve launch”)

The launchpad app should **not** let random visitors click “Launch token”.
Instead it should focus on:
- listing worlds (metadata, endpoints, last seen)
- listing tokens/pools created per world (mint + pool + links)
- world pages (manifest summary, provenance, token stats, join/connect info)

## Token creation trigger (host-driven)

Token creation should be triggered as part of the **world creation/publish flow** on the host machine:

1. Host creates a world locally (Rust server).
2. Host optionally runs local generation (Claude/Codex) to produce the first world version.
3. Host triggers a “publish” action that can:
   - create a Meteora DBC pool (which mints the token)
   - register the world in the discovery registry (on-chain recommended)
4. The directory app reads registry state to render the “universe” list.

Important: even if an LLM is involved in the workflow, **only the host** should be allowed to initiate token creation and sign transactions.

## Integration boundary (open-source vs private)

Open-source should define a narrow interface (Rust trait or local HTTP API) that looks like:
- `create_world_pool(manifest) -> { mint, pool, signatures }`

Then:
- Public repo provides interface definitions + a mock provider.
- Private repo provides the real provider that talks to Meteora DBC and handles wallet signing.

## Clarifications needed

To implement this cleanly, decide:
- Where does the “publish” action live? (Unity client? CLI? server admin UI?)
- How does the host sign? (wallet adapter in private Next.js app vs local keypair vs hardware wallet)
- Is token creation required for every world, or opt-in per world? (devnet-first vs mainnet opt-in)

