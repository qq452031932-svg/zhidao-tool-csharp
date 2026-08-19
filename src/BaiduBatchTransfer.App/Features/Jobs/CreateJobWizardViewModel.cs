using System.Collections.ObjectModel;
using System.ComponentModel;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core = BaiduBatchTransfer.Core;

namespace BaiduBatchTransfer.App.Features.Jobs;

public sealed class CreateJobWizardViewModel : ObservableObject
{
    private const int PreviewPageSize = 100;
    private readonly IImportPreviewPageSource pageSource;
    private readonly IWizardBatchService batchService;
    private readonly IDialogService dialogService;
    private readonly IAdapterAvailability adapterAvailability;
    private readonly List<WizardAccountOption> options;
    private Guid importSessionId;
    private Guid batchJobId;
    private string jobName = "新建批量转存任务";
    private int readyResourceCount;
    private bool hasExcelAccountColumn;
    private int unresolvedAccountMappingCount;
    private int conflictRowCount;
    private int needsResolutionRowCount;
    private string defaultTargetPath = "/";
    private TransferMode? transferMode;
    private QuotaBasis quotaBasis = QuotaBasis.SuccessTarget;
    private ShortagePolicy shortagePolicy = ShortagePolicy.ProportionalReduction;
    private ExcelAccountHandling excelAccountHandling;
    private bool useBulkPrecheck;
    private Guid? precheckAccountId;
    private bool isFinalConfirmationChecked;
    private string? confirmationToken;
    private string validationMessage = string.Empty;
    private string statusMessage = string.Empty;
    private long previewTotalCount;
    private long? nextPreviewCursor;
    private int currentStepIndex;
    private bool isBusy;
    private bool suppressInvalidation;

    public CreateJobWizardViewModel(
        IReadOnlyList<WizardAccountOption> accounts,
        IImportPreviewPageSource pageSource,
        IWizardBatchService batchService,
        IDialogService dialogService,
        IAdapterAvailability? adapterAvailability = null)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        this.pageSource = pageSource ?? throw new ArgumentNullException(nameof(pageSource));
        this.batchService = batchService ?? throw new ArgumentNullException(nameof(batchService));
        this.dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        this.adapterAvailability = adapterAvailability ?? AvailableAdapterAvailability.Instance;
        options = accounts.Where(IsEligible).OrderBy(x => x.DisplayName, StringComparer.CurrentCulture).ToList();
        Accounts = new(options.Select(option => new AccountAllocationRowViewModel(option, defaultTargetPath)));
        foreach (var account in Accounts) account.PropertyChanged += AccountChanged;
        Steps = new([
            new(WizardStepKind.Import, "导入数据") { IsCurrent = true },
            new(WizardStepKind.Precheck, "批量预检") { IsSkipped = true },
            new(WizardStepKind.Destination, "选择目录"),
            new(WizardStepKind.Allocation, "分配账号"),
            new(WizardStepKind.Confirmation, "确认任务"),
        ]);
        PreviewRows = new();
        PrepareConfirmationCommand = new AsyncRelayCommand(PrepareConfirmationAsync, () => !IsBusy);
        CreateJobCommand = new AsyncRelayCommand(CreateJobAsync, () => CanCreateJob && !IsBusy);
        LoadFirstPreviewPageCommand = new AsyncRelayCommand(LoadFirstPreviewPageAsync, () => !IsBusy);
        LoadNextPreviewPageCommand = new AsyncRelayCommand(LoadNextPreviewPageAsync, () => nextPreviewCursor is not null && !IsBusy);
        NextStepCommand = new RelayCommand(MoveNext, () => CanContinue);
        PreviousStepCommand = new RelayCommand(MovePrevious, () => currentStepIndex > 0);
        SelectReplicationCommand = new RelayCommand(() => SelectMode(Core.Domain.TransferMode.Replication));
        SelectDistributionCommand = new RelayCommand(() => SelectMode(Core.Domain.TransferMode.Distribution));
        UseTaskQuotaCommand = new RelayCommand(() => QuotaBasis = Core.Domain.QuotaBasis.TaskCount);
        UseSuccessTargetCommand = new RelayCommand(() => QuotaBasis = Core.Domain.QuotaBasis.SuccessTarget);
        UseProportionalReductionCommand = new RelayCommand(() => ShortagePolicy = Core.Domain.ShortagePolicy.ProportionalReduction);
        UseAccountPriorityCommand = new RelayCommand(() => ShortagePolicy = Core.Domain.ShortagePolicy.AccountPriority);
        UseWaitForCandidatesCommand = new RelayCommand(() => ShortagePolicy = Core.Domain.ShortagePolicy.WaitForCandidates);
        KeepExcelMappingsCommand = new RelayCommand(() => ExcelAccountHandling = ExcelAccountHandling.KeepMappings);
        OverrideExcelMappingsCommand = new RelayCommand(() => ExcelAccountHandling = ExcelAccountHandling.OverrideAll);
        IgnoreExcelAccountColumnCommand = new RelayCommand(() => ExcelAccountHandling = ExcelAccountHandling.IgnoreColumn);
        ApplyOneTwoOneCommand = new RelayCommand(() => TryApplyOneTwoOne());
    }

    public ObservableCollection<AccountAllocationRowViewModel> Accounts { get; }
    public ObservableCollection<WizardStepItem> Steps { get; }
    public ObservableCollection<ImportPreviewRow> PreviewRows { get; }
    public IAsyncRelayCommand PrepareConfirmationCommand { get; }
    public IAsyncRelayCommand CreateJobCommand { get; }
    public IAsyncRelayCommand LoadFirstPreviewPageCommand { get; }
    public IAsyncRelayCommand LoadNextPreviewPageCommand { get; }
    public IRelayCommand NextStepCommand { get; }
    public IRelayCommand PreviousStepCommand { get; }
    public IRelayCommand SelectReplicationCommand { get; }
    public IRelayCommand SelectDistributionCommand { get; }
    public IRelayCommand UseTaskQuotaCommand { get; }
    public IRelayCommand UseSuccessTargetCommand { get; }
    public IRelayCommand UseProportionalReductionCommand { get; }
    public IRelayCommand UseAccountPriorityCommand { get; }
    public IRelayCommand UseWaitForCandidatesCommand { get; }
    public IRelayCommand KeepExcelMappingsCommand { get; }
    public IRelayCommand OverrideExcelMappingsCommand { get; }
    public IRelayCommand IgnoreExcelAccountColumnCommand { get; }
    public IRelayCommand ApplyOneTwoOneCommand { get; }

    public string JobName { get => jobName; set => SetAndInvalidate(ref jobName, value ?? string.Empty); }
    public string DefaultTargetPath
    {
        get => defaultTargetPath;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
            if (!SetAndInvalidate(ref defaultTargetPath, normalized)) return;
            foreach (var account in Accounts.Where(x => string.IsNullOrWhiteSpace(x.DefaultTargetPath) || x.DefaultTargetPath == "/"))
                account.DefaultTargetPath = normalized;
        }
    }
    public TransferMode? TransferMode => transferMode;
    public QuotaBasis QuotaBasis { get => quotaBasis; set => SetAndInvalidate(ref quotaBasis, value); }
    public ShortagePolicy ShortagePolicy { get => shortagePolicy; set => SetAndInvalidate(ref shortagePolicy, value); }
    public ExcelAccountHandling ExcelAccountHandling { get => excelAccountHandling; set => SetAndInvalidate(ref excelAccountHandling, value); }
    public bool UseBulkPrecheck
    {
        get => useBulkPrecheck;
        set
        {
            if (!SetAndInvalidate(ref useBulkPrecheck, value)) return;
            Steps.Single(x => x.Kind == WizardStepKind.Precheck).IsSkipped = !value;
            if (value && precheckAccountId is null) PrecheckAccountId = Accounts.FirstOrDefault(x => x.IsSelected)?.AccountId ?? Accounts.FirstOrDefault()?.AccountId;
            OnPropertyChanged(nameof(ValidationModeText));
        }
    }
    public Guid? PrecheckAccountId { get => precheckAccountId; set => SetAndInvalidate(ref precheckAccountId, value); }
    public bool IsFinalConfirmationChecked
    {
        get => isFinalConfirmationChecked;
        set
        {
            if (!SetProperty(ref isFinalConfirmationChecked, value)) return;
            RaiseDerived();
        }
    }
    public string? ConfirmationToken { get => confirmationToken; private set => SetProperty(ref confirmationToken, value); }
    public string ValidationMessage { get => validationMessage; private set => SetProperty(ref validationMessage, value); }
    public string StatusMessage { get => statusMessage; private set => SetProperty(ref statusMessage, value); }
    public bool IsBusy { get => isBusy; private set { if (SetProperty(ref isBusy, value)) RaiseDerived(); } }
    public int ReadyResourceCount => readyResourceCount;
    public long ResourceCount => readyResourceCount;
    public long OperationCount => transferMode == Core.Domain.TransferMode.Replication
        ? checked((long)readyResourceCount * Accounts.Count(x => x.IsSelected))
        : readyResourceCount;
    public long AssignedCount => Accounts.Where(x => x.IsSelected).Sum(x => (long)x.TargetItemCount);
    public long UnassignedCount => Math.Max(0, readyResourceCount - AssignedCount);
    public long SuccessTargetCount => Accounts.Where(x => x.IsSelected).Sum(x => (long)x.TargetSuccessCount);
    public long ReserveCandidateCount => Math.Max(0, readyResourceCount - SuccessTargetCount);
    public long PreviewTotalCount { get => previewTotalCount; private set => SetProperty(ref previewTotalCount, value); }
    public int CurrentStepIndex { get => currentStepIndex; private set => SetProperty(ref currentStepIndex, value); }
    public string AccountSelectionNotice { get; private set; } = string.Empty;
    public string ValidationModeText => UseBulkPrecheck ? "批量预检" : "执行前即时检测";
    public string AdapterStatusMessage => adapterAvailability.StatusMessage;
    public string WarningMessage => BuildWarning();
    public bool CanContinue => string.IsNullOrEmpty(EvaluateValidation());
    public bool CanCreateJob => adapterAvailability.CanStartTransfers && ConfirmationToken is not null &&
        IsFinalConfirmationChecked && CanContinue && !IsBusy;

    public void Initialize(ImportWizardContext context)
    {
        if (context.ImportSessionId == Guid.Empty) throw new ArgumentException("导入会话不能为空。", nameof(context));
        suppressInvalidation = true;
        importSessionId = context.ImportSessionId;
        batchJobId = Guid.NewGuid();
        jobName = context.JobName;
        readyResourceCount = Math.Max(0, context.ReadyResourceCount);
        hasExcelAccountColumn = context.HasExcelAccountColumn;
        unresolvedAccountMappingCount = Math.Max(0, context.UnresolvedAccountMappingCount);
        conflictRowCount = Math.Max(0, context.ConflictRowCount);
        needsResolutionRowCount = Math.Max(0, context.NeedsResolutionRowCount);
        defaultTargetPath = context.DefaultTargetPath;
        excelAccountHandling = hasExcelAccountColumn ? ExcelAccountHandling.Unresolved : ExcelAccountHandling.OverrideAll;
        foreach (var account in Accounts)
        {
            account.DefaultTargetPath = defaultTargetPath;
            account.IsSelected = false;
            account.TargetItemCount = 0;
            account.TargetSuccessCount = account.OriginalTargetSuccessCount;
        }
        if (Accounts.Count == 1)
        {
            transferMode = Core.Domain.TransferMode.Distribution;
            Accounts[0].IsSelected = true;
            Accounts[0].TargetItemCount = readyResourceCount;
            Accounts[0].TargetSuccessCount = Accounts[0].OriginalTargetSuccessCount > 0
                ? Accounts[0].OriginalTargetSuccessCount
                : readyResourceCount;
            AccountSelectionNotice = $"已自动选择唯一可用账号：{Accounts[0].DisplayName}，请在确认页核对。";
        }
        else
        {
            transferMode = null;
            AccountSelectionNotice = "请选择多账号复制或多账号分流。";
        }
        suppressInvalidation = false;
        InvalidateConfirmation();
        RaiseAll();
    }

    public void SelectMode(TransferMode mode)
    {
        if (transferMode == mode) return;
        transferMode = mode;
        if (mode == Core.Domain.TransferMode.Replication) quotaBasis = Core.Domain.QuotaBasis.TaskCount;
        InvalidateConfirmation();
        OnPropertyChanged(nameof(TransferMode));
        RaiseAll();
    }

    public void SetImportResolutionCounts(int conflictRows, int needsResolutionRows)
    {
        conflictRowCount = Math.Max(0, conflictRows);
        needsResolutionRowCount = Math.Max(0, needsResolutionRows);
        InvalidateConfirmation();
        RaiseAll();
    }

    public bool TryApplyOneTwoOne()
    {
        var selected = Accounts.Where(x => x.IsSelected).ToArray();
        if (selected.Length != 3 || transferMode != Core.Domain.TransferMode.Distribution) return false;
        suppressInvalidation = true;
        try
        {
            var first = readyResourceCount / 4;
            var second = readyResourceCount / 2;
            var third = readyResourceCount - first - second;
            SetTarget(selected[0], first);
            SetTarget(selected[1], second);
            SetTarget(selected[2], third);
        }
        finally { suppressInvalidation = false; }
        InvalidateConfirmation();
        RaiseAll();
        return true;
    }

    public async Task LoadFirstPreviewPageAsync(CancellationToken cancellationToken = default)
    {
        nextPreviewCursor = null;
        await LoadPreviewPageAsync(null, replace: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task LoadNextPreviewPageAsync(CancellationToken cancellationToken = default)
    {
        if (nextPreviewCursor is null) return;
        await LoadPreviewPageAsync(nextPreviewCursor, replace: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task PrepareConfirmationAsync(CancellationToken cancellationToken = default)
    {
        var validation = EvaluateValidation();
        ValidationMessage = validation;
        if (validation.Length != 0) return;
        IsBusy = true;
        try
        {
            var draft = BuildDraft();
            var summary = await batchService.PreviewAsync(draft, cancellationToken).ConfigureAwait(false);
            if (!summary.HasSufficientDiskSpace)
                throw new StorageCapacityException(0, 0);
            ConfirmationToken = summary.ConfirmationToken;
            StatusMessage = $"预览完成：{summary.ResourceCount:N0} 个资源，{summary.OperationCount:N0} 次转存操作。";
            IsFinalConfirmationChecked = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "预览已取消。";
        }
        catch (StorageCapacityException)
        {
            InvalidateConfirmation();
            ValidationMessage = "本机磁盘空间不足，无法安全创建任务。请释放空间后重新预览。";
        }
        catch (BatchDraftValidationException)
        {
            InvalidateConfirmation();
            ValidationMessage = "任务配置或导入草稿已变化，请返回检查并重新预览。";
        }
        catch (Exception)
        {
            InvalidateConfirmation();
            ValidationMessage = "任务预览失败，请检查配置后重试。";
        }
        finally { IsBusy = false; }
    }

    public async Task CreateJobAsync(CancellationToken cancellationToken = default)
    {
        if (!CanCreateJob || ConfirmationToken is null) return;
        var token = ConfirmationToken;
        var draft = BuildDraft();
        var confirmed = await dialogService.ConfirmAsync(
            "最终确认",
            $"将创建任务“{JobName}”，包含 {ResourceCount:N0} 个资源、{OperationCount:N0} 次转存操作。确认继续吗？",
            cancellationToken).ConfigureAwait(false);
        if (!confirmed) return;
        IsBusy = true;
        try
        {
            await batchService.ConfirmAsync(draft, token, cancellationToken).ConfigureAwait(false);
            ConfirmationToken = null;
            IsFinalConfirmationChecked = false;
            StatusMessage = "任务已创建，正在等待调度。";
        }
        catch (BatchConfirmationException)
        {
            InvalidateConfirmation();
            ValidationMessage = "导入草稿或任务设置已变化，请重新预览并确认。";
        }
        catch (StorageCapacityException)
        {
            InvalidateConfirmation();
            ValidationMessage = "本机磁盘空间不足，任务没有创建。";
        }
        catch (Exception)
        {
            InvalidateConfirmation();
            ValidationMessage = "任务创建失败，未写入执行队列，请重试。";
        }
        finally { IsBusy = false; }
    }

    private async Task LoadPreviewPageAsync(long? cursor, bool replace, CancellationToken cancellationToken)
    {
        if (importSessionId == Guid.Empty) return;
        IsBusy = true;
        try
        {
            var page = await pageSource.LoadAsync(new(importSessionId, PreviewPageSize, cursor), cancellationToken)
                .ConfigureAwait(false);
            void ApplyPage()
            {
                if (replace) PreviewRows.Clear();
                foreach (var row in page.Items) PreviewRows.Add(row);
                PreviewTotalCount = page.TotalCount;
                nextPreviewCursor = page.NextSourceRowNumber;
                LoadNextPreviewPageCommand.NotifyCanExecuteChanged();
            }
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) ApplyPage();
            else await dispatcher.InvokeAsync(ApplyPage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception) { StatusMessage = "无法加载预览页，请稍后重试。"; }
        finally { IsBusy = false; }
    }

    private WizardBatchDraft BuildDraft()
    {
        var mode = transferMode ?? throw new InvalidOperationException("尚未选择转存模式。");
        var selected = Accounts.Where(x => x.IsSelected).ToArray();
        var accountDrafts = selected.Select((account, index) => new WizardAccountDraft(
            account.AccountId,
            index,
            account.DefaultTargetPath,
            account.TargetItemCount,
            account.TargetSuccessCount)).ToArray();
        return new(
            batchJobId,
            importSessionId,
            JobName,
            mode,
            mode == Core.Domain.TransferMode.Replication ? Core.Domain.QuotaBasis.TaskCount : quotaBasis,
            shortagePolicy,
            accountDrafts,
            DefaultTargetPath,
            "None",
            DuplicatePolicy.Skip,
            UseBulkPrecheck ? "BatchPrecheck" : "Immediate",
            mode == Core.Domain.TransferMode.Replication && ExcelAccountHandling == ExcelAccountHandling.IgnoreColumn,
            readyResourceCount);
    }

    private string EvaluateValidation()
    {
        if (importSessionId == Guid.Empty) return "请先完成数据导入。";
        var unresolved = conflictRowCount + needsResolutionRowCount;
        if (unresolved > 0) return $"仍有 {unresolved:N0} 条冲突或待处理记录，请先解决。";
        if (readyResourceCount <= 0) return "没有可加入任务的就绪资源。";
        if (Accounts.Count == 0) return "没有可用账号，请先完成账号登录。";
        if (transferMode is null) return "多个账号时必须选择多账号复制或多账号分流。";
        var selected = Accounts.Where(x => x.IsSelected).ToArray();
        if (selected.Length == 0) return "请至少选择一个参与账号。";
        if (string.IsNullOrWhiteSpace(JobName)) return "请输入任务名称。";
        if (string.IsNullOrWhiteSpace(DefaultTargetPath)) return "请选择默认目标目录。";
        if (UseBulkPrecheck && (PrecheckAccountId is null || selected.All(x => x.AccountId != PrecheckAccountId)))
            return "请选择一个本批次参与的可用账号作为预检账号。";
        if (hasExcelAccountColumn)
        {
            if (transferMode == Core.Domain.TransferMode.Replication && ExcelAccountHandling != ExcelAccountHandling.IgnoreColumn)
                return "Excel 含逐行指定账号。复制模式只能明确忽略该列，或切换为分流模式。";
            if (transferMode == Core.Domain.TransferMode.Distribution && ExcelAccountHandling is ExcelAccountHandling.Unresolved or ExcelAccountHandling.IgnoreColumn)
                return "请选择保留、映射或覆盖 Excel 逐行指定账号。";
            if (transferMode == Core.Domain.TransferMode.Distribution &&
                ExcelAccountHandling is ExcelAccountHandling.KeepMappings or ExcelAccountHandling.MapNames &&
                unresolvedAccountMappingCount > 0)
                return $"Excel 中有 {unresolvedAccountMappingCount:N0} 个账号名称尚未映射，请完成映射或覆盖全部记录。";
        }
        if (selected.Any(x => string.IsNullOrWhiteSpace(x.DefaultTargetPath))) return "每个参与账号都必须设置目标目录。";
        if (transferMode == Core.Domain.TransferMode.Distribution && quotaBasis == Core.Domain.QuotaBasis.TaskCount)
        {
            foreach (var account in selected)
                if (account.LockedItemCount > account.TargetItemCount)
                    return $"{account.DisplayName} 已锁定任务 {account.LockedItemCount:N0} 条，超过目标配额 {account.TargetItemCount:N0} 条。";
            var assigned = selected.Sum(x => (long)x.TargetItemCount);
            if (assigned < readyResourceCount) return $"任务配额还差 {readyResourceCount - assigned:N0} 条。";
            if (assigned > readyResourceCount) return $"任务配额超出 {assigned - readyResourceCount:N0} 条。";
        }
        if (transferMode == Core.Domain.TransferMode.Distribution && quotaBasis == Core.Domain.QuotaBasis.SuccessTarget)
        {
            if (selected.Any(x => x.TargetSuccessCount <= 0)) return "参与成功目标调度的账号目标必须大于零。";
            var target = selected.Sum(x => (long)x.TargetSuccessCount);
            if (target > readyResourceCount && shortagePolicy != Core.Domain.ShortagePolicy.WaitForCandidates)
                return "成功目标总数超过当前候选资源数；只有“暂停等待补充”可以保留更高目标。";
        }
        return string.Empty;
    }

    private string BuildWarning()
    {
        if (transferMode != Core.Domain.TransferMode.Distribution || quotaBasis != Core.Domain.QuotaBasis.SuccessTarget)
            return string.Empty;
        var warnings = new List<string>();
        if (ReserveCandidateCount == 0) warnings.Add("当前没有候补资源，任一资源失效都可能产生目标缺口。");
        foreach (var account in Accounts.Where(x => x.IsSelected && x.ReachableTargetSuccessCount < x.TargetSuccessCount))
            warnings.Add($"{account.DisplayName} 的可达目标为 {account.ReachableTargetSuccessCount:N0}，预计缺口 {account.ShortfallCount:N0}。");
        return string.Join(" ", warnings);
    }

    private void AccountChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (suppressInvalidation) return;
        InvalidateConfirmation();
        RaiseAll();
    }

    private bool SetAndInvalidate<T>(ref T field, T value)
    {
        if (!SetProperty(ref field, value)) return false;
        InvalidateConfirmation();
        RaiseAll();
        return true;
    }

    private void SetTarget(AccountAllocationRowViewModel account, int value)
    {
        if (quotaBasis == Core.Domain.QuotaBasis.TaskCount) account.TargetItemCount = value;
        else account.TargetSuccessCount = value;
    }

    private void InvalidateConfirmation()
    {
        if (suppressInvalidation) return;
        ConfirmationToken = null;
        isFinalConfirmationChecked = false;
        OnPropertyChanged(nameof(IsFinalConfirmationChecked));
        ValidationMessage = EvaluateValidation();
        RaiseDerived();
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(JobName));
        OnPropertyChanged(nameof(DefaultTargetPath));
        OnPropertyChanged(nameof(TransferMode));
        OnPropertyChanged(nameof(QuotaBasis));
        OnPropertyChanged(nameof(ShortagePolicy));
        OnPropertyChanged(nameof(ExcelAccountHandling));
        OnPropertyChanged(nameof(ReadyResourceCount));
        OnPropertyChanged(nameof(ResourceCount));
        OnPropertyChanged(nameof(OperationCount));
        OnPropertyChanged(nameof(AssignedCount));
        OnPropertyChanged(nameof(UnassignedCount));
        OnPropertyChanged(nameof(SuccessTargetCount));
        OnPropertyChanged(nameof(ReserveCandidateCount));
        OnPropertyChanged(nameof(WarningMessage));
        RaiseDerived();
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(CanCreateJob));
        NextStepCommand?.NotifyCanExecuteChanged();
        CreateJobCommand?.NotifyCanExecuteChanged();
        PrepareConfirmationCommand?.NotifyCanExecuteChanged();
        LoadFirstPreviewPageCommand?.NotifyCanExecuteChanged();
        LoadNextPreviewPageCommand?.NotifyCanExecuteChanged();
    }

    private void MoveNext()
    {
        var next = currentStepIndex + 1;
        while (next < Steps.Count && Steps[next].IsSkipped) next++;
        if (next >= Steps.Count) return;
        SetCurrentStep(next);
    }

    private void MovePrevious()
    {
        var previous = currentStepIndex - 1;
        while (previous >= 0 && Steps[previous].IsSkipped) previous--;
        if (previous < 0) return;
        SetCurrentStep(previous);
    }

    private void SetCurrentStep(int index)
    {
        Steps[currentStepIndex].IsCurrent = false;
        CurrentStepIndex = index;
        Steps[currentStepIndex].IsCurrent = true;
        PreviousStepCommand.NotifyCanExecuteChanged();
    }

    private static bool IsEligible(WizardAccountOption account) =>
        account.IsEnabled && !account.IsDeleted && string.Equals(account.Status, "Active", StringComparison.Ordinal);

    private sealed class AvailableAdapterAvailability : IAdapterAvailability
    {
        public static AvailableAdapterAvailability Instance { get; } = new();
        public bool CanStartTransfers => true;
        public string StatusMessage => string.Empty;
    }
}
