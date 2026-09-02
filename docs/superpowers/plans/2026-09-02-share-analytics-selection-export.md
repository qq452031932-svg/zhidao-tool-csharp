# Share Analytics Selection and Export Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Repair share titles and rebuild the share-analytics table with stable bulk selection, range selection, horizontal navigation, and selected-row Excel export.

**Architecture:** Keep remote title normalization in the Baidu adapter, projection/sorting and workbook generation in Application, and interactive selection state in the App view model. Selection is keyed by `ShareId`, bulk operations are generation-fenced, and export freezes a complete snapshot before reusing the existing streaming OpenXML publisher.

**Tech Stack:** .NET 8, C#, WPF, CommunityToolkit.Mvvm, Open XML SDK, xUnit.

---

## File map

- Modify `src/BaiduBatchTransfer.BaiduAdapter/Http/BaiduAccountShareAnalyticsClient.cs`: normalize `typicalPath`/`name` and reject the `0` placeholder.
- Modify `tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduAccountShareAnalyticsClientTests.cs`: freeze title parsing behavior.
- Modify `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsContracts.cs`: expose sequence and selection state on row projections; hide cached `0` safely.
- Modify `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsViewModel.cs`: own selected IDs, range inputs, generation-fenced bulk commands, and export orchestration.
- Create `src/BaiduBatchTransfer.Application/ShareAnalytics/ShareAnalyticsWorkbookExportService.cs`: freeze selected projections and map them to workbook rows/layout.
- Modify `src/BaiduBatchTransfer.Application/Jobs/ReplacementWorkbookModels.cs`: expose a layer-safe publish error contract so Application can distinguish destination collisions.
- Modify `src/BaiduBatchTransfer.Infrastructure/Excel/OpenXmlReplacementWorkbookFilePublisher.cs`: implement the Application-owned publish error contract without changing atomic publication.
- Create `tests/BaiduBatchTransfer.Application.Tests/ShareAnalytics/ShareAnalyticsWorkbookExportServiceTests.cs`: verify exact workbook contract and cancellation.
- Modify `src/BaiduBatchTransfer.App/AppBootstrap.cs`: register/inject export dependencies.
- Modify `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsView.xaml`: rebuild the toolbar and columns, align headers, and enable horizontal scrolling.
- Modify `tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewModelTests.cs`: cover selection identity, ranges, account/refresh boundaries, notifications, and export commands.
- Create `tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewStructureTests.cs`: parse XAML and verify the actual DataGrid column contract.

### Task 1: Repair remote and cached share titles

**Files:**
- Modify: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduAccountShareAnalyticsClientTests.cs`
- Modify: `src/BaiduBatchTransfer.BaiduAdapter/Http/BaiduAccountShareAnalyticsClient.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewModelTests.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsContracts.cs`

- [ ] **Step 1: Write failing adapter contract tests**

Add cases whose response item includes both fields and assert the selected title:

```csharp
[Theory]
[InlineData("/电视剧/春归无觅处/", "0", "春归无觅处")]
[InlineData(" 0 ", "有效名称", "有效名称")]
[InlineData(null, "123", "123")]
public async Task FetchPageAsync_NormalizesShareTitle(
    string? typicalPath, string? name, string expected)
{
    var page = await FetchSyntheticItemAsync(typicalPath, name);
    Assert.Equal(expected, Assert.Single(page.Items).Title);
}
```

Also add object/array/number fields, trailing `/` and `\\`, both path separators, a value longer than the existing 4096-character bound, and control characters. Assert every invalid `typicalPath` candidate falls through to a valid `name`; if both are invalid the client uses `ShareId` without dropping the record.

- [ ] **Step 2: Run the focused adapter tests and verify failure**

Run:

```powershell
dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduBatchTransfer.BaiduAdapter.Tests.csproj --filter "FullyQualifiedName~BaiduAccountShareAnalyticsClientTests"
```

Expected: FAIL because `name` currently wins or `0` remains a title.

- [ ] **Step 3: Implement bounded title normalization**

Add helpers equivalent to:

```csharp
private static string? NormalizeTitleCandidate(JsonElement root, string property, bool path)
{
    if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        return null;

    var candidate = value.GetString()?.Trim();
    if (path)
        candidate = candidate?.TrimEnd('/', '\\')
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()?.Trim();

    return string.IsNullOrWhiteSpace(candidate) || candidate == "0" ? null : candidate;
}
```

Resolve `typicalPath`, then `name`, then the stable share ID. Reuse the existing `ReadBoundedString` validation so the 4096-character and control-character boundaries remain intact before path normalization; do not allow a malformed display field to fail the full page.

- [ ] **Step 4: Add and pass the cached-placeholder display test**

Construct `ShareAnalyticsRow` from a projection whose title is `" 0 "`; assert `Title == ShareId`. Keep other numeric titles unchanged.

Run:

```powershell
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter "FullyQualifiedName~ShareAnalyticsViewModelTests"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/BaiduBatchTransfer.BaiduAdapter/Http/BaiduAccountShareAnalyticsClient.cs src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsContracts.cs tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduAccountShareAnalyticsClientTests.cs tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewModelTests.cs
git commit -m "fix: normalize share analytics titles"
```

### Task 2: Add stable row selection and numbering

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsContracts.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsViewModel.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewModelTests.cs`

- [ ] **Step 1: Write failing tests for identity and numbering**

Cover:

```csharp
Assert.Equal(Enumerable.Range(1, vm.VisibleItems.Count), vm.VisibleItems.Select(x => x.Sequence));
vm.VisibleItems[1].IsSelected = true;
vm.SearchText = "different";
vm.SearchText = "";
Assert.True(vm.VisibleItems.Single(x => x.ShareId == selectedId).IsSelected);
```

Add cases for sort/metric/status persistence, account switch clearing, and same-account refresh retaining only the ID intersection.

- [ ] **Step 2: Run the focused tests and verify failure**

Run the ShareAnalyticsViewModel test filter. Expected: FAIL because rows have no sequence/selection model.

- [ ] **Step 3: Implement ID-backed selection**

Add `Sequence`, `IsSelected`, and a selection callback to `ShareAnalyticsRow`. In the view model add:

```csharp
private HashSet<string> selectedShareIds = new(StringComparer.Ordinal);
public int SelectedCount => selectedShareIds.Count;
public int CurrentResultCount => currentResults.Count;
public bool? HeaderSelectionState { get; }
```

Have `Rebuild` project the full ordered result once, assign continuous sequence values, and restore `IsSelected` from the HashSet. A row toggle mutates the HashSet without rebuilding or sorting the whole result, then raises `SelectedCount`, `HeaderSelectionState`, and export can-execute exactly once.

- [ ] **Step 4: Pass identity and notification tests**

Assert one row toggle changes selection summary, `HeaderSelectionState`, and export can-execute only, without replacing `VisibleItems`. Run focused tests and expect PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsContracts.cs src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsViewModel.cs tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewModelTests.cs
git commit -m "feat: track share analytics selection"
```

### Task 3: Implement generation-fenced bulk selection

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsViewModel.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewModelTests.cs`

- [ ] **Step 1: Write failing range and tri-state tests**

Test closed ranges, end truncation, invalid inputs, all/invert/clear scoped to current results, hidden selections, and header behavior. For every empty, nonnumeric, nonpositive, reversed, or fully out-of-range interval, capture the original selected-ID set and assert it remains byte-for-byte equal while `StatusMessage` becomes the agreed stable Chinese validation message rather than throwing:

```csharp
vm.RangeStartText = "2";
vm.RangeEndText = "4";
await vm.SelectRangeCommand.ExecuteAsync(null);
Assert.Equal(new[] { "id-2", "id-3", "id-4" }, vm.SelectedShareIdsForTest);
```

Also assert empty-result header is unchecked/disabled; partial header click fills the current result; full header click clears it.

- [ ] **Step 2: Write failing stale-completion and bounded-notification tests**

Use a controllable background scheduler/delay seam. Start a 100,000-row selection, then change account, refresh generation, metric, search, status, sort, or operation generation. Release the old calculation and assert it does not update rows or IDs. Count at most one `VisibleItems`, one selected-summary, and one export-can-execute notification per successful bulk operation.

- [ ] **Step 3: Run focused tests and verify failure**

Run the ShareAnalyticsViewModel test filter. Expected: FAIL because range/bulk commands do not exist.

- [ ] **Step 4: Implement batch commands and generation fence**

Add async commands for range, all, invert, clear, and header toggle. Each operation captures:

```csharp
record SelectionFence(
    Guid AccountId, long SnapshotGeneration, AccountShareMetric Metric,
    string Search, ShareAnalyticsStatusFilter Status,
    AccountShareSortColumn Sort, bool Descending, long OperationGeneration);
```

Build the new HashSet and row projection off the UI thread. Apply once only if every captured field still matches. Increment/cancel the operation generation when any captured input changes. Gate commands with `IsSelectionBusy`.

- [ ] **Step 5: Run focused tests and expect PASS**

Run the ShareAnalyticsViewModel tests. Confirm all range, identity, race, and notification cases pass.

- [ ] **Step 6: Commit**

```powershell
git add src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsViewModel.cs tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewModelTests.cs
git commit -m "feat: add bulk share analytics selection"
```

### Task 4: Build the streaming share-analytics workbook exporter

**Files:**
- Create: `src/BaiduBatchTransfer.Application/ShareAnalytics/ShareAnalyticsWorkbookExportService.cs`
- Modify: `src/BaiduBatchTransfer.Application/Jobs/ReplacementWorkbookModels.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Excel/OpenXmlReplacementWorkbookFilePublisher.cs`
- Create: `tests/BaiduBatchTransfer.Application.Tests/ShareAnalytics/ShareAnalyticsWorkbookExportServiceTests.cs`
- Modify: `tests/BaiduBatchTransfer.Infrastructure.Tests/Excel/OpenXmlReplacementWorkbookFilePublisherTests.cs`

- [ ] **Step 1: Write failing workbook-contract tests**

Define an immutable request containing account-independent display data only: metric, five frozen dates, ordered selected projections, destination directory, and timestamp. Capture `IReplacementWorkbookFilePublisher` input and assert:

```csharp
Assert.Equal(
    new[] { "序号", "指标", "历史累计", "新增", "今天", "昨天", "08-31", "08-30", "08-29", "链接", "标题", "状态" },
    publisher.Layout.Headers);
```

Verify continuous export numbering, sort order supplied by the caller, blank missing values, invariant decimal text, negative sign, formula-injection-safe plain text through the real writer, fixed status text, filename collisions, cancellation, and no final half-file. Use a UTC `TimeProvider` instant with a known local offset and assert the filename timestamp is produced from local time rather than UTC. Add one exact cell assertion for every metric: `Transfer -> 转存`, `View -> 浏览`, and `Download -> 下载`; never serialize enum names.

- [ ] **Step 2: Run the focused Application tests and verify failure**

Run:

```powershell
dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj --filter "FullyQualifiedName~ShareAnalyticsWorkbookExportServiceTests"
```

Expected: FAIL because the service does not exist.

- [ ] **Step 3: Implement the immutable export contract and service**

Move `ReplacementWorkbookFilePublishErrorCode` and `ReplacementWorkbookFilePublishException` from Infrastructure into `BaiduBatchTransfer.Application.Jobs` beside `IReplacementWorkbookFilePublisher`, preserving the public names and behavior; update the Infrastructure publisher/tests to use that Application-owned contract. This avoids an Application-to-Infrastructure dependency while allowing callers to distinguish `DestinationExists`.

Create request/row/result types plus `IShareAnalyticsWorkbookExportService`. Build a `ReplacementWorkbookLayout`, stream `ReplacementWorkbookRow` objects, map metrics explicitly to Chinese, and call the existing `IReplacementWorkbookFilePublisher.PublishAsync`. Convert the injected clock instant to local time and generate a sanitized base filename such as `分享数据-20260902-173015.xlsx`. Probe candidates using suffixes `(2)`, `(3)` and so on, and retry only `ReplacementWorkbookFilePublishException` with `DestinationExists`, with a finite upper bound; never overwrite an existing file. Keep all other publisher failures unchanged so atomic cleanup remains centralized.

- [ ] **Step 4: Run Application and Infrastructure workbook tests**

Run:

```powershell
dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj --filter "FullyQualifiedName~ShareAnalyticsWorkbookExportServiceTests|FullyQualifiedName~ReplacementWorkbookExportTests"
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj --filter "FullyQualifiedName~OpenXmlReplacementWorkbookFilePublisherTests"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/BaiduBatchTransfer.Application/ShareAnalytics/ShareAnalyticsWorkbookExportService.cs src/BaiduBatchTransfer.Application/Jobs/ReplacementWorkbookModels.cs src/BaiduBatchTransfer.Infrastructure/Excel/OpenXmlReplacementWorkbookFilePublisher.cs tests/BaiduBatchTransfer.Application.Tests/ShareAnalytics/ShareAnalyticsWorkbookExportServiceTests.cs tests/BaiduBatchTransfer.Infrastructure.Tests/Excel/OpenXmlReplacementWorkbookFilePublisherTests.cs
git commit -m "feat: export selected share analytics"
```

### Task 5: Wire export into the page safely

**Files:**
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsViewModel.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewModelTests.cs`

- [ ] **Step 1: Write failing command and bootstrap tests**

Assert export is disabled with no selection, disabled again while an export is running, directory cancellation creates nothing, selection freezes before async writing, success message contains only row count and safe filename, and exceptions produce the stable Chinese error. Select rows, hide part of them with search/status, then assert export still includes every selected row ordered by the current metric/sort plus shared-time/Ordinal-ShareId tie-breakers rather than only `currentResults`.

Add a deterministic frozen-snapshot race test: start export with a controllable directory dialog or exporter blocked after request capture, advance the Beijing clock across midnight, apply a same-account refreshed snapshot with changed daily values, then change metric and sort. Release export and assert its five dates/three headers, row values, selected IDs, metric label, and row order are exactly the originally captured request. Separately switch account, call `Leave`, and call `Dispose`; complete the old exporter and assert each lifecycle boundary cancels work and prevents late status from overwriting the current page.

- [ ] **Step 2: Run focused tests and verify failure**

Run AppBootstrap and ShareAnalyticsViewModel filters. Expected: FAIL because dependencies and command are absent.

- [ ] **Step 3: Inject and implement export orchestration**

Register `IShareAnalyticsWorkbookExportService`. Inject it and `IResultSaveDialog` into the view model. Add `ExportCommand`, `IsExporting`, and export operation generation/cancellation. Freeze account, metric, dates, sorting comparator, selected IDs, and complete projected rows from the account snapshot—not the filtered result—before awaiting the directory/exporter. Cancel and increment the export generation on account switch, `Leave`, and `Dispose`; `IsExporting` disables duplicate export.

- [ ] **Step 4: Pass focused tests**

Run the two focused App test filters. Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/BaiduBatchTransfer.App/AppBootstrap.cs src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsViewModel.cs tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewModelTests.cs
git commit -m "feat: connect analytics export command"
```

### Task 6: Rebuild the WPF toolbar and DataGrid

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsView.xaml`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewStructureTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewModelTests.cs`

- [ ] **Step 1: Write the failing XAML structure test**

Parse XAML and inspect the DataGrid column elements. Assert the semantic order:

```csharp
Assert.Equal(
    new[] { "选择", "序号", "历史累计", "新增", "今天", "昨天", "动态日期", "动态日期", "动态日期", "链接", "标题", "状态" },
    ReadColumnRoles(document));
Assert.DoesNotContain(columns, IsShareTimeColumn);
```

Also assert uniform column-header height plus horizontal and vertical centering, `HorizontalScrollBarVisibility="Visible"`, two-way selection binding, toolbar commands/bindings, right alignment for every numeric data cell, left alignment for title data, and a wide title cell template with `TextTrimming="CharacterEllipsis"` plus a tooltip exposing the full title. Update the existing view-model/XAML contract assertions to expect seven visible sortable metric columns, no `SharedAt` visual header binding, and the new title width; these assertions must fail against the old view before XAML is changed.

- [ ] **Step 2: Run the structure test and verify failure**

Run:

```powershell
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter "FullyQualifiedName~ShareAnalyticsViewStructureTests"
```

Expected: FAIL against the old table.

- [ ] **Step 3: Implement the final toolbar and column order**

Add the row:

```text
已选 N 条 / 共 M 条 | 从 [1] 到 [50] | 选取 | 全选 | 反选 | 清空选择 | 导出表格
```

Rebuild columns as checkbox, sequence, eight statistical/link columns, wide title, status. Remove the visual share-time column. Use one centered header template for plain and sortable headers, right-align numbers, left-align title, retain sort glyph/highlight, and set explicit total widths plus `ScrollViewer.HorizontalScrollBarVisibility="Visible"`. Truncate long title cells with an ellipsis and bind the full title as the cell tooltip.

- [ ] **Step 4: Pass XAML and view-model tests**

Run ShareAnalyticsViewStructureTests and ShareAnalyticsViewModelTests. Expected: PASS, including the updated seven-sort-column and title-width assertions.

- [ ] **Step 5: Commit**

```powershell
git add src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsView.xaml tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewStructureTests.cs tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewModelTests.cs
git commit -m "feat: rebuild share analytics table"
```

### Task 7: Regression and real-window verification

**Files:**
- Modify only if a verified defect is found.

- [ ] **Step 1: Run scoped regression suites**

```powershell
dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduBatchTransfer.BaiduAdapter.Tests.csproj --filter "FullyQualifiedName~BaiduAccountShareAnalyticsClientTests"
dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj --filter "FullyQualifiedName~ShareAnalytics|FullyQualifiedName~ReplacementWorkbookExportTests"
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj --filter "FullyQualifiedName~OpenXmlReplacementWorkbookFilePublisherTests"
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter "FullyQualifiedName~ShareAnalytics|FullyQualifiedName~AppBootstrapTests"
```

Expected: all scoped tests PASS.

- [ ] **Step 2: Build the application**

```powershell
dotnet build src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj -c Release
```

Expected: build succeeds with no new warnings.

- [ ] **Step 3: Launch and visually verify the real window**

Open the Release app. Confirm cached rows appear immediately, titles no longer show `0`, headers share one baseline, the bottom horizontal scrollbar reaches title/status, sorting retains the yellow column, range/all/invert/clear behave visibly, and export creates one readable `.xlsx` containing only selected rows.

- [ ] **Step 4: Inspect the exported workbook**

Open the workbook with the Open XML verifier/test helper. Confirm 12 headers, frozen date labels, selected ordering, safe text links/titles, and the selected row count.

- [ ] **Step 5: Run diff hygiene and commit any final test-only corrections**

```powershell
git diff --check
git status --short
```

Expected: no whitespace errors and no unrelated files staged.
