# OTA Password Minimum Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Allow OTA administrator passwords with at least eight Unicode characters while leaving the 1024-byte maximum and every other security control unchanged.

**Architecture:** Keep the server as the authority: `security.HashPassword` counts Unicode code points for the minimum and UTF-8 bytes for the existing maximum. Mirror the same code-point check in the embedded browser UI so users receive immediate feedback without weakening server enforcement. Rebuild and redeploy only the isolated OTA admin service through the audited idempotent installer.

**Tech Stack:** Go 1.27, embedded HTML/JavaScript, Go unit/browser tests, PowerShell build scripts, systemd/Nginx isolated deployment.

---

### Task 1: Define the eight-character server contract

**Files:**
- Modify: `services/ota_admin/internal/security/security_test.go`
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
    {"1024 bytes", strings.Repeat("a", 1024), true},
    {"1025 bytes", strings.Repeat("a", 1025), false},
}
```

Hash accepted inputs with deterministic salt bytes and assert rejected inputs fail before hashing.

- [ ] **Step 2: Run the focused test and verify RED**

Run: `& .tools/go-1.27.1/go/bin/go.exe -C services/ota_admin test ./internal/security -run TestHashPasswordEnforcesCharacterMinimum -count=1`

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

Stage only the two files and commit: `fix: allow eight-character OTA passwords`.

### Task 2: Align the embedded Chinese web page

**Files:**
- Modify: `services/ota_admin/internal/webui/assets_test.go`
- Modify: `services/ota_admin/internal/webui/browser_test.go`
- Modify: `services/ota_admin/internal/webui/static/index.html`
- Modify: `services/ota_admin/internal/webui/static/app.js`

- [ ] **Step 1: Write failing embedded-asset and browser tests**

Require the HTML to say `至少 8 位`, forbid the old `至少 16 位` and `minlength="16"`, and require a client helper that counts code points with `Array.from(value).length`. Extend the real-browser test so a seven-code-point Unicode password is rejected without calling the password endpoint, while an eight-code-point Unicode password reaches the endpoint and completes the existing forced-password-change flow.

- [ ] **Step 2: Run the focused web tests and verify RED**

Run: `& .tools/go-1.27.1/go/bin/go.exe -C services/ota_admin test ./internal/webui -run 'TestEmbeddedAdminPage|TestAdminBrowserWorkflow' -count=1`

Expected: FAIL on the old 16-character HTML/UI behavior.

- [ ] **Step 3: Implement minimal browser validation**

Remove the contradictory native `minlength="16"`, change help text to `请使用至少 8 位、与临时密码不同的密码。`, and add a small code-point-count helper. At the start of `changePassword`, reject fewer than eight code points with the same Chinese guidance, focus the new-password field, and do not send a request. Preserve all existing field clearing, CSRF, error handling, and session behavior.

- [ ] **Step 4: Run the web tests**

Run: `& .tools/go-1.27.1/go/bin/go.exe -C services/ota_admin test ./internal/webui -count=1`

Expected: PASS, including the real Chrome workflow.

- [ ] **Step 5: Commit the web alignment**

Stage only the four web files and commit: `fix: align OTA password guidance`.

### Task 3: Verify, rebuild, and deploy the isolated service

**Files:**
- Generated only: `.artifacts/ota-admin-linux-amd64/*`
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

Record current `f.fanfh.cn` vhost/certificate/body hashes, OTA `/health`, OTA vhost, service state, loopback listener, and empty production release tree. Abort on unexplained drift.

- [ ] **Step 3: Deploy only the isolated OTA service**

Transfer the audited deployment bundle through a root-only staging directory and rerun the existing idempotent `deploy/ota-admin/install.sh` with the newly recorded binary/source/certificate hashes and SPKI pin. The installer must preserve the database, HMAC key, initial password, OTA release tree, existing vhost content, and both existing sites; it may restart only `superpan-ota-admin` and perform the already-audited BaoTa Nginx configuration test/reload.

- [ ] **Step 4: Verify production behavior**

Verify service active/enabled, port bound only to `127.0.0.1:18091`, HTTPS `/admin/` security headers, served HTML containing `至少 8 位` and not `至少 16 位`, unchanged `f.fanfh.cn` and OTA `/health` baselines, unchanged empty release tree, and no credentials in logs.

- [ ] **Step 5: Record evidence and commit**

Append the new binary/source hashes, test counts, deployment backup path, production HTTP checks, and unchanged-site baselines to the acceptance record without secrets. Stage only the acceptance document and commit: `docs: record eight-character OTA deployment`.
