> **⚠️ 本文件已作废，仅作历史归档。不要据此工作。**
>
> 作废原因：其中的分支名、HEAD、"关键文件"清单和"下一窗口"计划全部过时
> （它描述的 HEAD 是 `eb3a35f`，实际功能分支已推进到 `5494d16a`，相差数百个提交）。
> 它所列的关键文件在主工作树中并不存在。
>
> 当前唯一有效交接文件：`docs/HANDOFF.md`

# Execution Log

## 2026-08-21 Repository Check

### Scope

- Workspace: `C:\Users\45203\Documents\dakazhandian`
- Active branch: `master`
- Baseline commit: `6e84ba8 docs: clarify share result headers`
- Feature history located on `codex/transfer-share-link-replacement`, ending at `d588758 perf: stream exact set reconciliation queries`.
- No prior standalone execution-log or history file was present in the current branch.
- The feature worktree is dirty: 13 tracked files are modified and 3 files are untracked. Full-suite results below therefore do not represent the clean `d588758` commit.

### History Evidence

The local Git history is available and was inspected. The feature branch contains the transfer/share pipeline commits from `0a8ce96` through `d588758`, including durable share state, exact transfer evidence, Excel mapping, idempotent sharing, and streamed reconciliation queries. No Codex app session inspection was needed.

### Checks Executed

| Check | Result | Evidence |
| --- | --- | --- |
| .NET SDK | PASS | `dotnet --version` = `8.0.424` |
| Restore | PASS | `dotnet restore` completed for solution projects |
| Release build on `master` | PASS | 0 warnings, 0 errors |
| Core tests | PASS | 173 passed |
| Baidu adapter tests | PASS | 26 passed |
| Application tests | PASS | 287 passed |
| WPF app tests | PASS | 125 passed |
| Feature-branch share query/index regression | PASS | 3 targeted tests passed |
| `master` Infrastructure.Tests excluding the scale timing gate | PASS | 194 passed |
| Formatting check | PASS | `git diff --check master..codex/transfer-share-link-replacement` returned clean |

The feature-worktree full-suite run was **not a clean pass**: Infrastructure.Tests reported 287 passed and 4 failed, while Core.Tests failed to compile. Application.Tests (409), App.Tests (157), and BaiduAdapter.Tests (33) passed in that run. The observed failures were:

- `StateTransitionTests` references `BaiduBatchTransfer.Application.Scheduling.OperationStatusChineseMapper`, but `BaiduBatchTransfer.Core.Tests.csproj` only references Core.
- `ExpiredRecoverySkipsManyEarlierUnexpiredRowsAndReturnsEarliestCanonicalLease` returned no lease after the uncommitted account-occupancy predicates were added.
- `ShareWritesStreamSixHundredThousandOperationsInBoundedChunksAndMemory` returned no share operation under the same uncommitted scheduling changes.
- `DiagnosticZipUsesAllowlistAndRedactsSecrets` rejected `999`; the generated manifest timestamp contained those digits, so the assertion is substring-based and timing-sensitive.
- The 200,000-resource bulk insert measured 29.23 seconds in that worktree, exceeding the 20-second test limit; managed-memory growth remained bounded at 15,529,576 bytes.

These failures are recorded as dirty-worktree blockers, not as regressions proven against the committed feature tip.

### Known Gate

The `master` Debug full-suite run had one infrastructure performance failure:

```text
PreparedStreamingWritesHandleTwoHundredThousandResourcesAndThreeReplicasWithinBoundedMemory
Bulk insert took 00:00:22.5805331.
```

The test's fixed limit is 20 seconds. Managed-memory growth stayed bounded at 16,958,792 bytes, and all functional assertions passed. Treat this as an environment/performance-threshold gate, not as a functional pass claim. Re-run on the target acceptance machine before changing the threshold or implementation.

### Next Steps

1. Run the complete suite from the intended feature branch after a clean restore.
2. Re-run the 200,000-resource performance gate on the target machine and record the elapsed time.
3. Complete the user-assisted Baidu/WPF acceptance gates; these cannot be established from local automated history alone.
4. Only after those gates pass, merge or otherwise promote `codex/transfer-share-link-replacement` into the release branch.

## 2026-08-21 Follow-up: Dirty Feature Worktree

The user confirmed the dirty feature worktree changes are intentional. The following fixes were applied there:

- Added the missing Application project reference to Core.Tests.
- Kept account/global occupancy predicates intact; updated the expired-recovery fixture to use independent output identities and made global slot counting ignore expired leases so stale work can be reclaimed.
- Updated the 600,000-operation benchmark to release each test lease before the next claim and restore rows before discovery validation.
- Replaced the diagnostic export test's timing-sensitive `999` substring assertion with a stable structural redaction assertion.

Focused verification passed: Core state 32/32, Application scheduling 28/28, infrastructure occupancy/recovery/diagnostic 5/5, and the 600,000-operation share benchmark 1/1. Release build passed with 0 warnings and 0 errors; `git diff --check` passed.

The feature-worktree full Debug suite is functionally green: Core 203, BaiduAdapter 33, Application 409, App 157, and Infrastructure 290 passed. One scale gate remains over its fixed threshold: the 200,000-resource bulk insert took 26.103 seconds against 20 seconds, with 15,217,160 bytes peak managed growth. No threshold was changed. This requires a target-machine performance decision before release promotion.

No credentials, cookies, share links, extraction codes, account IDs, or response bodies were written to this log.

## 2026-08-22 Browser Connection Attempt

The in-session browser runtime was initialized for the official Baidu page, but `agent.browsers.list()` returned an empty list. No page was opened and no login, cookie, local-storage, or profile inspection was performed.

For the next Codex session, connect a user-owned Chrome/Edge instance through the Codex browser extension, or use the desktop application's own WebView2 login window. Do not assume a Chrome/Edge session can be copied into WebView2. Continue to keep the live adapter capability disabled until the user-assisted field acceptance produces sanitized evidence.

## 2026-08-22 Handoff Follow-up: Live Adapter Gate

### Checks Executed

| Check | Result | Evidence |
| --- | --- | --- |
| Feature worktree Release build | PASS | `dotnet build --configuration Release`, 0 warnings, 0 errors |
| Baidu adapter offline tests | PASS | `dotnet test tests\\BaiduBatchTransfer.BaiduAdapter.Tests\\BaiduBatchTransfer.BaiduAdapter.Tests.csproj -c Release --no-build --no-restore`, 33 passed |
| Live transfer/share implementation status | BLOCKED | `LiveBaiduNetdiskAdapter` and `LiveBaiduShareAdapter` still return `NotValidated` for live operations |
| Application availability status | INTENTIONALLY DISABLED | `AppBootstrap` registers `UnavailableAdapterAvailability` |

### Decision

Do not replace `UnavailableAdapterAvailability` or invent Baidu endpoint semantics from memory. The next implementation step is the user-assisted field acceptance described in the transfer/share plan: one disposable user-owned test account, one or a few non-sensitive links, and sanitized evidence for exact remote IDs, request acceptance, permanent-share settings, pagination, and eventual consistency. Until that evidence exists, live capability must remain disabled.

No credentials, cookies, share links, extraction codes, account IDs, or response bodies were written to this log.
