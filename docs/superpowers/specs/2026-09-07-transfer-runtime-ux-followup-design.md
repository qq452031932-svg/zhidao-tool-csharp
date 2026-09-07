# Transfer Runtime UX Follow-up Design

## Goal

Close four user-visible gaps in the direct-background transfer workflow:

1. Every newly created Baidu share is created with “分享链接自动填充提取码” enabled, so the generated URL itself carries the extraction code.
2. The run log follows the newest entry and the app gives one clear completion notification.
3. The import-facing “待处理链接” page no longer remains visually frozen after a batch starts.
4. The toolbar accepts and imports many pasted links at once.

## Scope and invariants

- Direct-background sharing remains permanent, access-unlimited, random-code, and regenerate-existing-share.
- Embedding the extraction code in the generated URL is mandatory and has no user toggle.
- The result workbook keeps both the generated share-link column and the extraction-code column. The link column must contain the distributable URL with `pwd`; the independent code column remains useful for searching and manual recovery.
- The share action still has one authoritative dispatch. Failure to prove the embed-code option before dispatch is a pre-dispatch failure; an uncertain post-click state follows the existing reconcile/retry rules.
- Existing imported rows remain immutable evidence. Runtime status is presentation state and must not rewrite original URLs, codes, or import decisions.

## Share creation and exported link

The managed browser share submission path gains a fixed `embed extraction code in link` invariant. Before the sole create-share click, the page bridge must select the matching Baidu option and prove it is selected together with permanent validity, unlimited access, and random/fixed code settings. The fixed helper protocol does not accept a caller-controlled selector or optional boolean.

The observed successful share result is normalized at the trusted boundary:

- A URL that already contains `pwd=<effective code>` is preserved.
- A successful result that provides a base share URL and a separately verified effective code is stored and exported with a query-aware `pwd=<code>` upsert. Existing query parameters and fragments are preserved; `pwd` is never duplicated.
- A URL containing a different `pwd` than the verified effective code is rejected as inconsistent.
- Query parameters are never duplicated and the extraction code is never logged.

This gives the recipient a directly usable link while preserving the independent extraction-code column.

## Live status, log following, and completion notification

`WorkspaceRunPanelViewModel` already refreshes the selected running batch. It will treat a transition from `Queued` or `Active` to a terminal status as a lifecycle event. A completed batch updates its status message and raises one completion notification for that batch during the current app session. Reopening an already completed historical batch must not show the notification again.

The transfer workspace consumes the lifecycle event through the existing dialog abstraction and shows one concise message containing completion and failure counts when available. The same message is appended to the run log.

The log list follows its observable collection at the view layer. When a new entry arrives, the view scrolls that entry into view on the UI dispatcher. Subscriptions are removed when the view unloads, and reopening the view does not create duplicate handlers.

## Pending-list presentation

Immediately after a batch is created:

- the workspace switches to `处理结果` explicitly rather than depending on an indirect property-change race;
- visible included import rows show `已进入任务`/`处理中` instead of remaining `就绪`;
- periodic result refresh updates the current page’s presentation state where exact candidate-sequence results are available;
- when the batch reaches a terminal status, unresolved presentation rows show `任务已结束，请查看结果`, avoiding a false per-row success claim.

For replication, multiple operations can correspond to one source row, so authoritative per-account success and failure remain on the result page. The pending page is a batch-stage summary, not a replacement for operation results.

When exact results are available, all operations for the same candidate sequence are aggregated deterministically:

- show `已完成` only when every required transfer and share for every participating account succeeded, including reuse of a valid existing share;
- show `有问题，请查看结果` if any operation failed, was cancelled, paused, or entered an uncertain/reconcile-required state, even when another account succeeded;
- otherwise show `处理中` while any operation is queued, reserved, preparing, submitting, or waiting for its linked share;
- if the batch is terminal but the current import page has no exact loaded operation details, show `任务已结束，请查看结果` rather than guessing success.

Tests cover one candidate sequence with success plus failure, success plus running, and success plus uncertain results so a partial outcome can never appear as full success.

## Batch paste input

The current text parser already supports multiple lines. The toolbar will expose that capability with a visible multiline text box, vertical scrolling, and a short hint that each line may contain a title, link, and extraction code. Pasting many lines followed by one click imports the entire text as one session. Existing single-link paste behavior remains valid.

The import button remains disabled only for blank text or an active import. No per-line button click is introduced.

## Error handling

- If the Baidu embed-code option cannot be found, selected, or proven, do not dispatch the share click; use the existing automatic retry, then mark failure.
- If a generated URL/code pair is inconsistent, do not export a misleading link; keep the operation failed/reconcilable according to the existing state machine.
- UI auto-scroll and completion notification failures must not affect transfer execution or result persistence.
- Batch-paste parse failures use the existing row-level import statistics and do not discard valid rows.

## Verification

- Helper tests prove the embed-code invariant is set before the sole click and that failure to prove it remains pre-dispatch.
- Application tests prove canonical share URLs contain the verified extraction code and replacement workbooks export both the full URL and the separate code.
- Run-panel tests prove terminal transition notification is emitted exactly once and not for already-terminal history.
- Transfer workspace tests prove explicit result-tab selection and pending-row stage updates.
- WPF structure/interaction tests prove multiline input and log auto-follow without duplicate subscriptions.
- Existing focused direct-background, result export, import, and Release packaging tests remain green.
