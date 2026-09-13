# Production license trust and rotation evidence

Status: **PRODUCTION REHEARSAL PASSED (2026-09-13)**.

Observed immutable source commit: `63c72139a02ece768a82bcc1b2bc8fa15c0b6f90`.
The installed response signer is
`license-signing-78287173058d823d1f7cb9ede78c8098`, with public SPKI SHA-256
`78287173058d823d1f7cb9ede78c80983fe6a191932d4d572a410bf0a1a99402`.
A fresh SSH challenge was signed by the installed private key and verified by the
exact signed clients; the redacted challenge transcript hash is
`c0ee630e6d43d6646aeb5e25bbeec0bd96b6796667bb01934efa16200ea13fe8`.

TLS SPKI A/B/C were distinct and chain-valid:

- A: `6ca6442aae242a1b19ed325e526da73a6b15bf4437d63fab04438f3e2e32decf`
- B: `597c1712399c4171a0633e60aa51743240f17369e7675b70ced384deac5c074b`
- C: `f03ce760da0ac669a03d604840d1f0ceb90575698e7ff2fa1dd21e0846dc7e40`

The signed A/B client passed A, B, and rollback A. The independently signed B/C
client then passed B and C. Nginx configuration tests passed before every reload,
the master PID remained `9117`, and the live vhost was restored byte-for-byte to A.
The signed boundary probe authorized one second before the 30-minute lease boundary
and denied one second after it. No legacy licensing client population exists for this
initial rollout.

Windows artifacts use the self-signed code-signing certificate
`CN=SuperPan OTA Publisher` (thumbprint
`5AE52053A2D7E548E1FA747DF3C6313167668F40`) with an RFC3161 SHA-256 timestamp.
This deliberately does not claim commercial reputation: untrusted Windows machines
may show a SmartScreen/security warning, and the certificate is not silently installed
on customer machines.

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

Run both scripts under PowerShell 7.2 or newer. The build is made from a `git archive` snapshot of the exact clean commit; its detached certificate-store CMS manifest covers every output file. The rotation command requires the externally approved signing-certificate thumbprint, verifies both signed manifests against it, and checks all hashes plus the fixed three Authenticode identities and timestamp/SHA-256 policy. It creates a fresh nonce/endpoint/commit/time-bound challenge and asks the named installed host, through fixed SSH commands, to sign it without exporting private material. It then runs the exact signed smoke client through A, B, and rollback A while independently observing each chain-valid TLS SPKI, runs the exact signed offline boundary probe, and accepts the signed B/C manifest. Evidence is written only after every check succeeds.
