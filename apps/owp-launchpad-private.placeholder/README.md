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

See `docs/LAUNCHPAD_DIRECTORY.md`.

