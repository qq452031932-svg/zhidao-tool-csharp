# Local Multi-Product License System Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build and verify the complete multi-product card-key licensing system locally, including the isolated license service, the unified OTA admin console, and the Windows client gate, without changing or deploying anything on the server.

**Architecture:** Add a second Go executable inside the existing `services/ota_admin` module so it runs as an independent process with its own port, SQLite database, secrets, and signing key. The existing OTA admin process proxies authenticated license-management calls to that loopback service, while Windows clients call the public activation/validation API and verify signed claims. Put reusable client logic in a new `SuperPan.Licensing.Client` project; keep WPF dialogs and operation-entry wiring in the existing desktop app.

**Tech Stack:** Go `net/http` + `modernc.org/sqlite`, ECDSA P-256 signed payload envelopes, WPF/.NET 8, Windows DPAPI `CurrentUser`, `System.Management`, xUnit, PowerShell local acceptance scripts.

---

## Scope boundary

This plan ends with both Go processes and the desktop client running against loopback addresses and passing automated/local acceptance tests. It must not edit production Nginx, systemd, DNS, firewall, SSH, `/var/lib`, `/etc`, or the live `ota.fanfh.cn` database. Server deployment and production key enrollment require a later plan.

## File map

### Isolated Go license runtime

- `services/ota_admin/cmd/license-service/main.go`: production-shaped CLI entry point.
- `services/ota_admin/cmd/license-service/serve.go`: loads license config, opens keys/database, binds loopback, and starts HTTP server.
- `services/ota_admin/internal/licensing/config/config.go`: strict independent license-service config.
- `services/ota_admin/internal/licensing/model/model.go`: request, response, software, package, card, and lease types.
- `services/ota_admin/internal/licensing/store/migrations.go`: license-only schema and indexes.
- `services/ota_admin/internal/licensing/store/store.go`: transactional persistence and searches.
- `services/ota_admin/internal/licensing/service/service.go`: activation, validation, renewal, unbind, deletion, and package rules.
- `services/ota_admin/internal/licensing/signing/envelope.go`: byte-exact signed response envelopes.
- `services/ota_admin/internal/licensing/httpapi/router.go`: public and internal management HTTP routes.
- `services/ota_admin/internal/licensing/httpapi/middleware.go`: body limits, internal bearer authentication, replay cache, and rate limits.

### Unified OTA admin

- `services/ota_admin/internal/licenseadmin/client.go`: typed loopback client for the license service.
- `services/ota_admin/internal/config/config.go`: license-service URL and secret-file path for the OTA admin process.
- `services/ota_admin/cmd/ota-admin/serve.go`: create the loopback client and inject it into the router.
- `services/ota_admin/internal/httpapi/router.go`: authenticated `/admin/api/license/*` proxy routes.
- `services/ota_admin/internal/httpapi/license_handlers.go`: request validation and response mapping.
- `services/ota_admin/internal/webui/static/index.html`: make license management the main console and OTA a tab.
- `services/ota_admin/internal/webui/static/app.js`: software/package/card actions and filters.
- `services/ota_admin/internal/webui/static/styles.css`: compact Chinese management UI.

### Local-only acceptance stack

- `services/ota_admin/cmd/license-admin-local/main.go`: build-tagged loopback-only host using the real login/router and fake OTA publishing dependencies.
- `scripts/run_local_license_stack.ps1`: create disposable keys/config/databases, start both local processes, and print URLs/password.
- `scripts/stop_local_license_stack.ps1`: stop only PIDs recorded by the local runner.
- `.gitignore`: ignore `.tmp/license-local/` runtime secrets and databases.
- `tools/license_client_harness/SuperPan.Licensing.AcceptanceHarness.csproj`: command-line consumer of the real reusable client.
- `tools/license_client_harness/Program.cs`: activate/validate/authorize commands with injectable product, store root, machine fixture, and UTC time.
- `tools/license_client_harness/packages.lock.json`: locked harness dependencies.

### Reusable Windows client

- `src/SuperPan.Licensing.Client/SuperPan.Licensing.Client.csproj`: reusable Windows licensing library.
- `src/SuperPan.Licensing.Client/LicenseContracts.cs`: stable result/error models.
- `src/SuperPan.Licensing.Client/LicenseClientOptions.cs`: immutable product ID, endpoint, signing key, and two TLS pins.
- `src/SuperPan.Licensing.Client/SignedLicenseEnvelopeVerifier.cs`: ECDSA verification and field/time binding.
- `src/SuperPan.Licensing.Client/LicenseApiClient.cs`: activation/validation HTTP behavior and error mapping.
- `src/SuperPan.Licensing.Client/MachineFingerprintProvider.cs`: normalized `MachineGuid`/BIOS UUID hash.
- `src/SuperPan.Licensing.Client/DpapiLicenseStore.cs`: shared per-user encrypted cards, activation IDs, and leases.
- `src/SuperPan.Licensing.Client/LicenseAuthorizationGate.cs`: startup and operation-time authorization policy.

### WPF integration

- `src/BaiduBatchTransfer.App/Features/Licensing/LicenseActivationViewModel.cs`: Chinese activation states.
- `src/BaiduBatchTransfer.App/Features/Licensing/LicenseActivationWindow.xaml`: minimal activation dialog.
- `src/BaiduBatchTransfer.App/Features/Licensing/LicenseActivationWindow.xaml.cs`: dialog binding only.
- `src/BaiduBatchTransfer.App/Features/Licensing/WpfLicenseActivationPresenter.cs`: interactive activation adapter.
- `src/BaiduBatchTransfer.App/Features/Settings/SettingsViewModel.cs`: expose package, expiry, and binding.
- `src/BaiduBatchTransfer.App/Features/Settings/SettingsView.xaml`: read-only authorization panel.
- `src/BaiduBatchTransfer.App/AppBootstrap.cs`: register client, store, gate, presenter, and options.
- `src/BaiduBatchTransfer.App/App.xaml.cs`: perform non-blocking startup validation and prompt when needed.
- `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs`: gate start/continue/manual retry.
- `src/BaiduBatchTransfer.App/Features/Shares/ShareWorkspaceViewModel.cs`: gate start/resume/manual retry.
- `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsViewModel.cs`: gate remote refresh.

---

### Task 1: Lock the wire contract and solution structure

**Files:**
- Create: `services/ota_admin/internal/licensing/model/model.go`
- Create: `services/ota_admin/internal/licensing/model/model_test.go`
- Create: `src/SuperPan.Licensing.Client/SuperPan.Licensing.Client.csproj`
- Create: `src/SuperPan.Licensing.Client/LicenseContracts.cs`
- Create: `tests/SuperPan.Licensing.Client.Tests/SuperPan.Licensing.Client.Tests.csproj`
- Create: `src/SuperPan.Licensing.Client/packages.lock.json`
- Create: `tests/SuperPan.Licensing.Client.Tests/packages.lock.json`
- Create: `tests/SuperPan.Licensing.Client.Tests/LicenseContractTests.cs`
- Modify: `BaiduBatchTransfer.sln`
- Modify: `Directory.Packages.props`

- [ ] **Step 1: Write failing Go and .NET contract tests**

Define protocol version `1`, stable codes (`ok`, `invalid_request`, `unsupported_protocol`, `license_not_found`, `license_expired`, `app_not_allowed`, `app_disabled`, `machine_mismatch`, `rate_limited`, `service_unavailable`), and a signed envelope:

```json
{
  "key_id": "license-signing-local-1",
  "payload": "BASE64_OF_EXACT_UTF8_JSON_BYTES",
  "signature": "BASE64_ECDSA_ASN1_SIGNATURE"
}
```

The payload record fields are `protocol_version`, `request_id`, `server_time`, `decision`, `code`, `card_id`, `software_id`, `machine_hash`, `package_name`, `expires_at`, and `lease_until`. Tests must reject unknown decision/code pairs and overlong identifiers.

- [ ] **Step 2: Run the focused tests and verify failure**

Run:

```powershell
Push-Location services/ota_admin
go test ./internal/licensing/model
Pop-Location
dotnet test tests/SuperPan.Licensing.Client.Tests/SuperPan.Licensing.Client.Tests.csproj
```

Expected: FAIL because contract types/projects do not exist.

- [ ] **Step 3: Add the projects and minimal matching contract types**

Use a Windows target for the client library:

```xml
<TargetFramework>net8.0-windows</TargetFramework>
<Nullable>enable</Nullable>
<ImplicitUsings>enable</ImplicitUsings>
```

Add central versions for `System.Management` and reference `System.Security.Cryptography.ProtectedData`. Keep HTTP/JSON types dependency-free. Add both projects to `BaiduBatchTransfer.sln`, run `dotnet restore` once, and commit the generated lock files.

- [ ] **Step 4: Run the focused tests and verify pass**

Run `dotnet restore BaiduBatchTransfer.sln`, then repeat the focused tests with `--no-restore`. Expected: both Go and .NET commands PASS and both new `packages.lock.json` files exist.

- [ ] **Step 5: Commit only Task 1 files**

```powershell
git add BaiduBatchTransfer.sln Directory.Packages.props services/ota_admin/internal/licensing/model src/SuperPan.Licensing.Client tests/SuperPan.Licensing.Client.Tests
git commit -m "feat: define multi-product license contract"
```

### Task 2: Build the license-only SQLite store

**Files:**
- Create: `services/ota_admin/internal/licensing/store/migrations.go`
- Create: `services/ota_admin/internal/licensing/store/store.go`
- Create: `services/ota_admin/internal/licensing/store/store_test.go`

- [ ] **Step 1: Write failing migration and transaction tests**

Cover:

- immutable software/package/card UUIDs;
- software enable/disable without delete;
- package membership join table;
- encrypted card ciphertext, full-card keyed digest, indexed `prefix8` and `last4`;
- `base_days` and `unactivated_total_days`;
- nullable activation/expiry and machine hash;
- activation request idempotency table keyed by request ID + card/software/machine;
- replay nonce expiry table;
- service-side leases and hourly-coalesced `last_seen`;
- permanent card delete cascading bindings and leases;
- exact card digest, exact prefix-8, exact last-4, and note-contains search.

- [ ] **Step 2: Run and verify the tests fail**

Run:

```powershell
Push-Location services/ota_admin
go test ./internal/licensing/store -run Test -count=1
Pop-Location
```

Expected: FAIL because the store does not exist.

- [ ] **Step 3: Implement schema and focused store methods**

Use `PRAGMA foreign_keys=ON`, WAL mode, busy timeout, parameterized queries, and `BEGIN IMMEDIATE` only for activation/renew/delete boundaries. Expose small methods such as:

```go
CreateSoftware(ctx context.Context, name string) (model.Software, error)
SetSoftwareEnabled(ctx context.Context, id string, enabled bool) error
CreatePackage(ctx context.Context, name string, softwareIDs []string) (model.Package, error)
ReplacePackageMembers(ctx context.Context, id string, softwareIDs []string) error
GenerateCards(ctx context.Context, req model.GenerateCardsRequest, sealed []model.SealedCard) ([]model.Card, error)
Activate(ctx context.Context, req model.ActivationMutation) (model.ActivationState, error)
RenewCard(ctx context.Context, id string, days int, now time.Time) error
UnbindCard(ctx context.Context, id string) error
DeleteCard(ctx context.Context, id string) error
SearchCards(ctx context.Context, filter model.CardFilter) ([]model.CardSummary, error)
```

- [ ] **Step 4: Run store tests and race check**

Run:

```powershell
Push-Location services/ota_admin
go test ./internal/licensing/store -count=1
go test -race ./internal/licensing/store -count=1
Pop-Location
```

Expected: PASS; no `busy/locked` error in concurrent activation tests.

- [ ] **Step 5: Commit Task 2**

```powershell
git add services/ota_admin/internal/licensing/store
git commit -m "feat: persist license products packages and cards"
```

### Task 3: Implement business rules and card protection

**Files:**
- Create: `services/ota_admin/internal/licensing/service/card_crypto.go`
- Create: `services/ota_admin/internal/licensing/service/service.go`
- Create: `services/ota_admin/internal/licensing/service/service_test.go`

- [ ] **Step 1: Write failing table-driven business tests**

Test 30/90/180/365-day generation, first-use activation, same-machine reuse, other-machine rejection, dynamic package membership, software disable, active/expired renewal from `max(now, expiry)`, unused-card renewal accumulation, admin unbind, permanent delete, and default/per-card notes.

Also prove that cards are generated with cryptographic randomness, normalized before digest lookup, encrypted at rest with AES-256-GCM, and never returned by list APIs unless a copy/export action explicitly requests decryption.

- [ ] **Step 2: Run and verify failure**

Run: `Push-Location services/ota_admin; go test ./internal/licensing/service -run Test -count=1; Pop-Location`

- [ ] **Step 3: Implement the smallest service satisfying the rules**

Use injected `Clock`, `Random`, store interface, 32-byte encryption key, and distinct 32-byte HMAC lookup key. Generate a human format with at least 128 bits of entropy, for example four uppercase base32 groups. Zero temporary plaintext byte slices after encryption/decryption.

- [ ] **Step 4: Run tests**

Run: `Push-Location services/ota_admin; go test ./internal/licensing/service -count=1; Pop-Location`

Expected: PASS with no logged card plaintext.

- [ ] **Step 5: Commit Task 3**

```powershell
git add services/ota_admin/internal/licensing/service
git commit -m "feat: enforce license lifecycle rules"
```

### Task 4: Add signed public activation and validation APIs

**Files:**
- Create: `services/ota_admin/internal/licensing/config/config.go`
- Create: `services/ota_admin/internal/licensing/config/config_test.go`
- Create: `services/ota_admin/internal/licensing/signing/envelope.go`
- Create: `services/ota_admin/internal/licensing/signing/envelope_test.go`
- Create: `services/ota_admin/internal/licensing/httpapi/middleware.go`
- Create: `services/ota_admin/internal/licensing/httpapi/router.go`
- Create: `services/ota_admin/internal/licensing/httpapi/public_handlers.go`
- Create: `services/ota_admin/internal/licensing/httpapi/public_handlers_test.go`
- Create: `services/ota_admin/cmd/license-service/main.go`
- Create: `services/ota_admin/cmd/license-service/serve.go`
- Create: `services/ota_admin/cmd/license-service/serve_test.go`

- [ ] **Step 1: Write failing API security tests**

Exercise exact routes `/license-api/v1/activate` and `/license-api/v1/validate`. Assert body/header limits, JSON unknown-field rejection, request timestamps, nonce replay rejection, `activation_request_id` idempotency, independent activation/validation rate-limit buckets with `Retry-After`, stable HTTP/code mapping, and signed success/business-rejection responses.

- [ ] **Step 2: Run and verify failure**

Run: `Push-Location services/ota_admin; go test ./internal/licensing/config ./internal/licensing/signing ./internal/licensing/httpapi ./cmd/license-service -count=1; Pop-Location`

- [ ] **Step 3: Implement byte-exact ECDSA envelopes**

Serialize the claims once into exact UTF-8 bytes, sign those bytes with ECDSA P-256/SHA-256 using ASN.1 signatures, then base64 the unmodified bytes. Never parse and reserialize before verification. Config requires loopback listen address, absolute database path, encryption/HMAC/private-key/internal-secret files, and public API base path.

- [ ] **Step 4: Implement error and cache semantics**

`400/403/409` are signed definitive rejections. Per the 2026-09-09 protective-rate-limit amendment, charge the endpoint/IP bucket at handler entry before reading or parsing the body. Every over-limit request returns only an unsigned `429` with `Retry-After`, `Content-Type: application/json`, `Cache-Control: no-store`, and `{"code":"rate_limited"}`; it has no request-ID correlation, signature, replay claim, or service call. Clients branch on HTTP `429` before envelope parsing and may only retain an existing unexpired lease. Signed `200` and definitive `400/403/409` remain authorization decisions and invalid signatures fail closed; `5xx` and transport failures are temporary and never grant authorization without an existing lease. Add an import-boundary test using `go list -deps` or AST imports to prove `cmd/license-service` and `internal/licensing/*` do not import OTA candidate, deploy, publisher, release, upload, operation, session, or main store packages. No endpoint exposes stack traces, paths, card plaintext, raw machine values, or key contents.

- [ ] **Step 5: Run tests and the race detector**

Run:

```powershell
Push-Location services/ota_admin
go test ./internal/licensing/config ./internal/licensing/signing ./internal/licensing/httpapi ./cmd/license-service -count=1
go test -race ./internal/licensing/httpapi -count=1
Pop-Location
```

Expected: PASS.

- [ ] **Step 6: Commit Task 4**

```powershell
git add services/ota_admin/internal/licensing/config services/ota_admin/internal/licensing/signing services/ota_admin/internal/licensing/httpapi services/ota_admin/cmd/license-service
git commit -m "feat: expose signed license activation API"
```

### Task 5: Add the internal management API

**Files:**
- Create: `services/ota_admin/internal/licensing/httpapi/admin_handlers.go`
- Create: `services/ota_admin/internal/licensing/httpapi/admin_handlers_test.go`

- [ ] **Step 1: Write failing internal API tests**

Require an exact bearer token read from the root-owned/group-readable secret file and loopback source address. Cover list/create/rename/enable software; list/create/update packages plus package-removal impact preview; generate/search/copy/export/remark/renew/unbind/delete cards; all required card filters; and read device binding summaries.

- [ ] **Step 2: Run and verify failure**

Run: `Push-Location services/ota_admin; go test ./internal/licensing/httpapi -run TestAdmin -count=1; Pop-Location`

- [ ] **Step 3: Implement strict routes**

Use `/internal/v1/software`, `/internal/v1/packages`, `/internal/v1/packages/{id}/impact`, `/internal/v1/cards`, `/internal/v1/devices`, and resource action suffixes. Mutations require UUID idempotency keys. CSV export must encode UTF-8 BOM, quote formula-leading cells, and include complete card, package, type, state, expiry, binding short ID, and note.

- [ ] **Step 4: Run tests**

Expected: PASS; non-loopback or wrong bearer requests return generic 404/401 without details. Use a distinct `internal-admin` limiter, separate from activation and validation, and prove its threshold and reset behavior.

- [ ] **Step 5: Commit Task 5**

```powershell
git add services/ota_admin/internal/licensing/httpapi
git commit -m "feat: add private license management API"
```

### Task 6: Proxy license management through the existing authenticated OTA admin

**Files:**
- Create: `services/ota_admin/internal/licenseadmin/client.go`
- Create: `services/ota_admin/internal/licenseadmin/client_test.go`
- Create: `services/ota_admin/internal/httpapi/license_handlers.go`
- Create: `services/ota_admin/internal/httpapi/license_handlers_test.go`
- Modify: `services/ota_admin/internal/config/config.go`
- Modify: `services/ota_admin/internal/config/config_test.go`
- Modify: `services/ota_admin/cmd/ota-admin/serve.go`
- Modify: `services/ota_admin/cmd/ota-admin/serve_test.go`
- Modify: `services/ota_admin/internal/httpapi/router.go`
- Modify: `services/ota_admin/internal/httpapi/router_test.go`

- [ ] **Step 1: Write failing proxy/auth tests**

Prove all `/admin/api/license/*` calls require the existing admin session and CSRF policy, the browser never receives the loopback bearer secret, paths cannot be injected, request/response sizes are bounded, idempotency keys survive the proxy, and license-service downtime does not break OTA release endpoints.

- [ ] **Step 2: Run and verify failure**

Run: `Push-Location services/ota_admin; go test ./internal/licenseadmin ./internal/httpapi ./internal/config ./cmd/ota-admin -count=1; Pop-Location`

- [ ] **Step 3: Add strict config and typed client**

Add `license_admin_base_url` fixed to an explicit loopback HTTP origin and `license_admin_secret_path` as a distinct trusted absolute file. Validate it does not overlap OTA release/staging paths. The typed client exposes business methods rather than a generic open proxy.

- [ ] **Step 4: Register authenticated routes**

Pass the current session principal only for audit attribution; never trust actor data sent by the browser. Map internal service unavailability to a Chinese-friendly `license_service_unavailable` code while leaving OTA routes operational.

- [ ] **Step 5: Run tests**

Expected: PASS, including proof that `MustChangePassword` continues to block license routes until password rotation is complete.

- [ ] **Step 6: Commit Task 6**

```powershell
git add services/ota_admin/internal/licenseadmin services/ota_admin/internal/httpapi services/ota_admin/internal/config services/ota_admin/cmd/ota-admin
git commit -m "feat: integrate licensing with OTA admin session"
```

### Task 7: Make card management the primary admin interface

**Files:**
- Modify: `services/ota_admin/internal/webui/static/index.html`
- Modify: `services/ota_admin/internal/webui/static/app.js`
- Modify: `services/ota_admin/internal/webui/static/styles.css`
- Modify: `services/ota_admin/internal/webui/assets_test.go`
- Modify: `services/ota_admin/internal/webui/browser_test.go`

- [ ] **Step 1: Extend the real-browser test and verify failure**

The browser driver must log in, land on “卡密管理”, create and rename software, disable/re-enable one software item, create a multi-software package, preview the affected-valid-card count before removing a package member, generate cards with a batch note, filter by complete card/prefix8/last4/note/package/card type/state, copy/export, renew, inspect and unbind a device, permanently delete with confirmation, and switch to the “OTA 更新” tab without losing current OTA functions. It must also prove `429 Retry-After` disables repeated mutation clicks until the server permits retry.

- [ ] **Step 2: Run the browser test**

Run: `Push-Location services/ota_admin; go test ./internal/webui -run TestAdminConsoleInRealBrowser -count=1; Pop-Location`

Expected: FAIL because license tabs/actions do not exist.

- [ ] **Step 3: Implement the compact Chinese UI**

Navigation order: `卡密管理 | 软件管理 | 套餐管理 | 设备绑定 | OTA 更新`. Default to card management after login. Use a single dense toolbar for package, card type, quantity, default note, and generate. Filters cover complete card, fixed first 8/last 4, note, package, card type, and state. Use a table for masked key, package, days, state, activation, expiry, binding, and note. Software rows expose rename and enable/disable only. Package membership removal first calls an impact-preview endpoint and shows the number of affected valid cards plus the possible 30-minute offline delay. Device binding shows only card short ID, machine short ID, bound time, last successful validation, and administrator unbind. When an endpoint returns 429, disable only the affected action, show the retry time, and use capped exponential retry with random jitter for automatic reads; never spin or poll continuously. Destructive delete is a two-step confirmation; no recycle/restore control exists.

- [ ] **Step 4: Run asset and browser tests**

Run: `Push-Location services/ota_admin; go test ./internal/webui -count=1; Pop-Location`

Expected: PASS in both happy and service-unavailable browser scenarios.

- [ ] **Step 5: Commit Task 7**

```powershell
git add services/ota_admin/internal/webui
git commit -m "feat: add license management admin console"
```

### Task 8: Provide a safe local acceptance stack

**Files:**
- Create: `services/ota_admin/cmd/license-admin-local/main.go`
- Create: `services/ota_admin/cmd/license-admin-local/main_test.go`
- Create: `scripts/run_local_license_stack.ps1`
- Create: `scripts/stop_local_license_stack.ps1`
- Create: `tests/BaiduBatchTransfer.App.Tests/Updates/LicenseLocalStackScriptTests.cs`
- Modify: `.gitignore`

- [ ] **Step 1: Write failing structure/safety tests**

Assert the local host has a build tag, refuses non-loopback binds, uses disposable `.tmp/license-local`, records exact child PIDs, and the stop script kills only those PIDs after verifying executable paths and command lines. `LicenseLocalStackScriptTests` reads both PowerShell scripts and asserts literal loopback ports, validated PID manifest paths, executable/command-line identity checks before `Stop-Process`, `try/finally` cleanup, no wildcard process kill, no production hostname/SSH/Nginx/systemd command, and ignored secret/database output paths. Assert secrets/databases are ignored by Git.

- [ ] **Step 2: Run and verify failure**

Run:

```powershell
Push-Location services/ota_admin
go test -tags license_local_acceptance ./cmd/license-admin-local -count=1
Pop-Location
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter "FullyQualifiedName~LicenseLocalStackScriptTests" --no-restore
```

- [ ] **Step 3: Implement local runner and real-login host**

Generate ECDSA key, encryption key, HMAC key, internal secret, disposable SQLite paths, and a one-time local admin password. Start license service on `127.0.0.1:19091` and the tagged admin host on `127.0.0.1:19090`; print `http://127.0.0.1:19090/admin/` and write the client acceptance settings under `.tmp/license-local/client.json`. Do not open a browser automatically in tests.

- [ ] **Step 4: Run local smoke checks**

Run the start script, query both loopback health endpoints, log in through a browser, generate one 30-day card, and stop the stack. Confirm no process remains and no tracked file contains generated secrets.

- [ ] **Step 5: Commit Task 8**

```powershell
git add .gitignore services/ota_admin/cmd/license-admin-local scripts/run_local_license_stack.ps1 scripts/stop_local_license_stack.ps1 tests/BaiduBatchTransfer.App.Tests/Updates/LicenseLocalStackScriptTests.cs
git commit -m "test: add local license acceptance stack"
```

### Task 9: Verify signed claims and TLS policy in the reusable .NET client

**Files:**
- Create: `src/SuperPan.Licensing.Client/LicenseClientOptions.cs`
- Create: `src/SuperPan.Licensing.Client/SignedLicenseEnvelopeVerifier.cs`
- Create: `src/SuperPan.Licensing.Client/LicenseApiClient.cs`
- Create: `tests/SuperPan.Licensing.Client.Tests/SignedLicenseEnvelopeVerifierTests.cs`
- Create: `tests/SuperPan.Licensing.Client.Tests/LicenseApiClientTests.cs`

- [ ] **Step 1: Write failing client security tests**

Test valid ECDSA envelope, wrong signer, changed payload, mismatched request/software/machine, unknown key ID/code, stale/future server time, lease beyond 30 minutes or card expiry, every HTTP mapping, and 429/5xx/transport failures. Test two SPKI pins and rejection of unpinned certificates. Use an injected delay/random source to prove retry timing without sleeping: at most three attempts, exponential delays of `250 ms * 2^(attempt-1)` plus `0..250 ms` jitter, cancellation, and `Retry-After` capped at 10 seconds.

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/SuperPan.Licensing.Client.Tests/SuperPan.Licensing.Client.Tests.csproj --filter "FullyQualifiedName~Envelope|FullyQualifiedName~LicenseApiClient" --no-restore`

- [ ] **Step 3: Implement fail-closed verification**

Parse the outer envelope, base64-decode exact payload bytes, verify ECDSA before parsing claims, then bind all expected fields. Production options require HTTPS plus exactly two valid lowercase SPKI SHA-256 pins. A separate explicit `AllowLoopbackHttpForAcceptance` option may allow only `http://127.0.0.1` and only in acceptance builds. For transport failure, 429, or 5xx, attempt at most three total sends with cancellation-aware delays of `250 ms * 2^(attempt-1)` plus injected 0–250 ms jitter; honor a larger `Retry-After` up to a 10-second cap. Reuse the activation business request ID but generate a fresh timestamp and nonce on every send. Never retry definitive 4xx business rejections. After the retry budget, return the temporary-failure result so the gate may evaluate its local lease.

- [ ] **Step 4: Run tests**

Expected: PASS, including cancellation and response-size limits.

- [ ] **Step 5: Commit Task 9**

```powershell
git add src/SuperPan.Licensing.Client tests/SuperPan.Licensing.Client.Tests
git commit -m "feat: verify signed license responses on Windows"
```

### Task 10: Implement shared DPAPI state, stable machine binding, and authorization gate

**Files:**
- Create: `src/SuperPan.Licensing.Client/MachineFingerprintProvider.cs`
- Create: `src/SuperPan.Licensing.Client/DpapiLicenseStore.cs`
- Create: `src/SuperPan.Licensing.Client/LicenseAuthorizationGate.cs`
- Create: `tests/SuperPan.Licensing.Client.Tests/MachineFingerprintProviderTests.cs`
- Create: `tests/SuperPan.Licensing.Client.Tests/DpapiLicenseStoreTests.cs`
- Create: `tests/SuperPan.Licensing.Client.Tests/LicenseAuthorizationGateTests.cs`

- [ ] **Step 1: Write failing policy tests**

Cover normalized `MachineGuid` + BIOS UUID, one-field fallback, both-missing failure, no random fallback, same-user shared storage, durable activation UUID reuse, successful lease persistence, definitive rejection clearing the affected lease, 429/5xx/transport use of an unexpired lease, lease expiry, startup validation, and no idle polling. Add explicit redaction assertions for card values, machine hashes, serialized lease contents, signed payloads, and raw service/internal errors across `ToString`, exceptions, status events, and logs.

Define operation scopes:

```csharp
public enum LicenseOperationKind { Startup, Transfer, Share, ShareAnalyticsRefresh }
public interface ILicenseAuthorizationGate
{
    Task<LicenseGateResult> AuthorizeAsync(LicenseOperationKind operation, CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/SuperPan.Licensing.Client.Tests/SuperPan.Licensing.Client.Tests.csproj --filter "FullyQualifiedName~MachineFingerprint|FullyQualifiedName~Dpapi|FullyQualifiedName~AuthorizationGate" --no-restore`

- [ ] **Step 3: Implement secure shared storage and gate**

Store under `%LOCALAPPDATA%/SuperPan/Licensing/v1/licenses.dat` with a per-user mutex, atomic replace, restrictive ACL, DPAPI `CurrentUser`, strict size/count limits, and a shared purpose string independent of product display name. Never expose card values through `ToString`, exceptions, events, or logs.

- [ ] **Step 4: Run tests**

Expected: PASS. On non-Windows test environments, crypto integration tests skip explicitly while pure policy tests still run.

- [ ] **Step 5: Commit Task 10**

```powershell
git add src/SuperPan.Licensing.Client tests/SuperPan.Licensing.Client.Tests
git commit -m "feat: cache machine-bound licenses securely"
```

### Task 11: Add the activation dialog and read-only settings status

**Files:**
- Create: `src/BaiduBatchTransfer.App/Features/Licensing/LicenseActivationViewModel.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Licensing/LicenseActivationWindow.xaml`
- Create: `src/BaiduBatchTransfer.App/Features/Licensing/LicenseActivationWindow.xaml.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Licensing/WpfLicenseActivationPresenter.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/LicenseActivationViewModelTests.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/LicenseActivationViewStructureTests.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Settings/SettingsViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Settings/SettingsView.xaml`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/SettingsViewModelTests.cs`

- [ ] **Step 1: Write failing WPF/view-model tests**

Activation window contains only product name, card input, activate, status, and close. All status strings are the approved Chinese messages; no renewal/payment/expiry reminder exists. Settings display package, expiry, authorization, and binding as read-only values.

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter "FullyQualifiedName~LicenseActivation|FullyQualifiedName~SettingsViewModel" --no-restore`

- [ ] **Step 3: Implement the minimal UI**

Disable duplicate activation submissions, trim/normalize pasted card input, keep secrets out of accessibility status text, and close only on success or explicit user close. Do not block access to local data/settings when activation is absent or invalid.

- [ ] **Step 4: Run tests**

Expected: PASS.

- [ ] **Step 5: Commit Task 11**

```powershell
git add src/BaiduBatchTransfer.App/Features/Licensing src/BaiduBatchTransfer.App/Features/Settings tests/BaiduBatchTransfer.App.Tests/Features
git commit -m "feat: add Chinese license activation experience"
```

### Task 12: Wire startup and remote-operation authorization boundaries

**Files:**
- Modify: `src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj`
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Modify: `src/BaiduBatchTransfer.App/App.xaml.cs`
- Modify: `src/BaiduBatchTransfer.App/AppStartupController.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Licensing/LicenseRuntimeAuthorizationState.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Licensing/LicenseAcceptanceSmokeRunner.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Shares/ShareWorkspaceViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Common/WorkspaceRunPanelViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Jobs/JobResultsViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Services/TransferCoordinatorHostedService.cs`
- Modify: `src/BaiduBatchTransfer.App/Services/StandaloneShareCoordinatorHostedService.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/AppStartupControllerTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewModelTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/ShareWorkspaceViewModelTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewModelTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/WorkspaceRunPanelViewModelTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/JobResultsViewModelTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Services/TransferCoordinatorHostedServiceTests.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Services/StandaloneShareCoordinatorHostedServiceTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Updates/OtaBuildConfigurationTests.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/LicenseOperationBoundaryTests.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/LicenseAcceptanceSmokeRunnerTests.cs`
- Create: `scripts/build_local_license_acceptance.ps1`

- [ ] **Step 1: Write failing boundary tests**

Assert startup validates once before hosted coordinators may dispatch recovered work, but still opens the local UI on rejection. `LicenseRuntimeAuthorizationState` keeps both coordinator hosted services waiting until startup validation or later activation succeeds. Transfer/share start, workspace manual resume/retry, job-results manual retry, and every newly-created analytics refresh flight (`Initial`, `Manual`, `AutomaticDue`, or delayed `Retry`) call the gate before creating remote work. Callers joining an existing authorized analytics flight do not revalidate. An already-running batch and scheduler-owned automatic retry do not revalidate; process-restart recovery and a new batch do. A failed gate must not mutate job state or send Baidu requests. In an acceptance-stamped build only, `--license-acceptance-smoke --license-acceptance-config=ABSOLUTE_PATH` runs the real startup gate once and exits 0 on authorized or activation-required states, and nonzero on malformed trust/config; production and Debug-disabled builds reject this switch.

- [ ] **Step 2: Run and verify failure**

Run:

```powershell
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter "FullyQualifiedName~LicenseOperationBoundary|FullyQualifiedName~TransferWorkspaceViewModel|FullyQualifiedName~ShareWorkspaceViewModel|FullyQualifiedName~ShareAnalyticsViewModel|FullyQualifiedName~WorkspaceRunPanelViewModel|FullyQualifiedName~JobResultsViewModel|FullyQualifiedName~CoordinatorHostedService" --no-restore
```

- [ ] **Step 3: Register a permanent product ID and acceptance configuration**

Generate the current app's product UUID once and commit it as assembly metadata; the display name remains changeable. Add three explicit build modes: Debug developer builds default to `Disabled`; local acceptance builds set `LicenseAcceptanceBuild=true` and may read only a command-line-selected loopback public configuration file; Release production builds require HTTPS `LicenseServiceUrl`, signing public key, current SPKI pin, and next SPKI pin and fail when any trust setting is absent/invalid. Add MSBuild tests proving Release fails closed and acceptance mode cannot target a non-loopback HTTP URL.

- [ ] **Step 4: Gate every boundary that can create or recover remote work**

Inject `ILicenseAuthorizationGate` into the transfer/share/analytics view models plus the shared run and results controls. Gate the creation of every new analytics refresh flight regardless of origin (`Initial`, `Manual`, `AutomaticDue`, or delayed `Retry`), but do not re-gate callers that merely join the same in-flight operation. Hosted coordinators must await `LicenseRuntimeAuthorizationState` before their first recovery/dispatch cycle and resume when activation signals authorization; their internal scheduling and automatic retries remain free of network validation. Convert failures to one Chinese dialog/status without exposing raw API codes.

- [ ] **Step 5: Run app tests and build**

Run:

```powershell
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --no-restore
dotnet build BaiduBatchTransfer.sln -c Debug --no-restore
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/build_local_license_acceptance.ps1
```

Expected: PASS with zero errors.

- [ ] **Step 6: Commit Task 12**

```powershell
git add src/BaiduBatchTransfer.App tests/BaiduBatchTransfer.App.Tests scripts/build_local_license_acceptance.ps1
git commit -m "feat: require licensing for remote operations"
```

### Task 13: Run full local end-to-end acceptance

**Files:**
- Create: `docs/testing/2026-09-08-local-license-acceptance.md`
- Create: `scripts/test_local_license_stack.ps1`
- Modify: `scripts/build_local_license_acceptance.ps1`
- Create: `tools/license_client_harness/SuperPan.Licensing.AcceptanceHarness.csproj`
- Create: `tools/license_client_harness/Program.cs`
- Create: `tools/license_client_harness/packages.lock.json`
- Modify: `services/ota_admin/internal/webui/browser_test.go`
- Modify: `tests/SuperPan.Licensing.Client.Tests/LicenseAuthorizationGateTests.cs`

- [ ] **Step 1: Write the end-to-end test before the final wiring**

The script must start the disposable stack, perform the real login/CSRF flow, create software A and B plus one package, generate a 30-day card with a note, and drive the real reusable client through the command-line harness. Activate A in store `user-a`, validate B with the same store without entering the card again, then use an empty store `user-b` with the same deterministic machine fixture to simulate another Windows user's empty DPAPI store and prove the server accepts the same physical machine without admin unbind. Unit tests still verify actual DPAPI `CurrentUser` protection; an existing second Windows account may be used for the documented manual check without creating or deleting OS accounts.

The same script must launch the generated WPF acceptance build with `--license-acceptance-smoke` and the loopback client config, assert its exit code and embedded immutable product ID, then remove B from the package and observe signed `app_not_allowed`, re-add B, disable it and observe signed `app_disabled`, then re-enable it. Snapshot a valid encrypted lease store, delete the card, and prove an online request is rejected immediately. Stop the license service and use the harness's injected `TimeProvider` (`--utc-now`) against the pre-delete store snapshot to prove temporary use before 30 minutes and rejection after 30 minutes without sleeping. Exercise renewal and unbind on a separate card, and stop all local processes in `finally`.

- [ ] **Step 2: Run and verify the test exposes any missing wiring**

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test_local_license_stack.ps1`

Expected before final corrections: FAIL at the first incomplete seam, without touching network hosts other than `127.0.0.1`.

- [ ] **Step 3: Make only the corrections required by end-to-end evidence**

Implement the harness as a small `net8.0-windows` executable referencing `SuperPan.Licensing.Client`; it accepts only explicit loopback acceptance config, product ID, store root, machine fixture, and optional UTC time. Run `dotnet restore` after adding it and commit its lock file. Make `scripts/build_local_license_acceptance.ps1` build the real WPF app with `LicenseAcceptanceBuild=true` and the generated loopback public-key config. Do not add server deploy artifacts. Record exact commands, generated product ID (not secrets), ports, expected UI actions, and result counts in the acceptance document.

- [ ] **Step 4: Run complete verification**

Run:

```powershell
Push-Location services/ota_admin
go test ./... -count=1
Pop-Location
dotnet restore BaiduBatchTransfer.sln
dotnet restore tools/license_client_harness/SuperPan.Licensing.AcceptanceHarness.csproj
dotnet test BaiduBatchTransfer.sln --no-restore
dotnet build BaiduBatchTransfer.sln --no-restore
dotnet build tools/license_client_harness/SuperPan.Licensing.AcceptanceHarness.csproj --no-restore
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/build_local_license_acceptance.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test_local_license_stack.ps1
git diff --check
```

Expected: all commands exit 0; no generated secret or database is tracked; local processes are stopped.

- [ ] **Step 5: Commit local acceptance artifacts**

```powershell
git add docs/testing/2026-09-08-local-license-acceptance.md scripts/test_local_license_stack.ps1 scripts/build_local_license_acceptance.ps1 tools/license_client_harness services/ota_admin/internal/webui/browser_test.go tests/SuperPan.Licensing.Client.Tests/LicenseAuthorizationGateTests.cs
git commit -m "test: verify local multi-product licensing end to end"
```

- [ ] **Step 6: Stop and report the server-deployment boundary**

Deliver the local admin URL/start-stop commands and local desktop acceptance build. Explicitly state that production DNS, SSH, Nginx, systemd, secrets, database, and OTA stable channel remain unchanged. Create the separate server deployment plan only after the user accepts the local test results.
