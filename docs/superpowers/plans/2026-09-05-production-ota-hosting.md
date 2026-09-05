# Production OTA Hosting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deploy an isolated HTTPS static host for `ota.fanfh.cn` without changing the existing `f.fanfh.cn` application or document root.

**Architecture:** A dedicated BaoTa/Nginx virtual host serves only `/www/wwwroot/ota.fanfh.cn`. Stable releases live below `releases/win-x64/stable`; mutable metadata and immutable versioned assets use separate cache policies. Candidate configuration is validated before reload, and the existing site is checked before and after.

**Tech Stack:** CentOS 7, BaoTa, Nginx 1.30.4, ACME TLS, Velopack static feed.

---

### Task 1: Capture baseline and backup configuration

**Files:**
- Read: `/www/server/nginx/conf/nginx.conf`
- Read: `/www/server/panel/vhost/nginx/f.fanfh.cn.conf`
- Create: `/root/ota-deploy-backup-<timestamp>/`

- [ ] Record current `f.fanfh.cn` HTTP status, response hash, certificate identity, Nginx config test, listeners and existing vhost hashes.
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
- [ ] Atomically place it in the included vhost directory.
- [ ] Run `/www/server/nginx/sbin/nginx -t`; on failure restore the backup without reload.
- [ ] Reload Nginx only after a successful config test.
- [ ] Verify `http://ota.fanfh.cn/` serves the isolated health page while `f.fanfh.cn` remains unchanged.

### Task 4: Issue TLS and apply production cache policy

**Files:**
- Create: `/www/server/panel/vhost/cert/ota.fanfh.cn/fullchain.pem`
- Create: `/www/server/panel/vhost/cert/ota.fanfh.cn/privkey.pem`
- Modify: `/www/server/panel/vhost/nginx/ota.fanfh.cn.conf`

- [ ] Use the existing ACME client when available; otherwise install the minimum supported client.
- [ ] Issue a certificate only for `ota.fanfh.cn` using the isolated webroot.
- [ ] Restrict private-key permissions.
- [ ] Add HTTPS, HSTS, GET/HEAD-only static serving, metadata no-cache rules and versioned-asset immutable rules.
- [ ] Validate config before reload and verify certificate hostname, SNI and expiry.

### Task 5: Verify isolation and record deployment

**Files:**
- Modify: `docs/testing/2026-09-01.md`

- [ ] Verify OTA HTTP redirect, HTTPS 200, 404 isolation, headers and disabled directory listing.
- [ ] Re-run the complete `f.fanfh.cn` baseline checks and compare hashes/certificate.
- [ ] Confirm the stable directory contains no acceptance package or partial production release.
- [ ] Record server paths, certificate expiry, checks and remaining production-signing/release work.
- [ ] Commit only the documentation update.
