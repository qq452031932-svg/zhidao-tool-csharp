# Account Management and Excel Feedback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add selective per-account logout with an optional, account-scoped terminal-history cleanup, and make local Excel imports start and report durable progress even while live transfer execution is disabled.

**Architecture:** Keep the WPF layer thin: view models consume an application-level preview/apply contract and never issue cleanup SQL. A new SQLite account-removal store owns the `BEGIN IMMEDIATE` manifest/revalidation/cleanup/credential-clear transaction; the existing profile lifecycle runs only after that transaction. Local import progress is reported only after each staging transaction commits and is independent of live-transfer availability.

**Tech Stack:** .NET 8, C#, WPF, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite, xUnit, Open XML streaming reader.

---

## File Map

- Create `src/BaiduBatchTransfer.Application/Accounts/AccountRemovalContracts.cs`: cleanup mode, account page row, preview, confirmation manifest digest, store interfaces and safe result codes.
- Create `src/BaiduBatchTransfer.Application/Accounts/AccountRemovalCoordinator.cs`: preview/apply orchestration and post-transaction profile cleanup/tombstone lifecycle.
- Modify `src/BaiduBatchTransfer.Application/Accounts/RemoveAccountService.cs`: expose an idempotent post-disable profile cleanup/finalization step without reopening the database race.
- Create `src/BaiduBatchTransfer.Infrastructure/Database/SqliteAccountRemovalStore.cs`: paged account query plus immediate-transaction manifest, guards, cleanup, counter repair, FK validation, disable and credential clearing.
- Modify `src/BaiduBatchTransfer.Application/Imports/ImportStagingService.cs`: optional committed-chunk progress callback.
- Modify `src/BaiduBatchTransfer.App/Features/Imports/ImportViewModel.cs`: remove the inner live-adapter gate and add the Chinese selection/import/progress state machine.
- Modify `src/BaiduBatchTransfer.App/Features/Imports/LocalImportWorkflow.cs`: bridge committed staging progress to the view model.
- Create `src/BaiduBatchTransfer.App/Features/Accounts/AccountManagementViewModel.cs`: keyset/page-bounded account list and per-row preview/confirmation/apply commands.
- Create `src/BaiduBatchTransfer.App/Features/Accounts/AccountManagementWindow.xaml` and `.xaml.cs`: Chinese account-management dialog.
- Create `src/BaiduBatchTransfer.App/Services/AccountManagementLauncher.cs`: testable window boundary and refresh result.
- Modify `src/BaiduBatchTransfer.App/MainWindowViewModel.cs`, `MainWindow.xaml`, and `AppBootstrap.cs`: add the button, dependency injection, and refresh count/wizard snapshot.
- Modify `src/BaiduBatchTransfer.App/Features/Jobs/CreateJobWizardViewModel.cs`: reload and reconcile eligible account options after account management closes.
- Add focused tests in App, Application and Infrastructure projects; reuse the real SQLite test database fixtures where foreign-key behavior matters.

### Task 1: Prove and Fix the Excel No-Response Boundary

**Files:**
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/ImportViewModelTests.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Imports/ImportViewModel.cs`

- [ ] **Step 1: Write the failing adapter-disabled Excel test**

Add a dialog fake that returns `D:\\imports\\selected.xlsx`, construct the view model with `TransferUnavailable`, execute `SelectAndImportExcelCommand`, and assert the local workflow receives the path and `SessionId` becomes non-null:

```csharp
[Fact]
public async Task ExcelSelectionStartsLocalWorkflowWhenTransfersAreUnavailable()
{
    var workflow = new FakeImportWorkflow();
    var vm = new ImportViewModel(
        workflow,
        new FakePageSource([], 0, null),
        new FakeDialogService(@"D:\imports\selected.xlsx"),
        new TransferUnavailable());

    await vm.SelectAndImportExcelCommand.ExecuteAsync(null);

    Assert.Equal(1, workflow.ExcelCalls);
    Assert.NotNull(vm.SessionId);
    Assert.Equal("导入完成，请分页检查异常、重复和账号映射。", vm.StatusMessage);
}
```

- [ ] **Step 2: Run the focused test and verify RED**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~ExcelSelectionStartsLocalWorkflowWhenTransfersAreUnavailable`

Expected: FAIL because `RunImportAsync` returns on `adapterAvailability.CanStartTransfers == false`, so `ExcelCalls` remains zero.

- [ ] **Step 3: Remove only the obsolete internal gate**

Delete the `adapterAvailability.CanStartTransfers` early-return block from `RunImportAsync`. Keep `AdapterStatusMessage` and every create/transfer gate unchanged. In `SelectAndImportExcelAsync`, set `StatusMessage` to `请选择 Excel 文件` before opening the dialog, `未选择文件` for null, and `已选择 Excel 文件，正在检查` before calling the workflow. Never include the path.

- [ ] **Step 4: Run ImportViewModel tests and verify GREEN**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~ImportViewModelTests`

Expected: all ImportViewModel tests PASS.

- [ ] **Step 5: Commit the isolated bug fix**

```powershell
git add tests/BaiduBatchTransfer.App.Tests/Features/ImportViewModelTests.cs src/BaiduBatchTransfer.App/Features/Imports/ImportViewModel.cs
git commit -m "fix: allow local excel imports without transfer gate"
```

### Task 2: Report Only Durable Import Progress

**Files:**
- Modify: `tests/BaiduBatchTransfer.Application.Tests/Imports/ImportStagingServiceTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/ImportViewModelTests.cs`
- Modify: `src/BaiduBatchTransfer.Application/Imports/ImportStagingService.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Imports/ImportViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Imports/LocalImportWorkflow.cs`

- [ ] **Step 1: Write a failing committed-progress test**

Use a small chunk size and a unit-of-work fake whose **third** commit fails (commit 1 creates the session, commit 2 saves chunk 1, commit 3 attempts chunk 2). Capture progress values and assert session creation plus only the first committed chunk are reported:

```csharp
var saved = new List<ImportCommittedProgress>();
await Assert.ThrowsAsync<InvalidOperationException>(() => service.StageAsync(
    request, Rows(7), saved.Add, CancellationToken.None));
Assert.Equal(
    [new(ImportProgressKind.SessionCreated, 0), new(ImportProgressKind.ChunkCommitted, 3)],
    saved);
```

- [ ] **Step 2: Run and verify RED**

Run: `dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~Progress`

Expected: compile FAIL because the committed-progress contract/overload does not exist.

- [ ] **Step 3: Add the progress contract after commit boundaries**

Add:

```csharp
public enum ImportProgressKind { SessionCreated, ChunkCommitted }
public sealed record ImportCommittedProgress(ImportProgressKind Kind, int PersistedRows);
```

Extend `StageAsync`, `ResumeAsync`, and the internal chunk processor with an optional `Action<ImportCommittedProgress>?` while preserving the existing overloads for callers that do not need progress. Report `SessionCreated` immediately after the initial `ImportSessions` transaction commits, then report `ChunkCommitted` only after each row-chunk `CommitAsync` returns successfully, using the session's persisted total—not the number of enumerated rows.

- [ ] **Step 4: Bridge progress through the local workflow and UI**

Add progress-capable overloads to the workflow import/resume methods and report `正在解析并写入草稿` after `SessionCreated`, followed by `正在解析并写入草稿，已保存 N 行` after durable chunks. Use the WPF dispatcher through `Progress<T>` so property changes remain on the UI thread. Preserve safe error mapping and resumability. At completion, format all counters: total, ready, duplicate, invalid, excluded, conflict, and needs-resolution.

Add UI tests that prove the `SessionCreated` message cannot appear before the session commit, a failed chunk cannot inflate N, and the final Chinese statistics include `冲突` and `待解析`.

- [ ] **Step 5: Run focused import tests**

Run:

```powershell
dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~Import
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~ImportViewModelTests
```

Expected: all focused tests PASS; failed/uncommitted chunks never inflate the displayed count.

- [ ] **Step 6: Commit**

```powershell
git add src/BaiduBatchTransfer.Application/Imports/ImportStagingService.cs src/BaiduBatchTransfer.App/Features/Imports/ImportViewModel.cs src/BaiduBatchTransfer.App/Features/Imports/LocalImportWorkflow.cs tests/BaiduBatchTransfer.Application.Tests/Imports tests/BaiduBatchTransfer.App.Tests/Features/ImportViewModelTests.cs
git commit -m "feat: show durable local import progress"
```

### Task 3: Define Account-Scoped Preview and Apply Contracts

**Files:**
- Create: `src/BaiduBatchTransfer.Application/Accounts/AccountRemovalContracts.cs`
- Create: `src/BaiduBatchTransfer.Application/Accounts/AccountRemovalCoordinator.cs`
- Modify: `tests/BaiduBatchTransfer.Application.Tests/Accounts/AccountLifecycleTests.cs`

- [ ] **Step 1: Write failing contract and coordinator tests**

Cover: empty account id, unavailable account, active-work rejection, preview safe fields, A-token-for-B rejection, stale-token rejection, store failure preventing profile cleanup, and cleanup-pending result after committed disable.

Use this public shape:

```csharp
public enum AccountExitHistoryMode { KeepHistory, RemoveTerminalHistory }
public enum AccountExitStatus { Ready, Removed, AlreadyRemoved, NotFound, ActiveWorkBlocked, StaleConfirmation, CleanupPending, Failed }
public enum AccountRemovalPreparationStatus { Prepared, AlreadyRemoved, NotFound, ActiveWorkBlocked, StaleConfirmation, Failed }
public sealed record AccountExitPreview(
    AccountExitStatus Status,
    string DisplayName,
    int TerminalOperationCount,
    int TerminalBatchCount,
    int SharedBatchCount,
    string ConfirmationToken,
    string SafeMessage);
public sealed record AccountExitRequest(
    Guid AccountId,
    AccountExitHistoryMode HistoryMode,
    string ConfirmationToken,
    DateTimeOffset RemovedAt);
public interface IAccountRemovalStore
{
    Task<AccountPage> GetAccountsPageAsync(int pageNumber, int pageSize, CancellationToken token);
    Task<AccountExitPreview> PreviewAsync(Guid accountId, AccountExitHistoryMode mode, CancellationToken token);
    Task<AccountRemovalPreparation> PrepareRemovalAsync(AccountExitRequest request, CancellationToken token);
    Task FinalizeTombstoneAsync(Guid accountId, DateTimeOffset removedAt, CancellationToken token);
}
```

Define the referenced records explicitly:

```csharp
public sealed record AccountPage(IReadOnlyList<AccountListItem> Items, int PageNumber, int PageSize, long TotalCount);
public sealed record AccountListItem(Guid AccountId, string DisplayName, string Status, bool IsEnabled);
public sealed record AccountRemovalPreparation(
    AccountRemovalPreparationStatus Status,
    Guid AccountId,
    string? ProfilePath,
    bool DatabasePrepared,
    string SafeMessage);
```

`AccountRemovalPreparation.ToString()` must redact the profile path and account id. Only `AccountRemovalPreparationStatus.Prepared` may proceed to external profile cleanup.

Ensure all `ToString()` methods redact display name, stable identity, token and paths.

- [ ] **Step 2: Run and verify RED**

Run: `dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~AccountRemoval`

Expected: compile FAIL for missing types.

- [ ] **Step 3: Implement the coordinator minimally**

`PreviewAsync` delegates to the store. `ApplyAsync` first calls the store's atomic preparation; only `Prepared` may call profile close/delete and tombstone finalization. Profile failure returns `CleanupPending`; it never restores credentials or enables the account.

- [ ] **Step 4: Run lifecycle tests and commit**

Run: `dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~Account`

Expected: all account application tests PASS.

```powershell
git add src/BaiduBatchTransfer.Application/Accounts tests/BaiduBatchTransfer.Application.Tests/Accounts
git commit -m "feat: define safe account removal workflow"
```

### Task 4: Build the Canonical Manifest and Immediate Transaction Guard

**Files:**
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteAccountRemovalStore.cs`
- Create: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/AccountRemovalStoreTests.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/BaiduBatchTransfer.Infrastructure.csproj` only if source/resource inclusion requires it.

- [ ] **Step 1: Write real-SQLite RED tests for guards and token binding**

Seed A/B/C and assert:

- any B operation in `Waiting`, `Reserved`, `Preparing`, `Submitting`, or `ResultPending` blocks preview and apply;
- an allocation for B in `Queued`, `Active`, `Paused`, `AwaitingCandidates`, or `PausedReady` blocks even with zero operations;
- A's token cannot remove B;
- changing any operation, allocation counter/path, resource ownership/status, batch field, import link or account state makes the token stale and produces zero writes;
- a competing `BEGIN IMMEDIATE` writer cannot assign new work between final validation and credential clearing.

- [ ] **Step 2: Run and verify RED**

Run: `dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~AccountRemovalStoreTests`

Expected: compile FAIL for `SqliteAccountRemovalStore`.

- [ ] **Step 3: Implement page-bounded account reads**

Reject page sizes outside `1..1000`. Select only safe fields for the UI. Keep deleted tombstones out of the normal list.

- [ ] **Step 4: Materialize and hash the canonical manifest**

Within one connection/transaction, select the account, stable identity fingerprint (`SHA-256` of the local canonical identity), all candidate terminal operations, every resource referenced by those operations, every B-constrained candidate resource even when it has no operation, **every resource in a B-only terminal batch that would be deleted as a whole**, and every public/shared resource whose fields affect survival or aggregate decisions. Include every persisted resource field used by deletion or aggregate decisions. Sort all collections by stable GUID/sequence keys and serialize fixed field order with invariant UTC timestamps. Include account id and history mode. Hash the bytes with SHA-256; compare with `CryptographicOperations.FixedTimeEquals`.

- [ ] **Step 5: Use a real immediate transaction for apply**

Open a connection and execute `BEGIN IMMEDIATE`. Recheck active-work guards, rebuild the manifest, verify the token, perform the optional cleanup, then update the target account to `Disabled`, `IsEnabled=0`, `EncryptedSession=NULL`, `LastValidatedAt=NULL`. Commit only after `PRAGMA foreign_key_check` returns no rows. Roll back on every exception/cancellation.

- [ ] **Step 6: Run guard/token tests**

Run: `dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~AccountRemovalStoreTests`

Expected: all guard/token tests PASS.

- [ ] **Step 7: Commit**

```powershell
git add src/BaiduBatchTransfer.Infrastructure/Database/SqliteAccountRemovalStore.cs tests/BaiduBatchTransfer.Infrastructure.Tests/Database/AccountRemovalStoreTests.cs
git commit -m "feat: atomically prepare selective account logout"
```

### Task 5: Delete Only the Target Account's Terminal History

**Files:**
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteAccountRemovalStore.cs`
- Modify: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/AccountRemovalStoreTests.cs`

- [ ] **Step 1: Write failing three-account cleanup tests**

Seed:

1. a B-only completed batch;
2. an A/B/C shared completed batch;
3. a shared success-target batch with unused public and B-exclusive candidates;
4. execution logs bound by account, operation, resource, and public batch context;
5. linked completed import sessions.

Assert `KeepHistory` changes only B account credentials/status. Assert `RemoveTerminalHistory` removes B terminal operations/allocations and B-owned resources/logs, preserves A/C byte-for-byte account/session records and operation results, preserves shared public context, and removes a whole B-only graph only after nulling both import/batch references.

- [ ] **Step 2: Run and verify RED**

Run: `dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~AccountRemovalHistory`

Expected: FAIL because cleanup is not implemented.

- [ ] **Step 3: Implement deletion in foreign-key-safe order**

Use only ids already present in the validated manifest. Delete target logs, target terminal operations and target allocations. Delete a resource only when the manifest proves it was referenced by a deleted B operation or was constrained to B and no surviving operation references it. Whole-batch deletion may cascade only across the fully manifested resource graph. For B-only terminal batches, null `ImportSessions.CreatedBatchJobId`, `ImportSessions.AppendToBatchJobId`, and `BatchJobs.ImportSessionId` before deleting the graph/session. For shared batches, preserve public audit rows and other accounts.

- [ ] **Step 4: Recalculate shared aggregates and protect nonterminal data**

Recompute `BatchJobs.ResourceCount` and `OperationCount` from surviving rows and recalculate every surviving allocation count from persisted operations/resources. Any nonterminal row discovered during apply aborts the entire transaction. Never delete import drafts.

- [ ] **Step 5: Add an injected mid-cleanup failure and verify rollback**

Inject a test-only command boundary or SQLite trigger that fails after operation deletion but before account disable. Assert all tables and B credentials are unchanged after rollback.

- [ ] **Step 6: Run and commit**

Run:

```powershell
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~AccountRemoval
dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~Account
```

Expected: all focused tests PASS.

```powershell
git add src/BaiduBatchTransfer.Infrastructure/Database/SqliteAccountRemovalStore.cs tests/BaiduBatchTransfer.Infrastructure.Tests/Database/AccountRemovalStoreTests.cs
git commit -m "feat: clean terminal history for one account"
```

### Task 6: Preserve the Existing Secure Profile Lifecycle

**Files:**
- Modify: `src/BaiduBatchTransfer.Application/Accounts/RemoveAccountService.cs`
- Modify: `src/BaiduBatchTransfer.Application/Accounts/AccountRemovalCoordinator.cs`
- Modify: `tests/BaiduBatchTransfer.Application.Tests/Accounts/AccountLifecycleTests.cs`

- [ ] **Step 1: Write RED tests for post-transaction cleanup**

Assert call order `prepare database -> close -> delete -> tombstone`; a close failure skips deletion and leaves `CleanupPending`; unsafe path never reaches delete; delete/tombstone failure returns `CleanupPending`; retry first closes successfully and then resumes deletion/finalization; A/C profile paths are never passed to the store.

- [ ] **Step 2: Run and verify RED**

Run: `dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~Remove`

Expected: FAIL until the lifecycle is split.

- [ ] **Step 3: Refactor without weakening ownership/path checks**

Keep `IAccountProfilePathPolicy.EnsureSafeAccountPath`, `IWebViewProfileStore.CloseAsync`, and `DeleteAsync`. Remove the old independent disable transaction from the coordinator path. Expose a method that accepts the already-disabled account snapshot from `PrepareRemovalAsync`, performs idempotent profile cleanup, and calls `FinalizeTombstoneAsync` only after safe delete.

- [ ] **Step 4: Run all Task9 lifecycle/path tests**

Run:

```powershell
dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~AccountLifecycleTests
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~ProfilePathSecurityTests
```

Expected: all existing and new security tests PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/BaiduBatchTransfer.Application/Accounts tests/BaiduBatchTransfer.Application.Tests/Accounts
git commit -m "refactor: finalize account logout after atomic disable"
```

### Task 7: Add the Chinese Account Management Window

**Files:**
- Create: `src/BaiduBatchTransfer.App/Features/Accounts/AccountManagementViewModel.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Accounts/AccountManagementWindow.xaml`
- Create: `src/BaiduBatchTransfer.App/Features/Accounts/AccountManagementWindow.xaml.cs`
- Create: `src/BaiduBatchTransfer.App/Services/AccountManagementLauncher.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/AccountManagementViewModelTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/MainWindowViewModelTests.cs`

- [ ] **Step 1: Write the view-model RED tests**

Cover page size `<=1000`, safe columns only, row selection, both radio modes, target display name in confirmation, explicit “其他账号不受影响”, active-work Chinese error, stale token, cancel/no write, cleanup-pending status, and refresh event. Assert no user-visible string contains ids, cookies, token, paths, URLs or exception messages.

- [ ] **Step 2: Run and verify RED**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~AccountManagement`

Expected: compile FAIL for missing view model/window contracts.

- [ ] **Step 3: Implement the view model**

Expose one page of rows at a time, default to `KeepHistory`, and build confirmation from the preview. Disable confirm while loading/applying. Do not accept a token supplied by the view; store the last preview internally and invalidate it on account/mode/page changes.

- [ ] **Step 4: Implement the window**

Use a virtualizing `DataGrid` with columns `账号名称`, `登录状态`, `启用状态`, `操作`. The confirmation area uses two radio buttons:

- `仅退出账号，保留历史记录`
- `退出账号，并清理该账号的终态任务历史`

Show the terminal-operation/batch/shared-batch counts and the shared-context retention note. Provide `取消` and `确认退出` buttons. No global cleanup action exists.

- [ ] **Step 5: Run App focused tests and build XAML**

Run:

```powershell
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~AccountManagement|FullyQualifiedName~MainWindowViewModelTests"
dotnet build src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj -c Debug --no-restore -warnaserror
```

Expected: tests PASS; XAML build has zero warnings/errors.

- [ ] **Step 6: Commit**

```powershell
git add src/BaiduBatchTransfer.App/Features/Accounts src/BaiduBatchTransfer.App/Services/AccountManagementLauncher.cs tests/BaiduBatchTransfer.App.Tests/Features/AccountManagementViewModelTests.cs tests/BaiduBatchTransfer.App.Tests/MainWindowViewModelTests.cs
git commit -m "feat: add selective account management window"
```

### Task 8: Wire DI, Main Window Refresh and Real SQLite Import

**Files:**
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Modify: `src/BaiduBatchTransfer.App/MainWindowViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/MainWindow.xaml`
- Modify: `src/BaiduBatchTransfer.App/Features/Jobs/CreateJobWizardViewModel.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/CreateJobWizardViewModelTests.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/LocalExcelImportIntegrationTests.cs`

- [ ] **Step 1: Write composition and real-import RED tests**

Resolve `SqliteAccountRemovalStore`, `AccountRemovalCoordinator`, and the management launcher from the host without registering a fake adapter. Create a real `.xlsx` fixture, return its path through the dialog fake while transfer availability is false, execute the command, and assert one `ImportSessions` row exists plus keyset page output.

- [ ] **Step 2: Run and verify RED**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~AppBootstrapTests|FullyQualifiedName~LocalExcelImportIntegrationTests"`

Expected: FAIL until DI and the real boundary are wired.

- [ ] **Step 3: Register the store/coordinator/launcher and button**

Add `账号管理` beside `添加百度账号`. After any result whose database preparation committed—including `Removed` and `CleanupPending`—refresh `AccountStatusMessage` and wizard state. Add `CreateJobWizardViewModel.ReloadAccounts(IReadOnlyList<WizardAccountOption>)`: it replaces only the bounded account-option collection, clears selected/deleted/ineligible accounts, preserves surviving account selections by `AccountId`, invalidates any preview token, recomputes single-account defaulting/multi-account mode gates, and raises command state. `AppShellFactory` supplies a refresh callback that reloads eligible accounts and calls this method. Keep live transfer execution unavailable as currently gated.

Add tests for removing B from an A/B/C wizard snapshot: A/C survive with their selections, B disappears, a previous confirmation token is cleared, and a one-account result visibly defaults but still requires confirmation.

- [ ] **Step 4: Run focused integration tests**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Release --no-restore`

Expected: all App tests PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/BaiduBatchTransfer.App/AppBootstrap.cs src/BaiduBatchTransfer.App/MainWindowViewModel.cs src/BaiduBatchTransfer.App/MainWindow.xaml src/BaiduBatchTransfer.App/Features/Jobs/CreateJobWizardViewModel.cs tests/BaiduBatchTransfer.App.Tests
git commit -m "feat: wire account management and excel import feedback"
```

### Task 9: Security Review and Final Verification

**Files:**
- Review: every file changed by Tasks 1-8
- Create: `tests/BaiduBatchTransfer.App.Tests/Acceptance/AccountManagementAcceptanceSeeder.cs`
- Modify: `src/BaiduBatchTransfer.App/App.xaml.cs` to accept a disposable data root only in `DEBUG` builds.
- Update tests only when review finds a reproducible gap.

- [ ] **Step 1: Request an independent code review**

Use `@superpowers:requesting-code-review`. Ask specifically about cross-account deletion, active-allocation races, manifest completeness, foreign-key order, profile cleanup ownership, progress truthfulness, and sensitive-data exposure.

- [ ] **Step 2: Convert every Critical/Important finding into a failing test**

Run each new test first and capture RED before changing production code. Fix only confirmed issues and request re-review until approved.

- [ ] **Step 3: Restore and build strictly**

Run:

```powershell
dotnet restore BaiduBatchTransfer.sln --locked-mode
dotnet build BaiduBatchTransfer.sln -c Release --no-restore -warnaserror
```

Expected: restore succeeds; build has zero warnings and zero errors.

- [ ] **Step 4: Run the full suite and database recovery tests**

Run:

```powershell
dotnet test BaiduBatchTransfer.sln -c Release --no-build --no-restore
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~Migration|FullyQualifiedName~Recovery|FullyQualifiedName~AccountRemoval"
```

Expected: all functional tests PASS. If an established large-fixture timing assertion fails only under parallel full-suite load, rerun that test alone and report both measurements; do not hide functional failures.

- [ ] **Step 5: Inspect sensitive text and diffs**

Run:

```powershell
rg -n "Cookie|Authorization|BDUSS|STOKEN|ConfirmationToken|BaiduUserId" src/BaiduBatchTransfer.App src/BaiduBatchTransfer.Application/Accounts src/BaiduBatchTransfer.Infrastructure/Database/SqliteAccountRemovalStore.cs
git diff --check
git status --short
```

Expected: only structural/redacted uses; no user-visible leaks; diff check clean.

- [ ] **Step 6: Create and verify the disposable acceptance fixture**

Add a test-only seeder executable entry through an xUnit test named `SeedAccountManagementAcceptanceDatabase`. It must require explicit `BBT_ACCEPTANCE_ROOT` and `BBT_ACCEPTANCE_SCENARIO` environment variables, reject an existing/nonempty directory, migrate a new database, and seed A/B/C by calling the real repository/scheduling-store APIs (not unrestricted inserts). Supported isolated scenarios are `keep-history` (terminal B history), `cleanup-history` (terminal B-only and A/B/C shared graphs), and `active-work` (a nonterminal B allocation with no operation). It writes only the disposable root/scenario to test output and never writes the normal LocalAppData root.

In `DEBUG` only, allow `App.OnStartup` to accept `--data-root <absolute-empty-or-seeded-acceptance-root>`; Release always uses the normal LocalAppData path. Add tests that Release compilation has no override branch and Debug rejects a root lacking the acceptance marker file.

Concrete invocation:

```powershell
foreach ($scenario in @("keep-history", "cleanup-history", "active-work")) {
    $acceptanceRoot = Join-Path $env:TEMP ("bbt-account-acceptance-" + $scenario + "-" + [Guid]::NewGuid().ToString("N"))
    $env:BBT_ACCEPTANCE_ROOT = $acceptanceRoot
    $env:BBT_ACCEPTANCE_SCENARIO = $scenario
    dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~SeedAccountManagementAcceptanceDatabase
    dotnet run --project src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj -c Debug --no-build -- --data-root $acceptanceRoot
    # Close the app and complete the scenario checks before continuing.
    if (-not ($acceptanceRoot.StartsWith($env:TEMP)) -or -not (Test-Path -LiteralPath (Join-Path $acceptanceRoot ".acceptance-marker"))) { throw "Unsafe acceptance cleanup target." }
    Remove-Item -LiteralPath $acceptanceRoot -Recurse
}
```

Each scenario uses a fresh account database, so successfully removing B in one mode cannot invalidate another mode's acceptance. After each run, close the app, verify the exact root is under `$env:TEMP` and contains the marker, then remove only that disposable directory.

- [ ] **Step 7: Perform the WPF acceptance flow**

Launch the Debug app with the seeded disposable root without enabling real transfer execution. Never register the fixture in production DI. Verify:

1. selecting a valid Excel file immediately shows Chinese checking/parsing/progress text and creates a draft while transfer execution remains unavailable;
2. account management lists the logged-in account;
3. with A/B/C, removing B in keep-history mode leaves A/C logged in;
4. removing B in cleanup mode affects only B's terminal history;
5. a nonterminal B operation/allocation produces a Chinese refusal and no write;
6. no screen offers global cleanup or cloud-file deletion.

- [ ] **Step 8: Final commit**

```powershell
git add src tests docs
git commit -m "feat: complete account management and import feedback"
```
