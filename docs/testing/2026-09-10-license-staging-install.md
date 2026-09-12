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

On the approved disposable snapshot, invoke
`deploy/license-service/test-install-v219.sh` separately for `first-install`,
`upgrade`, `restart`, `rollback`, and each enumerated `failpoint`. Pass the same
explicit installer arguments used by the reviewed preflight. Each install or
failpoint invocation must create exactly one backup below the fixed backup root.

The failpoint allowlist is: `identity`, `secret-encryption`, `secret-lookup`,
`secret-admin`, `secret-signing`, `secret-publish`, `backup`, `license-binary`,
`license-config`, `license-unit`, `license-start`, `license-health`, `ota-binary`,
`ota-config`, `ota-unit`, `ota-restart`, and `ota-health`. For every injected
failure, record that the six service payloads and two unit states were restored,
or that rollback explicitly retained the sole backup and left both services
stopped for manual recovery. Never mark a failure as passed merely because it
returned nonzero.

The staging run must also prove the upgrade snapshot/checkpoint sequence, new
binary migration on temporary loopback port `28092`, old-binary read compatibility,
normal loopback health on `18092`, controlled OTA readiness on `18091`, explicit
rollback, and restart survival. Do not install or read any public license route,
alter a virtual host, or reload the web server during this matrix.

## Evidence to record

Record only command exit status, artifact hashes, owner/group/mode, listener addresses, health status, and rollback result. Never record card values, machine hashes, tokens, private keys, cookies, or response bodies.

## Approval boundary

Before any staging SSH connection, obtain explicit approval for the exact disposable host and snapshot. Production hostname, keys, certificates, and databases are out of scope for this rehearsal.
