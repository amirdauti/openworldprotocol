# Security model (draft)

OWP is designed for **user-hosted worlds** where the host machine may run powerful local tools (Claude CLI / Codex CLI) to generate content. This requires a strict trust boundary.

## Non-negotiable: host authority

- Remote clients must **never** be able to:
  - trigger generation jobs
  - supply prompts that are executed by host CLIs
  - write arbitrary files to the host filesystem
- Remote clients may only request:
  - world/chunk/entity data the server is willing to serve
  - chat/input messages within protocol limits

## Generation job constraints (stability + safety)

Even without moderation policies, generation must be operationally constrained:

- Per-job timeout
- Max stdout/stderr capture size
- Max file count/size written inside a world workspace directory
- Serialized job queue (do not block simulation tick)
- Per-job logs for audit/debugging

## Network exposure

- Worlds are reachable via direct connection on known ports.
- NAT/port forwarding is expected for most home networks unless you add UPnP or relays later.
- Assume hostile internet traffic: rate-limit, validate message sizes, and reject unknown protocol versions.

## Key management

- Each world should have a locally-generated “world authority” keypair used to sign manifests and (optionally) registry updates.
- Never transmit private keys to clients.

