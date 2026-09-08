# Transfer Progress and Directory Creation Implementation Plan

> **For agentic workers:** Execute this plan task-by-task with tests first. Do not reset, clean, or modify unrelated worktree changes.

**Goal:** Replace noisy periodic refresh logs with deterministic changed-only transfer progress, and let users create and enter a Baidu Netdisk folder from the target-directory picker without auto-confirming it.

**Architecture:** `JobResultsViewModel` owns lifecycle progress snapshot state and publishes only meaningful status changes; `WorkspaceRunPanelViewModel` keeps the existing five-second polling and terminal notification order. Directory writes go through a new session-backed creator beside the existing read source, while `TransferTargetDirectoryPickerViewModel` owns validation-independent create state, cancellation, stale-result fencing, and post-create navigation; WPF owns only the name dialog and event wiring.

**Tech Stack:** .NET 8, C#, WPF, CommunityToolkit.Mvvm, xUnit, Microsoft.Extensions.DependencyInjection.

---

## Task 1: Deterministic changed-only transfer progress

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Features/Jobs/JobResultsViewModel.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/JobResultsViewModelTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/WorkspaceRunPanelViewModelTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewModelTests.cs`

1. Add focused tests proving the first successful lifecycle snapshot emits `进度 0/25：成功 0，失败 0，剩余 25。`, a changed snapshot emits the new counts, and an unchanged automatic or manual refresh does not raise another `StatusMessage` change.
2. Run the focused tests and confirm they fail because the current implementation emits `正在刷新任务结果……` and a repeated total.
3. Add a private progress snapshot value, remembered batch id, and publishing helper to `JobResultsViewModel`. Reset the snapshot when a different batch is initialized and calculate processed as success plus terminal problems, with remaining clamped at zero.
4. Add tests for one safe log per consecutive failure episode and a forced recovery progress emission on the first later successful refresh even when counts did not change.
5. Run those tests red, then add the minimal failure-episode/recovery state. Keep manual refresh busy state but do not publish start noise.
6. Add tests for switching batches resetting the first-snapshot rule, a stale/out-of-order refresh never overwriting the active batch, and progress/status text never containing row titles, URLs, extraction codes, account ids, or paths.
7. Add/adjust run-panel and workspace tests proving the final result snapshot is applied before `BatchTerminal`, polling stops after terminal state, and no later progress status can be appended after the terminal notification.
8. Run all job-results, run-panel, and workspace tests green.

## Task 2: Session-backed remote folder creation

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferTargetDirectoryContracts.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Transfers/BaiduTransferTargetDirectoryCreator.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/TransferTargetDirectoryPickerViewModelTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs`

1. Add tests for an `ITransferTargetDirectoryCreator` contract and implementation: valid canonical direct-child creation, parent/traversal escape rejection, existing-folder success, account-session mismatch fail-closed with zero adapter writes, invalid names (`empty`, `.`, `..`, separators, controls, over 255 Unicode scalar values), API error categorization, non-directory collision behavior, cancellation before a write, cancellation after the remote commit, and safe failure messages.
2. Run the focused tests red because the creator does not exist.
3. Implement the result contract and `BaiduTransferTargetDirectoryCreator`. Canonicalize the parent with `RemotePath`, validate one trimmed path segment, prove the joined path is exactly one canonical descendant, reload and verify the exact captured account session immediately before `EnsureDirectoryAsync`, and return only stable Chinese errors. Map adapter `InvalidTargetPath` to `无法创建文件夹，请更换名称。` (this covers the observable same-name-file collision without exposing server diagnostics); map expired/mismatched sessions and transient/unknown service failures to their distinct stable safe categories.
4. Register the creator in `AppBootstrap` and make the DI test green.

## Task 3: Picker create-folder state, cancellation, and fencing

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferTargetDirectoryPickerViewModel.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/TransferTargetDirectoryPickerViewModelTests.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewModelTests.cs`

1. Add tests for create success entering the child without completing the picker, same-name directory behavior, create failure preserving current path and prior selection, create-success/open-failure messaging, busy command disabling, close cancellation, cancellation after a committed write, and ignoring stale responses after account/path/navigation/lifetime changes.
2. Run the focused tests red.
3. Inject the creator, add create cancellation and an operation generation fence, expose `IsCreating`, `CanCreateFolder`, and `CreateFolderAsync(name)`, and share the busy state with navigation/select/create commands.
4. On successful ensure, navigate to the child. If opening fails, restore/stay on the captured parent and show `文件夹已创建，但暂时无法打开，请刷新后重试。`. Never complete the picker automatically.
5. Add workspace tests proving the modal captures participant ids plus the browsing account at open time, zero selection performs no picker read/write, and confirmation updates every captured participant that is still present while leaving newly added or removed accounts untouched. A captured participant deselected while the modal is open still receives the confirmed path, because selection is snapshotted at open time. Update `PickTargetDirectoryAsync` to re-resolve only the captured ids against the current account collection before applying the confirmed path.
6. Run all picker and workspace tests green.

## Task 4: WPF name dialog and picker wiring

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/WpfTransferTargetDirectoryPicker.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferTargetDirectoryWindow.xaml`
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferTargetDirectoryWindow.xaml.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Transfers/TransferFolderNameWindow.xaml`
- Create: `src/BaiduBatchTransfer.App/Features/Transfers/TransferFolderNameWindow.xaml.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/TransferTargetDirectoryPickerViewModelTests.cs`

1. Add structural tests asserting the picker exposes a Chinese `新建文件夹` action and the owned dialog contains a name field plus confirm/cancel controls.
2. Run the structural tests red.
3. Inject the creator through `WpfTransferTargetDirectoryPicker`, add the button to the existing top toolbar, and open a small owner-bound dialog. Cancel/close performs no write; confirm forwards the raw name to the view model, where authoritative validation occurs.
4. Disable navigation, selection, and repeat creation while create is busy, while keeping progress/error feedback visible.
5. Run the WPF/picker tests green.

## Task 5: Integrated verification and distributable package

**Files:**
- Verify all affected source/test projects
- Output: `.artifacts/distribution/SuperPan-1.0.12-progress-folder-create-win-x64.zip`

1. Run focused tests for job results, run panel, picker, workspace, and bootstrap.
2. Run the full test suite, `dotnet build` in Release, and `git diff --check`; require zero failures, warnings, and whitespace errors.
3. Launch the real application and manually verify: the log advances only when counts change, terminal completion remains final, `新建文件夹` opens an owned dialog, successful creation enters the folder, and the user must still click `选择此目录`.
4. Publish self-contained Win-x64 output and create the versioned ZIP without account sessions, cookies, databases, logs, test artifacts, or user files.
5. Verify archive contents and SHA-256, then open the distribution directory for the user.
