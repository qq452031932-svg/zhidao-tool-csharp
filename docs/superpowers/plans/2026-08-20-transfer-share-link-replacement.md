# Transfer Share Link Replacement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver a local Windows pipeline that imports title/link spreadsheets, transfers each selected source into explicitly selected Baidu accounts, creates exactly one permanent replacement share per account and exact transferred resource set, and saves a new workbook that preserves every source value while adding deterministic result columns.

**Architecture:** Keep import, transfer, share, and export as separately persisted stages joined by immutable source-row, transfer-operation, account, and exact remote-item identities. Extend the existing single persistent coordinator so transfer and share work share the same global/account gates, and make every external submission boundary recoverable without blind retries. Add a v8 forward migration, a bounded Excel inspection/mapping flow, authoritative job planning, a share adapter behind a live-evidence availability gate, and a page-streamed pivot export.

**Tech Stack:** .NET 8 (`net8.0` / `net8.0-windows`), C#, WPF/MVVM, SQLite/WAL with Microsoft.Data.Sqlite, DocumentFormat.OpenXml, WebView2/HttpClient-based Baidu adapter, xUnit.

---

## Scope and non-negotiable invariants

- The implementation follows `docs/superpowers/specs/2026-08-20-transfer-share-link-replacement-design.md`.
- Logged-in accounts are candidates only. The user must select the participating accounts and transfer mode before the application calculates transfer/share counts.
- Source workbook cells are immutable preserved values. Operational edits never rewrite the imported title, original URL, or raw cells.
- A successful transfer is shareable only when the exact non-empty remote item ID set has been durably committed. A descriptor, path, filename, or approximate presence check is insufficient.
- One `(account, exact remote item set, compatible settings)` has at most one accepted current share result. Unknown acceptance becomes `ResultPending`; it is never blindly retried.
- Historical and upgraded jobs remain share-disabled. Migration must not backfill share operations.
- User-visible output is Chinese. Exceptions, cookies, user IDs, paths, request headers, confirmation tokens, and raw response bodies are never displayed or exported.
- Live transfer/share execution stays disabled until the user-assisted field acceptance task proves the official Baidu behavior with a user-owned test account. Persistence, UI, scheduling, and fake-adapter tests may complete before that gate.

## Parallel work map

After Task 1 fixes the contracts, use these dependency-safe lanes:

- Wave 1 in parallel: Task 2 (schema/persistence), Task 3 (pure Excel inspection/mapping), Task 5 (pure workload/layout planning), and Task 12's read-only field-evidence collection.
- Wave 2: Task 4 requires Tasks 2+3; Task 6 requires Tasks 2+5.
- Wave 3: Task 7 requires Tasks 2+6; Task 8 requires Tasks 2+6+7; Task 12's production adapter implementation requires Tasks 1+8 and confirmed field evidence.
- Wave 4: Task 9 requires Tasks 7+8; Task 10 requires Tasks 2+4+5+8; Task 11 requires Tasks 4+5+8+9+10.
- Wave 5: Task 13 requires Tasks 2+8+9; Task 14 requires Tasks 1–13.

Do not parallel-edit these hotspots: `PersistenceModels.cs`, `IImportRepository.cs`, `SqliteImportRepository.cs`, `BatchConfirmationSummary.cs`, `BatchDraftValidator.cs`, `CreateBatchService.cs`, `CreateJobWizardViewModel.cs`, `AppBootstrap.cs`, or migration `008_TransferShareLinkReplacement.sql`.

## Task 1: Freeze exact transfer/share contracts and the live-evidence gate

**Files:**

- Create: `src/BaiduBatchTransfer.Core/Domain/RemoteItemIdentitySet.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/RemoteItemIdentity.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/TransferConfirmationResult.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/PermanentShareRequest.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/ShareCreationReceipt.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/BaiduShareRecord.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/BaiduSharePage.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/ShareOperationStatus.cs`
- Create: `src/BaiduBatchTransfer.Core/Abstractions/IBaiduShareAdapter.cs`
- Modify: `src/BaiduBatchTransfer.Core/Abstractions/IBaiduNetdiskAdapter.cs`
- Modify: `src/BaiduBatchTransfer.Core/Domain/TransferReceipt.cs`
- Modify: `src/BaiduBatchTransfer.Core/Domain/TransferConfirmation.cs`
- Modify: `src/BaiduBatchTransfer.BaiduAdapter/Http/LiveBaiduNetdiskAdapter.cs`
- Create: `src/BaiduBatchTransfer.BaiduAdapter/Http/LiveBaiduShareAdapter.cs`
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Create test: `tests/BaiduBatchTransfer.Core.Tests/BaiduAdapterResultTests.cs`
- Tests: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/AdapterContractTests.cs`
- Create test: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/LiveAdapterAvailabilityTests.cs`

- [ ] Write failing Core contract tests requiring `RemoteItemIdentitySet` to reject empty, blank, or duplicate IDs and to emit a stable canonical fingerprint. Differently ordered inputs containing the same unique IDs must canonicalize to the same set/fingerprint. Each `RemoteItemIdentity` carries an opaque exact ID plus a sanitized displayed top-level name for title fallback; identity equality/fingerprinting uses the ID, never the display name.
- [ ] Write failing adapter contract tests requiring transfer confirmation to distinguish `Absent`, `ExactPresent(ids)`, and `Ambiguous`, and requiring share creation/list results to carry a typed request ID, exact remote IDs, URL/code/settings, and typed acceptance (`Unknown` or `DefinitelyNotAccepted`).
- [ ] Run `dotnet test tests/BaiduBatchTransfer.Core.Tests/BaiduBatchTransfer.Core.Tests.csproj -c Release --filter "FullyQualifiedName~RemoteItemIdentitySet|FullyQualifiedName~BaiduAdapterResult"` and confirm RED from missing contracts.
- [ ] Implement immutable domain records with redacted `ToString()` values. Keep `IBaiduNetdiskAdapter` backward-compatible only where transfer-only tests need it; make share-enabled calls require exact evidence.
- [ ] Add `IBaiduShareAdapter` separately so existing transfer fakes do not silently become share-capable.
- [ ] Add an explicit adapter capability record (`TransferValidated`, `ShareValidated`, safe Chinese reason). Do not register live pipeline availability merely because the user is logged in.
- [ ] Keep live methods fail-closed with `NotValidated` until Task 12 field acceptance captures and codifies official request/response behavior. Do not guess endpoint names, status meanings, fs-id semantics, permanent-validity values, or pagination.
- [ ] Run the focused Core and Adapter tests; expect GREEN.
- [ ] Commit: `git commit -m "feat: define exact transfer and share contracts"`.

## Task 2: Add the v8 pipeline schema and strict upgrade/recovery validation

**Files:**

- Create: `src/BaiduBatchTransfer.Infrastructure/Database/Migrations/008_TransferShareLinkReplacement.sql`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/DatabaseMigrator.cs`
- Modify: `src/BaiduBatchTransfer.Core/Abstractions/PersistenceModels.cs`
- Modify: `src/BaiduBatchTransfer.Core/Domain/OperationStatus.cs`
- Modify: `src/BaiduBatchTransfer.Core/Domain/TransferOperation.cs`
- Modify: `src/BaiduBatchTransfer.Core/Abstractions/IImportRepository.cs`
- Modify: `src/BaiduBatchTransfer.Core/Abstractions/IJobRepository.cs`
- Create: `src/BaiduBatchTransfer.Core/Abstractions/IShareSchedulingStore.cs`
- Create: `src/BaiduBatchTransfer.Core/Abstractions/IAdapterCapabilityStore.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteShareSchedulingStore.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteAdapterCapabilityStore.cs`
- Modify: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/MigrationTests.cs`
- Modify: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/DatabaseRecoveryTests.cs`
- Modify: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/RepositoryTests.cs`
- Create test: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/ShareSchedulingStoreTests.cs`

- [ ] Write a failing v7→v8 migration test that expects a validated v7 backup, the new schema/indexes, no share-settings/share-operation backfill, and unchanged historical transfer state.
- [ ] Write failing malformed-v8 rollback and restore-v7-then-migrate tests. Assert `DatabaseMigrator.LatestVersion == 8` without rerunning migration inside the assertion.
- [ ] Write failing repository tests for positional raw JSON payloads, column definitions, batch share settings, output account snapshots, share operations, exact item sets, baseline pages, accepted share results, and keyset pagination.
- [ ] Run `dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~MigrationTests|FullyQualifiedName~DatabaseRecoveryTests|FullyQualifiedName~RepositoryTests|FullyQualifiedName~ShareSchedulingStoreTests"`; confirm schema/API RED.
- [ ] Create migration 008 without editing 001–007. Include:
  - `ImportSessions.ColumnMappingFingerprint`.
  - `ImportColumnDefinitions` with exactly one required `ShareUrl` role, zero-or-one `Title`, zero-or-one `ExtractionCode`, and any number of `Preserve` roles. Enforce the mapped-role uniqueness with a partial unique index while the application validator rejects a missing link role.
  - `StagedImportRowPayloads` with `SourceTitle`, `ResolvedOutputTitle`, `OriginalShareUrl`, positional `RawValuesJson`.
  - `BatchShareSettings`; absence means sharing disabled.
  - `BatchOutputAccounts` with stable output ordinal/label and planned counts.
  - `BatchJobs.ShareOperationCount` and `ResourceItems.ResolvedOutputTitle`.
  - `ShareOperations`, `ShareOperationRemoteItems`, `ShareDiscoveryBaselines`, and `BaiduShareResults`, with partial unique keys preventing duplicate active create work.
  - `BatchShareDecisionSnapshots`, one row per participating account when a conflict decision is needed, containing decision, discovery fingerprint/counts, generation, and confirmation time.
  - `AdapterCapabilityAcceptances`, keyed by adapter kind + protocol version + adapter build version and containing only acceptance time and a nonsecret evidence hash.
  - Rebuild `TransferOperations` transactionally so the `Status` CHECK also admits `Paused`. Copy every existing column byte-for-byte, recreate every v1–v7 index/FK/partial unique constraint, validate row counts/FKs/schema fingerprint inside the migration transaction, and roll back the entire v8 migration on any mismatch.
  - Status/lease/batch/account/resource and export indexes beginning with equality predicates followed by stable cursor fields.
- [ ] Implement parameterized repositories and a deferred read / IMMEDIATE write store. All state transitions must be conditional CAS updates inside one transaction.
- [ ] Run focused migration/recovery/repository tests; expect GREEN and schema fingerprint validation.
- [ ] Commit: `git commit -m "feat: persist transfer share pipeline state"`.

## Task 3: Inspect Excel safely and require confirmed column mapping

**Files:**

- Create: `src/BaiduBatchTransfer.Core/Abstractions/TabularImportModels.cs`
- Create: `src/BaiduBatchTransfer.Core/Services/BaiduShareUrlExtractor.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/Excel/OpenXmlWorkbookInspector.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/Excel/ExcelColumnSuggestionEngine.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Excel/OpenXmlImportReader.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Excel/OpenXmlTemplateWriter.cs`
- Modify: `src/BaiduBatchTransfer.Core/Abstractions/ImportSourceRow.cs`
- Modify test: `tests/BaiduBatchTransfer.Infrastructure.Tests/Excel/OpenXmlStreamingTests.cs`
- Create test: `tests/BaiduBatchTransfer.Infrastructure.Tests/Excel/ExcelColumnMappingTests.cs`

- [ ] Write failing tests for known headers, unfamiliar headers, headerless sheets, URL-content detection, nearest-left title suggestion, missing title/code, manual remap, mandatory link mapping, and the 20-row inspection cap.
- [ ] Add adversarial cases for external links, malformed Baidu-like URLs, formula-like text, blank cells, whitespace preservation, shared-string bounds, oversized rows, macros, and external workbook relationships.
- [ ] Run `dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~ExcelColumnMapping|FullyQualifiedName~OpenXmlStreaming"`; confirm RED.
- [ ] Implement a bounded inspector that reads only the first worksheet, headers, and at most 20 sample data rows without creating an import session.
- [ ] Implement pure deterministic suggestions and a confirmed mapping fingerprint. Extract only the supported Baidu URL substring from a link cell; preserve the untrimmed original cell separately.
- [ ] Change the streaming reader to require `ConfirmedImportColumnMapping` and return immutable ordered raw values. Preserve operational normalization separately from raw values.
- [ ] Add the source-title column to the template.
- [ ] Run focused tests; expect GREEN.
- [ ] Commit: `git commit -m "feat: inspect and map excel source columns"`.

## Task 4: Persist raw source rows atomically and expose mapping UI

**Files:**

- Modify: `src/BaiduBatchTransfer.Application/Imports/ImportStagingService.cs`
- Modify: `src/BaiduBatchTransfer.Application/Imports/DraftRowEditingService.cs`
- Modify: `src/BaiduBatchTransfer.Application/Imports/BulkPrecheckService.cs`
- Create: `src/BaiduBatchTransfer.Application/Imports/ResolveMissingTitlesService.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/Repositories/SqliteImportRepository.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Imports/LocalImportWorkflow.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Imports/ImportViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Imports/ImportView.xaml`
- Create: `src/BaiduBatchTransfer.App/Features/Imports/ExcelColumnMappingViewModel.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Imports/ExcelColumnMappingWindow.xaml`
- Create: `src/BaiduBatchTransfer.App/Features/Imports/ExcelColumnMappingWindow.xaml.cs`
- Create: `src/BaiduBatchTransfer.App/Services/IExcelColumnMappingPresenter.cs`
- Create: `src/BaiduBatchTransfer.App/Services/ExcelColumnMappingPresenter.cs`
- Modify tests: `tests/BaiduBatchTransfer.Application.Tests/Imports/ImportStagingServiceTests.cs`
- Modify tests: `tests/BaiduBatchTransfer.Application.Tests/Imports/DraftWorkflowServicesTests.cs`
- Modify tests: `tests/BaiduBatchTransfer.Application.Tests/Imports/BulkPrecheckServiceTests.cs`
- Modify tests: `tests/BaiduBatchTransfer.App.Tests/Features/ImportViewModelTests.cs`
- Modify tests: `tests/BaiduBatchTransfer.App.Tests/Features/LocalExcelImportIntegrationTests.cs`

- [ ] Write failing tests proving mapping cancellation creates no session, confirmed mapping survives durable resume, and each staged row/raw payload commits or rolls back together in the same chunk transaction.
- [ ] Add tests proving link/code edits do not change `OriginalShareUrl`, source title, raw JSON, or mapping fingerprint.
- [ ] Add failing missing-title tests: before a share-enabled job may be confirmed, blank/missing source titles are resolved through bounded `InspectShareAsync` metadata using one explicitly selected healthy account; persist `OuterName` or `OuterName 等 N 项` in `StagedImportRowPayloads.ResolvedOutputTitle`. A user title is copied unchanged into that field without a network call. An unresolved/failed inspection remains a stable draft error and cannot silently create a share-enabled job.
- [ ] Add a 200,000-row cancellation/resume case with page/chunk size ≤1,000 and bounded memory.
- [ ] Run the focused Import and App tests; confirm RED.
- [ ] Refactor staging so row ID and payload are produced together and persisted atomically. Resume must advance only past rows for which both records exist.
- [ ] Implement `ResolveMissingTitlesService` as a cancellable, resumable, page/chunk-bounded pass. Reuse successful bulk-precheck metadata where present, never switch accounts silently, and invalidate the resolved fallback only when the source URL/code changes—not when unrelated raw columns change.
- [ ] Implement pick → inspect → Chinese mapping dialog → confirmed streaming import. Keep the sample/path scoped to the modal; persist only the base source filename.
- [ ] Keep the current draft grid keyset-paged; never materialize all source rows in an `ObservableCollection`.
- [ ] Run focused tests; expect GREEN.
- [ ] Commit: `git commit -m "feat: preserve mapped excel source rows"`.

## Task 5: Make account selection and workload counts authoritative

**Files:**

- Modify: `src/BaiduBatchTransfer.Application/Jobs/BatchConfirmationSummary.cs`
- Modify: `src/BaiduBatchTransfer.Application/Jobs/BatchDraftValidator.cs`
- Create: `src/BaiduBatchTransfer.Application/Jobs/BatchWorkloadPlanner.cs`
- Create: `src/BaiduBatchTransfer.Application/Jobs/ResultColumnLayoutBuilder.cs`
- Create: `src/BaiduBatchTransfer.Application/Jobs/ResolvedOutputTitleBuilder.cs`
- Modify: `src/BaiduBatchTransfer.Application/Jobs/ReplicationPreflightEstimator.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Jobs/WizardContracts.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Jobs/AccountAllocationRowViewModel.cs`
- Modify tests: `tests/BaiduBatchTransfer.Application.Tests/Jobs/CreateBatchServiceTests.cs`
- Modify tests: `tests/BaiduBatchTransfer.Application.Tests/Jobs/ReplicationPreflightEstimatorTests.cs`
- Modify tests: `tests/BaiduBatchTransfer.App.Tests/Features/CreateJobWizardViewModelTests.cs`

- [ ] Write failing count tests for:
  - 200,000 rows + 10 logged accounts + only A selected = 200,000 transfers and 200,000 shares.
  - A/B/C distribution = 200,000 total transfers and shares.
  - A/B/C replication = 600,000 transfers and shares.
  - a selected account with sharing disabled keeps its transfer count and has zero share count.
  - row-account mapping uses its actual mapped account and does not multiply by all logged accounts.
- [ ] Write failing tests for source columns + two system columns + three columns per output account exceeding Excel’s 16,384-column limit.
- [ ] Write failing confirmation-token tests for account selection/order, allocation, `ShareEnabled`, random/fixed code mode, fixed code value, existing-share policy, mapping fingerprint, and generated account labels.
- [ ] Write failing pure title tests: nonblank user title wins unchanged; blank/missing title uses the one displayed top-level name; multiple top-level items use exactly `第一个资源名称 等 N 项`; unsafe/empty display names yield a stable Chinese fallback without changing source cells.
- [ ] Run focused Job tests; confirm RED.
- [ ] Implement one planner used by both preview and confirmation. UI properties are hints only and cannot authorize writes.
- [ ] Add conservative database/index/WAL estimates for share operations/results and output snapshots.
- [ ] Generate deterministic safe account labels and collision-free system headers (`（系统2）`, `（系统3）`, …) without exposing account IDs.
- [ ] Run focused tests; expect GREEN.
- [ ] Commit: `git commit -m "feat: plan transfer and share workloads"`.

## Task 6: Persist share settings and precreate the pipeline atomically

**Files:**

- Modify: `src/BaiduBatchTransfer.Application/Jobs/CreateBatchService.cs`
- Modify: `src/BaiduBatchTransfer.Application/Jobs/BatchDraftValidator.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/Repositories/SqliteJobRepository.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteShareSchedulingStore.cs`
- Modify tests: `tests/BaiduBatchTransfer.Application.Tests/Jobs/CreateBatchServiceTests.cs`
- Modify tests: `tests/BaiduBatchTransfer.Application.Tests/Jobs/BatchTestDatabase.cs`
- Modify tests: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/ShareSchedulingStoreTests.cs`

- [ ] Write a failing real-SQLite test requiring job/resources/transfer operations/share settings/output snapshots/share operations/audit/import-session commit to succeed or roll back as one transaction.
- [ ] Add failure injection after transfer-operation insert and after share-operation insert; assert zero partial job graph.
- [ ] Add upgrade tests proving jobs without `BatchShareSettings` cannot acquire share work.
- [ ] Run the focused tests; confirm RED.
- [ ] Persist immutable share settings and output-account layout in confirmation. Precreate one `WaitingTransfer` share operation only for each planned share-enabled transfer operation.
- [ ] Copy the already resolved title from each staged payload into `ResourceItems.ResolvedOutputTitle` during confirmation. Reject a share-enabled included row whose resolved title is still empty, so later transfer failure cannot leave the mandatory export column unset.
- [ ] Bind share operation to transfer operation and resource/account identity, but do not invent remote IDs before transfer completion.
- [ ] Use prepared commands and streaming inserts. Do not materialize the multiplied operation graph for 200k×N in managed memory.
- [ ] Run focused tests, including the existing 200k prepared-write case in isolation; expect GREEN.
- [ ] Commit: `git commit -m "feat: create durable transfer share jobs"`.

## Task 7: Harden transfer completion into an exact atomic handoff

**Files:**

- Modify: `src/BaiduBatchTransfer.Application/Scheduling/OperationExecutor.cs`
- Modify: `src/BaiduBatchTransfer.Application/Scheduling/OperationReconciliationService.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteTransferSchedulingStore.cs`
- Modify: `src/BaiduBatchTransfer.Core/Abstractions/IJobRepository.cs`
- Modify tests: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/OperationExecutorTests.cs`
- Modify tests: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/ReconciliationTests.cs`
- Modify tests: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/CrashBoundaryTests.cs`
- Modify tests: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/SqliteSchedulingStoreTests.cs`

- [ ] Write failing tests for transfer success with empty IDs, descriptor-only confirmation, multiple approximate matches, crash before exact-ID commit, and user-requested blind retry after ambiguous acceptance.
- [ ] Assert that all those share-enabled cases remain `ResultPending` and never release share work.
- [ ] Write a failing real-SQLite test requiring transfer success, exact ID-set persistence, transfer outcome/counters, and share `WaitingTransfer → WaitingShare` handoff to commit atomically.
- [ ] Add failing title-resolution tests at the same atomic boundary. The pre-transfer resolved title must survive transfer failure/`ResultPending`. On successful exact transfer, displayed identity names may only fill a legacy/transfer-only blank via `ResolvedOutputTitleBuilder`; they must never overwrite a persisted user title or precheck fallback.
- [ ] Run focused scheduling tests; confirm RED.
- [ ] Implement `CompleteSucceededWithEvidenceAsync` as one IMMEDIATE transaction. Exact remote IDs must be canonicalized and non-empty before a share-enabled transfer can become `Succeeded`.
- [ ] Tighten reconciliation so `ExactPresent(ids)` may complete, `Absent` may permit a safe transfer retry only when the adapter proves definitely not accepted, and `Ambiguous` never guesses/retries/shares.
- [ ] Preserve current transfer-only compatibility for jobs with no share settings.
- [ ] Run focused tests; expect GREEN.
- [ ] Commit: `git commit -m "fix: require exact transfer evidence before sharing"`.

## Task 8: Execute permanent shares idempotently

**Files:**

- Create: `src/BaiduBatchTransfer.Application/Scheduling/ShareOperationExecutor.cs`
- Create: `src/BaiduBatchTransfer.Application/Scheduling/ShareReconciliationPolicy.cs`
- Create: `src/BaiduBatchTransfer.Application/Scheduling/ExistingShareDecisionService.cs`
- Create: `src/BaiduBatchTransfer.Application/Scheduling/ExistingShareDecisionModels.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteShareSchedulingStore.cs`
- Create tests: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/ShareOperationExecutorTests.cs`
- Extend tests: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/ShareSchedulingStoreTests.cs`

- [ ] Write failing tests for permanent/unlimited/random code and permanent/unlimited/fixed code requests; fixed code must satisfy Baidu format rules and never appear in `ToString()`/logs.
- [ ] Write failing existing-share tests:
  - exact remote set + compatible permanent/unlimited settings → prompt decision required.
  - `Reuse` persists the existing URL/code without create.
  - `Regenerate` creates a new link and never cancels the old one.
  - `Cancel` leaves work paused.
  - incompatible settings cannot silently reuse.
- [ ] Write failing durable-decision tests: after exact IDs are known, discovery aggregates compatible/incompatible counts by account; the service pauses affected share work, returns one batch summary, validates a confirmation token over the exact discovery snapshot, persists the batch default plus per-account overrides, and resumes the same operations after restart. A changed share/discovery snapshot makes the token stale with zero writes.
- [ ] Write failing submission-boundary tests: definitely-not-accepted transient failures may retry up to 3 with typed server wait; unknown acceptance becomes `ResultPending` after the first create attempt and never blind retries.
- [ ] Run focused share tests; confirm RED.
- [ ] Implement preflight baseline paging and exact-set/settings comparison before creation. Persist the user’s batch-scoped default and per-account overrides in `BatchShareDecisionSnapshots` so 200k rows do not each open a dialog; surface only conflicts needing a different decision.
- [ ] Persist descriptor/request ID before submission, response/request ID before interpreting the result, and accepted result before releasing completion counters.
- [ ] Ensure share opt-out produces stable `未启用分享` output without external calls.
- [ ] Run focused tests; expect GREEN.
- [ ] Commit: `git commit -m "feat: execute idempotent permanent shares"`.

## Task 9: Reconcile share results and integrate one bounded coordinator

**Files:**

- Create: `src/BaiduBatchTransfer.Application/Scheduling/ShareOperationReconciliationService.cs`
- Modify: `src/BaiduBatchTransfer.Application/Scheduling/TransferCoordinator.cs`
- Modify: `src/BaiduBatchTransfer.Application/Scheduling/StartupRecoveryService.cs`
- Modify: `src/BaiduBatchTransfer.Core/Domain/OperationStatus.cs`
- Modify: `src/BaiduBatchTransfer.Core/Domain/TransferOperation.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteShareSchedulingStore.cs`
- Modify tests: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/TransferCoordinatorTests.cs`
- Modify tests: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/CrashBoundaryTests.cs`
- Create tests: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/ShareReconciliationTests.cs`

- [ ] Write failing reconciliation cases with a persisted pre-create baseline:
  - exactly one newly visible compatible share → accept.
  - zero matches before timeout → remain pending/ask user; do not regenerate.
  - multiple matches → remain ambiguous.
  - eventual consistency reveals the one match on a later bounded page/poll.
- [ ] Write failing crash cases at reserve, prepare, submit, response-ID persistence, result persistence, and counter update.
- [ ] Write failing concurrency tests proving global network limit 2, account limit 1, `ResultPending` does not occupy a worker but continues to lock the account/exact resource set, and account login/verification pauses only that account.
- [ ] Write failing durable-pause tests for transfer operations: login/verification/user-intervention outcomes transition the affected operation to `Paused`, release its worker lease without losing its assigned account/resource/target slot/submission evidence, survive restart, appear as `暂停` in projections, and return to the correct safe state only through explicit resume/reconciliation. Other accounts continue.
- [ ] Run focused coordinator/crash tests; confirm RED.
- [ ] Generalize the existing coordinator to lease transfer and share operations from one state model; do not launch a second independent coordinator.
- [ ] Add the `Paused` domain transitions and conditional SQLite updates. Scheduler claims and exhaustion logic must exclude paused work while batch/account summaries count it separately from waiting/failed/result-pending work.
- [ ] Recover expired pre-submit share leases to `WaitingShare`; recover submitting leases to `ResultPending`; run share recovery before scheduler start.
- [ ] Finalize a batch only when transfer and share terminal/pending rules are satisfied. A share failure must not erase transfer success or original URL.
- [ ] Run focused tests; expect GREEN.
- [ ] Commit: `git commit -m "feat: recover and schedule share operations"`.

## Task 10: Stream one-row-per-resource replacement workbooks

**Files:**

- Create: `src/BaiduBatchTransfer.Core/Abstractions/IBatchResultProjectionRepository.cs`
- Create: `src/BaiduBatchTransfer.Application/Jobs/ReplacementWorkbookModels.cs`
- Create: `src/BaiduBatchTransfer.Application/Jobs/ReplacementWorkbookFileNameBuilder.cs`
- Create: `src/BaiduBatchTransfer.Application/Jobs/ReplacementWorkbookExportService.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/Repositories/SqliteBatchResultProjectionRepository.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/Excel/OpenXmlReplacementResultWriter.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Excel/OpenXmlResultWriter.cs` only if shared sanitization must be extracted.
- Modify tests: `tests/BaiduBatchTransfer.Application.Tests/Jobs/PagingAndStatisticsTests.cs`
- Create tests: `tests/BaiduBatchTransfer.Application.Tests/Jobs/ReplacementWorkbookExportTests.cs`
- Create tests: `tests/BaiduBatchTransfer.Infrastructure.Tests/Excel/ReplacementResultWriterTests.cs`

- [ ] Write failing layout tests for original-column order, mandatory `转存结果_原分享链接`, mandatory `转存结果_解析后标题`, dynamic per-account link/code/status groups, and deterministic collision suffixes.
- [ ] Assert every exported row has `转存结果_解析后标题`: it equals the preserved user title when nonblank, otherwise the stable persisted fallback (`单个显示名称` or `第一个资源名称 等 N 项`). Pasted/no-title imports and Excel files with no title mapping must follow the same rule.
- [ ] Write failing row tests for distribution blanks, replication values, disabled sharing, transfer failure, share failure, and successful permanent shares. Failure rows must preserve all original values/title/original URL and leave new URL/code blank.
- [ ] Add one combined regression with a missing source title, every selected account share-disabled, and a transfer failure; the mandatory `转存结果_解析后标题` must still contain the pre-transfer resolved fallback.
- [ ] Write failing safety tests for formula injection in every raw/system text cell, control characters, duplicate/empty/very-long account names, rollover sheets, cancellation, and partial-file cleanup.
- [ ] Run focused export tests; confirm RED.
- [ ] Implement resource-keyset paging by `CandidateSequence`, then page-scoped account results ordered by output ordinal. Keep memory to one resource page plus `pageSize × participatingAccounts` results.
- [ ] Write a brand-new workbook to a temporary sibling file, validate it, then atomically promote to `原文件名_转存分享结果_yyyyMMddHHmmss.xlsx`. Never overwrite the source workbook.
- [ ] Preserve raw values as explicit safe text; generated row numbers may remain numeric.
- [ ] Run focused tests; expect GREEN.
- [ ] Commit: `git commit -m "feat: export replacement share workbooks"`.

## Task 11: Add the Chinese wizard and results workflow

**Files:**

- Modify: `src/BaiduBatchTransfer.App/Features/Jobs/CreateJobWizardViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Jobs/CreateJobWizardView.xaml`
- Create: `src/BaiduBatchTransfer.App/Features/Jobs/JobResultsViewModel.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Jobs/JobResultsView.xaml`
- Create: `src/BaiduBatchTransfer.App/Features/Jobs/JobResultsView.xaml.cs`
- Create: `src/BaiduBatchTransfer.App/Services/IResultSaveDialog.cs`
- Create: `src/BaiduBatchTransfer.App/Services/ResultSaveDialog.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Jobs/ExistingShareDecisionViewModel.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Jobs/ExistingShareDecisionWindow.xaml`
- Create: `src/BaiduBatchTransfer.App/Features/Jobs/ExistingShareDecisionWindow.xaml.cs`
- Create: `src/BaiduBatchTransfer.App/Services/IExistingShareDecisionPresenter.cs`
- Create: `src/BaiduBatchTransfer.App/Services/ExistingShareDecisionPresenter.cs`
- Modify: `src/BaiduBatchTransfer.App/MainWindowViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/MainWindow.xaml`
- Modify tests: `tests/BaiduBatchTransfer.App.Tests/Features/CreateJobWizardViewModelTests.cs`
- Create tests: `tests/BaiduBatchTransfer.App.Tests/Features/JobResultsViewModelTests.cs`
- Modify tests: `tests/BaiduBatchTransfer.App.Tests/MainWindowViewModelTests.cs`

- [ ] Write failing ViewModel tests proving only active/enabled/nondeleted accounts appear; single account defaults but still requires confirmation; multiple accounts require explicit distribution/replication choice and explicit selection.
- [ ] Add share controls: default enabled per selected account, permanent validity shown and fixed, unlimited access shown and fixed, random/fixed code, and existing-share choice (`询问，默认复用`).
- [ ] Add authoritative confirmation summary tests for selected accounts, transfer count, share count, total actions, per-account counts, mode, directory/duplicate/precheck summary, and Excel output column count. Include the 200k/one-of-ten example.
- [ ] Add token-generation guards so any edit/navigation/reload invalidates stale preview; disable editing while preview is running and ignore stale async completions.
- [ ] Add results-page tests for bounded paging, Chinese stable status codes, retry-share-only command, reconciliation action, and save-as progress/cancellation.
- [ ] Add transfer/share `Paused` projection and explicit resume-command tests; no English enum value may be shown directly.
- [ ] Add existing-share prompt tests: once exact IDs are discovered, show one aggregated count summary, default to `复用已有分享`, allow `重新生成`/`取消` and per-account overrides, submit the original confirmation token, recover the still-pending prompt after restart, and invalidate the prompt if discovery/account/settings change.
- [ ] Run App focused tests; confirm RED.
- [ ] Implement bindings, keyboard access, row/column virtualization, live-region announcements, Chinese empty/error states, and `IDialogService` prompts. Never bind exception text directly.
- [ ] Keep “转存功能尚未验证” / “分享功能尚未验证” gates visible and commands disabled independently until capabilities are accepted.
- [ ] Run App tests and Debug/Release XAML builds with warnings as errors; expect GREEN.
- [ ] Commit: `git commit -m "feat: add transfer share pipeline workflow"`.

## Task 12: Implement and field-accept the official Baidu adapter

**Files:**

- Modify: `src/BaiduBatchTransfer.BaiduAdapter/Http/LiveBaiduNetdiskAdapter.cs`
- Modify: `src/BaiduBatchTransfer.BaiduAdapter/Http/LiveBaiduShareAdapter.cs`
- Create: `src/BaiduBatchTransfer.BaiduAdapter/Http/BaiduAuthenticatedRequestClient.cs`
- Create: `src/BaiduBatchTransfer.BaiduAdapter/Http/BaiduAdapterProtocolModels.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteAdapterCapabilityStore.cs`
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Modify tests: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/AdapterContractTests.cs`
- Create tests: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduShareAdapterTests.cs`
- Create acceptance test: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/LiveBaiduPipelineAcceptanceTests.cs`
- Create restart test: `tests/BaiduBatchTransfer.App.Tests/AdapterCapabilityPersistenceTests.cs`

- [ ] With a disposable user-owned Baidu test account, manually perform one transfer and one permanent/unlimited share in Baidu’s official UI. Record only sanitized endpoint shape, required nonsecret fields, typed status semantics, exact remote ID evidence, request ID behavior, pagination cursor, expiry representation, code representation, and eventual-consistency behavior. Never log cookies, QR codes, response bodies, user IDs, full paths, or share URLs.
- [ ] If exact remote IDs or unambiguous share acceptance cannot be established, stop this task and leave the live capability disabled. Do not substitute filenames/paths or UI success text as evidence.
- [ ] Convert the sanitized observations into offline HTTP-handler fixtures and first write failing adapter tests for request serialization, pagination, error classification, throttling/server wait, exact IDs, permanent validity, unlimited access, create response, and discovery/reconciliation.
- [ ] Run `dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduBatchTransfer.BaiduAdapter.Tests.csproj -c Release`; confirm RED.
- [ ] Implement a narrow authenticated client using the existing encrypted session. Use an allowlist of official HTTPS Baidu hosts, bounded response sizes, cancellation/timeouts, typed errors, and no secret-bearing logging.
- [ ] Keep login/verification handling user-assisted; never bypass QR, CAPTCHA, verification, throttling, or account restrictions.
- [ ] Run offline adapter tests; expect GREEN.
- [ ] Run the opt-in live acceptance once for folder, single file, archive/APK, multi-top-level share, existing compatible share, fixed code, random code, and account pause. Require user confirmation that the new link opens the same top-level resource set.
- [ ] Persist capability acceptance through `IAdapterCapabilityStore` / `SqliteAdapterCapabilityStore` in `AdapterCapabilityAcceptances`, keyed by adapter kind, explicit protocol version, and adapter build version. Store only acceptance time and a nonsecret fixture/evidence hash, never credentials or live URLs.
- [ ] Add a restart test: matching accepted versions enable the corresponding capability; a changed build/protocol version, missing row, failed integrity check, or unaccepted transfer/share half remains disabled with a stable Chinese reason.
- [ ] Commit: `git commit -m "feat: implement validated baidu transfer sharing"`.

## Task 13: Extend account removal, history cleanup, diagnostics, and recovery

**Files:**

- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteAccountRemovalStore.cs`
- Modify: `src/BaiduBatchTransfer.Application/Scheduling/StartupRecoveryService.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/DiagnosticExportService.cs`
- Modify tests: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/AccountRemovalStoreTests.cs`
- Modify tests: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/CrashBoundaryTests.cs`

- [ ] Write failing removal tests proving active `WaitingShare`, leased, `Submitting`, and `ResultPending` work blocks account removal at both preview and prepare.
- [ ] Add terminal-history cleanup tests covering share operations, exact IDs, baselines, results, output snapshots, source payload graph, public/shared batches, and other-account logs. Stale manifest/token and cancellation must produce zero writes.
- [ ] Add diagnostics tests proving share URL/code/request ID/remote IDs/raw values/source title are omitted or structurally redacted. Free-form messages remain omitted.
- [ ] Run focused account-removal/crash tests; confirm RED.
- [ ] Extend the manifest fixpoint and cleanup SQL with indexed share graph edges. Preserve other accounts and public/shared source context.
- [ ] Recover share leases and rebuild share counters before scheduler start.
- [ ] Run focused tests; expect GREEN.
- [ ] Commit: `git commit -m "fix: secure share recovery and account cleanup"`.

## Task 14: Wire the application and prove the complete pipeline

**Files:**

- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Modify: `src/BaiduBatchTransfer.App/AppStartupController.cs`
- Modify tests: `tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs`
- Create integration test: `tests/BaiduBatchTransfer.Application.Tests/Jobs/TransferSharePipelineIntegrationTests.cs`
- Create scale test: `tests/BaiduBatchTransfer.Infrastructure.Tests/Excel/ReplacementPipelineScaleTests.cs`
- Update: `docs/superpowers/specs/2026-08-20-transfer-share-link-replacement-design.md` only if implementation evidence requires a confirmed nonbehavioral clarification.

- [ ] Write a failing clean-composition-root test requiring migration/integrity/recovery before coordinator start and ensuring fake/live adapters are never silently substituted.
- [ ] Write an end-to-end real-SQLite/fake-adapter integration test:
  1. inspect and map an input workbook;
  2. stage raw rows;
  3. select accounts/mode/share flags;
  4. preview and confirm counts;
  5. transfer and persist exact IDs;
  6. create/reuse permanent shares;
  7. reconcile an ambiguous response;
  8. export a new workbook;
  9. reopen and verify every original value plus deterministic result columns.
- [ ] Add crash/restart variants at every transfer/share submission boundary and assert no duplicate transfer/share creation.
- [ ] Add a real 200,000-row + representative multi-account scale test using SQLite keyset pages and a real `.xlsx` writer. Assert exactly 200,000 output rows, page size ≤1,000, source order, no OFFSET query, bounded managed memory (target <128 MiB delta), index use, and no full pivot collection.
- [ ] Run all focused suites. Then run:

```powershell
dotnet restore BaiduBatchTransfer.sln --locked-mode -p:NuGetAudit=false
dotnet build BaiduBatchTransfer.sln -c Release -warnaserror --no-restore
dotnet test BaiduBatchTransfer.sln -c Release --no-build --no-restore
git diff --check
```

- [ ] If the known `PreparedStreamingWritesHandleTwoHundredThousandResourcesAndThreeReplicasWithinBoundedMemory` wall-clock assertion fails only during the full parallel suite, rerun it alone and record both outputs; do not weaken the unrelated threshold in this feature.
- [ ] Run a source/binary secret scan for Cookie, Authorization, BDUSS, STOKEN, raw response-body projection, and accidental full-path/user-ID logging.
- [ ] Perform the user-owned live smoke test: one row first, then a small mixed batch. Verify title preservation, exact resource-set equivalence, selected-account cardinality, permanent link behavior, retry/reconciliation, and “另存一份” output.
- [ ] Commit: `git commit -m "feat: deliver transfer share replacement pipeline"`.

## Final acceptance checklist

- [ ] Ten logged accounts with only one selected produce one transfer and one share per source row, not ten.
- [ ] Distribution totals equal source-row count; replication totals equal source-row count × selected accounts; share-disabled accounts contribute zero shares.
- [ ] Folder, file, APK/archive, and multiple top-level items produce one replacement share that opens the exact transferred top-level set.
- [ ] Source title and all raw source cells are unchanged. Missing titles use `转存结果_解析后标题` only.
- [ ] The source URL is always present in `转存结果_原分享链接`, including transfer/share failures.
- [ ] A failed or pending share never fills a false replacement URL.
- [ ] Existing shares prompt once per applicable decision scope; default reuse prevents duplicate creation.
- [ ] Crashes and unknown responses never trigger blind transfer/share retries.
- [ ] The source workbook is never overwritten; output uses a new validated workbook.
- [ ] All user-facing text is Chinese and no sensitive values appear in UI, logs, diagnostics, exceptions, or `ToString()`.
