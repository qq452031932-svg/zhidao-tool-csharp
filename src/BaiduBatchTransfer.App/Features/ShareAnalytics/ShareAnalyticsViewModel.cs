using BaiduBatchTransfer.Application.ShareAnalytics;
using BaiduBatchTransfer.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaiduBatchTransfer.App.Features.ShareAnalytics;

public sealed class ShareAnalyticsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(23);
    private static readonly TimeSpan StaleRetryDelay = TimeSpan.FromHours(1);
    private const string RefreshFailureMessage = "分享数据获取失败，请稍后重试。";

    private readonly IShareAnalyticsWorkspaceService service;
    private readonly AccountShareAnalyticsProjector projector;
    private readonly IShareLinkLauncher launcher;
    private readonly TimeProvider timeProvider;
    private readonly IShareAnalyticsDelay delay;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object refreshSync = new();
    private CancellationTokenSource? activation;
    private CancellationTokenSource? pendingDelay;
    private RefreshFlight? currentRefresh;
    private Guid? currentRefreshAccount;
    private long currentRefreshSelection;
    private long currentRefreshNavigation;
    private AccountShareCachedSnapshot? snapshot;
    private Guid? accountId;
    private string currentAccountName = "未选择账号";
    private string searchText = string.Empty;
    private ShareAnalyticsStatusFilter statusFilter;
    private ShareAnalyticsStatusOption selectedStatusOption;
    private AccountShareMetric selectedMetric = AccountShareMetric.Transfer;
    private AccountShareSortColumn sortColumn = AccountShareSortColumn.SharedAt;
    private bool sortDescending = true;
    private bool isRefreshing;
    private bool isActive;
    private string statusMessage = "请选择左侧账号。";
    private IReadOnlyList<ShareAnalyticsRow> visibleItems = [];
    private long selectionGeneration;
    private long navigationGeneration;
    private long delayGeneration;

    public ShareAnalyticsViewModel(
        IShareAnalyticsWorkspaceService service,
        AccountShareAnalyticsProjector projector,
        IShareLinkLauncher launcher,
        TimeProvider timeProvider,
        IShareAnalyticsDelay delay)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.projector = projector ?? throw new ArgumentNullException(nameof(projector));
        this.launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.delay = delay ?? throw new ArgumentNullException(nameof(delay));
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => accountId is not null && isActive);
        SelectMetricCommand = new RelayCommand<AccountShareMetric>(SelectMetric);
        SortCommand = new RelayCommand<AccountShareSortColumn>(Sort);
        OpenCommand = new RelayCommand<ShareAnalyticsRow>(Open, row => row is not null);
        StatusFilterOptions =
        [
            new(ShareAnalyticsStatusFilter.All, "全部状态"),
            new(ShareAnalyticsStatusFilter.Valid, "有效"),
            new(ShareAnalyticsStatusFilter.Invalid, "失效"),
        ];
        selectedStatusOption = StatusFilterOptions[0];
        UpdateDateLabels();
    }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand<AccountShareMetric> SelectMetricCommand { get; }
    public IRelayCommand<AccountShareSortColumn> SortCommand { get; }
    public IRelayCommand<ShareAnalyticsRow> OpenCommand { get; }
    public IReadOnlyList<ShareAnalyticsRow> VisibleItems { get => visibleItems; private set => SetProperty(ref visibleItems, value); }
    public IReadOnlyList<ShareAnalyticsStatusOption> StatusFilterOptions { get; }
    public ShareAnalyticsStatusOption SelectedStatusOption
    {
        get => selectedStatusOption;
        set
        {
            if (value is null || !SetProperty(ref selectedStatusOption, value)) return;
            StatusFilter = value.Value;
        }
    }
    public string CurrentAccountName { get => currentAccountName; private set => SetProperty(ref currentAccountName, value); }
    public string StatusMessage { get => statusMessage; private set => SetProperty(ref statusMessage, value); }
    public bool IsRefreshing
    {
        get => isRefreshing;
        private set
        {
            if (!SetProperty(ref isRefreshing, value)) return;
            RefreshCommand.NotifyCanExecuteChanged();
        }
    }
    public string SearchText
    {
        get => searchText;
        set { if (SetProperty(ref searchText, value?.Trim() ?? string.Empty)) Rebuild(); }
    }
    public ShareAnalyticsStatusFilter StatusFilter
    {
        get => statusFilter;
        set { if (SetProperty(ref statusFilter, value)) Rebuild(); }
    }
    public AccountShareMetric SelectedMetric
    {
        get => selectedMetric;
        private set
        {
            if (!SetProperty(ref selectedMetric, value)) return;
            OnPropertyChanged(nameof(IsTransferSelected));
            OnPropertyChanged(nameof(IsViewSelected));
            OnPropertyChanged(nameof(IsDownloadSelected));
            Rebuild();
        }
    }
    public bool IsTransferSelected => SelectedMetric == AccountShareMetric.Transfer;
    public bool IsViewSelected => SelectedMetric == AccountShareMetric.View;
    public bool IsDownloadSelected => SelectedMetric == AccountShareMetric.Download;
    public int TotalCount { get; private set; }
    public int ValidCount { get; private set; }
    public int InvalidCount { get; private set; }
    public string LastSuccessfulSyncText { get; private set; } = "尚未同步";
    public string PriorDay2Header { get; private set; } = string.Empty;
    public string PriorDay3Header { get; private set; } = string.Empty;
    public string PriorDay4Header { get; private set; } = string.Empty;

    public void SelectAccount(Guid? selectedAccountId, string? displayName)
    {
        if (selectedAccountId == Guid.Empty) selectedAccountId = null;
        CancelPendingDelay();
        activation?.Cancel();
        IsRefreshing = false;
        Interlocked.Increment(ref selectionGeneration);
        accountId = selectedAccountId;
        CurrentAccountName = selectedAccountId is null || string.IsNullOrWhiteSpace(displayName)
            ? "未选择账号"
            : displayName.Trim();
        snapshot = null;
        VisibleItems = [];
        LastSuccessfulSyncText = "尚未同步";
        OnPropertyChanged(nameof(LastSuccessfulSyncText));
        UpdateSummary();
        StatusMessage = selectedAccountId is null ? "请选择左侧账号。" : "已选择账号，等待读取分享数据。";
        RefreshCommand.NotifyCanExecuteChanged();
        if (isActive && selectedAccountId is not null)
            ObserveActivation(EnterAsync(lifetime.Token));
    }

    public async Task EnterAsync(CancellationToken cancellationToken = default)
    {
        isActive = true;
        var navigation = Interlocked.Increment(ref navigationGeneration);
        RefreshCommand.NotifyCanExecuteChanged();
        if (accountId is not { } selected) return;
        var selection = Volatile.Read(ref selectionGeneration);
        CancelPendingDelay();
        activation?.Cancel();
        activation?.Dispose();
        IsRefreshing = false;
        activation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        var token = activation.Token;
        try
        {
            var cached = await service.LoadCachedAsync(selected, token);
            if (!CanApply(selected, selection, navigation)) return;
            Apply(cached);
            if (cached.LastSuccessfulSyncAtUtc is not { } lastSuccess)
            {
                await StartOrJoinRefreshAsync(selected, selection, navigation, token, RefreshOrigin.Initial);
                return;
            }

            var remaining = RemainingLifetime(lastSuccess);
            if (remaining <= TimeSpan.Zero)
                await StartOrJoinRefreshAsync(selected, selection, navigation, token, RefreshOrigin.Initial);
            else
                ScheduleDelay(selected, selection, navigation, token, remaining, RefreshOrigin.AutomaticDue);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch
        {
            if (CanApply(selected, selection, navigation)) StatusMessage = "分享数据暂时无法加载。";
        }
    }

    public void Leave()
    {
        isActive = false;
        Interlocked.Increment(ref navigationGeneration);
        CancelPendingDelay();
        activation?.Cancel();
        IsRefreshing = false;
        RefreshCommand.NotifyCanExecuteChanged();
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!isActive || accountId is not { } selected) return;
        var selection = Volatile.Read(ref selectionGeneration);
        var navigation = Volatile.Read(ref navigationGeneration);
        var token = activation?.Token ?? lifetime.Token;
        CancelPendingDelay();
        var sharedRefresh = StartOrJoinRefreshAsync(selected, selection, navigation, token, RefreshOrigin.Manual);
        try { await sharedRefresh.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (token.IsCancellationRequested || cancellationToken.IsCancellationRequested) { }
    }

    private Task StartOrJoinRefreshAsync(
        Guid selected,
        long selection,
        long navigation,
        CancellationToken cancellationToken,
        RefreshOrigin origin)
    {
        lock (refreshSync)
        {
            if (currentRefreshAccount == selected &&
                currentRefreshSelection == selection &&
                currentRefreshNavigation == navigation &&
                currentRefresh is { Task.IsCompleted: false } running)
            {
                if (origin == RefreshOrigin.Manual) running.AddManualIntent();
                return running.Task;
            }

            currentRefreshAccount = selected;
            currentRefreshSelection = selection;
            currentRefreshNavigation = navigation;
            var flight = new RefreshFlight(origin);
            currentRefresh = flight;
            flight.Task = RefreshCoreAsync(selected, selection, navigation, cancellationToken, flight);
            return flight.Task;
        }
    }

    private async Task RefreshCoreAsync(
        Guid selected,
        long selection,
        long navigation,
        CancellationToken cancellationToken,
        RefreshFlight flight)
    {
        IsRefreshing = true;
        StatusMessage = snapshot?.LastSuccessfulSyncAtUtc is null
            ? "正在首次同步分享数据…"
            : "正在后台刷新分享数据…";
        try
        {
            var result = await service.SyncAsync(selected, cancellationToken);
            if (!CanApply(selected, selection, navigation)) return;
            if (result.Disposition is AccountShareSyncDisposition.Updated or AccountShareSyncDisposition.StaleResultIgnored)
            {
                var refreshed = await service.LoadCachedAsync(selected, cancellationToken);
                if (!CanApply(selected, selection, navigation)) return;
                Apply(refreshed);
                StatusMessage = result.SafeMessage;
                ScheduleAfterSuccess(selected, selection, navigation, cancellationToken);
                return;
            }

            StatusMessage = string.IsNullOrWhiteSpace(result.SafeMessage) ? RefreshFailureMessage : result.SafeMessage;
            ScheduleAfterFailure(selected, selection, navigation, cancellationToken, flight);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            if (!CanApply(selected, selection, navigation)) return;
            StatusMessage = RefreshFailureMessage;
            ScheduleAfterFailure(selected, selection, navigation, cancellationToken, flight);
        }
        finally
        {
            if (CanApply(selected, selection, navigation)) IsRefreshing = false;
        }
    }

    private void ScheduleAfterSuccess(Guid selected, long selection, long navigation, CancellationToken token)
    {
        if (snapshot?.LastSuccessfulSyncAtUtc is not { } lastSuccess) return;
        var remaining = RemainingLifetime(lastSuccess);
        if (remaining <= TimeSpan.Zero) remaining = CacheLifetime;
        ScheduleDelay(selected, selection, navigation, token, remaining, RefreshOrigin.AutomaticDue);
    }

    private void ScheduleAfterFailure(
        Guid selected,
        long selection,
        long navigation,
        CancellationToken token,
        RefreshFlight flight)
    {
        if (!flight.HasManualIntent || flight.Origin == RefreshOrigin.Retry) return;
        if (snapshot?.LastSuccessfulSyncAtUtc is { } lastSuccess)
        {
            var remaining = RemainingLifetime(lastSuccess);
            if (remaining > TimeSpan.Zero)
            {
                ScheduleDelay(selected, selection, navigation, token, remaining, RefreshOrigin.AutomaticDue);
                return;
            }
        }
        ScheduleDelay(selected, selection, navigation, token, StaleRetryDelay, RefreshOrigin.Retry);
    }

    private TimeSpan RemainingLifetime(DateTimeOffset lastSuccess) =>
        lastSuccess + CacheLifetime - timeProvider.GetUtcNow();

    private void ScheduleDelay(
        Guid selected,
        long selection,
        long navigation,
        CancellationToken activationToken,
        TimeSpan duration,
        RefreshOrigin origin)
    {
        CancelPendingDelay();
        if (!CanApply(selected, selection, navigation) || activationToken.IsCancellationRequested) return;
        var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, activationToken);
        pendingDelay = source;
        var generation = Volatile.Read(ref delayGeneration);
        ObserveActivation(RunDelayAsync(selected, selection, navigation, generation, duration, origin, source.Token));
    }

    private async Task RunDelayAsync(
        Guid selected,
        long selection,
        long navigation,
        long generation,
        TimeSpan duration,
        RefreshOrigin origin,
        CancellationToken cancellationToken)
    {
        try
        {
            await delay.WaitAsync(duration, cancellationToken);
            if (generation != Volatile.Read(ref delayGeneration) ||
                !CanApply(selected, selection, navigation) || cancellationToken.IsCancellationRequested) return;
            await StartOrJoinRefreshAsync(selected, selection, navigation, activation?.Token ?? lifetime.Token, origin);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch
        {
            if (CanApply(selected, selection, navigation)) StatusMessage = RefreshFailureMessage;
        }
    }

    private void CancelPendingDelay()
    {
        Interlocked.Increment(ref delayGeneration);
        var source = pendingDelay;
        pendingDelay = null;
        if (source is null) return;
        source.Cancel();
        source.Dispose();
    }

    private bool CanApply(Guid selected, long selection, long navigation) =>
        isActive && accountId == selected && selection == Volatile.Read(ref selectionGeneration) &&
        navigation == Volatile.Read(ref navigationGeneration);

    private void Apply(AccountShareCachedSnapshot value)
    {
        if (value.AccountId != accountId) return;
        snapshot = value;
        LastSuccessfulSyncText = value.LastSuccessfulSyncAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "尚未同步";
        OnPropertyChanged(nameof(LastSuccessfulSyncText));
        UpdateDateLabels();
        UpdateSummary();
        Rebuild();
    }

    private void UpdateSummary()
    {
        TotalCount = snapshot?.Items.Count ?? 0;
        ValidCount = snapshot?.Items.Count(item => item.Status == AccountShareRemoteStatus.Valid) ?? 0;
        InvalidCount = TotalCount - ValidCount;
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(ValidCount));
        OnPropertyChanged(nameof(InvalidCount));
    }

    private void Rebuild()
    {
        if (snapshot is null) { VisibleItems = []; return; }
        IEnumerable<AccountShareCachedRecord> source = snapshot.Items;
        if (!string.IsNullOrWhiteSpace(SearchText))
            source = source.Where(item => item.Title.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        source = StatusFilter switch
        {
            ShareAnalyticsStatusFilter.Valid => source.Where(item => item.Status == AccountShareRemoteStatus.Valid),
            ShareAnalyticsStatusFilter.Invalid => source.Where(item => item.Status == AccountShareRemoteStatus.Invalid),
            _ => source,
        };
        var projected = source.Select(item => projector.Project(item, SelectedMetric, timeProvider.GetUtcNow()));
        VisibleItems = projector.Sort(projected, sortColumn, sortDescending).Select(item => new ShareAnalyticsRow(item)).ToArray();
    }

    private void SelectMetric(AccountShareMetric metric)
    {
        if (Enum.IsDefined(metric)) SelectedMetric = metric;
    }

    private void Sort(AccountShareSortColumn column)
    {
        if (!Enum.IsDefined(column)) return;
        if (sortColumn == column) sortDescending = !sortDescending;
        else { sortColumn = column; sortDescending = true; }
        Rebuild();
    }

    private void Open(ShareAnalyticsRow? row)
    {
        if (row is null) return;
        _ = launcher.TryOpen(row.ShareUri, out var message);
        StatusMessage = message;
    }

    private void UpdateDateLabels()
    {
        var dates = projector.FiveDayWindow(timeProvider.GetUtcNow());
        PriorDay2Header = dates[2].ToString("MM-dd");
        PriorDay3Header = dates[3].ToString("MM-dd");
        PriorDay4Header = dates[4].ToString("MM-dd");
        OnPropertyChanged(nameof(PriorDay2Header));
        OnPropertyChanged(nameof(PriorDay3Header));
        OnPropertyChanged(nameof(PriorDay4Header));
    }

    public void Dispose()
    {
        isActive = false;
        Interlocked.Increment(ref navigationGeneration);
        Interlocked.Increment(ref selectionGeneration);
        IsRefreshing = false;
        RefreshCommand.NotifyCanExecuteChanged();
        CancelPendingDelay();
        lifetime.Cancel();
        activation?.Cancel();
        activation?.Dispose();
        lifetime.Dispose();
    }

    private enum RefreshOrigin
    {
        Initial,
        Manual,
        AutomaticDue,
        Retry,
    }

    private sealed class RefreshFlight(RefreshOrigin origin)
    {
        private int manualIntent = origin == RefreshOrigin.Manual ? 1 : 0;

        public RefreshOrigin Origin { get; } = origin;
        public Task Task { get; set; } = Task.CompletedTask;
        public bool HasManualIntent => Volatile.Read(ref manualIntent) != 0;
        public void AddManualIntent() => Volatile.Write(ref manualIntent, 1);
    }

    private static async void ObserveActivation(Task operation)
    {
        try { await operation.ConfigureAwait(true); }
        catch { }
    }
}
