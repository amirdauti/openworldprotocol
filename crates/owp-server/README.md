# owp-server

Rust local world server for OWP.

Responsibilities (MVP):
- Create/load/save worlds under `~/.owp/worlds/<world_id>/`
- Listen on configured port(s) and accept client connections
- Serve world manifests, chunks, and entity replication
- Enforce host-only admin actions (generation jobs)

Status: scaffold only.

