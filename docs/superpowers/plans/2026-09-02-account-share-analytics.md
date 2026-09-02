# Account Share Analytics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace “链接监控” with a read-only “分享数据” workspace for the currently selected Baidu account, showing every share returned by the complete “我的分享” pagination, historical totals, five Beijing-calendar-day values, deltas, filters, deterministic sorting, and safe link opening.

**Architecture:** Freeze and verify the private read-only web contract before feature implementation. A Baidu adapter maps the verified response into Core observations; an Application sync service loads the encrypted account session, retries transient reads once, walks all pages, and atomically commits account-scoped snapshots to SQLite; the WPF view model reads cache first and controls enter/manual refresh without polling. The old monitoring runtime and JSON remain on disk but are no longer referenced.

**Tech Stack:** .NET 8, C# 12, WPF, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite, xUnit.

**Reference spec:** `docs/superpowers/specs/2026-09-02-account-share-analytics-design.md`

**Required implementation skills:** `@superpowers:test-driven-development`, `@superpowers:systematic-debugging` for unexpected failures, and `@superpowers:verification-before-completion` before claiming completion.

---

## Scope gate

Task 1 is a hard go/no-go gate. Do not implement Tasks 2–8 unless the live, user-authorized read-only probe proves that the current personal account contract returns:

1. complete deterministic pagination for “我的分享”;
2. a stable share identity, title/path, share URL, creation time, and status;
3. non-negative historical totals for transfer, view, and download;
4. requests that are GET/HEAD, body-free, HTTPS, default-port, and limited to an exact `pan.baidu.com` allowlist.

If any item is absent, stop and report the sanitized response shape and the missing capability. Do not silently substitute browser DOM automation, enterprise APIs, or guessed fields.

## File map

### Create

- `src/BaiduBatchTransfer.Core/Domain/AccountShareAnalytics.cs` — stable share, metric, status, page, snapshot, and query models.
- `src/BaiduBatchTransfer.Core/Abstractions/IAccountShareAnalyticsClient.cs` — read-only remote page contract.
- `src/BaiduBatchTransfer.Core/Abstractions/IAccountShareAnalyticsRepository.cs` — atomic commit and cached-query contract.
- `src/BaiduBatchTransfer.BaiduAdapter/Http/BaiduAccountShareAnalyticsClient.cs` — frozen read-only route, bounded JSON parser, and error mapping.
- `src/BaiduBatchTransfer.Application/ShareAnalytics/AccountShareAnalyticsSyncService.cs` — session loading, retry-once pagination, single-flight and generation fencing.
- `src/BaiduBatchTransfer.Application/ShareAnalytics/AccountShareAnalyticsProjector.cs` — Beijing-date window, daily derivation, delta, filtering, and deterministic sorting.
- `src/BaiduBatchTransfer.Infrastructure/Database/Migrations/022_AccountShareAnalytics.sql` — account-scoped current records, metric snapshots, and sync state.
- `src/BaiduBatchTransfer.Infrastructure/Database/SqliteAccountShareAnalyticsRepository.cs` — transactional commit and cached snapshot query.
- `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsContracts.cs` — UI enums, row model, account selection contract, safe launcher contract.
- `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsViewModel.cs` — enter/refresh/account-change orchestration.
- `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsView.xaml` — approved share-data layout.
- `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsView.xaml.cs` — initialization, custom numeric sorting hook, and disposal only.
- `src/BaiduBatchTransfer.App/Features/ShareAnalytics/SystemShareLinkLauncher.cs` — allowlisted system-browser opening.
- `tests/BaiduBatchTransfer.BaiduAdapter.Tests/Fixtures/ShareAnalytics/share-page.synthetic.json` — sanitized contract fixture.
- `tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduAccountShareAnalyticsClientTests.cs`.
- `tests/BaiduBatchTransfer.Application.Tests/ShareAnalytics/AccountShareAnalyticsSyncServiceTests.cs`.
- `tests/BaiduBatchTransfer.Application.Tests/ShareAnalytics/AccountShareAnalyticsProjectorTests.cs`.
- `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/AccountShareAnalyticsRepositoryTests.cs`.
- `tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewModelTests.cs`.
- `tests/BaiduBatchTransfer.App.Tests/Features/SystemShareLinkLauncherTests.cs`.
- `docs/testing/2026-09-02-share-analytics-contract.md` — field names/types and sanitized live acceptance only.

### Modify

- `tests/BaiduBatchTransfer.App.Tests/Services/ReadOnlyProductionContractProbeTests.cs` — add verified share-list route(s) to the opt-in live probe.
- `src/BaiduBatchTransfer.BaiduAdapter/Http/BaiduReadOnlyContractCharacterizer.cs` — add safe dynamic-pagination characterization with redacted cursor handling and sanitized counts.
- `src/BaiduBatchTransfer.Infrastructure/Database/DatabaseMigrator.cs` — advance `LatestVersion` from 21 to 22.
- `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/MigrationTests.cs` — verify v22 schema, constraints, keys, indexes, and account cascade.
- `src/BaiduBatchTransfer.App/AppBootstrap.cs` — register client/repository/services and construct the new workspace.
- `src/BaiduBatchTransfer.App/MainWindowViewModel.cs` — publish the last left-rail account selected on, enter workspace once per navigation, and rename navigation semantics.
- `src/BaiduBatchTransfer.App/MainWindow.xaml` — rename the feature-navigation button to “分享数据”.
- `tests/BaiduBatchTransfer.App.Tests/MainWindowViewModelTests.cs` — navigation, selected-account routing, no-selection behavior, and late-result isolation.
- `tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs` — DI graph and read-only client registration.
- `docs/testing/2026-09-01.md` — final implementation and real read-only acceptance summary.

### Delete from source control after replacement is wired

- `src/BaiduBatchTransfer.App/Features/Monitoring/LinkMonitoringContracts.cs`
- `src/BaiduBatchTransfer.App/Features/Monitoring/LinkMonitoringViewModel.cs`
- `src/BaiduBatchTransfer.App/Features/Monitoring/LinkMonitoringView.xaml`
- `src/BaiduBatchTransfer.App/Features/Monitoring/LinkMonitoringView.xaml.cs`
- `src/BaiduBatchTransfer.App/Features/Monitoring/LocalLinkMonitoringStore.cs`
- `tests/BaiduBatchTransfer.App.Tests/Features/LinkMonitoringViewModelTests.cs`

Do not delete the user’s existing runtime monitoring JSON. Removing old source references must not remove or migrate that file.

---

### Task 1: Freeze the live read-only contract (hard gate)

**Files:**
- Modify: `src/BaiduBatchTransfer.BaiduAdapter/Http/BaiduReadOnlyContractCharacterizer.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Services/ReadOnlyProductionContractProbeTests.cs`
- Create: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduReadOnlyContractCharacterizerTests.cs`
- Create: `docs/testing/2026-09-02-share-analytics-contract.md`

- [ ] **Step 1: Add a failing protocol-safety test**

Add tests proving a share analytics probe refuses POST/PUT/DELETE, request bodies, redirects, non-HTTPS, non-default ports, non-`pan.baidu.com` hosts, unapproved paths, unknown query keys, non-positive/overflow page values, and any page outside the bounded walk. Also assert `BaiduReadOnlyContractObservation.ToString()` contains field paths/types and sanitized counts only and never response values, cursors, cookies, share IDs, titles, or full URLs.

```csharp
[Theory]
[InlineData("POST")]
[InlineData("PUT")]
[InlineData("DELETE")]
public async Task ShareAnalyticsProbeRejectsEveryWriteMethod(string method)
{
    using var request = new HttpRequestMessage(new HttpMethod(method), VerifiedShareRecordUri);
    var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        Characterizer().ObserveAsync("share-records", request, Session(), default));
    Assert.Equal("READ_ONLY_METHOD_REQUIRED", error.Message);
}
```

- [ ] **Step 2: Run the safety test and verify it fails for the missing verified route**

Run:

```powershell
dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduBatchTransfer.BaiduAdapter.Tests.csproj -c Release --filter "FullyQualifiedName~BaiduReadOnlyContractCharacterizerTests"
```

Expected: FAIL until the verified share-record route is represented in the test matrix; all existing identity/quota safety tests remain green.

- [ ] **Step 3: Characterize only the user-authorized current account**

Open the official “我的分享” page for development observation only, capture the exact body-free GET/HEAD request used for the first page and the next page, and add only the verified host/path plus exact query-key schema to the opt-in production probe. Use the existing encrypted `IBaiduSessionStore`; never paste CK into source, environment variables, logs, or docs.

Extend `BaiduReadOnlyContractCharacterizer` with a pagination-only route policy and observation method. The policy fixes method/host/path/query-key names and validates each generated page URI; the observer parses the verified list/count/next-page fields inside the characterizer, returns only item/page counts plus an opaque in-memory continuation, rejects repeated continuations, and stops at a fixed maximum. The continuation is usable only to build the next allowlisted URI and is redacted by every `ToString()`.

```csharp
public sealed record BaiduReadOnlyPagedRoutePolicy(
    string RouteId,
    HttpMethod Method,
    string Path,
    IReadOnlySet<string> AllowedQueryKeys,
    int MaximumPages);

public sealed record BaiduReadOnlyPageObservation(
    int PageNumber,
    int ItemCount,
    bool HasNextPage,
    ImmutableArray<string> JsonShape)
{
    public override string ToString() =>
        $"BaiduReadOnlyPageObservation {{ PageNumber = {PageNumber}, ItemCount = {ItemCount}, HasNextPage = {HasNextPage}, JsonShape = [{string.Join(", ", JsonShape)}], Values = [REDACTED] }}";
}
```

The opt-in test must select the account deterministically: when exactly one active enabled account exists, use it; when more than one exists, require `BAIDU_BATCH_SHARE_ANALYTICS_ACCOUNT_ID` to equal one active local account GUID and fail before any request if it is absent or unmatched. Never print that GUID.

The production test must remain opt-in:

```csharp
if (!string.Equals(
        Environment.GetEnvironmentVariable("BAIDU_BATCH_SHARE_ANALYTICS_PROBE"),
        "approved",
        StringComparison.Ordinal))
    return;
```

The probe output may contain only:

```text
route-id, HTTP method, status code, page count, item count, JSON field paths, JSON value kinds
```

- [ ] **Step 4: Run the live gate once**

Run in the current user session:

```powershell
$env:BAIDU_BATCH_SHARE_ANALYTICS_PROBE='approved'
$env:BAIDU_BATCH_SHARE_ANALYTICS_ACCOUNT_ID='<local account GUID only when multiple accounts are active>'
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Release --filter "FullyQualifiedName~ReadOnlyProductionContractProbeTests&FullyQualifiedName~ShareAnalytics"
Remove-Item Env:BAIDU_BATCH_SHARE_ANALYTICS_PROBE
Remove-Item Env:BAIDU_BATCH_SHARE_ANALYTICS_ACCOUNT_ID -ErrorAction SilentlyContinue
```

Expected: PASS; all pages are reachable; the sanitized shape proves stable identity plus transfer/view/download cumulative fields. If not, stop at the scope gate.

- [ ] **Step 5: Record sanitized evidence**

Write `docs/testing/2026-09-02-share-analytics-contract.md` with the verified method, allowlisted host/path, pagination parameter names, field names/types, error-code shapes, response size bound, and the gate result. Do not include query tokens, cookies, account IDs, share IDs, titles, links, raw payloads, or exact private counts.

- [ ] **Step 6: Commit the gate**

```powershell
git add src/BaiduBatchTransfer.BaiduAdapter/Http/BaiduReadOnlyContractCharacterizer.cs tests/BaiduBatchTransfer.App.Tests/Services/ReadOnlyProductionContractProbeTests.cs tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduReadOnlyContractCharacterizerTests.cs docs/testing/2026-09-02-share-analytics-contract.md
git commit -m "test: verify read-only share analytics contract"
```

---

### Task 2: Add stable Core contracts and five-day projection rules

**Files:**
- Create: `src/BaiduBatchTransfer.Core/Domain/AccountShareAnalytics.cs`
- Create: `src/BaiduBatchTransfer.Core/Abstractions/IAccountShareAnalyticsClient.cs`
- Create: `src/BaiduBatchTransfer.Core/Abstractions/IAccountShareAnalyticsRepository.cs`
- Create: `src/BaiduBatchTransfer.Application/ShareAnalytics/AccountShareAnalyticsProjector.cs`
- Test: `tests/BaiduBatchTransfer.Core.Tests/AccountShareAnalyticsTests.cs`
- Test: `tests/BaiduBatchTransfer.Application.Tests/ShareAnalytics/AccountShareAnalyticsProjectorTests.cs`

- [ ] **Step 1: Write failing domain and projector tests**

Cover validation, sensitive `ToString()` redaction, exactly three metrics, five `Asia/Shanghai` dates, direct daily buckets taking priority, cumulative-difference fallback, missing/declining snapshots producing `null`, and `NewValue = Today - Yesterday` only when both exist.

```csharp
[Fact]
public void MissingYesterdayMakesNewValueUnavailable()
{
    var row = Projector().Project(Item(today: 12, yesterday: null), ShareMetric.Transfer);
    Assert.Equal("12", row.TodayText);
    Assert.Equal("--", row.YesterdayText);
    Assert.Equal("--", row.NewText);
}

[Fact]
public void BeijingMidnightDefinesTheFiveDateWindow()
{
    var now = new DateTimeOffset(2026, 9, 1, 16, 30, 0, TimeSpan.Zero);
    Assert.Equal(
        [new DateOnly(2026, 9, 2), new(2026, 9, 1), new(2026, 8, 31), new(2026, 8, 30), new(2026, 8, 29)],
        Projector().FiveDayWindow(now));
}
```

- [ ] **Step 2: Run tests to verify failure**

```powershell
dotnet test tests/BaiduBatchTransfer.Core.Tests/BaiduBatchTransfer.Core.Tests.csproj -c Release --filter "FullyQualifiedName~AccountShareAnalytics"
dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj -c Release --filter "FullyQualifiedName~AccountShareAnalyticsProjector"
```

Expected: FAIL because the types do not exist.

- [ ] **Step 3: Implement the Core models and interfaces**

Use these public shapes; collections must be immutable snapshots and all counts must be non-negative when present:

```csharp
public enum AccountShareMetric { Transfer, View, Download }
public enum AccountShareRemoteStatus { Valid, Invalid }

public sealed record AccountShareMetricObservation(
    AccountShareMetric Metric,
    long? HistoricalTotal,
    IReadOnlyDictionary<DateOnly, long>? DirectDailyValues = null);

public sealed record AccountShareObservation(
    string ShareId,
    string Title,
    Uri ShareUri,
    DateTimeOffset SharedAtUtc,
    AccountShareRemoteStatus Status,
    IReadOnlyList<AccountShareMetricObservation> Metrics);

public sealed record AccountSharePage(
    IReadOnlyList<AccountShareObservation> Items,
    string? NextCursor);

public interface IAccountShareAnalyticsClient
{
    Task<BaiduAdapterResult<AccountSharePage>> ReadPageAsync(
        AccountSession session, string? cursor, CancellationToken cancellationToken);
}
```

Every observation must contain exactly one entry for each metric kind, but each individual historical/daily value may be `null`; unavailable values are not page corruption. Stable identity corruption, duplicate metric kinds, an unknown metric kind, or an unusable page envelope still rejects the page.

The repository contract exposes `ReserveGenerationAsync(accountId)`, `LoadCachedAsync(accountId)`, and `CommitCompleteSyncAsync(commit)`. It must not expose partial-page writes.

- [ ] **Step 4: Implement projection and deterministic sorting**

`AccountShareAnalyticsProjector` must:

- use `TimeZoneInfo.FindSystemTimeZoneById("China Standard Time")` on Windows and the `Asia/Shanghai` alias fallback;
- output today, yesterday, and three prior dates;
- prefer direct daily values, otherwise derive a day only from adjacent cumulative snapshots;
- return `null` for missing inputs or decreasing cumulative values;
- sort missing values last in both directions;
- break numeric ties by `SharedAtUtc` descending then `ShareId` ordinal;
- format valid counts with `N0` and missing values as `--`.

- [ ] **Step 5: Run tests and commit**

```powershell
dotnet test tests/BaiduBatchTransfer.Core.Tests/BaiduBatchTransfer.Core.Tests.csproj -c Release --filter "FullyQualifiedName~AccountShareAnalytics"
dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj -c Release --filter "FullyQualifiedName~AccountShareAnalyticsProjector"
git add src/BaiduBatchTransfer.Core src/BaiduBatchTransfer.Application/ShareAnalytics tests/BaiduBatchTransfer.Core.Tests tests/BaiduBatchTransfer.Application.Tests/ShareAnalytics
git commit -m "feat: add account share analytics model"
```

Expected: new focused tests PASS.

---

### Task 3: Implement the bounded read-only Baidu adapter

**Files:**
- Create: `src/BaiduBatchTransfer.BaiduAdapter/Http/BaiduAccountShareAnalyticsClient.cs`
- Create: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/Fixtures/ShareAnalytics/share-page.synthetic.json`
- Create: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduAccountShareAnalyticsClientTests.cs`

- [ ] **Step 1: Write failing parser and transport tests**

Use a scripted `HttpMessageHandler` and sanitized synthetic fixture matching Task 1. Assert:

- cursor/page values are generated internally and cannot alter host/path;
- only the frozen GET route or routes proven by Task 1 are used and every request body is absent;
- redirects are not followed;
- cookies apply by domain/path/secure/expiry and are never observable through `ToString()`;
- response is bounded (start with 2 MiB unless Task 1 proves a smaller safe ceiling);
- share identity, title, URL, timestamp, status, and all three totals parse exactly;
- malformed or duplicate stable identities fail the complete page;
- the client emits exactly the three known metric kinds, while a missing, negative, or type-confused individual statistic becomes `null` and displays/sorts as `--` rather than discarding the share;
- expired login, verification, rate limit, transient service, and contract change map to distinct safe error kinds;
- a page cursor is opaque, bounded, and redacted.

- [ ] **Step 2: Run tests and verify failure**

```powershell
dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduBatchTransfer.BaiduAdapter.Tests.csproj -c Release --filter "FullyQualifiedName~BaiduAccountShareAnalyticsClientTests"
```

Expected: FAIL because the client is missing.

- [ ] **Step 3: Implement the client from the frozen contract**

The client owns every route builder and accepts only `(session, cursor)`. Callers cannot supply a URI, method, headers, query token, or body. Task 1 determines whether a logical share page contains statistics inline or requires additional verified read-only statistics requests. Freeze all observed read-only routes in this client; `ReadPageAsync` composes the complete logical page and does not expose route details upward. Configure its handler as:

```csharp
new HttpClientHandler
{
    AllowAutoRedirect = false,
    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    UseCookies = false,
};
```

Validate `https`, default port, exact `pan.baidu.com`, exact verified path, and allowlisted pagination/statistics keys before every request. Parse with `JsonDocumentOptions { MaxDepth = 64 }`. Route every individual HTTP read through one shared `SendReadWithRetryAsync` helper: retry a transient network/5xx/429 read exactly once, do not retry expired login/verification/contract errors, and never retry the complete logical page on top of the request-level retry. Return only safe fixed Chinese messages to upper layers and redact payload values in diagnostics.

Tests must cover both contract shapes supported after the gate: inline statistics, or a share page followed by one or more separately verified statistics reads. In the separate shape, a transient statistics request is called exactly twice, previously successful requests are not committed independently, and the logical page fails as a unit after the second failure.

- [ ] **Step 4: Run adapter tests and commit**

```powershell
dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduBatchTransfer.BaiduAdapter.Tests.csproj -c Release --filter "FullyQualifiedName~BaiduAccountShareAnalyticsClientTests|FullyQualifiedName~BaiduReadOnlyContractCharacterizerTests"
git add src/BaiduBatchTransfer.BaiduAdapter/Http/BaiduAccountShareAnalyticsClient.cs tests/BaiduBatchTransfer.BaiduAdapter.Tests
git commit -m "feat: read Baidu account share analytics"
```

Expected: PASS; captured requests contain no mutation method/path.

---

### Task 4: Add account-scoped atomic SQLite storage

**Files:**
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/Migrations/022_AccountShareAnalytics.sql`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/DatabaseMigrator.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteAccountShareAnalyticsRepository.cs`
- Modify: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/MigrationTests.cs`
- Create: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/AccountShareAnalyticsRepositoryTests.cs`

- [ ] **Step 1: Write failing migration tests**

Require these account-scoped tables and indexes:

```sql
CREATE TABLE AccountShareRecords (
  AccountId TEXT NOT NULL,
  ShareId TEXT NOT NULL,
  Title TEXT NOT NULL,
  ShareUrl TEXT NOT NULL,
  SharedAtUtc TEXT NOT NULL,
  Status TEXT NOT NULL CHECK(Status IN ('Valid','Invalid')),
  IsCurrent INTEGER NOT NULL CHECK(IsCurrent IN (0,1)),
  LastSeenAtUtc TEXT NOT NULL,
  PRIMARY KEY(AccountId,ShareId),
  FOREIGN KEY(AccountId) REFERENCES Accounts(Id) ON DELETE CASCADE
);

CREATE TABLE AccountShareMetricSnapshots (
  AccountId TEXT NOT NULL,
  ShareId TEXT NOT NULL,
  Metric TEXT NOT NULL CHECK(Metric IN ('Transfer','View','Download')),
  DateKey TEXT NOT NULL,
  CapturedAtUtc TEXT NOT NULL,
  HistoricalTotal INTEGER NULL CHECK(HistoricalTotal IS NULL OR HistoricalTotal>=0),
  DailyValue INTEGER NULL CHECK(DailyValue IS NULL OR DailyValue>=0),
  DailySource TEXT NULL CHECK(DailySource IS NULL OR DailySource IN ('Direct','Derived')),
  PRIMARY KEY(AccountId,ShareId,Metric,DateKey),
  FOREIGN KEY(AccountId,ShareId) REFERENCES AccountShareRecords(AccountId,ShareId) ON DELETE CASCADE
);

CREATE TABLE AccountShareSyncState (
  AccountId TEXT PRIMARY KEY,
  IssuedGeneration INTEGER NOT NULL DEFAULT 0 CHECK(IssuedGeneration>=0),
  CommittedGeneration INTEGER NOT NULL DEFAULT 0 CHECK(CommittedGeneration>=0 AND CommittedGeneration<=IssuedGeneration),
  LastSuccessfulSyncAtUtc TEXT NULL,
  FOREIGN KEY(AccountId) REFERENCES Accounts(Id) ON DELETE CASCADE
);
```

Also require indexes for `(AccountId, IsCurrent, SharedAtUtc)` and `(AccountId, ShareId, Metric, DateKey)`.

- [ ] **Step 2: Run migration tests and verify failure**

```powershell
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~MigrationTests&DisplayName~TwentyTwo"
```

Expected: FAIL at schema version 22.

- [ ] **Step 3: Add migration 022 and advance the version**

Create the schema above, add the two indexes, and set `DatabaseMigrator.LatestVersion = 22`. Do not change previous migrations.

- [ ] **Step 4: Write failing repository tests**

Cover:

- all reads and writes require `AccountId`;
- a complete commit upserts returned records and marks previously current but absent records `IsCurrent=0` without deleting snapshots;
- an explicitly invalid returned record remains current and counts as invalid;
- a failed transaction leaves the previous current list and sync time untouched;
- a generation lower than or equal to `CommittedGeneration` cannot overwrite newer data;
- `ReserveGenerationAsync` atomically increments and returns `IssuedGeneration`, including after a repository/service restart;
- a failed reserved generation may leave a gap, and the next reservation still returns a strictly larger value;
- the last sync of one Beijing date replaces that date’s cumulative snapshot;
- direct buckets upsert their own dates;
- account deletion cascades records, snapshots, and sync state;
- account A cannot read account B values.

- [ ] **Step 5: Implement one-transaction commit and cache query**

`ReserveGenerationAsync` must use one SQLite transaction to insert the account state if absent, increment `IssuedGeneration`, and return it. `CommitCompleteSyncAsync` must begin one SQLite transaction, require a reserved generation greater than `CommittedGeneration` and no greater than `IssuedGeneration`, set existing current rows to zero, upsert all observed records, upsert all metric/date snapshots, update committed generation/last-success time, and commit. On any exception it rolls back. `LoadCachedAsync` returns only `IsCurrent=1` records plus enough snapshots to project five days; no CK is stored.

- [ ] **Step 6: Run repository tests and commit**

```powershell
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~AccountShareAnalyticsRepositoryTests|FullyQualifiedName~MigrationTests"
git add src/BaiduBatchTransfer.Infrastructure/Database tests/BaiduBatchTransfer.Infrastructure.Tests/Database
git commit -m "feat: persist account share analytics snapshots"
```

Expected: PASS.

---

### Task 5: Implement complete sync, retry once, and stale-result fencing

**Files:**
- Create: `src/BaiduBatchTransfer.Application/ShareAnalytics/AccountShareAnalyticsSyncService.cs`
- Create: `tests/BaiduBatchTransfer.Application.Tests/ShareAnalytics/AccountShareAnalyticsSyncServiceTests.cs`

- [ ] **Step 1: Write failing sync tests**

Test these exact scenarios:

1. cache is loadable without a remote request;
2. every page is read sequentially until `NextCursor == null`;
3. duplicate/repeated cursor or more than the bounded maximum page count fails safely;
4. a page whose adapter-level request retry succeeds is accepted without an additional service-level retry;
5. an adapter result that is still transient after its request-level retry ends the sync and never commits;
6. expired login and verification end immediately;
7. any page/metric contract failure prevents the complete commit;
8. account A and B can sync independently;
9. same-account concurrent calls are single-flight;
10. every sync reserves its durable generation before remote work, and a newly constructed service continues above the persisted issued generation;
11. older generation completion cannot overwrite a newer generation;
12. cancellation never produces a partial commit;
13. safe result messages contain no cookies, account IDs, share IDs, URLs, or raw exception text.

```csharp
[Fact]
public async Task MiddlePageFailureAfterOneRetryKeepsOldCache()
{
    var service = Service(pages: Page1ThenTwoTransientFailures());
    var result = await service.SyncAsync(AccountId, default);
    Assert.Equal(AccountShareSyncDisposition.TemporarilyUnavailable, result.Disposition);
    Assert.Equal(1, Client.LogicalPageCallsFor("page-2"));
    Assert.Equal(0, Repository.CommitCalls);
    Assert.Equal("旧记录", Assert.Single(await Repository.LoadCachedAsync(AccountId, default)).Title);
}
```

- [ ] **Step 2: Run tests and verify failure**

```powershell
dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj -c Release --filter "FullyQualifiedName~AccountShareAnalyticsSyncServiceTests"
```

Expected: FAIL because the service is missing.

- [ ] **Step 3: Implement the service**

Dependencies:

```csharp
public AccountShareAnalyticsSyncService(
    IBaiduSessionStore sessions,
    IAccountShareAnalyticsClient client,
    IAccountShareAnalyticsRepository repository,
    TimeProvider timeProvider)
```

Use a per-account semaphore. Reserve a durable generation from the repository before remote work, then load the encrypted session by local account ID. The adapter owns the exactly-once transient retry for each verified HTTP read, so the service must not add a second retry layer. Build the full observation list in memory; reject repeated cursors and duplicate share IDs; call `CommitCompleteSyncAsync` exactly once only after all pages succeed. Add a restart test that constructs a new repository/service over the same database and proves the next issued generation is greater than the prior one.

- [ ] **Step 4: Run tests and commit**

```powershell
dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj -c Release --filter "FullyQualifiedName~AccountShareAnalyticsSyncServiceTests|FullyQualifiedName~AccountShareAnalyticsProjectorTests"
git add src/BaiduBatchTransfer.Application/ShareAnalytics tests/BaiduBatchTransfer.Application.Tests/ShareAnalytics
git commit -m "feat: synchronize complete share analytics pages"
```

Expected: PASS.

---

### Task 6: Build the cache-first share-data view model

**Files:**
- Create: `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsContracts.cs`
- Create: `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsViewModel.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewModelTests.cs`

- [ ] **Step 1: Write failing view-model tests**

Cover:

- no account selected shows “请先选择左侧账号” and makes refresh unavailable;
- selecting an account immediately loads only its cache but does not poll;
- entering the workspace starts one sync for that navigation;
- when this replaced workspace is the shell’s initial content, shell startup activates it exactly once after account selection/cache initialization;
- repeated property changes during the same stay do not auto-sync;
- leaving/re-entering triggers one new sync;
- manual refresh triggers one sync and is disabled while that account is syncing;
- failed sync keeps rows and last successful time;
- expired login says “账号登录已失效，请重新扫码”;
- current-account switch replaces visible cache immediately and old-account late completion cannot affect it;
- summary counts include only current returned records;
- metric switch preserves search/status filter and resets sort to historical total descending;
- title search and all/effective/invalid filters compose;
- all numeric columns sort both ways with `--` last;
- no timer or background polling member exists.

- [ ] **Step 2: Run tests and verify failure**

```powershell
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Release --filter "FullyQualifiedName~ShareAnalyticsViewModelTests"
```

Expected: FAIL because the view model is missing.

- [ ] **Step 3: Implement commands and account/navigation generation**

Expose:

```csharp
public void SelectAccount(Guid? accountId, string? displayName);
public Task EnterAsync(CancellationToken cancellationToken = default);
public void Leave();
public IAsyncRelayCommand RefreshCommand { get; }
public IRelayCommand<AccountShareMetric> SelectMetricCommand { get; }
public IRelayCommand<ShareAnalyticsSortColumn> SortCommand { get; }
public IRelayCommand<ShareAnalyticsRow> OpenCommand { get; }
```

Maintain an account-selection generation and navigation generation. Apply a result only when both still match. Load cache before starting the enter sync. The view model owns no `DispatcherTimer`, `PeriodicTimer`, delayed loop, or hosted service.

- [ ] **Step 4: Run tests and commit**

```powershell
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Release --filter "FullyQualifiedName~ShareAnalyticsViewModelTests"
git add src/BaiduBatchTransfer.App/Features/ShareAnalytics tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewModelTests.cs
git commit -m "feat: add cache-first share analytics workspace"
```

Expected: PASS.

---

### Task 7: Replace navigation/UI and add safe system-browser opening

**Files:**
- Create: `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsView.xaml`
- Create: `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsView.xaml.cs`
- Create: `src/BaiduBatchTransfer.App/Features/ShareAnalytics/SystemShareLinkLauncher.cs`
- Modify: `src/BaiduBatchTransfer.App/MainWindow.xaml`
- Modify: `src/BaiduBatchTransfer.App/MainWindowViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/MainWindowViewModelTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/SystemShareLinkLauncherTests.cs`

- [ ] **Step 1: Write failing shell, XAML, launcher, and DI tests**

Assert the feature menu is exactly:

```text
首页, 分享数据, 批量转存, 广告插入, 文件排除, 数据统计, 设置中心
```

Assert the share workspace contains current account, last successful sync, refresh button, total/effective/invalid summaries, transfer/view/download metric controls, title search, status filter, and DataGrid columns:

```text
标题, 状态, 分享时间, 历史累计, 新增, 今天, 昨天, dynamic date 1, dynamic date 2, dynamic date 3, 链接
```

Assert there are no start/pause/stop/interval/paste/transfer controls. Assert the launcher accepts only default-port `https://pan.baidu.com/s/...` and rejects HTTP, custom ports, user-info hosts, lookalike hosts, non-share paths, and arbitrary protocols without calling `Process.Start`.

- [ ] **Step 2: Run focused tests and verify failure**

```powershell
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Release --filter "FullyQualifiedName~MainWindowViewModelTests|FullyQualifiedName~SystemShareLinkLauncherTests|FullyQualifiedName~AppBootstrapTests"
```

Expected: FAIL on old “链接监控” wiring.

- [ ] **Step 3: Implement the approved XAML**

Use the existing `SuperPanTheme.xaml` brushes/styles. Keep the feature menu centered/top-aligned. Use compact summary cards, three metric toggle buttons, one-line filter controls, and a dominant virtualized read-only DataGrid. Dynamic date headers bind to the view-model Beijing-date labels. Positive delta foreground is red, negative is green, and zero/missing displays `--`.

- [ ] **Step 4: Wire account selection and navigation**

Rename `ShowLinkMonitorCommand` to `ShowShareDataCommand` and title to “分享数据”. The left rail remains the existing multi-account batch selector; the analytics account is the most recently toggled-on account. If that account is toggled off, use the most recently toggled-on account still selected; otherwise clear the analytics selection. At startup, if multiple rows are already selected, use the last selected row in the existing `Accounts` collection order as the deterministic initial analytics account. On share-data navigation call `EnterAsync`; on other navigation call `Leave`. Account capacity refresh behavior remains unchanged.

In `AppBootstrap.cs`, register the client, SQLite repository, projector, sync service, and launcher. Construct `ShareAnalyticsViewModel` once, pass account selection/navigation callbacks into `MainWindowViewModel`, and dispose the workspace on shell close. Because this workspace remains the initial shell content, `HandleLoadedAsync` must call a dedicated initial-workspace activation exactly once after applying the initially selected account. Add a shell test with a preselected account proving one cache load followed by one sync, and no duplicate call from `Loaded`/navigation wiring.

- [ ] **Step 5: Implement safe opening**

Validate the URI before calling an injected process starter:

```csharp
uri.Scheme == Uri.UriSchemeHttps &&
uri.IsDefaultPort &&
string.IsNullOrEmpty(uri.UserInfo) &&
string.Equals(uri.IdnHost, "pan.baidu.com", StringComparison.OrdinalIgnoreCase) &&
uri.AbsolutePath.StartsWith("/s/", StringComparison.Ordinal)
```

Then open with `UseShellExecute = true`. Fail with a safe Chinese message and never log the complete URI.

- [ ] **Step 6: Run focused UI tests and commit**

```powershell
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Release --filter "FullyQualifiedName~ShareAnalytics|FullyQualifiedName~MainWindowViewModelTests|FullyQualifiedName~SystemShareLinkLauncherTests|FullyQualifiedName~AppBootstrapTests"
git add src/BaiduBatchTransfer.App tests/BaiduBatchTransfer.App.Tests
git commit -m "feat: replace link monitoring with share data"
```

Expected: PASS.

---

### Task 8: Retire old runtime code, run real read-only acceptance, and package

**Files:**
- Delete: `src/BaiduBatchTransfer.App/Features/Monitoring/*`
- Delete: `tests/BaiduBatchTransfer.App.Tests/Features/LinkMonitoringViewModelTests.cs`
- Modify: `docs/testing/2026-09-01.md`
- Modify: `docs/testing/2026-09-02-share-analytics-contract.md`

- [ ] **Step 1: Prove old code is unreferenced before deletion**

```powershell
rg -n "LinkMonitoring|链接监控|ShowLinkMonitor" src tests -g '!**/bin/**' -g '!**/obj/**'
```

Expected: only files scheduled for deletion or tests intentionally being updated. Do not delete the runtime JSON file from the application data directory.

- [ ] **Step 2: Remove the unreferenced old source and run compile checks**

Delete only the tracked source/test files listed above, then run:

```powershell
dotnet build BaiduBatchTransfer.sln -c Release
git diff --check
```

Expected: build succeeds and diff check has no errors.

- [ ] **Step 3: Run all focused suites**

```powershell
dotnet test tests/BaiduBatchTransfer.Core.Tests/BaiduBatchTransfer.Core.Tests.csproj -c Release --filter "FullyQualifiedName~AccountShareAnalytics"
dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduBatchTransfer.BaiduAdapter.Tests.csproj -c Release --filter "FullyQualifiedName~ShareAnalytics|FullyQualifiedName~ReadOnlyContract"
dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj -c Release --filter "FullyQualifiedName~AccountShareAnalytics"
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~AccountShareAnalytics|FullyQualifiedName~MigrationTests"
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Release --filter "FullyQualifiedName~ShareAnalytics|FullyQualifiedName~MainWindowViewModelTests|FullyQualifiedName~AppBootstrapTests"
```

Expected: all focused suites PASS.

- [ ] **Step 4: Run the full non-live suites**

```powershell
dotnet test BaiduBatchTransfer.sln -c Release --no-build
```

Expected: PASS. If unrelated pre-existing managed-browser timing tests fail, isolate and document them with exact names/output; do not claim the full suite passed.

- [ ] **Step 5: Perform current-account real read-only acceptance**

With the user’s current encrypted session:

1. select the account on the left;
2. enter “分享数据” and confirm exactly one sync;
3. compare complete page count/total count with Baidu “我的分享” without recording private values;
4. switch transfer/view/download;
5. verify ascending/descending order and `--` last;
6. verify history/new/today/yesterday/three prior date headers and calculations;
7. click manual refresh once;
8. open one user-chosen link in the default browser;
9. confirm no cancel/delete/regenerate/share/write request occurred.

Record only sanitized pass/fail evidence and aggregate shape in `docs/testing/2026-09-02-share-analytics-contract.md`.

- [ ] **Step 6: Update handoff and commit**

```powershell
git add -A src/BaiduBatchTransfer.App/Features/Monitoring tests/BaiduBatchTransfer.App.Tests/Features/LinkMonitoringViewModelTests.cs docs/testing/2026-09-01.md docs/testing/2026-09-02-share-analytics-contract.md
git commit -m "docs: verify account share analytics"
```

- [ ] **Step 7: Build the next artifact without overwriting r2**

Publish to a new `BaiduBatchTransfer-20260902-r3` directory, archive it, calculate SHA-256, and start that packaged executable for visual review. Never overwrite or delete the existing r2 artifact.

Expected final handoff:

- exact artifact directory and ZIP path;
- ZIP SHA-256;
- focused/full test counts with any unrelated failures named;
- real read-only acceptance result;
- explicit statement that no remote share was modified.
