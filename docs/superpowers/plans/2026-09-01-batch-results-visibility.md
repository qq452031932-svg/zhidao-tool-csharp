# Batch Results Visibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:test-driven-development and execute inline; the user explicitly requested no subagents.

**Goal:** Automatically present completed transfer/share results and provide one-click copy plus existing workbook export.

**Architecture:** Reuse the existing result projection and history source. Add a cancellable refresh loop to the run panel, bind the transfer tab selection to the workspace ViewModel, and inject a WPF clipboard adapter into the result ViewModel.

**Tech Stack:** .NET 8, WPF, CommunityToolkit.Mvvm, xUnit

---

### Task 1: Automatically show and refresh results

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceView.xaml`
- Modify: `src/BaiduBatchTransfer.App/Features/Common/WorkspaceRunPanelViewModel.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewModelTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/WorkspaceRunPanelViewModelTests.cs`

- [ ] Write failing tests for result-tab selection and active-batch refresh.
- [ ] Run the focused tests and verify the expected failures.
- [ ] Implement tab selection and the cancellable refresh loop.
- [ ] Run the focused tests and verify they pass.

### Task 2: Copy generated link and extraction code

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Services/DialogService.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Jobs/JobResultsViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Jobs/JobResultsView.xaml`
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/JobResultsViewModelTests.cs`

- [ ] Write failing tests for command availability, copied text, and safe failure status.
- [ ] Run the focused tests and verify the expected failures.
- [ ] Add the clipboard interface, WPF implementation, command, and button.
- [ ] Run the focused tests and verify they pass.

### Task 3: Verify the integrated result workflow

- [ ] Run the focused transfer/result/run-panel test groups.
- [ ] Build `BaiduBatchTransfer.sln` with no restore.
- [ ] Run `git diff --check`.
- [ ] Start the application and confirm the latest real batch appears in “处理结果”.
