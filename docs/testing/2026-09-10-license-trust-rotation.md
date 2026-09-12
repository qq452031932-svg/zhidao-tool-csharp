# Production license trust and rotation evidence

Status: tooling implemented; exact installed public identities and authorized local/staging rehearsal evidence are pending.

The immutable shipping origin is `https://ota.fanfh.cn`; SuperPan uses product `8270e8d7-1791-487d-ad76-0da7a0cbed12`. The non-shipping smoke client uses a distinct immutable product ID and shares only the installed public response-signing identity and the current/next TLS SPKI pins. Card input is stdin-only. The boundary probe is visibly non-shipping and alone permits injected UTC plus an absolute DPAPI store root.

The build wrapper accepts a Windows certificate-store thumbprint only. It accepts no PFX, certificate file, password, private key, endpoint override, product override, machine fixture, or feed destination. It requires a clean exact source commit, signs the three executables, independently verifies Authenticode signer, SHA-256 policy and timestamp presence, and emits hashes plus public identifiers. Smoke and probe directories must never be copied into the OTA stable feed.

Authorized evidence checklist:

- source commit and three artifact SHA-256 values
- signer thumbprint and timestamp policy result
- installed response-signing key ID, canonical SPKI SHA-256 and challenge transcript SHA-256
- distinct TLS SPKI A/B/C hashes
- A-to-B-to-A probe result and B-to-C next manifest
- rollback certificate identifier, custodian approval reference and destruction date at least 90 days away
- confirmation that no legacy licensing client population exists at initial launch

Never record card values, request bodies, SQLite/DPAPI contents, passwords, cookies, private PEM/PFX bytes, or private-key paths.

Run both scripts under PowerShell 7.2 or newer. The rotation command consumes the two exact redacted build manifests (A/B and B/C), the installed public signing identity, and a non-secret challenge plus server signature. It first verifies the ECDSA P-256 challenge, then observes the endpoint certificate directly through a chain-validated `SslStream` while the authorized operator switches A to B and rolls back to A. It writes evidence only after all three observed pins and the B/C manifest chain agree.
