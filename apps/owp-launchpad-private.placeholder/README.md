# Private launchpad + landing (placeholder)

`openworldprotocol.com` is intended to run a single private Next.js app that provides:
- OWP landing page
- a directory/explorer of worlds and their world-tokens

The real app lives in a private repo cloned into:

`apps/owp-launchpad-private/`

That folder is gitignored by design.

## Open-source boundary

The public repo should only contain:
- interface definitions (Rust trait or local HTTP API) for token creation + registry publishing
- mock providers for local development

## Local publish flow (v0)

The open-source repo includes a local-only host admin API (`owp-server admin`) that the private app can call in **admin mode** to:
- list local worlds
- read a world manifest
- persist the token mint/pool back into the manifest after wallet-signed creation

See `docs/PUBLISH_FLOW.md`.
