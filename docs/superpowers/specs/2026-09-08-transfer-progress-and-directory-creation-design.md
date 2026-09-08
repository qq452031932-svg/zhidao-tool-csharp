# Transfer Progress Logging and Directory Creation Design

## Goal

Finish the current transfer workflow with two focused improvements:

1. Replace repetitive automatic-refresh log noise with useful transfer progress.
2. Let the target-directory picker create a folder before the user confirms the destination.

## Progress logging

- The existing five-second lifecycle refresh remains unchanged so completion detection stays timely.
- Automatic refresh must not append `正在刷新任务结果……` or an unchanged total-count message on every poll.
- After a successful refresh, append a single progress line only when the progress snapshot changed:
  `进度 8/25：成功 6，失败 2，剩余 17。`
- The snapshot uses the existing result summary:
  - total = operation count;
  - success = complete-success count;
  - failed = terminal-problem count;
  - displayed progress numerator = `success + failed`;
  - remaining = `max(0, total - success - failed)`.
- The first successful snapshot for every newly presented batch is always emitted, including `0/N`.
- The result view model owns the last-emitted snapshot and its batch ID. It resets that comparison state when a different batch is initialized. The workspace remains the single owner that turns the resulting status change into a log entry.
- Repeating the same snapshot produces no new log entry, including an explicit manual refresh with unchanged counts. Manual refresh still exposes its existing busy state while it runs.
- A failed refresh preserves the last successful snapshot and emits at most one safe failure status until the status changes. A later successful refresh may re-emit the current progress once to show recovery, even when its counts did not change.
- Result refreshes remain single-flight through the existing busy gate and use the existing batch/generation fence. An older result may not overwrite or log after a newer batch/result.
- The existing terminal completion notification remains the final summary.
- The final successful result refresh is applied before that terminal notification. Once the terminal notification is published, automatic polling stops and no later progress line may be appended for that batch.
- Progress logs contain counts only; they must not include share titles, URLs, extraction codes, account identifiers, or remote paths.
- Manual refresh keeps visible feedback but follows the same count format after the refresh succeeds.

## Creating a directory

- The target-directory picker adds a `新建文件夹` action.
- Clicking it opens a small owned name-entry dialog; creation is not started until the user confirms that dialog.
- The name is one folder segment: trim surrounding whitespace, reject empty names, `.` / `..`, path separators, control characters, and values over 255 Unicode scalar values. Server-side byte/character limits remain authoritative and map to a safe validation failure.
- Creation uses the same selected account session as directory browsing and calls the existing `IBaiduNetdiskAdapter.EnsureDirectoryAsync` API.
- When the name dialog is confirmed, the picker captures its immutable browsing account ID, current canonical parent path, and navigation generation. The creator reloads and verifies that exact account session immediately before any remote write.
- The child path is built as one canonical direct descendant through `RemotePath`; raw concatenation, traversal, and escape from the captured parent are rejected before the adapter call.
- While creating, navigation, selection, and another create action are disabled.
- On success, the picker navigates into the new folder but does not close or confirm the target. The user can create another level or click `选择此目录`.
- An already-existing directory is a successful ensure and is entered. A same-name non-directory collision is a safe `无法创建文件夹，请更换名称。` failure.
- If ensure succeeds but the subsequent directory read cannot open the child, the picker stays at the parent and reports `文件夹已创建，但暂时无法打开，请刷新后重试。`; it must not report that creation failed.
- Cancelling before confirmation or closing the name dialog performs zero writes. Cancellation or failure keeps the current picker path and previously configured target path unchanged.
- Remote creation is best-effort cancellable and is not rolled back: cancellation may arrive after Baidu has committed the folder. In that case the stale completion is ignored, the picker/target state remains unchanged, and a later refresh can discover the folder.
- Closing the picker cancels any in-flight create request. A response is applied only when its captured account, parent path, navigation generation, and picker lifetime are still current.
- Errors use stable safe categories: invalid name, login expired/account mismatch, name collision, network/remote failure, and created-but-not-opened. Adapter diagnostics are never displayed or logged.

## Multi-account behavior

- As today, the first selected account is the browsing account.
- Opening the picker with zero selected accounts is a no-op with `请先选择参与账号。` and performs no read or write.
- The participant account IDs and first browsing account are captured when the modal picker opens. The main window is blocked while it is open; confirmation applies only to that captured participant set that is still present.
- A newly created folder is created immediately only in that browsing account.
- After the user confirms the path, it is applied only to selected accounts.
- Other selected accounts create a missing destination later through the existing transfer-directory preparation flow. Unselected account paths remain untouched.

## Architecture

- Keep directory reads and writes behind transfer-feature interfaces.
- Add a session-backed directory creator beside `BaiduTransferTargetDirectorySource`.
- The picker view model owns input state, busy state, cancellation, generation fencing, safe errors, and post-create navigation.
- The WPF picker owns only the name dialog and view event wiring.
- `JobResultsViewModel` owns progress formatting, per-batch snapshot comparison, refresh serialization/generation checks, and status publication. `WorkspaceRunPanelViewModel` owns terminal detection/order, and `TransferWorkspaceViewModel` remains the only presentation-log appender.

## Verification

- TDD tests cover the first snapshot, per-batch reset, changed-only progress messages, manual unchanged refresh, unchanged-poll suppression, count calculation, out-of-order refresh suppression, recovery after failure, terminal-final ordering, no progress after terminal, and privacy redaction.
- Creator tests cover exact account session matching, canonical contained child paths, valid creation, existing-directory success, non-directory collision, invalid names, API failure, cancellation, cancellation after remote commit, and zero-write fail-closed behavior.
- Picker tests cover create busy-state disabling, browsing-account-only creation, success navigation, ensure-success/navigation-read-failure, cancellation, account/path-generation stale completion suppression, close cancellation, zero-selected behavior, captured selected/unselected target propagation, and path preservation.
- WPF tests cover the visible `新建文件夹` action, owner assignment, name-dialog confirm/cancel behavior, and zero writes before confirmation.
- Run the focused App tests, Release build, package integrity checks, and a desktop smoke launch before producing the next test ZIP.
