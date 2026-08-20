using System.Collections.ObjectModel;
using BaiduBatchTransfer.App.Features.Jobs;
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
    int UnresolvedAccountMappings = 0);
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
    bool HasResumableReplaySource(Guid sessionId) => true;
    void DiscardReplaySource(Guid sessionId) { }
    Task UpdateLinkAsync(Guid rowId, string shareUrl, string? extractionCode, CancellationToken cancellationToken);
    Task SetExcludedAsync(Guid rowId, bool excluded, CancellationToken cancellationToken);
}

public sealed class UnavailableImportWorkflow : IImportWorkflow
{
    public bool IsAvailable => false;
    public string StatusMessage => "本地导入流程尚不可用。";
    private static ImportWorkflowException Error() => new(ImportUiError.AdapterUnavailable);
    public Task<ImportWorkflowResult> ImportTextAsync(Guid sessionId, string text, CancellationToken cancellationToken) => Task.FromException<ImportWorkflowResult>(Error());
    public Task<ImportWorkflowResult> ImportExcelAsync(Guid sessionId, string filePath, CancellationToken cancellationToken) => Task.FromException<ImportWorkflowResult>(Error());
    public Task<ImportWorkflowResult> ResumeAsync(Guid sessionId, CancellationToken cancellationToken) => Task.FromException<ImportWorkflowResult>(Error());
    public Task UpdateLinkAsync(Guid rowId, string shareUrl, string? extractionCode, CancellationToken cancellationToken) => Task.FromException(Error());
    public Task SetExcludedAsync(Guid rowId, bool excluded, CancellationToken cancellationToken) => Task.FromException(Error());
}

public sealed class ImportViewModel : ObservableObject
{
    private const int PageSize = 100;
    private readonly IImportWorkflow workflow;
    private readonly IImportPreviewPageSource pageSource;
    private readonly IDialogService dialogService;
    private readonly IAdapterAvailability adapterAvailability;
    private CancellationTokenSource? operationCancellation;
    private Guid? sessionId;
    private string textContent = string.Empty;
    private string statusMessage = "粘贴百度分享链接或选择 Excel 文件开始导入。";
    private string statisticsText = "尚未导入数据";
    private bool isBusy;
    private bool canResumeImport;
    private bool durableSessionObserved;
    private long totalRows;
    private long? nextCursor;
    private ImportPreviewRow? selectedRow;
    private string selectedShareUrl = string.Empty;
    private string selectedExtractionCode = string.Empty;
    private bool selectedRowIsExcluded;
    private long pageGeneration;

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
        ResumeImportCommand = new AsyncRelayCommand(ResumeImportAsync, () => CanResumeImport && !IsBusy);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        LoadFirstPageCommand = new AsyncRelayCommand(LoadFirstPageAsync, () => sessionId is not null && !IsBusy);
        LoadNextPageCommand = new AsyncRelayCommand(LoadNextPageAsync, () => nextCursor is not null && !IsBusy);
        SaveSelectedRowCommand = new AsyncRelayCommand(SaveSelectedRowAsync, () => SelectedRow is not null && !IsBusy);
        ToggleSelectedExclusionCommand = new AsyncRelayCommand(ToggleSelectedExclusionAsync, () => SelectedRow is not null && !IsBusy);
    }

    public ObservableCollection<ImportPreviewRow> Rows { get; }
    public event EventHandler<ImportDraftReady>? DraftReady;
    public IAsyncRelayCommand ImportTextCommand { get; }
    public IAsyncRelayCommand SelectAndImportExcelCommand { get; }
    public IAsyncRelayCommand ResumeImportCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand LoadFirstPageCommand { get; }
    public IAsyncRelayCommand LoadNextPageCommand { get; }
    public IAsyncRelayCommand SaveSelectedRowCommand { get; }
    public IAsyncRelayCommand ToggleSelectedExclusionCommand { get; }

    public string TextContent { get => textContent; set { if (SetProperty(ref textContent, value ?? string.Empty)) RaiseCommands(); } }
    public string StatusMessage { get => statusMessage; private set => SetProperty(ref statusMessage, value); }
    public string StatisticsText { get => statisticsText; private set => SetProperty(ref statisticsText, value); }
    public bool IsBusy { get => isBusy; private set { if (SetProperty(ref isBusy, value)) RaiseCommands(); } }
    public bool CanResumeImport { get => canResumeImport; private set { if (SetProperty(ref canResumeImport, value)) RaiseCommands(); } }
    public bool CanImport => workflow.IsAvailable && !IsBusy;
    public bool CanImportText => CanImport && !string.IsNullOrWhiteSpace(TextContent);
    public string AdapterStatusMessage => !workflow.IsAvailable
        ? workflow.StatusMessage
        : adapterAvailability.CanStartTransfers
            ? string.Empty
            : "可以先导入并整理草稿；实际转存功能仍在验证中。";
    public long TotalRows { get => totalRows; private set => SetProperty(ref totalRows, value); }
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
        }
    }
    public string SelectedShareUrl { get => selectedShareUrl; set => SetProperty(ref selectedShareUrl, value ?? string.Empty); }
    public string SelectedExtractionCode { get => selectedExtractionCode; set => SetProperty(ref selectedExtractionCode, value ?? string.Empty); }
    public string SelectedExclusionActionText => selectedRowIsExcluded ? "恢复所选记录" : "排除所选记录";

    public void AttachSession(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("导入会话不能为空。", nameof(value));
        durableSessionObserved = true;
        sessionId = value;
        pageGeneration++;
        OnPropertyChanged(nameof(SessionId));
        RaiseCommands();
    }

    public void ApplyStatistics(ImportDraftStatistics statistics)
    {
        StatisticsText = $"总计 {statistics.Total:N0} · 就绪 {statistics.Ready:N0} · 重复 {statistics.Duplicate:N0} · 异常 {statistics.Invalid:N0} · 已排除 {statistics.Excluded:N0} · 冲突 {statistics.Conflict:N0} · 待解析 {statistics.NeedsResolution:N0}";
    }

    public async Task ImportTextAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy) return;
        var newSessionId = Guid.NewGuid();
        BeginNewImport();
        await RunImportAsync(
            newSessionId,
            isResume: false,
            (progress, token) => workflow.ImportTextAsync(newSessionId, TextContent, progress, token),
            cancellationToken);
    }

    public async Task ImportExcelAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || IsBusy) return;
        var newSessionId = Guid.NewGuid();
        BeginNewImport();
        await RunImportAsync(
            newSessionId,
            isResume: false,
            (progress, token) => workflow.ImportExcelAsync(newSessionId, filePath, progress, token),
            cancellationToken);
    }

    public async Task SelectAndImportExcelAsync(CancellationToken cancellationToken = default)
    {
        StatusMessage = "请选择 Excel 文件";
        var path = dialogService.PickExcelFile();
        if (path is null)
        {
            StatusMessage = "未选择文件";
            return;
        }

        StatusMessage = "已选择 Excel 文件，正在检查";
        await ImportExcelAsync(path, cancellationToken);
    }

    public async Task ResumeImportAsync(CancellationToken cancellationToken = default)
    {
        if (sessionId is null || IsBusy) return;
        await RunImportAsync(
            sessionId.Value,
            isResume: true,
            (progress, token) => workflow.ResumeAsync(sessionId.Value, progress, token),
            cancellationToken);
    }

    public async Task LoadFirstPageAsync(CancellationToken cancellationToken = default)
    {
        nextCursor = null;
        await LoadPageAsync(null, cancellationToken);
    }

    public async Task LoadNextPageAsync(CancellationToken cancellationToken = default)
    {
        if (nextCursor is null) return;
        await LoadPageAsync(nextCursor, cancellationToken);
    }

    public async Task UpdateLinkAsync(Guid rowId, string shareUrl, string? extractionCode, CancellationToken cancellationToken = default)
    {
        try
        {
            await workflow.UpdateLinkAsync(rowId, shareUrl, extractionCode, cancellationToken);
            StatusMessage = "预检结果已失效，需要重新预检或按未预检状态继续。";
            await LoadFirstPageAsync(cancellationToken);
        }
        catch (Exception) { StatusMessage = "修改失败，请检查链接与提取码。"; }
    }

    public async Task SetExcludedAsync(Guid rowId, bool excluded, CancellationToken cancellationToken = default)
    {
        try
        {
            await workflow.SetExcludedAsync(rowId, excluded, cancellationToken);
            StatusMessage = excluded ? "已排除该记录。" : "已恢复该记录。";
        }
        catch (Exception) { StatusMessage = "无法更新记录，请刷新后重试。"; }
    }

    private async Task SaveSelectedRowAsync(CancellationToken cancellationToken)
    {
        if (SelectedRow is null) return;
        await UpdateLinkAsync(
            SelectedRow.RowId,
            SelectedShareUrl,
            string.IsNullOrWhiteSpace(SelectedExtractionCode) ? null : SelectedExtractionCode,
            cancellationToken);
    }

    private async Task ToggleSelectedExclusionAsync(CancellationToken cancellationToken)
    {
        if (SelectedRow is null) return;
        var exclude = !selectedRowIsExcluded;
        await SetExcludedAsync(SelectedRow.RowId, exclude, cancellationToken);
        selectedRowIsExcluded = exclude;
        OnPropertyChanged(nameof(SelectedExclusionActionText));
    }

    private async Task RunImportAsync(
        Guid operationSessionId,
        bool isResume,
        Func<IProgress<ImportCommittedProgress>, CancellationToken, Task<ImportWorkflowResult>> action,
        CancellationToken externalCancellation)
    {
        if (IsBusy) return;
        if (isResume) durableSessionObserved = true;
        operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
        IsBusy = true;
        StatusMessage = "正在流式导入，已保存的批次会持续写入本机数据库…";
        try
        {
            var progress = new UiProgress<ImportCommittedProgress>(
                value => ApplyCommittedProgress(operationSessionId, value));
            var result = await action(progress, operationCancellation.Token);
            AttachSession(result.SessionId);
            ApplyStatistics(result.Statistics);
            CanResumeImport = false;
            StatusMessage = "导入完成，请分页检查异常、重复和账号映射。";
            DraftReady?.Invoke(this, new(result.SessionId, result.Statistics));
            await LoadFirstPageAsync(externalCancellation);
        }
        catch (OperationCanceledException)
        {
            CanResumeImport = HasResumableReplaySource();
            StatusMessage = CanResumeImport
                ? "导入已取消，已保存的进度可以继续。"
                : durableSessionObserved
                    ? "导入已取消，草稿已保存，但没有可继续的解析进度。"
                    : "导入已取消，尚未保存可继续的草稿。";
        }
        catch (ImportWorkflowException error)
        {
            CanResumeImport = HasResumableReplaySource();
            StatusMessage = CanResumeImport
                ? "导入中断，已保存的进度可以继续。"
                : SafeError(error.Error);
        }
        catch (Exception)
        {
            CanResumeImport = HasResumableReplaySource();
            StatusMessage = CanResumeImport
                ? "导入中断，已保存的进度可以继续。"
                : SafeError(ImportUiError.Unknown);
        }
        finally
        {
            operationCancellation.Dispose();
            operationCancellation = null;
            IsBusy = false;
        }
    }

    private async Task LoadPageAsync(long? cursor, CancellationToken cancellationToken)
    {
        if (sessionId is null) return;
        var expectedSession = sessionId.Value;
        var generation = pageGeneration;
        try
        {
            var page = await pageSource.LoadAsync(new(expectedSession, PageSize, cursor), cancellationToken);
            if (sessionId != expectedSession || generation != pageGeneration) return;
            var selectedRowId = SelectedRow?.RowId;
            void Apply()
            {
                Rows.Clear();
                foreach (var item in page.Items) Rows.Add(item);
                SelectedRow = selectedRowId is { } rowId
                    ? Rows.FirstOrDefault(x => x.RowId == rowId)
                    : null;
                TotalRows = page.TotalCount;
                nextCursor = page.NextSourceRowNumber;
                LoadNextPageCommand.NotifyCanExecuteChanged();
            }
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) Apply();
            else await dispatcher.InvokeAsync(Apply);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception) { StatusMessage = "无法加载导入记录，请稍后重试。"; }
    }

    private void Cancel() => operationCancellation?.Cancel();

    private bool HasResumableReplaySource() =>
        durableSessionObserved &&
        sessionId is { } persistedSessionId &&
        workflow.HasResumableReplaySource(persistedSessionId);

    private void BeginNewImport()
    {
        if (sessionId is { } previousSessionId)
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
        if (progress.Kind == ImportProgressKind.SessionCreated)
            AttachSession(operationSessionId);
        StatusMessage = progress.Kind switch
        {
            ImportProgressKind.SessionCreated => "正在解析并写入草稿",
            ImportProgressKind.ChunkCommitted => $"正在解析并写入草稿，已保存 {progress.PersistedRows:N0} 行",
            _ => StatusMessage,
        };
    }

    private void RaiseCommands()
    {
        OnPropertyChanged(nameof(CanImport));
        OnPropertyChanged(nameof(CanImportText));
        ImportTextCommand.NotifyCanExecuteChanged();
        SelectAndImportExcelCommand.NotifyCanExecuteChanged();
        ResumeImportCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        LoadFirstPageCommand.NotifyCanExecuteChanged();
        LoadNextPageCommand.NotifyCanExecuteChanged();
        SaveSelectedRowCommand.NotifyCanExecuteChanged();
        ToggleSelectedExclusionCommand.NotifyCanExecuteChanged();
    }

    private static string SafeError(ImportUiError error) => error switch
    {
        ImportUiError.InvalidWorkbook => "Excel 文件无法读取，请确认文件未损坏且格式为 .xlsx。",
        ImportUiError.UnsafeWorkbook => "Excel 文件包含异常内容，为保护本机已停止导入。",
        ImportUiError.MissingHeader => "Excel 缺少“分享链接”列，请使用软件提供的模板。",
        ImportUiError.InvalidData => "导入内容有误，请修改异常记录后重试。",
        ImportUiError.AdapterUnavailable => "百度适配器尚不可用，当前版本不能开始导入或转存。",
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
