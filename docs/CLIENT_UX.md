# Client UX (draft)

This doc describes the intended first-run experience for the Unity client, including avatar customization, the in-client AI companion, world creation, and the token launch prompt.

## High-level flow

1. Launch Unity client
2. Client starts the local Rust server process (`owp-server`) if it is not running
3. Client displays Home/Character screen (avatar preview + customization)
4. User opens AI companion (top-right “orb”) and chooses provider (Codex/Claude) on first run
5. User creates a world (via UI or companion)
6. After first world version is generated, user is prompted to connect a wallet and launch the world token (host-signed)
7. User enters/join world

## Screen: Home / Character

Goals:
- show the user “their presence” immediately (avatar preview)
- provide a “safe” place to customize identity before joining worlds

Requirements:
- Avatar preview in a neutral/futuristic environment (generic background)
- Basic customization (skin tone, hair, outfit presets, colors)
- Save/load local profile

Implementation note (current):
- The avatar is a procedural placeholder; “prompt anything” visuals require either a kitbash library (VRM) or an eventual text-to-3D pipeline.

## AI companion (“orb”)

The top-right UI element is a futuristic “floating blob/orb” that opens a chat panel.

Naming:
- Do not call it “Jarvis”.
- Use a project name like **Companion**, **Wisp**, **Beacon**, or **Guide** until final branding is chosen.

### First-run provider selection

If the user has never initiated a local CLI session:
- Prompt: “Choose your assistant provider: Codex or Claude”
- Show a short explanation of what each option means (local CLI requirement, auth)
- Store the choice locally (see `docs/ASSISTANT_PROVIDERS.md`)

### Switching providers

In Settings:
- allow switching between Codex and Claude
- show “installed / not installed” and auth status
- switching should not break existing worlds; it only affects new generation jobs

Also in Settings (MVP):
- allow selecting the **model** for Codex and Claude
- allow selecting Codex **reasoning effort** (low/medium/high/xhigh)

### Assistant onboarding message

On first open (and accessible later):
- friendly message describing capabilities:
  - create/edit avatar (generates avatar config, textures, or preset suggestions)
  - create/edit worlds (biomes/POIs/rules, content generation)
  - help publish worlds (host-only flow)

## World creation UX (host-only)

World creation should be possible via:
- a “Create World” button in UI, and/or
- asking the companion in chat

Behind the scenes:
- Unity sends a host-only request to the local server
- server creates a world workspace and runs generation via the selected provider

Security rule:
- remote players must never be able to trigger generation or token launch

## Token launch prompt (after world creation)

Trigger:
- after the first successful world version exists (manifest/chunks/assets generated), prompt the host to launch the token.

Default (recommended):
- wallet signing occurs via the private Next.js app (wallet adapter)
- Unity deep-links or embeds the launchpad experience for signing

Optional (advanced / risky):
- allow importing a seed phrase inside the client for one-click signing
- see security notes in `docs/WALLET_SIGNING.md`

## Startup behavior: client launches server

On client startup:
- if `owp-server` isn’t running, spawn it
- poll a local health endpoint until ready
- if server fails to start, show a clear error and a “View logs” button

The long-term goal is “one-click host”.
