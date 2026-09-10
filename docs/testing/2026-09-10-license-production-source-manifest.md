# License production source manifest

This manifest is the allowlist for splitting the already-reviewed Task 12 and Task 13 licensing work from the dirty `codex/transfer-share-link-replacement` worktree. `Unrelated work = yes` means the file must be staged by reviewed hunks; the complete working-copy blob is forbidden.

| Commit group | Path | State in group | Licensing responsibility | Unrelated work in same file | Focused test / proof |
|---|---|---|---|---|---|
| boundary | `docs/superpowers/plans/2026-09-10-production-license-service-deployment.md` | new | Approved production deployment plan and task boundary | no | boundary verifier allowlist |
| boundary | `scripts/verify_license_change_boundary.ps1` | new | Enforce staged path, new-file, hunk, and runtime/secret/build exclusions | no | RED with empty index; GREEN with boundary-only index |
| boundary | `docs/testing/2026-09-10-license-production-source-manifest.md` | new | Human-auditable ownership and focused-test map | no | boundary verifier allowlist |
| task12 | `src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj` | modified | Immutable product ID, runtime modes, public trust metadata, and build-time fail-closed validation | yes | `OtaBuildConfigurationTests` |
| task12 | `src/BaiduBatchTransfer.App/AppBootstrap.cs` | modified | Register one license gate/state and inject it into remote-operation view models | yes | `AppBootstrapTests` |
| task12 | `src/BaiduBatchTransfer.App/App.xaml.cs` | modified | Acceptance smoke entry, activation prompt, and safe shutdown of authorization-waiting hosted services | yes | `LicenseAcceptanceSmokeRunnerTests`; `ApplicationShutdownCoordinatorTests` |
| task12 | `src/BaiduBatchTransfer.App/AppStartupController.cs` | modified | Split local recovery from authorized recovery; validate startup before releasing remote work | no | `AppStartupControllerTests`; `TransferRecoveryAuthorizationSourceTests` |
| task12 | `src/BaiduBatchTransfer.App/ApplicationShutdownCoordinator.cs` | modified | Convert host `ApplicationStopping` into one fail-closed UI shutdown attempt | no | `ApplicationShutdownCoordinatorTests`; `StandaloneShareCoordinatorHostedServiceTests` |
| task12 | `src/BaiduBatchTransfer.App/Features/Licensing/LicenseRuntimeAuthorizationState.cs` | new | Serialize activation recovery and release coordinators only after authorization | no | `LicenseOperationBoundaryTests` |
| task12 | `src/BaiduBatchTransfer.App/Features/Licensing/LicenseAcceptanceSmokeRunner.cs` | new | Read immutable build metadata, construct real gate, and restrict smoke mode to loopback acceptance builds | no | `LicenseAcceptanceSmokeRunnerTests` |
| task12 | `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs` | modified | Authorize before creating a new transfer operation | yes | `TransferWorkspaceViewModelTests` license cases |
| task12 | `src/BaiduBatchTransfer.App/Features/Shares/ShareWorkspaceViewModel.cs` | modified | Authorize share start, manual resume, and manual retry before mutation | yes | `ShareWorkspaceViewModelTests` license cases |
| task12 | `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsViewModel.cs` | modified | Authorize each new analytics refresh flight without reauthorizing joiners | no | `ShareAnalyticsViewModelTests` license cases |
| task12 | `src/BaiduBatchTransfer.App/Features/Common/WorkspaceRunPanelViewModel.cs` | modified | Authorize manual transfer resume/retry before durable mutation | yes | `WorkspaceRunPanelViewModelTests` license cases |
| task12 | `src/BaiduBatchTransfer.App/Features/Jobs/JobResultsViewModel.cs` | modified | Authorize manual share/transfer row actions | yes | `JobResultsViewModelTests` license cases |
| task12 | `src/BaiduBatchTransfer.App/Services/TransferCoordinatorHostedService.cs` | modified | Hold recovered transfer dispatch until runtime authorization | yes | `TransferCoordinatorHostedServiceTests` authorization cases |
| task12 | `src/BaiduBatchTransfer.App/Services/StandaloneShareCoordinatorHostedService.cs` | modified | Hold recovered share dispatch until authorization and fail closed on deferred start failure | no | `StandaloneShareCoordinatorHostedServiceTests` |
| task12 | `tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs` | modified | Prove shared DI lifetime and gate injection | yes | self |
| task12 | `tests/BaiduBatchTransfer.App.Tests/AppStartupControllerTests.cs` | modified | Prove rejection opens local UI but does not recover remote work | no | self |
| task12 | `tests/BaiduBatchTransfer.App.Tests/ApplicationShutdownCoordinatorTests.cs` | modified | Prove `ApplicationStopping` shutdown is single-shot and fail closed | no | self |
| task12 | `tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewModelTests.cs` | modified | Prove transfer rejection precedes job creation or remote precheck | yes | self, license cases only |
| task12 | `tests/BaiduBatchTransfer.App.Tests/Features/ShareWorkspaceViewModelTests.cs` | modified | Prove share rejection precedes start/resume/retry mutation | yes | self, license cases only |
| task12 | `tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewModelTests.cs` | modified | Prove all new refresh origins gate once and joined flights do not re-gate | no | self, license cases only |
| task12 | `tests/BaiduBatchTransfer.App.Tests/Features/WorkspaceRunPanelViewModelTests.cs` | modified | Prove manual resume/retry rejection precedes mutation | yes | self, license cases only |
| task12 | `tests/BaiduBatchTransfer.App.Tests/Features/JobResultsViewModelTests.cs` | modified | Prove row-action rejection precedes workflow mutation | yes | self, license cases only |
| task12 | `tests/BaiduBatchTransfer.App.Tests/Services/TransferCoordinatorHostedServiceTests.cs` | modified | Prove transfer coordinator waits for later authorization | yes | self, authorization cases only |
| task12 | `tests/BaiduBatchTransfer.App.Tests/Services/StandaloneShareCoordinatorHostedServiceTests.cs` | new | Prove share coordinator wait/release/failure/shutdown behavior | no | self |
| task12 | `tests/BaiduBatchTransfer.App.Tests/Updates/OtaBuildConfigurationTests.cs` | modified | Prove immutable ID, disabled debug, acceptance loopback, and production trust requirements | yes | self, license configuration cases only; exclude OTA acceptance-build hunks |
| task12 | `tests/BaiduBatchTransfer.App.Tests/Features/LicenseOperationBoundaryTests.cs` | new | Prove state transitions, single recovery, safe activation errors, and waiter release | no | self |
| task12 | `tests/BaiduBatchTransfer.App.Tests/Features/LicenseAcceptanceSmokeRunnerTests.cs` | new | Prove acceptance-only smoke, configuration confinement, real gate registration, and public-only build inputs | no | self |
| task12 | `scripts/build_local_license_acceptance.ps1` | new | Produce an acceptance-stamped WPF build from a generated loopback public config | no | `LicenseAcceptanceSmokeRunnerTests.LocalAcceptanceBuildScriptStampsOnlyPublicLoopbackConfiguration` |
| task13 | `docs/testing/2026-09-08-local-license-acceptance.md` | new | Record commands, product ID, ports, UI checks, counts, and no-production boundary | no | manual document audit; E2E output |
| task13 | `scripts/test_local_license_stack.ps1` | new | Drive disposable real-login multi-product license E2E and exact cleanup | no | script execution; port/runtime absence checks |
| task13 | `scripts/build_local_license_acceptance.ps1` | modified | Verify the built assembly carries only the acceptance stamp and immutable product ID | no | build script execution; WPF smoke in E2E |
| task13 | `tools/license_client_harness/SuperPan.Licensing.AcceptanceHarness.csproj` | new | Define the reusable-client acceptance executable | no | harness build; local E2E |
| task13 | `tools/license_client_harness/Program.cs` | new | Drive activate/authorize using explicit loopback config, deterministic fingerprint, isolated store, and injected UTC | no | local E2E |
| task13 | `tools/license_client_harness/packages.lock.json` | new | Lock harness dependency closure | no | `dotnet restore --locked-mode`; harness build |
| task13 | `services/ota_admin/internal/webui/browser_test.go` | modified | Assert every license-admin acceptance action executes exactly once | no | `go test ./... -count=1` |
| task13 | `tests/SuperPan.Licensing.Client.Tests/LicenseAuthorizationGateTests.cs` | modified | Prove deleted-card clearing and lease-expiry boundary with injected time | no | filtered `AuthorizationGate` tests |

## Explicit exclusions

- Never stage `.tmp/`, `.artifacts/`, `bin/`, `obj/`, runtime databases, generated keys, secrets, or deployment outputs.
- Never stage managed-browser, transfer-target-picker, share-analytics-preference, direct-background, workspace-log, result-export, or replacement-workbook hunks from the mixed Task 12 files.
- `services/ota_admin/.tmp_db_verify/`, `tmp-live-debug.db`, and `tmp-live-root/` are unknown/user-owned and must remain untouched.
