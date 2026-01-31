# OWP Starter Pack (MVP)

This folder contains the **built-in** “starter pack” used by the Unity client to make *prompted* avatars look more varied than a plain capsule + sphere.

## What it includes (today)

- A small set of **procedural avatar archetypes** (humanoid/robot/Na’vi-ish/etc.)
- A lightweight “kitbash” system using:
  - `avatar.parts` (primitive attachments like horns, wings, tail, visor, etc.)
  - a few built-in **pattern textures** under `Assets/Resources/OWPStarterPack/Textures/`

## How it’s used

The local server’s Companion (Codex/Claude) returns an `avatar` object. Unity renders:

- base body + head (simple primitives), plus
- any `avatar.parts` attachments, plus
- a “look” (textures/material params) inferred from `avatar.tags`

This is intentionally **not** full text-to-3D mesh generation yet; it’s an MVP “prompt anything → visible change” foundation.

## License

See `LICENSE.txt`.

