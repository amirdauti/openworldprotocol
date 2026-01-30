# owp-discovery

Discovery clients for populating the “Universe Map”.

Planned modes:
- On-chain registry reader (Solana)
- Optional HTTP registry reader

Current:
- Solana on-chain registry reader via JSON-RPC `getProgramAccounts` (see `fetch_worlds_from_rpc`).
