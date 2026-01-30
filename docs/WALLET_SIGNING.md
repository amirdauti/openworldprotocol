# Wallet signing model (draft)

OWP needs a host-signed transaction flow for “token-per-world” launches.

## Recommendation: wallet adapter (default)

Default approach:
- Token/pool creation is initiated by the host (Unity + local server flow).
- Signing happens in the private Next.js app using a wallet adapter (Phantom/Solflare/etc).
- After success, the app calls back into the host (local admin API) to persist:
  - token mint
  - pool address
  - tx signatures

Benefits:
- avoids storing secrets in the game client
- aligns with normal Solana security expectations
- easier to rotate/upgrade wallets

## Optional: seed phrase import (high risk)

Importing a seed phrase into the Unity client can enable one-click signing, but it is high-risk:
- malware / memory scraping risk on the host machine
- accidental logging/screenshot risk
- harder to provide strong guarantees to users

If implemented at all, treat it as:
- **advanced mode**
- explicit warnings and user confirmation
- never accessible to remote clients

Minimum safeguards:
- do not transmit seed phrases to any LLM
- store secrets only in OS keychain / secure enclave when possible
- encrypt at rest with a user-chosen passphrase
- provide “lock/unlock” and session timeout

## Devnet-first and mainnet opt-in

Token launches should default to devnet during early development.
Mainnet should be opt-in and gated behind explicit acknowledgement of fees/risk.

