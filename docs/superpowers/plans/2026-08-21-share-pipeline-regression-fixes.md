# Share Pipeline Regression Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore a clean, deterministic testable state for the in-progress transfer/share pipeline changes.

**Architecture:** Keep the existing SQLite scheduling and WPF test boundaries. Preserve the documented global concurrency limit and one-account occupancy invariant; repair test fixtures and lease lifecycle assumptions rather than weakening those predicates. Make diagnostic assertions validate serialized structure rather than incidental timestamp digits. Preserve bounded streaming behavior and verify the scale gates separately.

**Tech Stack:** .NET 8, C#, WPF, SQLite, xUnit.

---

### Task 1: Restore Core test project dependency

**Files:**
- Modify: `tests/BaiduBatchTransfer.Core.Tests/BaiduBatchTransfer.Core.Tests.csproj`
- Test: `tests/BaiduBatchTransfer.Core.Tests/Domain/StateTransitionTests.cs`

- [x] **Step 1: Run the focused build and capture the missing Application reference error**
- [x] **Step 2: Add the minimal Application project reference**
- [x] **Step 3: Build and run the focused Core test**

### Task 2: Align expired share lease fixture with account occupancy rules

**Files:**
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteShareSchedulingStore.cs`
- Test: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/ShareSchedulingStoreTests.cs`

- [x] **Step 1: Reproduce the expired recovery failure with the focused test**
- [x] **Step 2: Trace the candidate selection predicates and confirm the fixture's same-account unexpired rows correctly block reclamation**
- [x] **Step 3: Seed independent account/output-slot identities for the unexpired scan rows, leaving the expired candidate on the original account**
- [x] **Step 4: Run the focused recovery and occupancy tests, including the concurrent-claim guard**

### Task 3: Restore large-batch share reservation/write behavior

**Files:**
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteShareSchedulingStore.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteTransferSchedulingStore.cs`
- Test: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/RepositoryTests.cs`

- [x] **Step 1: Reproduce the null reservation in the 600,000-operation test**
- [x] **Step 2: Compare global/account occupancy checks with the transfer scheduler contract**
- [x] **Step 3: Release each benchmark lease through a deterministic test-only completion boundary before claiming the next row**
- [x] **Step 4: Run the focused large-batch test and bounded-memory assertions**

### Task 4: Make diagnostic export verification deterministic

**Files:**
- Modify: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/DatabaseRecoveryTests.cs`
- Test: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/DatabaseRecoveryTests.cs`

- [x] **Step 1: Reproduce the timestamp substring collision**
- [x] **Step 2: Parse manifest, logs, and metadata JSON; assert exact structural fields and stable redaction markers instead of broad incidental digit substrings**
- [x] **Step 3: Run all diagnostic export tests**

### Task 5: Verify application scheduling integration

**Files:**
- Test: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/ShareReconciliationTests.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/CrashBoundaryTests.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/TransferCoordinatorTests.cs`

- [x] **Step 1: Run focused reconciliation, startup-recovery, and coordinator tests**
- [x] **Step 2: Confirm paused-before-submit, result-pending, and account/global occupancy transitions**
- [x] **Step 3: Run the focused Application test set after infrastructure fixture fixes**

### Task 6: Full verification and log update

- [x] **Step 1: Run the complete Debug suite from a clean build/restore, including migration and recovery tests**
- [x] **Step 2: Run Release build and `git diff --check`**
- [x] **Step 3: Run the 200,000-resource and 600,000-operation scale gates separately and record timings without changing thresholds**
- [x] **Step 4: Update the workspace log at `C:\Users\45203\Documents\dakazhandian\docs\superpowers\execution-log.md` with exact results and remaining user-assisted acceptance gates**

The full suite is functionally green. The 200,000-resource timing gate remains open on this machine at 26.10 seconds versus the fixed 20-second threshold; no threshold was changed.
