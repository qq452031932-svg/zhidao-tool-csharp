> **⚠️ 状态：挂起，不要执行。基准已失效，且与已交付代码存在语义冲突。**
>
> 1. **基准失效**：本文件是对着 `master` / `codex/superpan` 的代码结构写的，
>    而该基准已落后功能分支 `codex/transfer-share-link-replacement` 449 个提交。
>    文中点名的 `OpenXmlResultWriter.cs` 并非实际实现位置
>    （真实实现为 `ReplacementWorkbookExportService.cs`）。
>    照此执行会造出第二套并行实现。
> 2. **已被实现**：本文描述的功能已于 2026-09-12 在功能分支落地
>    （`5ecac93` / `6f5daf4` / `af7454c`）。
> 3. **语义冲突未决**：规格要求"不新增横向结果列"，但实现在原列就地替换之外
>    **又追加了** `转存结果`/`分享链接`/`提取码`/`失败原因` 四列。
>    以哪边为准，需用户决策。详见 `docs/HANDOFF.md` 第 4.1 节。

# Account-Split Result Export Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Export each account's successful and unsuccessful transfer rows into timestamped workbooks, replacing links and extraction codes only for successful rows while preserving retryable failed rows.

**Architecture:** Persist immutable input URL/code plus latest generated URL/code on operation results. A batch export service groups by stable account ID and result class, writes the software's supported standard columns, and replaces link/code cells only for successes. The UI adds a “选中失败” command that selects failed rows without starting a retry.

**Tech Stack:** .NET 8/C#, existing application services and SQLite repositories, Open XML streaming writer, CommunityToolkit MVVM, xUnit.

---

### Task 1: Lock down result data semantics

**Files:**
- Modify: `src/BaiduBatchTransfer.Core/Abstractions/PersistenceModels.cs`
- Modify: `src/BaiduBatchTransfer.Core/Abstractions/PersistenceModels.cs` (`OperationResultRecord`)
- Modify: the existing SQLite job repository/migrations that read and write `OperationResults`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Jobs/OperationResultPersistenceTests.cs`

- [ ] Write failing tests proving immutable input URL/code and latest output URL/code are persisted; successful results export latest values, failed/skipped/pending results export input values, and a success without code clears the code.
- [ ] Run focused tests and confirm failure because fields/migration are absent.
- [ ] Add immutable-per-attempt input URL/code plus nullable latest URL/code fields, migration, and writes in the transfer-success transaction; on retry, the newly imported failed-row values become that attempt's input while prior attempts remain audit history.
- [ ] Run focused tests and full operation-result tests.
- [ ] Commit `feat: persist latest share outputs per operation result`.

### Task 2: Add result classification and failed-selection behavior

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Features/Jobs/*ViewModel.cs`
- Modify: related XAML view containing the result toolbar/table
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/*Tests.cs`

- [ ] Write failing tests for a `SelectFailed` command: it selects every failed/skipped/pending row, leaves successful rows unselected, and does not start transfer.
- [ ] Run focused UI tests and verify the expected failure.
- [ ] Implement a global selected-ID set (not only visible rows), button text `选中失败`, selected-count update, and empty-result message; selection survives paging/refresh until explicitly cleared.
- [ ] Run UI tests and verify selection is stable across refresh/paging.
- [ ] Commit `feat: add select-failed transfer action`.

### Task 3: Build account/result export model

**Files:**
- Create: `src/BaiduBatchTransfer.Application/Jobs/ExportAccountResultsService.cs`
- Modify: `src/BaiduBatchTransfer.Application/Jobs/ExportBatchResultsService.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Jobs/AccountResultExportTests.cs`

- [ ] Write failing tests for one-account success/failed files, multi-account splitting by AccountId, and omission of empty result files.
- [ ] Add a single export timestamp per invocation and deterministic grouping/order; stream account/outcome groups in database order instead of loading the whole batch into memory.
- [ ] Implement filename sanitization, reserved-name handling, path containment, atomic non-overwriting file creation, and collision suffixes.
- [ ] Ensure status mapping is fixed: `Success`→成功 file; `Failed`, `Skipped`, `Pending`, `Cancelled`, conflicts, and unknown statuses→失败 file. Success rows use latest URL/code; all other rows use immutable input URL/code and include fixed 状态/失败原因 columns.
- [ ] Run tests including same-name accounts, invalid Unicode/Windows names, cancellation, and write failure.
- [ ] Commit `feat: split result exports by account and outcome`.

### Task 4: Write supported standard workbook columns while replacing link/code cells

**Files:**
- Modify: `src/BaiduBatchTransfer.Infrastructure/Excel/OpenXmlResultWriter.cs`
- Modify: Excel row mapping/import-export contracts
- Test: `tests/BaiduBatchTransfer.Infrastructure.Tests/Excel/AccountResultWorkbookTests.cs`

- [ ] Write failing Open XML tests that read generated workbooks and assert the supported standard columns remain, exactly one link/code column exists, and no horizontal result columns are added.
- [ ] Implement row mapping that replaces only link and extraction-code cells for success rows.
- [ ] Keep failed rows unchanged for retry; define the standard columns exactly as 序号、标题、账号、分享链接、提取码、目标目录、转存状态、失败原因, and make the importer ignore the two result-only columns while preserving account/path mapping.
- [ ] Verify formula sanitization, row rollover, empty workbooks, and >Excel row-limit behavior.
- [ ] Commit `feat: preserve source columns in account result workbooks`.

### Task 5: Wire UI export flow and documentation

**Files:**
- Modify: export command/view model and result view XAML
- Modify: `docs/deployment/ota-fanfh-cn.md` or user-facing operation docs
- Test: app integration tests for export command and generated file list

- [ ] Write failing integration tests for one/two-account export filenames and success/failed file contents.
- [ ] Implement output-directory selection, progress, cancellation, partial-failure reporting, and user-facing file list; write `.partial` files only during generation, atomically rename on success, and remove partials on cancellation/failure.
- [ ] Ensure each run uses one timestamp and never overwrites existing files.
- [ ] Update docs with retry flow: click `选中失败` → review → start transfer → export success/failed files.
- [ ] Run focused app/infrastructure tests and full regression suite.
- [ ] Commit `feat: expose account-split result export workflow`.

### Task 6: Final verification and handoff

- [ ] Run `dotnet test` for affected projects and `git diff --check`.
- [ ] Manually inspect a disposable workbook containing success, failure, and no-code success rows.
- [ ] Confirm no server/SSH actions were performed.
- [ ] Update the handoff document with exact test commands and results.
- [ ] Commit final documentation and report remaining limitations.
