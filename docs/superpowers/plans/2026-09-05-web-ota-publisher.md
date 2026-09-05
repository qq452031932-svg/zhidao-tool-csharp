# Web OTA Publisher Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a production-safe web publisher at `https://ota.fanfh.cn/admin/` that accepts a locally signed SuperPan OTA ZIP, validates it, publishes one atomic stable generation, retains three generations, and lets the pinned-key Windows client consume only authentic non-replayed releases.

**Architecture:** Keep the public OTA feed as static Nginx files and add a loopback-only Go admin service with embedded HTML/CSS/JavaScript, SQLite state, strict upload validation, and atomic generation switching. Keep the signing key only in the publisher's Windows certificate store; the PowerShell release pipeline signs executable assets and a canonical manifest, while both the Go server and .NET client pin the same RSA public key/SPKI identity. Treat the `stable` symlink as the public commit point and recover database state from the signed generation after interrupted operations.

**Tech Stack:** .NET 8/WPF, Velopack 1.2, C# cryptography, PowerShell 7/Windows Authenticode, Go 1.27.1 static linux/amd64 binary, `modernc.org/sqlite`, `golang.org/x/crypto/argon2`, embedded HTML/CSS/JavaScript, Nginx, systemd, SQLite.

---

## File map

- `eng/ota/SuperPan.OtaPublisher.cer`: public publisher certificate committed for client/server enrollment; no private key or PFX is ever committed.
- `scripts/create_ota_publisher_certificate.ps1`: creates/reuses the fixed RSA-3072 code-signing identity and exports only the public certificate.
- `scripts/build_ota_release.ps1`: adds a production mode that signs, verifies, builds, canonicalizes, signs the manifest, and emits one upload ZIP.
- `src/BaiduBatchTransfer.App/Updates/OtaPublisherTrust.cs`: embedded public-key/SPKI trust anchor.
- `src/BaiduBatchTransfer.App/Updates/SignedFeedManifest.cs`: strict canonical manifest parser and RSA verifier.
- `src/BaiduBatchTransfer.App/Updates/SignedFeedStateStore.cs`: durable replay floor and pending-download receipt store.
- `src/BaiduBatchTransfer.App/Updates/SignedFeedUpdateSource.cs`: fixed-origin, cookie-free signed-feed network adapter below Velopack.
- `src/BaiduBatchTransfer.App/Updates/SignedFeedPackageGate.cs`: post-download hash gate and updater authorization receipt.
- `services/ota_admin/`: isolated Go module, tests, embedded UI, deployment assets, and local toolchain wrapper.
- `deploy/ota-admin/`: Nginx/systemd templates and isolated deployment/verification scripts.

### Task 0: Freeze baselines and prove the server runtime

**Files:**
- Create: `services/ota_admin/go.mod`
- Create: `services/ota_admin/go.sum`
- Create: `services/ota_admin/cmd/ota-admin/main.go`
- Create: `services/ota_admin/internal/compat/probe.go`
- Create: `services/ota_admin/internal/compat/probe_test.go`
- Create: `services/ota_admin/deploy/probe/superpan-ota-admin-probe.service`
- Create: `services/ota_admin/deploy/probe/run-centos7-probe.sh`
- Create: `services/ota_admin/scripts/bootstrap_go_toolchain.ps1`
- Create: `services/ota_admin/scripts/build_linux_amd64.ps1`
- Modify: `.gitignore`

- [ ] **Step 1: Record a clean OTA/client test baseline without touching unrelated dirty files.**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~Updates --no-restore`

Expected: all existing update tests pass; record the exact count in the task notes.

- [ ] **Step 2: Write failing compatibility-probe tests.**

Cover health JSON, SQLite create/write/read, Argon2id hash/verify, RSA manifest verification, atomic rename, symlink switch, and directory fsync through injectable filesystem seams.

Run: `powershell -File services/ota_admin/scripts/bootstrap_go_toolchain.ps1`

Expected: a repository-local, ignored Go 1.27.1 toolchain whose downloaded Windows amd64 ZIP SHA-256 equals `a3911b5e0e1b1053f25ed0675f4c1c6aad1e2bfcf253df2b9be4caabd2edd95d`.

- [ ] **Step 3: Run the new tests and verify the intended failure.**

Run: `& .tools\go-1.27.1\go\bin\go.exe -C services/ota_admin test ./internal/compat`

Expected: FAIL because the probe implementation is incomplete.

- [ ] **Step 4: Implement the smallest `compat-probe` command, loopback health server, and static build.**

The command exercises SQLite, Argon2id, RSA verification, rename/symlink/fsync, starts a loopback-only `/admin/health` server, prints one JSON record, never secrets, and exits non-zero if any required primitive fails. Build with `CGO_ENABLED=0 GOOS=linux GOARCH=amd64` and strip symbols. Commit `go.sum` and exact dependency versions after the real probe passes.

- [ ] **Step 5: Run local tests and build.**

Run: `& .tools\go-1.27.1\go\bin\go.exe -C services/ota_admin test ./...`

Run: `powershell -File services/ota_admin/scripts/build_linux_amd64.ps1`

Expected: PASS and `.artifacts/ota-admin/superpan-ota-admin-linux-amd64` exists.

- [ ] **Step 6: Run the probe and temporary systemd unit on CentOS 7 in an isolated directory.**

Copy only the probe binary/unit/runner to the server. In `/root/superpan-ota-admin-verify`, download and unpack `go1.27.1.linux-amd64.tar.gz` only after its SHA-256 equals `63d339f0da5ab53635a56f2490a7984dfe12dfcff22ad749f63edaf590168445`; use `CGO_ENABLED=0` for all server-side test builds, so no system compiler/package installation is required. Execute `compat-probe`, run `CGO_ENABLED=0 /root/superpan-ota-admin-verify/go/bin/go test -shuffle=on -count=10 ./...`, start/stop/restart the temporary unit, request `/admin/health` through its loopback port after both starts, and verify the JSON success flags, journald status, static ELF properties, and binary SHA-256. Then remove the unit, source copy, toolchain, and verification directory. Do not touch the live stable feed or `f.fanfh.cn`.

- [ ] **Step 7: Commit only Task 0 files.**

Commit: `build: add OTA admin compatibility probe`

### Task 1: Create and enroll the publisher identity

**Files:**
- Create: `scripts/create_ota_publisher_certificate.ps1`
- Create: `eng/ota/README.md`
- Create after explicit script execution: `eng/ota/SuperPan.OtaPublisher.cer`
- Create: `src/BaiduBatchTransfer.App/Updates/OtaPublisherTrust.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Updates/OtaPublisherTrustTests.cs`
- Modify: `src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj`
- Modify: `Directory.Build.props`

- [ ] **Step 1: Write failing tests for an embedded RSA-3072 public certificate and fixed SPKI SHA-256.**

Tests must reject an absent resource, non-RSA key, short key, or computed SPKI different from the compile-time pin.

- [ ] **Step 2: Run the focused test and verify it fails.**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~OtaPublisherTrustTests`

- [ ] **Step 3: Implement the idempotent certificate script.**

Create a non-exportable RSA-3072 SHA-256 code-signing certificate in `CurrentUser\My`, reuse an exact existing subject/SPKI, export only DER `.cer`, print thumbprint and SPKI SHA-256, and fail if multiple identities match. Never create or write a PFX.

- [ ] **Step 4: Execute the script once and embed the public certificate.**

Run: `powershell -ExecutionPolicy Bypass -File scripts/create_ota_publisher_certificate.ps1`

Expected: a public `.cer` is created and the private key remains only in the current user's certificate store.

- [ ] **Step 5: Implement `OtaPublisherTrust` and pass tests.**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~OtaPublisherTrustTests`

- [ ] **Step 6: Commit exact identity/public-trust files.**

Commit: `feat: enroll OTA publisher public identity`

### Task 2: Verify the canonical signed manifest in the client

**Files:**
- Create: `src/BaiduBatchTransfer.App/Updates/SignedFeedManifest.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Updates/SignedFeedManifestTests.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Updates/Fixtures/SignedFeed/*`

- [ ] **Step 1: Write failing parser/verifier tests.**

Cover UTF-8 without BOM, LF-only input, exactly one final LF, lowercase 64-character SHA-256, two spaces, flat allowed filenames, UTF-8 ordinal ordering, duplicate/case-colliding names, excluded manifest filenames, wrong key, missing signature, and modified bytes.

- [ ] **Step 2: Verify tests fail.**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~SignedFeedManifestTests`

- [ ] **Step 3: Implement immutable manifest entries and RSA PKCS#1 v1.5 SHA-256 verification.**

The parser must validate exact bytes before producing entries and use ordinal filename comparisons only.

- [ ] **Step 4: Pass focused tests and commit.**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~SignedFeedManifestTests`

Commit: `feat: verify signed OTA manifests`

### Task 3: Add durable replay protection and a signed feed source

**Files:**
- Create: `src/BaiduBatchTransfer.App/Updates/SignedFeedStateStore.cs`
- Create: `src/BaiduBatchTransfer.App/Updates/SignedFeedUpdateSource.cs`
- Create: `src/BaiduBatchTransfer.App/Updates/SignedFeedModels.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Updates/SignedFeedStateStoreTests.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Updates/SignedFeedUpdateSourceTests.cs`
- Modify: `src/BaiduBatchTransfer.App/Updates/VelopackUpdateClient.cs`
- Modify: `src/BaiduBatchTransfer.App/Updates/ApplicationBuildInfo.cs`
- Modify: `src/BaiduBatchTransfer.App/App.xaml.cs`

- [ ] **Step 1: Write failing state-store tests.**

Cover atomic persistence, monotonic highest accepted version, current application floor, corrupt state fail-closed, different feed identity rejection, and concurrent writers.

- [ ] **Step 2: Write failing signed-source tests with a fake HTTP handler.**

Require fixed HTTPS origin, no redirects, no cookies, fixed sibling manifest/signature/release URLs, signed manifest first, signed inclusion of release JSON, feed asset filename/version/size/hash closure, and rejection of older signed feeds.

- [ ] **Step 3: Verify both suites fail, then implement the minimum source/store.**

Preserve Velopack 1.2's `IUpdateSource.GetReleaseFeed` signature and the existing true-completion cancellation behavior. Keep `MaximumDeltasBeforeFallback = -1`.

- [ ] **Step 4: Pass focused suites and existing update tests.**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~SignedFeed`

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~Updates`

- [ ] **Step 5: Commit.**

Commit: `feat: consume pinned signed OTA feed`

### Task 4: Gate downloaded packages before updater launch

**Files:**
- Create: `src/BaiduBatchTransfer.App/Updates/SignedFeedPackageGate.cs`
- Create: `src/BaiduBatchTransfer.App/Updates/SignedPendingUpdateReceipt.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Updates/SignedFeedPackageGateTests.cs`
- Modify: `src/BaiduBatchTransfer.App/Updates/VelopackUpdateRuntime.cs`
- Modify: `src/BaiduBatchTransfer.App/Updates/UpdateAwareShutdownOrchestrator.cs`
- Modify: `src/BaiduBatchTransfer.App/Updates/ApplicationUpdateService.cs`

- [ ] **Step 1: Write failing tests for the post-download gate.**

Cover package byte tampering, wrong length/hash, unlisted filename, forged/corrupt/stale pending receipt, replayed lower version, cancellation, restart, and the invariant that updater launch is never called on failure.

- [ ] **Step 2: Run focused tests and verify failure.**

- [ ] **Step 3: Implement a local receipt authenticated with DPAPI CurrentUser.**

Write the receipt atomically only after package hashing succeeds; bind feed identity, version, filename, size, SHA-256, manifest digest, and timestamp. Validate it again at startup before exposing a pending update.

- [ ] **Step 4: Integrate below the existing update runtime and pass suites.**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~Updates`

- [ ] **Step 5: Commit.**

Commit: `feat: gate downloaded OTA packages`

### Task 5: Produce a signed production upload ZIP

**Files:**
- Modify: `scripts/build_ota_release.ps1`
- Create: `scripts/Test-OtaUploadBundle.ps1`
- Create: `tests/BaiduBatchTransfer.App.Tests/Updates/OtaProductionReleaseScriptTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Updates/OtaBuildConfigurationTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Updates/OtaReleaseScriptTests.cs`

- [ ] **Step 1: Write failing structural tests for mutually exclusive Acceptance and Production parameter sets.**

Production requires the enrolled certificate thumbprint, package ID `SuperPan.BaiduBatchTransfer`, channel `win-x64-stable`, and a strictly valid semantic version. Acceptance behavior must remain unchanged.

- [ ] **Step 2: Add a deterministic test fixture that proves canonical manifest generation/signature verification and ZIP re-open validation.**

- [ ] **Step 3: Implement Production mode.**

Build/publish, use Velopack signing parameters for Authenticode, verify every executable asset's signer SPKI, generate all metadata, generate canonical `upload-manifest.sha256`, sign its exact bytes with the enrolled RSA key, create the ZIP, re-open it, and repeat the full closure checks. Reject unknown/nested/case-colliding files.

Expose exact production parameters `-Production`, `-Version`, `-ProductionFeedUrl`, `-OutputDirectory`, `-SigningCertificateThumbprint`, optional `-PreviousReleaseDirectory`, optional `-ReleaseNotesPath`, and optional `-TimestampUrl`. When `-TimestampUrl` is supplied, a timestamp failure fails the build; when omitted, the build is explicitly untimestamped but still requires valid Authenticode/SPKI and manifest signatures. Never silently retry unsigned.

- [ ] **Step 4: Run focused and full update tests.**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~Ota`

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~Updates`

- [ ] **Step 5: Build one local production candidate without publishing it.**

Expected: one ZIP and a machine-readable verification report; no server changes.

- [ ] **Step 6: Commit.**

Commit: `build: create signed production OTA bundle`

### Task 6: Build the admin store, audit log, and configuration

**Files:**
- Create: `services/ota_admin/internal/config/config.go`
- Create: `services/ota_admin/internal/config/config_test.go`
- Create: `services/ota_admin/internal/store/store.go`
- Create: `services/ota_admin/internal/store/migrations.go`
- Create: `services/ota_admin/internal/store/store_test.go`
- Create: `services/ota_admin/internal/audit/audit.go`
- Modify: `services/ota_admin/cmd/ota-admin/main.go`

- [ ] **Step 1: Write failing tests for root-only config validation and SQLite migrations.**

Enforce compiled hard maxima, one administrator, monotonic highest committed version, state-machine constraints, candidate/session/operation/audit tables, WAL/busy timeout, and transaction rollback.

- [ ] **Step 2: Implement the minimal config/store/audit packages.**

Audit records contain safe event metadata and operation IDs, never secrets, cookies, raw source IPs, absolute paths, or package contents.

- [ ] **Step 3: Pass tests and add `init-admin`.**

`init-admin` creates the fixed username, Argon2id hash, forced-change flag, and one strong password printed exactly once.

- [ ] **Step 4: Commit.**

Commit: `feat: add OTA admin state store`

### Task 7: Implement hardened authentication and the API shell

**Files:**
- Create: `services/ota_admin/internal/security/password.go`
- Create: `services/ota_admin/internal/security/csrf.go`
- Create: `services/ota_admin/internal/security/origin.go`
- Create: `services/ota_admin/internal/security/ratelimit.go`
- Create: `services/ota_admin/internal/session/service.go`
- Create: `services/ota_admin/internal/httpapi/router.go`
- Create: `services/ota_admin/internal/httpapi/session_handlers.go`
- Create: `services/ota_admin/internal/httpapi/security_headers.go`
- Create: `services/ota_admin/internal/httpapi/*_test.go`

- [ ] **Step 1: Write failing HTTP tests.**

Only bootstrap/login are anonymous. Test one-use pre-auth, CSRF binding/consumption, strict same-origin Origin/Referer, cookie flags/path, five failures in fifteen minutes causing a fifteen-minute lock, 30-minute idle and eight-hour absolute expiry, forced password change, logout, session revocation, restart invalidation, JSON size/content-type limits, and no-cache admin responses.

- [ ] **Step 2: Implement security primitives and session handlers.**

Store only hashes of random tokens; use constant-time comparisons; normalize errors; use trusted Nginx forwarded address only from loopback.

- [ ] **Step 3: Pass repeated shuffled security tests.**

Run on Windows: `& .tools\go-1.27.1\go\bin\go.exe -C services/ota_admin test ./internal/security ./internal/session ./internal/httpapi`

Recreate `/root/superpan-ota-admin-verify` from the pinned Task 0 recipe, then run: `CGO_ENABLED=0 /root/superpan-ota-admin-verify/go/bin/go test -shuffle=on -count=10 ./internal/security ./internal/session ./internal/httpapi`

- [ ] **Step 4: Commit.**

Commit: `feat: secure OTA admin sessions`

### Task 8: Stream uploads and validate the ZIP envelope

**Files:**
- Create: `services/ota_admin/internal/upload/receiver.go`
- Create: `services/ota_admin/internal/upload/zip_envelope.go`
- Create: `services/ota_admin/internal/upload/limits.go`
- Create: `services/ota_admin/internal/upload/*_test.go`
- Create: `services/ota_admin/internal/httpapi/upload_handlers.go`

- [ ] **Step 1: Write failing adversarial tests.**

Cover 2 GiB compressed, 8 GiB expanded, 2 GiB single file, 256 entries, 100:1 ratios, interrupted bodies, low disk, traversal, absolute/drive/UNC names, directories, symlinks, nested files, duplicates, Unicode/case collisions, unknown names, and cleanup.

- [ ] **Step 2: Implement bounded streaming to a random candidate file.**

Never buffer the ZIP in memory; check disk before extraction; create extraction roots server-side; reject before writing outside the root.

- [ ] **Step 3: Pass upload tests and race tests.**

- [ ] **Step 4: Commit.**

Commit: `feat: validate OTA upload envelope`

### Task 9: Validate release authenticity and candidate lifecycle

**Files:**
- Create: `services/ota_admin/internal/publisher/trust.go`
- Create: `services/ota_admin/internal/publisher/manifest.go`
- Create: `services/ota_admin/internal/release/metadata.go`
- Create: `services/ota_admin/internal/release/releases_index.go`
- Create: `services/ota_admin/internal/release/validator.go`
- Create: `services/ota_admin/internal/release/*_test.go`
- Create: `services/ota_admin/internal/candidate/service.go`
- Create: `services/ota_admin/internal/candidate/service_test.go`
- Create: `services/ota_admin/internal/candidate/janitor.go`
- Create: `services/ota_admin/internal/candidate/janitor_test.go`
- Create: `services/ota_admin/internal/httpapi/candidate_handlers.go`

- [ ] **Step 1: Write failing tests using ZIPs created by the PowerShell validator.**

Cover fixed SPKI/RSA signature, exact manifest closure, file hashes/sizes, package/channel/version, asset catalog reverse references, RELEASES SHA-1/size/full set, aliases, Acceptance rejection, extra/missing assets, and version greater than the permanent historical floor.

- [ ] **Step 2: Implement strict validation in the specified fail-closed order.**

Create a 24-hour candidate bound to upload digest, session, validation report, and idempotent operation ID. Re-read and re-hash it on confirm. At startup and every hour, the janitor transactionally marks expired candidates, deletes their random ZIP and extraction directory only after proving both paths are beneath the configured staging root, and records success/failure without blocking serving; retry failed cleanup on the next run.

- [ ] **Step 3: Pass unit, fixture, and cross-language golden tests.**

- [ ] **Step 4: Commit.**

Commit: `feat: validate signed OTA candidates`

### Task 10: Publish atomic generations and recover interrupted commits

**Files:**
- Create: `services/ota_admin/internal/deploy/filesystem.go`
- Create: `services/ota_admin/internal/deploy/generation.go`
- Create: `services/ota_admin/internal/deploy/stable_link.go`
- Create: `services/ota_admin/internal/deploy/recovery.go`
- Create: `services/ota_admin/internal/deploy/migration.go`
- Create: `services/ota_admin/internal/deploy/*_test.go`
- Create: `services/ota_admin/cmd/ota-admin-acceptance/main_linux.go`
- Create: `services/ota_admin/scripts/build_linux_acceptance.ps1`
- Create: `services/ota_admin/internal/httpapi/publish_handlers.go`

- [ ] **Step 1: Write fault-injection tests at every durability boundary.**

Assert: fsync files, fsync candidate dir, rename to final generation, fsync generations dir, create temporary relative stable symlink, atomic replace stable, fsync win-x64 parent. Before the commit point current stays unchanged; after it recovery trusts and verifies the signed generation then repairs SQLite idempotently.

- [ ] **Step 2: Write migration tests for an empty real directory, non-empty directory, cross-filesystem rejection, and rollback on every failure.**

- [ ] **Step 3: Implement the per-process lock, DB transaction, immutable generation creation, relative-link containment, startup recovery, and read-only protection.**

- [ ] **Step 4: Pass repeated shuffled deploy tests.**

Run on Windows: `& .tools\go-1.27.1\go\bin\go.exe -C services/ota_admin test -count=20 ./internal/deploy`

Recreate `/root/superpan-ota-admin-verify` from the pinned Task 0 recipe, then run: `CGO_ENABLED=0 /root/superpan-ota-admin-verify/go/bin/go test -shuffle=on -count=20 ./internal/deploy`

- [ ] **Step 5: Build a separate acceptance-only fault harness.**

`ota-admin-acceptance` accepts only a caller-created isolated publication root and enumerated failure point, refuses `/www/wwwroot/ota.fanfh.cn`, refuses to run as the production service, and is never copied to `/opt/superpan-ota-admin`. It drives the real Linux filesystem path through each durability boundary, restarts recovery, and emits JSON evidence. Its source is compiled only by `build_linux_acceptance.ps1`; the production build script must structurally prove that the harness is absent.

- [ ] **Step 6: Commit.**

Commit: `feat: publish atomic OTA generations`

### Task 11: Add rollback, retention, and idempotent operations

**Files:**
- Create: `services/ota_admin/internal/deploy/rollback.go`
- Create: `services/ota_admin/internal/deploy/retention.go`
- Create: `services/ota_admin/internal/operation/service.go`
- Create: `services/ota_admin/internal/operation/service_test.go`
- Create: `services/ota_admin/internal/httpapi/history_handlers.go`
- Modify: `services/ota_admin/internal/httpapi/publish_handlers.go`

- [ ] **Step 1: Write failing tests.**

Cover password re-entry, target generation validation, parent fsync, duplicate operation IDs, concurrent publish/rollback exclusion, current plus two previous generations, unreferenced-only cleanup, cleanup failure without publish rollback, monotonic version floor, and the explicit no-client-downgrade behavior.

- [ ] **Step 2: Implement rollback/history/retention and return stable operation outcomes.**

- [ ] **Step 3: Pass focused and full backend tests.**

- [ ] **Step 4: Commit.**

Commit: `feat: add OTA rollback and retention`

### Task 12: Build the embedded Chinese admin page

**Files:**
- Create: `services/ota_admin/internal/webui/static/index.html`
- Create: `services/ota_admin/internal/webui/static/styles.css`
- Create: `services/ota_admin/internal/webui/static/app.js`
- Create: `services/ota_admin/internal/webui/assets.go`
- Create: `services/ota_admin/internal/webui/assets_test.go`
- Create: `services/ota_admin/internal/httpapi/integration_test.go`

- [ ] **Step 1: Write failing UI contract tests.**

Require login, forced password change, current version card, ZIP picker, release notes, progress, validation checklist, explicit publish confirmation, three-version history, password-confirmed rollback, loading/empty/error/read-only states, keyboard labels, and no arbitrary path/file browser.

- [ ] **Step 2: Implement one responsive page under `/admin/`.**

Use native browser APIs and same-origin fetch only; no CDN/runtime dependency. Keep the primary path to three actions: upload, confirm publish, rollback.

- [ ] **Step 3: Add browser-level happy/failure tests against a temporary server.**

Test login bootstrap/CSRF rotation, upload progress, validation failure, publish success, duplicate click idempotency, history, rollback, expired session, and read-only protection.

- [ ] **Step 4: Run full Go tests and build the static binary.**

Run on Windows: `& .tools\go-1.27.1\go\bin\go.exe -C services/ota_admin test ./...`

Recreate `/root/superpan-ota-admin-verify` from the pinned Task 0 recipe, then run: `CGO_ENABLED=0 /root/superpan-ota-admin-verify/go/bin/go test -shuffle=on -count=10 ./...`

Run: `powershell -File services/ota_admin/scripts/build_linux_amd64.ps1`

- [ ] **Step 5: Start the local server and visually inspect the actual page at `/admin/`.**

Do not use a mockup. Fix overflow, focus, progress, error, and small-window problems found in the running UI.

- [ ] **Step 6: Commit.**

Commit: `feat: add OTA publisher web console`

### Task 13: Package isolated production deployment

**Files:**
- Create: `deploy/ota-admin/superpan-ota-admin.service`
- Create: `deploy/ota-admin/ota.fanfh.cn.admin.nginx.conf`
- Create: `deploy/ota-admin/config.example.json`
- Create: `deploy/ota-admin/install.sh`
- Create: `deploy/ota-admin/verify.sh`
- Create: `tests/BaiduBatchTransfer.App.Tests/Updates/OtaDeploymentStructureTests.cs`
- Modify: `docs/deployment/ota-fanfh-cn.md`

- [ ] **Step 1: Write failing structural tests for loopback-only binding and exact `/admin/` proxying.**

Require no second proxy entry, no changes to `/health`/ACME/static release paths, no-cache for admin and signed metadata, stable downloadable when service is stopped, old-systemd-compatible hardening, root-owned config, isolated service user, and no access to `f.fanfh.cn` paths.

- [ ] **Step 2: Implement templates and an idempotent installer.**

Installer backs up only OTA admin/vhost targets, verifies SHA-256, creates least-privilege directories, runs `nginx -t`, reloads only on success, initializes the admin password once, and prints rollback instructions. It must not print the password into journald.

- [ ] **Step 3: Pass structural tests and shell syntax checks.**

- [ ] **Step 4: Commit.**

Commit: `deploy: package isolated OTA publisher`

### Task 14: Verify locally, deploy safely, and show the real page

**Files:**
- Create: `docs/testing/2026-09-05-web-ota-publisher-acceptance.md`
- Modify only if evidence requires it: `docs/deployment/ota-fanfh-cn.md`

- [ ] **Step 1: Run complete local verification from fresh build outputs.**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter FullyQualifiedName~Updates`

Run on Windows: `& .tools\go-1.27.1\go\bin\go.exe -C services/ota_admin test ./...`

Run in `/root/superpan-ota-admin-verify` provisioned in Task 0: `CGO_ENABLED=0 /root/superpan-ota-admin-verify/go/bin/go test -shuffle=on -count=10 ./...`

Run: `powershell -File services/ota_admin/scripts/build_linux_amd64.ps1`

Run:

```powershell
$publisherThumbprint = (Get-Content -LiteralPath '.artifacts\ota\publisher-thumbprint.txt' -Raw).Trim()
$previousReleaseDirectory = $null
foreach ($releaseVersion in @('1.0.1', '1.0.2', '1.0.3', '1.0.4')) {
    $releaseOutput = Join-Path $PWD ".artifacts\ota\production-$releaseVersion"
    $arguments = @('-File', 'scripts/build_ota_release.ps1', '-Production', '-Version', $releaseVersion, '-ProductionFeedUrl', 'https://ota.fanfh.cn/releases/win-x64/stable', '-OutputDirectory', $releaseOutput, '-SigningCertificateThumbprint', $publisherThumbprint)
    if ($null -ne $previousReleaseDirectory) { $arguments += @('-PreviousReleaseDirectory', $previousReleaseDirectory) }
    powershell @arguments
    if ($LASTEXITCODE -ne 0) { throw "Production OTA build failed for $releaseVersion" }
    $previousReleaseDirectory = $releaseOutput
}
```

The certificate creation script writes the non-secret thumbprint file atomically. Versions `1.0.1` through `1.0.4` are concrete strictly increasing candidates above the application's current `1.0.0`; they exercise update detection, rollback, and current-plus-two-previous retention. Step 2 must abort before mutation if the server has acquired a committed version at or above `1.0.1`, in which case update this plan and generate four new consecutive versions above the recorded permanent floor rather than guessing at runtime.

Expected: every suite passes and the final ZIP independently verifies with the public certificate.

- [ ] **Step 2: Re-baseline the production server before mutation.**

Record hashes/responses/certificates for `f.fanfh.cn`, `ota.fanfh.cn/health`, the active OTA vhost, stable content, and current disk space. Abort on unexplained drift.

- [ ] **Step 3: Deploy into an isolated test root and exercise real operations.**

Run compatibility probe, initialize admin, reject forged/oversized/path-traversal candidates, then upload and publish signed `1.0.1`, `1.0.2`, `1.0.3`, and `1.0.4` in order. Verify only `1.0.2`–`1.0.4` remain, roll back to `1.0.2`, and prove the permanent version floor remains `1.0.4`. Use the separately built `ota-admin-acceptance` harness to inject every enumerated commit-boundary failure and restart/recover. The harness must receive a newly created isolated root, refuse the production root, and be deleted from the server immediately after evidence capture. Keep production `stable` untouched.

- [ ] **Step 4: Install the loopback service and exact Nginx proxy.**

Back up targets, run `nginx -t`, reload, verify HTTPS `/admin/`, then change the one-time password through the browser. Confirm the service port is unreachable externally.

- [ ] **Step 5: Safely migrate the currently empty production stable directory to the relative generation link.**

Verify the migration precondition immediately before execution. If it is no longer empty or expected, stop without changing it.

- [ ] **Step 6: Publish the first signed production ZIP through the actual web page.**

Verify stable release JSON, manifest, signature, assets, Setup/Portable generation coherence, all no-cache headers, client SignedFeed acceptance, tamper rejection, and a full check/download/pending-update dry run without forcing app restart.

- [ ] **Step 7: Stop the admin service and prove static OTA remains available.**

Restart it afterward and verify recovery, session invalidation, and read-only state is clear.

- [ ] **Step 8: Prove `f.fanfh.cn` is unchanged and certificate renewal remains healthy.**

Compare the recorded vhost hash/body hash/certificate and run the existing renewal dry-run check.

- [ ] **Step 9: Open `https://ota.fanfh.cn/admin/` for the user and record evidence.**

Show the real deployed page, not a screenshot-only mock. Record commands, hashes, HTTP results, failure-injection results, rollback result, and known SmartScreen/self-signed trust limitations without recording credentials.

- [ ] **Step 10: Commit the acceptance record only after verification.**

Commit: `docs: record web OTA publisher acceptance`

## Final acceptance gate

- [ ] The private signing key is absent from Git, ZIPs, server, logs, SQLite, and deployment backups.
- [ ] Client rejects wrong key, missing/bad signature, tampered metadata/package, and signed replay before updater launch.
- [ ] Server rejects every malformed/unauthentic package and cannot publish without explicit confirmation.
- [ ] Atomic publish/recovery/rollback/retention tests pass at every failure boundary.
- [ ] `stable` remains downloadable while the admin service is stopped.
- [ ] `/admin/` is HTTPS-only through Nginx; backend is loopback-only.
- [ ] Current plus two previous immutable generations remain; historical version floor never decreases.
- [ ] Existing `f.fanfh.cn` content, vhost, certificate, and application remain unchanged.
- [ ] The user can log in, change the initial password, upload, validate, publish, and rollback from the browser without SSH or BaoTa.
