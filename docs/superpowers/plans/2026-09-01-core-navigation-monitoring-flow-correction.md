# Core Navigation And Monitoring Flow Correction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Correct the shell to match the reference module structure, add a usable link-monitoring workbench, and make auto-share an integrated batch-transfer outcome rather than a primary navigation module.

**Architecture:** Keep all existing transfer/share services intact. Add focused monitoring and placeholder workspaces at the presentation layer, route them through `MainWindowViewModel`, and reuse `TransferWorkspaceViewModel` for the core transfer → optional share → export path. Apply one reusable WPF control theme so buttons and inputs share the reference visual language.

**Tech Stack:** .NET 8, WPF, CommunityToolkit.Mvvm, xUnit, XDocument structural tests.

---

### Task 1: Correct shell navigation and visual controls

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Styles/SuperPanTheme.xaml`
- Modify: `src/BaiduBatchTransfer.App/MainWindow.xaml`
- Modify: `src/BaiduBatchTransfer.App/MainWindowViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/MainWindowViewModelTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs`

- [ ] Write failing tests for seven navigation labels/order, default link monitoring, absence of primary Share command, and flat rounded button templates.
- [ ] Run focused tests and verify the expected failures.
- [ ] Implement workspace routing and visual styles with the existing share workspace preserved off-navigation.
- [ ] Run focused tests and verify green.

### Task 2: Add link-monitoring workbench

**Files:**
- Create: `src/BaiduBatchTransfer.App/Features/Monitoring/LinkMonitoringViewModel.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Monitoring/LinkMonitoringContracts.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Monitoring/LocalLinkMonitoringStore.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Monitoring/LinkMonitoringView.xaml`
- Create: `src/BaiduBatchTransfer.App/Features/Monitoring/LinkMonitoringView.xaml.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/LinkMonitoringViewModelTests.cs`

- [ ] Write failing tests for add/import/delete, normalized-ID deduplication, transfer-draft handoff, start/pause/resume/stop/close lifecycle, persisted interval/filter configuration, bounded redacted logs, table columns, and fixed log region.
- [ ] Run tests and verify the expected failures.
- [ ] Implement a single cancellable `TimeProvider`-driven loop over `ILinkMonitoringProbe`, a durable local store, safe bounded `WorkspaceLogEntry` logs, and an explicit handoff interface to the existing transfer import draft.
- [ ] Run focused tests and verify green.

### Task 3: Add honest deferred-module pages

**Files:**
- Create: `src/BaiduBatchTransfer.App/Features/Common/DeferredWorkspaceViewModel.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Common/DeferredWorkspaceView.xaml`
- Create: `src/BaiduBatchTransfer.App/Features/Common/DeferredWorkspaceView.xaml.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/DeferredWorkspaceViewModelTests.cs`

- [ ] Write failing tests for module title and explicit construction status.
- [ ] Run tests and verify the expected failures.
- [ ] Implement one reusable deferred workspace for Home, Ad insertion, File exclusion, Statistics, and Settings.
- [ ] Run focused tests and verify green.

### Task 4: Make transfer page express the complete core outcome

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceView.xaml`
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Jobs/JobResultsContracts.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Jobs/JobResultsView.xaml`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewModelTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/JobResultsViewModelTests.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/OperationExecutorTests.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/ShareOperationExecutorTests.cs`

- [ ] Write failing tests for user-facing “转存完成后自动分享”, result directory, new share URL/code columns, and result export action.
- [ ] Run focused tests and verify the expected failures.
- [ ] Implement only missing labels/bindings while preserving existing backend behavior.
- [ ] Add regression tests for same-object transfer/share correlation, share failure, old-batch isolation, and export failure; export only confirmed current-batch URL/code pairs.
- [ ] Run focused tests and verify green.

### Task 5: Verify real layout and regression safety

**Files:**
- Test: `tests/BaiduBatchTransfer.App.Tests/MainWindowViewModelTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/LinkMonitoringViewModelTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewModelTests.cs`

- [ ] Build the solution with zero warnings/errors.
- [ ] Run focused App tests.
- [ ] Launch the actual WPF executable and capture Link Monitoring and Batch Transfer screenshots.
- [ ] Compare button shapes, navigation hierarchy, table dominance, and log region against the reference image; fix only confirmed mismatches.
