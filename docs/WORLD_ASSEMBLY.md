# World Assembly (LLM Plan → Asset Catalog → Unity)

OWP’s goal is “prompt anything” worlds, but **raw mesh generation** (LLM outputting vertices/coordinates) produces bland results and is hard to control.

The current MVP uses a hybrid approach:

1) User writes a freeform prompt (“a neon forest with a ruined portal…”)
2) The host’s local Codex/Claude produces a **structured world plan** (JSON)
3) Unity **assembles** the scene using a built-in **prefab catalog** + lightweight procedural meshes + a small starter pack of textures

This keeps UX “prompt anything” while keeping the visuals grounded in assets that can look good.

## API

Server endpoint (host-only admin API):

- `POST /world/plan` → `{ plan: WorldPlanV1 }`

Request:

```json
{ "prompt": "…" }
```

The response is intended to be consumed by the Unity client at runtime and should be stable across minor versions.

## Prefab Catalog (MVP)

The plan references only these prefab ids:

- `tower`
- `house`
- `ruins`
- `camp`
- `portal`
- `tree`
- `rock`
- `crystal`
- `lamp`

Unity maps these to runtime prefab builders (procedural composites). Later, these can point to real CC0/OSS asset packs.

## Extending visuals

Recommended path to “real” immersive worlds:

1) Ship a small built-in open-licensed starter pack (textures + a few simple meshes)
2) Support drop-in asset packs (CC0/OSS) placed under a known folder
3) Have the LLM reference assets by id and place them via plans

That way, better visuals come from better assets, and the LLM acts as a **director** (composition + placement), not a mesh generator.

