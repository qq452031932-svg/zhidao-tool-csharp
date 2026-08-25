# WebView2 Candidate Protocol Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a fail-closed, exact-operation WebView2 typed transfer/share candidate path for Debug acceptance runs without enabling production selectors.

**Architecture:** Keep candidate DOM commands in a separate App-layer executor with fixed built-in scripts and sanitized DTOs. Bind every session to the exact acceptance navigation scope, reauthorize immediately before every mutation, and reuse existing typed transfer/share flows for baseline and exact-result semantics.

**Tech Stack:** .NET 8, C#, WPF, Microsoft WebView2, xUnit.

---

### Task 1: Candidate operation authorization and share prepare ordering

**Files:**
- Modify: `src/BaiduBatchTransfer.Application/Scheduling/CoordinatedBaiduAdapters.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Acceptance/AcceptanceWriteLeaseCoordinator.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/IVisibleWebAutomationBroker.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/NetworkExecutionGateTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/AcceptanceWriteLeaseCoordinatorTests.cs`

- [ ] Write failing tests proving share prepare obtains exact `PrepareShare` authorization and injects an immutable source binding before the inner adapter/session factory; submit only revalidates that same source and original expiry.
- [ ] Run the focused tests and confirm RED.
- [ ] Add `PrepareShare`, immutable `ConsumedLease`/`ExactFormalRecord` bindings, temporary/formal/recovery checks, and no-source-switch validation.
- [ ] Run focused tests and confirm GREEN.
- [ ] Commit.

### Task 2: Fixed candidate DOM protocol shell

**Files:**
- Create: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/BaiduCandidateDomProtocol.cs`
- Create: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/WebView2CandidateDomExecutor.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/BaiduCandidateDomProtocolTests.cs`

- [ ] Write failing tests for unique/0/multiple/wrong-state/wrong exact set, reauthorization, temporary expiry/recovery denial, exact formal-record ordinary/recovery allowance and cancellation.
- [ ] Run the focused tests and confirm RED.
- [ ] Implement typed commands, sanitized response parser and fixed-script executor; leave unproven field mappings explicitly unavailable.
- [ ] Run focused tests and confirm GREEN.
- [ ] Commit.

### Task 3: WebView2 typed transfer/share session

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/WebView2AutomationSession.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/VisibleWebAutomationQueue.cs`
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/WebView2AutomationSessionTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/VisibleWebAutomationQueueTests.cs`

- [ ] Write failing tests proving the session implements transfer/share/export typed contracts and scope binds before navigation.
- [ ] Run the focused tests and confirm RED.
- [ ] Delegate typed methods to the candidate executor and preserve cancellation/user-close fencing.
- [ ] Run focused tests and confirm GREEN.
- [ ] Commit.

### Task 4: Adapter reachability and fail-closed regression

**Files:**
- Modify: `src/BaiduBatchTransfer.BaiduAdapter/WebAutomation/TransferAutomationFlow.cs`
- Modify: `src/BaiduBatchTransfer.BaiduAdapter/WebAutomation/ShareAutomationFlow.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/WebViewBaiduNetdiskAdapterTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/WebViewBaiduShareAdapterTests.cs`
- Modify: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/LiveAdapterAvailabilityTests.cs`

- [ ] Write failing tests for candidate typed reachability and explicit `NotValidated`/zero-click on unavailable field protocol.
- [ ] Run focused tests and confirm RED.
- [ ] Map candidate protocol denials without weakening unknown-after-submit handling.
- [ ] Run all Task 12 focused tests and Debug/Release `-warnaserror`.
- [ ] Commit.
