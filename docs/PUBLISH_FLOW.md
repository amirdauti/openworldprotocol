# Publish flow (draft)

This doc describes the first “host publish” flow that connects:
- the **local Rust host** (world server)
- the **private Next.js app** (wallet adapter signing + directory UI)

## Goals

- Token creation is **host-triggered** and **wallet-signed** by the host in the private app.
- The public deployment acts as a directory UI only (no public launch actions).
- The host can persist the resulting `{mint, pool, signatures}` into the local world manifest.

## Components

- Host API: `owp-server admin` (local-only) on `127.0.0.1:9333`
- Private app proxy routes (server-side):
  - `GET /api/owp/host/worlds`
  - `GET /api/owp/host/worlds/:worldId/manifest`
  - `POST /api/owp/host/worlds/:worldId/publish-result`

## Local setup (admin mode)

1. Start host admin API:

   - `cargo run -p owp-server -- admin`

   On first run it generates an admin token at `~/.owp/admin-token`.

2. Configure the private app:

   - copy env and set:
     - `OWP_ENABLE_ADMIN=1`
     - `NEXT_PUBLIC_OWP_ENABLE_ADMIN=1`
     - `OWP_HOST_API_URL=http://127.0.0.1:9333`
     - `OWP_ADMIN_TOKEN=<contents of ~/.owp/admin-token>`

3. Run the private app and visit `/worlds`.

4. Use “Publish” to open the world-scoped launch flow:
   - `/worlds` → “Publish” → “Launch token for this world”
   - After successful creation, the launch UI auto-persists `{mint, pool, signature}` back to the host manifest.

## Notes

- The host admin API should remain bound to `127.0.0.1` unless you add strong auth + explicit user intent.
- “Attach token” remains as a manual fallback, but “Publish” is the preferred path.
