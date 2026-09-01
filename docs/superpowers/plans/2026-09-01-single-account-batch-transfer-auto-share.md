# Single-Account Batch Transfer and Auto-Share Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enable one selected logged-in Baidu account to transfer pasted or Excel-imported share links in the background, auto-share the exact transferred object set, and automatically export results.

**Architecture:** Reuse the existing import, durable batch scheduler, exact remote-item evidence, share state machine, and result export pipeline. Add a fixed same-origin HTTP adapter for Baidu web-session reads and writes, fence new direct-background batches from every legacy/acceptance batch, and expose only the simplified single-account workflow. Remote writes remain disabled until the direct contract tests, scheduler isolation tests, and one user-approved live acceptance gate all pass.

**Tech Stack:** .NET 8, WPF, `HttpMessageInvoker`, SQLite, CommunityToolkit.Mvvm, Open XML, xUnit.

---

## Execution constraints

- Work only in `C:\Users\45203\Documents\dakazhandian\.worktrees\transfer-share-link-replacement`.
- The worktree contains extensive pre-existing uncommitted changes. Never use `git reset --hard`, `git checkout --`, broad restore/clean commands, or bulk staging.
- Do not commit production files task-by-task: most target files already contain user-owned uncommitted work, so staging a whole file would mix unrelated changes. Use focused diffs and tests as checkpoints; ask before any final production commit.
- Use `apply_patch` for edits.
- Do not open a real Baidu page, submit a transfer, create a folder, create a share, or run an opt-in live test until Task 9 and renewed user approval.
- Task 0 is a separate read-only gate: it may run only after the user approves read-only contract characterization. It may inspect the already logged-in account/session and fixed GET responses, but must prove that no mutating verb or endpoint is reachable.
- Treat response bodies, cookies, tokens, extraction codes, full share URLs, and local paths as sensitive. Tests use synthetic values only.

## File map

### New focused components

- `src/BaiduBatchTransfer.BaiduAdapter/Http/BaiduSameOriginHttpClient.cs` — allowlisted cookie request transport, bounded JSON reads, typed HTTP/error mapping, and no redirects.
- `src/BaiduBatchTransfer.BaiduAdapter/Http/BaiduWebContextClient.cs` — obtains and validates the current account UID, CSRF token, and fixed web-request context without exposing secrets.
- `src/BaiduBatchTransfer.BaiduAdapter/Http/BaiduBackgroundTransferClient.cs` — inspects source shares, handles extraction codes, lists/creates directories, submits preserve-both transfers, and returns operation-scoped exact item evidence.
- `src/BaiduBatchTransfer.BaiduAdapter/Http/BaiduBackgroundShareClient.cs` — creates permanent shares for an exact remote-item set and returns the observed URL/code/settings.
- `src/BaiduBatchTransfer.Core/Services/SecureExtractionCodeGenerator.cs` — creates exactly four cryptographically random lowercase ASCII letters/digits.
- `src/BaiduBatchTransfer.Infrastructure/Database/Migrations/021_DirectBackgroundBatch.sql` — fences new direct batches, snapshots concurrency and durable export destination, and leaves all existing rows disabled.
- `src/BaiduBatchTransfer.Application/Scheduling/IDirectBackgroundBatchSettingsStore.cs` — reads the active direct batch's concurrency and result destination.
- `src/BaiduBatchTransfer.App/Features/DirectBackground/DirectBackgroundWriteAuthorizationGate.cs` — concrete deny-by-default implementation of the existing operation-bound `IAdapterWriteAuthorizationGate`.
- `src/BaiduBatchTransfer.Infrastructure/Database/SqliteDirectBackgroundAuthorizationStore.cs` — persists only the explicit availability state and one acceptance operation identity; never a broad bearer capability.
- `src/BaiduBatchTransfer.Application/Scheduling/DirectBackgroundAcceptanceBatchService.cs` — creates an inert exact batch, prepares per-attempt authorization, then atomically activates only that batch.

### Existing files to extend, not rewrite

- `src/BaiduBatchTransfer.BaiduAdapter/Http/LiveBaiduNetdiskAdapter.cs` — delegate directory/share-inspection/transfer/confirmation methods to the direct transfer client.
- `src/BaiduBatchTransfer.BaiduAdapter/Http/LiveBaiduShareAdapter.cs` — delegate permanent-share/list methods to the direct share client.
- `src/BaiduBatchTransfer.Application/Scheduling/RemoteWriteExecutionMode.cs` — add a direct-background-only mode distinct from unrestricted `Full`.
- `src/BaiduBatchTransfer.Application/Scheduling/TransferCoordinator.cs` — honor per-batch concurrency 1–3 and share work in the same bounded scheduler.
- `src/BaiduBatchTransfer.Application/Scheduling/CoordinatedBaiduAdapters.cs` — replace the legacy account-wide single-flight rule only for fenced direct operations while preserving the direct authorization gate.
- `src/BaiduBatchTransfer.Application/Scheduling/OperationExecutor.cs` — implement the user-approved one extra submission on unknown direct-transfer results, then fail.
- `src/BaiduBatchTransfer.Application/Scheduling/ShareOperationExecutor.cs` — keep transfer success, fail the row when sharing fails, and allow share-only retry.
- `src/BaiduBatchTransfer.Infrastructure/Database/SqliteTransferSchedulingStore.cs` — reserve only direct-background batches in direct mode and persist exact transferred sets.
- `src/BaiduBatchTransfer.Infrastructure/Database/SqliteShareSchedulingStore.cs` — reserve only transfer-linked direct share operations and preserve exact set cardinality.
- `src/BaiduBatchTransfer.App/Features/Jobs/WizardContracts.cs` — carry the immutable direct-batch settings from UI confirmation into batch creation.
- `src/BaiduBatchTransfer.App/Services/DirectBackgroundAccountLoginLauncher.cs` — routes add/relogin for direct mode through cookie capture and encrypted `AccountSession` persistence while leaving managed-browser workflows isolated.
- `src/BaiduBatchTransfer.Infrastructure/Database/SqliteLocalResultDestinationStore.cs` and `SqliteLocalResultExportOwnerRepository.cs` — retain `BatchResultDestinations` as the single durable export source of truth.
- `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs` — enforce one account, auto-share, default concurrency 2, one confirmation, durable result destination, and simplified status.
- `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceView.xaml` — remove mode/auto-share toggles and expose the compact toolbar.
- `src/BaiduBatchTransfer.App/Features/Jobs/LocalJobResultsWorkflow.cs` and `JobResultsView.xaml` — show one result row per source link and multi-name cells.
- `src/BaiduBatchTransfer.App/AppBootstrap.cs` — register direct clients, direct mode, fenced coordinator, and available capability only after all fixed contracts are present.

## Task 0: Characterize the read-only session and web contract

**Files:**
- Inspect: `src/BaiduBatchTransfer.App/Features/Accounts/BaiduLoginWindow.xaml.cs`
- Inspect: `src/BaiduBatchTransfer.App/Services/ManagedBrowserAccountLoginLauncher.cs`
- Inspect: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Inspect: `src/BaiduBatchTransfer.Application/Accounts/AddAccountService.cs`
- Inspect: `src/BaiduBatchTransfer.BaiduAdapter/Authentication/WebViewCookieSessionBridge.cs`
- Inspect: `src/BaiduBatchTransfer.Infrastructure/Security/EncryptedBaiduSessionStore.cs`
- Create: `src/BaiduBatchTransfer.App/Services/DirectBackgroundAccountLoginLauncher.cs`
- Create: `docs/testing/fixtures/direct-background/read-contract.md`
- Create: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/Fixtures/DirectBackground/*.sanitized.json`
- Test: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/DirectBackgroundContractFixtureTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Services/DirectBackgroundAccountLoginLauncherTests.cs`

- [ ] **Step 1: Prove the usable session source locally**

First prove the current production fact: `AppBootstrap` routes add/relogin through `ManagedBrowserAccountLoginLauncher`, so the legacy `BaiduLoginWindow` path alone is not a usable direct-mode session source. Add a regression test for a new `DirectBackgroundAccountLoginLauncher` that explicitly routes direct-mode add and relogin through `BaiduLoginWindow`/`WebViewCookieSessionBridge`, binds the real Baidu UID through `AddAccountService`, and makes the DPAPI-protected `AccountSession` loadable from `EncryptedBaiduSessionStore`. Register this launcher for the left-account direct workflow; keep `ManagedBrowserAccountLoginLauncher` scoped only to legacy managed-browser workflows. A direct batch cannot start until the selected account has this encrypted session.

- [ ] **Step 2: Add a zero-write characterization harness**

The harness must hard-reject every verb except `GET`/`HEAD`, exact-allowlist the read-only origin/path set, disable redirects, count all requests, and fail if a body is supplied. Its output is a sanitized schema fixture only: field names, JSON types, bounded example placeholders, status/error categories, and request-header names. It must redact cookie values, CSRF values, UIDs, URLs, filenames, and paths.

- [ ] **Step 3: Stop for explicit read-only approval**

Ask the user to approve opening the already logged-in account for read-only characterization. Do not probe mutating transfer/share/folder endpoints. Record the exact read endpoint and CSRF source from non-mutating requests. Derive candidate mutating endpoint paths, `Origin`/`Referer` rules, request fields, polling fields, and response/error envelopes only by statically reading the already-delivered page/JavaScript assets; never call those endpoints during characterization. If the fixed write-contract shape cannot be established from those read-only artifacts, keep production direct writes disabled and stop rather than guessing.

- [ ] **Step 4: Freeze and test the sanitized contract**

Commit no secrets. `DirectBackgroundContractFixtureTests` must prove every subsequent parser/serializer uses only the observed schema and that the characterization run made zero POST/PUT/PATCH/DELETE requests. Task 2 cannot begin until this checkpoint passes.

## Task 1: Fence new direct-background batches and persist settings

**Files:**
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/Migrations/021_DirectBackgroundBatch.sql`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/DatabaseMigrator.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/DatabaseSchemaValidator.cs`
- Modify: `src/BaiduBatchTransfer.Core/Abstractions/PersistenceModels.cs`
- Modify: `src/BaiduBatchTransfer.Application/Jobs/BatchConfirmationSummary.cs`
- Modify: `src/BaiduBatchTransfer.Application/Jobs/BatchDraftValidator.cs`
- Modify: `src/BaiduBatchTransfer.Application/Jobs/CreateBatchService.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Jobs/WizardContracts.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/Repositories/SqliteJobRepository.cs`
- Test: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/MigrationTests.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Jobs/CreateBatchServiceTests.cs`

- [ ] **Step 1: Write migration RED tests**

Add tests proving migration 21 creates immutable `DirectBackground`, `MaxConcurrency`, an inert/active direct lifecycle, durable batch-pause state, durable unknown-resubmission state, per-share fixed-code intent, response ordinals, and one singleton direct-workspace preference; old rows receive `DirectBackground=0`; concurrency must be 1–3. A direct batch must contain exactly one account allocation and a complete transfer/share bijection: transfer count equals share count, and each transfer has exactly one linked share with no standalone or orphan share.

```csharp
Assert.Equal(0L, ReadInt64(oldBatch, "DirectBackground"));
Assert.ThrowsAsync<SqliteException>(() => InsertDirectBatch(maxConcurrency: 4));
```

- [ ] **Step 2: Run migration RED**

Run:

```powershell
dotnet test tests\BaiduBatchTransfer.Infrastructure.Tests\BaiduBatchTransfer.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~MigrationTests"
```

Expected: FAIL because migration 21 and the new columns do not exist.

- [ ] **Step 3: Add the minimal migration and validation**

Use defaults that keep every existing batch inert:

```sql
ALTER TABLE BatchJobs ADD COLUMN DirectBackground INTEGER NOT NULL DEFAULT 0 CHECK(DirectBackground IN (0,1));
ALTER TABLE BatchJobs ADD COLUMN MaxConcurrency INTEGER NOT NULL DEFAULT 1 CHECK(MaxConcurrency BETWEEN 1 AND 3);
ALTER TABLE BatchJobs ADD COLUMN DirectActivationState TEXT NOT NULL DEFAULT 'Inert' CHECK(DirectActivationState IN ('Inert','Active'));
ALTER TABLE BatchJobs ADD COLUMN DirectPauseReason TEXT NULL;
ALTER TABLE TransferOperations ADD COLUMN UnknownResubmissionCount INTEGER NOT NULL DEFAULT 0 CHECK(UnknownResubmissionCount BETWEEN 0 AND 1);
ALTER TABLE ShareOperations ADD COLUMN DirectExtractionCode TEXT NULL;
ALTER TABLE ShareOperationRemoteItems ADD COLUMN ResponseOrdinal INTEGER NULL;
CREATE TABLE DirectBackgroundWorkspacePreferences (
    SingletonId INTEGER PRIMARY KEY CHECK(SingletonId=1),
    DefaultResultDirectory TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);
CREATE TABLE DirectBackgroundAuthorizationState (
    SingletonId INTEGER PRIMARY KEY CHECK(SingletonId=1),
    NormalEnabled INTEGER NOT NULL DEFAULT 0 CHECK(NormalEnabled IN (0,1)),
    UpdatedAt TEXT NOT NULL
);
CREATE TABLE DirectBackgroundAcceptanceAuthorizations (
    OperationId TEXT NOT NULL,
    BatchId TEXT NOT NULL,
    AccountId TEXT NOT NULL,
    Generation INTEGER NOT NULL,
    AllowedAction TEXT NOT NULL,
    MutationOrdinal INTEGER NOT NULL DEFAULT 0 CHECK(MutationOrdinal >= 0),
    AttemptOrdinal INTEGER NOT NULL CHECK(AttemptOrdinal BETWEEN 1 AND 3),
    ExpiresAt TEXT NOT NULL,
    ConsumedAt TEXT NULL,
    PRIMARY KEY(OperationId, Generation, AllowedAction, MutationOrdinal, AttemptOrdinal)
);
```

Keep export destinations in existing `BatchResultDestinations`; do not create a competing `BatchJobs.ResultDirectory` field. Seed direct authorization as `NormalEnabled=0`; old rows never gain a write authorization. New direct batches are inserted as `Inert`, and every reservation query requires `DirectActivationState='Active'`. Add immutability/shape triggers and a repository pre-commit invariant query for the complete transfer/share bijection. Reservation queries must independently reject a corrupt/incomplete/inert direct batch. Do not update existing rows to `1` or synthesize direct codes for old rows.

- [ ] **Step 4: Write batch creation RED tests**

Cover one selected account, mandatory auto-share, normalized target path, `DuplicatePolicy.Rename`, concurrency default 2, fully qualified result directory in `BatchResultDestinations`, one cryptographically generated code persisted per share operation before commit, `ExistingSharePolicy.Regenerate`, and rejection of multi-account, incomplete-bijection, or standalone-share direct batches.

- [ ] **Step 5: Thread fields through confirmed creation**

Extend `WizardContracts`, the confirmation snapshot, validator, repository insert, and persistence record. The direct flag, settings, export destination, every transfer/share link, and each per-share code must be snapshotted in the same transaction. Before commit, query the newly created rows and require exact transfer/share count equality and one-to-one linkage.

- [ ] **Step 6: Run focused GREEN**

```powershell
dotnet test tests\BaiduBatchTransfer.Application.Tests\BaiduBatchTransfer.Application.Tests.csproj --no-restore --filter "FullyQualifiedName~CreateBatchServiceTests|FullyQualifiedName~BatchDraftValidatorTests"
dotnet test tests\BaiduBatchTransfer.Infrastructure.Tests\BaiduBatchTransfer.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~MigrationTests|FullyQualifiedName~RepositoryTests"
```

Expected: PASS; existing non-direct creation tests remain unchanged.

- [ ] **Step 7: Checkpoint**

```powershell
git diff --check -- src/BaiduBatchTransfer.Infrastructure/Database src/BaiduBatchTransfer.Application/Jobs src/BaiduBatchTransfer.Core/Abstractions tests/BaiduBatchTransfer.Infrastructure.Tests/Database tests/BaiduBatchTransfer.Application.Tests/Jobs
```

Do not commit the dirty production files.

## Task 2: Build the fixed same-origin HTTP boundary

**Files:**
- Create: `src/BaiduBatchTransfer.BaiduAdapter/Http/BaiduSameOriginHttpClient.cs`
- Create: `src/BaiduBatchTransfer.BaiduAdapter/Http/BaiduWebContextClient.cs`
- Modify: `src/BaiduBatchTransfer.Application/Accounts/AddAccountService.cs` only if Task 0 proves normal login does not already persist the bound session
- Test: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduSameOriginHttpClientTests.cs`
- Test: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduWebContextClientTests.cs`

- [ ] **Step 1: Write transport RED tests**

Require HTTPS, default port, the exact host/path/header/verb matrix frozen in Task 0, no redirects, cookie-domain/path filtering, JSON accept headers, response limit 64 KiB, caller cancellation, an internal 30-second request deadline, and redacted errors. A request outside the frozen matrix must fail before transport. With a never-completing synthetic handler and fake/short test deadline, prove the client returns typed timeout failure even when the caller token is never canceled.

```csharp
await Assert.ThrowsAsync<ArgumentException>(() => client.SendJsonAsync(
    session, HttpMethod.Get, new Uri("https://example.com/api"), null, token));
Assert.DoesNotContain("BDUSS", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
```

- [ ] **Step 2: Run transport RED**

```powershell
dotnet test tests\BaiduBatchTransfer.BaiduAdapter.Tests\BaiduBatchTransfer.BaiduAdapter.Tests.csproj --no-restore --filter "FullyQualifiedName~BaiduSameOriginHttpClientTests|FullyQualifiedName~BaiduWebContextClientTests"
```

Expected: FAIL because both clients are absent.

- [ ] **Step 3: Implement bounded transport**

Use `HttpMessageInvoker` with `AllowAutoRedirect=false`, `UseCookies=false`, gzip/deflate decompression, and a fixed request builder. Link the caller token to a per-request 30-second deadline supplied through an injectable timeout policy/time provider for deterministic tests; map internal deadline expiry separately from caller cancellation. Never accept arbitrary caller hosts, endpoints, verbs, `Origin`/`Referer`, or serialize raw response text into diagnostics. Tests replay only Task 0 fixtures.

- [ ] **Step 4: Implement web-context read**

Load the normal left-account `AccountSession` from `EncryptedBaiduSessionStore`, then read the exact fixed same-origin context source observed in Task 0 and return an internal value object:

```csharp
internal sealed record BaiduWebContext(string UserId, string CsrfToken, string RequestContextFingerprint);
```

Validate the UID against `AccountSession.BaiduUserId`; reject an unbound/Managed-Chromium-only session; do not expose the CSRF token through `ToString()` or logs. Pin exact CSRF location, Origin/Referer values, request fields, polling fields, and error envelopes with fixture tests; no placeholder endpoint or guessed field is allowed.

- [ ] **Step 5: Run GREEN and regression**

```powershell
dotnet test tests\BaiduBatchTransfer.BaiduAdapter.Tests\BaiduBatchTransfer.BaiduAdapter.Tests.csproj --no-restore --filter "FullyQualifiedName~BaiduSameOriginHttpClientTests|FullyQualifiedName~BaiduWebContextClientTests|FullyQualifiedName~BaiduSessionIdentityClientTests"
```

Expected: PASS with synthetic handlers only and zero network access.

## Task 3: Implement source inspection, destination creation, and exact transfer

**Files:**
- Create: `src/BaiduBatchTransfer.BaiduAdapter/Http/BaiduBackgroundTransferClient.cs`
- Modify: `src/BaiduBatchTransfer.BaiduAdapter/Http/LiveBaiduNetdiskAdapter.cs`
- Modify: `src/BaiduBatchTransfer.Core/Domain/ShareMetadata.cs`
- Modify: `src/BaiduBatchTransfer.Core/Domain/TransferReceipt.cs`
- Modify: `src/BaiduBatchTransfer.Core/Domain/RemoteItemIdentitySet.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteTransferSchedulingStore.cs`
- Test: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduBackgroundTransferClientTests.cs`
- Test: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/AdapterContractTests.cs`

- [ ] **Step 1: Write source-inspection RED tests**

Cover no-code shares, correct/incorrect four-character codes, expired links, verification responses, one and multiple top-level objects, response bounds, and UID drift. Persist only safe metadata plus exact source cardinality/fingerprint.

- [ ] **Step 2: Write directory RED tests**

Cover root `/`, Unicode paths, repeated separators, existing directories, one-by-one creation, already-created races, invalid paths, and preserve session binding.

- [ ] **Step 3: Write transfer RED tests**

Use fixed request/response fixtures to require:

- preserve-both (`DuplicatePolicy.Rename`) only;
- one operation correlation ID per submit;
- exact returned destination item IDs, displayed names, paths, and authoritative operation-response ordinal;
- returned count equal to source top-level count;
- no directory-difference inference;
- typed `ExpiredLogin`, `VerificationRequired`, `TransientService`, `UnknownResponse`, and `SubmissionAcceptance` mapping.

```csharp
Assert.Equal(source.ItemCount, receipt.ExactRemoteItems!.Count);
Assert.All(receipt.ExactRemoteItems.Items, item => Assert.False(string.IsNullOrWhiteSpace(item.ExactId)));
```

- [ ] **Step 4: Run RED**

```powershell
dotnet test tests\BaiduBatchTransfer.BaiduAdapter.Tests\BaiduBatchTransfer.BaiduAdapter.Tests.csproj --no-restore --filter "FullyQualifiedName~BaiduBackgroundTransferClientTests|FullyQualifiedName~AdapterContractTests"
```

Expected: FAIL because live write methods still return `NotValidated`.

- [ ] **Step 5: Implement minimal fixed clients**

Keep endpoint URIs private static readonly values. Serialize only the characterized fixed form keys. Build `RemoteItemIdentitySet` solely from the operation-scoped response or operation-result query. Do not use its canonical-ID ordering for display: persist a separate unique zero-based `ResponseOrdinal` per operation and verify ordinals are contiguous and cardinality matches the source.

- [ ] **Step 6: Wire `LiveBaiduNetdiskAdapter`**

Constructor-inject the background client and implement `IOperationBoundBaiduNetdiskAdapter`: all directory creation and transfer submissions must accept `TransferOperationAutomationContext` carrying exact batch/operation/account/generation identity. The legacy unbound mutating methods fail closed; only identity/capacity/read methods may use the unbound interface. Delegate bounded operations to the direct transfer client and preserve capacity/identity delegation.

- [ ] **Step 7: Run GREEN**

Run the Task 3 filter and the full BaiduAdapter test project. Expected: PASS with no live requests.

## Task 4: Implement secure four-character codes and exact permanent sharing

**Files:**
- Create: `src/BaiduBatchTransfer.Core/Services/SecureExtractionCodeGenerator.cs`
- Create: `src/BaiduBatchTransfer.BaiduAdapter/Http/BaiduBackgroundShareClient.cs`
- Modify: `src/BaiduBatchTransfer.BaiduAdapter/Http/LiveBaiduShareAdapter.cs`
- Modify: `src/BaiduBatchTransfer.Application/Scheduling/ShareOperationExecutor.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteShareSchedulingStore.cs`
- Test: `tests/BaiduBatchTransfer.Core.Tests/Services/SecureExtractionCodeGeneratorTests.cs`
- Test: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduBackgroundShareClientTests.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/ShareOperationExecutorTests.cs`

- [ ] **Step 1: Write generator RED tests**

Inject `RandomNumberGenerator` behind a tiny byte source seam. Assert exactly four characters, alphabet `[a-z0-9]`, rejection-free mapping, and no deterministic constant.

```csharp
Assert.Matches("^[a-z0-9]{4}$", generator.Create());
```

- [ ] **Step 2: Implement the minimal generator**

Use `RandomNumberGenerator.GetInt32(alphabet.Length)` for each position. Never log the generated code.

- [ ] **Step 3: Write direct-share RED tests**

Require the request to contain every exact transferred item and no others, ordered by persisted response ordinal; permanent/unlimited settings; the already-persisted fixed code; returned URL/code/settings; multiple-object cardinality; response bounds; UID drift; typed failures; and no name search.

- [ ] **Step 4: Run RED**

```powershell
dotnet test tests\BaiduBatchTransfer.Core.Tests\BaiduBatchTransfer.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~SecureExtractionCodeGeneratorTests"
dotnet test tests\BaiduBatchTransfer.BaiduAdapter.Tests\BaiduBatchTransfer.BaiduAdapter.Tests.csproj --no-restore --filter "FullyQualifiedName~BaiduBackgroundShareClientTests"
```

- [ ] **Step 5: Implement and wire sharing**

`LiveBaiduShareAdapter` must use the fixed background client and implement `IOperationBoundBaiduShareAdapter`; unbound mutating methods fail closed. Generate and persist the per-operation code transactionally during Task 1 creation, never at execution time. Every first submission, crash recovery, reconciliation, and share-only retry must load and reuse that exact code, then verify the returned code exactly matches. Direct batches always bypass discovery/reuse and force `ExistingSharePolicy.Regenerate`; `Ask`, `Reuse`, and decision-paused states are invalid and unreservable for direct rows.

- [ ] **Step 6: Run GREEN and share regressions**

```powershell
dotnet test tests\BaiduBatchTransfer.Application.Tests\BaiduBatchTransfer.Application.Tests.csproj --no-restore --filter "FullyQualifiedName~ShareOperationExecutorTests|FullyQualifiedName~ShareSubmissionEngineTests"
dotnet test tests\BaiduBatchTransfer.BaiduAdapter.Tests\BaiduBatchTransfer.BaiduAdapter.Tests.csproj --no-restore
```

## Task 5: Add direct-only scheduler mode, concurrency 1–3, and simplified retry

**Files:**
- Create: `src/BaiduBatchTransfer.Application/Scheduling/IDirectBackgroundBatchSettingsStore.cs`
- Modify: `src/BaiduBatchTransfer.Application/Scheduling/RemoteWriteExecutionMode.cs`
- Modify: `src/BaiduBatchTransfer.Application/Scheduling/TransferCoordinator.cs`
- Modify: `src/BaiduBatchTransfer.Application/Scheduling/OperationExecutor.cs`
- Modify: `src/BaiduBatchTransfer.Application/Scheduling/ShareOperationExecutor.cs`
- Modify: `src/BaiduBatchTransfer.Application/Scheduling/CoordinatedBaiduAdapters.cs`
- Modify: `src/BaiduBatchTransfer.Application/Scheduling/OperationBoundBaiduNetdiskAdapter.cs`
- Modify: `src/BaiduBatchTransfer.Application/Scheduling/OperationBoundBaiduShareAdapter.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteTransferSchedulingStore.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteShareSchedulingStore.cs`
- Modify: `src/BaiduBatchTransfer.App/Services/TransferCoordinatorHostedService.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/TransferCoordinatorTests.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/OperationExecutorTests.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/ShareOperationExecutorTests.cs`
- Test: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/SqliteSchedulingStoreTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Services/TransferCoordinatorHostedServiceTests.cs`

- [ ] **Step 1: Write scheduler-fence RED tests**

Prove direct mode can reserve only `DirectBackground=1`; every pre-existing, acceptance-only, managed, legacy, standalone-share, or non-direct batch remains unclaimed. Prove startup with old queued rows performs zero writes.

- [ ] **Step 2: Write concurrency RED tests**

Prove batch values 1, 2, and 3 cap a single account's simultaneous direct operations; default is 2; no fourth operation is reserved. A typed throttling response or two consecutive typed temporary-network failures for the same account lower the effective limit to 1. Cover all three existing single-flight layers: coordinator worker count, transfer/share SQL busy-account predicates, and `BaiduAdapterConcurrencyGate`. Legacy/acceptance operations must retain account-wide single-flight behavior.

- [ ] **Step 3: Write simplified unknown-result RED tests**

For direct transfer only, prove one unknown submit durably records `UnknownResubmissionCount=1` before the extra submit; a second unknown marks failure and continues. Every outbound mutating call carries a durable `MutationOrdinal` (zero for transfer/share submission; a directory-segment ordinal for folder creation) and a positive durable `AttemptOrdinal` in its operation context; the gate consumes the matching authorization exactly once. Restart between those two submissions and prove only one remaining submit/authorization is possible. Prove a confirmed transfer followed by share failure preserves exact item evidence and share retry never invokes transfer again. Prove `ExpiredLogin` or `VerificationRequired` atomically pauses the whole batch before another reservation is granted.

- [ ] **Step 4: Run RED**

```powershell
dotnet test tests\BaiduBatchTransfer.Application.Tests\BaiduBatchTransfer.Application.Tests.csproj --no-restore --filter "FullyQualifiedName~TransferCoordinatorTests|FullyQualifiedName~OperationExecutorTests|FullyQualifiedName~ShareOperationExecutorTests"
dotnet test tests\BaiduBatchTransfer.Infrastructure.Tests\BaiduBatchTransfer.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~SqliteSchedulingStoreTests"
```

- [ ] **Step 5: Implement direct mode and settings store**

Add `DirectBackground` as a mode that starts only the fenced coordinator. Raise the absolute scheduler ceiling to 3, but take each active direct batch's requested limit from durable storage. For direct rows only, replace the transfer/share busy-account predicates and `BaiduAdapterConcurrencyGate` account key with a batch-scoped limiter that permits the same selected account up to that limit; do not relax legacy paths.

- [ ] **Step 6: Implement adaptive throttling**

Keep a process-local per-account limiter that starts at the batch limit and drops to 1 on typed throttling or after two consecutive typed temporary-network failures. Use a direct-specific retry policy with exactly two backoffs, so the initial call plus retries is at most three attempts for any remote stage; do not use `RetryPolicy.Default`, whose three backoffs allow four total attempts. Pin exact call counts in tests. The transfer-unknown rule shares this stage budget and is stricter: at most two submissions total, never two unknown submissions plus further transient retries. On verification/login errors, transactionally set a batch pause reason and make every reservation query re-check it; in-flight work may finish safely but no new operation may begin.

- [ ] **Step 7: Implement user-approved retry semantics**

Do not surface pending terminology for direct batches. Persist the mutation ordinal, stage attempt ordinal, and direct unknown-resubmission counter before each remote attempt so crash recovery cannot reset any budget; pass both ordinals through `OperationBoundBaiduNetdiskAdapter`/`OperationBoundBaiduShareAdapter` into `AdapterWriteAuthorizationRequest`. Perform exactly one extra transfer submission after unknown acceptance, then fail. Share-only retry starts from persisted exact transfer evidence and persisted extraction-code intent.

- [ ] **Step 8: Run GREEN and old-mode regressions**

Run Task 5 filters plus existing acceptance-only and coordinator-hosted-service tests. Expected: direct batches run; all old rows remain inert.

## Task 6: Simplify batch creation and transfer UI

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceOptions.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceView.xaml`
- Modify: `src/BaiduBatchTransfer.App/Features/Jobs/CreateJobWizardViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Jobs/AccountAllocationRowViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Jobs/WizardContracts.cs`
- Modify: `src/BaiduBatchTransfer.App/Services/DirectBackgroundAccountLoginLauncher.cs`
- Create: `src/BaiduBatchTransfer.App/Features/DirectBackground/DirectBackgroundWriteAuthorizationGate.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteDirectBackgroundAuthorizationStore.cs`
- Create: `src/BaiduBatchTransfer.Application/Scheduling/DirectBackgroundAcceptanceBatchService.cs`
- Modify: `src/BaiduBatchTransfer.Application/Scheduling/CoordinatedBaiduAdapters.cs`
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewModelTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/CreateJobWizardViewModelTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Services/DirectBackgroundAccountLoginLauncherTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/DirectBackgroundWriteAuthorizationGateTests.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/DirectBackgroundAcceptanceBatchServiceTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs`

- [ ] **Step 1: Write UI/view-model RED tests**

Assert:

- exactly one selected left account is required;
- target defaults to `/` and missing directories are allowed;
- auto-share is always true and not user-editable;
- `DuplicatePolicy.Rename` is fixed;
- concurrency defaults to 2 and validates 1–3;
- result directory defaults to `Documents\百度批量转存结果` and persists;
- confirmation shows count/account/target/auto-share/result directory;
- multi-account and auto-share toggle controls are absent from XAML;
- start is blocked for invalid input, unavailable normal-account session, or disabled direct-write authorization;
- direct batches always use `ExistingSharePolicy.Regenerate` and cannot enter ask/reuse decision UI.

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests\BaiduBatchTransfer.App.Tests\BaiduBatchTransfer.App.Tests.csproj --no-restore --filter "FullyQualifiedName~TransferWorkspaceViewModelTests|FullyQualifiedName~CreateJobWizardViewModelTests|FullyQualifiedName~AppBootstrapTests"
```

- [ ] **Step 3: Implement minimal view-model changes**

Remove mode-selection commands from the visible flow, derive the single account from the left-side selection, create fixed share settings with `ExistingSharePolicy.Regenerate`, and snapshot direct settings when the user confirms. Persist the selected result directory through `SqliteLocalResultDestinationStore`/`BatchResultDestinations` and load it as the default after restart.

- [ ] **Step 4: Implement compact XAML**

Toolbar order: paste, Excel, target directory, concurrency 1–3, start. Keep advanced history/pause/retry controls but remove the user-facing account-mode and share-enable controls.

- [ ] **Step 5: Register production direct services without enabling old writes**

In `AppBootstrap`, register `DirectBackgroundAccountLoginLauncher` for the left-account direct workflow, direct clients, and `RemoteWriteExecutionMode.DirectBackground`. Register `DirectBackgroundWriteAuthorizationGate` as the concrete existing `IAdapterWriteAuthorizationGate`; it reads `SqliteDirectBackgroundAuthorizationStore`, starts deny-all, and validates the exact capability, batch ID, operation ID, account ID, generation, action, durable mutation/attempt ordinals, recovery flag, expiry, and one-use lease. Extend `AdapterWriteAuthorizationRequest`/operation-bound contexts with both ordinals. `LiveBaiduNetdiskAdapter`/`LiveBaiduShareAdapter` are wrapped through their operation-bound interfaces so directory creation, transfer, and share all carry that identity into `CoordinatedBaiduAdapters`; unbound mutating calls fail closed. Before Task 9 approval, ordinary `IAdapterAvailability.CanStartTransfers` remains false and the hosted coordinator does not claim work. Do not register unrestricted `Full` and do not start standalone share coordinator.

`DirectBackgroundAcceptanceBatchService` is the only pre-normal-enablement path: after renewed approval it (1) creates the exact batch and operations as `Inert`, (2) performs only read-only preflight needed to determine directory mutations, (3) stores one expiring authorization row for each exact planned mutation/attempt pair—each missing directory segment has its own mutation ordinal and up to three transient attempt ordinals, transfer mutation ordinal 0 has ordinals 1–2 for unknown-result semantics, and share mutation ordinal 0 has attempt ordinals up to the direct three-attempt ceiling—and then (4) atomically changes only that batch to `Active` and starts its bounded runner. Failure before step 4 leaves the batch inert; failure after activation cannot mint additional ordinals. Successful acceptance may persist a separate explicit `NormalEnabled` availability flag only after another user approval; normal enabled batches still require an exact operation context and durable mutation/attempt ordinals, but do not use acceptance rows.

Add gate tests proving a missing, mismatched, expired, reused, skipped/replayed mutation or attempt ordinal, or unbound identity performs zero inner-adapter calls; every mutating action/mutation/attempt needs its own valid binding; a retry consumes the next attempt ordinal and cannot reuse the previous binding; restart preserves deny-all/approved state correctly; and read-only identity/capacity/inspection does not consume a write lease. Add service tests proving no operation ID is authorized before inert creation, inert batches are never reserved, only the approved batch activates, and activation failure leaves zero runnable work.

- [ ] **Step 6: Run GREEN and XAML load tests**

Run Task 6 filter in Debug and Release. Expected: PASS; WPF resource loading succeeds.

## Task 7: Project multi-object results and durable automatic export

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Features/Jobs/JobResultsContracts.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Jobs/LocalJobResultsWorkflow.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Jobs/JobResultsView.xaml`
- Modify: `src/BaiduBatchTransfer.Application/Jobs/LocalResultExportCoordinator.cs`
- Modify: `src/BaiduBatchTransfer.Application/Jobs/ReplacementWorkbookExportService.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteLocalResultExportStore.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteLocalResultDestinationStore.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteLocalResultExportOwnerRepository.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Jobs/JobResultsViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Common/WorkspaceRunPanelViewModel.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/JobResultsViewModelTests.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Jobs/LocalResultExportCoordinatorTests.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Jobs/ReplacementWorkbookExportTests.cs`
- Test: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/ShareSchedulingStoreTests.cs`

- [ ] **Step 1: Write result-projection RED tests**

One original link must produce one row. Multiple actual saved names appear one per line in the UI/export cell in operation result order. Overall success requires transfer and share success; share failure displays `失败：已转存，分享失败`.

- [ ] **Step 2: Write export RED tests**

Assert automatic timestamped workbook creation, no overwrite, new URL/code mapping, durable destination/default after restart, and export-only retry after local failure. The retry test uses strict remote-adapter spies and must prove zero transfer/share/list/create calls.

- [ ] **Step 3: Run RED**

```powershell
dotnet test tests\BaiduBatchTransfer.Application.Tests\BaiduBatchTransfer.Application.Tests.csproj --no-restore --filter "FullyQualifiedName~ReplacementWorkbookExportTests|FullyQualifiedName~LocalResultExportCoordinatorTests"
dotnet test tests\BaiduBatchTransfer.App.Tests\BaiduBatchTransfer.App.Tests.csproj --no-restore --filter "FullyQualifiedName~JobResultsViewModelTests"
```

- [ ] **Step 4: Implement projection and export**

Read/write the destination only through the existing `BatchResultDestinations` path (`SqliteLocalResultDestinationStore` and `SqliteLocalResultExportOwnerRepository`) and persist the latest user-selected directory as the next-batch default. Use the separately persisted response ordinal—not `RemoteItemIdentitySet` canonical sort—for `Environment.NewLine` multi-name cells. Do not expose full URLs in logs; URLs and codes belong only in the secured result view/export. If export fails, preserve remote success and expose `处理完成，结果文件保存失败`; wire an export-only retry command/state through `JobResultsViewModel`/`WorkspaceRunPanelViewModel` that cannot enqueue or call any remote executor.

- [ ] **Step 5: Run GREEN**

Run Task 7 filters and existing result/export regressions. Open the generated synthetic workbook in tests and assert exact cell values.

## Task 8: End-to-end offline integration and restart recovery

**Files:**
- Test: `tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/DirectBackgroundBatchFlowTests.cs`
- Test: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/DirectBackgroundRecoveryTests.cs`
- Modify production files only if integration RED exposes missing wiring.

- [ ] **Step 1: Write an offline end-to-end RED test**

Create two imported links, one containing one top-level object and one containing multiple objects. Use a synthetic session and fixed HTTP handler. Assert directory creation, concurrency 2, preserve-both transfer, exact-set share, four-character code, one row per link, and automatic workbook export.

- [ ] **Step 2: Write restart RED tests**

Stop after confirmed transfer but before share, rebuild the host against the same temporary SQLite database, and assert only share runs with the same persisted extraction code. Stop after the first unknown transfer outcome but before resubmission and assert only one remaining submission is possible. Stop after remote success but before export and assert only export runs. For each restart case assert operation-response ordering and exact cardinality survive.

- [ ] **Step 3: Run RED, fix only missing integration wiring, then GREEN**

```powershell
dotnet test tests\BaiduBatchTransfer.Application.Tests\BaiduBatchTransfer.Application.Tests.csproj --no-restore --filter "FullyQualifiedName~DirectBackgroundBatchFlowTests"
dotnet test tests\BaiduBatchTransfer.Infrastructure.Tests\BaiduBatchTransfer.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~DirectBackgroundRecoveryTests"
dotnet test tests\BaiduBatchTransfer.App.Tests\BaiduBatchTransfer.App.Tests.csproj --no-restore --filter "FullyQualifiedName~AppBootstrapTests"
```

Expected: PASS with no browser, network, cookie store, or real filesystem outside test temp roots.

- [ ] **Step 4: Prove coexistence and batch pause recovery**

Seed legacy queued/active rows beside a direct batch and prove they neither execute nor block the direct batch. Trigger login expiry and verification on one direct operation, restart the host, and prove the entire batch remains paused with zero new reservations until the normal left-account session is refreshed and the user explicitly continues.

## Task 9: Full verification, UI inspection, and live acceptance gate

**Files:**
- Create: `docs/testing/2026-09-01-direct-batch-transfer-auto-share-handoff.md`
- Modify production only for defects proved by verification.

- [ ] **Step 1: Run focused suites**

```powershell
dotnet test tests\BaiduBatchTransfer.Core.Tests\BaiduBatchTransfer.Core.Tests.csproj --no-restore
dotnet test tests\BaiduBatchTransfer.BaiduAdapter.Tests\BaiduBatchTransfer.BaiduAdapter.Tests.csproj --no-restore
dotnet test tests\BaiduBatchTransfer.Application.Tests\BaiduBatchTransfer.Application.Tests.csproj --no-restore
dotnet test tests\BaiduBatchTransfer.Infrastructure.Tests\BaiduBatchTransfer.Infrastructure.Tests.csproj --no-restore
dotnet test tests\BaiduBatchTransfer.App.Tests\BaiduBatchTransfer.App.Tests.csproj --no-restore --filter "FullyQualifiedName~TransferWorkspaceViewModelTests|FullyQualifiedName~CreateJobWizardViewModelTests|FullyQualifiedName~JobResultsViewModelTests|FullyQualifiedName~AppBootstrapTests|FullyQualifiedName~TransferCoordinatorHostedServiceTests"
```

Expected: all pass; no opt-in real browser/process/network tests execute.

- [ ] **Step 2: Build Debug and Release**

```powershell
dotnet build BaiduBatchTransfer.sln -c Debug --no-restore -warnaserror
dotnet build BaiduBatchTransfer.sln -c Release --no-restore -warnaserror
```

Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Run diff and secret checks**

```powershell
git diff --check -- src tests docs
rg -n "BDUSS=|STOKEN=|access_token=|client_secret=" src tests docs
```

Expected: diff check exits 0 apart from informational line-ending warnings; no real credentials or tokens are present.

- [ ] **Step 4: Launch the app for read-only UI inspection**

Verify the compact batch-transfer toolbar, selected-account behavior, default concurrency 2, default `/` target, hidden mode/share/existing-share-decision controls, result columns, pause/continue/retry/export-only-retry controls, and no unexpected browser window. Confirm Start is still disabled by the direct authorization gate. Do not press Start with real data.

- [ ] **Step 5: Write the handoff**

Record exact test/build evidence, remaining private-web-contract risk, live test prerequisites, and explicit statement that no real cloud write has run.

- [ ] **Step 6: Request renewed approval for one live write**

Only after Steps 1–5 pass, ask the user to approve one disposable share link, one account with space, and one test directory. Invoke the two-phase acceptance service: create the exact batch inert, show its generated operation IDs/read-only mutation plan, mint only its expiring mutation/attempt authorizations, then atomically activate only that batch. Ordinary Start remains disabled and all other writes remain denied. First run with concurrency 1. Validate exact transferred cardinality, exact shared cardinality, response order, consumed attempt ordinals, reused persisted code, and exported row before asking whether to persist `NormalEnabled=1` and then testing concurrency 2.

- [ ] **Step 7: Do not commit or release without instruction**

Present focused diffs and verification results. Because target files had pre-existing changes, ask the user how they want the production work committed or integrated.
