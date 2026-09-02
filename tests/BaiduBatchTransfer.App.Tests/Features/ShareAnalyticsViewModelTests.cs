using BaiduBatchTransfer.App.Features.ShareAnalytics;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.ShareAnalytics;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ShareAnalyticsViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ShareAnalyticsViewUsesOnlyDeclaredNamedThemeStyles()
    {
        var root = FindRepositoryRoot();
        var view = File.ReadAllText(Path.Combine(root, "src", "BaiduBatchTransfer.App", "Features", "ShareAnalytics", "ShareAnalyticsView.xaml"));
        var theme = File.ReadAllText(Path.Combine(root, "src", "BaiduBatchTransfer.App", "Styles", "SuperPanTheme.xaml"));
        foreach (var key in new[] { "PrimaryButton", "SecondaryButtonStyle", "BorderedPanelStyle" })
        {
            Assert.Contains($"StaticResource {key}", view, StringComparison.Ordinal);
            Assert.Contains($"x:Key=\"{key}\"", theme, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("PrimaryButtonStyle", view, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusOptionRendersItsChineseLabelInTheComboBoxSelection()
    {
        var option = new ShareAnalyticsStatusOption(ShareAnalyticsStatusFilter.All, "全部状态");

        Assert.Equal("全部状态", option.ToString());
    }

    [Fact]
    public void LegacyZeroPlaceholderTitleDisplaysStableShareId()
    {
        var row = ProjectRow("stable-share-id", " 0 ");

        Assert.Equal("stable-share-id", row.Title);
    }

    [Theory]
    [InlineData("00")]
    [InlineData("42")]
    [InlineData("123456")]
    public void OtherNumericTitlesRemainUnchanged(string title)
    {
        var row = ProjectRow("stable-share-id", title);

        Assert.Equal(title, row.Title);
    }

    [Fact]
    public async Task ExportIsDisabledWithoutASelectionAndEnabledForAnActiveAccountSelection()
    {
        var account = Guid.NewGuid();
        var viewModel = Create(new FakeSync(account, Snapshot(account, "alpha", Now)));
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();

        Assert.False(viewModel.ExportCommand.CanExecute(null));

        Assert.Single(viewModel.VisibleItems).IsSelected = true;

        Assert.True(viewModel.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task ExportDisablesReentryWhileDirectoryChoiceIsPending()
    {
        var account = Guid.NewGuid();
        var dialog = new GateSaveDialog();
        var exporter = new RecordingExporter();
        var viewModel = Create(
            new FakeSync(account, Snapshot(account, "alpha", Now)),
            exportService: exporter,
            saveDialog: dialog);
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();
        Assert.Single(viewModel.VisibleItems).IsSelected = true;

        var exporting = viewModel.ExportCommand.ExecuteAsync(null);
        await dialog.Started.Task;

        Assert.True(viewModel.IsExporting);
        Assert.False(viewModel.ExportCommand.CanExecute(null));
        dialog.Complete(null);
        await exporting;
        Assert.False(viewModel.IsExporting);
        Assert.Empty(exporter.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CancelingTheDestinationDialogDoesNotCallTheExporter(string? directory)
    {
        var account = Guid.NewGuid();
        var exporter = new RecordingExporter();
        var viewModel = Create(
            new FakeSync(account, Snapshot(account, "alpha", Now)),
            exportService: exporter,
            saveDialog: new ImmediateSaveDialog(directory));
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();
        Assert.Single(viewModel.VisibleItems).IsSelected = true;
        var originalStatus = viewModel.StatusMessage;

        await viewModel.ExportCommand.ExecuteAsync(null);

        Assert.Empty(exporter.Requests);
        Assert.Equal(originalStatus, viewModel.StatusMessage);
        Assert.False(viewModel.IsExporting);
    }

    [Fact]
    public async Task SuccessfulExportReportsOnlyTheCountAndASanitizedBoundedFileName()
    {
        var account = Guid.NewGuid();
        var unsafeFileName = new string('x', 300) + "\u0001.xlsx";
        var exporter = new RecordingExporter((_, _) => Task.FromResult(
            new ShareAnalyticsWorkbookExportResult(
                Path.Combine(@"C:\private\account-session", unsafeFileName), 1, 1)));
        var viewModel = Create(
            new FakeSync(account, Snapshot(account, "alpha", Now)),
            exportService: exporter,
            saveDialog: new ImmediateSaveDialog(@"C:\private\account-session"));
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();
        Assert.Single(viewModel.VisibleItems).IsSelected = true;

        await viewModel.ExportCommand.ExecuteAsync(null);

        Assert.Contains("1", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\private", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("account-session", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\u0001', viewModel.StatusMessage);
        Assert.InRange(viewModel.StatusMessage.Length, 1, 180);
    }

    [Fact]
    public async Task ExportFailureIsContainedAndUsesOnlyTheStableMessage()
    {
        var account = Guid.NewGuid();
        var exporter = new RecordingExporter((_, _) =>
            Task.FromException<ShareAnalyticsWorkbookExportResult>(
                new InvalidOperationException(@"private C:\secret\output.xlsx session=abc")));
        var viewModel = Create(
            new FakeSync(account, Snapshot(account, "alpha", Now)),
            exportService: exporter,
            saveDialog: new ImmediateSaveDialog(@"C:\secret"));
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();
        Assert.Single(viewModel.VisibleItems).IsSelected = true;

        var error = await Xunit.Record.ExceptionAsync(() => viewModel.ExportCommand.ExecuteAsync(null));

        Assert.Null(error);
        Assert.Equal("导出失败，请重试。", viewModel.StatusMessage);
        Assert.False(viewModel.IsExporting);
    }

    [Fact]
    public async Task ExportIncludesSelectionsHiddenBySearchAndStatusAndUsesCurrentStableSort()
    {
        var account = Guid.NewGuid();
        var date = new DateOnly(2026, 9, 2);
        var snapshot = new AccountShareCachedSnapshot(account, Now,
        [
            ExportRecord("zeta", "Z", AccountShareRemoteStatus.Invalid, Now.AddHours(-2), date, transferTotal: 40, transferToday: 20),
            ExportRecord("alpha", "A", AccountShareRemoteStatus.Valid, Now.AddHours(-1), date, transferTotal: 40, transferToday: 20),
            ExportRecord("beta", "B", AccountShareRemoteStatus.Valid, Now.AddHours(-1), date, transferTotal: 40, transferToday: 20),
        ]);
        var exporter = new RecordingExporter();
        var viewModel = Create(
            new FakeSync(account, snapshot),
            exportService: exporter,
            saveDialog: new ImmediateSaveDialog("exports"));
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();
        foreach (var row in viewModel.VisibleItems) row.IsSelected = true;
        viewModel.SortCommand.Execute(AccountShareSortColumn.Today);
        viewModel.StatusFilter = ShareAnalyticsStatusFilter.Valid;
        viewModel.SearchText = "A";

        await viewModel.ExportCommand.ExecuteAsync(null);

        var request = Assert.Single(exporter.Requests);
        Assert.Equal(3, request.SelectedRows.Length);
        Assert.Equal(
            ["https://pan.baidu.com/s/alpha", "https://pan.baidu.com/s/beta", "https://pan.baidu.com/s/zeta"],
            request.SelectedRows.Select(row => row.ShareLink));
    }

    [Fact]
    public async Task ExportFreezesClockSnapshotMetricValuesIdsAndOrderBeforeAwaitingTheDialog()
    {
        var account = Guid.NewGuid();
        var initialDate = new DateOnly(2026, 9, 1);
        var initial = new AccountShareCachedSnapshot(account, Now,
        [
            ExportRecord("alpha", "old alpha", AccountShareRemoteStatus.Valid, Now.AddHours(-2), initialDate, 100, 10),
            ExportRecord("beta", "old beta", AccountShareRemoteStatus.Valid, Now.AddHours(-1), initialDate, 200, 30),
        ]);
        var refreshed = new AccountShareCachedSnapshot(account, Now.AddMinutes(1),
        [
            ExportRecord("alpha", "new alpha", AccountShareRemoteStatus.Valid, Now.AddHours(2), initialDate.AddDays(1), 900, 90),
            ExportRecord("beta", "new beta", AccountShareRemoteStatus.Valid, Now.AddHours(1), initialDate.AddDays(1), 800, 80),
        ]);
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 9, 1, 15, 59, 0, TimeSpan.Zero));
        var dialog = new GateSaveDialog();
        var exporter = new RecordingExporter();
        var viewModel = Create(
            new FakeSync(account, initial, refreshed),
            timeProvider: time,
            exportService: exporter,
            saveDialog: dialog);
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();
        foreach (var row in viewModel.VisibleItems) row.IsSelected = true;
        viewModel.SortCommand.Execute(AccountShareSortColumn.Today);

        var exporting = viewModel.ExportCommand.ExecuteAsync(null);
        await dialog.Started.Task;
        time.Advance(TimeSpan.FromMinutes(2));
        await viewModel.RefreshCommand.ExecuteAsync(null);
        viewModel.SelectMetricCommand.Execute(AccountShareMetric.View);
        viewModel.SortCommand.Execute(AccountShareSortColumn.HistoricalTotal);
        dialog.Complete("exports");
        await exporting;

        var request = Assert.Single(exporter.Requests);
        Assert.Equal(AccountShareMetric.Transfer, request.Metric);
        Assert.Equal(
            new[] { new DateOnly(2026, 9, 1), new(2026, 8, 31), new(2026, 8, 30), new(2026, 8, 29), new(2026, 8, 28) },
            request.Dates.ToArray());
        Assert.Equal(["old beta", "old alpha"], request.SelectedRows.Select(row => row.Title));
        Assert.Equal([30L, 10L], request.SelectedRows.Select(row => row.DailyValues[0]));
        Assert.Equal(
            ["https://pan.baidu.com/s/beta", "https://pan.baidu.com/s/alpha"],
            request.SelectedRows.Select(row => row.ShareLink));
    }

    [Theory]
    [InlineData(ExportInvalidation.AccountSwitch)]
    [InlineData(ExportInvalidation.Leave)]
    [InlineData(ExportInvalidation.Dispose)]
    public async Task LifecycleBoundaryCancelsExportAndRejectsLateCompletion(ExportInvalidation invalidation)
    {
        var account = Guid.NewGuid();
        var otherAccount = Guid.NewGuid();
        var exporter = new GateExporter();
        var viewModel = Create(
            new MultiAccountSync(Snapshot(account, "alpha", Now), Snapshot(otherAccount, "other", Now)),
            exportService: exporter,
            saveDialog: new ImmediateSaveDialog("exports"));
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();
        Assert.Single(viewModel.VisibleItems).IsSelected = true;

        var exporting = viewModel.ExportCommand.ExecuteAsync(null);
        await exporter.Started.Task;
        switch (invalidation)
        {
            case ExportInvalidation.AccountSwitch:
                viewModel.SelectAccount(otherAccount, "其他账号");
                await WaitUntilAsync(() => viewModel.VisibleItems.Count == 1 &&
                    viewModel.VisibleItems[0].ShareId == "other");
                Assert.Single(viewModel.VisibleItems).IsSelected = true;
                Assert.True(viewModel.ExportCommand.CanExecute(null));
                break;
            case ExportInvalidation.Leave:
                viewModel.Leave();
                break;
            case ExportInvalidation.Dispose:
                viewModel.Dispose();
                break;
        }
        var expectedStatus = viewModel.StatusMessage;
        Assert.True(exporter.CancellationToken.IsCancellationRequested);
        Assert.False(viewModel.IsExporting);

        exporter.Complete(new ShareAnalyticsWorkbookExportResult(@"C:\private\late.xlsx", 999, 1));
        await exporting;

        Assert.Equal(expectedStatus, viewModel.StatusMessage);
        Assert.False(viewModel.IsExporting);
    }

    [Fact]
    public async Task OperationCanceledExportDoesNotShowFailure()
    {
        var account = Guid.NewGuid();
        var exporter = new RecordingExporter((_, _) =>
            Task.FromCanceled<ShareAnalyticsWorkbookExportResult>(new CancellationToken(canceled: true)));
        var viewModel = Create(
            new FakeSync(account, Snapshot(account, "alpha", Now)),
            exportService: exporter,
            saveDialog: new ImmediateSaveDialog("exports"));
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();
        Assert.Single(viewModel.VisibleItems).IsSelected = true;
        var expectedStatus = viewModel.StatusMessage;

        await viewModel.ExportCommand.ExecuteAsync(null);

        Assert.Equal(expectedStatus, viewModel.StatusMessage);
        Assert.False(viewModel.IsExporting);
    }

    [Fact]
    public async Task ExportUsesShareIdForAnExactLegacyZeroTitle()
    {
        var account = Guid.NewGuid();
        var exporter = new RecordingExporter();
        var snapshot = new AccountShareCachedSnapshot(account, Now,
        [
            new AccountShareCachedRecord(
                "stable-id", "0", new Uri("https://pan.baidu.com/s/stable-id"), Now,
                AccountShareRemoteStatus.Valid,
                Enum.GetValues<AccountShareMetric>()
                    .Select(metric => new AccountShareMetricHistory(metric, 1))
                    .ToArray()),
        ]);
        var viewModel = Create(
            new FakeSync(account, snapshot),
            exportService: exporter,
            saveDialog: new ImmediateSaveDialog("exports"));
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();
        Assert.Single(viewModel.VisibleItems).IsSelected = true;

        await viewModel.ExportCommand.ExecuteAsync(null);

        Assert.Equal("stable-id", Assert.Single(Assert.Single(exporter.Requests).SelectedRows).Title);
    }

    [Fact]
    public void RowPublishesSequenceAndTwoWaySelectionCallback()
    {
        var projected = ProjectRow("stable-share-id", "标题").Projected;
        var callbacks = new List<(string ShareId, bool IsSelected)>();
        var row = new ShareAnalyticsRow(
            projected,
            7,
            false,
            (shareId, selected) => callbacks.Add((shareId, selected)));
        var changed = new List<string?>();
        row.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        row.IsSelected = true;

        Assert.Equal(7, row.Sequence);
        Assert.True(row.IsSelected);
        Assert.Equal([("stable-share-id", true)], callbacks);
        Assert.Equal([nameof(ShareAnalyticsRow.IsSelected)], changed);
    }

    [Fact]
    public void ShareAnalyticsColumnsUseCompactFixedWidthsInsteadOfCollapsingTheTitle()
    {
        var root = FindRepositoryRoot();
        var view = File.ReadAllText(Path.Combine(root, "src", "BaiduBatchTransfer.App", "Features", "ShareAnalytics", "ShareAnalyticsView.xaml"));

        Assert.DoesNotContain("<DataGridTemplateColumn Width=\"2*\">", view, StringComparison.Ordinal);
        Assert.Contains("<DataGridTemplateColumn Width=\"160\">", view, StringComparison.Ordinal);
    }

    [Fact]
    public void ShareAnalyticsSortableHeadersAreFlatAndExposeCurrentDirection()
    {
        var root = FindRepositoryRoot();
        var view = File.ReadAllText(Path.Combine(root, "src", "BaiduBatchTransfer.App", "Features", "ShareAnalytics", "ShareAnalyticsView.xaml"));

        Assert.Contains("x:Key=\"ShareAnalyticsColumnHeaderStyle\"", view, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"ShareAnalyticsSortButtonStyle\"", view, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"ShareAnalyticsSortedHeaderBrush\" Color=\"#FFF3C4\"", view, StringComparison.Ordinal);
        Assert.Contains("ColumnHeaderStyle=\"{StaticResource ShareAnalyticsColumnHeaderStyle}\"", view, StringComparison.Ordinal);
        Assert.Contains("BorderThickness\" Value=\"0\"", view, StringComparison.Ordinal);
        Assert.Contains("Background\" Value=\"#F3F4F6\"", view, StringComparison.Ordinal);
        Assert.Contains("Text=\"{TemplateBinding Tag}\"", view, StringComparison.Ordinal);
        Assert.Contains("Value=\"↑\"", view, StringComparison.Ordinal);
        Assert.Contains("Value=\"↓\"", view, StringComparison.Ordinal);
        Assert.Contains("Tag=\"{Binding DataContext.SharedAtSortGlyph", view, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"分享时间\"", view, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"历史累计\"", view, StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentSortColumnHighlightsEverySortableCellWithoutChangingGridBehavior()
    {
        var root = FindRepositoryRoot();
        var view = File.ReadAllText(Path.Combine(root, "src", "BaiduBatchTransfer.App", "Features", "ShareAnalytics", "ShareAnalyticsView.xaml"));

        Assert.Contains("x:Key=\"ShareAnalyticsSortedCellBrush\" Color=\"#FFF9E3\"", view, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"ShareAnalyticsSortCellStyle\"", view, StringComparison.Ordinal);
        Assert.Equal(8, view.Split("<DataGridTemplateColumn.CellStyle>", StringSplitOptions.None).Length - 1);
        Assert.Equal(8, view.Split("Binding DataContext.SortColumn", StringSplitOptions.None).Length - 1);
        Assert.Contains("Value=\"{x:Static analytics:AccountShareSortColumn.SharedAt}\"", view, StringComparison.Ordinal);
        Assert.Contains("Value=\"{x:Static analytics:AccountShareSortColumn.PriorDay4}\"", view, StringComparison.Ordinal);
        Assert.Contains("IsReadOnly=\"True\"", view, StringComparison.Ordinal);
        Assert.Contains("EnableRowVirtualization=\"True\" EnableColumnVirtualization=\"True\"", view, StringComparison.Ordinal);
        Assert.Contains("VirtualizingPanel.VirtualizationMode=\"Recycling\"", view, StringComparison.Ordinal);
    }

    [Fact]
    public void ShareAnalyticsGridUsesThinNeutralLinesInsteadOfDefaultBlackCellFrames()
    {
        var root = FindRepositoryRoot();
        var view = File.ReadAllText(Path.Combine(root, "src", "BaiduBatchTransfer.App", "Features", "ShareAnalytics", "ShareAnalyticsView.xaml"));

        Assert.Contains("GridLinesVisibility=\"All\"", view, StringComparison.Ordinal);
        Assert.Contains("HorizontalGridLinesBrush=\"#E5E7EB\"", view, StringComparison.Ordinal);
        Assert.Contains("VerticalGridLinesBrush=\"#E5E7EB\"", view, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"ShareAnalyticsCellStyle\"", view, StringComparison.Ordinal);
        Assert.Contains("CellStyle=\"{StaticResource ShareAnalyticsCellStyle}\"", view, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"ShareAnalyticsSortCellStyle\"", view, StringComparison.Ordinal);
        Assert.Contains("Property=\"BorderBrush\" Value=\"#E5E7EB\"", view, StringComparison.Ordinal);
        Assert.Contains("Property=\"BorderThickness\" Value=\"0\"", view, StringComparison.Ordinal);
    }

    [Fact]
    public void ShareAnalyticsHeaderDisplaysThePublishedLocalCacheStatus()
    {
        var root = FindRepositoryRoot();
        var view = File.ReadAllText(Path.Combine(root, "src", "BaiduBatchTransfer.App", "Features", "ShareAnalytics", "ShareAnalyticsView.xaml"));

        Assert.Contains("Text=\"{Binding CacheStatusText}\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding LastSuccessfulSyncText}\"", view, StringComparison.Ordinal);
    }

    [Fact]
    public void SortCommandPublishesCurrentColumnAndDirectionForTheHeader()
    {
        var sortColumnProperty = typeof(ShareAnalyticsViewModel).GetProperty("SortColumn");
        var sortDescendingProperty = typeof(ShareAnalyticsViewModel).GetProperty("SortDescending");
        var sharedAtGlyphProperty = typeof(ShareAnalyticsViewModel).GetProperty("SharedAtSortGlyph");
        var todayGlyphProperty = typeof(ShareAnalyticsViewModel).GetProperty("TodaySortGlyph");

        Assert.NotNull(sortColumnProperty);
        Assert.NotNull(sortDescendingProperty);
        Assert.NotNull(sharedAtGlyphProperty);
        Assert.NotNull(todayGlyphProperty);

        var account = Guid.NewGuid();
        var viewModel = Create(new FakeSync(account, new AccountShareCachedSnapshot(account, null, [])));
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        viewModel.SortCommand.Execute(AccountShareSortColumn.SharedAt);

        Assert.Equal(AccountShareSortColumn.SharedAt, sortColumnProperty.GetValue(viewModel));
        Assert.Equal(false, sortDescendingProperty.GetValue(viewModel));
        Assert.Equal("↑", sharedAtGlyphProperty.GetValue(viewModel));
        Assert.Equal(string.Empty, todayGlyphProperty.GetValue(viewModel));
        Assert.Contains("SortColumn", changed);
        Assert.Contains("SortDescending", changed);

        changed.Clear();
        viewModel.SortCommand.Execute(AccountShareSortColumn.Today);

        Assert.Equal(AccountShareSortColumn.Today, sortColumnProperty.GetValue(viewModel));
        Assert.Equal(true, sortDescendingProperty.GetValue(viewModel));
        Assert.Equal(string.Empty, sharedAtGlyphProperty.GetValue(viewModel));
        Assert.Equal("↓", todayGlyphProperty.GetValue(viewModel));
        Assert.Contains("SortColumn", changed);
        Assert.Contains("SortDescending", changed);
    }

    [Fact]
    public async Task CacheStatusReportsAppliedSnapshotAndResetsWithNotificationOnAccountSwitch()
    {
        var account = Guid.NewGuid();
        var cachedAt = Now.AddHours(-22);
        var snapshot = new AccountShareCachedSnapshot(account, cachedAt,
        [
            Record("alpha", AccountShareRemoteStatus.Valid, 10),
            Record("beta", AccountShareRemoteStatus.Invalid, 20),
        ]);
        var viewModel = Create(new FakeSync(account, snapshot));
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.Equal("尚无本地缓存", viewModel.CacheStatusText);
        viewModel.SelectAccount(account, "账号");
        Assert.Contains("CacheStatusText", changed);

        changed.Clear();
        await viewModel.EnterAsync();

        Assert.Equal($"本地缓存｜更新于 {cachedAt.ToLocalTime():yyyy-MM-dd HH:mm}｜2 条", viewModel.CacheStatusText);
        Assert.Contains("CacheStatusText", changed);

        changed.Clear();
        viewModel.SelectAccount(null, null);

        Assert.Equal("尚无本地缓存", viewModel.CacheStatusText);
        Assert.Contains("CacheStatusText", changed);
    }

    [Fact]
    public async Task FailedRefreshKeepsThePublishedLocalCacheStatus()
    {
        var account = Guid.NewGuid();
        var cachedAt = Now.AddHours(-22);
        var service = new FakeSync(account, Snapshot(account, "cached", cachedAt))
        {
            SyncResults = [FailureResult]
        };
        var viewModel = Create(service, new FakeDelay());
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();
        var before = viewModel.CacheStatusText;

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(before, viewModel.CacheStatusText);
        Assert.Equal("cached", Assert.Single(viewModel.VisibleItems).Title);
    }

    [Fact]
    public async Task FreshTwentyTwoHourCacheIsAppliedAndSchedulesExactRemainingHourWithoutSync()
    {
        var account = Guid.NewGuid();
        var service = new FakeSync(account, Snapshot(account, "cached", Now.AddHours(-22)));
        var delay = new FakeDelay();
        var viewModel = Create(service, delay);
        viewModel.SelectAccount(account, "账号甲");

        await viewModel.EnterAsync();

        Assert.Equal(new[] { "cached" }, service.AppliedLoadTitles);
        Assert.Equal(0, service.SyncCalls);
        Assert.Equal(TimeSpan.FromHours(1), Assert.Single(delay.Requests).Delay);
        Assert.Equal("cached", Assert.Single(viewModel.VisibleItems).Title);
        Assert.Equal("账号甲", viewModel.CurrentAccountName);
    }

    [Fact]
    public async Task FreshCachePublishesStableDisplayedStatusAfterApplyWithoutSync()
    {
        var account = Guid.NewGuid();
        var service = new FakeSync(account, Snapshot(account, "cached", Now.AddHours(-22)));
        var viewModel = Create(service, new FakeDelay());
        viewModel.SelectAccount(account, "账号");

        await viewModel.EnterAsync();

        Assert.Equal(0, service.SyncCalls);
        Assert.Equal("cached", Assert.Single(viewModel.VisibleItems).Title);
        Assert.Equal("已显示本地缓存。", viewModel.StatusMessage);
    }

    [Fact]
    public async Task CacheWithoutSuccessfulTimestampSyncsImmediately()
    {
        var account = Guid.NewGuid();
        var service = new FakeSync(
            account,
            SnapshotWithoutSync(account, "cached"),
            Snapshot(account, "fresh", Now));
        var delay = new FakeDelay();
        var viewModel = Create(service, delay);
        viewModel.SelectAccount(account, "账号");

        await viewModel.EnterAsync();

        Assert.Equal(1, service.SyncCalls);
        Assert.Equal("fresh", Assert.Single(viewModel.VisibleItems).Title);
        Assert.Equal(TimeSpan.FromHours(23), Assert.Single(delay.Requests).Delay);
    }

    [Fact]
    public async Task CacheExactlyTwentyThreeHoursOldSyncsImmediately()
    {
        var account = Guid.NewGuid();
        var service = new FakeSync(
            account,
            Snapshot(account, "cached", Now.AddHours(-23)),
            Snapshot(account, "fresh", Now));
        var delay = new FakeDelay();
        var viewModel = Create(service, delay);
        viewModel.SelectAccount(account, "账号");

        await viewModel.EnterAsync();

        Assert.Equal(1, service.SyncCalls);
        Assert.Equal("fresh", Assert.Single(viewModel.VisibleItems).Title);
    }

    [Fact]
    public async Task FailedInitialRefreshDoesNotScheduleRetry()
    {
        var account = Guid.NewGuid();
        var service = new FakeSync(account, SnapshotWithoutSync(account, "cached"))
        {
            SyncResults = [FailureResult]
        };
        var delay = new FakeDelay();
        var viewModel = Create(service, delay);
        viewModel.SelectAccount(account, "账号");

        await viewModel.EnterAsync();

        Assert.Equal(1, service.SyncCalls);
        Assert.Empty(delay.Requests);
        Assert.Equal("cached", Assert.Single(viewModel.VisibleItems).Title);
    }

    [Fact]
    public async Task CompletingFreshCacheDelayStartsExactlyOneSync()
    {
        var account = Guid.NewGuid();
        var service = new FakeSync(
            account,
            Snapshot(account, "cached", Now.AddHours(-22)),
            Snapshot(account, "fresh", Now));
        var delay = new FakeDelay();
        var viewModel = Create(service, delay);
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();

        delay.Complete(0);
        await service.WaitForSyncCallsAsync(1);
        await WaitUntilAsync(() => delay.Requests.Count == 2);

        Assert.Equal(1, service.SyncCalls);
        Assert.Equal("fresh", Assert.Single(viewModel.VisibleItems).Title);
        Assert.Equal(TimeSpan.FromHours(23), delay.Requests[1].Delay);
    }

    [Fact]
    public async Task FailedAutomaticDueRefreshDoesNotScheduleRetry()
    {
        var account = Guid.NewGuid();
        var service = new FakeSync(account, Snapshot(account, "cached", Now.AddHours(-22)))
        {
            SyncResults = [FailureResult]
        };
        var delay = new FakeDelay();
        var viewModel = Create(service, delay);
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();

        delay.Complete(0);
        await service.WaitForSyncCallsAsync(1);
        await WaitUntilAsync(() => !viewModel.IsRefreshing);

        Assert.Single(delay.Requests);
        Assert.Equal("分享数据获取失败，请稍后重试。", viewModel.StatusMessage);
    }

    [Fact]
    public async Task LeaveCancelsPendingDelayAndPreventsItsRemoteWork()
    {
        var account = Guid.NewGuid();
        var service = new FakeSync(account, Snapshot(account, "cached", Now.AddHours(-22)));
        var delay = new FakeDelay();
        var viewModel = Create(service, delay);
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();

        viewModel.Leave();

        Assert.True(delay.Requests[0].Token.IsCancellationRequested);
        Assert.Equal(0, service.SyncCalls);
    }

    [Fact]
    public async Task AccountSwitchCancelsPreviousPendingDelay()
    {
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var service = new MultiAccountSync(
            Snapshot(accountA, "a", Now.AddHours(-22)),
            Snapshot(accountB, "b", Now.AddHours(-22)));
        var delay = new FakeDelay();
        var viewModel = Create(service, delay);
        viewModel.SelectAccount(accountA, "甲");
        await viewModel.EnterAsync();

        viewModel.SelectAccount(accountB, "乙");
        await WaitUntilAsync(() => delay.Requests.Count == 2);

        Assert.True(delay.Requests[0].Token.IsCancellationRequested);
        Assert.Equal(0, service.SyncCalls);
    }

    [Fact]
    public async Task AccountSwitchResetsProgressWhileNonCooperativeOldRefreshFinishes()
    {
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var service = new AccountSwitchBlockingSync(
            accountA,
            SnapshotWithoutSync(accountA, "a"),
            Snapshot(accountB, "b", Now));
        var viewModel = Create(service, new FakeDelay());
        viewModel.SelectAccount(accountA, "甲");
        var oldEntry = viewModel.EnterAsync();
        await service.SyncStarted.Task;
        Assert.True(viewModel.IsRefreshing);

        viewModel.SelectAccount(accountB, "乙");

        Assert.False(viewModel.IsRefreshing);
        Assert.Equal("b", Assert.Single(viewModel.VisibleItems).Title);
        service.Release.TrySetResult();
        await oldEntry;
        Assert.False(viewModel.IsRefreshing);
        Assert.Equal("b", Assert.Single(viewModel.VisibleItems).Title);
    }

    [Fact]
    public async Task AccountSelectionClearsSnapshotTimestampAndCounts()
    {
        var account = Guid.NewGuid();
        var viewModel = Create(
            new FakeSync(account, Snapshot(account, "cached", Now.AddHours(-22))),
            new FakeDelay());
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();
        Assert.NotEqual("尚未同步", viewModel.LastSuccessfulSyncText);
        Assert.Equal(1, viewModel.TotalCount);

        viewModel.SelectAccount(null, null);

        Assert.Equal("尚未同步", viewModel.LastSuccessfulSyncText);
        Assert.Equal(0, viewModel.TotalCount);
        Assert.Empty(viewModel.VisibleItems);
    }

    [Fact]
    public async Task ReturningToSameAccountDoesNotJoinCanceledPreviousActivation()
    {
        var account = Guid.NewGuid();
        var service = new BlockingSync(account, SnapshotWithoutSync(account, "cached"));
        var viewModel = Create(service, new FakeDelay());
        viewModel.SelectAccount(account, "账号");
        var firstEntry = viewModel.EnterAsync();
        await service.SyncStarted.Task;

        viewModel.SelectAccount(null, null);
        viewModel.SelectAccount(account, "账号");

        Assert.Equal(2, service.SyncCalls);
        service.Release.TrySetResult();
        await firstEntry;
    }

    [Fact]
    public async Task DisposeCancelsPendingDelayAndPreventsItsRemoteWork()
    {
        var account = Guid.NewGuid();
        var service = new FakeSync(account, Snapshot(account, "cached", Now.AddHours(-22)));
        var delay = new FakeDelay();
        var viewModel = Create(service, delay);
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();

        viewModel.Dispose();

        Assert.True(delay.Requests[0].Token.IsCancellationRequested);
        Assert.Equal(0, service.SyncCalls);
    }

    [Fact]
    public async Task DisposePreventsNonCooperativeInFlightSyncFromApplyingRowsOrStatus()
    {
        var account = Guid.NewGuid();
        var service = new BlockingSync(
            account,
            new AccountShareCachedSnapshot(account, null, []),
            Snapshot(account, "fresh", Now));
        var viewModel = Create(service, new FakeDelay());
        viewModel.SelectAccount(account, "账号");
        var entering = viewModel.EnterAsync();
        await service.SyncStarted.Task;
        var statusBeforeDispose = viewModel.StatusMessage;

        viewModel.Dispose();
        service.Release.TrySetResult();
        await entering;

        Assert.Empty(viewModel.VisibleItems);
        Assert.Equal(statusBeforeDispose, viewModel.StatusMessage);
    }

    [Fact]
    public async Task ManualRefreshJoinsRunningAutomaticRefreshWithoutSecondSync()
    {
        var account = Guid.NewGuid();
        var service = new BlockingSync(account, Snapshot(account, "cached", Now.AddHours(-23)));
        var viewModel = Create(service, new FakeDelay());
        viewModel.SelectAccount(account, "账号");
        var entering = viewModel.EnterAsync();
        await service.SyncStarted.Task;

        var manual = viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.False(manual.IsCompleted);
        Assert.Equal(1, service.SyncCalls);
        service.Release.TrySetResult();
        await Task.WhenAll(entering, manual);
        Assert.Equal(1, service.SyncCalls);
    }

    [Fact]
    public async Task ManualRefreshJoiningFailedAutomaticDueFlightSchedulesOneRetry()
    {
        var account = Guid.NewGuid();
        var service = new BlockingSync(account, Snapshot(account, "cached", Now.AddHours(-22)))
        {
            SyncResult = FailureResult
        };
        var delay = new FakeDelay();
        var viewModel = Create(service, delay);
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();
        delay.Complete(0);
        await service.SyncStarted.Task;

        var manual = viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.False(manual.IsCompleted);
        Assert.Equal(1, service.SyncCalls);
        service.Release.TrySetResult();
        await manual;
        await WaitUntilAsync(() => delay.Requests.Count == 2);
        Assert.Equal(1, service.SyncCalls);
        Assert.Equal(TimeSpan.FromHours(1), delay.Requests[1].Delay);
    }

    [Fact]
    public async Task CancelingManualCommandStopsOnlyCallerWaitAndNotSharedSync()
    {
        var account = Guid.NewGuid();
        var service = new BlockingSync(account, Snapshot(account, "cached", Now.AddHours(-22)));
        var viewModel = Create(service, new FakeDelay());
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();

        var manual = viewModel.RefreshCommand.ExecuteAsync(null);
        await service.SyncStarted.Task;
        viewModel.RefreshCommand.Cancel();
        await manual;

        Assert.False(service.SyncCancellationToken.IsCancellationRequested);
        Assert.True(viewModel.IsRefreshing);
        service.Release.TrySetResult();
        await WaitUntilAsync(() => !viewModel.IsRefreshing);
    }

    [Fact]
    public async Task FailedFreshManualRefreshRestoresOriginalDueTimeAndPreservesCache()
    {
        var account = Guid.NewGuid();
        var time = new MutableTimeProvider(Now);
        var service = new FakeSync(account, Snapshot(account, "cached", Now.AddHours(-22)))
        {
            SyncResults = [FailureResult]
        };
        var delay = new FakeDelay();
        var viewModel = Create(service, delay, time);
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();
        time.Advance(TimeSpan.FromMinutes(30));

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.True(delay.Requests[0].Token.IsCancellationRequested);
        Assert.Equal(TimeSpan.FromMinutes(30), delay.Requests[1].Delay);
        Assert.Equal("cached", Assert.Single(viewModel.VisibleItems).Title);
        Assert.Equal(Now.ToLocalTime().AddHours(-22).ToString("yyyy-MM-dd HH:mm"), viewModel.LastSuccessfulSyncText);
        Assert.Equal("分享数据获取失败，请稍后重试。", viewModel.StatusMessage);
    }

    [Fact]
    public async Task FailedStaleManualRefreshSchedulesOneHourRetryOnlyOnce()
    {
        var account = Guid.NewGuid();
        var time = new MutableTimeProvider(Now);
        var service = new FakeSync(account, Snapshot(account, "cached", Now.AddHours(-22)))
        {
            SyncResults = [FailureResult, FailureResult]
        };
        var delay = new FakeDelay();
        var viewModel = Create(service, delay, time);
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();
        time.Advance(TimeSpan.FromHours(2));

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(TimeSpan.FromHours(1), delay.Requests[1].Delay);
        delay.Complete(1);
        await service.WaitForSyncCallsAsync(2);
        await WaitUntilAsync(() => !viewModel.IsRefreshing);
        Assert.Equal(2, delay.Requests.Count);
    }

    [Fact]
    public async Task SuccessfulManualRefreshSchedulesNewTwentyThreeHourDelay()
    {
        var account = Guid.NewGuid();
        var service = new FakeSync(
            account,
            Snapshot(account, "cached", Now.AddHours(-22)),
            Snapshot(account, "fresh", Now));
        var delay = new FakeDelay();
        var viewModel = Create(service, delay);
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.True(delay.Requests[0].Token.IsCancellationRequested);
        Assert.Equal(TimeSpan.FromHours(23), delay.Requests[1].Delay);
        Assert.Equal("fresh", Assert.Single(viewModel.VisibleItems).Title);
    }

    [Fact]
    public async Task FirstSyncAndThrownRefreshFailureUseStableChineseMessagesWithoutLeaking()
    {
        var account = Guid.NewGuid();
        var service = new BlockingSync(account, SnapshotWithoutSync(account, "cached")) { ThrowOnRelease = true };
        var viewModel = Create(service, new FakeDelay());
        viewModel.SelectAccount(account, "账号");
        var entering = viewModel.EnterAsync();
        await service.SyncStarted.Task;

        Assert.Equal("正在首次同步分享数据…", viewModel.StatusMessage);
        service.Release.TrySetResult();
        await entering;

        Assert.Equal("分享数据获取失败，请稍后重试。", viewModel.StatusMessage);
        Assert.Equal("cached", Assert.Single(viewModel.VisibleItems).Title);
    }

    [Fact]
    public async Task MetricFilterSearchAndNumericSortRebuildReadOnlyRows()
    {
        var account = Guid.NewGuid();
        var snapshot = new AccountShareCachedSnapshot(account, Now,
        [
            Record("alpha", AccountShareRemoteStatus.Valid, 10),
            Record("beta", AccountShareRemoteStatus.Invalid, 30),
        ]);
        var service = new FakeSync(account, snapshot, snapshot);
        var viewModel = Create(service);
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();

        viewModel.StatusFilter = ShareAnalyticsStatusFilter.Invalid;
        Assert.Equal("beta", Assert.Single(viewModel.VisibleItems).Title);
        viewModel.SearchText = "beta";
        Assert.Equal("beta", Assert.Single(viewModel.VisibleItems).Title);
        viewModel.SelectMetricCommand.Execute(AccountShareMetric.View);
        Assert.Equal(AccountShareMetric.View, Assert.Single(viewModel.VisibleItems).Metric);
        viewModel.SortCommand.Execute(AccountShareSortColumn.HistoricalTotal);

        var row = Assert.Single(viewModel.VisibleItems);
        Assert.Equal("beta", row.Title);
        Assert.Equal(AccountShareMetric.View, row.Metric);
        Assert.Equal(31, row.HistoricalTotal);
        Assert.Equal(2, viewModel.TotalCount);
        Assert.Equal(1, viewModel.InvalidCount);
    }

    [Fact]
    public async Task RebuildRenumbersCurrentResultsAndRestoresSelectionByShareId()
    {
        var account = Guid.NewGuid();
        var snapshot = new AccountShareCachedSnapshot(account, Now,
        [
            Record("alpha", AccountShareRemoteStatus.Valid, 10),
            Record("beta", AccountShareRemoteStatus.Invalid, 30),
            Record("gamma", AccountShareRemoteStatus.Valid, 20),
        ]);
        var viewModel = Create(new FakeSync(account, snapshot));
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();
        Assert.Equal(Enumerable.Range(1, 3), viewModel.VisibleItems.Select(row => row.Sequence));
        var originalRows = viewModel.VisibleItems;
        originalRows.Single(row => row.ShareId == "beta").IsSelected = true;

        viewModel.SortCommand.Execute(AccountShareSortColumn.Today);
        Assert.NotSame(originalRows, viewModel.VisibleItems);
        Assert.Equal(Enumerable.Range(1, 3), viewModel.VisibleItems.Select(row => row.Sequence));
        Assert.True(viewModel.VisibleItems.Single(row => row.ShareId == "beta").IsSelected);

        viewModel.SelectMetricCommand.Execute(AccountShareMetric.View);
        Assert.Equal(Enumerable.Range(1, 3), viewModel.VisibleItems.Select(row => row.Sequence));
        Assert.True(viewModel.VisibleItems.Single(row => row.ShareId == "beta").IsSelected);

        viewModel.StatusFilter = ShareAnalyticsStatusFilter.Invalid;
        var filtered = Assert.Single(viewModel.VisibleItems);
        Assert.Equal(1, filtered.Sequence);
        Assert.True(filtered.IsSelected);
        viewModel.SearchText = "different";
        Assert.Empty(viewModel.VisibleItems);
        viewModel.SearchText = string.Empty;
        Assert.True(Assert.Single(viewModel.VisibleItems).IsSelected);

        Assert.Equal(1, viewModel.SelectedCount);
        Assert.Equal(1, viewModel.CurrentResultCount);
        Assert.True(viewModel.HeaderSelectionState);
    }

    [Fact]
    public async Task SingleRowToggleUpdatesSelectionNotificationsOnceWithoutRebuildingRows()
    {
        var account = Guid.NewGuid();
        var snapshot = new AccountShareCachedSnapshot(account, Now,
        [
            Record("alpha", AccountShareRemoteStatus.Valid, 10),
            Record("beta", AccountShareRemoteStatus.Valid, 20),
        ]);
        var viewModel = Create(new FakeSync(account, snapshot));
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();
        var visibleItems = viewModel.VisibleItems;
        var changed = new List<string?>();
        var exportCanExecuteChanged = 0;
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        viewModel.ExportCommand.CanExecuteChanged += (_, _) => exportCanExecuteChanged++;

        visibleItems[0].IsSelected = true;

        Assert.Same(visibleItems, viewModel.VisibleItems);
        Assert.Equal(1, changed.Count(name => name == nameof(ShareAnalyticsViewModel.SelectedCount)));
        Assert.Equal(1, changed.Count(name => name == nameof(ShareAnalyticsViewModel.HeaderSelectionState)));
        Assert.DoesNotContain(nameof(ShareAnalyticsViewModel.CurrentResultCount), changed);
        Assert.DoesNotContain(nameof(ShareAnalyticsViewModel.VisibleItems), changed);
        Assert.Equal(1, exportCanExecuteChanged);
        Assert.True(viewModel.ExportCommand.CanExecute(null));
        Assert.Null(viewModel.HeaderSelectionState);
    }

    [Fact]
    public async Task HeaderSelectionStateTracksVisibleSelectionAcrossFilteringAndToggles()
    {
        var account = Guid.NewGuid();
        var snapshot = new AccountShareCachedSnapshot(account, Now,
        [
            Record("alpha", AccountShareRemoteStatus.Valid, 10),
            Record("beta", AccountShareRemoteStatus.Valid, 20),
            Record("gamma", AccountShareRemoteStatus.Invalid, 30),
        ]);
        var viewModel = Create(new FakeSync(account, snapshot));
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();

        Assert.False(viewModel.HeaderSelectionState);
        viewModel.VisibleItems.Single(row => row.ShareId == "alpha").IsSelected = true;
        Assert.Null(viewModel.HeaderSelectionState);
        viewModel.VisibleItems.Single(row => row.ShareId == "beta").IsSelected = true;
        Assert.Null(viewModel.HeaderSelectionState);

        viewModel.StatusFilter = ShareAnalyticsStatusFilter.Valid;
        Assert.True(viewModel.HeaderSelectionState);
        viewModel.SearchText = "beta";
        Assert.True(viewModel.HeaderSelectionState);
        Assert.Single(viewModel.VisibleItems).IsSelected = false;
        Assert.False(viewModel.HeaderSelectionState);

        viewModel.SearchText = string.Empty;
        Assert.Null(viewModel.HeaderSelectionState);
        viewModel.StatusFilter = ShareAnalyticsStatusFilter.Invalid;
        Assert.False(viewModel.HeaderSelectionState);
        viewModel.SearchText = "missing";
        Assert.Empty(viewModel.VisibleItems);
        Assert.False(viewModel.HeaderSelectionState);
    }

    [Fact]
    public async Task RangeSelectionUsesClosedCurrentResultSequenceAndOnlyAddsSelection()
    {
        var account = Guid.NewGuid();
        var snapshot = new AccountShareCachedSnapshot(account, Now,
        [
            Record("alpha", AccountShareRemoteStatus.Valid, 10),
            Record("beta", AccountShareRemoteStatus.Valid, 20),
            Record("gamma", AccountShareRemoteStatus.Valid, 30),
            Record("delta", AccountShareRemoteStatus.Valid, 40),
            Record("epsilon", AccountShareRemoteStatus.Valid, 50),
        ]);
        var viewModel = Create(new FakeSync(account, snapshot));
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();
        var orderedIds = viewModel.VisibleItems.Select(row => row.ShareId).ToArray();
        viewModel.VisibleItems.Single(row => row.ShareId == orderedIds[4]).IsSelected = true;

        Assert.Equal("1", viewModel.RangeStartText);
        Assert.Equal("50", viewModel.RangeEndText);
        viewModel.RangeStartText = "2";
        viewModel.RangeEndText = "3";
        await viewModel.SelectRangeCommand.ExecuteAsync(null);

        Assert.Equal(3, viewModel.SelectedCount);
        Assert.Equal(
            [orderedIds[1], orderedIds[2], orderedIds[4]],
            viewModel.VisibleItems.Where(row => row.IsSelected).Select(row => row.ShareId));

        viewModel.RangeStartText = "4";
        viewModel.RangeEndText = "500";
        await viewModel.SelectRangeCommand.ExecuteAsync(null);

        Assert.Equal(4, viewModel.SelectedCount);
        Assert.Equal(orderedIds.Skip(1), viewModel.VisibleItems.Where(row => row.IsSelected).Select(row => row.ShareId));
    }

    [Theory]
    [InlineData("", "2")]
    [InlineData("x", "2")]
    [InlineData("1", "")]
    [InlineData("1", "x")]
    [InlineData("0", "2")]
    [InlineData("-1", "2")]
    [InlineData("2", "0")]
    [InlineData("3", "2")]
    [InlineData("30", "40")]
    public async Task InvalidRangeLeavesSelectionAndRowsUntouched(string start, string end)
    {
        var account = Guid.NewGuid();
        var snapshot = new AccountShareCachedSnapshot(account, Now,
        [
            Record("alpha", AccountShareRemoteStatus.Valid, 10),
            Record("beta", AccountShareRemoteStatus.Valid, 20),
            Record("gamma", AccountShareRemoteStatus.Valid, 30),
        ]);
        var viewModel = Create(new FakeSync(account, snapshot));
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();
        viewModel.VisibleItems[1].IsSelected = true;
        var originalRows = viewModel.VisibleItems;
        var originalState = originalRows.Select(row => (row.ShareId, row.IsSelected)).ToArray();
        viewModel.RangeStartText = start;
        viewModel.RangeEndText = end;

        await viewModel.SelectRangeCommand.ExecuteAsync(null);

        Assert.Same(originalRows, viewModel.VisibleItems);
        Assert.Equal(originalState, viewModel.VisibleItems.Select(row => (row.ShareId, row.IsSelected)));
        Assert.Equal(1, viewModel.SelectedCount);
        Assert.Equal("请输入有效的选取范围。", viewModel.StatusMessage);
    }

    [Fact]
    public async Task RangeCommandReportsInvalidInputWhenCurrentResultsAreEmpty()
    {
        var account = Guid.NewGuid();
        var viewModel = Create(new FakeSync(
            account,
            Snapshot(account, "alpha", Now)));
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();
        viewModel.SearchText = "missing";

        Assert.True(viewModel.SelectRangeCommand.CanExecute(null));
        await viewModel.SelectRangeCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.VisibleItems);
        Assert.Equal("请输入有效的选取范围。", viewModel.StatusMessage);
    }

    [Fact]
    public async Task AllInvertAndClearOnlyAffectCurrentResultsAndPreserveHiddenSelection()
    {
        var account = Guid.NewGuid();
        var snapshot = new AccountShareCachedSnapshot(account, Now,
        [
            Record("alpha", AccountShareRemoteStatus.Valid, 10),
            Record("beta", AccountShareRemoteStatus.Valid, 20),
            Record("gamma", AccountShareRemoteStatus.Invalid, 30),
        ]);
        var viewModel = Create(new FakeSync(account, snapshot));
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();
        viewModel.VisibleItems.Single(row => row.ShareId == "gamma").IsSelected = true;
        viewModel.StatusFilter = ShareAnalyticsStatusFilter.Valid;

        await viewModel.SelectAllCommand.ExecuteAsync(null);
        Assert.Equal(3, viewModel.SelectedCount);
        Assert.All(viewModel.VisibleItems, row => Assert.True(row.IsSelected));

        await viewModel.InvertSelectionCommand.ExecuteAsync(null);
        Assert.Equal(1, viewModel.SelectedCount);
        Assert.All(viewModel.VisibleItems, row => Assert.False(row.IsSelected));

        viewModel.VisibleItems.Single(row => row.ShareId == "alpha").IsSelected = true;
        await viewModel.ClearSelectionCommand.ExecuteAsync(null);
        Assert.Equal(1, viewModel.SelectedCount);
        Assert.All(viewModel.VisibleItems, row => Assert.False(row.IsSelected));

        viewModel.StatusFilter = ShareAnalyticsStatusFilter.Invalid;
        Assert.True(Assert.Single(viewModel.VisibleItems).IsSelected);
    }

    [Fact]
    public async Task HeaderCommandIsDisabledForEmptyResultsAndCompletesOrClearsCurrentSelection()
    {
        var account = Guid.NewGuid();
        var snapshot = new AccountShareCachedSnapshot(account, Now,
        [
            Record("alpha", AccountShareRemoteStatus.Valid, 10),
            Record("beta", AccountShareRemoteStatus.Valid, 20),
            Record("gamma", AccountShareRemoteStatus.Valid, 30),
        ]);
        var viewModel = Create(new FakeSync(account, snapshot));
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();

        Assert.True(viewModel.IsHeaderSelectionEnabled);
        Assert.True(viewModel.ToggleHeaderSelectionCommand.CanExecute(null));
        await viewModel.ToggleHeaderSelectionCommand.ExecuteAsync(null);
        Assert.True(viewModel.HeaderSelectionState);

        viewModel.VisibleItems[0].IsSelected = false;
        Assert.Null(viewModel.HeaderSelectionState);
        await viewModel.ToggleHeaderSelectionCommand.ExecuteAsync(null);
        Assert.True(viewModel.HeaderSelectionState);
        Assert.All(viewModel.VisibleItems, row => Assert.True(row.IsSelected));

        await viewModel.ToggleHeaderSelectionCommand.ExecuteAsync(null);
        Assert.False(viewModel.HeaderSelectionState);
        Assert.All(viewModel.VisibleItems, row => Assert.False(row.IsSelected));

        viewModel.SearchText = "missing";
        Assert.Empty(viewModel.VisibleItems);
        Assert.False(viewModel.HeaderSelectionState);
        Assert.False(viewModel.IsHeaderSelectionEnabled);
        Assert.False(viewModel.ToggleHeaderSelectionCommand.CanExecute(null));
    }

    [Fact]
    public async Task HundredThousandRowBulkSelectionReplacesAndNotifiesOnlyOnce()
    {
        const int Count = 100_000;
        var account = Guid.NewGuid();
        var snapshot = new AccountShareCachedSnapshot(
            account,
            Now,
            Enumerable.Range(0, Count).Select(SyntheticRecord).ToArray());
        var scheduler = new GateBulkScheduler();
        var viewModel = Create(new FakeSync(account, snapshot), bulkScheduler: scheduler);
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();
        var originalRows = viewModel.VisibleItems;
        var changed = new List<string?>();
        var exportCanExecuteChanged = 0;
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        viewModel.ExportCommand.CanExecuteChanged += (_, _) => exportCanExecuteChanged++;

        var selecting = viewModel.SelectAllCommand.ExecuteAsync(null);
        await scheduler.Started.Task;
        Assert.True(viewModel.IsSelectionBusy);
        Assert.False(viewModel.SelectAllCommand.CanExecute(null));
        scheduler.Release.TrySetResult();
        await selecting;

        Assert.NotSame(originalRows, viewModel.VisibleItems);
        Assert.Equal(Count, viewModel.SelectedCount);
        Assert.True(viewModel.HeaderSelectionState);
        Assert.False(viewModel.IsSelectionBusy);
        Assert.Equal(1, changed.Count(name => name == nameof(ShareAnalyticsViewModel.VisibleItems)));
        Assert.Equal(1, changed.Count(name => name == nameof(ShareAnalyticsViewModel.SelectedCount)));
        Assert.Equal(1, changed.Count(name => name == nameof(ShareAnalyticsViewModel.CurrentResultCount)));
        Assert.Equal(1, changed.Count(name => name == nameof(ShareAnalyticsViewModel.HeaderSelectionState)));
        Assert.Equal(1, exportCanExecuteChanged);
        Assert.Equal(1, scheduler.OperationInvocations);
    }

    [Fact]
    public async Task BulkSchedulerFailureKeepsSelectionAndRowsAndShowsOnlyStableMessage()
    {
        var account = Guid.NewGuid();
        var snapshot = new AccountShareCachedSnapshot(account, Now,
        [
            Record("alpha", AccountShareRemoteStatus.Valid, 10),
            Record("beta", AccountShareRemoteStatus.Valid, 20),
        ]);
        var scheduler = new ThrowingBulkScheduler();
        var viewModel = Create(new FakeSync(account, snapshot), bulkScheduler: scheduler);
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();
        viewModel.VisibleItems[0].IsSelected = true;
        var originalRows = viewModel.VisibleItems;
        var originalState = originalRows.Select(row => (row.ShareId, row.IsSelected)).ToArray();

        var exception = await Xunit.Record.ExceptionAsync(() => viewModel.InvertSelectionCommand.ExecuteAsync(null));

        Assert.Null(exception);
        Assert.Same(originalRows, viewModel.VisibleItems);
        Assert.Equal(originalState, viewModel.VisibleItems.Select(row => (row.ShareId, row.IsSelected)));
        Assert.Equal(1, viewModel.SelectedCount);
        Assert.False(viewModel.IsSelectionBusy);
        Assert.Equal("选择操作失败，请重试。", viewModel.StatusMessage);
        Assert.DoesNotContain("sensitive", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(BulkInvalidation.AccountSwitch)]
    [InlineData(BulkInvalidation.SameAccountRefresh)]
    [InlineData(BulkInvalidation.Metric)]
    [InlineData(BulkInvalidation.Search)]
    [InlineData(BulkInvalidation.Status)]
    [InlineData(BulkInvalidation.Sort)]
    [InlineData(BulkInvalidation.Leave)]
    [InlineData(BulkInvalidation.Dispose)]
    public async Task StaleBulkCompletionCannotOverwriteChangedWorkspace(BulkInvalidation invalidation)
    {
        var account = Guid.NewGuid();
        var otherAccount = Guid.NewGuid();
        var initial = new AccountShareCachedSnapshot(account, Now,
        [
            Record("alpha", AccountShareRemoteStatus.Valid, 10),
            Record("beta", AccountShareRemoteStatus.Invalid, 20),
            Record("gamma", AccountShareRemoteStatus.Valid, 30),
        ]);
        var refreshed = new AccountShareCachedSnapshot(account, Now,
        [
            Record("fresh", AccountShareRemoteStatus.Valid, 40),
        ]);
        IShareAnalyticsWorkspaceService service = invalidation switch
        {
            BulkInvalidation.AccountSwitch => new MultiAccountSync(
                initial,
                Snapshot(otherAccount, "other", Now)),
            BulkInvalidation.SameAccountRefresh => new FakeSync(account, initial, refreshed),
            _ => new FakeSync(account, initial),
        };
        var scheduler = new GateBulkScheduler();
        var viewModel = Create(service, bulkScheduler: scheduler);
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();

        var selecting = viewModel.SelectAllCommand.ExecuteAsync(null);
        await scheduler.Started.Task;
        switch (invalidation)
        {
            case BulkInvalidation.AccountSwitch:
                viewModel.SelectAccount(otherAccount, "其他账号");
                await WaitUntilAsync(() => viewModel.VisibleItems.Count == 1 && viewModel.VisibleItems[0].ShareId == "other");
                break;
            case BulkInvalidation.SameAccountRefresh:
                await viewModel.RefreshCommand.ExecuteAsync(null);
                break;
            case BulkInvalidation.Metric:
                viewModel.SelectMetricCommand.Execute(AccountShareMetric.View);
                break;
            case BulkInvalidation.Search:
                viewModel.SearchText = "alpha";
                break;
            case BulkInvalidation.Status:
                viewModel.StatusFilter = ShareAnalyticsStatusFilter.Invalid;
                break;
            case BulkInvalidation.Sort:
                viewModel.SortCommand.Execute(AccountShareSortColumn.Today);
                break;
            case BulkInvalidation.Leave:
                viewModel.Leave();
                break;
            case BulkInvalidation.Dispose:
                viewModel.Dispose();
                break;
        }
        var expectedRows = viewModel.VisibleItems;
        var expectedState = expectedRows.Select(row => (row.ShareId, row.IsSelected)).ToArray();
        var expectedSelectedCount = viewModel.SelectedCount;

        await scheduler.CancellationObserved.Task;
        await selecting;

        Assert.Same(expectedRows, viewModel.VisibleItems);
        Assert.Equal(expectedState, viewModel.VisibleItems.Select(row => (row.ShareId, row.IsSelected)));
        Assert.Equal(expectedSelectedCount, viewModel.SelectedCount);
        Assert.False(viewModel.IsSelectionBusy);
        Assert.Equal(0, scheduler.OperationInvocations);
    }

    [Fact]
    public async Task SelectingDifferentAccountClearsAllSelectedShareIds()
    {
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var service = new MultiAccountSync(
            Snapshot(accountA, "alpha", Now),
            Snapshot(accountB, "beta", Now));
        var viewModel = Create(service);
        viewModel.SelectAccount(accountA, "甲");
        await viewModel.EnterAsync();
        Assert.Single(viewModel.VisibleItems).IsSelected = true;
        Assert.Equal(1, viewModel.SelectedCount);

        viewModel.SelectAccount(accountB, "乙");

        Assert.Equal(0, viewModel.SelectedCount);
        Assert.False(viewModel.HeaderSelectionState);
        await WaitUntilAsync(() => viewModel.VisibleItems.Count == 1);
        Assert.Equal("beta", Assert.Single(viewModel.VisibleItems).ShareId);
        Assert.False(Assert.Single(viewModel.VisibleItems).IsSelected);
    }

    [Fact]
    public async Task SameAccountRefreshRetainsOnlySelectedIdsStillInSnapshot()
    {
        var account = Guid.NewGuid();
        var cached = new AccountShareCachedSnapshot(account, Now.AddHours(-22),
        [
            Record("alpha", AccountShareRemoteStatus.Valid, 10),
            Record("beta", AccountShareRemoteStatus.Valid, 20),
        ]);
        var refreshed = new AccountShareCachedSnapshot(account, Now,
        [
            Record("beta", AccountShareRemoteStatus.Valid, 30),
            Record("gamma", AccountShareRemoteStatus.Valid, 40),
        ]);
        var viewModel = Create(new FakeSync(account, cached, refreshed), new FakeDelay());
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();
        foreach (var row in viewModel.VisibleItems) row.IsSelected = true;
        var changed = new List<string?>();
        var exportCanExecuteChanged = 0;
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        viewModel.ExportCommand.CanExecuteChanged += (_, _) => exportCanExecuteChanged++;

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(1, viewModel.SelectedCount);
        Assert.Equal(1, changed.Count(name => name == nameof(ShareAnalyticsViewModel.SelectedCount)));
        Assert.Equal(1, exportCanExecuteChanged);
        Assert.Equal(["beta", "gamma"], viewModel.VisibleItems.Select(row => row.ShareId).Order());
        Assert.True(viewModel.VisibleItems.Single(row => row.ShareId == "beta").IsSelected);
        Assert.False(viewModel.VisibleItems.Single(row => row.ShareId == "gamma").IsSelected);
        Assert.Null(viewModel.HeaderSelectionState);
    }

    [Fact]
    public async Task RowFromBeforeRebuildCannotMutateCurrentSelection()
    {
        var account = Guid.NewGuid();
        var snapshot = new AccountShareCachedSnapshot(account, Now,
        [
            Record("alpha", AccountShareRemoteStatus.Valid, 10),
            Record("beta", AccountShareRemoteStatus.Valid, 20),
        ]);
        var viewModel = Create(new FakeSync(account, snapshot));
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();
        var staleRow = viewModel.VisibleItems.Single(row => row.ShareId == "alpha");
        viewModel.VisibleItems.Single(row => row.ShareId == "beta").IsSelected = true;
        viewModel.SortCommand.Execute(AccountShareSortColumn.Today);
        var currentItems = viewModel.VisibleItems;

        staleRow.IsSelected = true;

        Assert.Same(currentItems, viewModel.VisibleItems);
        Assert.Equal(1, viewModel.SelectedCount);
        Assert.Null(viewModel.HeaderSelectionState);
        Assert.False(currentItems.Single(row => row.ShareId == "alpha").IsSelected);
        Assert.True(currentItems.Single(row => row.ShareId == "beta").IsSelected);
    }

    [Fact]
    public async Task RowFromPreviousAccountCannotInjectSelectionIntoCurrentAccount()
    {
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var viewModel = Create(new MultiAccountSync(
            Snapshot(accountA, "alpha", Now),
            Snapshot(accountB, "beta", Now)));
        viewModel.SelectAccount(accountA, "甲");
        await viewModel.EnterAsync();
        var staleRow = Assert.Single(viewModel.VisibleItems);
        viewModel.SelectAccount(accountB, "乙");
        await WaitUntilAsync(() => viewModel.VisibleItems.Count == 1);
        var currentItems = viewModel.VisibleItems;

        staleRow.IsSelected = true;

        Assert.Same(currentItems, viewModel.VisibleItems);
        Assert.Equal(0, viewModel.SelectedCount);
        Assert.False(viewModel.HeaderSelectionState);
        Assert.Equal("beta", Assert.Single(currentItems).ShareId);
        Assert.False(Assert.Single(currentItems).IsSelected);
    }

    [Fact]
    public async Task RowRetainedAfterDisposeCannotMutateDisposedSelectionState()
    {
        var account = Guid.NewGuid();
        var viewModel = Create(new FakeSync(account, Snapshot(account, "alpha", Now)));
        viewModel.SelectAccount(account, "账号");
        await viewModel.EnterAsync();
        var staleRow = Assert.Single(viewModel.VisibleItems);
        var currentItems = viewModel.VisibleItems;
        viewModel.Dispose();

        staleRow.IsSelected = true;

        Assert.Same(currentItems, viewModel.VisibleItems);
        Assert.Equal(0, viewModel.SelectedCount);
        Assert.False(viewModel.HeaderSelectionState);
        Assert.False(Assert.Single(viewModel.VisibleItems).IsSelected);
    }

    [Fact]
    public async Task LeavingOrChangingAccountPreventsOldRefreshFromApplying()
    {
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var service = new BlockingSync(accountA, SnapshotWithoutSync(accountA, "a"));
        var viewModel = Create(service);
        viewModel.SelectAccount(accountA, "甲");
        var entering = viewModel.EnterAsync();
        await service.SyncStarted.Task;
        viewModel.Leave();
        viewModel.SelectAccount(accountB, "乙");
        service.Release.TrySetResult();
        await entering;
        Assert.Empty(viewModel.VisibleItems);
    }

    private static readonly AccountShareSyncResult FailureResult =
        new(AccountShareSyncDisposition.TemporarilyUnavailable, "分享数据获取失败，请稍后重试。");

    private static ShareAnalyticsViewModel Create(
        IShareAnalyticsWorkspaceService service,
        IShareAnalyticsDelay? delay = null,
        TimeProvider? timeProvider = null,
        IShareAnalyticsBulkScheduler? bulkScheduler = null,
        IShareAnalyticsWorkbookExportService? exportService = null,
        IResultSaveDialog? saveDialog = null) =>
        new(service, new AccountShareAnalyticsProjector(), new FakeLauncher(),
            timeProvider ?? new FrozenTimeProvider(Now), delay ?? new FakeDelay(),
            exportService ?? new RecordingExporter(), saveDialog ?? new ImmediateSaveDialog(null), bulkScheduler);

    private static AccountShareCachedSnapshot Snapshot(Guid account, string title, DateTimeOffset? syncedAt = null) =>
        new(account, syncedAt ?? Now, [Record(title, AccountShareRemoteStatus.Valid, 10)]);

    private static AccountShareCachedSnapshot SnapshotWithoutSync(Guid account, string title) =>
        new(account, null, [Record(title, AccountShareRemoteStatus.Valid, 10)]);

    private static AccountShareCachedRecord Record(string title, AccountShareRemoteStatus status, long total) => new(
        title, title, new Uri($"https://pan.baidu.com/s/{title}"), Now.AddDays(-1), status,
        Enum.GetValues<AccountShareMetric>().Select(metric => new AccountShareMetricHistory(
            metric, total + (long)metric,
            new Dictionary<DateOnly, long?> { [new(2026, 9, 1)] = total, [new(2026, 9, 2)] = total + 2 })).ToArray());

    private static AccountShareCachedRecord ExportRecord(
        string shareId,
        string title,
        AccountShareRemoteStatus status,
        DateTimeOffset sharedAt,
        DateOnly date,
        long transferTotal,
        long transferToday) => new(
        shareId,
        title,
        new Uri($"https://pan.baidu.com/s/{shareId}"),
        sharedAt,
        status,
        Enum.GetValues<AccountShareMetric>()
            .Select(metric => new AccountShareMetricHistory(
                metric,
                transferTotal + ((long)metric * 1_000),
                directDailyValues: new Dictionary<DateOnly, long?>
                {
                    [date] = transferToday + ((long)metric * 100),
                    [date.AddDays(-1)] = transferToday - 1 + ((long)metric * 100),
                }))
            .ToArray());

    private static AccountShareCachedRecord SyntheticRecord(int index) => new(
        $"id-{index:D6}",
        $"标题 {index}",
        new Uri($"https://pan.baidu.com/s/id-{index:D6}"),
        Now.AddTicks(index),
        AccountShareRemoteStatus.Valid,
        Enum.GetValues<AccountShareMetric>()
            .Select(metric => new AccountShareMetricHistory(metric, index))
            .ToArray());

    private static ShareAnalyticsRow ProjectRow(string shareId, string title)
    {
        var source = new AccountShareCachedRecord(
            shareId,
            title,
            new Uri("https://pan.baidu.com/s/synthetic"),
            Now,
            AccountShareRemoteStatus.Valid,
            Enum.GetValues<AccountShareMetric>()
                .Select(metric => new AccountShareMetricHistory(metric, 1))
                .ToArray());
        var projected = new AccountShareAnalyticsProjector().Project(source, AccountShareMetric.Transfer, Now);
        return new ShareAnalyticsRow(projected);
    }

    private sealed class FakeSync(Guid account, params AccountShareCachedSnapshot[] snapshots) : IShareAnalyticsWorkspaceService
    {
        private int load;
        public int SyncCalls { get; private set; }
        public IReadOnlyList<AccountShareSyncResult> SyncResults { get; init; } =
            [new(AccountShareSyncDisposition.Updated, "分享数据已更新。")];
        public List<string> AppliedLoadTitles { get; } = [];
        public Task<AccountShareCachedSnapshot> LoadCachedAsync(Guid accountId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(account, accountId);
            var value = snapshots[Math.Min(load++, snapshots.Length - 1)];
            AppliedLoadTitles.Add(value.Items.First().Title);
            return Task.FromResult(value);
        }
        public Task<AccountShareSyncResult> SyncAsync(Guid accountId, CancellationToken cancellationToken = default)
        {
            SyncCalls++;
            return Task.FromResult(SyncResults[Math.Min(SyncCalls - 1, SyncResults.Count - 1)]);
        }

        public async Task WaitForSyncCallsAsync(int expected) =>
            await WaitUntilAsync(() => SyncCalls >= expected);
    }

    private sealed class BlockingSync(Guid account, params AccountShareCachedSnapshot[] snapshots) : IShareAnalyticsWorkspaceService
    {
        private int load;
        public TaskCompletionSource SyncStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SyncCalls { get; private set; }
        public bool ThrowOnRelease { get; init; }
        public AccountShareSyncResult SyncResult { get; init; } =
            new(AccountShareSyncDisposition.Updated, "ok");
        public CancellationToken SyncCancellationToken { get; private set; }
        public Task<AccountShareCachedSnapshot> LoadCachedAsync(Guid accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult(accountId == account
                ? snapshots[Math.Min(load++, snapshots.Length - 1)]
                : new AccountShareCachedSnapshot(accountId, null, []));
        public async Task<AccountShareSyncResult> SyncAsync(Guid accountId, CancellationToken cancellationToken = default)
        {
            SyncCalls++;
            SyncCancellationToken = cancellationToken;
            SyncStarted.TrySetResult();
            await Release.Task;
            if (ThrowOnRelease) throw new InvalidOperationException("sensitive detail");
            return SyncResult;
        }
    }

    private sealed class AccountSwitchBlockingSync(
        Guid blockedAccount,
        AccountShareCachedSnapshot blockedSnapshot,
        AccountShareCachedSnapshot freshSnapshot) : IShareAnalyticsWorkspaceService
    {
        public TaskCompletionSource SyncStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AccountShareCachedSnapshot> LoadCachedAsync(Guid accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult(accountId == blockedAccount ? blockedSnapshot : freshSnapshot);

        public async Task<AccountShareSyncResult> SyncAsync(Guid accountId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(blockedAccount, accountId);
            SyncStarted.TrySetResult();
            await Release.Task;
            return new(AccountShareSyncDisposition.Updated, "ok");
        }
    }

    private sealed class MultiAccountSync(params AccountShareCachedSnapshot[] snapshots) : IShareAnalyticsWorkspaceService
    {
        private readonly Dictionary<Guid, AccountShareCachedSnapshot> values = snapshots.ToDictionary(value => value.AccountId);
        public int SyncCalls { get; private set; }
        public Task<AccountShareCachedSnapshot> LoadCachedAsync(Guid accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult(values[accountId]);
        public Task<AccountShareSyncResult> SyncAsync(Guid accountId, CancellationToken cancellationToken = default)
        {
            SyncCalls++;
            return Task.FromResult(new AccountShareSyncResult(AccountShareSyncDisposition.Updated, "ok"));
        }
    }

    private sealed class FakeDelay : IShareAnalyticsDelay
    {
        public List<DelayRequest> Requests { get; } = [];

        public Task WaitAsync(TimeSpan delay, CancellationToken token)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var request = new DelayRequest(delay, token, completion);
            Requests.Add(request);
            token.Register(() => completion.TrySetCanceled(token));
            return completion.Task;
        }

        public void Complete(int index) => Requests[index].Completion.TrySetResult();

        public sealed record DelayRequest(TimeSpan Delay, CancellationToken Token, TaskCompletionSource Completion);
    }

    private sealed class FakeLauncher : IShareLinkLauncher
    {
        public bool TryOpen(Uri uri, out string safeMessage) { safeMessage = "ok"; return true; }
    }

    private sealed class ImmediateSaveDialog(string? directory) : IResultSaveDialog
    {
        public Task<string?> PickDestinationDirectoryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(directory);
    }

    private sealed class GateSaveDialog : IResultSaveDialog
    {
        private readonly TaskCompletionSource<string?> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken CancellationToken { get; private set; }

        public async Task<string?> PickDestinationDirectoryAsync(CancellationToken cancellationToken = default)
        {
            CancellationToken = cancellationToken;
            Started.TrySetResult();
            return await completion.Task;
        }

        public void Complete(string? directory) => completion.TrySetResult(directory);
    }

    private sealed class RecordingExporter(
        Func<ShareAnalyticsWorkbookExportRequest, CancellationToken, Task<ShareAnalyticsWorkbookExportResult>>? handler = null)
        : IShareAnalyticsWorkbookExportService
    {
        private readonly Func<ShareAnalyticsWorkbookExportRequest, CancellationToken, Task<ShareAnalyticsWorkbookExportResult>> handler =
            handler ?? ((_, _) => Task.FromResult(
                new ShareAnalyticsWorkbookExportResult(Path.Combine("exports", "share.xlsx"), 1, 1)));

        public List<ShareAnalyticsWorkbookExportRequest> Requests { get; } = [];
        public List<CancellationToken> CancellationTokens { get; } = [];

        public Task<ShareAnalyticsWorkbookExportResult> ExportAsync(
            ShareAnalyticsWorkbookExportRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            CancellationTokens.Add(cancellationToken);
            return handler(request, cancellationToken);
        }
    }

    private sealed class GateExporter : IShareAnalyticsWorkbookExportService
    {
        private readonly TaskCompletionSource<ShareAnalyticsWorkbookExportResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ShareAnalyticsWorkbookExportRequest? Request { get; private set; }
        public CancellationToken CancellationToken { get; private set; }

        public async Task<ShareAnalyticsWorkbookExportResult> ExportAsync(
            ShareAnalyticsWorkbookExportRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            CancellationToken = cancellationToken;
            Started.TrySetResult();
            return await completion.Task;
        }

        public void Complete(ShareAnalyticsWorkbookExportResult result) => completion.TrySetResult(result);
    }

    private sealed class GateBulkScheduler : IShareAnalyticsBulkScheduler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int OperationInvocations { get; private set; }

        public async Task<T> RunAsync<T>(
            Func<CancellationToken, T> operation,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
            OperationInvocations++;
            return operation(cancellationToken);
        }
    }

    private sealed class ThrowingBulkScheduler : IShareAnalyticsBulkScheduler
    {
        public Task<T> RunAsync<T>(
            Func<CancellationToken, T> operation,
            CancellationToken cancellationToken) =>
            Task.FromException<T>(new InvalidOperationException("sensitive C:\\private\\selection.txt"));
    }

    public enum BulkInvalidation
    {
        AccountSwitch,
        SameAccountRefresh,
        Metric,
        Search,
        Status,
        Sort,
        Leave,
        Dispose,
    }

    public enum ExportInvalidation
    {
        AccountSwitch,
        Leave,
        Dispose,
    }

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset now = now;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BaiduBatchTransfer.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
