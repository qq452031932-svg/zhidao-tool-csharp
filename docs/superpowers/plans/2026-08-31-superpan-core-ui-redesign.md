# SuperPan-Style Core UI Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the current coarse WPF shell with a visually faithful, dense SuperPan-style desktop workbench for Transfer and Share while preserving existing business logic.

**Architecture:** Keep all existing commands and scheduling services. Add a focused theme dictionary, a custom-window chrome shell, presentation-only log buffers, and compact Transfer/Share views. Result-only display projections may join existing accepted/current records but must not change execution or acceptance rules.

**Tech Stack:** .NET 8, WPF/XAML, CommunityToolkit.Mvvm, xUnit, SQLite-backed existing repositories.

**Design specification:** `docs/superpowers/specs/2026-08-31-superpan-core-ui-redesign.md`

---

## File map

- Create `src/BaiduBatchTransfer.App/Styles/SuperPanTheme.xaml`: colors, typography, buttons, cards, navigation and DataGrid styles.
- Modify `src/BaiduBatchTransfer.App/App.xaml`: merge the theme dictionary and keep converters.
- Modify `src/BaiduBatchTransfer.App/MainWindow.xaml`: custom title bar, product header, three-column shell and removal of duplicate footer.
- Modify `src/BaiduBatchTransfer.App/MainWindow.xaml.cs`: title-bar drag/maximize/minimize/close behaviors and live-region continuity.
- Create `src/BaiduBatchTransfer.App/Features/Common/WorkspaceLogEntry.cs`: safe UI-only log row and bounded buffer.
- Modify `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceView.xaml`: dense toolbar, large table, fixed log area.
- Modify `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs`: presentation log projection and compact display helpers only.
- Modify `src/BaiduBatchTransfer.App/Features/Jobs/JobResultsContracts.cs`: carry the already-available target path into the UI result row.
- Modify `src/BaiduBatchTransfer.App/Features/Jobs/LocalJobResultsWorkflow.cs`: map `OperationPageItem.TargetPath` without changing result rules.
- Modify `src/BaiduBatchTransfer.App/Features/Jobs/JobResultsView.xaml`: display the Baidu target directory column.
- Modify `src/BaiduBatchTransfer.App/Features/Shares/ShareWorkspaceView.xaml`: dense toolbar, resource/result tabs, fixed log area.
- Modify `src/BaiduBatchTransfer.App/Features/Shares/ShareWorkspaceViewModel.cs`: share display rows, accepted/current result projection and log projection.
- Modify `src/BaiduBatchTransfer.App/AppBootstrap.cs`: inject the existing standalone result projection repository and local export read store.
- Modify focused tests under `tests/BaiduBatchTransfer.App.Tests`.

### Task 1: Theme tokens and custom window chrome

**Files:**
- Create: `src/BaiduBatchTransfer.App/Styles/SuperPanTheme.xaml`
- Modify: `src/BaiduBatchTransfer.App/App.xaml`
- Modify: `src/BaiduBatchTransfer.App/MainWindow.xaml`
- Modify: `src/BaiduBatchTransfer.App/MainWindow.xaml.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/MainWindowViewModelTests.cs`

- [ ] **Step 1: Add failing structural assertions**

Assert the main XAML contains `WindowStyle="None"`, `WindowChrome`, a 36px `CustomTitleBar`, `MinimizeWindowCommand`/click target, `MaximizeWindowCommand`/click target, `CloseWindowCommand`/click target, a 64px `ProductHeader`, columns `240`, `170`, `*`, and no old 36px global footer.

- [ ] **Step 2: Run the focused test and confirm failure**

Run:

```powershell
dotnet test tests\BaiduBatchTransfer.App.Tests\BaiduBatchTransfer.App.Tests.csproj --no-restore --filter "FullyQualifiedName~MainWindowViewModelTests" --verbosity minimal
```

Expected: FAIL on missing custom-chrome/theme structure.

- [ ] **Step 3: Implement the theme dictionary**

Define at minimum:

```xml
<Color x:Key="TitleBarColor">#08B7B7</Color>
<SolidColorBrush x:Key="PageBrush" Color="#F7F9FC" />
<SolidColorBrush x:Key="PanelBorderBrush" Color="#E3E8EF" />
<SolidColorBrush x:Key="NavigationSelectedBrush" Color="#EAF6FF" />
<SolidColorBrush x:Key="SuccessRowBrush" Color="#DFF7E8" />
```

Add 34px toolbar button, compact secondary button, panel border, navigation item and dense DataGrid styles. Merge this dictionary from `App.xaml`.

- [ ] **Step 4: Implement custom chrome**

Use `WindowChrome` with resize border and caption height 0. Add a 36px teal client title bar with drag/double-click support and accessible minimize/maximize/close buttons. Preserve the current live-region notifier.

- [ ] **Step 5: Run focused tests and build**

Expected: MainWindow tests pass and build has 0 errors.

### Task 2: Faithful account and navigation rails

**Files:**
- Modify: `src/BaiduBatchTransfer.App/MainWindow.xaml`
- Modify: `src/BaiduBatchTransfer.App/MainWindowViewModel.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/MainWindowViewModelTests.cs`

- [ ] **Step 1: Write failing shell-layout tests**

Assert:

- `AccountRail` is a white bordered panel, not a full-height gray block.
- `FeatureRail` is white and contains title `功能导航` and subtitle `选择一个工作模块`.
- Exactly the user-facing navigation labels `转存` and `分享` exist.
- Current-module badge binds `CurrentWorkspaceTitle`.
- Account cards bind display name, status, remaining/used capacity and use the selected-account card style.

- [ ] **Step 2: Verify red**

Run the focused `MainWindowViewModelTests`; expected failure on old dark navigation and missing header/badge.

- [ ] **Step 3: Rebuild the shell**

Use an outer 12px margin, 10px gaps, 240/170/* columns, bordered white rails, compact account summary, scrollable account cards and bottom account commands. Navigation buttons use the selected style driven by the current workspace.

- [ ] **Step 4: Verify green**

Run the focused tests and build.

### Task 3: Safe bounded workspace logs

**Files:**
- Create: `src/BaiduBatchTransfer.App/Features/Common/WorkspaceLogEntry.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/WorkspaceLogBufferTests.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Shares/ShareWorkspaceViewModel.cs`

- [ ] **Step 1: Write failing log-buffer tests**

Cover a 100-row limit, ascending order, consecutive duplicate coalescing, level mapping and redaction of extraction codes, cookies/tokens, browser profile paths, internal fingerprints and local paths. Also assert a full Baidu share URL is reduced to a masked summary and never appears verbatim.

- [ ] **Step 2: Verify red**

Run:

```powershell
dotnet test tests\BaiduBatchTransfer.App.Tests\BaiduBatchTransfer.App.Tests.csproj --no-restore --filter "FullyQualifiedName~WorkspaceLogBufferTests" --verbosity minimal
```

Expected: FAIL because the type does not exist.

- [ ] **Step 3: Implement minimal presentation-only log types**

Create:

```csharp
public enum WorkspaceLogLevel { Information, Success, Warning, Error }
public sealed record WorkspaceLogEntry(DateTimeOffset Timestamp, WorkspaceLogLevel Level, string Message);
public sealed class WorkspaceLogBuffer { /* ObservableCollection + AppendSafe */ }
```

Subscribe only to existing status-property changes in Transfer and Share view models. Dispose subscriptions with the owning view model. Do not publish business events or write to the database.

- [ ] **Step 4: Verify green and run privacy tests**

Run log tests plus existing visible-text/privacy-focused tests.

### Task 4: Dense Transfer workbench

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceView.xaml`
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewModelTests.cs`

- [ ] **Step 1: Replace old layout expectations with failing SuperPan structure assertions**

Assert the XAML has:

- `TransferToolbar`, `TransferDataRegion`, `TransferLogRegion`.
- A compact toolbar rather than a 245px form card.
- `待处理链接` and `处理结果` tabs.
- a large star-sized DataGrid with 34px rows.
- an always-visible 170px log region.
- no root `ScrollViewer` and no technical acceptance/adapter copy.

- [ ] **Step 2: Verify red**

Run `TransferWorkspaceViewModelTests`; expected failure on the current coarse card layout.

- [ ] **Step 3: Implement the compact toolbar**

Place import/paste, Excel, default target directory, account mode, auto-share, result directory and `开始转存` in one or two compact toolbar rows. Put quotas, Excel mapping and fixed extraction code in `高级设置`.

- [ ] **Step 4: Implement the dominant data table**

Pending tab uses import rows and result tab reuses the existing `ResultsHost`. Keep edit/paging controls in a thin footer. Apply dense table and status-row styles.

- [ ] **Step 5: Implement the log region**

Bind a virtualized `ListBox` to the safe `Logs` collection. Keep current status text as an accessible live region without creating a second footer.

- [ ] **Step 6: Carry the target directory into transfer results**

First add a failing `LocalJobResultsWorkflowTests` assertion that `OperationPageItem.TargetPath` becomes `JobResultRow.TargetPath`, and a failing XAML assertion for the `百度保存目录` column. Then add the optional `TargetPath` property to `JobResultRow`, map it in `LocalJobResultsWorkflow`, and bind it in `JobResultsView.xaml`. Do not alter accepted/current URL projection behavior.

- [ ] **Step 7: Verify green**

Run all Transfer workspace tests and build.

### Task 5: Dense Share workbench and result display

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Features/Shares/ShareWorkspaceView.xaml`
- Modify: `src/BaiduBatchTransfer.App/Features/Shares/ShareWorkspaceViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/ShareWorkspaceViewModelTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs`

- [ ] **Step 1: Add failing share-layout/result tests**

Assert toolbar/table/log structure, permanent-only label, pending/result tabs, compact history actions and absence of whole-page scrolling. Assert pending rows expose title, path, target account, existing-share policy and readiness. Assert result rows expose completion time and export status, and expose URL/code only when supplied by the accepted/current projection.

- [ ] **Step 2: Verify red**

Run `ShareWorkspaceViewModelTests`; expected failure on the existing long wizard page.

- [ ] **Step 3: Implement compact Share toolbar and tabs**

Toolbar contains account/resource picker, fixed/random extraction code, read-only `永久有效 · 不限次数`, result directory and `开始分享`. Move existing-share policy and draft recovery into `高级设置`.

Create a presentation-only pending row from `SelectionGroups` plus the currently selected existing-share policy. Its columns are resource title, parent path, account display name, policy display name and readiness text. Resource removal continues to invoke the existing command.

- [ ] **Step 4: Implement presentation result rows**

Inject the already-registered `IStandaloneShareResultProjectionRepository` into `ShareWorkspaceViewModel` through `AppBootstrap.cs` and update constructor fakes in tests. Load fail-closed accepted/current result pages for the selected batch by following `NextSequenceNumber` until null; never assume one page covers the batch. Correlate rows by operation id to the existing operation history, and use operation `UpdatedAt` as completion time. If the projection has no URL/code, leave those cells blank. Add a multi-page fake-repository test proving an operation after the first page receives its accepted/current result.

Inject the already-registered `ILocalResultExportStore` as a read dependency through `AppBootstrap.cs`. Read the exact existing standalone export key `LocalResultExportKey(StandaloneShareBatch, selectedBatchId, StandaloneShareWorkbook, generation: 1)` and map its status to a user-facing result-save status. Keep `ILocalResultExportCoordinator` for commands only. Add a test that captures and asserts this exact key. Add tests proving a missing, stale or non-current result never leaks URL/code and export-read failure produces a safe blank/unknown display rather than changing share state.

Preserve all pause/resume/retry/export commands in a compact result toolbar or `更多` menu.

- [ ] **Step 5: Bind the fixed log region and verify green**

Run Share tests, result privacy tests and build.

### Task 6: Full verification and rendered comparison

**Files:**
- Modify only if failures reveal a scoped regression.

- [ ] **Step 1: Run focused functional tests**

Run MainWindow, Transfer, Share, JobResults, LocalJobResultsWorkflow and WorkspaceLog tests.

- [ ] **Step 2: Build the full solution**

Run:

```powershell
dotnet build BaiduBatchTransfer.sln --no-restore --verbosity minimal
```

Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Run `git diff --check`**

Expected: no whitespace errors; CRLF conversion warnings are informational.

- [ ] **Step 4: Launch and compare at 1280×820, 100% scaling**

Verify title bar, product header, 240/170/rest columns, compact toolbar, dominant table, fixed 170px log, no clipping and no whole-page scroll. Compare side-by-side with the supplied reference image.

- [ ] **Step 5: Preserve known unrelated failures transparently**

Do not change frozen managed-browser code merely to make unrelated tests green. Report any known old failures separately from this UI slice.
