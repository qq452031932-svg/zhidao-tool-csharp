# Baidu Batch Transfer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Windows 10/11 x64 .NET 8 WPF client that imports large Baidu Netdisk share-link batches, supports isolated multiple accounts, replication and distribution modes, persistent safe scheduling, and local-only recovery/export.

**Architecture:** A WPF/MVVM shell calls application services backed by a domain-only Core project. SQLite, Open XML, DPAPI, system integration, and Baidu-specific HTTP behavior live behind interfaces in separate Infrastructure and BaiduAdapter projects. Source resources and per-account transfer operations are distinct persisted entities so replication, success-target refill, crash recovery, and account-specific outcomes remain deterministic.

**Tech Stack:** .NET 8, WPF, CommunityToolkit.Mvvm, WebView2, Microsoft.Data.Sqlite, DocumentFormat.OpenXml, Windows DPAPI, xUnit.

---

## Prerequisites and execution rules

- Work only in `C:\Users\45203\Documents\dakazhandian\.worktrees\baidu-batch-transfer` on branch `codex/baidu-batch-transfer`.
- Current machine state: .NET 6/7 runtimes are installed, but no .NET SDK is installed. Task 1 installs the .NET 8 SDK after explicit command approval.
- Use test-driven development for every behavior task: failing test, observed failure, minimal implementation, observed pass, focused commit.
- Never store or commit real Cookie values, access tokens, request signatures, account IDs, share content, or unredacted network captures.
- The live Baidu adapter is a go/no-go spike. If login isolation, whole-share saving, or result confirmation cannot be implemented without bypassing security measures, stop after Task 4 and return to design.
- All commands below run from the worktree root unless a task says otherwise.

## Planned repository structure

```text
BaiduBatchTransfer.sln
global.json
Directory.Build.props
Directory.Packages.props
src/
  BaiduBatchTransfer.Core/
    Abstractions/
    Domain/
    Services/
  BaiduBatchTransfer.Application/
    Accounts/
    Imports/
    Jobs/
    Scheduling/
  BaiduBatchTransfer.Infrastructure/
    Database/
    Excel/
    Security/
    SystemIntegration/
  BaiduBatchTransfer.BaiduAdapter/
    Authentication/
    Http/
    Parsing/
  BaiduBatchTransfer.App/
    Features/Accounts/
    Features/Imports/
    Features/Jobs/
    Services/
    App.xaml
    MainWindow.xaml
tests/
  BaiduBatchTransfer.Core.Tests/
  BaiduBatchTransfer.Application.Tests/
  BaiduBatchTransfer.Infrastructure.Tests/
  BaiduBatchTransfer.BaiduAdapter.Tests/
  BaiduBatchTransfer.App.Tests/
benchmarks/
  BaiduBatchTransfer.Benchmarks/
docs/
  adapter/baidu-adapter-contract.md
  superpowers/specs/2026-08-19-baidu-batch-transfer-design.md
```

Each production file owns one concept. Domain projects never reference WPF, WebView2, SQLite, or Baidu response types. The BaiduAdapter never updates UI or database state directly.

### Task 1: Install and pin the .NET 8 toolchain

**Files:**
- Create: `global.json`

- [ ] **Step 1: Confirm the missing SDK baseline**

Run `dotnet --list-sdks`.

Expected: no installed SDK entries.

- [ ] **Step 2: Install the official .NET 8 SDK after approval**

Run with required elevated/network approval:

```powershell
winget install --id Microsoft.DotNet.SDK.8 --exact --source winget
```

Expected: installation succeeds from the Microsoft package source.

- [ ] **Step 3: Verify the SDK**

Run:

```powershell
dotnet --version
dotnet --list-sdks
```

Expected: `dotnet --version` starts with `8.` and an 8.0 SDK path is listed.

- [ ] **Step 4: Pin the SDK**

Create `global.json`:

```json
{
  "sdk": {
    "version": "8.0.100",
    "rollForward": "latestFeature",
    "allowPrerelease": false
  }
}
```

Run `dotnet --version` and expect an installed stable 8.0 SDK.

- [ ] **Step 5: Commit the toolchain pin**

```powershell
git add global.json
git commit -m "build: pin dotnet 8 sdk"
```

### Task 2: Scaffold the solution and project boundaries

**Files:**
- Create: `BaiduBatchTransfer.sln`
- Create: `Directory.Build.props`
- Create: `Directory.Packages.props`
- Create: five production projects under `src/`
- Create: five test projects under `tests/`
- Create: `benchmarks/BaiduBatchTransfer.Benchmarks/BaiduBatchTransfer.Benchmarks.csproj`

- [ ] **Step 1: Create the solution and projects**

Run:

```powershell
dotnet new sln -n BaiduBatchTransfer
dotnet new classlib -n BaiduBatchTransfer.Core -o src/BaiduBatchTransfer.Core -f net8.0
dotnet new classlib -n BaiduBatchTransfer.Application -o src/BaiduBatchTransfer.Application -f net8.0
dotnet new classlib -n BaiduBatchTransfer.Infrastructure -o src/BaiduBatchTransfer.Infrastructure -f net8.0
dotnet new classlib -n BaiduBatchTransfer.BaiduAdapter -o src/BaiduBatchTransfer.BaiduAdapter -f net8.0
dotnet new wpf -n BaiduBatchTransfer.App -o src/BaiduBatchTransfer.App -f net8.0
dotnet new xunit -n BaiduBatchTransfer.Core.Tests -o tests/BaiduBatchTransfer.Core.Tests -f net8.0
dotnet new xunit -n BaiduBatchTransfer.Application.Tests -o tests/BaiduBatchTransfer.Application.Tests -f net8.0
dotnet new xunit -n BaiduBatchTransfer.Infrastructure.Tests -o tests/BaiduBatchTransfer.Infrastructure.Tests -f net8.0
dotnet new xunit -n BaiduBatchTransfer.BaiduAdapter.Tests -o tests/BaiduBatchTransfer.BaiduAdapter.Tests -f net8.0
dotnet new xunit -n BaiduBatchTransfer.App.Tests -o tests/BaiduBatchTransfer.App.Tests -f net8.0
dotnet new console -n BaiduBatchTransfer.Benchmarks -o benchmarks/BaiduBatchTransfer.Benchmarks -f net8.0
```

Then edit `tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj` so its target is `<TargetFramework>net8.0-windows</TargetFramework>` before adding the WPF application reference.

Expected: templates are created without errors and the application test project has a Windows-compatible TFM.

- [ ] **Step 2: Add projects and references**

Run:

```powershell
dotnet sln add (Get-ChildItem -Recurse -Filter *.csproj | ForEach-Object FullName)
dotnet add src/BaiduBatchTransfer.Application reference src/BaiduBatchTransfer.Core
dotnet add src/BaiduBatchTransfer.Infrastructure reference src/BaiduBatchTransfer.Core
dotnet add src/BaiduBatchTransfer.BaiduAdapter reference src/BaiduBatchTransfer.Core
dotnet add src/BaiduBatchTransfer.App reference src/BaiduBatchTransfer.Core src/BaiduBatchTransfer.Application src/BaiduBatchTransfer.Infrastructure src/BaiduBatchTransfer.BaiduAdapter
dotnet add tests/BaiduBatchTransfer.Core.Tests reference src/BaiduBatchTransfer.Core
dotnet add tests/BaiduBatchTransfer.Application.Tests reference src/BaiduBatchTransfer.Application src/BaiduBatchTransfer.Core
dotnet add tests/BaiduBatchTransfer.Infrastructure.Tests reference src/BaiduBatchTransfer.Infrastructure src/BaiduBatchTransfer.Core
dotnet add tests/BaiduBatchTransfer.BaiduAdapter.Tests reference src/BaiduBatchTransfer.BaiduAdapter src/BaiduBatchTransfer.Core
dotnet add tests/BaiduBatchTransfer.App.Tests reference src/BaiduBatchTransfer.App src/BaiduBatchTransfer.Application
dotnet add benchmarks/BaiduBatchTransfer.Benchmarks reference src/BaiduBatchTransfer.Core src/BaiduBatchTransfer.Application src/BaiduBatchTransfer.Infrastructure
```

- [ ] **Step 3: Configure packages and compiler rules**

Create `Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  </PropertyGroup>
</Project>
```

Create `Directory.Packages.props` with `ManagePackageVersionsCentrally=true`. For each of `CommunityToolkit.Mvvm`, `DocumentFormat.OpenXml`, `Microsoft.Data.Sqlite`, `Microsoft.Extensions.Hosting`, `Microsoft.Web.WebView2`, `System.Security.Cryptography.ProtectedData`, `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`, and `coverlet.collector`, query NuGet during this task, select the newest stable release that declares .NET 8 compatibility, and write the exact resolved version into `Directory.Packages.props`; do not leave floating versions or placeholders. Record the selection date and package/version table in the commit body. Add packages only to projects that use them. Commit generated `packages.lock.json` files. Do not add an Excel library that loads the complete workbook into memory.

- [ ] **Step 4: Verify the clean baseline**

Delete only template `Class1.cs` and `UnitTest1.cs` files. Run:

```powershell
dotnet restore
dotnet build -c Debug --no-restore
dotnet test -c Debug --no-build
dotnet restore --locked-mode
```

Expected: build and tests pass; locked restore succeeds.

- [ ] **Step 5: Commit**

```powershell
git add BaiduBatchTransfer.sln Directory.Build.props Directory.Packages.props src tests benchmarks
git commit -m "build: scaffold wpf solution"
```

### Task 3: Define the Baidu adapter boundary

**Files:**
- Create: `src/BaiduBatchTransfer.Core/Abstractions/IBaiduNetdiskAdapter.cs`
- Create: `src/BaiduBatchTransfer.Core/Abstractions/IShareLinkResolver.cs`
- Create: `src/BaiduBatchTransfer.Core/Abstractions/IBaiduSessionStore.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/BaiduAccountIdentity.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/BaiduAdapterResult.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/BaiduErrorScope.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/BaiduErrorKind.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/AccountSession.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/AccountCapacity.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/RemoteDirectory.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/ShareMetadata.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/TransferReceipt.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/TransferConfirmation.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/TransferConfirmationQuery.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/OperationConfirmationDescriptor.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/DuplicatePolicy.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/ShareReference.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/RemotePath.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/ResolvedShareLink.cs`
- Test: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/AdapterContractTests.cs`

- [ ] **Step 1: Write failing contract tests**

Test that adapter results classify global resource errors separately from account and operation errors and expose only sanitized messages.

```csharp
[Fact]
public void InvalidExtractionCode_IsGlobalResourceError()
{
    var result = BaiduAdapterResult.Failure(BaiduErrorKind.InvalidExtractionCode, "invalid code");
    Assert.Equal(BaiduErrorScope.Resource, result.Scope);
    Assert.DoesNotContain("Cookie", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Run the focused test and observe failure**

```powershell
dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests --filter FullyQualifiedName~AdapterContractTests
```

Expected: FAIL because adapter result types do not exist.

- [ ] **Step 3: Implement minimal stable contracts**

`IBaiduNetdiskAdapter` exposes no raw Baidu DTOs:

```csharp
public interface IBaiduNetdiskAdapter
{
    Task<BaiduAdapterResult<BaiduAccountIdentity>> ValidateSessionAsync(AccountSession session, CancellationToken ct);
    Task<BaiduAdapterResult<AccountCapacity>> GetCapacityAsync(AccountSession session, CancellationToken ct);
    Task<BaiduAdapterResult<IReadOnlyList<RemoteDirectory>>> ListDirectoriesAsync(AccountSession session, RemotePath path, CancellationToken ct);
    Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryAsync(AccountSession session, RemotePath path, CancellationToken ct);
    Task<BaiduAdapterResult<ShareMetadata>> InspectShareAsync(AccountSession session, ShareReference share, CancellationToken ct);
    Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareAsync(AccountSession session, ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken ct);
    Task<BaiduAdapterResult<TransferConfirmation>> ConfirmTransferAsync(AccountSession session, TransferConfirmationQuery query, CancellationToken ct);
}
```

`IShareLinkResolver.ResolveAsync(string rawUrl, CancellationToken ct)` is session-free and owns network redirect resolution for Baidu short links. It allowlists Baidu HTTPS hosts, limits redirects, rejects cross-domain redirects, and returns the final URL plus stable share ID. If resolution is offline, blocked, or cannot produce a stable ID, staging keeps the row as `NeedsResolution`; it cannot be deduplicated as ready or committed until a later retry succeeds. Core normalization remains pure.

`EnsureDirectoryAsync` idempotently ensures the complete normalized destination path exists before save and returns its stable remote directory identity. `SaveWholeShareAsync` accepts only an ensured destination. `TransferConfirmationQuery` is rebuildable from persisted operation state and contains the stable source share identity, normalized target path, expected outer-folder/name fingerprint, operation fingerprint, submit time window, and optional adapter request/remote item IDs learned after a response. `OperationConfirmationDescriptor` is created and persisted before submit and enriched atomically when a response arrives; confirmation must therefore work after a crash even when no in-memory `TransferReceipt` survives. It returns only `ConfirmedPresent`, `ConfirmedAbsent`, or `Ambiguous` and must not treat same-name presence alone as proof. Map known error kinds to `Resource`, `Account`, `Operation`, or `Unknown`. Reject unsafe messages containing credential header names.

- [ ] **Step 4: Verify the focused tests pass**

```powershell
dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests --filter FullyQualifiedName~AdapterContractTests
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/BaiduBatchTransfer.Core tests/BaiduBatchTransfer.BaiduAdapter.Tests
git commit -m "feat: define baidu adapter contracts"
```

### Task 4: Prove WebView2 login isolation and live adapter feasibility

**Files:**
- Create: `src/BaiduBatchTransfer.App/Features/Accounts/BaiduLoginWindow.xaml`
- Create: `src/BaiduBatchTransfer.App/Features/Accounts/BaiduLoginWindow.xaml.cs`
- Create: `src/BaiduBatchTransfer.App/Services/WebView2AccountEnvironmentFactory.cs`
- Create: `src/BaiduBatchTransfer.App/Services/WebView2ProfilePath.cs`
- Create: `src/BaiduBatchTransfer.BaiduAdapter/Authentication/WebViewCookieSessionBridge.cs`
- Create: `src/BaiduBatchTransfer.BaiduAdapter/Http/LiveBaiduNetdiskAdapter.cs`
- Create: `src/BaiduBatchTransfer.BaiduAdapter/Parsing/BaiduErrorClassifier.cs`
- Create: `src/BaiduBatchTransfer.BaiduAdapter/Http/BaiduShareLinkResolver.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Accounts/AdapterSpikeViewModel.cs`
- Test: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduErrorClassifierTests.cs`
- Test: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduShareLinkResolverTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Services/WebView2ProfilePathTests.cs`
- Create: `docs/adapter/baidu-adapter-contract.md`

- [ ] **Step 1: Write failing isolation and classifier tests**

```csharp
[Fact]
public void Profiles_AreIsolatedByLocalAccountId()
{
    var a = WebView2ProfilePath.ForAccount(_root, Guid.Parse("00000000-0000-0000-0000-000000000001"));
    var b = WebView2ProfilePath.ForAccount(_root, Guid.Parse("00000000-0000-0000-0000-000000000002"));
    Assert.NotEqual(a, b);
}
```

Also test scrubbed representative errors for invalid link, invalid code, expired login, verification required, capacity, target path, transient service, and unknown response. With a fake HTTP handler, test canonical links, bounded Baidu short-link redirects, cross-domain rejection, redirect loops, offline `NeedsResolution`, and final stable share-ID extraction without an account session.

- [ ] **Step 2: Run tests and observe failure**

```powershell
dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests --filter "FullyQualifiedName~BaiduErrorClassifierTests|FullyQualifiedName~BaiduShareLinkResolverTests"
dotnet test tests/BaiduBatchTransfer.App.Tests --filter FullyQualifiedName~WebView2ProfilePathTests
```

Expected: FAIL because profile and classifier implementations do not exist.

- [ ] **Step 3: Implement the minimal login spike**

Use one `CoreWebView2Environment` user-data folder per local account record. Restrict top-level navigation to the Baidu login/netdisk hosts observed during the spike. After user scan-login, use `CoreWebView2.CookieManager` to create an in-memory `AccountSession`; never log Cookie values.

Add a `DEBUG`-only discovery probe using WebView2 DevTools Protocol events. Record only official host, path, HTTP method, status code, and JSON property names while the user manually performs one whole-share save. Omit headers, bodies, names, IDs, query signatures, and Cookie values.

- [ ] **Step 4: Execute the user-assisted go/no-go gate**

Run:

```powershell
dotnet run --project src/BaiduBatchTransfer.App
```

With a user-owned test account and legal test share, verify:

1. Account A remains logged in after Account B signs in through a separate profile.
2. Stable Baidu identity is read and duplicate account addition rejected.
3. Root directory listing succeeds.
4. A public Baidu short link resolves to a stable share ID without selecting an account; non-Baidu redirects are rejected.
5. Share inspection works with and without an extraction code.
6. A previously absent nested outer directory is idempotently created, then the complete share saves into that ensured directory.
7. The result is confirmed without automatic resubmission.
8. Invalid link, invalid code, expired login, and verification-required responses are distinguished.

Document sanitized endpoint families, required fields, response fields, allowlisted hosts, same-name behavior, and error mapping in `docs/adapter/baidu-adapter-contract.md`. If any required action needs CAPTCHA bypass, credential interception beyond the user's own session, or cannot be confirmed reliably, stop implementation and report the failed gate.

- [ ] **Step 5: Create scrubbed fixtures and pass tests**

Create minimal response fixtures using deterministic fake IDs. Implement the adapter parser/classifier against them. Run:

```powershell
dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests
dotnet build -c Debug
git diff --check
```

Expected: tests/build pass; `git diff` contains no secrets or personal content.

- [ ] **Step 6: Commit only after the gate passes**

```powershell
git add src/BaiduBatchTransfer.App src/BaiduBatchTransfer.BaiduAdapter tests/BaiduBatchTransfer.BaiduAdapter.Tests docs/adapter
git commit -m "feat: validate baidu login and transfer adapter"
```

### Task 5: Implement domain models and state invariants

**Files:**
- Create: `src/BaiduBatchTransfer.Core/Domain/TransferMode.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/QuotaBasis.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/ShortagePolicy.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/BatchStatus.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/OperationStatus.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/TargetSlotState.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/ImportSession.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/StagedImportRow.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/BaiduAccount.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/BatchJob.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/ResourceItem.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/TransferOperation.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/AccountAllocation.cs`
- Test: `tests/BaiduBatchTransfer.Core.Tests/Domain/StateTransitionTests.cs`
- Test: `tests/BaiduBatchTransfer.Core.Tests/Domain/TargetSlotTests.cs`

- [ ] **Step 1: Write failing state-transition tests**

Cover legal and illegal transitions, including `Submitting -> ResultPending`, cancellation only before submit, and `AwaitingCandidates -> PausedReady -> Active`.

```csharp
[Fact]
public void ResultPending_RetainsTargetSlot()
{
    var operation = TransferOperation.CreateReserved(TestIds.Resource, TestIds.Account);
    operation.MarkSubmitting(TestClock.Now);
    operation.MarkResultPending("timeout");
    Assert.Equal(TargetSlotState.Reserved, operation.TargetSlotState);
}
```

- [ ] **Step 2: Run and observe failure**

```powershell
dotnet test tests/BaiduBatchTransfer.Core.Tests --filter "FullyQualifiedName~StateTransitionTests|FullyQualifiedName~TargetSlotTests"
```

Expected: FAIL because domain types do not exist.

- [ ] **Step 3: Implement minimal aggregates and invariants**

Use private setters and intent methods. Enforce:

```csharp
public int AvailableSlots => Math.Max(0, EffectiveTargetSuccessCount - FulfilledCount - ReservedSlotCount);
```

`MarkFulfilled` converts one reserved slot to fulfilled atomically at the aggregate level. `MarkDefinitivelyFailed` releases it. `MarkResultPending` retains it. Account tombstoning clears session references but preserves historical identity.

- [ ] **Step 4: Run all Core tests**

```powershell
dotnet test tests/BaiduBatchTransfer.Core.Tests
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/BaiduBatchTransfer.Core tests/BaiduBatchTransfer.Core.Tests
git commit -m "feat: add transfer domain model"
```

### Task 6: Implement share/path normalization and duplicate rules

**Files:**
- Create: `src/BaiduBatchTransfer.Core/Services/BaiduShareNormalizer.cs`
- Create: `src/BaiduBatchTransfer.Core/Services/BaiduPathNormalizer.cs`
- Create: `src/BaiduBatchTransfer.Core/Services/DuplicateClassifier.cs`
- Test: `tests/BaiduBatchTransfer.Core.Tests/Services/BaiduShareNormalizerTests.cs`
- Test: `tests/BaiduBatchTransfer.Core.Tests/Services/BaiduPathNormalizerTests.cs`
- Test: `tests/BaiduBatchTransfer.Core.Tests/Services/DuplicateClassifierTests.cs`

- [ ] **Step 1: Write table-driven failing tests**

Include canonical/short-link resolved forms, tracking parameters, trailing Chinese/ASCII punctuation, Unicode paths, repeated separators, root paths, case behavior from the adapter spike, conflicting extraction codes, and distribution-vs-replication duplicate behavior.

```csharp
[Theory]
[InlineData("https://pan.baidu.com/s/abc?pwd=1a2b。", "abc")]
[InlineData("HTTPS://PAN.BAIDU.COM/s/abc?from=share", "abc")]
public void EquivalentLinks_HaveSameShareId(string input, string expected) =>
    Assert.Equal(expected, _normalizer.Normalize(input).ShareId);
```

- [ ] **Step 2: Run and observe failure**

```powershell
dotnet test tests/BaiduBatchTransfer.Core.Tests --filter "FullyQualifiedName~NormalizerTests|FullyQualifiedName~DuplicateClassifierTests"
```

Expected: FAIL.

- [ ] **Step 3: Implement deterministic normalizers**

The normalizer must not perform network requests. The Application import pipeline calls session-free `IShareLinkResolver` for short links, then passes its final URL to Core; direct canonical links go straight to Core. Unresolved short links are not assigned a guessed identity. In distribution mode, one share ID per batch is allowed; conflicting code/path/account fields produce a conflict. In replication mode, one source resource expands later by account.

- [ ] **Step 4: Verify tests**

```powershell
dotnet test tests/BaiduBatchTransfer.Core.Tests
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/BaiduBatchTransfer.Core tests/BaiduBatchTransfer.Core.Tests
git commit -m "feat: normalize baidu shares and paths"
```

### Task 7: Implement deterministic account allocation algorithms

**Files:**
- Create: `src/BaiduBatchTransfer.Core/Services/ReplicationExpander.cs`
- Create: `src/BaiduBatchTransfer.Core/Services/TaskQuotaAllocator.cs`
- Create: `src/BaiduBatchTransfer.Core/Services/SuccessTargetAllocator.cs`
- Create: `src/BaiduBatchTransfer.Core/Services/CandidateSelector.cs`
- Create: `src/BaiduBatchTransfer.Core/Services/ShortageSimulator.cs`
- Create: `src/BaiduBatchTransfer.Core/Services/AccountRuleResolver.cs`
- Create: `src/BaiduBatchTransfer.Core/Domain/AccountRule.cs`
- Test: `tests/BaiduBatchTransfer.Core.Tests/Services/ReplicationExpanderTests.cs`
- Test: `tests/BaiduBatchTransfer.Core.Tests/Services/TaskQuotaAllocatorTests.cs`
- Test: `tests/BaiduBatchTransfer.Core.Tests/Services/SuccessTargetAllocatorTests.cs`
- Test: `tests/BaiduBatchTransfer.Core.Tests/Services/ShortageSimulatorTests.cs`
- Test: `tests/BaiduBatchTransfer.Core.Tests/Services/AccountRuleResolverTests.cs`

- [ ] **Step 1: Write failing replication and 1:2:1 tests**

Verify 1,000 resources x 3 accounts yields 3,000 unique operation intents. Verify 200,000 resources distribute exactly 50,000/100,000/50,000 in weighted interleaving.

- [ ] **Step 2: Write failing success-target tests**

Cover common candidates, account-exclusive candidates, proportional progress ties by confirmed account order, target zero exclusion, pending slot retention, deterministic integer rounding, priority shortage, proportional shortage, and wait-for-more behavior.

```csharp
[Fact]
public void PendingOperation_PreventsReplacementBeyondTarget()
{
    var state = AllocationState.ForTarget(target: 2, fulfilled: 1, reserved: 1);
    Assert.Equal(0, state.AvailableSlots);
}
```

- [ ] **Step 3: Run and observe failure**

Add rule-resolution tests before running: exact per-row account wins over directory binding, the longest normalized directory prefix wins between directory rules, then lower numeric rule priority, then confirmed account order; disabled/deleted/unhealthy accounts cannot win; an unmatched row remains public. Verify locked counts produced by these rules are deducted before task-quota allocation and reject creation when a locked count exceeds that account's target.

```powershell
dotnet test tests/BaiduBatchTransfer.Core.Tests --filter "FullyQualifiedName~AllocatorTests|FullyQualifiedName~ExpanderTests|FullyQualifiedName~ShortageSimulatorTests|FullyQualifiedName~AccountRuleResolverTests"
```

Expected: FAIL.

- [ ] **Step 4: Implement minimal pure algorithms**

Resolve account rules into an explicit `CandidateConstraintAccountId` and resolution reason before allocation; never re-evaluate mutable account rules after batch confirmation. Select the account with minimum `(Fulfilled + Reserved) / OriginalTarget`, comparing ratios by cross multiplication to avoid floating-point error; tie by `PriorityOrder`. Choose the smallest account-exclusive `CandidateSequence`, otherwise the smallest public sequence. Produce an `AllocationDecision` without database access.

- [ ] **Step 5: Verify determinism and commit**

```powershell
dotnet test tests/BaiduBatchTransfer.Core.Tests
git add src/BaiduBatchTransfer.Core tests/BaiduBatchTransfer.Core.Tests
git commit -m "feat: add deterministic account allocation"
```

### Task 8: Build the SQLite persistence layer and migrations

**Files:**
- Create: `src/BaiduBatchTransfer.Core/Abstractions/IUnitOfWork.cs`
- Create: `src/BaiduBatchTransfer.Core/Abstractions/IImportRepository.cs`
- Create: `src/BaiduBatchTransfer.Core/Abstractions/IJobRepository.cs`
- Create: `src/BaiduBatchTransfer.Core/Abstractions/IAccountRepository.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteConnectionFactory.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/DatabaseMigrator.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/Migrations/001_InitialSchema.sql`
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/Repositories/*.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/DatabaseIntegrityService.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/DatabaseBackupService.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/DatabaseRecoveryService.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/DiagnosticExportService.cs`
- Test: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/MigrationTests.cs`
- Test: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/RepositoryTests.cs`
- Test: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/DatabaseRecoveryTests.cs`

- [ ] **Step 1: Write failing migration tests**

Verify a new database enables foreign keys and WAL, creates all spec tables/indexes, records schema version, and preserves a backup before upgrading an older fixture. `StagedImportRows.Disposition` must allow `NeedsResolution` in addition to ready/invalid/duplicate/conflict/excluded states. The operation schema must persist the pre-submit confirmation descriptor plus optional adapter request/remote item IDs so Task 13 can reconcile without an in-memory receipt.

```csharp
[Fact]
public async Task NewDatabase_UsesWalAndForeignKeys()
{
    await _migrator.MigrateAsync(_dbPath, CancellationToken.None);
    Assert.Equal("wal", await ScalarAsync<string>("PRAGMA journal_mode"));
    Assert.Equal(1L, await ScalarAsync<long>("PRAGMA foreign_keys"));
}
```

- [ ] **Step 2: Run and observe failure**

```powershell
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests --filter "FullyQualifiedName~MigrationTests|FullyQualifiedName~RepositoryTests|FullyQualifiedName~DatabaseRecoveryTests"
```

Expected: FAIL.

- [ ] **Step 3: Implement schema and focused repositories**

Create tables from spec sections 8.1-8.10, including the explicit `NeedsResolution` staged-row disposition. Extend `TransferOperations` with `ConfirmationDescriptorJson`, `AdapterRequestId`, and `RemoteItemIdsJson`; write the descriptor before crossing the submit boundary and enrich the optional identifiers with the adapter response. Add indexes for import session/disposition, batch/status, account/status, candidate sequence, operation lease/status, and unique active operation key. Use parameterized SQL only. Configure busy timeout. Keep repository methods page-based and cancellation-aware.

- [ ] **Step 4: Implement integrity and migration recovery**

Before migration, use SQLite's online backup API to create a versioned backup and validate the copy. On startup run `PRAGMA quick_check`; block scheduling when it does not return `ok`. Implement: list validated backups, restore a user-selected backup only after closing active connections and first preserving the damaged file, re-run migration/integrity before reopening, and export a ZIP containing schema/app versions, integrity output, sanitized logs, and no database/session/profile contents. Never silently replace a corrupt database.

- [ ] **Step 5: Verify and commit**

```powershell
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests --filter "FullyQualifiedName~MigrationTests|FullyQualifiedName~RepositoryTests|FullyQualifiedName~DatabaseRecoveryTests"
dotnet build -c Debug
git add src/BaiduBatchTransfer.Core src/BaiduBatchTransfer.Infrastructure tests/BaiduBatchTransfer.Infrastructure.Tests
git commit -m "feat: add sqlite persistence and migrations"
```

### Task 9: Implement encrypted account/session persistence

**Files:**
- Create: `src/BaiduBatchTransfer.Infrastructure/Security/DpapiSessionProtector.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/Security/SessionEnvelope.cs`
- Create: `src/BaiduBatchTransfer.Application/Accounts/AddAccountService.cs`
- Create: `src/BaiduBatchTransfer.Application/Accounts/RemoveAccountService.cs`
- Test: `tests/BaiduBatchTransfer.Infrastructure.Tests/Security/DpapiSessionProtectorTests.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Accounts/AccountLifecycleTests.cs`

- [ ] **Step 1: Write failing security/lifecycle tests**

Test DPAPI round-trip for the current Windows user, corrupt ciphertext rejection, duplicate `BaiduUserId` rejection, and tombstone removal that clears session/profile references but preserves historical account ID.

- [ ] **Step 2: Run and observe failure**

```powershell
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests --filter FullyQualifiedName~DpapiSessionProtectorTests
dotnet test tests/BaiduBatchTransfer.Application.Tests --filter FullyQualifiedName~AccountLifecycleTests
```

Expected: FAIL.

- [ ] **Step 3: Implement minimal session protection and account services**

Use `ProtectedData.Protect/Unprotect` with `DataProtectionScope.CurrentUser` and application-specific entropy. Store a versioned JSON envelope before encryption. Account removal first blocks new work, deletes the WebView2 profile through an abstraction, clears encrypted session, then tombstones the row.

- [ ] **Step 4: Verify tests and scan logs/source**

```powershell
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests
dotnet test tests/BaiduBatchTransfer.Application.Tests
rg -n "Cookie:|BDUSS|access_token" src tests
```

Expected: tests pass; search finds no real credential literals or logging statements.

- [ ] **Step 5: Commit**

```powershell
git add src tests
git commit -m "feat: secure multi-account sessions"
```

### Task 10: Implement streaming text and Excel staging imports

**Files:**
- Create: `src/BaiduBatchTransfer.Application/Imports/TextImportParser.cs`
- Create: `src/BaiduBatchTransfer.Application/Imports/ImportStagingService.cs`
- Create: `src/BaiduBatchTransfer.Application/Imports/ImportConflictService.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/Excel/OpenXmlImportReader.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/Excel/OpenXmlTemplateWriter.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/Excel/OpenXmlResultWriter.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/Excel/ExcelTextSanitizer.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Imports/TextImportParserTests.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Imports/ImportStagingServiceTests.cs`
- Test: `tests/BaiduBatchTransfer.Infrastructure.Tests/Excel/OpenXmlStreamingTests.cs`
- Test: `tests/BaiduBatchTransfer.Infrastructure.Tests/Excel/ExcelTextSanitizerTests.cs`

- [ ] **Step 1: Write failing text parsing/staging tests**

Cover mixed prose, multiple links, adjacent extraction codes, no-code public links, canonical and short links, duplicates after resolved redirects, offline/blocked short links retained as `NeedsResolution`, retry-to-ready, conflicts, malformed links, row notes, and batches of 5,000 staged rows committed in chunks.

- [ ] **Step 2: Write failing Open XML tests**

Generate a 200,000-row workbook fixture during the test instead of committing it. Verify streaming enumeration, bounded memory instrumentation, template columns, result export, and formula-injection escaping for `=`, `+`, `-`, `@` prefixes.

- [ ] **Step 3: Run and observe failure**

```powershell
dotnet test tests/BaiduBatchTransfer.Application.Tests --filter "FullyQualifiedName~Import"
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests --filter "FullyQualifiedName~OpenXml|FullyQualifiedName~ExcelTextSanitizer"
```

Expected: FAIL.

- [ ] **Step 4: Implement streaming import/export**

Use `OpenXmlReader` and `OpenXmlWriter`; never materialize all rows. Persist `ImportSession` before reading, write staging rows in bounded transactions, and persist progress/counters. Inject `IShareLinkResolver`; resolve short links with bounded concurrency and persist unresolved rows for retry, while canonical links use pure Core normalization. Copy `Note`, resolved path overrides, account text, and folder fields into staging. Export all user-sourced cells as explicit text.

- [ ] **Step 5: Verify and commit**

```powershell
dotnet test tests/BaiduBatchTransfer.Application.Tests
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests
git add src tests
git commit -m "feat: add streaming text and excel imports"
```

### Task 11: Finalize drafts into replication or distribution batches

**Files:**
- Create: `src/BaiduBatchTransfer.Application/Jobs/BatchDraftValidator.cs`
- Create: `src/BaiduBatchTransfer.Application/Jobs/BatchConfirmationSummary.cs`
- Create: `src/BaiduBatchTransfer.Application/Jobs/CreateBatchService.cs`
- Create: `src/BaiduBatchTransfer.Application/Jobs/AppendCandidatesService.cs`
- Create: `src/BaiduBatchTransfer.Application/Jobs/ReopenShortfallBatchService.cs`
- Create: `src/BaiduBatchTransfer.Application/Jobs/ReplicationPreflightEstimator.cs`
- Create: `src/BaiduBatchTransfer.Core/Abstractions/IDiskSpaceProbe.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/SystemIntegration/WindowsDiskSpaceProbe.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Jobs/CreateBatchServiceTests.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Jobs/AppendCandidatesServiceTests.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Jobs/ReplicationPreflightEstimatorTests.cs`

- [ ] **Step 1: Write failing replication finalization tests**

Verify 1,000 included resources and three accounts produce 1,000 `ResourceItems`, 3,000 unique `TransferOperations`, per-account target directory snapshots, and a summary showing both resource and operation counts. Estimate operation count and conservative database growth before creation; reject when available disk is below estimated growth plus safety reserve, without writing a partial batch.

- [ ] **Step 2: Write failing distribution finalization tests**

Verify task-quota totals/locked assignments, success-target candidate constraints, target-zero account removal, candidate sequence assignment, note/path snapshot persistence, and rejection of unresolved conflicts.

- [ ] **Step 3: Write failing append/reopen tests**

Verify appended candidates start after the maximum existing sequence, inherit immutable batch settings, deduplicate against the original batch, preserve original targets/results, reactivate unused exclusive candidates, and transition `AwaitingCandidates -> PausedReady` only after confirmation.

- [ ] **Step 4: Run and observe failure**

```powershell
dotnet test tests/BaiduBatchTransfer.Application.Tests --filter "FullyQualifiedName~CreateBatchServiceTests|FullyQualifiedName~AppendCandidatesServiceTests|FullyQualifiedName~ReplicationPreflightEstimatorTests"
```

Expected: FAIL.

- [ ] **Step 5: Implement transactional finalization**

Keep draft validation read-only. Before opening the finalization transaction, calculate `resource count × selected account count`, estimate SQLite row/index/WAL growth from a documented conservative per-row factor, query free space through `IDiskSpaceProbe`, and return a confirmation summary or hard storage error. In one unit-of-work transaction, write resources and replication operations in bounded parameter batches, create allocations, mark import committed, and write an audit event; any failure rolls the transaction back so no partial batch becomes visible. Appends use a separate confirmed import session and never reopen the original draft.

- [ ] **Step 6: Verify and commit**

```powershell
dotnet test tests/BaiduBatchTransfer.Application.Tests
git add src/BaiduBatchTransfer.Core src/BaiduBatchTransfer.Application src/BaiduBatchTransfer.Infrastructure tests/BaiduBatchTransfer.Application.Tests
git commit -m "feat: create and extend transfer batches"
```

### Task 12: Implement the single-coordinator scheduler

**Files:**
- Create: `src/BaiduBatchTransfer.Application/Scheduling/TransferCoordinator.cs`
- Create: `src/BaiduBatchTransfer.Application/Scheduling/OperationExecutor.cs`
- Create: `src/BaiduBatchTransfer.Application/Scheduling/RetryPolicy.cs`
- Create: `src/BaiduBatchTransfer.Application/Scheduling/AccountDailyLimitService.cs`
- Create: `src/BaiduBatchTransfer.Application/Scheduling/NetworkExecutionGate.cs`
- Create: `src/BaiduBatchTransfer.Application/Scheduling/AllocationTransactionService.cs`
- Create: `src/BaiduBatchTransfer.Application/Jobs/ReassignRemainingWorkService.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/TransferCoordinatorTests.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/AccountDailyLimitServiceTests.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/RetryPolicyTests.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Jobs/ReassignRemainingWorkServiceTests.cs`

- [ ] **Step 1: Write failing coordinator tests**

Use a fake adapter and fake clock. Verify one active batch, at most one active operation per account, global maximum two, serial allocation ordinals, exclusive-before-public candidates, result-pending slot retention, other-account continuation, and no new work while offline. Verify the executor idempotently ensures the snapshotted target path (including optional outer directory) before save, passes the returned directory identity to save, and classifies directory creation errors without attempting transfer.

- [ ] **Step 2: Write failing daily/retry tests**

Verify count increments once on first persisted submit, not on retry/restart/confirmation; local-date reset; three transient retries with clock-controlled backoff; no retry for global invalid or definite account/path errors; server wait duration honored. For explicit account-failure reassignment, verify preview is read-only and shows old/new targets, deltas, and affected count; task-quota mode moves only never-submitted operations, success-target mode changes only unreserved candidates/targets; submitted, result-pending, fulfilled, and account-exclusive resources never move implicitly. Applying requires a user-confirmation token plus current batch version and is rejected on stale state.

- [ ] **Step 3: Run and observe failure**

```powershell
dotnet test tests/BaiduBatchTransfer.Application.Tests --filter "FullyQualifiedName~Scheduling|FullyQualifiedName~RetryPolicy"
dotnet test tests/BaiduBatchTransfer.Application.Tests --filter FullyQualifiedName~ReassignRemainingWorkServiceTests
```

Expected: FAIL.

- [ ] **Step 4: Implement coordinator and atomic allocation**

The coordinator alone assigns candidates. In one SQLite transaction check slot availability, select candidate, reserve it, create operation, increment `ReservedSlotCount`, and assign `AllocationOrdinal`. Dispatch the committed operation to a bounded worker channel of capacity two. `OperationExecutor` derives the complete destination from immutable resource/account snapshots, calls idempotent `EnsureDirectoryAsync`, then saves only to the ensured remote directory. Persist the `OperationConfirmationDescriptor` and submit state before the save request boundary, then persist any returned adapter request/remote IDs before marking a result. Implement reassignment as a separate preview/apply command; the scheduler only pauses unavailable accounts and never invokes reassignment automatically.

- [ ] **Step 5: Verify and commit**

```powershell
dotnet test tests/BaiduBatchTransfer.Application.Tests
git add src/BaiduBatchTransfer.Application tests/BaiduBatchTransfer.Application.Tests
git commit -m "feat: add persistent transfer scheduler"
```

### Task 13: Implement crash recovery and uncertain-result reconciliation

**Files:**
- Create: `src/BaiduBatchTransfer.Core/Abstractions/ISingleInstanceGuard.cs`
- Create: `src/BaiduBatchTransfer.Application/Scheduling/StartupRecoveryService.cs`
- Create: `src/BaiduBatchTransfer.Application/Scheduling/OperationReconciliationService.cs`
- Create: `src/BaiduBatchTransfer.Application/Scheduling/AllocationCounterRebuilder.cs`
- Create: `src/BaiduBatchTransfer.Application/Jobs/CancelOperationService.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/CrashBoundaryTests.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/ReconciliationTests.cs`

- [ ] **Step 1: Write parameterized crash-boundary tests**

Simulate process death after lease, reservation, confirmation-descriptor persistence, preparing, submitting, adapter response, response-ID persistence, and before final outcome persistence. Verify pre-submit leases recover to waiting, any state that may have crossed submit recovers to result pending with a reconstructible confirmation query, pending retains its slot, and allocation counters rebuild from operation rows.

- [ ] **Step 2: Write failing reconciliation/cancellation tests**

Verify a persisted descriptor alone can yield confirmed present/absent/ambiguous; optional response identifiers strengthen but are not required for querying. Confirmed remote presence converts reserved to fulfilled; confirmed absent releases the slot only when the adapter can prove absence for the submit window; ambiguous stays pending. User retry warns and reuses the same operation row; cancellation is rejected once submit may have occurred.

- [ ] **Step 3: Run and observe failure**

```powershell
dotnet test tests/BaiduBatchTransfer.Application.Tests --filter "FullyQualifiedName~CrashBoundaryTests|FullyQualifiedName~ReconciliationTests"
```

Expected: FAIL.

- [ ] **Step 4: Implement recovery and reconciliation**

At startup call an `ISingleInstanceGuard` abstraction before database access, run integrity/migrations, expire safe leases, convert unsafe states to pending, rebuild counters, and only then allow coordinator start. Use a deterministic fake guard in these tests; Task 17 supplies the concrete Windows mutex/named-pipe implementation. Rebuild `TransferConfirmationQuery` from the persisted operation descriptor and optional adapter IDs. Reconciliation never infers success or absence from a timeout alone; an unprovable state remains pending for user action rather than being automatically submitted again.

- [ ] **Step 5: Verify and commit**

```powershell
dotnet test tests/BaiduBatchTransfer.Application.Tests
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests
git add src tests
git commit -m "feat: recover and reconcile interrupted transfers"
```

### Task 14: Implement precheck, paging, and result-query services

**Files:**
- Create: `src/BaiduBatchTransfer.Application/Imports/BulkPrecheckService.cs`
- Create: `src/BaiduBatchTransfer.Application/Imports/InvalidatePrecheckService.cs`
- Create: `src/BaiduBatchTransfer.Application/Imports/ImportPageQuery.cs`
- Create: `src/BaiduBatchTransfer.Application/Jobs/JobPageQuery.cs`
- Create: `src/BaiduBatchTransfer.Application/Jobs/BatchStatisticsQuery.cs`
- Create: `src/BaiduBatchTransfer.Application/Jobs/ExportBatchResultsService.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Imports/BulkPrecheckServiceTests.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Jobs/PagingAndStatisticsTests.cs`

- [ ] **Step 1: Write failing precheck tests**

Verify a user-selected precheck account, low concurrency, persisted progress, pause on login/verification, invalidation after URL/code edit, no effect on transfer daily count, and no silent precheck-account switching.

- [ ] **Step 2: Write failing query tests**

Seed 200,000 fake resources and operations. Verify page size bounds, indexed filters, separate resource/operation counts, per-account new/existing/failed/pending counts, and exports that do not load every row at once.

- [ ] **Step 3: Run and observe failure**

```powershell
dotnet test tests/BaiduBatchTransfer.Application.Tests --filter "FullyQualifiedName~BulkPrecheck|FullyQualifiedName~PagingAndStatistics"
```

Expected: FAIL.

- [ ] **Step 4: Implement application queries/services**

Use cursor or indexed keyset paging for large result sets. Bulk precheck writes per-row outcomes in bounded transactions and stops when its account needs user action. Result export streams query pages through `OpenXmlResultWriter`.

- [ ] **Step 5: Verify and commit**

```powershell
dotnet test tests/BaiduBatchTransfer.Application.Tests
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests
git add src tests
git commit -m "feat: add precheck paging and result queries"
```

### Task 15: Build the WPF shell and task-creation wizard

**Files:**
- Modify: `src/BaiduBatchTransfer.App/App.xaml`
- Modify: `src/BaiduBatchTransfer.App/App.xaml.cs`
- Modify: `src/BaiduBatchTransfer.App/MainWindow.xaml`
- Modify: `src/BaiduBatchTransfer.App/MainWindow.xaml.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Imports/ImportView.xaml`
- Create: `src/BaiduBatchTransfer.App/Features/Imports/ImportViewModel.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Jobs/CreateJobWizardView.xaml`
- Create: `src/BaiduBatchTransfer.App/Features/Jobs/CreateJobWizardViewModel.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Jobs/AccountAllocationRowViewModel.cs`
- Create: `src/BaiduBatchTransfer.App/Services/DialogService.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/CreateJobWizardViewModelTests.cs`

- [ ] **Step 1: Write failing wizard ViewModel tests**

Verify adaptive steps, replication/distribution mode switching, one-account default, Excel-account conflict prompt, task-quota total validation, success-target/reserve warnings, operation-count preview, proportional `1:2:1` shortcuts, and final confirmation gating.

- [ ] **Step 2: Run and observe failure**

```powershell
dotnet test tests/BaiduBatchTransfer.App.Tests --filter FullyQualifiedName~CreateJobWizardViewModelTests
```

Expected: FAIL.

- [ ] **Step 3: Implement the MVVM shell**

Configure `Microsoft.Extensions.Hosting` and DI in `App.xaml.cs`. Use CommunityToolkit commands/properties. The wizard steps are Import, optional Precheck, Destination, Account/Quota, Confirmation. Use database paging/virtualization for preview; never bind all resources.

- [ ] **Step 4: Implement quota confirmation UI**

Show account status, remaining capacity, locked/exclusive/public candidate counts, original/effective success targets, reserved/fulfilled counts, resource count, operation count, and shortage policy. Disable Continue until the selected mode's invariant passes.

- [ ] **Step 5: Verify UI logic and build**

```powershell
dotnet test tests/BaiduBatchTransfer.App.Tests
dotnet build -c Debug
```

Expected: ViewModel tests and build pass.

- [ ] **Step 6: Commit**

```powershell
git add src/BaiduBatchTransfer.App tests/BaiduBatchTransfer.App.Tests
git commit -m "feat: add import and batch creation ui"
```

### Task 16: Build account pool and task-center UI

**Files:**
- Create: `src/BaiduBatchTransfer.App/Features/Accounts/AccountPoolView.xaml`
- Create: `src/BaiduBatchTransfer.App/Features/Accounts/AccountPoolViewModel.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Accounts/ManualCookieDialog.xaml`
- Create: `src/BaiduBatchTransfer.App/Features/Accounts/AccountRuleEditorView.xaml`
- Create: `src/BaiduBatchTransfer.App/Features/Accounts/AccountRuleEditorViewModel.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Jobs/TaskCenterView.xaml`
- Create: `src/BaiduBatchTransfer.App/Features/Jobs/TaskCenterViewModel.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Jobs/ResultPendingDialog.xaml`
- Create: `src/BaiduBatchTransfer.App/Features/Jobs/AppendCandidatesDialog.xaml`
- Create: `src/BaiduBatchTransfer.App/Features/Jobs/ReassignRemainingWorkDialog.xaml`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/AccountPoolViewModelTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/TaskCenterViewModelTests.cs`

- [ ] **Step 1: Write failing account-pool tests**

Verify QR login default, masked Cookie input, duplicate identity rejection, capacity/status display, daily submitted/success counts, disable/tombstone behavior, and profile deletion confirmation. Verify account rules can add/edit/disable normalized directory-prefix bindings, reject duplicate priority ties for the same prefix, preview matching counts against the current import, and persist an explicit ordered priority.

- [ ] **Step 2: Write failing task-center tests**

Verify one-active-batch controls, reorder queue, pause/resume/cancel permissions, per-account pause, result-pending warnings, append/reopen flow, filters, paged rows, retry actions, and export command. When an account is unavailable, verify “重新分配剩余任务” first shows old/new targets, delta, affected never-submitted count, immovable count, and a second confirmation; no apply command is emitted when the preview is cancelled or stale.

- [ ] **Step 3: Run and observe failure**

```powershell
dotnet test tests/BaiduBatchTransfer.App.Tests --filter "FullyQualifiedName~AccountPool|FullyQualifiedName~TaskCenter"
```

Expected: FAIL.

- [ ] **Step 4: Implement views and commands**

All destructive actions require exact-scope confirmation. Bind reassignment to the Task 12 preview/apply service and label account-rule-locked or already-submitted work as immovable. Do not display complete Cookie or reusable headers. Keep progress updates throttled so high-frequency operation changes do not saturate the UI thread.

- [ ] **Step 5: Verify and commit**

```powershell
dotnet test tests/BaiduBatchTransfer.App.Tests
dotnet build -c Debug
git add src/BaiduBatchTransfer.App tests/BaiduBatchTransfer.App.Tests
git commit -m "feat: add account pool and task center"
```

### Task 17: Add Windows single-instance, tray, network, and shutdown behavior

**Files:**
- Create: `src/BaiduBatchTransfer.Infrastructure/SystemIntegration/SingleInstanceService.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/SystemIntegration/NetworkStatusService.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/SystemIntegration/PowerStatusService.cs`
- Create: `src/BaiduBatchTransfer.App/Services/TrayIconService.cs`
- Create: `src/BaiduBatchTransfer.App/Services/SafeShutdownService.cs`
- Create: `src/BaiduBatchTransfer.App/Services/ClearAllLocalDataService.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Recovery/DatabaseRecoveryWindow.xaml`
- Create: `src/BaiduBatchTransfer.App/Features/Recovery/DatabaseRecoveryViewModel.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Settings/BehaviorSettingsViewModel.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Settings/DataManagementViewModel.cs`
- Test: `tests/BaiduBatchTransfer.Infrastructure.Tests/SystemIntegration/SystemGateTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Services/SafeShutdownServiceTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Services/ClearAllLocalDataServiceTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/DatabaseRecoveryViewModelTests.cs`

- [ ] **Step 1: Write failing system-gate tests**

Verify the named mutex allows one owner, second activation signals the first process, offline stops new allocation, wake requires network/session revalidation, and safe exit stops intake before waiting on in-flight state persistence. Verify a failed integrity check opens recovery instead of the main window; recovery lists only validated backups, requires confirmation before restore, and can export the sanitized diagnostic ZIP. Verify “清除全部本地数据” requires the exact confirmation phrase, pauses/stops scheduling, closes WebView2/database/log handles, deletes only canonical app-owned database/backup/log/profile/settings paths, preserves installer binaries, and exits for a clean restart.

- [ ] **Step 2: Run and observe failure**

```powershell
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests --filter FullyQualifiedName~SystemGateTests
dotnet test tests/BaiduBatchTransfer.App.Tests --filter "FullyQualifiedName~SafeShutdownServiceTests|FullyQualifiedName~ClearAllLocalDataServiceTests|FullyQualifiedName~DatabaseRecoveryViewModelTests"
```

Expected: FAIL.

- [ ] **Step 3: Implement Windows behavior**

Use a per-user named mutex plus a small named-pipe activation signal. Expose network/power changes through interfaces so application tests remain deterministic. After acquiring the mutex and before starting the coordinator, route failed integrity to `DatabaseRecoveryWindow`; successful restore must pass migration and `quick_check` before normal startup. On close, prompt for tray continuation or safe pause/exit; persist the remembered preference. Implement clear-all as a coordinated shutdown over an injected, canonical allowlist of app-data paths—no globs or parent-directory deletion—and require typed confirmation. Never abandon an operation between submit-state persistence and request dispatch without marking it recoverable.

- [ ] **Step 4: Verify and commit**

```powershell
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests
dotnet test tests/BaiduBatchTransfer.App.Tests
git add src tests
git commit -m "feat: add windows lifecycle integration"
```

### Task 18: Add sanitized logging, retention, and performance benchmarks

**Files:**
- Create: `src/BaiduBatchTransfer.Application/Jobs/ExecutionLogService.cs`
- Create: `src/BaiduBatchTransfer.Application/Jobs/LogRetentionService.cs`
- Create: `src/BaiduBatchTransfer.Core/Services/SensitiveTextSanitizer.cs`
- Create: `benchmarks/BaiduBatchTransfer.Benchmarks/ImportBenchmark.cs`
- Create: `benchmarks/BaiduBatchTransfer.Benchmarks/AllocationBenchmark.cs`
- Create: `benchmarks/BaiduBatchTransfer.Benchmarks/OperationPagingBenchmark.cs`
- Test: `tests/BaiduBatchTransfer.Core.Tests/Services/SensitiveTextSanitizerTests.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/Jobs/LogRetentionServiceTests.cs`

- [ ] **Step 1: Write failing sanitizer/retention tests**

Cover Cookie/header/token patterns, query signatures, personal IDs, 30-day retention, 500 MB cap, oldest-completed-detail deletion, and preservation of batch summaries/final results.

- [ ] **Step 2: Run and observe failure**

```powershell
dotnet test tests/BaiduBatchTransfer.Core.Tests --filter FullyQualifiedName~SensitiveTextSanitizerTests
dotnet test tests/BaiduBatchTransfer.Application.Tests --filter FullyQualifiedName~LogRetentionServiceTests
```

Expected: FAIL.

- [ ] **Step 3: Implement sanitized structured logs and retention**

Store stable event codes and sanitized messages. Never store request/response bodies. Retention runs only when the scheduler is idle or paused and deletes in bounded transactions.

- [ ] **Step 4: Implement benchmark scenarios**

Generate deterministic data at runtime. Measure 200,000-row Open XML import, 50k/100k/50k allocation, three-account replication expansion, keyset page query, and streamed export. Record wall time and peak working set.

- [ ] **Step 5: Run acceptance benchmarks**

```powershell
dotnet run -c Release --project benchmarks/BaiduBatchTransfer.Benchmarks -- --scenario all --rows 200000
```

Expected on the agreed 4-core/8 GB/SSD baseline: import under 5 minutes, peak working set below 1 GB, first page under 1 second, and no benchmark failure exit code. If the current machine differs, record hardware/results and run the same scenario later on the baseline machine.

- [ ] **Step 6: Commit**

```powershell
git add src tests benchmarks
git commit -m "perf: add safe logging and large batch benchmarks"
```

### Task 19: Package the self-contained x64 application

**Files:**
- Create: `installer/BaiduBatchTransfer.Installer.wixproj`
- Create: `installer/Package.wxs`
- Create: `src/BaiduBatchTransfer.App/app.manifest`
- Create: `src/BaiduBatchTransfer.App/Services/WebView2RuntimeCheck.cs`
- Create: `THIRD-PARTY-NOTICES.md`
- Create: `scripts/build-release.ps1`
- Test: `tests/BaiduBatchTransfer.App.Tests/Services/WebView2RuntimeCheckTests.cs`

- [ ] **Step 1: Write failing runtime-check tests**

Abstract runtime detection. Verify available runtime continues, missing runtime blocks login and exposes only the official Microsoft install URL, and no silent third-party download occurs.

- [ ] **Step 2: Run and observe failure**

```powershell
dotnet test tests/BaiduBatchTransfer.App.Tests --filter FullyQualifiedName~WebView2RuntimeCheckTests
```

Expected: FAIL.

- [ ] **Step 3: Implement publish and installer**

Publish `win-x64`, self-contained, single-file only if WebView2/native dependencies remain reliable. Use WiX Toolset SDK to package the publish directory per-user without administrator rights. Add uninstall entries and preserve user data on ordinary uninstall unless the user explicitly selects removal.

`scripts/build-release.ps1` must run restore locked, build, tests, publish, license inventory, and MSI build in that order. It accepts an optional signing certificate configuration; public distribution is blocked when signing is required but unavailable.

- [ ] **Step 4: Verify release build**

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-release.ps1 -Configuration Release
```

Expected: tests pass; self-contained app and MSI are produced; `THIRD-PARTY-NOTICES.md` lists every distributed dependency license.

- [ ] **Step 5: Test install/uninstall on clean Windows 10 and 11 x64**

Verify install, first launch, WebView2 missing-runtime message, upgrade over prior build with database migration, ordinary uninstall preserving data, explicit in-app data deletion, and no administrator prompt for the normal per-user path.

- [ ] **Step 6: Commit**

```powershell
git add installer src/BaiduBatchTransfer.App scripts THIRD-PARTY-NOTICES.md tests/BaiduBatchTransfer.App.Tests
git commit -m "build: package windows x64 client"
```

### Task 20: Run full end-to-end verification

**Files:**
- Create: `docs/verification/first-release-checklist.md`
- Create: `docs/verification/first-release-results.md`
- Modify only if failures require fixes: relevant production/test files

- [ ] **Step 1: Run the automated suite from a clean restore**

```powershell
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
git diff --check
```

Expected: zero build errors, warnings treated as errors, zero failed tests, clean diff check.

- [ ] **Step 2: Run fake-adapter end-to-end scenarios**

Verify text and Excel import, conflict correction, replication, task-quota distribution, success-target proportional shortage, priority shortage, wait/append/resume, account verification pause, result pending, crash restart, export, and tombstone account history.

- [ ] **Step 3: Run bounded live-account scenarios**

With user confirmation and legal test resources, execute a small batch only. Verify QR and manual Cookie login, separate profiles, per-account directories, whole-share save, existing-resource handling, invalid code/link, session expiry, and user-handled verification. Do not manufacture high-frequency risk-control events.

- [ ] **Step 4: Run the large simulated benchmark and release installer**

```powershell
dotnet run -c Release --project benchmarks/BaiduBatchTransfer.Benchmarks -- --scenario all --rows 200000
powershell -ExecutionPolicy Bypass -File scripts/build-release.ps1 -Configuration Release
```

Record machine specifications, timings, peak memory, artifact hashes, test counts, and any accepted limitations in `docs/verification/first-release-results.md`.

- [ ] **Step 5: Review requirements line by line**

Map all 23 acceptance criteria from the spec to an automated test, manual check, benchmark result, or explicit blocker. Do not mark the release complete while any criterion lacks fresh evidence.

- [ ] **Step 6: Commit final verification evidence**

```powershell
git add docs/verification
git commit -m "test: record first release verification"
```

## Requirements traceability

| Spec acceptance criterion | Primary implementation tasks | Verification |
|---|---|---|
| 1 | 4, 9, 16 | Login/profile/account lifecycle tests + bounded live login |
| 2 | 10, 14, 18 | Streaming import/paging tests + 200,000-row benchmark |
| 3 | 10, 11, 14 | Staging/conflict/precheck/finalization integration tests |
| 4 | 7, 11 | Replication expansion and 1,000 × 3 test |
| 5 | 7, 11, 16 | 50k/100k/50k, row lock, directory-rule tests |
| 6 | 7, 11, 12 | Success refill and three shortage-policy tests |
| 7 | 7, 11, 15 | Quota invariant tests and wizard gate |
| 8 | 14, 15 | Precheck invalidation/recalculation tests |
| 9 | 6, 11, 15 | Path normalization/snapshot/wizard tests |
| 10 | 3, 4, 15 | Duplicate-policy contract and live adapter gate |
| 11 | 4, 14, 15 | Edge validation and bulk precheck paths |
| 12 | 12, 13, 16, 17 | Lifecycle, recovery, cancellation, export tests |
| 13 | 3, 8, 12, 13 | Persisted confirmation-descriptor crash matrix |
| 14 | 12 | Fake-clock daily limit/retry/restart tests |
| 15 | 4, 12, 16 | Verification/login pause classifier and UI tests |
| 16 | 9, 10, 18 | DPAPI, sanitizer, formula-injection tests |
| 17 | 8, 18 | Migration backup/restore, WAL, corruption, retention tests |
| 18 | 3, 4 | Adapter boundary contract and spike documentation |
| 19 | 10, 14, 18 | Large-data benchmark suite |
| 20 | 10, 11 | Snapshot finalization and cleaned-staging test |
| 21 | 5, 7, 12, 13 | Slot invariants and crash-boundary tests |
| 22 | 7 | Deterministic rounding/order replay tests |
| 23 | 11, 16 | Append/reopen state and confirmation tests |

Additional mandatory spec paths: account rules and user-confirmed reassignment are covered by Tasks 7, 12, and 16; database restore/diagnostic export by Tasks 8 and 17; replication disk preflight by Task 11; clear-all-local-data by Task 17.

## Final handoff checklist

- All automated tests and benchmarks have fresh passing output.
- The adapter go/no-go gate passed with sanitized evidence.
- No secret, personal content, or reusable session value is tracked by Git.
- User data survives upgrade; account removal clears session/profile but preserves history.
- Public installer is signed before distribution.
- Use `superpowers:requesting-code-review` before integration.
- Use `superpowers:finishing-a-development-branch` after all review findings are resolved.
