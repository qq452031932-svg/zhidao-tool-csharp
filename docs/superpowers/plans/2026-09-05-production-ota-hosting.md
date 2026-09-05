# Production OTA Hosting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Phase 1 deploys an isolated HTTPS static host for `ota.fanfh.cn` without changing the existing `f.fanfh.cn` application or document root; the stable channel remains empty and closed until a separate signed production-publication task implements the release gate.

**Architecture:** A dedicated BaoTa/Nginx virtual host serves only `/www/wwwroot/ota.fanfh.cn`. Stable releases live below `releases/win-x64/stable`; mutable metadata and immutable versioned assets use separate cache policies. Candidate configuration is validated before reload, and the existing site is checked before and after.

**Tech Stack:** CentOS 7, BaoTa, Nginx 1.30.4, ACME TLS, Velopack static feed.

---

### Task 1: Capture baseline and backup configuration

**Files:**
- Read: `/www/server/nginx/conf/nginx.conf`
- Read: `/www/server/panel/vhost/nginx/f.fanfh.cn.conf`
- Create: `/root/ota-deploy-backup-<timestamp>/`

- [ ] Record current `f.fanfh.cn` status, selected static/header evidence, certificate identity, Nginx config test, listeners and existing vhost hashes using local SNI/`--resolve` requests rather than assuming public routing.
- [ ] Copy Nginx main/vhost configuration into a timestamped root-only backup directory.
- [ ] Verify backup files exist and hashes match the originals.

### Task 2: Create isolated OTA filesystem

**Files:**
- Create: `/www/wwwroot/ota.fanfh.cn/index.html`
- Create: `/www/wwwroot/ota.fanfh.cn/.well-known/acme-challenge/`
- Create: `/www/wwwroot/ota.fanfh.cn/releases/win-x64/stable/`

- [ ] Create directories owned by the Nginx-serving account with non-world-writable permissions.
- [ ] Add a minimal health page that contains no existing-site content.
- [ ] Verify no path resolves inside `/www/wwwroot/f.fanfh.cn`.

### Task 3: Install and validate the independent vhost

**Files:**
- Create: `/www/server/panel/vhost/nginx/ota.fanfh.cn.conf`

- [ ] Write an HTTP-only candidate vhost with ACME exception, OTA root and no default-site fallback.
- [ ] Atomically place it in the included vhost directory while retaining whether the previous state was absent or present.
- [ ] Run `/www/server/nginx/sbin/nginx -t`; on failure restore the exact previous/absent state, test again and do not reload.
- [ ] Reload Nginx only after a successful config test.
- [ ] Verify `http://ota.fanfh.cn/` serves the isolated health page while `f.fanfh.cn` remains unchanged.

### Task 4: Issue TLS and apply production cache policy

**Files:**
- Create: `/www/server/panel/vhost/cert/ota.fanfh.cn/fullchain.pem`
- Create: `/www/server/panel/vhost/cert/ota.fanfh.cn/privkey.pem`
- Modify: `/www/server/panel/vhost/nginx/ota.fanfh.cn.conf`

- [ ] Use the existing ACME client when available; otherwise install the minimum supported client. Allow only `certonly`/webroot mode for the OTA webroot; prohibit Nginx installer/edit/reload modes.
- [ ] Issue a certificate only for `ota.fanfh.cn` using the isolated webroot and copy/link only its resulting certificate into the OTA certificate directory.
- [ ] Restrict private-key permissions.
- [ ] Add HTTPS, HSTS, GET/HEAD-only static serving, explicit metadata no-cache rules, versioned-asset immutable rules, fixed Setup/Portable no-cache rules, and an OTA-only 503 isolation fallback.
- [ ] Atomically replace the HTTP candidate with HTTPS while retaining the previous HTTP vhost. On failure restore the HTTP vhost, remove the bad candidate, run `nginx -t` again and reload only the restored valid state.
- [ ] Validate config before reload and verify certificate hostname, SNI and expiry.
- [ ] Install a renewal deploy hook scoped to the OTA certificate that runs `nginx -t` before reload and fails closed; run the supported renewal dry-run/test and re-verify hostname, SNI and expiry.

### Task 5: Verify isolation and record deployment

**Files:**
- Modify: `docs/testing/2026-09-01.md`

- [ ] Verify with explicit local SNI/`--resolve` requests: OTA HTTP redirect; HTTPS health 200; unknown path 404; directory listing denied; POST rejected; release JSON, asset JSON, RELEASES and manifest no-cache; versioned asset immutable; fixed Setup/Portable no-cache.
- [ ] Re-run the complete `f.fanfh.cn` baseline checks with local SNI/`--resolve` and compare its configuration hash, certificate and selected response evidence.
- [ ] Confirm the stable directory contains no acceptance package, partial production release or feed metadata. Phase 1 completion does not authorize uploading stable assets.
- [ ] Record server paths, certificate expiry, checks and the separate required signed production-publication/release-gate task.
- [ ] Commit only the documentation update.
