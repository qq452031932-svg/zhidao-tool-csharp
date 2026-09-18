using BaiduBatchTransfer.Application.ShareAnalytics;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using SuperPan.Licensing.Client;

namespace BaiduBatchTransfer.App.Features.ShareAnalytics;

public sealed class ShareAnalyticsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(23);
    private static readonly TimeSpan StaleRetryDelay = TimeSpan.FromHours(1);
    private const string RefreshFailureMessage = "分享数据获取失败，请稍后重试。";
    private const string InvalidRangeMessage = "请输入有效的选取范围。";
    private const string SelectionFailureMessage = "选择操作失败，请重试。";
    private const string ExportFailureMessage = "导出失败，请重试。";
    private const int MaximumDisplayedFileNameLength = 96;

    private readonly IShareAnalyticsWorkspaceService service;
    private readonly AccountShareAnalyticsProjector projector;
    private readonly IShareLinkLauncher launcher;
    private readonly TimeProvider timeProvider;
    private readonly IShareAnalyticsDelay delay;
    private readonly IShareAnalyticsWorkbookExportService exportService;
    private readonly IResultSaveDialog saveDialog;
    private readonly IShareAnalyticsBulkScheduler bulkScheduler;
    private readonly ILicenseAuthorizationGate? licenseGate;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object refreshSync = new();
    private HashSet<string> selectedShareIds = new(StringComparer.Ordinal);
    private CancellationTokenSource? activation;
    private CancellationTokenSource? pendingDelay;
    private CancellationTokenSource? selectionOperation;
    private CancellationTokenSource? exportOperation;
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
    private string rangeStartText = "1";
    private string rangeEndText = "50";
    private IReadOnlyList<ShareAnalyticsRow> visibleItems = [];
    private int currentVisibleSelectedCount;
    private bool isSelectionBusy;
    private bool isExporting;
    private long selectionGeneration;
    private long snapshotGeneration;
    private long selectionOperationGeneration;
    private long navigationGeneration;
    private long delayGeneration;
    private long exportGeneration;

    public ShareAnalyticsViewModel(
        IShareAnalyticsWorkspaceService service,
        AccountShareAnalyticsProjector projector,
        IShareLinkLauncher launcher,
        TimeProvider timeProvider,
        IShareAnalyticsDelay delay,
        IShareAnalyticsWorkbookExportService exportService,
        IResultSaveDialog saveDialog,
        IShareAnalyticsBulkScheduler? bulkScheduler = null,
        ILicenseAuthorizationGate? licenseGate = null)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.projector = projector ?? throw new ArgumentNullException(nameof(projector));
        this.launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.delay = delay ?? throw new ArgumentNullException(nameof(delay));
        this.exportService = exportService ?? throw new ArgumentNullException(nameof(exportService));
        this.saveDialog = saveDialog ?? throw new ArgumentNullException(nameof(saveDialog));
        this.bulkScheduler = bulkScheduler ?? new ThreadPoolShareAnalyticsBulkScheduler();
        this.licenseGate = licenseGate;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => accountId is not null && isActive);
        SelectMetricCommand = new RelayCommand<AccountShareMetric>(SelectMetric);
        SortCommand = new RelayCommand<AccountShareSortColumn>(Sort);
        OpenCommand = new RelayCommand<ShareAnalyticsRow>(Open, row => row is not null);
        ExportCommand = new AsyncRelayCommand(
            ExportAsync,
            CanExecuteExport,
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        SelectRangeCommand = new AsyncRelayCommand(SelectRangeAsync, CanExecuteRangeSelection);
        SelectAllCommand = new AsyncRelayCommand(
            token => RunBulkSelectionAsync(BulkSelectionKind.SelectAll, null, token),
            CanExecuteBulkSelection);
        InvertSelectionCommand = new AsyncRelayCommand(
            token => RunBulkSelectionAsync(BulkSelectionKind.Invert, null, token),
            CanExecuteBulkSelection);
        ClearSelectionCommand = new AsyncRelayCommand(
            token => RunBulkSelectionAsync(BulkSelectionKind.Clear, null, token),
            CanExecuteBulkSelection);
        ToggleHeaderSelectionCommand = new AsyncRelayCommand(
            token => RunBulkSelectionAsync(BulkSelectionKind.ToggleHeader, null, token),
            CanExecuteBulkSelection);
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
    public IAsyncRelayCommand ExportCommand { get; }
    public IAsyncRelayCommand SelectRangeCommand { get; }
    public IAsyncRelayCommand SelectAllCommand { get; }
    public IAsyncRelayCommand InvertSelectionCommand { get; }
    public IAsyncRelayCommand ClearSelectionCommand { get; }
    public IAsyncRelayCommand ToggleHeaderSelectionCommand { get; }
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
    public string RangeStartText { get => rangeStartText; set => SetProperty(ref rangeStartText, value ?? string.Empty); }
    public string RangeEndText { get => rangeEndText; set => SetProperty(ref rangeEndText, value ?? string.Empty); }
    public bool IsSelectionBusy => isSelectionBusy;
    public bool IsExporting
    {
        get => isExporting;
        private set
        {
            if (!SetProperty(ref isExporting, value)) return;
            ExportCommand.NotifyCanExecuteChanged();
        }
    }
    public bool IsHeaderSelectionEnabled => CanExecuteBulkSelection();
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
    public int SelectedCount => selectedShareIds.Count;
    public int CurrentResultCount => VisibleItems.Count;
    public bool? HeaderSelectionState
    {
        get
        {
            if (VisibleItems.Count == 0 || currentVisibleSelectedCount == 0) return false;
            return currentVisibleSelectedCount == VisibleItems.Count ? true : null;
        }
    }
    public string LastSuccessfulSyncText { get; private set; } = "尚未同步";
    public string CacheStatusText => snapshot?.LastSuccessfulSyncAtUtc is { } updatedAt
        ? $"本地缓存｜更新于 {updatedAt.ToLocalTime():yyyy-MM-dd HH:mm}｜{snapshot.Items.Count:N0} 条"
        : "尚无本地缓存";
    public string PriorDay2Header { get; private set; } = string.Empty;
    public string PriorDay3Header { get; private set; } = string.Empty;
    public string PriorDay4Header { get; private set; } = string.Empty;
    public AccountShareSortColumn SortColumn => sortColumn;
    public bool SortDescending => sortDescending;
    public string SharedAtSortGlyph => SortGlyph(AccountShareSortColumn.SharedAt);
    public string HistoricalTotalSortGlyph => SortGlyph(AccountShareSortColumn.HistoricalTotal);
    public string NewValueSortGlyph => SortGlyph(AccountShareSortColumn.NewValue);
    public string TodaySortGlyph => SortGlyph(AccountShareSortColumn.Today);
    public string YesterdaySortGlyph => SortGlyph(AccountShareSortColumn.Yesterday);
    public string PriorDay2SortGlyph => SortGlyph(AccountShareSortColumn.PriorDay2);
    public string PriorDay3SortGlyph => SortGlyph(AccountShareSortColumn.PriorDay3);
    public string PriorDay4SortGlyph => SortGlyph(AccountShareSortColumn.PriorDay4);

    public void SelectAccount(Guid? selectedAccountId, string? displayName)
    {
        if (selectedAccountId == Guid.Empty) selectedAccountId = null;
        var accountChanged = accountId != selectedAccountId;
        if (accountChanged) InvalidateExport();
        InvalidateBulkSelection();
        Interlocked.Increment(ref snapshotGeneration);
        CancelPendingDelay();
        activation?.Cancel();
        IsRefreshing = false;
        Interlocked.Increment(ref selectionGeneration);
        accountId = selectedAccountId;
        CurrentAccountName = selectedAccountId is null || string.IsNullOrWhiteSpace(displayName)
            ? "未选择账号"
            : displayName.Trim();
        snapshot = null;
        ReplaceVisibleItems([]);
        NotifyCurrentResultsChanged();
        if (accountChanged && selectedShareIds.Count > 0)
        {
            selectedShareIds.Clear();
            OnPropertyChanged(nameof(SelectedCount));
            NotifySelectionDependentCanExecuteChanged();
        }
        LastSuccessfulSyncText = "尚未同步";
        OnPropertyChanged(nameof(LastSuccessfulSyncText));
        OnPropertyChanged(nameof(CacheStatusText));
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
        ExportCommand.NotifyCanExecuteChanged();
        NotifyBulkSelectionCanExecuteChanged();
        OnPropertyChanged(nameof(IsHeaderSelectionEnabled));
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
            {
                StatusMessage = "已显示本地缓存。";
                ScheduleDelay(selected, selection, navigation, token, remaining, RefreshOrigin.AutomaticDue);
            }
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
        InvalidateExport();
        InvalidateBulkSelection();
        Interlocked.Increment(ref navigationGeneration);
        CancelPendingDelay();
        activation?.Cancel();
        IsRefreshing = false;
        RefreshCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        NotifyBulkSelectionCanExecuteChanged();
        OnPropertyChanged(nameof(IsHeaderSelectionEnabled));
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!isActive || accountId is not { } selected) return;
        InvalidateBulkSelection();
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
        if (licenseGate is not null)
        {
            var authorization = await licenseGate
                .AuthorizeAsync(LicenseOperationKind.ShareAnalyticsRefresh, cancellationToken)
                .ConfigureAwait(true);
            if (!authorization.IsAuthorized)
            {
                if (CanApply(selected, selection, navigation))
                    StatusMessage = "软件尚未激活，操作未开始。请在「设置中心 → 软件授权」中激活后重试。";
                return;
            }
        }
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
        InvalidateBulkSelection();
        Interlocked.Increment(ref snapshotGeneration);
        var selectionChanged = false;
        if (selectedShareIds.Count > 0)
        {
            var availableShareIds = value.Items
                .Select(item => item.ShareId)
                .ToHashSet(StringComparer.Ordinal);
            selectionChanged = selectedShareIds.RemoveWhere(shareId => !availableShareIds.Contains(shareId)) > 0;
        }
        snapshot = value;
        LastSuccessfulSyncText = value.LastSuccessfulSyncAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "尚未同步";
        OnPropertyChanged(nameof(LastSuccessfulSyncText));
        OnPropertyChanged(nameof(CacheStatusText));
        UpdateDateLabels();
        UpdateSummary();
        Rebuild(invalidateBulkSelection: false);
        if (selectionChanged)
        {
            OnPropertyChanged(nameof(SelectedCount));
            NotifySelectionDependentCanExecuteChanged();
        }
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

    private void Rebuild(bool invalidateBulkSelection = true)
    {
        if (invalidateBulkSelection) InvalidateBulkSelection();
        if (snapshot is null)
        {
            ReplaceVisibleItems([]);
            NotifyCurrentResultsChanged();
            return;
        }
        IEnumerable<AccountShareCachedRecord> source = snapshot.Items;
        if (!string.IsNullOrWhiteSpace(SearchText))
            source = source.Where(item => item.Title.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        source = StatusFilter switch
        {
            ShareAnalyticsStatusFilter.Valid => source.Where(item => item.Status == AccountShareRemoteStatus.Valid),
            ShareAnalyticsStatusFilter.Invalid => source.Where(item => item.Status == AccountShareRemoteStatus.Invalid),
            _ => source,
        };
        var projectedAt = timeProvider.GetUtcNow();
        var projected = source.Select(item => projector.Project(item, SelectedMetric, projectedAt));
        var ordered = projector.Sort(projected, sortColumn, sortDescending).ToArray();
        ReplaceVisibleItems(ordered
            .Select((item, index) => new ShareAnalyticsRow(
                item,
                index + 1,
                selectedShareIds.Contains(item.ShareId),
                OnRowSelectionChanged))
            .ToArray());
        NotifyCurrentResultsChanged();
    }

    private void ReplaceVisibleItems(IReadOnlyList<ShareAnalyticsRow> replacement)
    {
        DeactivateVisibleItems();
        currentVisibleSelectedCount = replacement.Count(row => row.IsSelected);
        VisibleItems = replacement;
    }

    private void DeactivateVisibleItems()
    {
        foreach (var row in VisibleItems) row.Deactivate();
    }

    private void OnRowSelectionChanged(string shareId, bool selected)
    {
        InvalidateBulkSelection();
        var changed = selected ? selectedShareIds.Add(shareId) : selectedShareIds.Remove(shareId);
        if (!changed) return;
        currentVisibleSelectedCount += selected ? 1 : -1;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HeaderSelectionState));
        NotifySelectionDependentCanExecuteChanged();
    }

    private void NotifyCurrentResultsChanged()
    {
        OnPropertyChanged(nameof(CurrentResultCount));
        OnPropertyChanged(nameof(HeaderSelectionState));
        OnPropertyChanged(nameof(IsHeaderSelectionEnabled));
        NotifyBulkSelectionCanExecuteChanged();
    }

    private void NotifySelectionDependentCanExecuteChanged()
    {
        ExportCommand.NotifyCanExecuteChanged();
    }

    private bool CanExecuteExport() =>
        isActive && accountId is not null && snapshot is not null && selectedShareIds.Count > 0 && !IsExporting;

    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        if (!CanExecuteExport() || accountId is not { } selectedAccount || snapshot is not { } currentSnapshot)
            return;

        var generation = Interlocked.Increment(ref exportGeneration);
        var frozen = FreezeExport(selectedAccount, generation, currentSnapshot);
        var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        var operationToken = source.Token;
        exportOperation = source;
        IsExporting = true;
        try
        {
            var destination = await saveDialog
                .PickDestinationDirectoryAsync(operationToken)
                .ConfigureAwait(true);
            if (operationToken.IsCancellationRequested || !CanApply(frozen) ||
                string.IsNullOrWhiteSpace(destination))
                return;

            var request = new ShareAnalyticsWorkbookExportRequest(
                destination,
                frozen.Metric,
                frozen.Dates,
                frozen.Rows);
            var result = await exportService.ExportAsync(request, operationToken).ConfigureAwait(true);
            if (operationToken.IsCancellationRequested || !CanApply(frozen)) return;

            var count = result.ExportedRows.ToString("N0", CultureInfo.InvariantCulture);
            StatusMessage = $"已导出 {count} 条：{SafeDisplayFileName(result.FinalPath)}";
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (!operationToken.IsCancellationRequested && CanApply(frozen))
                StatusMessage = ExportFailureMessage;
        }
        finally
        {
            if (generation == Volatile.Read(ref exportGeneration) && ReferenceEquals(exportOperation, source))
            {
                exportOperation = null;
                source.Dispose();
                IsExporting = false;
            }
        }
    }

    private FrozenExport FreezeExport(
        Guid selectedAccount,
        long generation,
        AccountShareCachedSnapshot currentSnapshot)
    {
        var projectedAt = timeProvider.GetUtcNow();
        var metric = SelectedMetric;
        var currentSort = sortColumn;
        var currentSortDescending = sortDescending;
        var selectedIds = selectedShareIds.OrderBy(value => value, StringComparer.Ordinal).ToImmutableArray();
        var selectedIdSet = selectedIds.ToHashSet(StringComparer.Ordinal);
        var projected = currentSnapshot.Items
            .Where(item => selectedIdSet.Contains(item.ShareId))
            .Select(item => projector.Project(item, metric, projectedAt));
        var ordered = projector.Sort(projected, currentSort, currentSortDescending);
        var rows = ordered.Select(item => new ShareAnalyticsWorkbookExportRow(
                ShareAnalyticsRow.ResolveDisplayTitle(item.Title, item.ShareId),
                item.ShareUri.AbsoluteUri,
                item.Status,
                item.HistoricalTotal,
                item.DailyValues))
            .ToImmutableArray();
        return new(
            selectedAccount,
            generation,
            metric,
            currentSort,
            currentSortDescending,
            projector.FiveDayWindow(projectedAt).ToImmutableArray(),
            selectedIds,
            rows);
    }

    private bool CanApply(FrozenExport frozen) =>
        isActive && accountId == frozen.AccountId &&
        frozen.Generation == Volatile.Read(ref exportGeneration);

    private void InvalidateExport()
    {
        Interlocked.Increment(ref exportGeneration);
        var source = exportOperation;
        exportOperation = null;
        if (source is not null)
        {
            source.Cancel();
            source.Dispose();
        }
        IsExporting = false;
    }

    private static string SafeDisplayFileName(string? finalPath)
    {
        string fileName;
        try
        {
            fileName = Path.GetFileName(finalPath ?? string.Empty);
        }
        catch
        {
            return "导出文件";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName
            .Where(character => !char.IsControl(character) && !invalid.Contains(character))
            .ToArray())
            .Trim();
        if (string.IsNullOrWhiteSpace(sanitized) || sanitized is "." or "..") return "导出文件";
        return sanitized.Length <= MaximumDisplayedFileNameLength
            ? sanitized
            : sanitized[..MaximumDisplayedFileNameLength];
    }

    private bool CanExecuteBulkSelection() =>
        isActive && accountId is not null && snapshot is not null && VisibleItems.Count > 0 && !isSelectionBusy;

    private bool CanExecuteRangeSelection() =>
        isActive && accountId is not null && snapshot is not null && !isSelectionBusy;

    private async Task SelectRangeAsync(CancellationToken cancellationToken)
    {
        if (!TryGetRange(out var range))
        {
            StatusMessage = InvalidRangeMessage;
            return;
        }

        await RunBulkSelectionAsync(BulkSelectionKind.Range, range, cancellationToken);
    }

    private bool TryGetRange(out SelectionRange range)
    {
        range = default;
        if (!int.TryParse(RangeStartText, NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
            !int.TryParse(RangeEndText, NumberStyles.None, CultureInfo.InvariantCulture, out var end) ||
            start <= 0 || end <= 0 || start > end || start > CurrentResultCount)
            return false;
        range = new(start - 1, Math.Min(end, CurrentResultCount));
        return range.StartIndex < range.EndExclusive;
    }

    private async Task RunBulkSelectionAsync(
        BulkSelectionKind kind,
        SelectionRange? range,
        CancellationToken cancellationToken)
    {
        if (!CanExecuteBulkSelection() || accountId is not { } selectedAccount) return;
        var generation = Interlocked.Increment(ref selectionOperationGeneration);
        var fence = new SelectionFence(
            selectedAccount,
            Volatile.Read(ref snapshotGeneration),
            SelectedMetric,
            SearchText,
            StatusFilter,
            sortColumn,
            sortDescending,
            generation);
        var projectedRows = VisibleItems.Select(row => row.Projected).ToArray();
        var selectedIds = selectedShareIds.ToArray();
        var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        var operationToken = source.Token;
        selectionOperation = source;
        SetSelectionBusy(true);
        try
        {
            var result = await bulkScheduler.RunAsync(
                token => BuildBulkSelection(kind, range, projectedRows, selectedIds, token),
                operationToken);
            if (operationToken.IsCancellationRequested || !CanApply(fence)) return;

            selectedShareIds = result.SelectedShareIds;
            ReplaceVisibleItems(result.Rows);
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(CurrentResultCount));
            OnPropertyChanged(nameof(HeaderSelectionState));
            NotifySelectionDependentCanExecuteChanged();
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (CanApply(fence) && !operationToken.IsCancellationRequested)
                StatusMessage = SelectionFailureMessage;
        }
        finally
        {
            if (generation == Volatile.Read(ref selectionOperationGeneration) &&
                ReferenceEquals(selectionOperation, source))
            {
                selectionOperation = null;
                source.Dispose();
                SetSelectionBusy(false);
            }
        }
    }

    private BulkSelectionResult BuildBulkSelection(
        BulkSelectionKind kind,
        SelectionRange? range,
        IReadOnlyList<AccountShareProjectedRow> projectedRows,
        IReadOnlyList<string> selectedIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var replacementSelection = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < selectedIds.Count; index++)
        {
            ThrowIfCancellationRequested(cancellationToken, index);
            replacementSelection.Add(selectedIds[index]);
        }
        switch (kind)
        {
            case BulkSelectionKind.Range:
                var selectedRange = range!.Value;
                for (var index = selectedRange.StartIndex; index < selectedRange.EndExclusive; index++)
                {
                    ThrowIfCancellationRequested(cancellationToken, index - selectedRange.StartIndex);
                    replacementSelection.Add(projectedRows[index].ShareId);
                }
                break;
            case BulkSelectionKind.SelectAll:
                for (var index = 0; index < projectedRows.Count; index++)
                {
                    ThrowIfCancellationRequested(cancellationToken, index);
                    replacementSelection.Add(projectedRows[index].ShareId);
                }
                break;
            case BulkSelectionKind.Invert:
                for (var index = 0; index < projectedRows.Count; index++)
                {
                    ThrowIfCancellationRequested(cancellationToken, index);
                    var row = projectedRows[index];
                    if (!replacementSelection.Remove(row.ShareId)) replacementSelection.Add(row.ShareId);
                }
                break;
            case BulkSelectionKind.Clear:
                for (var index = 0; index < projectedRows.Count; index++)
                {
                    ThrowIfCancellationRequested(cancellationToken, index);
                    replacementSelection.Remove(projectedRows[index].ShareId);
                }
                break;
            case BulkSelectionKind.ToggleHeader:
                var allSelected = true;
                for (var index = 0; index < projectedRows.Count; index++)
                {
                    ThrowIfCancellationRequested(cancellationToken, index);
                    if (replacementSelection.Contains(projectedRows[index].ShareId)) continue;
                    allSelected = false;
                    break;
                }
                for (var index = 0; index < projectedRows.Count; index++)
                {
                    ThrowIfCancellationRequested(cancellationToken, index);
                    if (allSelected) replacementSelection.Remove(projectedRows[index].ShareId);
                    else replacementSelection.Add(projectedRows[index].ShareId);
                }
                break;
        }

        var rows = new ShareAnalyticsRow[projectedRows.Count];
        for (var index = 0; index < projectedRows.Count; index++)
        {
            ThrowIfCancellationRequested(cancellationToken, index);
            var row = projectedRows[index];
            rows[index] = new ShareAnalyticsRow(
                row,
                index + 1,
                replacementSelection.Contains(row.ShareId),
                OnRowSelectionChanged);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(replacementSelection, rows);
    }

    private static void ThrowIfCancellationRequested(CancellationToken cancellationToken, int index)
    {
        if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
    }

    private bool CanApply(SelectionFence fence) =>
        isActive &&
        accountId == fence.AccountId &&
        Volatile.Read(ref snapshotGeneration) == fence.SnapshotGeneration &&
        SelectedMetric == fence.Metric &&
        string.Equals(SearchText, fence.Search, StringComparison.Ordinal) &&
        StatusFilter == fence.Status &&
        sortColumn == fence.Sort &&
        sortDescending == fence.Descending &&
        Volatile.Read(ref selectionOperationGeneration) == fence.OperationGeneration;

    private void InvalidateBulkSelection()
    {
        Interlocked.Increment(ref selectionOperationGeneration);
        var source = selectionOperation;
        selectionOperation = null;
        if (source is not null)
        {
            source.Cancel();
            source.Dispose();
        }
        SetSelectionBusy(false);
    }

    private void SetSelectionBusy(bool value)
    {
        if (!SetProperty(ref isSelectionBusy, value, nameof(IsSelectionBusy))) return;
        OnPropertyChanged(nameof(IsHeaderSelectionEnabled));
        NotifyBulkSelectionCanExecuteChanged();
    }

    private void NotifyBulkSelectionCanExecuteChanged()
    {
        SelectRangeCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        InvertSelectionCommand.NotifyCanExecuteChanged();
        ClearSelectionCommand.NotifyCanExecuteChanged();
        ToggleHeaderSelectionCommand.NotifyCanExecuteChanged();
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
        NotifySortStateChanged();
        Rebuild();
    }

    private string SortGlyph(AccountShareSortColumn column) =>
        sortColumn == column ? sortDescending ? "↓" : "↑" : string.Empty;

    private void NotifySortStateChanged()
    {
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDescending));
        OnPropertyChanged(nameof(SharedAtSortGlyph));
        OnPropertyChanged(nameof(HistoricalTotalSortGlyph));
        OnPropertyChanged(nameof(NewValueSortGlyph));
        OnPropertyChanged(nameof(TodaySortGlyph));
        OnPropertyChanged(nameof(YesterdaySortGlyph));
        OnPropertyChanged(nameof(PriorDay2SortGlyph));
        OnPropertyChanged(nameof(PriorDay3SortGlyph));
        OnPropertyChanged(nameof(PriorDay4SortGlyph));
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
        InvalidateExport();
        InvalidateBulkSelection();
        Interlocked.Increment(ref navigationGeneration);
        Interlocked.Increment(ref selectionGeneration);
        IsRefreshing = false;
        RefreshCommand.NotifyCanExecuteChanged();
        DeactivateVisibleItems();
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

    private enum BulkSelectionKind
    {
        Range,
        SelectAll,
        Invert,
        Clear,
        ToggleHeader,
    }

    private readonly record struct SelectionRange(int StartIndex, int EndExclusive);

    private sealed record SelectionFence(
        Guid AccountId,
        long SnapshotGeneration,
        AccountShareMetric Metric,
        string Search,
        ShareAnalyticsStatusFilter Status,
        AccountShareSortColumn Sort,
        bool Descending,
        long OperationGeneration);

    private sealed record BulkSelectionResult(
        HashSet<string> SelectedShareIds,
        IReadOnlyList<ShareAnalyticsRow> Rows);

    private sealed record FrozenExport(
        Guid AccountId,
        long Generation,
        AccountShareMetric Metric,
        AccountShareSortColumn Sort,
        bool SortDescending,
        ImmutableArray<DateOnly> Dates,
        ImmutableArray<string> SelectedIds,
        ImmutableArray<ShareAnalyticsWorkbookExportRow> Rows);

    private sealed class ThreadPoolShareAnalyticsBulkScheduler : IShareAnalyticsBulkScheduler
    {
        public Task<T> RunAsync<T>(
            Func<CancellationToken, T> operation,
            CancellationToken cancellationToken) =>
            Task.Run(() => operation(cancellationToken), cancellationToken);
    }

    private static async void ObserveActivation(Task operation)
    {
        try { await operation.ConfigureAwait(true); }
        catch { }
    }
}
