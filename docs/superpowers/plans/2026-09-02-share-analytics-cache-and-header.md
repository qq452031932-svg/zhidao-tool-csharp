# Share Analytics Cache and Header Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the share analytics workspace render a flat highlighted sort header, restore the last viewed account, and use a complete SQLite snapshot until the cache reaches 23 hours or the user manually refreshes.

**Architecture:** Keep remote synchronization in `AccountShareAnalyticsSyncService` and make cache freshness a presentation-workspace policy in `ShareAnalyticsViewModel`. Persist the last viewed account through a focused SQLite preference store. Automatic refresh uses one cancellable one-shot delay per active account, never polling, and complete-sync atomic commit remains the only way cached data changes.

**Tech Stack:** .NET 8, WPF, CommunityToolkit.Mvvm, SQLite, xUnit

---

## File map

- `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsViewModel.cs`: cache-age policy, one-shot refresh scheduling, cache status copy, sort state.
- `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsContracts.cs`: delay abstraction and row-facing sort metadata.
- `src/BaiduBatchTransfer.App/Features/ShareAnalytics/SystemShareAnalyticsDelay.cs`: production cancellable one-shot delay.
- `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsView.xaml`: flat headers, highlighted sorted column, cache summary.
- `src/BaiduBatchTransfer.Core/Abstractions/IShareAnalyticsPreferenceStore.cs`: account preference persistence boundary.
- `src/BaiduBatchTransfer.Infrastructure/Database/Migrations/023_ShareAnalyticsPreferences.sql`: singleton last-viewed-account storage.
- `src/BaiduBatchTransfer.Infrastructure/Database/Repositories/SqliteShareAnalyticsPreferenceStore.cs`: SQLite preference implementation.
- `src/BaiduBatchTransfer.Infrastructure/Database/DatabaseMigrator.cs`: schema version 23.
- `src/BaiduBatchTransfer.App/AppBootstrap.cs`: DI wiring, restore preference during shell creation, save account changes.
- `src/BaiduBatchTransfer.App/MainWindowViewModel.cs`: choose the restored account and persist later selections.
- `tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewModelTests.cs`: cache freshness, delay cancellation, status and sort tests.
- `tests/BaiduBatchTransfer.App.Tests/MainWindowViewModelTests.cs`: restored-account selection and persistence tests.
- `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/MigrationTests.cs`: v23 schema test.
- `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/ShareAnalyticsPreferenceStoreTests.cs`: preference round-trip and deleted-account behavior.
- `docs/testing/2026-09-01.md`: final build, tests, artifact and visual acceptance evidence.

### Task 1: Cache freshness and one-shot automatic refresh

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsContracts.cs`
- Create: `src/BaiduBatchTransfer.App/Features/ShareAnalytics/SystemShareAnalyticsDelay.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewModelTests.cs`

- [ ] **Step 1: Write failing tests for cached, empty, stale and delayed entry**

Add focused tests equivalent to:

```csharp
[Fact]
public async Task FreshCacheLoadsWithoutRemoteSyncAndSchedulesRemainingLifetime()
{
    var cachedAt = Now.AddHours(-22);
    var service = new FakeSync(account, Snapshot(account, "cached", cachedAt));
    var delay = new CapturingDelay();
    var vm = Create(service, delay);
    vm.SelectAccount(account, "账号");

    await vm.EnterAsync();

    Assert.Equal(0, service.SyncCalls);
    Assert.Equal(TimeSpan.FromHours(1), delay.RequestedDelay);
    Assert.Equal("cached", Assert.Single(vm.VisibleItems).Title);
}
```

Cover these cases separately:

- no `LastSuccessfulSyncAtUtc` calls `SyncAsync` immediately;
- age exactly 23 hours calls `SyncAsync` immediately;
- fresh cache does not call `SyncAsync` before the delay completes;
- completing the delay starts one sync;
- `Leave`, account switch, and manual refresh cancel the old delay;
- `Dispose` (the window/application shutdown path) cancels the old delay and does not start remote work afterward;
- manual refresh joining an already-running automatic refresh produces one `SyncAsync` call, not two sequential full reads;
- failed sync keeps the old rows and old timestamp;
- failed manual refresh restores the original remaining cache delay when the cache is still fresh, or schedules one retry after one hour when it is already stale;
- a failed one-hour retry does not create an hourly retry loop;
- successful manual refresh schedules a new 23-hour delay;
- no-cache and refresh-failure states use stable Chinese status messages.

- [ ] **Step 2: Run the focused tests and verify the new cases fail**

Run:

```powershell
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ShareAnalyticsViewModelTests"
```

Expected: the new freshness tests fail because entry always synchronizes and no delay abstraction exists.

- [ ] **Step 3: Add the delay boundary and minimal cache policy**

Add:

```csharp
public interface IShareAnalyticsDelay
{
    Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemShareAnalyticsDelay : IShareAnalyticsDelay
{
    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}
```

Register `SystemShareAnalyticsDelay` as the production `IShareAnalyticsDelay` in `AppBootstrap`, and pass it to the `ShareAnalyticsViewModel` constructor. Tests always pass an explicit controllable fake.

In `ShareAnalyticsViewModel`, add `CacheLifetime = TimeSpan.FromHours(23)`. `EnterAsync` must:

1. load and apply the complete cache;
2. refresh immediately only when no successful snapshot exists or it is already stale;
3. otherwise start a fire-and-forget cancellable wait for exactly `lastSuccessful + 23h - now`;
4. refresh once when that wait completes;
5. schedule the next 23-hour one-shot wait after a successful sync;
6. after a failed manual refresh, restore the remaining original delay when the cache is fresh, or schedule exactly one one-hour retry when already stale;
7. keep delay cancellation separate from sync cancellation so a manual click can join an automatic refresh instead of cancelling it and starting another request.

Add a current-account in-flight task guarded on the UI state path. Both the automatic timer and `RefreshCommand` call `StartOrJoinRefreshAsync`; when a task already exists for the same account and selection generation, both await that task. Account switch, page leave, and disposal cancel the account activation token; they must also cancel and dispose the one-shot delay token.

Keep the existing account/navigation generation fence around every result application.

- [ ] **Step 4: Run the focused tests and verify they pass**

Run the command from Step 2.

Expected: all `ShareAnalyticsViewModelTests` pass.

- [ ] **Step 5: Commit the cache policy**

```powershell
git add src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsContracts.cs src/BaiduBatchTransfer.App/Features/ShareAnalytics/SystemShareAnalyticsDelay.cs src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsViewModel.cs src/BaiduBatchTransfer.App/AppBootstrap.cs tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewModelTests.cs
git commit -m "feat: use expiring share analytics cache"
```

### Task 2: Persist and restore the last viewed account

**Files:**
- Create: `src/BaiduBatchTransfer.Core/Abstractions/IShareAnalyticsPreferenceStore.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/Migrations/023_ShareAnalyticsPreferences.sql`
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/Repositories/SqliteShareAnalyticsPreferenceStore.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/DatabaseMigrator.cs`
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Modify: `src/BaiduBatchTransfer.App/MainWindowViewModel.cs`
- Test: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/MigrationTests.cs`
- Create: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/ShareAnalyticsPreferenceStoreTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/MainWindowViewModelTests.cs`

- [ ] **Step 1: Write failing migration and repository tests**

The v23 migration creates exactly one preference row:

```sql
CREATE TABLE ShareAnalyticsPreferences (
    SingletonId INTEGER PRIMARY KEY CHECK(SingletonId=1),
    LastViewedAccountId TEXT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    FOREIGN KEY(LastViewedAccountId) REFERENCES Accounts(Id) ON DELETE SET NULL
);
```

Tests must verify initial null, account round-trip, clearing the preference, and automatic null after account deletion.

- [ ] **Step 2: Run focused infrastructure tests and verify failure**

```powershell
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~MigrationTests|FullyQualifiedName~ShareAnalyticsPreferenceStoreTests"
```

Expected: failures for missing migration 023 and store types.

- [ ] **Step 3: Implement migration and focused store**

Add this boundary:

```csharp
public interface IShareAnalyticsPreferenceStore
{
    Task<Guid?> LoadLastViewedAccountAsync(CancellationToken cancellationToken = default);
    Task SaveLastViewedAccountAsync(Guid? accountId, CancellationToken cancellationToken = default);
}
```

Use a singleton row and parameterized SQLite commands. Raise `DatabaseMigrator.LatestVersion` to 23.

- [ ] **Step 4: Write failing shell restoration tests**

Cover:

- a valid preferred account wins over incidental current selection;
- missing preference with one available account selects that account;
- missing/deleted preference with multiple accounts remains unselected;
- later left-card selection invokes the persistence delegate once with the selected account ID.

- [ ] **Step 5: Implement shell wiring**

Register the store in `AppBootstrap`. In `AppShellFactory.CreateAsync`, load the preferred account before constructing `MainWindowViewModel`. Pass the preferred ID and a save delegate. `MainWindowViewModel` resolves the ID against the current eligible rows and applies the fallback rules without creating or mutating any batch job.

- [ ] **Step 6: Run focused infrastructure and shell tests**

```powershell
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~MigrationTests|FullyQualifiedName~ShareAnalyticsPreferenceStoreTests"
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~MainWindowViewModelTests"
```

Expected: all selected tests pass.

- [ ] **Step 7: Commit account restoration**

```powershell
git add src/BaiduBatchTransfer.Core/Abstractions/IShareAnalyticsPreferenceStore.cs src/BaiduBatchTransfer.Infrastructure/Database/Migrations/023_ShareAnalyticsPreferences.sql src/BaiduBatchTransfer.Infrastructure/Database/Repositories/SqliteShareAnalyticsPreferenceStore.cs src/BaiduBatchTransfer.Infrastructure/Database/DatabaseMigrator.cs src/BaiduBatchTransfer.App/AppBootstrap.cs src/BaiduBatchTransfer.App/MainWindowViewModel.cs tests/BaiduBatchTransfer.Infrastructure.Tests/Database/MigrationTests.cs tests/BaiduBatchTransfer.Infrastructure.Tests/Database/ShareAnalyticsPreferenceStoreTests.cs tests/BaiduBatchTransfer.App.Tests/MainWindowViewModelTests.cs
git commit -m "feat: restore last share analytics account"
```

### Task 3: Apply the approved flat sortable header

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsView.xaml`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewModelTests.cs`

- [ ] **Step 1: Write failing sort-state and XAML structure tests**

Verify that the view model exposes the active sort column, direction, per-column header text and per-column active state. Verify XAML contains a borderless flat header style and sorted-column cell highlight, and no longer renders the sort headers with the normal bordered button style.

- [ ] **Step 2: Run focused App tests and verify failure**

```powershell
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ShareAnalyticsViewModelTests"
```

- [ ] **Step 3: Implement sort state and cache summary**

Expose deterministic properties such as:

```csharp
public string CacheSummaryText => snapshot?.LastSuccessfulSyncAtUtc is { } synced
    ? $"本地缓存｜更新于 {synced.ToLocalTime():yyyy-MM-dd HH:mm}｜{TotalCount:N0} 条"
    : "尚无本地缓存";
public bool IsSharedAtSorted => sortColumn == AccountShareSortColumn.SharedAt;
public string SharedAtHeader => Header("分享时间", AccountShareSortColumn.SharedAt);
```

Raise all header/active-state notifications in `Sort` and cache summary notifications in `Apply`/`UpdateSummary`.

- [ ] **Step 4: Implement the flat WPF styles**

Create local header and cell styles:

- transparent, borderless header buttons;
- light gray base header background;
- `#FFF5DF` sorted header and sorted column background;
- arrow appended to the active header label;
- right-aligned numeric cells and thin neutral grid lines.

Keep keyboard activation and automation names on every sortable header.

- [ ] **Step 5: Run focused tests and build Release**

```powershell
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ShareAnalyticsViewModelTests|FullyQualifiedName~SystemShareLinkLauncherTests"
dotnet build src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj -c Release --no-restore
```

Expected: focused tests pass and build reports 0 errors.

- [ ] **Step 6: Commit visual changes**

```powershell
git add src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsViewModel.cs src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsView.xaml tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewModelTests.cs
git commit -m "feat: flatten share analytics sort header"
```

### Task 4: Regression verification and release artifact

**Files:**
- Modify: `docs/testing/2026-09-01.md`

- [ ] **Step 1: Run the share analytics test slices**

```powershell
dotnet test tests/BaiduBatchTransfer.Core.Tests/BaiduBatchTransfer.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~AccountShareAnalytics"
dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~AccountShareAnalytics"
dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduBatchTransfer.BaiduAdapter.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~BaiduAccountShareAnalytics"
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~AccountShareAnalytics|FullyQualifiedName~ShareAnalyticsPreferenceStore|FullyQualifiedName~MigrationTests"
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ShareAnalytics|FullyQualifiedName~MainWindowViewModelTests"
```

- [ ] **Step 2: Run Release build and whitespace validation**

```powershell
dotnet build BaiduBatchTransfer.sln -c Release --no-restore
git diff --check
```

Record the known unrelated Release-only acceptance-switch failure separately if it remains; do not describe the entire suite as green when that pre-existing failure exists.

- [ ] **Step 3: Publish a new versioned artifact and ZIP**

```powershell
dotnet publish src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj -c Release --no-restore -o .artifacts/BaiduBatchTransfer-20260902-r5
Compress-Archive -LiteralPath .artifacts/BaiduBatchTransfer-20260902-r5 -DestinationPath .artifacts/BaiduBatchTransfer-20260902-r5.zip -CompressionLevel Optimal
Get-FileHash -LiteralPath .artifacts/BaiduBatchTransfer-20260902-r5.zip -Algorithm SHA256
```

- [ ] **Step 4: Perform real-window visual acceptance**

Start r5, select the restored account, and verify:

- an account with no successful cache starts the first full synchronization and shows the first-sync status;
- cached rows appear without an immediate full request when cache is fresh;
- a test snapshot at exactly 23 hours old starts one background synchronization while retaining the cached rows;
- cache source/time/count is visible;
- sorted header and column are pale yellow with a direction arrow;
- headers have no boxed-button appearance;
- manual refresh remains available;
- closing the window during a scheduled wait leaves no application process or background synchronization;
- the window stays open for user review.

- [ ] **Step 5: Update handoff evidence and commit**

Record commands, counts, known unrelated failures, artifact paths, hash, and visual observations in `docs/testing/2026-09-01.md`, then commit only the scoped handoff file.
