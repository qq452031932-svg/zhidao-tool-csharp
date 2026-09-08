# Batch Transfer Flat Workspace Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild the batch-transfer workspace as the approved flat two-level toolbar layout while preserving every existing transfer, account-mode, result, and log behavior.

**Architecture:** Keep the existing `TransferWorkspaceViewModel` and child result view intact. Restructure `TransferWorkspaceView.xaml` into a top configuration toolbar plus one remaining-height tab workspace, and use a small view-only helper in `TransferWorkspaceView.xaml.cs` to calculate a 2–8 visual-line paste-box height after text and width changes. Extend the existing selected-tab index to expose logs as the third main view and preserve `WorkspaceLogAutoScroll`, adding visibility-aware scrolling so logs collected while hidden are shown at the newest entry when opened.

**Tech Stack:** .NET 8, WPF XAML, CommunityToolkit.Mvvm, xUnit, STA WPF layout tests.

---

### Task 1: Lock the flat workspace contract with failing tests

**Files:**
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewModelTests.cs`
- Reference: `docs/superpowers/specs/2026-09-08-transfer-workspace-flat-layout-design.md`

- [ ] **Step 1: Replace the fixed-height input assertions with the approved adaptive contract**

Assert that `BulkPasteInput` has `MinLines="2"`, `MaxLines="8"`, wrapping and an internal automatic vertical scrollbar, and no explicit `Height`. Assert the standalone helper text is absent while the tooltip remains.

- [ ] **Step 2: Update the XAML structure test for the flat layout**

Assert root rows are `Auto` and `*`; `TransferToolbar` and `TransferDataRegion` remain; `TransferLogRegion` and `ToolbarActionWrap` no longer exist; the redundant automatic-share label is absent; the three tab headers are `待处理链接`, `处理结果`, and `运行日志`; and the log list/live region now live under `TransferDataRegion`.

- [ ] **Step 3: Update the STA layout tests**

At a 716×500 workspace viewport, assert the top toolbar stays within the viewport, the paste box has a usable two-line minimum, and the main data region/table retains enough height for a header plus data. Repeat at 740px height to prove the main region receives the extra height.

- [ ] **Step 4: Add a selected-view range test**

```csharp
[Fact]
public void SelectedDataTabIndexAcceptsThreeWorkspaceViewsAndRejectsOutOfRangeValues()
{
    using var fixture = Fixture.Create();
    fixture.ViewModel.SelectedDataTabIndex = 2;
    Assert.Equal(2, fixture.ViewModel.SelectedDataTabIndex);
    fixture.ViewModel.SelectedDataTabIndex = 3;
    Assert.Equal(2, fixture.ViewModel.SelectedDataTabIndex);
}
```

- [ ] **Step 5: Add failing STA tests for adaptive visual-line height**

Exercise the view-only height helper with empty/one-line, four-line, eight-line, and more-than-eight-line text. Arrange the same long wrapped text at a wide width and then a narrow width, pump the dispatcher, and assert that height increases but never exceeds the eight-line cap. Unload the owner and confirm later text/size changes no longer schedule layout work.

- [ ] **Step 6: Run the focused tests and verify RED**

Run:
`dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --no-restore --filter "FullyQualifiedName~TransferWorkspaceViewModelTests"`

Expected: failures identify the old three-row layout, explicit 76px input height, missing height helper/log tab, and rejected index 2.

### Task 2: Implement the top compact toolbar and adaptive input

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceView.xaml`
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceView.xaml.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewModelTests.cs`

- [ ] **Step 1: Change the root grid to two rows**

Use `Auto` for the top configuration area and `*` for the main workspace. Remove the bounded bottom log row.

- [ ] **Step 2: Replace the nested configuration grids with a compact wrapping toolbar**

Keep the current bindings and named controls for import, Excel, target directory, concurrency, result directory, directory picker, and start command. Use a wrapping toolbar container so narrow supported widths flow to a second line rather than clipping.

- [ ] **Step 3: Give the paste box 2–8 line limits**

Remove the explicit height and set:

```xml
MinLines="2"
MaxLines="8"
AcceptsReturn="True"
TextWrapping="Wrap"
VerticalScrollBarVisibility="Auto"
```

Keep the existing binding, tooltip, automation name, and top vertical content alignment. Do not clear or collapse the input after import.

- [ ] **Step 4: Implement the view-only adaptive height helper**

Create an internal helper beside `TransferWorkspaceView` that subscribes to owner load/unload plus paste-box text and size changes. Defer measurement until layout is ready, determine the actual visual line count (including wrapped lines), calculate line height from the current font and control chrome, clamp to 2–8 lines, and avoid writing unchanged heights. Abort pending dispatcher work and detach handlers on unload or dispatcher shutdown. Construct it in `TransferWorkspaceView` after `InitializeComponent`.

- [ ] **Step 5: Remove only the two confirmed redundant labels**

Remove the visible link-input helper and `转存后自动生成分享链接`. Do not alter the automatic share setting or workflow.

- [ ] **Step 6: Run the adaptive-input and layout tests**

Run the focused class test command from Task 1. Expected: adaptive-input and top-layout assertions pass; remaining failures concern the not-yet-migrated main workspace/log tab.

### Task 3: Build the single main workspace with three full-height views

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceView.xaml`
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewModelTests.cs`

- [ ] **Step 1: Create one compact tab toolbar in `TransferDataRegion`**

Use a custom local `TabControl` template whose top row contains resource statistics, the three tab headers, and the existing account-mode radio buttons. Style selected tab headers with the existing light brand background and brand foreground.

- [ ] **Step 2: Preserve the pending and result views unchanged inside the new structure**

Move, rather than rewrite, the pending DataGrid/edit footer and `ResultsHost`. Preserve all bindings, virtualization settings, commands, control names, and row heights.

- [ ] **Step 3: Move logs into a third tab**

Add `TabItem Header="运行日志"` containing the existing `TransferLogsList` and `TransferStatusLiveRegion`. Keep the Chinese level text, local timestamps, 100-item source, virtualization, and accessibility bindings.

- [ ] **Step 4: Extend the view index range**

Change `SelectedDataTabIndex` validation from `0..1` to `0..2`. Leave imports selecting 0 and active/result batches selecting 1.

- [ ] **Step 5: Run the focused tests and verify GREEN**

Run the focused class test command. Expected: all `TransferWorkspaceViewModelTests` pass.

- [ ] **Step 6: Commit the layout unit**

Stage only the two production files and the focused test file, then commit with `feat: flatten transfer workspace layout`.

### Task 4: Keep hidden-log updates anchored to the newest entry

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceView.xaml.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewModelTests.cs`

- [ ] **Step 1: Add a failing STA test for reopening the log view**

Construct the existing `WorkspaceLogAutoScroll`, add log items while its list is not visible, make the list visible, pump the dispatcher, and assert the latest item receives `RequestBringIntoView`.

- [ ] **Step 2: Run the new test and verify RED**

Run only the new test with `dotnet test ... --filter "FullyQualifiedName~LogAutoScrollMovesToNewestEntryWhenLogViewBecomesVisible"`. Expected: no scroll request is made on visibility change.

- [ ] **Step 3: Add visibility-aware scheduling to `WorkspaceLogAutoScroll`**

Subscribe once to `IsVisibleChanged` while loaded, reuse the existing deferred scroll method, and unsubscribe on unload. Schedule only when becoming visible and items exist; keep dispatcher-shutdown safeguards.

- [ ] **Step 4: Run all log-scroll tests and verify GREEN**

Run `dotnet test ... --filter "FullyQualifiedName~TransferWorkspaceViewModelTests&Name~LogAutoScroll"` (or the test runner’s supported equivalent filters). Expected: existing append/unload tests and the new visibility test all pass.

- [ ] **Step 5: Commit the log-view behavior unit**

Stage only `TransferWorkspaceView.xaml.cs` and the focused test file, then commit with `fix: keep transfer logs anchored when reopened`.

### Task 5: Verify the complete page and inspect the real UI

**Files:**
- Verify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceView.xaml`
- Verify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceView.xaml.cs`
- Verify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs`
- Verify: `tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewModelTests.cs`

- [ ] **Step 1: Run the entire app test project**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --no-restore`

Expected: zero failures.

- [ ] **Step 2: Build the desktop app**

Run: `dotnet build src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj --no-restore`

Expected: build succeeds with zero errors.

- [ ] **Step 3: Inspect the diff**

Run `git diff --check` and a path-limited `git diff` for the four files. Confirm no unrelated dirty files were staged or modified by this work.

- [ ] **Step 4: Launch the actual UI**

Start the built desktop application, open “批量转存”, and visually confirm: compact top toolbar, 2–8 line adaptive input, one lower toolbar, full-height pending/results/log views, readable logs, and no clipping at the current window size.

- [ ] **Step 5: Report evidence and hand the running UI to the user**

Provide the exact tests/build results and leave the UI open for user review. Do not claim completion if any test, build, or visual check is outstanding.
