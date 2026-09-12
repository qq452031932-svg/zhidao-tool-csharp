# License staging installation rehearsal

This document is a redacted runbook for a disposable staging host. It is not an authorization to connect to any host.

## Local gates

Run from a clean, reviewed commit:

```powershell
& .tools\go-1.27.1\go\bin\go.exe -C services\ota_admin test ./cmd/license-db-snapshot ./internal/deploymentcontract -count=1
git diff --check
```

These local checks validate source, script structure, Windows-compatible fixtures,
and focused Go migrations. They do **not** mean the v219 Linux rehearsal passed.

The public Nginx include must remain absent during installation. The service is reachable only at `127.0.0.1:18092` until a separately approved routing change.

## Disposable-host prerequisites

- CentOS 7 or equivalent systemd v219 fixture
- Restorable VM snapshot taken immediately before installation
- Bundle and configuration hashes recorded without secrets
- Dedicated `superpan-license` user, group, data, and maintenance roots
- The authenticated `license-db-snapshot`, its SHA-256 sidecar, and
  `source-commit.txt` installed below `/opt/superpan-license-maintenance`; the
  commit must equal the installation bundle commit

## Authorized Linux staging matrix

On the approved disposable snapshot, provide a reviewed root-owned fixture driver
with `reset SCENARIO` and `seed-wal` commands, then run the single matrix entry
point:

```bash
sudo bash deploy/license-service/test-install-v219.sh \
  --bundle /root/reviewed-license-bundle \
  --fixture-vhost /root/fixtures/centos7-v219-driver
```

`--fixture-vhost` is a canonical absolute path to that executable fixture driver,
not a public route or configuration path. The harness requires it and every
ancestor to be root-owned and non-writable by group/other, and requires the file
to be regular, non-symlinked, single-linked, and executable. Its `reset` operation
restores the disposable fixture before each independent scenario; `seed-wal`
prepares an old-version active service with a coherent WAL database. The harness
derives all explicit hash and template arguments from the reviewed bundle and
deploy tree and does not discover ambient lifecycle commands from `PATH`.

The entry point automatically runs first install, WAL upgrade, explicit rollback,
restart, all 17 failpoints, and normal retries after `secret-publish`,
`license-binary`, `license-config`, `license-unit`, `license-start`,
`license-health`, and every `ota-*` failpoint. Each installer invocation must
create exactly one backup below the fixed backup root.

The failpoint allowlist is: `identity`, `secret-encryption`, `secret-lookup`,
`secret-admin`, `secret-signing`, `secret-publish`, `backup`, `license-binary`,
`license-config`, `license-unit`, `license-start`, `license-health`, `ota-binary`,
`ota-config`, `ota-unit`, `ota-restart`, and `ota-health`. For every injected
failure, record that the six service payloads and two unit states were restored,
or that rollback explicitly retained the sole backup and left both services
stopped for manual recovery. Never mark a failure as passed merely because it
returned nonzero.

Every successful scenario also verifies binary/config/unit owner, group and mode;
enabled/active state; listener address, MainPID and `/proc/<pid>/exe` hash; and
database/key pairing. A complete key set with an absent database is accepted as a
retryable secret-publication state. A complete valid key set and database with no
old binary is accepted as `interrupted-initial` and verified with the authenticated
new binary against a coherent snapshot; partial keys or a database without all
valid keys fail the matrix.

The staging run must also prove the upgrade snapshot/checkpoint sequence, new
binary migration on temporary loopback port `28092`, old-binary read compatibility,
normal loopback health on `18092`, controlled OTA readiness on `18091`, explicit
rollback, and restart survival. Do not install or read any public license route,
alter a virtual host, or reload the web server during this matrix.

## Evidence to record

Record only command exit status, artifact hashes, owner/group/mode, listener addresses, health status, and rollback result. Never record card values, machine hashes, tokens, private keys, cookies, or response bodies.

## Approval boundary

Before any staging SSH connection, obtain explicit approval for the exact disposable host and snapshot. Production hostname, keys, certificates, and databases are out of scope for this rehearsal.
