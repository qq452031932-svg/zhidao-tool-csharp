# OTA Password Minimum Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Allow OTA administrator passwords with at least eight Unicode characters while leaving the 1024-byte maximum and every other security control unchanged.

**Architecture:** Keep the server as the authority: `security.HashPassword` counts Unicode code points for the minimum and UTF-8 bytes for the existing maximum. Mirror the same code-point check in the embedded browser UI so users receive immediate feedback without weakening server enforcement. Rebuild and redeploy only the isolated OTA admin service through the audited idempotent installer.

**Tech Stack:** Go 1.27, embedded HTML/JavaScript, Go unit/browser tests, PowerShell build scripts, systemd/Nginx isolated deployment.

---

### Task 1: Define the eight-character server contract

**Files:**
- Modify: `services/ota_admin/internal/security/security_test.go`
- Modify: `services/ota_admin/internal/httpapi/router_test.go`
- Modify: `services/ota_admin/internal/security/password.go`

- [ ] **Step 1: Write the failing password-boundary test**

Add a table-driven test that proves:

```go
tests := []struct {
    name     string
    password string
    accepted bool
}{
    {"seven ascii", "1234567", false},
    {"eight ascii", "12345678", true},
    {"seven unicode code points", "密码密码密码a", false},
    {"eight unicode code points", "密码密码密码ab", true},
    {"1024 utf8 bytes", strings.Repeat("密", 341) + "a", true},
    {"1025 utf8 bytes", strings.Repeat("密", 341) + "ab", false},
}
```

Hash accepted inputs with deterministic salt bytes and assert rejected inputs fail before hashing.

Add API/session cases in `router_test.go` for both states: the forced first change and a later voluntary change. In each state, submit a seven-code-point password and require `password_rejected`; submit an eight-code-point password and require success; then invalidate/logout the old session and prove a fresh login succeeds with the accepted password. Preserve the existing CSRF and session-rotation assertions.

- [ ] **Step 2: Run the focused test and verify RED**

Run: `& .tools/go-1.27.1/go/bin/go.exe -C services/ota_admin test ./internal/security ./internal/httpapi -run 'TestHashPasswordEnforcesCharacterMinimum|TestPasswordChangeEnforcesCharacterMinimum' -count=1`

Expected: FAIL because the current minimum is 16 bytes and byte length does not match Unicode code-point length.

- [ ] **Step 3: Implement the minimal server rule**

In `password.go`, introduce an accurately named minimum-character constant with value `8`, use `utf8.RuneCountInString(password)` for the lower bound, and retain `len(password) > MaximumPasswordBytes` for the existing UTF-8 byte maximum. Do not change password verification, Argon2id parameters, normalization, trimming, complexity, initial password generation, or current-password checks.

- [ ] **Step 4: Run the focused and session/API tests**

Run:

```powershell
& .tools/go-1.27.1/go/bin/go.exe -C services/ota_admin test ./internal/security ./internal/session ./internal/httpapi -count=1
```

Expected: PASS.

- [ ] **Step 5: Commit the server contract**

Stage only the three files and commit: `fix: allow eight-character OTA passwords`.

### Task 2: Align the embedded Chinese web page

**Files:**
- Modify: `services/ota_admin/internal/webui/assets_test.go`
- Modify: `services/ota_admin/internal/webui/browser_test.go`
- Modify: `services/ota_admin/internal/webui/static/index.html`
- Modify: `services/ota_admin/internal/webui/static/app.js`

- [ ] **Step 1: Write failing embedded-asset and browser tests**

Require the HTML to say `至少 8 位`, forbid the old `至少 16 位` and `minlength="16"`, and require a client helper that counts code points with `Array.from(value).length`. Extend the real-browser test so a seven-code-point Unicode password is rejected without calling the password endpoint, while an eight-code-point Unicode password reaches the endpoint and completes the existing forced-password-change flow.

- [ ] **Step 2: Run the focused web tests and verify RED**

Run: `& .tools/go-1.27.1/go/bin/go.exe -C services/ota_admin test ./internal/webui -run 'TestEmbeddedAdminPageHasCompleteAccessibleChineseWorkflow|TestAdminConsoleInRealBrowser' -count=1`

Expected: FAIL on the old 16-character HTML/UI behavior.

- [ ] **Step 3: Implement minimal browser validation**

Remove the contradictory native `minlength="16"`, change help text to `请使用至少 8 位、与临时密码不同的密码。`, and add a small code-point-count helper. At the start of `changePassword`, reject fewer than eight code points with the same Chinese guidance, focus the new-password field, and do not send a request. Keep `maxlength="1024"` only as a coarse browser input cap; the server remains authoritative for the 1024 UTF-8-byte maximum, and the UI does not claim byte-limit parity. Preserve all existing field clearing, CSRF, error handling, and session behavior.

- [ ] **Step 4: Run the web tests**

Run: `& .tools/go-1.27.1/go/bin/go.exe -C services/ota_admin test ./internal/webui -count=1`

Expected: PASS, including the real Chrome workflow.

- [ ] **Step 5: Commit the web alignment**

Stage only the four web files and commit: `fix: align OTA password guidance`.

### Task 3: Verify, rebuild, and deploy the isolated service

**Files:**
- Generated only: `.artifacts/ota-admin/*`
- Update after evidence: `docs/testing/2026-09-05-web-ota-publisher-acceptance.md`

- [ ] **Step 1: Run complete local verification**

Run:

```powershell
& .tools/go-1.27.1/go/bin/go.exe -C services/ota_admin test ./... -count=1
& .tools/go-1.27.1/go/bin/go.exe -C services/ota_admin vet ./...
powershell -File services/ota_admin/scripts/build_linux_amd64.ps1
```

Expected: all tests and vet pass; official Linux binary/source outputs are rebuilt atomically with recorded SHA-256 values.

- [ ] **Step 2: Re-baseline production read-only**

Record current `f.fanfh.cn` vhost/certificate/body hashes, OTA `/health`, OTA vhost, service state, loopback listener, and empty production release tree. In a new root-only `0700` temporary baseline directory, copy the current database, HMAC file, initial-password file, admin config, service/mount units, and Nginx include without printing their contents. Record only non-secret path metadata and public-site hashes in the log. Abort on unexplained drift.

- [ ] **Step 3: Deploy only the isolated OTA service**

Compute locally and record:

```powershell
Get-FileHash .artifacts/ota-admin/superpan-ota-admin-linux-amd64 -Algorithm SHA256
Get-FileHash .artifacts/ota-admin/superpan-ota-admin-source.zip -Algorithm SHA256
Get-FileHash .artifacts/ota/publisher.cer -Algorithm SHA256
```

Create a unique root-only remote staging directory, transfer `.artifacts/ota-admin/superpan-ota-admin-linux-amd64`, `.artifacts/ota/publisher.cer`, and the complete `deploy/ota-admin` directory using the existing SSH/SCP authentication method without putting credentials in arguments or logs. On the server, run `sha256sum` for the transferred binary and certificate and compare them to the local values before mutation. Record the source-bundle hash only as build provenance; the installer does not receive the source bundle.

Invoke the actual supported installer interface:

```bash
bash deploy/ota-admin/install.sh \
  --binary ./superpan-ota-admin-linux-amd64 \
  --binary-sha256 '<verified binary SHA-256>' \
  --certificate ./publisher.cer \
  --certificate-sha256 '<verified certificate SHA-256>' \
  --publisher-spki-sha256 '<existing audited SPKI SHA-256>'
```

The idempotent installer rewrites the OTA binary, service/mount units, Nginx include, public certificate, and config; reapplies ownership/mode to the OTA release root; restarts `var-lib-otaadmin-releases.mount` and `superpan-ota-admin.service`; runs `nginx -t`; and reloads BaoTa Nginx through its audited absolute binary. It must preserve database contents, HMAC key, initial password, release tree, existing vhost content, and both existing sites.

- [ ] **Step 4: Verify production behavior**

Verify service and mount active/enabled, port bound only to `127.0.0.1:18091`, HTTPS `/admin/` security headers, served HTML containing `至少 8 位` and not `至少 16 位`, unchanged `f.fanfh.cn` and OTA `/health` baselines, unchanged empty release tree, and no credentials in logs.

Within the root-only baseline directory, use `cmp -s` for the database, HMAC file, initial-password file, and admin config; output only PASS/FAIL labels, never their contents or hashes. Compare release-tree path/type/owner/group/mode/size snapshots, both vhosts, both public certificates, and trusted public body hashes. Abort and preserve evidence on any unexpected difference. After all comparisons pass, delete only the unique remote transfer/baseline directories whose resolved paths were checked under the intended root-only staging root; leave installer backups intact.

- [ ] **Step 5: Record evidence and commit**

Append the new binary/source hashes, test counts, deployment backup path, production HTTP checks, and unchanged-site baselines to the acceptance record without secrets. Stage only the acceptance document and commit: `docs: record eight-character OTA deployment`.
