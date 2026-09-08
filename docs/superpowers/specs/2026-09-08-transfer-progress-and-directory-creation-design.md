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
  - remaining = `max(0, total - success - failed)`.
- Repeating the same snapshot produces no new log entry.
- The existing terminal completion notification remains the final summary.
- Progress logs contain counts only; they must not include share titles, URLs, extraction codes, account identifiers, or remote paths.
- Manual refresh keeps visible feedback but follows the same count format after the refresh succeeds.

## Creating a directory

- The target-directory picker adds a `新建文件夹` action.
- Clicking it opens a small owned name-entry dialog; creation is not started until the user confirms that dialog.
- The name is one folder segment: trim surrounding whitespace, reject empty names, `.` / `..`, path separators, control characters, and names over 255 characters.
- Creation uses the same selected account session as directory browsing and calls the existing `IBaiduNetdiskAdapter.EnsureDirectoryAsync` API.
- The creator verifies that the loaded session belongs to the requested account before any remote write.
- While creating, navigation, selection, and another create action are disabled.
- On success, the picker navigates into the new folder but does not close or confirm the target. The user can create another level or click `选择此目录`.
- On cancellation or failure, the current directory and previously configured target path remain unchanged. Errors shown to the user are concise Chinese messages and never expose adapter diagnostics.
- Closing the picker cancels any in-flight create request, and stale create/read responses cannot replace newer navigation state.

## Multi-account behavior

- As today, the first selected account is the browsing account.
- A newly created folder is created immediately only in that browsing account.
- After the user confirms the path, it is applied only to selected accounts.
- Other selected accounts create a missing destination later through the existing transfer-directory preparation flow. Unselected account paths remain untouched.

## Architecture

- Keep directory reads and writes behind transfer-feature interfaces.
- Add a session-backed directory creator beside `BaiduTransferTargetDirectorySource`.
- The picker view model owns input state, busy state, cancellation, generation fencing, safe errors, and post-create navigation.
- The WPF picker owns only the name dialog and view event wiring.
- Progress snapshot comparison belongs in the run-panel/result lifecycle layer; the transfer workspace continues to translate status changes into presentation logs.

## Verification

- TDD tests cover changed-only progress messages, unchanged-poll suppression, count calculation, terminal behavior, and privacy redaction.
- Creator tests cover exact account session matching, valid creation, invalid names, API failure, cancellation, and zero-write fail-closed behavior.
- Picker tests cover create busy-state disabling, success navigation, cancellation, stale completion suppression, close cancellation, and path preservation.
- WPF structure tests cover the visible `新建文件夹` action and owned name-entry dialog.
- Run the focused App tests, Release build, package integrity checks, and a desktop smoke launch before producing the next test ZIP.
