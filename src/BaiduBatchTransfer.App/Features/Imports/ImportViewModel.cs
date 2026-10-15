using System.Collections.ObjectModel;
using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Features.Transfers;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Imports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaiduBatchTransfer.App.Features.Imports;

public enum ImportUiError
{
    InvalidWorkbook,
    UnsafeWorkbook,
    MissingHeader,
    InvalidData,
    AdapterUnavailable,
    Unknown,
}

public sealed class ImportWorkflowException(ImportUiError error, Exception? innerException = null)
    : Exception("The import workflow failed.", innerException)
{
    public ImportUiError Error { get; } = error;

    public override string ToString() =>
        $"ImportWorkflowException {{ Error = {Error}, Details = [REDACTED] }}";
}

public sealed record ImportDraftStatistics(
    int Total,
    int Ready,
    int Duplicate,
    int Invalid,
    int Excluded,
    int Conflict = 0,
    int NeedsResolution = 0,
    bool HasExcelAccountColumn = false,
    int UnresolvedAccountMappings = 0,
    IReadOnlyList<Guid>? MappedAccountIds = null);
public sealed record ImportWorkflowResult(Guid SessionId, ImportDraftStatistics Statistics);
public sealed record ImportDraftReady(Guid SessionId, ImportDraftStatistics Statistics);

public interface IImportWorkflow
{
    bool IsAvailable => true;
    string StatusMessage => string.Empty;
    Task<ImportWorkflowResult> ImportTextAsync(Guid sessionId, string text, CancellationToken cancellationToken);
    Task<ImportWorkflowResult> ImportTextAsync(
        Guid sessionId,
        string text,
        IProgress<ImportCommittedProgress> committedProgress,
        CancellationToken cancellationToken) =>
        ImportTextAsync(sessionId, text, cancellationToken);
    Task<ImportWorkflowResult> ImportExcelAsync(Guid sessionId, string filePath, CancellationToken cancellationToken);
    Task<ImportWorkflowResult> ImportExcelAsync(
        Guid sessionId,
        string filePath,
        IProgress<ImportCommittedProgress> committedProgress,
        CancellationToken cancellationToken) =>
        ImportExcelAsync(sessionId, filePath, cancellationToken);
    Task<ImportWorkflowResult> ResumeAsync(Guid sessionId, CancellationToken cancellationToken);
    Task<ImportWorkflowResult> ResumeAsync(
        Guid sessionId,
        IProgress<ImportCommittedProgress> committedProgress,
        CancellationToken cancellationToken) =>
        ResumeAsync(sessionId, cancellationToken);
    Task<Guid?> FindLatestResumableExcelSessionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<Guid?>(null);
    Task<bool> CanResumeAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(HasResumableReplaySource(sessionId));
    bool HasResumableReplaySource(Guid sessionId) => true;
    void DiscardReplaySource(Guid sessionId) { }
    Task<ImportWorkflowResult> UpdateLinkAsync(Guid rowId, string shareUrl, string? extractionCode, CancellationToken cancellationToken);
    Task<ImportWorkflowResult> SetExcludedAsync(Guid rowId, bool excluded, CancellationToken cancellationToken);
    /// <summary>把一行从待处理列表里彻底删除；与排除不同，删除后不能恢复。</summary>
    Task<ImportWorkflowResult> DeleteRowAsync(Guid rowId, CancellationToken cancellationToken) =>
        Task.FromException<ImportWorkflowResult>(new ImportWorkflowException(ImportUiError.AdapterUnavailable));
    /// <summary>一次删除多行；要么全部删掉，要么一行都不删。</summary>
    Task<ImportWorkflowResult> DeleteRowsAsync(IReadOnlyList<Guid> rowIds, CancellationToken cancellationToken) =>
        Task.FromException<ImportWorkflowResult>(new ImportWorkflowException(ImportUiError.AdapterUnavailable));
}

public sealed class UnavailableImportWorkflow : IImportWorkflow
{
    public bool IsAvailable => false;
    public string StatusMessage => "本地导入流程尚不可用。";
    private static ImportWorkflowException Error() => new(ImportUiError.AdapterUnavailable);
    public Task<ImportWorkflowResult> ImportTextAsync(Guid sessionId, string text, CancellationToken cancellationToken) => Task.FromException<ImportWorkflowResult>(Error());
    public Task<ImportWorkflowResult> ImportExcelAsync(Guid sessionId, string filePath, CancellationToken cancellationToken) => Task.FromException<ImportWorkflowResult>(Error());
    public Task<ImportWorkflowResult> ResumeAsync(Guid sessionId, CancellationToken cancellationToken) => Task.FromException<ImportWorkflowResult>(Error());
    public Task<ImportWorkflowResult> UpdateLinkAsync(Guid rowId, string shareUrl, string? extractionCode, CancellationToken cancellationToken) => Task.FromException<ImportWorkflowResult>(Error());
    public Task<ImportWorkflowResult> SetExcludedAsync(Guid rowId, bool excluded, CancellationToken cancellationToken) => Task.FromException<ImportWorkflowResult>(Error());
}

public sealed class ImportViewModel : ObservableObject, IDisposable
{
    private const int PageSize = 100;
    private readonly IImportWorkflow workflow;
    private readonly IImportPreviewPageSource pageSource;
    private readonly IDialogService dialogService;
    private readonly IAdapterAvailability adapterAvailability;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim mutationGate = new(1, 1);
    private CancellationTokenSource? operationCancellation;
    private Guid? sessionId;
    private string textContent = string.Empty;
    private string statusMessage = "粘贴百度分享链接或选择 Excel 文件开始导入。";
    private string statisticsText = "尚未导入数据";
    private bool isBusy;
    private bool canResumeImport;
    private bool durableSessionObserved;
    private bool isClosed;
    private long totalRows;
    private long displayOrdinalOffset;
    private long? nextCursor;
    // 当前页是从哪个游标读出来的；翻过的页按顺序记下，才能往回翻。
    private long? currentCursor;
    private readonly Stack<(long? Cursor, long OrdinalOffset)> previousPages = new();
    private bool showConflictsOnly;
    // 当前列表（可能经过筛选）的总条数，只用来算页码；TotalRows 始终是整批导入的条数。
    private long listedRows;
    private ImportPreviewRow? selectedRow;
    // 表格里同时选中的行（按住 Ctrl/Shift 或 Ctrl+A）。只用于批量删除；编辑仍针对单独一行。
    private IReadOnlyList<ImportPreviewRow> selectedRows = [];
    private string selectedShareUrl = string.Empty;
    private string selectedExtractionCode = string.Empty;
    private bool selectedRowIsExcluded;
    private long pageGeneration;
    private long mutationGeneration;
    private int mutationRequests;
    private int pageRequests;
    private bool externalCleanupBusy;
    private CurrentImportActivityGate activityGate = new();

    public ImportViewModel(
        IImportWorkflow workflow,
        IImportPreviewPageSource pageSource,
        IDialogService dialogService,
        IAdapterAvailability? adapterAvailability = null)
    {
        this.workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        this.pageSource = pageSource ?? throw new ArgumentNullException(nameof(pageSource));
        this.dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        this.adapterAvailability = adapterAvailability ?? AvailableAdapterAvailability.Instance;
        Rows = new();
        ImportTextCommand = new AsyncRelayCommand(ImportTextAsync, () => CanImportText);
        SelectAndImportExcelCommand = new AsyncRelayCommand(SelectAndImportExcelAsync, () => CanImport);
        ResumeImportCommand = new AsyncRelayCommand(ResumeImportAsync, () => CanEdit && CanResumeImport);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        LoadFirstPageCommand = new AsyncRelayCommand(LoadFirstPageAsync, () => CanEdit && sessionId is not null);
        LoadNextPageCommand = new AsyncRelayCommand(LoadNextPageAsync, () => CanEdit && nextCursor is not null);
        LoadPreviousPageCommand = new AsyncRelayCommand(
            LoadPreviousPageAsync, () => CanEdit && sessionId is not null && previousPages.Count > 0);
        DeleteSelectedRowCommand = new AsyncRelayCommand(
            DeleteSelectedRowAsync, () => CanEdit && RowsToDelete().Count > 0 && !CanResumeImport);
        SaveSelectedRowCommand = new AsyncRelayCommand(
            SaveSelectedRowAsync, () => CanEdit && SelectedRow is not null && !HasMultipleSelection);
        ToggleSelectedExclusionCommand = new AsyncRelayCommand(
            ToggleSelectedExclusionAsync, () => CanEdit && SelectedRow is not null && !HasMultipleSelection);
    }

    public ObservableCollection<ImportPreviewRow> Rows { get; }
    public event EventHandler<ImportDraftReady>? DraftReady;
    public IAsyncRelayCommand ImportTextCommand { get; }
    public IAsyncRelayCommand SelectAndImportExcelCommand { get; }
    public IAsyncRelayCommand ResumeImportCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand LoadFirstPageCommand { get; }
    public IAsyncRelayCommand LoadNextPageCommand { get; }
    public IAsyncRelayCommand LoadPreviousPageCommand { get; }
    public IAsyncRelayCommand DeleteSelectedRowCommand { get; }
    public IAsyncRelayCommand SaveSelectedRowCommand { get; }
    public IAsyncRelayCommand ToggleSelectedExclusionCommand { get; }

    public string TextContent { get => textContent; set { if (SetProperty(ref textContent, value ?? string.Empty)) RaiseCommands(); } }
    public string StatusMessage { get => statusMessage; private set => SetProperty(ref statusMessage, value); }
    public string StatisticsText { get => statisticsText; private set => SetProperty(ref statisticsText, value); }
    public bool IsBusy { get => isBusy; private set { if (SetProperty(ref isBusy, value)) { OnPropertyChanged(nameof(CanEdit)); RaiseCommands(); } } }
    public bool CanEdit => !isClosed && !IsBusy && !externalCleanupBusy && Volatile.Read(ref pageRequests) == 0;
    public bool CanResumeImport { get => canResumeImport; private set { if (SetProperty(ref canResumeImport, value)) RaiseCommands(); } }
    public bool CanImport => CanEdit && workflow.IsAvailable;
    public bool CanImportText => CanImport && !string.IsNullOrWhiteSpace(TextContent);
    public string AdapterStatusMessage => !workflow.IsAvailable
        ? workflow.StatusMessage
        : adapterAvailability.CanStartTransfers
            ? string.Empty
            : "可以先导入并整理数据；实际转存功能仍在验证中。";
    public long TotalRows
    {
        get => totalRows;
        private set { if (SetProperty(ref totalRows, value)) OnPropertyChanged(nameof(PageText)); }
    }
    public int PageNumber => previousPages.Count + 1;
    public string PageText
    {
        get
        {
            if (sessionId is null || listedRows <= 0) return string.Empty;
            var pages = Math.Max(PageNumber, (listedRows + PageSize - 1) / PageSize);
            return $"第 {PageNumber}/{pages} 页";
        }
    }

    /// <summary>
    /// 只列出「冲突待处理」的记录，方便在很长的表里把它们找出来处理。
    /// 筛选时序号是在冲突记录里的顺序，原表行号仍可在序号的提示里看到。
    /// </summary>
    public bool ShowConflictsOnly
    {
        get => showConflictsOnly;
        set
        {
            if (showConflictsOnly == value) return;
            if (!CanEdit || sessionId is null)
            {
                // 正忙或还没有导入时不切换；通知一次让勾选框回到原状。
                OnPropertyChanged();
                return;
            }
            showConflictsOnly = value;
            OnPropertyChanged();
            _ = LoadFirstPageAsync();
        }
    }
    public bool CanFilterConflicts => CanEdit && sessionId is not null;
    public bool IsClosed => isClosed;
    public Guid? SessionId => sessionId;
    public ImportPreviewRow? SelectedRow
    {
        get => selectedRow;
        set
        {
            if (!SetProperty(ref selectedRow, value)) return;
            SelectedShareUrl = value?.ShareUrl ?? string.Empty;
            SelectedExtractionCode = value?.ExtractionCode ?? string.Empty;
            selectedRowIsExcluded = string.Equals(value?.DispositionText, "已排除", StringComparison.Ordinal);
            OnPropertyChanged(nameof(SelectedExclusionActionText));
            SaveSelectedRowCommand.NotifyCanExecuteChanged();
            ToggleSelectedExclusionCommand.NotifyCanExecuteChanged();
            DeleteSelectedRowCommand.NotifyCanExecuteChanged();
        }
    }
    public string SelectedShareUrl { get => selectedShareUrl; set => SetProperty(ref selectedShareUrl, value ?? string.Empty); }
    public string SelectedExtractionCode { get => selectedExtractionCode; set => SetProperty(ref selectedExtractionCode, value ?? string.Empty); }
    public string SelectedExclusionActionText => selectedRowIsExcluded ? "恢复所选记录" : "排除所选记录";
    public IReadOnlyList<ImportPreviewRow> SelectedRows => selectedRows;
    public bool HasMultipleSelection => selectedRows.Count > 1;
    /// <summary>选中多行时，下面的链接/提取码编辑框不知道该改哪一行，所以只在选中一行时可用。</summary>
    public bool CanEditSelectedRow => !HasMultipleSelection;
    public string DeleteMenuText => HasMultipleSelection ? $"删除所选 {selectedRows.Count} 条记录" : "删除这条记录";

    /// <summary>由表格在选中项变化时调用。只认当前这一页里的行。</summary>
    public void SetSelectedRows(IEnumerable<ImportPreviewRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var onPage = rows.Where(row => Rows.Contains(row)).Distinct().ToArray();
        if (onPage.SequenceEqual(selectedRows)) return;
        var wasMultiple = HasMultipleSelection;
        selectedRows = onPage;
        OnPropertyChanged(nameof(SelectedRows));
        OnPropertyChanged(nameof(DeleteMenuText));
        if (wasMultiple != HasMultipleSelection)
        {
            OnPropertyChanged(nameof(HasMultipleSelection));
            OnPropertyChanged(nameof(CanEditSelectedRow));
        }
        DeleteSelectedRowCommand.NotifyCanExecuteChanged();
        SaveSelectedRowCommand.NotifyCanExecuteChanged();
        ToggleSelectedExclusionCommand.NotifyCanExecuteChanged();
    }

    private IReadOnlyList<ImportPreviewRow> RowsToDelete() =>
        selectedRows.Count > 0 ? selectedRows : SelectedRow is { } row ? [row] : [];

    public void AttachSession(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("导入会话不能为空。", nameof(value));
        durableSessionObserved = true;
        if (sessionId != value) ClearConflictFilter();
        sessionId = value;
        pageGeneration++;
        OnPropertyChanged(nameof(SessionId));
        RaiseCommands();
    }

    internal void SetExternalCleanupBusy(bool value)
    {
        if (externalCleanupBusy == value) return;
        externalCleanupBusy = value;
        OnPropertyChanged(nameof(CanEdit));
        RaiseCommands();
    }

    internal void AttachActivityGate(CurrentImportActivityGate value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (activityGate.HasActiveActivities)
            throw new InvalidOperationException("Cannot replace the current-import activity gate while an operation is active.");
        activityGate = value;
    }

    public bool TryResetAfterSuccessfulCleanup(Guid expectedSessionId)
    {
        if (expectedSessionId == Guid.Empty || sessionId != expectedSessionId) return false;
        pageGeneration++;
        Interlocked.Increment(ref mutationGeneration);
        Rows.Clear();
        SelectedRow = null;
        SetSelectedRows([]);
        SelectedShareUrl = string.Empty;
        SelectedExtractionCode = string.Empty;
        selectedRowIsExcluded = false;
        nextCursor = null;
        currentCursor = null;
        previousPages.Clear();
        listedRows = 0;
        ClearConflictFilter();
        TotalRows = 0;
        StatisticsText = "尚未导入数据";
        CanResumeImport = false;
        durableSessionObserved = false;
        sessionId = null;
        workflow.DiscardReplaySource(expectedSessionId);
        OnPropertyChanged(nameof(SessionId));
        OnPropertyChanged(nameof(SelectedExclusionActionText));
        NotifyPagePosition();
        RaiseCommands();
        return true;
    }

    public void ApplyStatistics(ImportDraftStatistics statistics)
    {
        StatisticsText = $"总计 {statistics.Total:N0} · 就绪 {statistics.Ready:N0} · 重复 {statistics.Duplicate:N0} · 异常 {statistics.Invalid:N0} · 已排除 {statistics.Excluded:N0} · 冲突 {statistics.Conflict:N0} · 待解析 {statistics.NeedsResolution:N0}";
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (isClosed) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        var resumableSessionId = await workflow.FindLatestResumableExcelSessionAsync(linked.Token);
        if (isClosed || linked.IsCancellationRequested) return;
        if (resumableSessionId is not { } value) return;
        AttachSession(value);
        CanResumeImport = true;
        StatusMessage = "发现未完成的 Excel 导入，可以继续。";
    }

    public async Task ImportTextAsync(CancellationToken cancellationToken = default)
    {
        if (!CanEdit) return;
        using var activity = activityGate.TryEnterActivity();
        if (activity is null) return;
        var newSessionId = Guid.NewGuid();
        BeginNewImport();
        await RunImportAsync(
            newSessionId,
            isResume: false,
            (progress, token) => workflow.ImportTextAsync(newSessionId, TextContent, progress, token),
            cancellationToken);
    }

    public async Task ImportExcelAsync(string filePath, CancellationToken cancellationToken = default,
        bool preserveCurrentOnFailure = false)
    {
        if (!CanEdit || string.IsNullOrWhiteSpace(filePath)) return;
        using var activity = activityGate.TryEnterActivity();
        if (activity is null) return;
        var newSessionId = Guid.NewGuid();
        var previousSession = sessionId;
        var previousDurable = durableSessionObserved;
        var previousCanResume = CanResumeImport;
        BeginNewImport(discardPrevious: !preserveCurrentOnFailure);
        await RunImportAsync(
            newSessionId,
            isResume: false,
            (progress, token) => workflow.ImportExcelAsync(newSessionId, filePath, progress, token),
            cancellationToken);
        if (!preserveCurrentOnFailure || isClosed) return;
        if (sessionId is null)
        {
            // 列映射取消或文件检查失败时，尚未建立新会话；旧表格、选择和统计仍在。
            sessionId = previousSession;
            durableSessionObserved = previousDurable;
            CanResumeImport = previousCanResume;
            pageGeneration++;
            OnPropertyChanged(nameof(SessionId));
            RaiseCommands();
            if (previousSession is not null) StatusMessage += " 原导入内容已保留。";
        }
        else if (previousSession is { } replaced && replaced != sessionId)
        {
            // 新会话已落盘（包括可恢复的部分导入），此时才释放旧来源。
            workflow.DiscardReplaySource(replaced);
        }
    }

    public async Task SelectAndImportExcelAsync(CancellationToken cancellationToken = default)
    {
        if (!CanEdit) return;
        using var activity = activityGate.TryEnterActivity();
        if (activity is null) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        StatusMessage = "请选择 Excel 文件";
        var path = await dialogService.PickExcelFileAsync(linked.Token);
        if (isClosed || linked.IsCancellationRequested) return;
        if (path is null)
        {
            StatusMessage = "未选择文件";
            return;
        }

        StatusMessage = "已选择 Excel 文件，正在检查";
        await ImportExcelAsync(path, linked.Token);
    }

    public async Task ResumeImportAsync(CancellationToken cancellationToken = default)
    {
        if (!CanEdit || sessionId is null) return;
        using var activity = activityGate.TryEnterActivity();
        if (activity is null) return;
        await RunImportAsync(
            sessionId.Value,
            isResume: true,
            (progress, token) => workflow.ResumeAsync(sessionId.Value, progress, token),
            cancellationToken);
    }

    public async Task LoadFirstPageAsync(CancellationToken cancellationToken = default)
    {
        if (isClosed || externalCleanupBusy) return;
        using var activity = activityGate.TryEnterActivity();
        if (activity is null) return;
        nextCursor = null;
        await LoadPageAsync(PageMove.First, cancellationToken);
    }

    public async Task LoadNextPageAsync(CancellationToken cancellationToken = default)
    {
        if (isClosed || externalCleanupBusy || nextCursor is null) return;
        using var activity = activityGate.TryEnterActivity();
        if (activity is null) return;
        await LoadPageAsync(PageMove.Next, cancellationToken);
    }

    public async Task LoadPreviousPageAsync(CancellationToken cancellationToken = default)
    {
        if (isClosed || externalCleanupBusy || previousPages.Count == 0) return;
        using var activity = activityGate.TryEnterActivity();
        if (activity is null) return;
        await LoadPageAsync(PageMove.Previous, cancellationToken);
    }

    /// <summary>
    /// 删除选中的记录（一行或多行）。排除只是留着不转存，删除则把它从列表里拿掉且不能恢复，所以先问一次。
    /// 多行一起删时要么全部删掉，要么一行都不删。删完留在当前页；这一页删空了就退回上一页。
    /// </summary>
    public async Task DeleteSelectedRowAsync(CancellationToken cancellationToken = default)
    {
        var targets = RowsToDelete();
        if (!CanEdit || CanResumeImport || targets.Count == 0) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        var single = targets.Count == 1;
        bool confirmed;
        try
        {
            confirmed = await dialogService.ConfirmAsync(
                "删除记录",
                (single
                    ? $"确定把第 {targets[0].DisplayOrdinal} 条记录从待处理列表中删除吗？"
                    : $"确定把选中的 {targets.Count} 条记录从待处理列表中删除吗？") +
                "\n\n删除后不能恢复，需要时请重新导入。只想这次不转存，可以改用「排除所选记录」。",
                linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { return; }
        // 确认框开着的时候选中项变了，就不按旧的选择删。
        if (!confirmed || isClosed || !CanEdit || !RowsToDelete().SequenceEqual(targets)) return;
        var rowIds = targets.Select(row => row.RowId).ToArray();
        _ = await RunMutationAsync(
            token => single ? workflow.DeleteRowAsync(rowIds[0], token) : workflow.DeleteRowsAsync(rowIds, token),
            single ? "已删除该记录。" : $"已删除 {rowIds.Length} 条记录。",
            "无法删除记录，请刷新后重试。",
            linked.Token,
            stayOnPage: true,
            describeFailure: DescribeDeleteFailure);
    }

    /// <summary>
    /// 删除失败时说出真实原因，并带上错误代号：这句话会进运行日志，事后排查靠它。
    /// 代号只取错误类别，不含链接、路径等内容。
    /// </summary>
    private static string DescribeDeleteFailure(Exception error)
    {
        var (reason, code) = error switch
        {
            DraftWorkflowException { Code: "DRAFT_NOT_MUTABLE" } draft =>
                ("这批导入已经生成转存任务或正在处理，不能再删除", draft.Code),
            DraftWorkflowException { Code: "DRAFT_ROW_NOT_FOUND" or "DRAFT_ROW_SESSION_MISMATCH" } draft =>
                ("这条记录已不在当前导入中，请点「刷新」", draft.Code),
            DraftWorkflowException draft => ("当前不能修改这批导入", draft.Code),
            ImportWorkflowException { Error: ImportUiError.InvalidData } =>
                ("这条记录已不在当前导入中，请点「刷新」", "ROW_MISSING"),
            ImportWorkflowException workflowError => ("导入功能暂不可用", workflowError.Error.ToString()),
            _ when error.GetType().Name == "SqliteException" => ("本机数据库正忙，请稍后再试", "SqliteException"),
            _ => ("请刷新后重试", error.GetType().Name),
        };
        return $"无法删除记录：{reason}。（{code}）";
    }

    public async Task UpdateLinkAsync(Guid rowId, string shareUrl, string? extractionCode, CancellationToken cancellationToken = default)
    {
        _ = await RunMutationAsync(
            token => workflow.UpdateLinkAsync(rowId, shareUrl, extractionCode, token),
            "预检结果已失效，需要重新预检或按未预检状态继续。",
            "修改失败，请检查链接与提取码。",
            cancellationToken);
    }

    public async Task SetExcludedAsync(Guid rowId, bool excluded, CancellationToken cancellationToken = default)
    {
        _ = await RunMutationAsync(
            token => workflow.SetExcludedAsync(rowId, excluded, token),
            excluded ? "已排除该记录。" : "已恢复该记录。",
            "无法更新记录，请刷新后重试。",
            cancellationToken);
    }

    public async Task<bool> SaveCurrentRowEditAsync(CancellationToken cancellationToken = default)
    {
        if (isClosed || externalCleanupBusy) return false;
        if (SelectedRow is not { } selectedRow) return true;
        var shareUrl = SelectedShareUrl;
        var extractionCode = NormalizeSubmittedExtractionCode(SelectedExtractionCode);
        var originalExtractionCode = string.IsNullOrEmpty(selectedRow.ExtractionCode)
            ? null
            : selectedRow.ExtractionCode;
        if (string.Equals(shareUrl, selectedRow.ShareUrl, StringComparison.Ordinal) &&
            string.Equals(extractionCode, originalExtractionCode, StringComparison.Ordinal))
        {
            return true;
        }
        return await RunMutationAsync(
            token => workflow.UpdateLinkAsync(
                selectedRow.RowId,
                shareUrl,
                extractionCode,
                token),
            "链接修改已保存，开始前会重新检查。",
            "修改失败，请检查链接与提取码。",
            cancellationToken);
    }

    private static string? NormalizeSubmittedExtractionCode(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private async Task SaveSelectedRowAsync(CancellationToken cancellationToken)
    {
        _ = await SaveCurrentRowEditAsync(cancellationToken);
    }

    private async Task ToggleSelectedExclusionAsync(CancellationToken cancellationToken)
    {
        if (SelectedRow is null) return;
        var exclude = !selectedRowIsExcluded;
        var applied = await RunMutationAsync(
            token => workflow.SetExcludedAsync(SelectedRow.RowId, exclude, token),
            exclude ? "已排除该记录。" : "已恢复该记录。",
            "无法更新记录，请刷新后重试。",
            cancellationToken);
        if (!applied) return;
        selectedRowIsExcluded = exclude;
        OnPropertyChanged(nameof(SelectedExclusionActionText));
    }

    private async Task<bool> RunMutationAsync(
        Func<CancellationToken, Task<ImportWorkflowResult>> mutation,
        string successMessage,
        string failureMessage,
        CancellationToken cancellationToken,
        bool stayOnPage = false,
        Func<Exception, string>? describeFailure = null)
    {
        if (isClosed || externalCleanupBusy) return false;
        using var activity = activityGate.TryEnterActivity();
        if (activity is null) return false;
        var generation = Interlocked.Increment(ref mutationGeneration);
        if (Interlocked.Increment(ref mutationRequests) == 1) IsBusy = true;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        var entered = false;
        try
        {
            await mutationGate.WaitAsync(linked.Token);
            entered = true;
            var result = await mutation(linked.Token);
            if (!IsCurrentMutation(generation, linked)) return false;
            StatusMessage = successMessage;
            await ApplyMutationAsync(result, generation, linked.Token, stayOnPage);
            return IsCurrentMutation(generation, linked);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { return false; }
        catch (Exception error)
        {
            if (IsCurrentMutation(generation, linked)) StatusMessage = describeFailure?.Invoke(error) ?? failureMessage;
            return false;
        }
        finally
        {
            if (entered) mutationGate.Release();
            if (Interlocked.Decrement(ref mutationRequests) == 0 && !isClosed) IsBusy = false;
        }
    }

    private async Task ApplyMutationAsync(
        ImportWorkflowResult result,
        long generation,
        CancellationToken cancellationToken,
        bool stayOnPage)
    {
        if (sessionId != result.SessionId)
            throw new InvalidOperationException("The import mutation returned a different session.");
        if (generation != Volatile.Read(ref mutationGeneration)) return;
        ApplyStatistics(result.Statistics);
        // 筛选时分页查询给出的是筛选后的条数，整批的总数以这次修改的统计为准。
        if (showConflictsOnly) TotalRows = result.Statistics.Total;
        DraftReady?.Invoke(this, new(result.SessionId, result.Statistics));
        if (stayOnPage)
        {
            await LoadPageAsync(PageMove.Reload, cancellationToken, generation);
            return;
        }
        nextCursor = null;
        await LoadPageAsync(PageMove.First, cancellationToken, generation);
    }

    private async Task RunImportAsync(
        Guid operationSessionId,
        bool isResume,
        Func<IProgress<ImportCommittedProgress>, CancellationToken, Task<ImportWorkflowResult>> action,
        CancellationToken externalCancellation)
    {
        if (isClosed || IsBusy) return;
        if (isResume) durableSessionObserved = true;
        operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, externalCancellation);
        IsBusy = true;
        StatusMessage = "正在流式导入，已保存的批次会持续写入本机数据库…";
        try
        {
            var progress = new UiProgress<ImportCommittedProgress>(
                value => ApplyCommittedProgress(operationSessionId, value));
            var result = await action(progress, operationCancellation.Token);
            if (isClosed) return;
            AttachSession(result.SessionId);
            ApplyStatistics(result.Statistics);
            CanResumeImport = false;
            StatusMessage = "导入完成，请分页检查异常、重复和账号映射。";
            DraftReady?.Invoke(this, new(result.SessionId, result.Statistics));
            await LoadFirstPageAsync(operationCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            if (isClosed) return;
            CanResumeImport = await HasResumableSourceAsync(operationSessionId);
            StatusMessage = CanResumeImport
                ? "导入已取消，已保存的进度可以继续。"
                : durableSessionObserved
                    ? "导入已取消，数据已保存，但没有可继续的解析进度。"
                    : "导入已取消，尚未保存可继续的数据。";
        }
        catch (ImportWorkflowException error)
        {
            if (isClosed) return;
            CanResumeImport = await HasResumableSourceAsync(operationSessionId);
            StatusMessage = CanResumeImport
                ? "导入中断，已保存的进度可以继续。"
                : SafeError(error.Error);
        }
        catch (Exception)
        {
            if (isClosed) return;
            CanResumeImport = await HasResumableSourceAsync(operationSessionId);
            StatusMessage = CanResumeImport
                ? "导入中断，已保存的进度可以继续。"
                : SafeError(ImportUiError.Unknown);
        }
        finally
        {
            operationCancellation.Dispose();
            operationCancellation = null;
            if (!isClosed) IsBusy = false;
        }
    }

    private enum PageMove { First, Next, Previous, Reload }

    private async Task LoadPageAsync(
        PageMove move,
        CancellationToken cancellationToken,
        long? expectedMutationGeneration = null)
    {
        if (isClosed || sessionId is null) return;
        if (move == PageMove.Previous && previousPages.Count == 0) move = PageMove.First;
        BeginPageRequest();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        var expectedSession = sessionId.Value;
        var generation = pageGeneration;
        var (cursor, requestedOrdinalOffset) = move switch
        {
            PageMove.Next => (nextCursor, displayOrdinalOffset + Rows.Count),
            PageMove.Previous => previousPages.Peek(),
            PageMove.Reload => (currentCursor, displayOrdinalOffset),
            _ => ((long?)null, 0L),
        };
        var emptiedPage = false;
        var conflictsOnly = showConflictsOnly;
        var conflictsGone = false;
        try
        {
            var page = await pageSource.LoadAsync(
                new(expectedSession, PageSize, cursor, conflictsOnly ? "Conflict" : null), linked.Token);
            if (conflictsOnly != showConflictsOnly) return;
            if (isClosed || linked.IsCancellationRequested || sessionId != expectedSession || generation != pageGeneration ||
                expectedMutationGeneration is { } mutation && mutation != Volatile.Read(ref mutationGeneration)) return;
            var selectedRowId = SelectedRow?.RowId;
            void Apply()
            {
                Rows.Clear();
                var ordinal = requestedOrdinalOffset;
                foreach (var item in page.Items)
                {
                    item.DisplayOrdinal = ++ordinal;
                    Rows.Add(item);
                }
                // 翻页记录只在这一页真的换上来之后才改，读取失败时仍停在原来那页。
                switch (move)
                {
                    case PageMove.First: previousPages.Clear(); break;
                    case PageMove.Next: previousPages.Push((currentCursor, displayOrdinalOffset)); break;
                    case PageMove.Previous: previousPages.Pop(); break;
                }
                currentCursor = cursor;
                displayOrdinalOffset = requestedOrdinalOffset;
                emptiedPage = move == PageMove.Reload && page.Items.Count == 0 && previousPages.Count > 0;
                SelectedRow = selectedRowId is { } rowId
                    ? Rows.FirstOrDefault(x => x.RowId == rowId)
                    : null;
                // 换页或删除后，多选只保留仍在这一页上的行；表格随后会再报一次真实的选中项。
                SetSelectedRows(selectedRows.Where(row => Rows.Contains(row)).ToArray());
                // 原来选中的行不在新的一页里时（例如刚被删除），表格控件内部仍记着它，之后再选别的行
                // 会被它顶回去，结果操作落在一条已经不存在的记录上。先选中一条真实的行再清空，
                // 表格才会真正放下旧行；只发一次「选中项为空」的通知不够。
                if (SelectedRow is null && selectedRowId is not null && Rows.Count > 0)
                {
                    SelectedRow = Rows[0];
                    SelectedRow = null;
                }
                listedRows = page.TotalCount;
                if (!conflictsOnly) TotalRows = page.TotalCount;
                conflictsGone = conflictsOnly && page.TotalCount == 0;
                nextCursor = page.NextSourceRowNumber;
                NotifyPagePosition();
                LoadNextPageCommand.NotifyCanExecuteChanged();
                LoadPreviousPageCommand.NotifyCanExecuteChanged();
            }
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) Apply();
            else await dispatcher.InvokeAsync(Apply);
            // 删掉的是最后一页的最后一行：这一页空了，退回上一页，不让用户对着空表。
            if (emptiedPage) await LoadPageAsync(PageMove.Previous, cancellationToken, expectedMutationGeneration);
            if (conflictsGone)
            {
                // 冲突都处理完了（或本来就没有）：不让用户对着空表，回到全部记录。
                ClearConflictFilter();
                StatusMessage = "没有冲突记录了，已显示全部记录。";
                await LoadPageAsync(PageMove.First, cancellationToken, expectedMutationGeneration);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception) { if (!isClosed) StatusMessage = "无法加载导入记录，请稍后重试。"; }
        finally { EndPageRequest(); }
    }

    private void ClearConflictFilter()
    {
        if (!showConflictsOnly) return;
        showConflictsOnly = false;
        OnPropertyChanged(nameof(ShowConflictsOnly));
    }

    private void NotifyPagePosition()
    {
        OnPropertyChanged(nameof(PageNumber));
        OnPropertyChanged(nameof(PageText));
    }

    private void BeginPageRequest()
    {
        if (Interlocked.Increment(ref pageRequests) != 1) return;
        OnPropertyChanged(nameof(CanEdit));
        RaiseCommands();
    }

    private void EndPageRequest()
    {
        if (Interlocked.Decrement(ref pageRequests) != 0 || isClosed) return;
        OnPropertyChanged(nameof(CanEdit));
        RaiseCommands();
    }

    private void Cancel() => operationCancellation?.Cancel();

    public void Close()
    {
        if (isClosed) return;
        isClosed = true;
        pageGeneration++;
        lifetime.Cancel();
        operationCancellation?.Cancel();
        CanResumeImport = false;
        IsBusy = false;
        OnPropertyChanged(nameof(IsClosed));
        OnPropertyChanged(nameof(CanEdit));
        RaiseCommands();
    }

    public void Dispose()
    {
        Close();
        operationCancellation?.Dispose();
        lifetime.Dispose();
    }

    private bool IsCurrentMutation(long generation, CancellationTokenSource operation) =>
        !isClosed && !operation.IsCancellationRequested && generation == Volatile.Read(ref mutationGeneration);

    private Task<bool> HasResumableSourceAsync(Guid operationSessionId) =>
        durableSessionObserved &&
        sessionId is { } persistedSessionId &&
        persistedSessionId == operationSessionId
            ? workflow.CanResumeAsync(persistedSessionId, CancellationToken.None)
            : Task.FromResult(false);

    private void BeginNewImport(bool discardPrevious = true)
    {
        if (discardPrevious && sessionId is { } previousSessionId)
            workflow.DiscardReplaySource(previousSessionId);
        durableSessionObserved = false;
        CanResumeImport = false;
        if (sessionId is null) return;
        sessionId = null;
        pageGeneration++;
        OnPropertyChanged(nameof(SessionId));
        RaiseCommands();
    }

    private void ApplyCommittedProgress(Guid operationSessionId, ImportCommittedProgress progress)
    {
        if (isClosed) return;
        if (progress.Kind == ImportProgressKind.SessionCreated)
            AttachSession(operationSessionId);
        StatusMessage = progress.Kind switch
        {
            ImportProgressKind.SessionCreated => "正在解析并保存数据",
            ImportProgressKind.ChunkCommitted => $"正在解析并保存数据，已保存 {progress.PersistedRows:N0} 行",
            _ => StatusMessage,
        };
    }

    private void RaiseCommands()
    {
        OnPropertyChanged(nameof(CanFilterConflicts));
        OnPropertyChanged(nameof(CanImport));
        OnPropertyChanged(nameof(CanImportText));
        ImportTextCommand.NotifyCanExecuteChanged();
        SelectAndImportExcelCommand.NotifyCanExecuteChanged();
        ResumeImportCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        LoadFirstPageCommand.NotifyCanExecuteChanged();
        LoadNextPageCommand.NotifyCanExecuteChanged();
        LoadPreviousPageCommand.NotifyCanExecuteChanged();
        SaveSelectedRowCommand.NotifyCanExecuteChanged();
        ToggleSelectedExclusionCommand.NotifyCanExecuteChanged();
        DeleteSelectedRowCommand.NotifyCanExecuteChanged();
    }

    private static string SafeError(ImportUiError error) => error switch
    {
        ImportUiError.InvalidWorkbook => "Excel 文件无法读取，请确认文件未损坏且格式为 .xlsx。",
        ImportUiError.UnsafeWorkbook => "Excel 文件包含异常内容，为保护本机已停止导入。",
        ImportUiError.MissingHeader => "Excel 缺少“分享链接”列，请使用软件提供的模板。",
        ImportUiError.InvalidData => "导入内容有误，请修改异常记录后重试。",
        ImportUiError.AdapterUnavailable => "百度网盘功能暂不可用，当前版本不能开始导入或转存。",
        _ => "导入失败，请检查文件后重试。",
    };

    private sealed class AvailableAdapterAvailability : IAdapterAvailability
    {
        public static AvailableAdapterAvailability Instance { get; } = new();
        public bool CanStartTransfers => true;
        public string StatusMessage => string.Empty;
    }

    private sealed class UiProgress<T>(Action<T> handler) : IProgress<T>
    {
        private readonly System.Windows.Threading.Dispatcher? dispatcher =
            System.Windows.Application.Current?.Dispatcher;
        private readonly SynchronizationContext? synchronizationContext = SynchronizationContext.Current;

        public void Report(T value)
        {
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(() => handler(value));
                return;
            }

            if (synchronizationContext is not null &&
                !ReferenceEquals(SynchronizationContext.Current, synchronizationContext))
            {
                synchronizationContext.Send(_ => handler(value), null);
                return;
            }

            handler(value);
        }
    }
}
