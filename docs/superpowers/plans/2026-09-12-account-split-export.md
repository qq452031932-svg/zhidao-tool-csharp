# Account-Split Result Export Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Export each account's successful and unsuccessful transfer rows into timestamped workbooks, replacing links and extraction codes only for successful rows while preserving retryable failed rows.

**Architecture:** Persist the latest generated share URL and extraction code on operation results. A batch export service groups by stable account ID and result class, copies the original workbook-shaped columns, and replaces link/code cells only for successes. The UI adds a “选中失败” command that selects failed rows without starting a retry.

**Tech Stack:** .NET 8/C#, existing application services and SQLite repositories, Open XML streaming writer, CommunityToolkit MVVM, xUnit.

---

### Task 1: Lock down result data semantics

**Files:**
- Modify: `src/BaiduBatchTransfer.Core/Abstractions/PersistenceModels.cs`
- Modify: `src/BaiduBatchTransfer.Core/Domain/OperationResult.cs` (or the current operation-result model)
- Modify: SQLite migrations/repositories for operation results
- Test: `tests/BaiduBatchTransfer.Application.Tests/Jobs/OperationResultPersistenceTests.cs`

- [ ] Write failing tests proving successful results persist new URL/code, failed results retain original URL/code, and a success without code clears the code.
- [ ] Run focused tests and confirm failure because fields/migration are absent.
- [ ] Add nullable latest URL/code fields, migration, repository writes, and reads.
- [ ] Run focused tests and full operation-result tests.
- [ ] Commit `feat: persist latest share outputs per operation result`.

### Task 2: Add result classification and failed-selection behavior

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Features/Jobs/*ViewModel.cs`
- Modify: related XAML view containing the result toolbar/table
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/*Tests.cs`

- [ ] Write failing tests for a `SelectFailed` command: it selects every failed/skipped/pending row, leaves successful rows unselected, and does not start transfer.
- [ ] Run focused UI tests and verify the expected failure.
- [ ] Implement the command, button text `选中失败`, selected-count update, and empty-result message.
- [ ] Run UI tests and verify selection is stable across refresh/paging.
- [ ] Commit `feat: add select-failed transfer action`.

### Task 3: Build account/result export model

**Files:**
- Create: `src/BaiduBatchTransfer.Application/Jobs/ExportAccountResultsService.cs`
- Modify: `src/BaiduBatchTransfer.Application/Jobs/ExportBatchResultsService.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Jobs/AccountResultExportTests.cs`

- [ ] Write failing tests for one-account success/failed files, multi-account splitting by AccountId, and omission of empty result files.
- [ ] Add a single export timestamp per invocation and deterministic grouping/order.
- [ ] Implement filename sanitization, reserved-name handling, path containment, atomic non-overwriting file creation, and collision suffixes.
- [ ] Ensure success rows use latest URL/code; failed rows use original URL/code and include status/reason.
- [ ] Run tests including same-name accounts, invalid Unicode/Windows names, cancellation, and write failure.
- [ ] Commit `feat: split result exports by account and outcome`.

### Task 4: Preserve workbook columns while replacing link/code cells

**Files:**
- Modify: `src/BaiduBatchTransfer.Infrastructure/Excel/OpenXmlResultWriter.cs`
- Modify: Excel row mapping/import-export contracts
- Test: `tests/BaiduBatchTransfer.Infrastructure.Tests/Excel/AccountResultWorkbookTests.cs`

- [ ] Write failing Open XML tests that read generated workbooks and assert original title/name/path columns remain, exactly one link/code column exists, and no horizontal result columns are added.
- [ ] Implement row mapping that replaces only link and extraction-code cells for success rows.
- [ ] Keep failed rows unchanged for retry and add fixed status/reason columns only where defined by the approved schema.
- [ ] Verify formula sanitization, row rollover, empty workbooks, and >Excel row-limit behavior.
- [ ] Commit `feat: preserve source columns in account result workbooks`.

### Task 5: Wire UI export flow and documentation

**Files:**
- Modify: export command/view model and result view XAML
- Modify: `docs/deployment/ota-fanfh-cn.md` or user-facing operation docs
- Test: app integration tests for export command and generated file list

- [ ] Write failing integration tests for one/two-account export filenames and success/failed file contents.
- [ ] Implement output-directory selection, progress, cancellation, partial-failure reporting, and user-facing file list.
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
