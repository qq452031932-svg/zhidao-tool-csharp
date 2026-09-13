# SuperPan production license acceptance

Status: **PASSED — 2026-09-13**

## Installed identity

- Host: `ota.fanfh.cn` on CentOS 7 / systemd 219.
- Binary source commit: `63c72139a02ece768a82bcc1b2bc8fa15c0b6f90`.
- License binary SHA-256: `624658db19833d60be0b2ebb2b8e60129fdd0bfd26d2ddaf47a8e1bea997afff`.
- OTA admin SHA-256: `bb917d27e74e86c582393c6cbe0d744565252f5772a5682787708f01f3e67351`.
- Response-signing key ID: `license-signing-78287173058d823d1f7cb9ede78c8098`.
- Response-signing public SPKI SHA-256:
  `78287173058d823d1f7cb9ede78c80983fe6a191932d4d572a410bf0a1a99402`.
- Install transaction backup:
  `/var/backups/superpan-license/20260913T135013Z-9356-10803`.
- Pre-route baseline:
  `/var/backups/superpan-license-route/baseline-20260913T132533Z.bZHS3R`.

Private signing keys, TLS private keys, bearer credentials, card values, machine hashes,
request bodies, databases, and passwords are intentionally absent from this report.

## Acceptance results

- Both services were installed from the same immutable source commit and are active.
  Listeners are restricted to `127.0.0.1:18091` and `127.0.0.1:18092`.
- Capacity passed at 50 and 100 RPS with zero unexpected failures, zero health failures,
  and zero SQLite busy/locked events; detailed aggregate figures are in the capacity
  report.
- The public Nginx route exposes only activation and validation, adds `no-store`, and
  does not expose internal health/admin routes. Route verification deliberately stopped
  and recovered both services while proving the OTA health and stable feed stayed intact.
- The route was restored to its pre-route vhost hash
  `23046876bdd83b91eb64156c6b8a1ec7eaa321bcb42f14db3dcedbab58971786`,
  then reapplied and fully reverified. Nginx master PID remained `9117`.
- A→B→A and B→C TLS trust rehearsals passed with exact Authenticode-signed clients;
  the final live certificate was restored to A.
- Multi-product flow passed: create software/package/card, activate both products,
  package remove/re-add (`app_not_allowed` then `ok`), software disable/re-enable
  (`app_disabled` then `ok`), 30-day renewal, unbind, and rebind.
- With only the license service stopped and automatic recovery already armed, the signed
  client remained authorized from its cached lease. The signed offline boundary probe
  authorized at lease boundary minus one second and denied at plus one second.
- The deployment-smoke card was deleted and its root-only plaintext evidence file was
  securely removed. The two fixed smoke software records remain disabled (count 2), and
  the package remains as `DEPLOYMENT-SMOKE-RETAINED-20260913` (count 1), as required.

## Windows signing decision

The three Windows executables and their CMS manifests are signed and timestamped with
the self-signed `CN=SuperPan OTA Publisher` certificate. This is an identity/integrity
control, not purchased reputation. Windows may warn users until they explicitly trust
the publisher; the application does not install the certificate into a customer's trust
store.

## Final live state

`superpan-license.service` and `superpan-ota-admin.service` are active, the formal TLS A
certificate is live, and the verified public license route is enabled. No unrelated
site, OTA stable artifact, database, Nginx location, or server project was removed.
