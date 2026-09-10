# Production License Service Deployment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Package, install, verify, and safely roll back the SuperPan licensing system on the existing `ota.fanfh.cn` host without changing the OTA stable feed or any other site.

**Architecture:** The existing OTA admin remains on `127.0.0.1:18091`; a separate `superpan-license` service uses `127.0.0.1:18092`, its own Unix user, SQLite database, keys, config, systemd unit, and backup root. The OTA admin reaches `/internal/v1/` over loopback with a dedicated shared credential. Nginx exposes only the two public `/license-api/v1/` endpoints, and that include is installed only after loopback installation, rollback rehearsal, capacity testing, and client-trust rehearsal pass.

**Tech Stack:** Go 1.27.1 static linux/amd64 binaries, SQLite, P-256 ECDSA, PowerShell, Bash, systemd v219-compatible units, BaoTa Nginx, .NET 8/WPF, Windows DPAPI.

---

## Non-negotiable gates

- Never build or deploy production artifacts from the current dirty worktree.
- Never stage a whole overlapping file merely because one licensing hunk belongs to this task.
- Never expose card plaintext, machine hashes, request payloads, passwords, bearer tokens, private signing material, databases, or TLS private keys in Git, logs, screenshots, reports, or shell history.
- Do not modify DNS, certificates, the OTA stable link or generations, ACME, `f.fanfh.cn`, or unrelated Nginx locations.
- Stop before server mutation unless the exact host, SSH host key, key-based recovery login, active-vhost hash, Nginx master PID, time sync, disk/memory floor, free ports, and pre-deployment OTA/other-site baselines are proven.
- Stop before public routing unless rollback, restart, load, trust-rotation, and off-host exposure tests satisfy the approved design.

## Execution environments

- Task 1 runs only in the current local feature worktree and disposable sibling worktrees.
- Tasks 2–4 implement and test code locally in the new clean deployment worktree.
- Tasks 5–8 implement local tooling; any real systemd/Nginx rehearsal in those tasks runs only on a separately named disposable staging VM after separate user approval, using staging-only keys, hostnames, data, and certificates.
- Task 9 is the sole production-mutation task. It requires a new explicit approval and regenerates/reads the production signing identity, rebuilds production clients from that identity, reruns capacity with the installed production binary while still loopback-only, and enables Nginx last.
- Staging identities or results must never be copied into production client trust or production evidence.

### Task 1: Produce clean, self-contained Task 12/13 commits

**Files:**
- Create/Commit: `docs/superpowers/plans/2026-09-10-production-license-service-deployment.md`
- Create: `scripts/verify_license_change_boundary.ps1`
- Create: `docs/testing/2026-09-10-license-production-source-manifest.md`
- Stage only reviewed licensing hunks from the Task 12/13 paths in `docs/superpowers/plans/2026-09-08-local-multi-product-license-system.md`

- [ ] **Step 1: Write the exact allowlist and a failing staged-boundary check**

The manifest must enumerate every Task 12/13 path, including the added shutdown/recovery files and tests discovered during review. For each path record: commit group (`boundary`, `task12`, or `task13`), new/modified, intended licensing responsibility, whether unrelated work exists in the same file, and the focused test that covers the hunk. The PowerShell check accepts one required `-CommitGroup` and fails on a staged path absent from that group, a required new licensing file absent from the index, a hunk classified as unrelated, or a tracked runtime/secret/build path. The boundary group contains only the approved deployment plan, this verifier, and the manifest.

- [ ] **Step 2: Run the check before staging**

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify_license_change_boundary.ps1 -CommitGroup boundary
```

Expected: FAIL because required licensing paths are not staged.

- [ ] **Step 3: Commit the boundary tooling first**

Stage only the approved deployment plan, verifier, and manifest, run `-CommitGroup boundary`, inspect the staged diff, and commit:

```powershell
git add docs/superpowers/plans/2026-09-10-production-license-service-deployment.md scripts/verify_license_change_boundary.ps1 docs/testing/2026-09-10-license-production-source-manifest.md
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify_license_change_boundary.ps1 -CommitGroup boundary
git diff --cached --check
git commit -m "chore: define license change boundary"
```

- [ ] **Step 4: Stage only approved Task 12 hunks**

First select only the `task12` group. Use `git add -p` for overlapping tracked files and explicit `git add -- <path>` only for reviewed new files. Do not stage `services/ota_admin/.tmp_db_verify/`, `.tmp/`, `.artifacts/`, `bin/`, `obj/`, runtime databases, generated keys, or unrelated managed-browser work. After Task 12 is verified and committed, repeat Steps 3–5 from that new HEAD for only the `task13` group.

- [ ] **Step 5: Materialize the staged snapshot in a disposable clean worktree**

```powershell
$repo = (git rev-parse --show-toplevel).Trim()
$verifyPath = Join-Path (Split-Path $repo -Parent) 'license-staged-verify'
$parentCommit = (git rev-parse HEAD).Trim()
$stagedTree = (git write-tree).Trim()
$temporaryCommit = ("License staged verification snapshot" | git commit-tree $stagedTree -p $parentCommit).Trim()
git worktree add --detach $verifyPath $temporaryCommit
git -C $verifyPath status --porcelain=v1
```

Expected: empty status output. The detached temporary commit contains exactly the current index and parent. It does not update a branch or include unstaged files.

- [ ] **Step 6: Verify the staged snapshot, then commit it in the source worktree**

For the Task 12 snapshot, run inside the disposable worktree:

```powershell
dotnet restore BaiduBatchTransfer.sln
dotnet build BaiduBatchTransfer.sln --no-restore
git diff HEAD^ HEAD --check
git status --porcelain=v1
```

Expected: all commands exit 0 and status is empty. Validate that `$verifyPath` is the exact sibling path created above, then remove only that disposable worktree. Back in the source worktree, rerun `scripts/verify_license_change_boundary.ps1 -CommitGroup task12` and commit; afterwards prove the committed tree equals `$stagedTree`:

```powershell
git worktree remove $verifyPath
git commit -m "feat: require licensing for remote operations"
if ((git rev-parse 'HEAD^{tree}').Trim() -cne $stagedTree) { throw 'Task 12 commit tree differs from verified staged tree.' }
```

Then clear only the now-empty index state, stage the Task 13 group against the new HEAD, rerun `-CommitGroup task13`, recreate the disposable snapshot, bootstrap the pinned Go toolchain inside that clean worktree, and run:

```powershell
dotnet restore tools/license_client_harness/SuperPan.Licensing.AcceptanceHarness.csproj
dotnet restore BaiduBatchTransfer.sln
powershell.exe -NoProfile -ExecutionPolicy Bypass -File services/ota_admin/scripts/bootstrap_go_toolchain.ps1
& .tools/go-1.27.1/go/bin/go.exe -C services/ota_admin test ./... -count=1
dotnet test tests/SuperPan.Licensing.Client.Tests/SuperPan.Licensing.Client.Tests.csproj --no-restore --filter "FullyQualifiedName~AuthorizationGate"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test_local_license_stack.ps1
git diff HEAD^ HEAD --check
git status --porcelain=v1
```

Expected: all commands exit 0, status is empty, ports `19090/19091` are closed, and licensing runtime artifacts are absent. Remove the validated disposable worktree, rerun the Task 13 boundary check, commit, and compare the committed tree with the second `$stagedTree`:

```powershell
git worktree remove $verifyPath
git commit -m "test: verify local multi-product licensing end to end"
if ((git rev-parse 'HEAD^{tree}').Trim() -cne $stagedTree) { throw 'Task 13 commit tree differs from verified staged tree.' }
```

If clean separation cannot produce checkout-buildable commits, stop and ask the user whether to first commit the overlapping non-license work; do not guess ownership.

- [ ] **Step 7: Create the production branch in a new clean sibling worktree**

```powershell
$deploymentPath = Join-Path (Split-Path $repo -Parent) 'license-production-deploy'
if (Test-Path -LiteralPath $deploymentPath) { throw 'Deployment worktree path already exists; stop for user direction.' }
git show-ref --verify --quiet refs/heads/codex/license-production-deploy
if ($LASTEXITCODE -eq 0) { throw 'Deployment branch already exists; stop for user direction.' }
git worktree add -b codex/license-production-deploy $deploymentPath HEAD
git -C $deploymentPath status --porcelain=v1
```

Expected: empty output. Tasks 2–10 run only in this clean worktree. Record its HEAD and require it as the source commit for both production binaries.

### Task 2: Enforce runtime trust and add loopback-only observability

**Files:**
- Modify: `services/ota_admin/internal/licensing/config/config.go`
- Create: `services/ota_admin/internal/licensing/config/trusted_file_linux.go`
- Create: `services/ota_admin/internal/licensing/config/trusted_file_other.go`
- Modify: `services/ota_admin/internal/licensing/config/config_test.go`
- Modify: `services/ota_admin/internal/licensing/signing/envelope.go`
- Modify: `services/ota_admin/internal/licensing/signing/envelope_test.go`
- Modify: `services/ota_admin/internal/licensing/httpapi/router.go`
- Modify: `services/ota_admin/internal/licensing/httpapi/middleware.go`
- Create: `services/ota_admin/internal/licensing/httpapi/health_test.go`
- Create: `services/ota_admin/internal/licensing/httpapi/load_bypass_test.go`
- Create: `services/ota_admin/internal/licensing/httpapi/load_metrics_test.go`
- Modify: `services/ota_admin/internal/licensing/store/store.go`
- Create: `services/ota_admin/internal/licensing/store/metrics.go`
- Create: `services/ota_admin/internal/licensing/store/metrics_test.go`
- Modify: `services/ota_admin/cmd/license-service/serve.go`
- Modify: `services/ota_admin/cmd/license-service/serve_test.go`

- [ ] **Step 1: Write failing trusted-file tests**

On Linux, config and secret readers must use descriptor-based traversal with `openat`/`O_NOFOLLOW`; reject symlinks in any component, non-root owner, untrusted group, group/world-writable parents, wrong mode, multiple hard links, wrong size, device files, and path replacement during open. License-only config/keys allow group `superpan-license`; the shared admin secret allows the process's single approved supplementary group. Windows keeps the local-acceptance behavior and does not weaken Linux checks.

- [ ] **Step 2: Write failing health and load-bypass tests**

Define `GET /internal/health` as loopback-only, unauthenticated, `200`, `application/json`, `Cache-Control: no-store`, fixed small body, and no database/card detail. It is never proxied by Nginx. Add optional load-test configuration that is disabled by default; when enabled, accept one separate 32-byte secret only from a loopback peer, compare its token in constant time, and bypass only public activation/validation rate limiting. It must not bypass validation, replay, signature, body, or admin controls. Instrument every licensing-store database operation through one error classifier that atomically counts SQLite `busy` and `locked` results before wrapping/returning them. When and only when load mode is enabled, `GET /internal/load-metrics` requires the same loopback-only load credential and returns only those aggregate counters; production mode returns not found.

- [ ] **Step 3: Implement the minimum secure readers and routes**

Add optional config fields for the load-test secret and an explicit enable flag. Reject a secret path when disabled, require one when enabled, and keep it distinct from encryption, lookup, signing, and admin secrets. Thread the health, aggregate store metrics, and limiter-bypass dependencies through `serve.go` without adding sensitive logs. Tests must inject a real locked SQLite fixture and prove the aggregate counter increments, while ordinary constraint/not-found errors do not.

- [ ] **Step 4: Run focused and full Go tests**

```powershell
& .tools/go-1.27.1/go/bin/go.exe -C services/ota_admin test ./internal/licensing/config ./internal/licensing/signing ./internal/licensing/store ./internal/licensing/httpapi ./cmd/license-service -count=1
& .tools/go-1.27.1/go/bin/go.exe -C services/ota_admin test ./... -count=1
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add services/ota_admin/internal/licensing services/ota_admin/cmd/license-service
git commit -m "fix: enforce production license runtime trust"
```

### Task 3: Build both production binaries from one immutable commit

**Files:**
- Create: `services/ota_admin/scripts/build_linux_license_bundle.ps1`
- Modify: `services/ota_admin/internal/compat/probe_test.go`

- [ ] **Step 1: Write failing bundle-contract tests**

Require a clean worktree; exact explicit source commit equal to HEAD; pinned Go toolchain; a Git-object source snapshot; and one atomic bundle containing `ota-admin`, `superpan-license`, both lowercase SHA-256 files, the deterministic source manifest, its hash, and the source commit. Build `./cmd/ota-admin` and `./cmd/license-service` with identical `CGO_ENABLED=0`, `GOOS=linux`, `GOARCH=amd64`, `-buildvcs=false`, `-trimpath`, and `-ldflags '-s -w'`. Exclude acceptance/load generators, config, keys, databases, and untracked files.

- [ ] **Step 2: Implement by reusing the existing immutable builder**

Factor or call the safe staging, source allowlist, Git archive, toolchain identity, hash, and atomic-publication functions from `build_linux_amd64.ps1`; do not read mutable sources after the snapshot is created.

- [ ] **Step 3: Run function-level tests without invoking the clean-tree entry point**

Dot-source the script's `-FunctionsOnly` mode from disposable test harnesses, proving its snapshot allowlist, dirty-tree refusal, artifact authentication, and atomic publication helpers. The not-yet-committed builder is expected to make the top-level clean-tree command refuse at this stage.

- [ ] **Step 4: Commit the builder, then prove reproducibility from clean HEAD**

```powershell
git add services/ota_admin/scripts/build_linux_license_bundle.ps1 services/ota_admin/internal/compat/probe_test.go
git commit -m "build: package immutable license deployment bundle"
git status --porcelain=v1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File services/ota_admin/scripts/build_linux_license_bundle.ps1 -SourceCommit (git rev-parse HEAD)
```

Expected: status is empty before the build. Run the builder twice from that same explicit commit, save the first binary/manifest hashes, and fail unless both binaries and the actual source-manifest file are identical. Generated ignored artifacts do not dirty the worktree and no staging directory remains.

### Task 4: Define deploy templates and exact ACL contracts

**Files:**
- Create: `deploy/license-service/config.example.json`
- Create: `deploy/license-service/superpan-license.service`
- Create: `deploy/license-service/ota.fanfh.cn.license-api.nginx.conf`
- Create: `services/ota_admin/internal/deploymentcontract/license_templates_test.go`
- Modify: `deploy/ota-admin/config.example.json`
- Modify: `deploy/ota-admin/superpan-ota-admin.service`

- [ ] **Step 1: Write failing in-module contract tests**

Tests live under the existing Go module and resolve the repository root before reading `deploy/`. Require license listener `127.0.0.1:18092`, OTA listener `127.0.0.1:18091`, database under `/var/lib/superpan-license`, keys under `/etc/superpan-license`, no OTA release/site paths in the license unit, and only the dedicated shared group in both services.

Run:

```powershell
& .tools/go-1.27.1/go/bin/go.exe -C services/ota_admin test ./internal/deploymentcontract -count=1
```

Expected: FAIL because templates are incomplete.

- [ ] **Step 2: Add the exact ACL matrix to the contract**

| Target | Owner:group | Mode |
|---|---|---|
| `/etc/superpan-license` | `root:superpan-license-admin` | `0710` |
| config, encryption key, lookup key, signing private key | `root:superpan-license` | `0640` |
| shared `admin.secret` | `root:superpan-license-admin` | `0640` |
| `response-signing.key-id`, SPKI DER, base64-SPKI text, and public hash | `root:root` | `0644` |
| config/public-identity atomic temporary files | creator `root`, destination group | `0600` until verified rename, then destination mode |
| `/var/lib/superpan-license` | `superpan-license:superpan-license` | `0700` |
| database, WAL, SHM | `superpan-license:superpan-license` | `0600` |
| `/var/backups/superpan-license` and snapshots | `root:root` | `0700` |

Both service users belong to `superpan-license-admin`; neither receives the other's primary group. Existing files must also pass owner/group/mode, one-hardlink, non-symlink, size/format, and trusted-parent checks. Temporary files are created inside the trusted destination directory with `O_EXCL`, flushed, ownership/mode/format verified by descriptor, and atomically renamed without following links.

- [ ] **Step 3: Add systemd and Nginx templates**

Use only systemd v219-supported directives. The license unit is non-root, has no capabilities, writes only its data directory, and reads its config/keys. The Nginx include contains exactly two exact-match public locations—activate and validate—pointing to `127.0.0.1:18092`; it contains no `server`, prefix-wide proxy, internal/health route, OTA/admin/ACME/root directive, caching, or client-supplied forwarding chain. Name the test-only header `X-SuperPan-Load-Test` and require `proxy_set_header X-SuperPan-Load-Test ""` in both locations. Also require fixed production `Host`/`X-Forwarded-Proto`, direct-peer-only `X-Real-IP`/`X-Forwarded-For`, `proxy_cache off`, request buffering on, an exact reviewed small `client_max_body_size`, and bounded connect/read/send timeouts.

- [ ] **Step 4: Make signing identity executable**

The installer will persist `/etc/superpan-license/response-signing.key-id`, atomically render it into config, reject placeholder IDs, export the P-256 public key as canonical SPKI DER plus base64 text and lowercase SHA-256, and prove the public key corresponds to the installed private key. The desktop builder consumes the base64 DER and key ID, never PEM private material.

- [ ] **Step 5: Run and commit**

```powershell
& .tools/go-1.27.1/go/bin/go.exe -C services/ota_admin test ./internal/deploymentcontract ./internal/config ./internal/licensing/config -count=1
git add deploy/license-service deploy/ota-admin/config.example.json deploy/ota-admin/superpan-ota-admin.service services/ota_admin/internal/deploymentcontract
git commit -m "deploy: define isolated license service contracts"
```

### Task 5: Implement transactional loopback-only installation and rollback

**Files:**
- Create: `deploy/license-service/install.sh`
- Create: `deploy/license-service/rollback.sh`
- Create: `deploy/license-service/test-install-v219.sh`
- Create: `services/ota_admin/cmd/license-db-snapshot/main.go`
- Create: `services/ota_admin/cmd/license-db-snapshot/main_test.go`
- Create: `services/ota_admin/scripts/build_linux_license_maintenance_tools.ps1`
- Create: `services/ota_admin/internal/deploymentcontract/license_install_test.go`
- Modify: `deploy/ota-admin/install.sh`
- Modify: `docs/deployment/ota-fanfh-cn.md`
- Create: `docs/testing/2026-09-10-license-staging-install.md`

- [ ] **Step 1: Write failing installer/rollback tests**

Require root, fixed targets, non-symlink inputs, exact source/binary/template hashes, clean source commit, fixed backup roots, systemd state capture, ACL matrix enforcement, config validation, Nginx master identity, and armed restoration traps. Model first install and upgrade separately.

- [ ] **Step 2: Implement persistent secret lifecycle**

Create secrets only when absent: independent 32-byte encryption and lookup keys, one 32-byte shared admin secret, and one P-256 response-signing key/key ID. Preserve existing valid secrets on upgrade. Back them up as persistent state tied to the non-rollback database; never restore a mismatched key set over a migrated database.

- [ ] **Step 3: Implement a coherent stopped-database snapshot tool**

The same-module `license-db-snapshot` tool accepts only an explicit database and destination under validated root-owned maintenance directories. It first proves the named service is stopped and no other writer holds the database, obtains an exclusive SQLite lock, checkpoints/truncates WAL coherently, creates the snapshot through SQLite (`VACUUM INTO` or the driver's online-backup API), and runs `PRAGMA quick_check` on the result. It refuses ordinary live-file copying, symlinks, unexpected WAL/SHM identity, existing destinations, or output outside the fixed maintenance root. Output contains only schema version, quick-check status, aggregate counts, and hashes.

- [ ] **Step 4: Prove schema rollback compatibility before mutation**

For upgrades, arm the restoration trap, stop the exact old service briefly, use `license-db-snapshot` to produce a coherent verified snapshot, and immediately restore the prior service state. Start the new binary on a temporary loopback port against that snapshot, stop it, then use the snapshot tool again to checkpoint and copy the migrated state coherently. Start the currently deployed old binary against the migrated snapshot and require health plus read-only semantic verification. If this fails, stop before replacing the running version unless the migrations have separately proven forward compatibility. Initial install records that no prior schema/binary exists.

- [ ] **Step 5: Install both same-commit binaries without public routing**

Verify the bundle, install/start `superpan-license`, wait for `GET http://127.0.0.1:18092/internal/health`, then transactionally replace the OTA admin binary/config/unit with its same-commit artifact and restart it against `http://127.0.0.1:18092` plus `/etc/superpan-license/admin.secret`. Do not install or include the public Nginx snippet yet.

- [ ] **Step 6: Pass local contract tests and commit all installer tooling**

Run local Go/PowerShell/Bash contract tests, then commit before using the tools on any staging host:

```powershell
& .tools/go-1.27.1/go/bin/go.exe -C services/ota_admin test ./cmd/license-db-snapshot ./internal/deploymentcontract -count=1
git add deploy/license-service deploy/ota-admin/install.sh docs/deployment/ota-fanfh-cn.md docs/testing/2026-09-10-license-staging-install.md services/ota_admin/cmd/license-db-snapshot services/ota_admin/scripts/build_linux_license_maintenance_tools.ps1 services/ota_admin/internal/deploymentcontract
git commit -m "deploy: install license services on loopback safely"
git status --porcelain=v1
```

Expected: status is empty before any staging build or rehearsal.

- [ ] **Step 7: Exercise success and every failure boundary on staging**

Inject failure after user/group creation, each secret, binary/config/unit install, license start, OTA admin replacement/restart, and health verification. Each case restores both binaries/configs/unit states or leaves an explicitly stopped recoverable state with one exact backup. The live database and matching keys remain together.

- [ ] **Step 8: Rehearse on CentOS 7/systemd v219**

Static Go tests are not sufficient. Before connecting, obtain separate approval for the exact disposable staging VM/host and confirm its snapshot/restore boundary. On that v219 system run:

```bash
sudo test -x /root/license-stage/fixture-bin/superpan-license
sudo test -x /root/license-stage/fixture-bin/ota-admin
sudo systemd-analyze verify /root/license-stage/superpan-license.service /root/license-stage/superpan-ota-admin.service /root/license-stage/var-lib-otaadmin-releases.mount
sudo bash /root/license-stage/test-install-v219.sh --bundle /root/license-stage/bundle --fixture-vhost /root/license-stage/ota.fanfh.cn.conf
```

Expected: fixture `ExecStart` targets and all referenced units exist; unit verification exits 0; the harness runs first-install, coherent WAL snapshot, upgrade, every named injected failure, rollback, and restart, then reports the exact expected owner/group/mode/unit/listener state. Restore the disposable VM snapshot afterward. No production hostname, key, database, or certificate is used.

- [ ] **Step 9: Commit only redacted staging-install evidence**

```powershell
git add docs/testing/2026-09-10-license-staging-install.md
git commit -m "docs: record staging license installation"
git status --porcelain=v1
```

### Task 6: Prove capacity on the installed binary before public exposure

**Files:**
- Create: `services/ota_admin/cmd/license-loadtest/main.go`
- Create: `services/ota_admin/cmd/license-loadtest/main_test.go`
- Create: `services/ota_admin/cmd/license-dbcheck/main.go`
- Create: `services/ota_admin/cmd/license-dbcheck/main_test.go`
- Create: `services/ota_admin/scripts/build_linux_license_test_tools.ps1`
- Create: `deploy/license-service/run-capacity-test.sh`
- Create: `deploy/license-service/superpan-license-loadtest.service`
- Create: `services/ota_admin/internal/deploymentcontract/license_capacity_test.go`
- Create: `docs/testing/2026-09-10-license-capacity-acceptance.md`

- [ ] **Step 1: Write failing generator and orchestration tests**

The generator accepts only an explicit loopback origin, fixed rate/duration, seeded test manifest, and secret through a protected file descriptor/path. Output is limited to aggregate counts, p50/p95, CPU, RSS, memory floor, SQLite busy/locked count, and health continuity. It never prints cards, machine hashes, nonces, payloads, keys, or signatures. The database checker accepts only a stopped-instance database under the validated disposable root, opens it read-only, runs `PRAGMA quick_check`, and verifies semantic aggregate invariants/row counts without printing rows or identifiers. Its only success output is `quick_check=ok` plus aggregate counts.

- [ ] **Step 2: Implement the tooling and pass local contract tests**

Implement the generator, database checker, same-commit linux tool builder, orchestration script, and tests. Run the Go tests in the dirty implementation worktree; the top-level builder must still refuse because its source is not committed.

```powershell
& .tools/go-1.27.1/go/bin/go.exe -C services/ota_admin test ./cmd/license-loadtest ./cmd/license-dbcheck ./internal/deploymentcontract -count=1
```

- [ ] **Step 3: Commit the capacity tooling**

```powershell
git add services/ota_admin/cmd/license-loadtest services/ota_admin/cmd/license-dbcheck services/ota_admin/scripts/build_linux_license_test_tools.ps1 deploy/license-service/run-capacity-test.sh deploy/license-service/superpan-license-loadtest.service services/ota_admin/internal/deploymentcontract docs/testing/2026-09-10-license-capacity-acceptance.md
git commit -m "test: gate license rollout on production capacity"
git status --porcelain=v1
```

Expected: status is empty before any clean-source build.

- [ ] **Step 4: Build and install the production-equivalent disposable instance**

After committing the generator/checker/build wrapper, rebuild the full deployment bundle and test tools from that same explicit clean source commit, rerun the staging installer so `/opt/superpan-license/superpan-license` has the identical hash, and verify the hash before load. Start that installed binary on `127.0.0.1:18093` through the dedicated temporary `superpan-license-loadtest.service`, never as root or through an interactive shell. Its `User`, primary/supplementary groups, `UMask`, limits, capability set, address-family restriction, and systemd sandbox directives must match production; only port and validated disposable data/config/secret paths differ. Assert UID/GID/groups/UMask/limits and the exact PID before load. Enable bypass only for this instance. Store counters reset only at process start; every classified `SQLITE_BUSY`/`SQLITE_LOCKED` event counts even when the request later succeeds. Sample its exact PID via `/proc`, sample host memory, query health concurrently, and query the authenticated loopback-only `/internal/load-metrics` before and after each phase. Record only counter deltas, never database contents.

- [ ] **Step 5: Run the fixed workload**

Seed marked disposable cards, then run exactly 70% valid validation, 10% idempotent activation, and 20% deliberate business rejection at 50 requests/second for 10 minutes, cool down, then 100 requests/second for 10 minutes. Test ordinary public rate limiting separately with bypass disabled.

- [ ] **Step 6: Enforce thresholds and exact cleanup**

Fail unless the `/internal/load-metrics` busy/locked deltas are zero, unexpected errors are below 0.1%, p95 is at most 250 ms, average CPU is below 70%, free memory always exceeds 1 GB, RSS growth is below 10%, and health is continuously 200. Stop the exact PID, run the same-commit `license-dbcheck`, require `PRAGMA quick_check=ok` and all semantic invariants, then remove only the validated disposable `18093` data/config/secret tree. Capacity failure leaves the public Nginx include absent.

- [ ] **Step 7: Commit only redacted staging capacity evidence**

Update only aggregate results in the capacity document, scan it for secrets/identifiers, and commit:

```powershell
git add docs/testing/2026-09-10-license-capacity-acceptance.md
git commit -m "docs: record staging license capacity"
git status --porcelain=v1
```

### Task 7: Build client trust and rehearse certificate rotation

**Files:**
- Create: `scripts/build_production_license_clients.ps1`
- Create: `scripts/test_license_spki_rotation.ps1`
- Create: `tools/license_production_smoke/SuperPan.Licensing.ProductionSmoke.csproj`
- Create: `tools/license_production_smoke/Program.cs`
- Create: `tools/license_production_smoke/packages.lock.json`
- Create: `tools/license_production_boundary_probe/SuperPan.Licensing.ProductionBoundaryProbe.csproj`
- Create: `tools/license_production_boundary_probe/Program.cs`
- Create: `tools/license_production_boundary_probe/packages.lock.json`
- Create: `tests/SuperPan.Licensing.Client.Tests/ProductionSmokeHarnessTests.cs`
- Create: `tests/SuperPan.Licensing.Client.Tests/ProductionBoundaryProbeTests.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Updates/ProductionLicenseBuildScriptTests.cs`
- Create: `docs/testing/2026-09-10-license-trust-rotation.md`

- [ ] **Step 1: Write failing Release-build tests**

Require `https://ota.fanfh.cn`, fixed SuperPan UUID `8270e8d7-1791-487d-ad76-0da7a0cbed12`, installed response-signing key ID and canonical base64 SPKI DER, distinct current/next lowercase TLS SPKI hashes, and a clean recorded source commit. Reject private PEM, placeholders, loopback HTTP, reused pins, malformed P-256 points, dirty source, and sensitive MSBuild output.

- [ ] **Step 2: Define the second-product acceptance artifact**

Build a separate Authenticode-signed, non-shipping deployment-smoke client in `tools/license_production_smoke` with a distinct immutable software UUID and the same production public trust. It supports only activate/authorize against the exact production origin, accepts card input only via stdin, uses the real DPAPI store and machine fingerprint provider, and emits only whitelisted result codes. The build wrapper accepts an explicit code-signing certificate thumbprint, resolves it from the Windows certificate store, refuses file/PFX/password inputs, and signs SuperPan, the smoke executable, and the boundary probe. For all three it independently verifies the expected signer identity, SHA-256 Authenticode digest, artifact hash, and the repository's existing timestamp-server/policy requirements before publication. The smoke/probe artifacts are never placed in the OTA stable feed.

Create a second, visibly test-stamped `license_production_boundary_probe` that is also non-shipping and Authenticode-signed. It compiles in the exact production origin/product/trust, accepts only an injected UTC value and a protected store root, and exercises cached authorization while the service is deliberately unavailable during the authorized initial-launch window. Tests prove the shipping SuperPan and second-product smoke client reject time, config, product, machine-fixture, and offline-transport overrides; only the boundary probe accepts UTC injection.

- [ ] **Step 3: Implement and test locally, then commit before any exact client build**

Run focused tests against the source implementations. The production wrapper's top-level clean-source build must refuse while files are uncommitted. Then commit all tooling and an evidence-template document:

```powershell
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --no-restore --filter "FullyQualifiedName~OtaBuildConfiguration|FullyQualifiedName~ProductionLicenseBuildScript"
dotnet test tests/SuperPan.Licensing.Client.Tests/SuperPan.Licensing.Client.Tests.csproj --no-restore --filter "FullyQualifiedName~ProductionSmokeHarness|FullyQualifiedName~ProductionBoundaryProbe"
git add scripts/build_production_license_clients.ps1 scripts/test_license_spki_rotation.ps1 tools/license_production_smoke tools/license_production_boundary_probe tests/SuperPan.Licensing.Client.Tests/ProductionSmokeHarnessTests.cs tests/SuperPan.Licensing.Client.Tests/ProductionBoundaryProbeTests.cs tests/BaiduBatchTransfer.App.Tests/Updates/ProductionLicenseBuildScriptTests.cs docs/testing/2026-09-10-license-trust-rotation.md
git commit -m "build: pin production license trust"
git status --porcelain=v1
```

Expected: status is empty.

- [ ] **Step 4: Build exact clients and rehearse A-to-B-to-A trust before public routing**

Use a local/staging TLS endpoint and the exact client builds: current=A/next=B, verify A, switch to B, verify, roll back to A, then produce the next manifest current=B/next=C. For initial launch, record that no legacy licensing client population exists; future rotations require the minimum supported production build manifest to contain B before switching. Verify the old certificate/private-key rollback package is under approved custody with a recorded destruction date at least 90 days away; record only identifiers/hashes, never private material.

- [ ] **Step 5: Verify the installed response-signing identity**

Compare installed key ID, canonical SPKI DER/base64/hash, and a non-secret challenge signature to prove the client public key matches the server private key.

- [ ] **Step 6: Commit only redacted staging trust evidence**

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test_license_spki_rotation.ps1
git add docs/testing/2026-09-10-license-trust-rotation.md
git commit -m "docs: record staging license trust rehearsal"
git status --porcelain=v1
```

### Task 8: Add the public route only after all loopback gates pass

**Files:**
- Create: `deploy/license-service/capture-baseline.sh`
- Create: `deploy/license-service/enable-public-route.sh`
- Create: `deploy/license-service/verify.sh`
- Modify: `services/ota_admin/internal/deploymentcontract/license_install_test.go`
- Modify: `docs/deployment/ota-fanfh-cn.md`

- [ ] **Step 1: Write failing public-route and restoration tests**

Require exact active-vhost hash, include exactly once, `nginx -t` before reload, same Nginx master PID, HTTPS/no-store on both public routes, and no external access to health/internal routes or ports `18091/18092`. Arm service-restoration traps before any stop test.

- [ ] **Step 2: Capture immutable pre-route baselines**

In a fixed root-owned `0700` backup directory record vhost/include hashes, certificate/SPKI, Nginx PID, OTA `/health`, stable metadata/files, both unit states, listeners, and named `f.fanfh.cn` probes. Do not record credentials, request bodies, card data, or SQLite contents.

- [ ] **Step 3: Install the include transactionally**

Only after Tasks 5–7 are green, copy the exact two-route snippet, add its one include line to the existing HTTPS server block, run `nginx -t`, reload the verified master, and verify host-local HTTPS using `curl --noproxy '*' --resolve ota.fanfh.cn:443:127.0.0.1`.

- [ ] **Step 4: Verify isolation and restoration**

Check listener PID/user with `ss`; verify firewall rules; stop the license service and prove OTA stable downloads remain; restore it even on assertion failure. Stop OTA admin and prove stable downloads remain and no internal license route appears; restore it. From an explicitly authorized off-host probe, verify ports `18091/18092` and `/internal/health`/`/internal/v1/` are unreachable.

- [ ] **Step 5: Rehearse route rollback and commit**

Remove/restore the include, validate/reload, prove all pre-route baselines, then enable it again and reverify. Commit only scripts/templates/docs, not generated baselines.

```powershell
git add deploy/license-service services/ota_admin/internal/deploymentcontract docs/deployment/ota-fanfh-cn.md
git commit -m "deploy: expose only verified public license routes"
```

### Task 9: Perform authorized production smoke and rollback rehearsal

**Files:**
- Create: `docs/testing/2026-09-10-license-production-acceptance.md`
- Modify only with observed non-secret evidence: `docs/deployment/ota-fanfh-cn.md`

- [ ] **Step 1: Obtain explicit server-mutation authorization**

Ask the user to approve the named host, maintenance window, upload of both same-commit binaries, OTA admin replacement, license installation, Nginx include, and off-host probes. No SSH or mutation occurs before approval.

- [ ] **Step 2: Upload and verify the immutable bundle**

Upload both binaries, the source manifest/bundle, public trust material, templates, and hashes to a root-only staging directory. Generate private service secrets on the server. Verify source commit and all hashes before install.

- [ ] **Step 3: Run the ordered rollout**

Capture baselines; install license service loopback-only; install the same-commit OTA admin; run restart/rollback rehearsal; run capacity acceptance against the installed production binary while it is still unexposed; read/export only the production public signing identity; rebuild and Authenticode-verify all production/smoke/probe clients from that identity; rehearse trust; enable the exact Nginx include; then run HTTPS smoke. Any earlier failure leaves the public route absent. Staging keys and client artifacts are never reused.

- [ ] **Step 4: Exercise the real multi-product flow**

Create two explicitly named deployment-smoke software records, one explicitly named package, and one card. Activate the signed SuperPan Release client, then authorize the separately signed second-product smoke client from the same store without card re-entry. Exercise package removal/re-add, software disable/re-enable, renewal, and unbind/rebind. Snapshot the encrypted lease, then during the explicitly authorized initial-launch window stop only `superpan-license`, use the separately test-stamped boundary probe at deterministic pre/post-30-minute UTC values, and restore the service through an already armed trap. Delete the smoke card so binding/lease state is removed; software/package deletion is unsupported, so retain the two software records disabled and retain the package with an unmistakable `DEPLOYMENT-SMOKE-RETAINED` name documented in the evidence. Reports contain only whitelisted status codes and counts.

- [ ] **Step 5: Rehearse complete rollback**

Restore the prior route/include, both binary/config/unit states, and verify prior baselines. Preserve the license database only with its matching keys. Reapply the new version and rerun verification. If old binary compatibility with migrated data was not proven, never attempt that rollback on live state.

- [ ] **Step 6: Harden SSH as a separately approved mutation**

Only after a second verified key-authenticated recovery session is open, back up and syntax-check SSH configuration, restrict root password login, and add anti-scanning controls. Validate the existing session and a new session before closing the recovery path.

- [ ] **Step 7: Commit the non-secret observed evidence**

Run the tracked-secret scan first, then commit only redacted hashes, counts, public identifiers, and pass/fail results:

```powershell
git add docs/testing/2026-09-10-license-production-acceptance.md docs/deployment/ota-fanfh-cn.md
git commit -m "docs: record production license rollout evidence"
git status --porcelain=v1
```

Expected: empty status. Generated baselines, credentials, cards, databases, private keys, and raw payloads remain outside Git.

### Task 10: Final clean-commit verification and handoff

**Files:**
- Modify: `docs/testing/2026-09-10-license-production-acceptance.md`
- Modify: `docs/deployment/ota-fanfh-cn.md`

- [ ] **Step 1: Verify the immutable binary source commit in a fresh detached worktree**

Read `binarySourceCommit` from the authenticated bundle/upload manifest used by the installer, not from the later documentation commit. Create a new exact sibling path only if neither it nor an attached worktree already exists:

```powershell
$binarySourceCommit = '<exact commit from authenticated installed bundle manifest>'
$sourceVerifyPath = Join-Path (Split-Path (git rev-parse --show-toplevel) -Parent) 'license-binary-source-verify'
if (Test-Path -LiteralPath $sourceVerifyPath) { throw 'Source verification path already exists.' }
git cat-file -e "$binarySourceCommit^{commit}"
git worktree add --detach $sourceVerifyPath $binarySourceCommit
Push-Location $sourceVerifyPath
git status --porcelain=v1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File services/ota_admin/scripts/bootstrap_go_toolchain.ps1
& .tools/go-1.27.1/go/bin/go.exe -C services/ota_admin test ./... -count=1
dotnet restore BaiduBatchTransfer.sln
dotnet restore tools/license_client_harness/SuperPan.Licensing.AcceptanceHarness.csproj
dotnet test BaiduBatchTransfer.sln --no-restore
dotnet build BaiduBatchTransfer.sln --no-restore
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test_local_license_stack.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File services/ota_admin/scripts/build_linux_license_bundle.ps1 -SourceCommit $binarySourceCommit
git status --porcelain=v1
Pop-Location
```

Expected: both detached-worktree status commands are empty and every test/build exits 0. Existing unrelated suite failures block merge/publish/deploy until resolved or isolated in a separate clean-baseline decision approved by the user. Validate the resolved sibling path and remove only this clean disposable worktree after evidence capture.

- [ ] **Step 2: Reproduce both artifact hashes**

Build the bundle twice from `$binarySourceCommit` and compare both binaries, source manifest, source bundle, and commit metadata. Verify the uploaded/installed hashes match. The later evidence commit is not and must never be reported as the installed binaries' source commit.

- [ ] **Step 3: Run the tracked-file and secret scan**

Fail on generated keys, databases, credentials, runtime directories, `bin`, `obj`, `.tmp`, `.artifacts`, load manifests containing cards, or private PEM in Git. Confirm deployment reports contain only public identifiers/hashes and aggregate results.

- [ ] **Step 4: Record final non-secret evidence**

Record source commit; binary/source hashes; public signing key ID/SPKI hash; current/next TLS SPKI hashes; ACL/unit/listener state; Nginx master continuity; pre/post OTA and other-site baselines; capacity aggregates; real-client status counts; off-host isolation; restart and rollback results; CentOS 7/v219 evidence; backup identifier; and 90-day TLS rollback-custody expiry.

Stage only those two documentation files, rerun the tracked-secret scan on the staged bytes, inspect the diff, and commit a second documentation-only evidence commit:

```powershell
git add docs/testing/2026-09-10-license-production-acceptance.md docs/deployment/ota-fanfh-cn.md
git diff --cached --check
git commit -m "docs: record final license verification evidence"
```

This documentation commit is not the installed binaries' source commit; both identifiers remain separately labeled.

- [ ] **Step 5: Confirm the evidence branch is clean and present branch choices**

```powershell
git status --porcelain=v1
```

Expected: empty after the second redacted evidence commit. After every test is green, use `superpowers:finishing-a-development-branch` to offer exactly: merge locally, push/create PR, keep branch, or discard. Never merge, push, delete a branch/worktree, or deploy without the user's explicit choice and required authorization.
