# License staging installation rehearsal

This document is a redacted runbook for a disposable staging host. It is not an authorization to connect to any host.

## Local gates

Run from a clean, reviewed commit:

```powershell
& .tools\go-1.27.1\go\bin\go.exe -C services\ota_admin test ./cmd/license-db-snapshot ./internal/deploymentcontract -count=1
git diff --check
```

The public Nginx include must remain absent during installation. The service is reachable only at `127.0.0.1:18092` until a separately approved routing change.

## Disposable-host prerequisites

- CentOS 7 or equivalent systemd v219 fixture
- Restorable VM snapshot taken immediately before installation
- Bundle and configuration hashes recorded without secrets
- Dedicated `superpan-license` user, group, data, and maintenance roots

## Evidence to record

Record only command exit status, artifact hashes, owner/group/mode, listener addresses, health status, and rollback result. Never record card values, machine hashes, tokens, private keys, cookies, or response bodies.

## Approval boundary

Before any staging SSH connection, obtain explicit approval for the exact disposable host and snapshot. Production hostname, keys, certificates, and databases are out of scope for this rehearsal.
