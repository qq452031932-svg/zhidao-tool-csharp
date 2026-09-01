# Managed-Session Transfer/Share Pipeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the approved single-account batch-transfer workflow using a minimized managed Chromium session, fixed same-origin background operations, a durable transfer/share pipeline, automatic permanent sharing, and automatic result export.

**Architecture:** Keep migration 21 and the existing transfer/share operation graph as the durable source of truth. Add a fixed managed-session request boundary that exposes named Baidu operations rather than arbitrary URLs or scripts, then run transfer and share reservations through one global per-batch limiter so completed transfers immediately feed the durable share queue. Retain the existing DOM workflow only as a serial compatibility fallback and keep all real cloud writes behind the existing explicit live-acceptance gate.

**Tech Stack:** .NET 8, WPF, SQLite, CommunityToolkit.Mvvm, managed Chromium/CDP helper, Python helper protocol, Open XML, xUnit, unittest.

---

## Current checkpoint and constraints

- Work only in `C:\Users\45203\Documents\dakazhandian\.worktrees\transfer-share-link-replacement`.
- Tasks 0 and 1 of `2026-09-01-single-account-batch-transfer-auto-share.md` are complete. Task 2's identity/web-context/quota read boundary is complete. This amendment supersedes the remaining direct-cookie-write tasks.
- Preserve all unrelated dirty-worktree changes. Use `apply_patch`; do not reset, clean, bulk-stage, or commit production files without renewed instruction.
- Do not spawn subagents; the user requested sequential work in this task.
- Do not execute a real folder creation, transfer, or share until the offline gate passes and the user approves the exact disposable test.
- A normal business caller cannot provide an arbitrary URL, HTTP verb, JavaScript string, selector, cookie, token, `Origin`, or `Referer`.
- The managed browser is visible for login/security verification, minimized while normal work runs, and restored only when user action is required.

## File map

### New focused components

- `src/BaiduBatchTransfer.App/Features/ManagedBrowser/ManagedSessionOperationProtocol.cs` — named, allowlisted managed-session operations and typed bounded results.
- `src/BaiduBatchTransfer.App/Features/ManagedBrowser/ManagedSessionOperationClient.cs` — .NET client over the existing helper transport; no generic execute method.
- `tools/managed_browser_helper/session_operations.py` — fixed same-origin request implementations owned by the helper.
- `src/BaiduBatchTransfer.Application/Scheduling/DirectBackgroundPipelineCoordinator.cs` — one durable two-queue scheduler with a shared global limiter and backlog control.
- `src/BaiduBatchTransfer.App/Services/DirectBackgroundPipelineHostedService.cs` — host lifecycle, pause/resume, minimized-browser activation, and verification handoff.
- `tests/BaiduBatchTransfer.App.Tests/Features/ManagedSessionOperationProtocolTests.cs`
- `tests/BaiduBatchTransfer.App.Tests/Features/ManagedSessionOperationClientTests.cs`
- `tests/BaiduBatchTransfer.Application.Tests/Scheduling/DirectBackgroundPipelineCoordinatorTests.cs`
- `tests/BaiduBatchTransfer.App.Tests/Services/DirectBackgroundPipelineHostedServiceTests.cs`

### Existing components to extend

- `tools/managed_browser_helper/protocol.py`, `main.py`, and helper manifest/package scripts.
- `src/BaiduBatchTransfer.App/Features/ManagedBrowser/ManagedBrowserProtocol.ShareSubmission.cs` and related share-session classes — exact sets of one or more top-level objects.
- `src/BaiduBatchTransfer.App/Features/ManagedBrowser/ManagedChromiumTransferAdapter.cs` and `ManagedChromiumShareAdapter.cs` — route normal direct batches through named session operations and retain serial DOM fallback.
- `src/BaiduBatchTransfer.Infrastructure/Database/SqliteTransferSchedulingStore.cs` and `SqliteShareSchedulingStore.cs` — direct-batch reservations, exact-set handoff, backlog query, and batch pause fence.
- `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs` and `.xaml` — one selected account, one target, fixed auto-share, concurrency 1–3.
- `src/BaiduBatchTransfer.App/Features/Jobs/LocalJobResultsWorkflow.cs` and result/export components — one result row per source link and newline-separated actual names.
- `src/BaiduBatchTransfer.App/AppBootstrap.cs` — production registration after all fixed operation contracts are present.

## Task 1: Freeze the named managed-session operation boundary

**Files:**
- Create the protocol/client/helper files listed above.
- Modify `tools/managed_browser_helper/protocol.py`, `main.py`, and `src/BaiduBatchTransfer.App/Features/ManagedBrowser/ManagedBrowserHelperClient.cs`.
- Test the new C# files and `tools/managed_browser_helper/tests/test_protocol.py`.

- [ ] **Step 1: Write protocol RED tests**

Define only these actions: `InspectSource`, `EnsureDirectory`, `SubmitTransfer`, `ObserveTransfer`, `SubmitShare`, and `ObserveShare`. Assert unknown actions, extra JSON fields, arbitrary URLs/verbs/scripts/selectors, non-HTTPS origins, responses above 64 KiB, identity mismatches, and unredacted errors fail before dispatch.

```csharp
Assert.Throws<ManagedBrowserProtocolException>(() =>
    ManagedSessionOperationProtocol.ParseRequest("{\"action\":\"ExecuteScript\"}"));
Assert.DoesNotContain("BDUSS", failure.SafeMessage, StringComparison.OrdinalIgnoreCase);
```

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests\BaiduBatchTransfer.App.Tests\BaiduBatchTransfer.App.Tests.csproj --no-restore --filter "FullyQualifiedName~ManagedSessionOperation"
python -m unittest tools.managed_browser_helper.tests.test_protocol
```

Expected: failure because named session operations do not exist.

- [ ] **Step 3: Implement the minimal fixed protocol**

Every request contains the exact browser/session binding, Baidu UID, operation ID, attempt ordinal, deadline, and operation-specific bounded fields. The helper owns all endpoint paths, headers, request serialization, and response parsing. `ToString()` values expose only action, deadline, and redacted binding metadata.

- [ ] **Step 4: Add deny-by-default production routing**

The client may run read operations against sanitized fixtures. Write actions return `ContractUnavailable` until their exact request/response contract is either proven from delivered static assets or approved live characterization. No guessed endpoint can be registered.

- [ ] **Step 5: Run GREEN and package verification**

Run the Step 2 commands plus `scripts\verify_managed_browser_helper_package.ps1`. Expected: pass with zero real network calls.

## Task 2: Support an exact multi-object transfer/share handoff

**Files:**
- Modify `ManagedBrowserProtocol.ShareSubmission.cs`, `ManagedShareSubmissionSession.cs`, `ManagedShareAutomationSessionFactory.cs`, `ManagedChromiumShareAdapter.cs`, `tools/managed_browser_helper/protocol.py`, and `session_operations.py`.
- Test `ManagedBrowserShareSubmissionProtocolTests.cs`, `ManagedShareSubmissionSessionTests.cs`, `ManagedChromiumShareAdapterTests.cs`, and helper protocol tests.

- [ ] **Step 1: Write multi-object RED tests**

Assert a source with `N >= 1` top-level objects produces one ordered exact destination set of `N`, one share containing the same `N` exact IDs, and one result row. Reject zero items, duplicates, count mismatch, set mismatch, account mismatch, or response-order loss.

```csharp
Assert.Equal(sourceItems.Count, acceptedShare.ExactRemoteItems.Count);
Assert.Equal(destinationItems.Select(x => x.ExactId), acceptedShare.ExactRemoteItems.Items.Select(x => x.ExactId));
```

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests\BaiduBatchTransfer.App.Tests\BaiduBatchTransfer.App.Tests.csproj --no-restore --filter "FullyQualifiedName~ManagedBrowserShareSubmissionProtocolTests|FullyQualifiedName~ManagedShareSubmissionSessionTests|FullyQualifiedName~ManagedChromiumShareAdapterTests"
```

Expected: fail at the current `exactItems.Count != 1` restriction.

- [ ] **Step 3: Generalize immutable evidence**

Replace singular `ExactItem` fields with `RemoteItemIdentitySet ExactItems`; serialize bounded arrays with stable response ordinals; fingerprint the complete ordered set; update equality and outcome checks to compare count, exact IDs, account binding, and set fingerprint.

- [ ] **Step 4: Preserve duplicate-save naming**

Require `DuplicatePolicy.Rename`. Persist actual destination IDs, paths, and names returned/confirmed for this submission; never synthesize `(n)` locally and never infer ownership from whole-directory differences.

- [ ] **Step 5: Run GREEN**

Run Step 2 and existing single-item managed-transfer/share regressions. Expected: old single-item cases still pass and multi-object cases pass.

## Task 3: Implement the durable two-queue pipeline

**Files:**
- Create `DirectBackgroundPipelineCoordinator.cs` and its tests.
- Modify the two SQLite scheduling stores, `OperationExecutor.cs`, and `ShareOperationExecutor.cs`.

- [ ] **Step 1: Write pipeline RED tests**

Cover: transfer success immediately makes only its linked share reservable; later transfers may run while earlier shares run; transfer and share share one global limit; default 2/max 3; no fourth operation; backlog threshold stops new transfers; share completion reopens transfer reservation; restart resumes both queues.

```csharp
Assert.True(maximumObservedRemoteWrites <= batch.MaxConcurrency);
Assert.Equal(0, transferAdapter.CallsFor(alreadyTransferredResource));
```

- [ ] **Step 2: Add failure/recovery RED tests**

One unknown transfer result permits exactly one durable resubmission, then fails. A confirmed transfer plus failed share retries only the share. Login expiry or verification atomically pauses both queues before any new reservation. UI-facing result remains only success/failure.

- [ ] **Step 3: Run RED**

```powershell
dotnet test tests\BaiduBatchTransfer.Application.Tests\BaiduBatchTransfer.Application.Tests.csproj --no-restore --filter "FullyQualifiedName~DirectBackgroundPipelineCoordinatorTests|FullyQualifiedName~OperationExecutorTests|FullyQualifiedName~ShareOperationExecutorTests"
dotnet test tests\BaiduBatchTransfer.Infrastructure.Tests\BaiduBatchTransfer.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~SqliteSchedulingStoreTests|FullyQualifiedName~ShareSchedulingStoreTests"
```

- [ ] **Step 4: Implement one shared limiter and durable prioritization**

Use the existing transfer/share rows as the queues. Reserve shares first when unshared confirmed transfers reach the fixed backlog threshold; otherwise alternate fairly. Both kinds acquire the same per-batch semaphore. A typed throttle or two consecutive temporary failures lowers the effective limit to one. DOM compatibility fallback always forces one.

- [ ] **Step 5: Implement exact retry behavior**

Persist intent/attempt before each write. Unknown transfer: at most one extra submission. Share failure after confirmed transfer: keep exact transfer evidence and retry share only. Batch pause is durable and checked by every reservation query.

- [ ] **Step 6: Run GREEN and restart regressions**

Run Step 3 plus database recovery tests. Expected: pass with synthetic adapters and temporary databases.

## Task 4: Wire the simplified production workspace

**Files:**
- Modify `TransferWorkspaceViewModel.cs`, `TransferWorkspaceView.xaml`, `WizardContracts.cs`, `CreateJobWizardViewModel.cs`, `DirectBackgroundAccountLoginLauncher.cs`, `DirectBackgroundPipelineHostedService.cs`, and `AppBootstrap.cs`.
- Test corresponding App tests.

- [ ] **Step 1: Write UI/wiring RED tests**

Assert exactly one selected left account; default `/`; auto-share fixed on with no checkbox; rename-on-duplicate fixed; default concurrency 2 and range 1–3; persistent local result directory; one confirmation; no copy/distribute mode buttons; start creates an active direct batch only when the fixed write contract is available.

- [ ] **Step 2: Write browser lifecycle RED tests**

Assert login shows the browser; successful identity binding minimizes it; ordinary work keeps it minimized; login/security pause restores it; continue revalidates exact UID then minimizes it; closing the app terminates the owned browser safely.

- [ ] **Step 3: Run RED**

```powershell
dotnet test tests\BaiduBatchTransfer.App.Tests\BaiduBatchTransfer.App.Tests.csproj --no-restore --filter "FullyQualifiedName~TransferWorkspaceViewModelTests|FullyQualifiedName~CreateJobWizardViewModelTests|FullyQualifiedName~DirectBackgroundPipelineHostedServiceTests|FullyQualifiedName~AppBootstrapTests"
```

- [ ] **Step 4: Implement minimal XAML/view-model and registrations**

Keep paste, Excel, target directory, concurrency, and Start. Derive the account from the left selection, snapshot fixed share/duplicate settings, activate the pipeline host, and keep unrelated navigation modules deferred.

- [ ] **Step 5: Run GREEN in Debug and Release test configurations**

Expected: focused UI/wiring tests pass and WPF resources load.

## Task 5: Complete projection and automatic export

**Files:**
- Modify `JobResultsContracts.cs`, `LocalJobResultsWorkflow.cs`, `JobResultsView.xaml`, `LocalResultExportCoordinator.cs`, and `ReplacementWorkbookExportService.cs`.
- Test existing result/export suites.

- [ ] **Step 1: Write RED tests**

One source link produces one row. Actual names appear one per line in response order. Overall success requires transfer plus share. Export runs automatically at batch completion, never overwrites, persists its destination, and export-only retry performs zero remote calls.

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests\BaiduBatchTransfer.App.Tests\BaiduBatchTransfer.App.Tests.csproj --no-restore --filter "FullyQualifiedName~JobResultsViewModelTests|FullyQualifiedName~LocalJobResultsWorkflowTests"
dotnet test tests\BaiduBatchTransfer.Application.Tests\BaiduBatchTransfer.Application.Tests.csproj --no-restore --filter "FullyQualifiedName~LocalResultExportCoordinatorTests|FullyQualifiedName~ReplacementWorkbookExportTests"
```

- [ ] **Step 3: Implement and run GREEN**

Persist new URL/code only in the secured result/export path. Logs remain redacted. A local export failure never changes remote success and exposes only an export retry.

## Task 6: Offline release gate, UI launch, and one live acceptance

**Files:**
- Create `docs/testing/2026-09-01-managed-session-pipeline-handoff.md`.
- Modify production only for defects proven by verification.

- [ ] **Step 1: Run focused and full relevant suites**

Run Core, BaiduAdapter, Application, Infrastructure, and focused App tests. Record any unrelated pre-existing failures separately; all new and directly affected tests must pass.

- [ ] **Step 2: Build Debug and Release**

```powershell
dotnet build BaiduBatchTransfer.sln -c Debug --no-restore -warnaserror
dotnet build BaiduBatchTransfer.sln -c Release --no-restore -warnaserror
```

Expected: zero warnings and zero errors.

- [ ] **Step 3: Run diff/secret checks and launch read-only UI**

Verify the compact batch-transfer page, selected account, minimized-browser behavior, navigation alignment, result columns, and absence of auto-share/mode toggles. Do not press Start with real data.

- [ ] **Step 4: Request one exact live-write approval**

Ask for approval to use one disposable share link, the selected account, and one test directory at concurrency 1. Characterize/activate only the fixed write contracts needed for that operation, then validate transfer cardinality, duplicate naming, exact multi-object share membership, four-character code, and exported row.

- [ ] **Step 5: Enable ordinary use only after acceptance passes**

Persist normal direct availability only after the user approves activation. Then run a small concurrency-2 acceptance before describing the feature as ready for large batches.

