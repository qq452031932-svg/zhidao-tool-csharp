# Account Capacity Refresh Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refresh a logged-in Baidu account's used and total capacity after login and whenever its left-rail account card becomes selected, without adding a workspace, navigation item, or scan button.

**Architecture:** Add a fail-closed read-only capacity call to the existing live Baidu adapter, persist only validated capacity through an identity-guarded repository update, and expose a small application service that returns presentation-safe outcomes. The shell listens to account selection changes, coalesces duplicate in-flight requests per account, updates the matching row only, and refreshes newly added accounts once after login.

**Tech Stack:** .NET 8, C#, WPF/MVVM, CommunityToolkit.Mvvm, SQLite, xUnit

---

## File map

- Modify `src/BaiduBatchTransfer.BaiduAdapter/Http/BaiduSessionIdentityClient.cs`: add the bounded same-origin capacity read used by the live read-only adapter.
- Modify `src/BaiduBatchTransfer.BaiduAdapter/Http/LiveBaiduNetdiskAdapter.cs`: delegate `GetCapacityAsync` to the validated capacity read while leaving every write method fail-closed.
- Modify `src/BaiduBatchTransfer.Core/Abstractions/IAccountRepository.cs`: define an identity-guarded capacity update.
- Modify `src/BaiduBatchTransfer.Infrastructure/Database/Repositories/SqliteAccountRepository.cs`: persist capacity without overwriting unrelated account fields.
- Create `src/BaiduBatchTransfer.Application/Accounts/AccountCapacityRefreshService.cs`: load the bound session, query capacity, persist success, and return safe typed outcomes.
- Modify `src/BaiduBatchTransfer.App/Features/Jobs/AccountAllocationRowViewModel.cs`: support applying refreshed capacity and publish a single `已用 / 总容量` string.
- Modify `src/BaiduBatchTransfer.App/MainWindowViewModel.cs`: observe selection transitions, coalesce in-flight scans, and trigger one scan for newly added accounts after login.
- Modify `src/BaiduBatchTransfer.App/MainWindow.xaml`: replace the current remaining/used pair with one `已用 / 总容量` line and add no control.
- Modify `src/BaiduBatchTransfer.App/AppBootstrap.cs`: register and inject the refresh service.
- Modify/add the focused test files named in the tasks below.

### Task 1: Read and validate live capacity

**Files:**
- Modify: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduSessionIdentityClientTests.cs`
- Modify: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/LiveAdapterAvailabilityTests.cs`
- Modify: `src/BaiduBatchTransfer.BaiduAdapter/Http/BaiduSessionIdentityClient.cs`
- Modify: `src/BaiduBatchTransfer.BaiduAdapter/Http/LiveBaiduNetdiskAdapter.cs`

- [ ] **Step 1: Write failing capacity response tests**

Add tests proving that `GetCapacityAsync` requests HTTPS `pan.baidu.com/api/quota`, sends only applicable cookies, accepts non-negative integer `total` and `used`, and returns `AccountCapacity(total, used)`.

- [ ] **Step 2: Write failing rejection/error tests**

Cover missing fields, negative values, `used > total`, malformed JSON, oversized responses, cancellation, 401, 403, 429, and 5xx. Assert typed errors and that safe messages do not contain Cookie values or response bodies.

- [ ] **Step 3: Run the focused adapter tests and observe the expected failure**

Run:

```powershell
dotnet test tests\BaiduBatchTransfer.BaiduAdapter.Tests\BaiduBatchTransfer.BaiduAdapter.Tests.csproj --no-restore --filter "FullyQualifiedName~BaiduSessionIdentityClientTests|FullyQualifiedName~LiveAdapterAvailabilityTests" --verbosity minimal
```

Expected: capacity assertions fail because `LiveBaiduNetdiskAdapter.GetCapacityAsync` still returns `NotValidated`.

- [ ] **Step 4: Implement the minimal bounded capacity read**

Reuse the identity client's existing cookie applicability rules, HTTPS/same-origin request construction, redirect prohibition, response byte limit, cancellation behavior, and HTTP status mapping. Parse only `errno`, `total`, and `used`; reject every incomplete or inconsistent response.

- [ ] **Step 5: Re-run the focused adapter tests**

Expected: all selected tests pass; transfer and share methods remain explicitly fail-closed.

### Task 2: Persist capacity without overwriting account state

**Files:**
- Modify: `src/BaiduBatchTransfer.Core/Abstractions/IAccountRepository.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/Repositories/SqliteAccountRepository.cs`
- Modify: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/RepositoryTests.cs`

- [ ] **Step 1: Write a failing repository test**

Seed an active account, call the new method with its local ID and expected Baidu user ID, and assert only `CapacityTotal`, `CapacityUsed`, and `LastValidatedAt` change.

- [ ] **Step 2: Add failing stale/deleted account cases**

Assert the update returns false for a mismatched Baidu user ID, disabled/deleted account, or missing account and preserves every existing value.

- [ ] **Step 3: Run the focused repository tests and observe the expected compile/test failure**

Run:

```powershell
dotnet test tests\BaiduBatchTransfer.Infrastructure.Tests\BaiduBatchTransfer.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~RepositoryTests" --verbosity minimal
```

- [ ] **Step 4: Add `TryUpdateCapacityAsync`**

Use one guarded SQLite update:

```sql
UPDATE Accounts
SET CapacityTotal=$total, CapacityUsed=$used, LastValidatedAt=$validated
WHERE Id=$id AND BaiduUserId=$user AND Status='Active' AND IsEnabled=1 AND IsDeleted=0;
```

Return true only when exactly one row changes.

- [ ] **Step 5: Re-run the focused repository tests**

Expected: all selected tests pass.

### Task 3: Add the application capacity refresh service

**Files:**
- Create: `src/BaiduBatchTransfer.Application/Accounts/AccountCapacityRefreshService.cs`
- Create: `tests/BaiduBatchTransfer.Application.Tests/Accounts/AccountCapacityRefreshServiceTests.cs`

- [ ] **Step 1: Write failing success and preservation tests**

Specify a typed result carrying `AccountId`, disposition, optional capacity, and safe Chinese message. Assert a valid bound session invokes `IBaiduNetdiskAdapter.GetCapacityAsync`, commits the guarded update, and returns the validated values.

- [ ] **Step 2: Write failing failure-path tests**

Cover missing/unprotectable session, account identity mismatch, deleted/disabled account, login expiry, verification requirement, transient service failure, failed guarded update, cancellation, and exceptions. Assert no failed request writes capacity and no sensitive diagnostic escapes.

- [ ] **Step 3: Run the focused application tests and observe the expected failure**

Run:

```powershell
dotnet test tests\BaiduBatchTransfer.Application.Tests\BaiduBatchTransfer.Application.Tests.csproj --no-restore --filter "FullyQualifiedName~AccountCapacityRefreshServiceTests" --verbosity minimal
```

- [ ] **Step 4: Implement the minimal service**

Load the account session through `IBaiduSessionStore`, require the local account binding, query the coordinated read-only adapter, persist through `TryUpdateCapacityAsync`, and map expected failures to stable Chinese messages. Let caller cancellation propagate.

- [ ] **Step 5: Re-run the focused application tests**

Expected: all selected tests pass.

### Task 4: Present one-line capacity and refresh on selection/login

**Files:**
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/AccountAllocationRowViewModelTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/MainWindowViewModelTests.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Jobs/AccountAllocationRowViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/MainWindowViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/MainWindow.xaml`

- [ ] **Step 1: Write failing row presentation tests**

Assert `CapacitySummaryText` is `已用 512 GB / 2 TB`, incomplete data is `容量未知`, and `ApplyCapacity` updates the values and raises notifications for all dependent properties.

- [ ] **Step 2: Write failing shell trigger tests**

Assert: false-to-true selection requests one refresh; repeated true does not; deselect/reselect requests once more; duplicate requests while one is active are coalesced; different accounts are independent; a late response updates only the same still-present account row; and a newly added account is refreshed once after successful login/account reload.

- [ ] **Step 3: Write the failing XAML structure assertion**

Assert the account card binds `CapacitySummaryText`, no longer renders the old remaining/used pair, and contains no `空间扫描` button or navigation item.

- [ ] **Step 4: Run the focused App tests and observe the expected failure**

Run:

```powershell
dotnet test tests\BaiduBatchTransfer.App.Tests\BaiduBatchTransfer.App.Tests.csproj --no-restore --filter "FullyQualifiedName~AccountAllocationRowViewModelTests|FullyQualifiedName~MainWindowViewModelTests" --verbosity minimal
```

- [ ] **Step 5: Implement mutable capacity presentation**

Store capacity in private fields, add `CapacityTotalText` and `CapacitySummaryText`, and make `ApplyCapacity(AccountCapacity)` update the fields with dependent notifications.

- [ ] **Step 6: Implement selection/login coordination**

Subscribe to current `Accounts` rows and collection changes. On an `IsSelected` transition to true, start one per-account request; remove the in-flight entry in `finally`. After login refresh, compare account IDs before/after and request capacity for newly added rows once. Before applying a result, locate the current row by account ID so replaced/removed rows cannot receive stale data. Unsubscribe and cancel through the shell lifetime on close.

- [ ] **Step 7: Replace the account-card capacity markup**

Render one line bound to `CapacitySummaryText`, preserving the existing card size, selection styling, accessibility name, and no-button interaction.

- [ ] **Step 8: Re-run the focused App tests**

Expected: all selected tests pass.

### Task 5: Wire production dependencies and verify the slice

**Files:**
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs`

- [ ] **Step 1: Write the failing composition test**

Assert `AccountCapacityRefreshService` resolves from the host and `AppShellFactory` passes it to the main shell without enabling any transfer/share write capability.

- [ ] **Step 2: Run the focused composition tests and observe the expected failure**

Run:

```powershell
dotnet test tests\BaiduBatchTransfer.App.Tests\BaiduBatchTransfer.App.Tests.csproj --no-restore --filter "FullyQualifiedName~AppBootstrapTests" --verbosity minimal
```

- [ ] **Step 3: Register and inject the service**

Use the existing `IBaiduSessionStore`, coordinated `IBaiduNetdiskAdapter`, `IUnitOfWorkFactory`, and `TimeProvider`. Do not change remote write mode or capability gates.

- [ ] **Step 4: Re-run all capacity-focused tests**

Run:

```powershell
dotnet test tests\BaiduBatchTransfer.BaiduAdapter.Tests\BaiduBatchTransfer.BaiduAdapter.Tests.csproj --no-restore --filter "FullyQualifiedName~BaiduSessionIdentityClientTests|FullyQualifiedName~LiveAdapterAvailabilityTests" --verbosity minimal
dotnet test tests\BaiduBatchTransfer.Infrastructure.Tests\BaiduBatchTransfer.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~RepositoryTests" --verbosity minimal
dotnet test tests\BaiduBatchTransfer.Application.Tests\BaiduBatchTransfer.Application.Tests.csproj --no-restore --filter "FullyQualifiedName~AccountCapacityRefreshServiceTests" --verbosity minimal
dotnet test tests\BaiduBatchTransfer.App.Tests\BaiduBatchTransfer.App.Tests.csproj --no-restore --filter "FullyQualifiedName~AccountAllocationRowViewModelTests|FullyQualifiedName~MainWindowViewModelTests|FullyQualifiedName~AppBootstrapTests" --verbosity minimal
```

- [ ] **Step 5: Run broad regression and build verification**

Run:

```powershell
dotnet test BaiduBatchTransfer.sln --no-restore --verbosity minimal
dotnet build BaiduBatchTransfer.sln --no-restore --verbosity minimal
git diff --check
```

Expected: zero failed tests, zero build errors, and no whitespace errors introduced by this slice.

- [ ] **Step 6: Inspect the final diff against the approved design**

Confirm there is no directory scan, no new page, no new navigation item, no manual scan button, and no change to batch-transfer write authorization.
