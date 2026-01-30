# owp-registry (Solana program)

On-chain registry for OWP worlds.

This program stores a list of published worlds (endpoint + ports + mint/pool) so clients can render a “universe map” without relying on a centralized directory.

## Accounts

Each world is a PDA account derived from the world id:

- Seeds: `["world", <world_id_16_bytes>]`
- Address: `Pubkey::find_program_address(seeds, program_id)`

Account data is a fixed-size `WorldEntry` (Borsh).

## Instructions (MVP)

- `RegisterWorld` — create + initialize a world entry PDA (authority-signed)
- `UpdateWorld` — authority updates fields (endpoint/ports/token/metadata)
- `DelistWorld` — authority closes the entry (drains lamports)

## Build (dev)

Host build:

- `cargo build -p owp-registry`

SBF build (requires Solana toolchain):

- `cargo build-sbf -p owp-registry`

Program id is configured at deploy time; clients should treat it as configuration.

