# Architecture overview (draft)

OWP is an open protocol + reference implementation for a “multiverse” of user-hosted worlds.

## Components

### Unity client (OWP Client)
- Universe map (“planets”)
- Connect to a world by address
- Stream world chunks/entities
- Basic gameplay loop + currency UI hooks

### Rust world server (OWP World)
- Runs locally on the host PC
- Owns simulation + persistence + networking
- Hosts world assets/chunks
- Runs generation jobs by spawning Claude CLI / Codex CLI inside a world working directory

### OWP protocol + SDK
- Versioned protocol spec (handshake, messages, assets, chunk streaming)
- Shared message types for client/server

### Discovery (“Universe Map” source)
Two compatible modes:
1. On-chain registry (Solana-first; recommended)
2. Optional HTTP registry (self-hostable directory service / static JSON)

### Solana token + Meteora DBC + Launchpad (private)
- Each world can create a token via Meteora DBC pool creation (DBC mints a new token during pool creation).
- The launchpad implementation (which also serves as the public landing + directory UI) is private and gitignored; the open-source repo ships interface boundaries + mocks.
