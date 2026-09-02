using BaiduBatchTransfer.App.Features.ShareAnalytics;
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
        TimeProvider? timeProvider = null) =>
        new(service, new AccountShareAnalyticsProjector(), new FakeLauncher(),
            timeProvider ?? new FrozenTimeProvider(Now), delay ?? new FakeDelay());

    private static AccountShareCachedSnapshot Snapshot(Guid account, string title, DateTimeOffset? syncedAt = null) =>
        new(account, syncedAt ?? Now, [Record(title, AccountShareRemoteStatus.Valid, 10)]);

    private static AccountShareCachedSnapshot SnapshotWithoutSync(Guid account, string title) =>
        new(account, null, [Record(title, AccountShareRemoteStatus.Valid, 10)]);

    private static AccountShareCachedRecord Record(string title, AccountShareRemoteStatus status, long total) => new(
        title, title, new Uri($"https://pan.baidu.com/s/{title}"), Now.AddDays(-1), status,
        Enum.GetValues<AccountShareMetric>().Select(metric => new AccountShareMetricHistory(
            metric, total + (long)metric,
            new Dictionary<DateOnly, long?> { [new(2026, 9, 1)] = total, [new(2026, 9, 2)] = total + 2 })).ToArray());

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
