# Clear Current Import Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a safe “清空当前导入” action that permanently removes only the currently selected, not-yet-used local import draft and restores the transfer workspace to an empty state.

**Architecture:** A focused application contract represents the idempotent/conflict outcomes, while a singleton SQLite store performs exhaustive relationship checks and deletion in one transaction. `TransferWorkspaceViewModel` owns user confirmation and coordinates `ImportViewModel` plus `CreateJobWizardViewModel` reset only after a successful delete of the captured current session.

**Tech Stack:** .NET 8, C#, WPF, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite, xUnit.

---

## File structure

- Create `src/BaiduBatchTransfer.Core/Abstractions/ILocalImportDraftCleanupStore.cs`: cleanup result and persistence contract.
- Create `src/BaiduBatchTransfer.Application/Imports/LocalImportDraftCleanupService.cs`: validates IDs and delegates to the store.
- Create `src/BaiduBatchTransfer.Infrastructure/Database/SqliteLocalImportDraftCleanupStore.cs`: transactional safety checks and delete.
- Modify `src/BaiduBatchTransfer.App/AppBootstrap.cs`: singleton DI wiring.
- Modify `src/BaiduBatchTransfer.App/Features/Imports/ImportViewModel.cs`: expose reset state operation with generation invalidation.
- Modify `src/BaiduBatchTransfer.App/Features/Jobs/CreateJobWizardViewModel.cs`: detach/reset the captured import context.
- Modify `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs`: workspace-owned command, confirmation, busy fencing, and coordinated reset.
- Create `src/BaiduBatchTransfer.App/Features/Transfers/CurrentImportCleanupInteraction.cs`: dedicated WPF confirmation using `MessageBoxResult.No`.
- Modify `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceView.xaml`: add the toolbar button.
- Add focused tests in the corresponding Core/Application/Infrastructure/App test projects.

### Task 1: Define and test the cleanup contract

**Files:**
- Create: `src/BaiduBatchTransfer.Core/Abstractions/ILocalImportDraftCleanupStore.cs`
- Create: `src/BaiduBatchTransfer.Application/Imports/LocalImportDraftCleanupService.cs`
- Create: `tests/BaiduBatchTransfer.Application.Tests/Imports/LocalImportDraftCleanupServiceTests.cs`

- [ ] **Step 1: Write failing service tests**

Cover empty session rejection, delegation of a valid session, cancellation propagation, and the result states `Deleted`, `AlreadyAbsent`, `BatchConflict`, and `LocalDependencyConflict` without including user link content in results.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj --no-restore --filter FullyQualifiedName~LocalImportDraftCleanupServiceTests
```

Expected: compile/test failure because the contract and service do not exist.

- [ ] **Step 3: Implement the minimal contract and service**

Define a closed result enum/record containing only disposition and deleted row count. `DeleteAsync(Guid importSessionId, CancellationToken)` rejects `Guid.Empty` and delegates once.

- [ ] **Step 4: Run the focused tests and verify GREEN**

Expected: all `LocalImportDraftCleanupServiceTests` pass.

- [ ] **Step 5: Commit**

```powershell
git add src/BaiduBatchTransfer.Core/Abstractions/ILocalImportDraftCleanupStore.cs src/BaiduBatchTransfer.Application/Imports/LocalImportDraftCleanupService.cs tests/BaiduBatchTransfer.Application.Tests/Imports/LocalImportDraftCleanupServiceTests.cs
git commit -m "feat: define current import cleanup contract"
```

### Task 2: Implement exhaustive SQLite safety and deletion

**Files:**
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteLocalImportDraftCleanupStore.cs`
- Create: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/SqliteLocalImportDraftCleanupStoreTests.cs`
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs`

- [ ] **Step 1: Write failing real-SQLite tests**

Create isolated tests for: normal deletion; missing session idempotency; each independent guard (`CreatedBatchJobId`, `AppendToBatchJobId`, `BatchJobs.ImportSessionId`, `BatchCreationReceipts.ImportSessionId`, `ResourceItems.StagedImportRowId`); cross-session `DuplicateOfStagedRowId`; cascaded `ImportColumnDefinitions`/`StagedImportRowPayloads`; foreign-key check; and a concurrent second call. For rollback, install a test-only SQLite trigger that aborts deletion of the target `ImportSessions` row after staged rows have been explicitly deleted, then prove the transaction restores those staged rows and all cascade children.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~SqliteLocalImportDraftCleanupStoreTests
```

Expected: failure because the SQLite store is absent.

- [ ] **Step 3: Implement one-transaction recheck and delete**

Use an immediate/serializing transaction compatible with the existing factory. Re-read the session; return `AlreadyAbsent` when missing. Execute explicit existence checks for all five batch relationships and the cross-session duplicate dependency. Before deletion, count target `StagedImportRows`, `ImportColumnDefinitions`, and `StagedImportRowPayloads`. Delete the staged rows explicitly and require the affected-row count to equal the pre-count, then delete exactly one session. Before commit, require zero residue for the target session/rows in all four tables and an empty `PRAGMA foreign_key_check`; cascaded child deletion is valid only for the two declared child tables. Roll back on any exception or count/postcondition mismatch.

- [ ] **Step 4: Write failing bootstrap identity/injection tests**

Update `AppBootstrapTests` first to require the concrete store, interface, and service to resolve as the same singletons and to require `AppShellFactory` to inject the service into its manually constructed `TransferWorkspaceViewModel`.

Run:

```powershell
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --no-restore --filter FullyQualifiedName~AppBootstrapTests
```

Expected: RED because registration and constructor wiring are absent.

- [ ] **Step 5: Add singleton DI and constructor wiring**

Register `SqliteLocalImportDraftCleanupStore` once as the `ILocalImportDraftCleanupStore`, then register `LocalImportDraftCleanupService`; inject that service through `AppShellFactory` and the manual `new TransferWorkspaceViewModel(...)` call. Verify root and scoped resolutions use the same singleton store/service.

- [ ] **Step 6: Run focused tests and verify GREEN**

Run:

```powershell
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~SqliteLocalImportDraftCleanupStoreTests
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --no-restore --filter FullyQualifiedName~AppBootstrapTests
```

Expected: both filters pass.

- [ ] **Step 7: Commit**

```powershell
git add src/BaiduBatchTransfer.Infrastructure/Database/SqliteLocalImportDraftCleanupStore.cs tests/BaiduBatchTransfer.Infrastructure.Tests/Database/SqliteLocalImportDraftCleanupStoreTests.cs src/BaiduBatchTransfer.App/AppBootstrap.cs tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs
git commit -m "feat: safely delete unused import drafts"
```

### Task 3: Coordinate complete workspace reset

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Features/Imports/ImportViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Jobs/CreateJobWizardViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Transfers/CurrentImportCleanupInteraction.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/ImportViewModelTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/CreateJobWizardViewModelTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewModelTests.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/CurrentImportCleanupInteractionTests.cs`
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs`

- [ ] **Step 1: Write failing reset and concurrency tests**

Prove: no session disables command; busy/editing-disabled states disable it; dedicated confirmation text carries the exact row count and WPF uses `MessageBoxResult.No`; confirmation cancellation is inert; success clears import `Rows`, `SelectedRow`, share URL/extraction-code edit buffers, exclusion-selection state, `nextCursor`, `TotalRows`, `StatisticsText`, `CanResumeImport`, durable-session state, `SessionId`, and replay source; it also clears pending-row bookkeeping and detaches the wizard import ID, ready/conflict/resolution counts, precheck/account mapping state, batch/confirmation tokens, final-confirmation flag and step state. Prove the final message is exactly `已清空当前导入，可重新选择 Excel`, old wizard data cannot start, conflicts preserve all state, a captured-session mismatch preserves a newer session, late page/mutation results cannot repopulate either component, and repeated clicks coalesce or disable while cleanup runs. Add deterministic interleavings where import, row edit, page load, wizard precheck, navigation, and job creation each attempt to start after cleanup has entered the blocking store and are refused. Add a delayed-confirmation test: start a competing operation before confirmation returns Yes, then prove atomic lease acquisition refuses cleanup and preserves all UI/replay data.

Require exact conflict messages. `BatchConflict` must say the import is already associated with a task and instruct the user to wait for completion, then use “清空转存历史”. `LocalDependencyConflict` must say another local import depends on this data and advise reviewing/preserving the related import; it must not suggest history cleanup.

Also add post-confirmation service exception and cancellation tests: both preserve all captured UI and replay state, release every fence in `finally`, and report a safe status without exposing exception details.

- [ ] **Step 2: Run the three focused ViewModel test classes and verify RED**

Run:

```powershell
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --no-restore --filter "FullyQualifiedName~ImportViewModelTests|FullyQualifiedName~CreateJobWizardViewModelTests|FullyQualifiedName~TransferWorkspaceViewModelTests|FullyQualifiedName~CurrentImportCleanupInteractionTests"
```

Expected: tests fail because reset/fence methods, interaction, and `ClearCurrentImportCommand` are absent.

- [ ] **Step 3: Add component reset APIs**

Introduce an externally owned cleanup lease/fence that both child VMs reflect in `CanEdit`, `CanExecute`, import/edit/page commands, wizard navigation/precheck/create commands, and the workspace `StartAsync` guard. Expose an atomic `TryAcquire` that, at the acquisition instant after confirmation returns, rechecks captured session identity, `Import.IsBusy`, `Wizard.IsBusy`, workspace start state, all participants' editability, and absence of another cleanup; it fails without changing state unless every participant is idle/editable. Acquire it before the destructive call and release it in `finally`; the immediate SQLite transaction remains the cross-thread/process backstop. In `ImportViewModel`, add an expected-session reset that increments page and mutation generations and clears the exact fields listed in Step 1. In `CreateJobWizardViewModel`, add an expected-session detach that returns false for a newer context and clears the exact wizard creation/confirmation fields listed in Step 1.

- [ ] **Step 4: Implement the workspace-owned command**

Capture `Import.SessionId`, show the dedicated count-bearing Yes/No confirmation with default No, call the atomic shared `TryAcquire`, and refuse with a safe “当前正在处理其他操作，请稍后重试” status if the post-confirmation recheck fails. Then call the cleanup service and handle each disposition. Only for `Deleted`/`AlreadyAbsent` and a still-current captured session, reset Import, Wizard, and pending row state and refresh start/configuration commands. Discard the replay source only after that successful outcome, never during preparation or `finally`. Emit the exact distinct `BatchConflict` and `LocalDependencyConflict` guidance defined in Step 1; exceptions and cancellation are safe and preserve state. Always release an acquired lease in `finally`.

- [ ] **Step 5: Wire the production interaction with RED→GREEN bootstrap coverage**

First extend `AppBootstrapTests` to require `ICurrentImportCleanupInteraction` resolves to `WpfCurrentImportCleanupInteraction` and that `AppShellFactory` receives/injects it into the manually constructed `TransferWorkspaceViewModel`; run the `AppBootstrapTests` filter and observe RED. Then register the interaction singleton in `AppBootstrap.cs`, add it to `AppShellFactory`'s constructor, and pass it to the workspace. Do not silently default to auto-confirm; any test fallback must default to refusal.

- [ ] **Step 6: Run focused tests and verify GREEN**

Run the exact command from Step 2. Expected: all four classes pass with no unhandled dispatcher exceptions.

- [ ] **Step 7: Commit**

```powershell
git add src/BaiduBatchTransfer.App/Features/Imports/ImportViewModel.cs src/BaiduBatchTransfer.App/Features/Jobs/CreateJobWizardViewModel.cs src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs src/BaiduBatchTransfer.App/Features/Transfers/CurrentImportCleanupInteraction.cs src/BaiduBatchTransfer.App/AppBootstrap.cs tests/BaiduBatchTransfer.App.Tests/Features/ImportViewModelTests.cs tests/BaiduBatchTransfer.App.Tests/Features/CreateJobWizardViewModelTests.cs tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewModelTests.cs tests/BaiduBatchTransfer.App.Tests/Features/CurrentImportCleanupInteractionTests.cs tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs
git commit -m "feat: clear current import workspace state"
```

### Task 4: Add the user-facing button and verify the integrated feature

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceView.xaml`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewStructureTests.cs`
- Modify: `docs/testing/2026-09-14-local-license-history-branding-handoff.md`

- [ ] **Step 1: Write the failing XAML structure test**

Require a “清空当前导入” button next to the import controls, bound to `ClearCurrentImportCommand`, with `AutomationProperties.Name`, explanatory help text, and the existing danger action visual language.

- [ ] **Step 2: Run the structure test and verify RED**

Expected: failure because the button is absent.

- [ ] **Step 3: Add the toolbar button**

Use compact spacing consistent with “选择 Excel”. The help text must state “只清除本地待处理导入，不影响百度网盘文件和分享链接”.

- [ ] **Step 4: Run focused and regression verification**

Run:

```powershell
dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj --no-restore --filter FullyQualifiedName~LocalImportDraftCleanupServiceTests
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~SqliteLocalImportDraftCleanupStoreTests
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --no-restore --filter "FullyQualifiedName~ImportViewModelTests|FullyQualifiedName~CreateJobWizardViewModelTests|FullyQualifiedName~TransferWorkspaceViewModelTests|FullyQualifiedName~TransferWorkspaceViewStructureTests|FullyQualifiedName~LocalHistoryCleanup"
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --no-restore --filter FullyQualifiedName~Updates
dotnet build src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj --no-restore --configuration Debug
git diff --check
```

Expected: all selected tests pass; build succeeds with zero errors; diff check is clean.

- [ ] **Step 5: Update handoff evidence**

Record behavior, test counts, the local Debug executable path, and the explicit statement that no server or Baidu remote delete operation was performed.

- [ ] **Step 6: Commit**

```powershell
git add src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceView.xaml tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewStructureTests.cs docs/testing/2026-09-14-local-license-history-branding-handoff.md
git commit -m "feat: expose current import cleanup action"
```

### Task 5: Independent review and final handoff

- [ ] **Step 1: Request independent code review**

Review against `docs/superpowers/specs/2026-09-14-clear-current-import-design.md`, focusing on destructive boundaries, stale-session races, all schema relationships, and user-visible wording.

- [ ] **Step 2: Fix any findings with a failing regression test first**

For every accepted issue, add a RED test, apply the smallest fix, and rerun the relevant suite.

- [ ] **Step 3: Repeat final verification**

Run all commands from Task 4 Step 4 and record exact counts/output in the handoff document.

- [ ] **Step 4: Preserve the branch for local acceptance**

Confirm `git status --short --branch` is clean. Do not merge, push, deploy, or publish OTA without a separate user request.
