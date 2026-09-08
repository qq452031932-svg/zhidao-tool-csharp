# Transfer Target Picker and Share Toolbar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the approved transfer toolbar, in-app Baidu target-directory picker, and compact share-analytics toolbar without regressing existing transfer and analytics behavior.

**Architecture:** Keep remote directory reads behind a small transfer-feature interface that loads the selected account session and calls the existing read-only `IBaiduNetdiskAdapter.ListDirectoriesAsync`. Present those results through a focused picker view model/window, while `TransferWorkspaceViewModel` owns applying the chosen path to participating accounts. Keep analytics search semantics in the existing view model and add the 300 ms debounce plus Enter flush at the WPF binding boundary.

**Tech Stack:** .NET 8, WPF/XAML, CommunityToolkit.Mvvm, xUnit

---

### Task 1: Fix compact concurrency selection and preserve the paste input

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Styles/SuperPanTheme.xaml`
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceView.xaml`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewModelTests.cs`

- [ ] Add failing structure/layout tests proving the ComboBox content and arrow occupy separate template columns, the transfer concurrency control displays its selected value at compact width, and `BulkPasteInput` remains present with 2–8 line bounds.
- [ ] Run the focused tests and confirm they fail for the overlapping global template.
- [ ] Change the ComboBox template to a two-column layout and give the transfer concurrency control enough compact width.
- [ ] Run the focused tests and confirm they pass.

### Task 2: Add the read-only Baidu target-directory picker

**Files:**
- Create: `src/BaiduBatchTransfer.App/Features/Transfers/TransferTargetDirectoryContracts.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Transfers/BaiduTransferTargetDirectorySource.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Transfers/TransferTargetDirectoryPickerViewModel.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Transfers/TransferTargetDirectoryWindow.xaml`
- Create: `src/BaiduBatchTransfer.App/Features/Transfers/TransferTargetDirectoryWindow.xaml.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Transfers/WpfTransferTargetDirectoryPicker.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceView.xaml`
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/TransferTargetDirectoryPickerViewModelTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewModelTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs`

- [ ] Add failing tests for root loading, child navigation, parent navigation, read failure, missing session, cancellation, sorted folder display, stale-response suppression, loading-time command disabling, and root parent-navigation disabling. Assert that `IBaiduSessionStore.LoadAsync` receives `accountId.ToString("D")`.
- [ ] Run those tests and confirm the picker contracts/view model are missing.
- [ ] Implement the session-backed read-only directory source and picker view model.
- [ ] Run the picker tests and confirm they pass.
- [ ] Add failing transfer-workspace tests proving the target is a picker button, cancel preserves the path, and a confirmed path updates `Wizard.DefaultTargetPath` plus every selected account while leaving unselected divergent paths unchanged. Cover single- and multi-account modes.
- [ ] Run the tests and confirm the command/UI are missing.
- [ ] Implement the WPF picker window, command wiring, path application, dependency registration, stale-response fencing, and cancellation of outstanding reads when the window closes.
- [ ] Run the transfer and bootstrap focused tests and confirm they pass.

### Task 3: Flatten the share-data header and debounce local title filtering

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsView.xaml`
- Modify: `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsView.xaml.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewStructureTests.cs`

- [ ] Add failing structure tests requiring one named two-row toolbar, no duplicate page title, a 300 ms delayed `SearchText` binding, and an Enter key handler. Add an STA behavior test proving delayed filtering, Enter flush, and clear-to-all behavior.
- [ ] Run the structure tests and confirm they fail against the current stacked-card layout.
- [ ] Replace the two cards with the approved compact toolbar and wire Enter to force the binding source update.
- [ ] Run analytics structure and view-model tests and confirm they pass.

### Task 4: Integrated verification

**Files:**
- Modify only if verification exposes a regression in the files above.

- [ ] Run `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --no-restore --filter "FullyQualifiedName~TransferWorkspaceViewModelTests|FullyQualifiedName~TransferTargetDirectoryPickerViewModelTests|FullyQualifiedName~ShareAnalyticsViewStructureTests|FullyQualifiedName~ShareAnalyticsViewModelTests|FullyQualifiedName~AppBootstrapTests"`.
- [ ] Run `dotnet build src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj --no-restore`.
- [ ] Run `git diff --check`.
- [ ] Launch the rebuilt app and visually verify the transfer paste area, target picker, concurrency value, share toolbar, table height, and horizontal scrolling.
