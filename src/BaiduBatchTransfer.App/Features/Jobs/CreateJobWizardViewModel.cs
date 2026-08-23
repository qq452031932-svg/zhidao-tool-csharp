using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
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
    private readonly IJobResultCapabilityAvailability resultCapabilities;
    private readonly IWizardDraftWorkflow draftWorkflow;
    private readonly IWizardPrecheckWorkflow precheckWorkflow;
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
    private long draftGeneration;
    private long busyGeneration;
    private string folderCreationMode = "None";
    private DuplicatePolicy duplicatePolicy = DuplicatePolicy.Skip;
    private long requiredFreeBytes;
    private long availableFreeBytes;
    private long confirmedResourceCount;
    private long confirmedOperationCount;
    private long transferCount;
    private long shareCount;
    private long totalActionCount;
    private int resultColumnCount;
    private ShareExtractionCodeBehavior extractionCodeBehavior = ShareExtractionCodeBehavior.Random;
    private string fixedExtractionCode = string.Empty;
    private string precheckStatusMessage = string.Empty;

    public event EventHandler<Guid>? JobCreated;

    public CreateJobWizardViewModel(
        IReadOnlyList<WizardAccountOption> accounts,
        IImportPreviewPageSource pageSource,
        IWizardBatchService batchService,
        IDialogService dialogService,
        IAdapterAvailability? adapterAvailability = null,
        IWizardDraftWorkflow? draftWorkflow = null,
        IWizardPrecheckWorkflow? precheckWorkflow = null,
        IJobResultCapabilityAvailability? resultCapabilities = null)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ValidateSelectionDecisions(accounts, nameof(accounts));
        this.pageSource = pageSource ?? throw new ArgumentNullException(nameof(pageSource));
        this.batchService = batchService ?? throw new ArgumentNullException(nameof(batchService));
        this.dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        this.adapterAvailability = adapterAvailability ?? AvailableAdapterAvailability.Instance;
        this.resultCapabilities = resultCapabilities ?? AvailableResultCapabilities.Instance;
        this.draftWorkflow = draftWorkflow ?? new PassiveWizardDraftWorkflow();
        this.precheckWorkflow = precheckWorkflow ?? new UnavailableWizardPrecheckWorkflow();
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
        NextStepCommand = new RelayCommand(MoveNext, () => CanMoveNext);
        PreviousStepCommand = new RelayCommand(MovePrevious, () => !IsBusy && currentStepIndex > 0);
        SelectReplicationCommand = new AsyncRelayCommand(() => SelectModeAsync(Core.Domain.TransferMode.Replication));
        SelectDistributionCommand = new AsyncRelayCommand(() => SelectModeAsync(Core.Domain.TransferMode.Distribution));
        UseTaskQuotaCommand = new RelayCommand(() => QuotaBasis = Core.Domain.QuotaBasis.TaskCount);
        UseSuccessTargetCommand = new RelayCommand(() => QuotaBasis = Core.Domain.QuotaBasis.SuccessTarget);
        UseProportionalReductionCommand = new RelayCommand(() => ShortagePolicy = Core.Domain.ShortagePolicy.ProportionalReduction);
        UseAccountPriorityCommand = new RelayCommand(() => ShortagePolicy = Core.Domain.ShortagePolicy.AccountPriority);
        UseWaitForCandidatesCommand = new RelayCommand(() => ShortagePolicy = Core.Domain.ShortagePolicy.WaitForCandidates);
        KeepExcelMappingsCommand = new RelayCommand(() => ExcelAccountHandling = ExcelAccountHandling.KeepMappings);
        AutoMapUniqueExcelAccountsCommand = new AsyncRelayCommand(AutoMapUniqueExcelAccountsAsync);
        OverrideExcelMappingsCommand = new AsyncRelayCommand(OverrideAllExcelAccountsAsync);
        IgnoreExcelAccountColumnCommand = new AsyncRelayCommand(IgnoreExcelAccountsAsync);
        ApplyOneTwoOneCommand = new RelayCommand(() => TryApplyOneTwoOne());
        ApplyEqualAllocationCommand = new RelayCommand(() => TryApplyEqualAllocation());
        ApplyCapacityAllocationCommand = new RelayCommand(() => TryApplyCapacityAllocation());
        PreserveStructureCommand = new RelayCommand(() => FolderCreationMode = "None");
        CreateOuterFolderCommand = new RelayCommand(() => FolderCreationMode = "OuterFolder");
        SkipDuplicateCommand = new RelayCommand(() => DuplicatePolicy = Core.Domain.DuplicatePolicy.Skip);
        RenameDuplicateCommand = new RelayCommand(() => DuplicatePolicy = Core.Domain.DuplicatePolicy.Rename);
        RunPrecheckCommand = new AsyncRelayCommand(RunPrecheckAsync, () => UseBulkPrecheck && PrecheckAccountId is not null && !IsBusy);
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
    public IAsyncRelayCommand SelectReplicationCommand { get; }
    public IAsyncRelayCommand SelectDistributionCommand { get; }
    public IRelayCommand UseTaskQuotaCommand { get; }
    public IRelayCommand UseSuccessTargetCommand { get; }
    public IRelayCommand UseProportionalReductionCommand { get; }
    public IRelayCommand UseAccountPriorityCommand { get; }
    public IRelayCommand UseWaitForCandidatesCommand { get; }
    public IRelayCommand KeepExcelMappingsCommand { get; }
    public IAsyncRelayCommand AutoMapUniqueExcelAccountsCommand { get; }
    public IAsyncRelayCommand OverrideExcelMappingsCommand { get; }
    public IAsyncRelayCommand IgnoreExcelAccountColumnCommand { get; }
    public IRelayCommand ApplyOneTwoOneCommand { get; }
    public IRelayCommand ApplyEqualAllocationCommand { get; }
    public IRelayCommand ApplyCapacityAllocationCommand { get; }
    public IRelayCommand PreserveStructureCommand { get; }
    public IRelayCommand CreateOuterFolderCommand { get; }
    public IRelayCommand SkipDuplicateCommand { get; }
    public IRelayCommand RenameDuplicateCommand { get; }
    public IAsyncRelayCommand RunPrecheckCommand { get; }

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
    public Guid? PrecheckAccountId
    {
        get => precheckAccountId;
        set
        {
            if (!SetAndInvalidate(ref precheckAccountId, value)) return;
            PrecheckStatusMessage = string.Empty;
            RunPrecheckCommand.NotifyCanExecuteChanged();
        }
    }
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
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!SetProperty(ref isBusy, value)) return;
            OnPropertyChanged(nameof(CanEdit));
            RaiseDerived();
        }
    }
    public bool CanEdit => !IsBusy;
    public int ReadyResourceCount => readyResourceCount;
    public Guid? CurrentImportSessionId => importSessionId == Guid.Empty ? null : importSessionId;
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
    public bool IsImportStepVisible => currentStepIndex == 0;
    public bool IsPrecheckStepVisible => currentStepIndex == 1;
    public bool IsDestinationStepVisible => currentStepIndex == 2;
    public bool IsAllocationStepVisible => currentStepIndex == 3;
    public bool IsConfirmationStepVisible => currentStepIndex == 4;
    public bool CanMoveNext => !IsBusy && Accounts.Count > 0 && (currentStepIndex switch
    {
        0 => importSessionId != Guid.Empty && readyResourceCount > 0 && conflictRowCount + needsResolutionRowCount == 0,
        1 => !UseBulkPrecheck || PrecheckAccountId is not null,
        2 => !string.IsNullOrWhiteSpace(DefaultTargetPath),
        3 => CanContinue,
        _ => false,
    });
    public string AccountSelectionNotice { get; private set; } = string.Empty;
    public string ValidationModeText => UseBulkPrecheck ? "批量预检" : "执行前即时检测";
    public string TransferModeText => transferMode switch
    {
        Core.Domain.TransferMode.Replication => "多账号复制",
        Core.Domain.TransferMode.Distribution => "多账号分流",
        _ => "尚未选择",
    };
    public string QuotaBasisText => quotaBasis == Core.Domain.QuotaBasis.TaskCount ? "按任务数量" : "按成功数量补足";
    public string ShortagePolicyText => shortagePolicy switch
    {
        Core.Domain.ShortagePolicy.ProportionalReduction => "按比例缩减",
        Core.Domain.ShortagePolicy.AccountPriority => "账号优先级",
        _ => "暂停等待补充",
    };
    public string FolderCreationModeText => FolderCreationMode == "OuterFolder" ? "创建外层文件夹" : "保持原结构";
    public string DuplicatePolicyText => DuplicatePolicy == Core.Domain.DuplicatePolicy.Rename ? "自动重命名" : "跳过同名项";
    public string AdapterStatusMessage => adapterAvailability.StatusMessage;
    public string FolderCreationMode { get => folderCreationMode; set => SetAndInvalidate(ref folderCreationMode, value); }
    public DuplicatePolicy DuplicatePolicy { get => duplicatePolicy; set => SetAndInvalidate(ref duplicatePolicy, value); }
    public long RequiredFreeBytes { get => requiredFreeBytes; private set => SetProperty(ref requiredFreeBytes, value); }
    public long AvailableFreeBytes { get => availableFreeBytes; private set => SetProperty(ref availableFreeBytes, value); }
    public long ConfirmedResourceCount { get => confirmedResourceCount; private set => SetProperty(ref confirmedResourceCount, value); }
    public long ConfirmedOperationCount { get => confirmedOperationCount; private set => SetProperty(ref confirmedOperationCount, value); }
    public long TransferCount { get => transferCount; private set => SetProperty(ref transferCount, value); }
    public long ShareCount { get => shareCount; private set => SetProperty(ref shareCount, value); }
    public long TotalActionCount { get => totalActionCount; private set => SetProperty(ref totalActionCount, value); }
    public int ResultColumnCount { get => resultColumnCount; private set => SetProperty(ref resultColumnCount, value); }
    public IReadOnlyList<AccountAllocationRowViewModel> ConfirmationAccounts =>
        Accounts.Where(account => account.IsSelected && account.HasCurrentConfirmation).ToArray();
    public bool IsSharePermanent => true;
    public bool IsShareAccessUnlimited => true;
    public string ShareValidityText => "永久";
    public string ShareAccessText => "不限次数";
    public bool UsesFixedExtractionCode => extractionCodeBehavior == ShareExtractionCodeBehavior.Fixed;
    public string ExtractionCodeBehaviorText => UsesFixedExtractionCode ? "固定提取码" : "随机提取码";
    public ShareExtractionCodeBehavior ExtractionCodeBehavior
    {
        get => extractionCodeBehavior;
        set
        {
            if (value is not ShareExtractionCodeBehavior.Random and not ShareExtractionCodeBehavior.Fixed)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (!SetAndInvalidate(ref extractionCodeBehavior, value)) return;
            OnPropertyChanged(nameof(UsesFixedExtractionCode));
            OnPropertyChanged(nameof(ExtractionCodeBehaviorText));
        }
    }
    public string FixedExtractionCode
    {
        get => fixedExtractionCode;
        set => SetAndInvalidate(ref fixedExtractionCode, value ?? string.Empty);
    }
    public string ExistingSharePolicy => "Ask";
    public string ExistingSharePolicyText => "询问，默认复用";
    public string ShareCapabilityStatusMessage => resultCapabilities.CanUseShareActions
        ? string.Empty
        : resultCapabilities.ShareStatusMessage;
    public string PrecheckStatusMessage { get => precheckStatusMessage; private set => SetProperty(ref precheckStatusMessage, value); }
    public string WarningMessage => BuildWarning();
    public bool CanContinue => string.IsNullOrEmpty(EvaluateValidation());
    public bool CanCreateJob => adapterAvailability.CanStartTransfers && ConfirmationToken is not null &&
        IsFinalConfirmationChecked && CanContinue && !IsBusy;

    public void Initialize(ImportWizardContext context)
    {
        if (context.ImportSessionId == Guid.Empty) throw new ArgumentException("导入会话不能为空。", nameof(context));
        ResetBusy();
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
        OnPropertyChanged(nameof(CurrentImportSessionId));
    }

    public void ReloadAccounts(IReadOnlyList<WizardAccountOption> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ValidateSelectionDecisions(accounts, nameof(accounts));
        if (accounts.Count > 1_000)
            throw new ArgumentOutOfRangeException(nameof(accounts), "账号列表一次最多可刷新 1,000 项。");
        ResetBusy();

        var previousCount = Accounts.Count;
        var previous = Accounts.ToDictionary(account => account.AccountId);
        var eligible = accounts
            .Where(IsEligible)
            .GroupBy(account => account.AccountId)
            .Select(group => group.First())
            .OrderBy(account => account.DisplayName, StringComparer.CurrentCulture)
            .ToArray();

        suppressInvalidation = true;
        try
        {
            foreach (var account in Accounts) account.PropertyChanged -= AccountChanged;
            Accounts.Clear();
            options.Clear();
            options.AddRange(eligible);

            foreach (var option in eligible)
            {
                var replacement = new AccountAllocationRowViewModel(option, defaultTargetPath);
                if (previous.TryGetValue(option.AccountId, out var existing))
                {
                    replacement.IsSelected = existing.IsSelected;
                    replacement.ShareEnabled = existing.ShareEnabled;
                    replacement.TargetItemCount = existing.TargetItemCount;
                    replacement.TargetSuccessCount = existing.TargetSuccessCount;
                    replacement.DefaultTargetPath = existing.DefaultTargetPath;
                    replacement.AllocationPercentage = existing.AllocationPercentage;
                }
                replacement.PropertyChanged += AccountChanged;
                Accounts.Add(replacement);
            }

            if (precheckAccountId is { } selectedPrecheck &&
                Accounts.All(account => account.AccountId != selectedPrecheck))
                precheckAccountId = null;

            if (Accounts.Count == 0)
            {
                transferMode = null;
                precheckAccountId = null;
                AccountSelectionNotice = "没有可用账号，请先完成账号登录。";
            }
            else if (Accounts.Count == 1)
            {
                var account = Accounts[0];
                transferMode = Core.Domain.TransferMode.Distribution;
                account.IsSelected = true;
                account.TargetItemCount = readyResourceCount;
                if (account.TargetSuccessCount <= 0)
                    account.TargetSuccessCount = account.OriginalTargetSuccessCount > 0
                        ? account.OriginalTargetSuccessCount
                        : readyResourceCount;
                if (UseBulkPrecheck) precheckAccountId = account.AccountId;
                AccountSelectionNotice = $"已自动选择唯一可用账号：{account.DisplayName}，请在确认页核对。";
            }
            else
            {
                if (previousCount <= 1) transferMode = null;
                AccountSelectionNotice = transferMode is null
                    ? "请选择多账号复制或多账号分流。"
                    : "账号列表已刷新，请重新核对分配。";
            }
        }
        finally
        {
            suppressInvalidation = false;
        }

        PrecheckStatusMessage = string.Empty;
        OnPropertyChanged(nameof(PrecheckAccountId));
        OnPropertyChanged(nameof(AccountSelectionNotice));
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

    public async Task SelectModeAsync(TransferMode mode, CancellationToken cancellationToken = default)
    {
        if (importSessionId == Guid.Empty) { SelectMode(mode); return; }
        var session = importSessionId;
        var generation = draftGeneration;
        var busyToken = BeginBusy();
        try
        {
            var mutation = await draftWorkflow.SwitchModeAsync(session, mode, cancellationToken);
            if (!IsCurrentDraft(session, generation)) return;
            ApplyMutation(mutation);
            SelectMode(mode);
            StatusMessage = mode == Core.Domain.TransferMode.Replication ? "已切换为多账号复制。" : "已切换为多账号分流。";
        }
        catch (Exception)
        {
            if (IsCurrentDraft(session, generation))
                ValidationMessage = "无法切换模式，请先处理草稿冲突。";
        }
        finally { EndBusy(busyToken); }
    }

    public async Task IgnoreExcelAccountsAsync(CancellationToken cancellationToken = default)
    {
        if (importSessionId == Guid.Empty || transferMode is null) return;
        var session = importSessionId;
        var generation = draftGeneration;
        var mode = transferMode.Value;
        var busyToken = BeginBusy();
        try
        {
            var mutation = await draftWorkflow.IgnoreAllAccountNamesAsync(session, mode, cancellationToken);
            if (!IsCurrentDraft(session, generation)) return;
            ApplyMutation(mutation);
            ExcelAccountHandling = ExcelAccountHandling.IgnoreColumn;
            unresolvedAccountMappingCount = 0;
        }
        catch (Exception)
        {
            if (IsCurrentDraft(session, generation))
                ValidationMessage = "无法忽略 Excel 账号列，请刷新草稿后重试。";
        }
        finally { EndBusy(busyToken); }
    }

    public async Task AutoMapUniqueExcelAccountsAsync(CancellationToken cancellationToken = default)
    {
        if (importSessionId == Guid.Empty || transferMode != Core.Domain.TransferMode.Distribution) return;
        var session = importSessionId;
        var generation = draftGeneration;
        var mode = transferMode.Value;
        var busyToken = BeginBusy();
        try
        {
            var mutation = await draftWorkflow.AutoMapUniqueAccountNamesAsync(
                session, mode, cancellationToken);
            if (!IsCurrentDraft(session, generation)) return;
            ApplyMutation(mutation);
            unresolvedAccountMappingCount = mutation.UnresolvedAccountMappings;
            ExcelAccountHandling = ExcelAccountHandling.MapNames;
            StatusMessage = unresolvedAccountMappingCount == 0
                ? "Excel 账号名称已全部唯一匹配。"
                : $"已匹配唯一名称，仍有 {unresolvedAccountMappingCount:N0} 个名称需要统一覆盖或人工处理。";
        }
        catch (Exception)
        {
            if (IsCurrentDraft(session, generation))
                ValidationMessage = "无法匹配 Excel 账号名称，请刷新草稿后重试。";
        }
        finally { EndBusy(busyToken); }
    }

    public async Task OverrideAllExcelAccountsAsync(CancellationToken cancellationToken = default)
    {
        if (importSessionId == Guid.Empty || transferMode is null) return;
        var account = Accounts.FirstOrDefault(x => x.IsSelected);
        if (account is null) { ValidationMessage = "请先选择用于覆盖的账号。"; return; }
        var session = importSessionId;
        var generation = draftGeneration;
        var mode = transferMode.Value;
        var busyToken = BeginBusy();
        try
        {
            var mutation = await draftWorkflow.OverrideAllAccountsAsync(session, account.AccountId, mode, cancellationToken);
            if (!IsCurrentDraft(session, generation)) return;
            ApplyMutation(mutation);
            ExcelAccountHandling = ExcelAccountHandling.OverrideAll;
            unresolvedAccountMappingCount = 0;
        }
        catch (Exception)
        {
            if (IsCurrentDraft(session, generation))
                ValidationMessage = "无法覆盖逐行账号，请刷新草稿后重试。";
        }
        finally { EndBusy(busyToken); }
    }

    public async Task RunPrecheckAsync(CancellationToken cancellationToken = default)
    {
        if (!UseBulkPrecheck || importSessionId == Guid.Empty || PrecheckAccountId is not Guid accountId) return;
        if (Accounts.All(x => x.AccountId != accountId))
        {
            ValidationMessage = "请选择一个可用账号执行预检。";
            return;
        }

        var session = importSessionId;
        var generation = draftGeneration;
        var busyToken = BeginBusy();
        try
        {
            var result = await precheckWorkflow.RunAsync(session, accountId, cancellationToken);
            if (!IsCurrentDraft(session, generation)) return;
            PrecheckStatusMessage = result.Status switch
            {
                WizardPrecheckStatus.Completed => $"预检已继续并完成，已处理 {result.ProcessedRows:N0} 条记录。",
                WizardPrecheckStatus.PausedForLogin => "预检已暂停，请完成账号登录后点击继续预检。",
                WizardPrecheckStatus.PausedForVerification => "预检已暂停，请完成安全验证后点击继续预检。",
                _ => "百度网盘适配器尚不可用，暂时无法执行预检。",
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (IsCurrentDraft(session, generation))
                PrecheckStatusMessage = "预检已取消，可使用同一账号继续。";
        }
        catch (Exception)
        {
            if (IsCurrentDraft(session, generation))
                PrecheckStatusMessage = "预检失败，可使用同一账号继续重试。";
        }
        finally { EndBusy(busyToken); }
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

    public bool TryApplyEqualAllocation()
    {
        var selected = Accounts.Where(x => x.IsSelected).ToArray();
        if (selected.Length == 0 || transferMode != Core.Domain.TransferMode.Distribution) return false;
        var perAccount = readyResourceCount / selected.Length;
        var remainder = readyResourceCount % selected.Length;
        ApplyTargets(selected, selected.Select((_, index) => perAccount + (index < remainder ? 1 : 0)).ToArray());
        return true;
    }

    public bool TryApplyCapacityAllocation()
    {
        var selected = Accounts.Where(x => x.IsSelected).ToArray();
        if (selected.Length == 0 || transferMode != Core.Domain.TransferMode.Distribution) return false;
        var weights = selected.Select(x => new BigInteger(Math.Max(0, x.RemainingCapacity ?? 0))).ToArray();
        var totalWeight = weights.Aggregate(BigInteger.Zero, (sum, value) => sum + value);
        if (totalWeight == BigInteger.Zero) return TryApplyEqualAllocation();

        var targets = new int[selected.Length];
        var remainders = new (int Index, BigInteger Remainder)[selected.Length];
        var assigned = 0;
        for (var index = 0; index < selected.Length; index++)
        {
            var quotient = BigInteger.DivRem(weights[index] * readyResourceCount, totalWeight, out var remainder);
            targets[index] = checked((int)quotient);
            assigned += targets[index];
            remainders[index] = (index, remainder);
        }
        foreach (var item in remainders.OrderByDescending(x => x.Remainder).ThenBy(x => x.Index).Take(readyResourceCount - assigned))
            targets[item.Index]++;
        ApplyTargets(selected, targets);
        return true;
    }

    private void ApplyTargets(IReadOnlyList<AccountAllocationRowViewModel> accounts, IReadOnlyList<int> targets)
    {
        suppressInvalidation = true;
        try
        {
            for (var index = 0; index < accounts.Count; index++) SetTarget(accounts[index], targets[index]);
        }
        finally { suppressInvalidation = false; }
        InvalidateConfirmation();
        RaiseAll();
    }

    public async Task LoadFirstPreviewPageAsync(CancellationToken cancellationToken = default)
    {
        nextPreviewCursor = null;
        await LoadPreviewPageAsync(null, replace: true, cancellationToken);
    }

    public async Task LoadNextPreviewPageAsync(CancellationToken cancellationToken = default)
    {
        if (nextPreviewCursor is null) return;
        await LoadPreviewPageAsync(nextPreviewCursor, replace: true, cancellationToken);
    }

    public async Task PrepareConfirmationAsync(CancellationToken cancellationToken = default)
    {
        var validation = EvaluateValidation();
        ValidationMessage = validation;
        if (validation.Length != 0) return;
        var busyToken = BeginBusy();
        var generation = draftGeneration;
        var session = importSessionId;
        try
        {
            var draft = BuildDraft();
            var summary = await batchService.PreviewAsync(draft, cancellationToken);
            if (generation != draftGeneration || session != importSessionId) return;
            if (!summary.HasSufficientDiskSpace)
                throw new StorageCapacityException(0, 0);
            ApplyConfirmationSummary(summary);
            ConfirmationToken = summary.ConfirmationToken;
            StatusMessage = $"预览完成：{summary.ResourceCount:N0} 个资源，{summary.OperationCount:N0} 次转存操作。";
            IsFinalConfirmationChecked = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (IsCurrentDraft(session, generation)) StatusMessage = "预览已取消。";
        }
        catch (StorageCapacityException)
        {
            if (IsCurrentDraft(session, generation))
            {
                InvalidateConfirmation();
                ValidationMessage = "本机磁盘空间不足，无法安全创建任务。请释放空间后重新预览。";
            }
        }
        catch (BatchDraftValidationException)
        {
            if (IsCurrentDraft(session, generation))
            {
                InvalidateConfirmation();
                ValidationMessage = "任务配置或导入草稿已变化，请返回检查并重新预览。";
            }
        }
        catch (Exception)
        {
            if (IsCurrentDraft(session, generation))
            {
                InvalidateConfirmation();
                ValidationMessage = "任务预览失败，请检查配置后重试。";
            }
        }
        finally { EndBusy(busyToken); }
    }

    public async Task CreateJobAsync(CancellationToken cancellationToken = default)
    {
        if (!CanCreateJob || ConfirmationToken is null) return;
        var token = ConfirmationToken;
        var draft = BuildDraft();
        var session = importSessionId;
        var generation = draftGeneration;
        var confirmed = await dialogService.ConfirmAsync(
            "最终确认",
            $"将创建任务“{JobName}”，包含 {ConfirmedResourceCount:N0} 个资源、{ConfirmedOperationCount:N0} 次转存操作。确认继续吗？",
            cancellationToken);
        if (!confirmed || !IsCurrentDraft(session, generation)) return;
        var busyToken = BeginBusy();
        var created = false;
        try
        {
            await batchService.ConfirmAsync(draft, token, cancellationToken);
            created = true;
            if (IsCurrentDraft(session, generation))
            {
                ConfirmationToken = null;
                IsFinalConfirmationChecked = false;
                StatusMessage = "任务已创建，正在等待调度。";
            }
        }
        catch (BatchConfirmationException)
        {
            if (IsCurrentDraft(session, generation))
            {
                InvalidateConfirmation();
                ValidationMessage = "导入草稿或任务设置已变化，请重新预览并确认。";
            }
        }
        catch (StorageCapacityException)
        {
            if (IsCurrentDraft(session, generation))
            {
                InvalidateConfirmation();
                ValidationMessage = "本机磁盘空间不足，任务没有创建。";
            }
        }
        catch (Exception)
        {
            if (IsCurrentDraft(session, generation))
            {
                InvalidateConfirmation();
                ValidationMessage = "任务创建失败，未写入执行队列，请重试。";
            }
        }
        finally { EndBusy(busyToken); }
        if (created) JobCreated?.Invoke(this, draft.BatchJobId);
    }

    private async Task LoadPreviewPageAsync(long? cursor, bool replace, CancellationToken cancellationToken)
    {
        if (importSessionId == Guid.Empty) return;
        var expectedSession = importSessionId;
        var generation = draftGeneration;
        var busyToken = BeginBusy();
        try
        {
            var page = await pageSource.LoadAsync(new(expectedSession, PreviewPageSize, cursor), cancellationToken);
            if (expectedSession != importSessionId || generation != draftGeneration) return;
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
        catch (Exception)
        {
            if (IsCurrentDraft(expectedSession, generation))
                StatusMessage = "无法加载预览页，请稍后重试。";
        }
        finally { EndBusy(busyToken); }
    }

    private WizardBatchDraft BuildDraft()
    {
        var mode = transferMode ?? throw new InvalidOperationException("尚未选择转存模式。");
        var accountDrafts = Accounts.Select((account, index) => new WizardAccountDraft(
            account.AccountId,
            index,
            account.DefaultTargetPath,
            account.TargetItemCount,
            account.TargetSuccessCount,
            account.IsSelected ? BatchAccountSelectionDecision.Selected : BatchAccountSelectionDecision.NotSelected,
            account.ShareEnabled)).ToArray();
        return new(
            batchJobId,
            importSessionId,
            JobName,
            mode,
            mode == Core.Domain.TransferMode.Replication ? Core.Domain.QuotaBasis.TaskCount : quotaBasis,
            shortagePolicy,
            accountDrafts,
            DefaultTargetPath,
            FolderCreationMode,
            DuplicatePolicy,
            UseBulkPrecheck ? "BatchPrecheck" : "Immediate",
            mode == Core.Domain.TransferMode.Replication && ExcelAccountHandling == ExcelAccountHandling.IgnoreColumn,
            readyResourceCount,
            IsSharePermanent,
            IsShareAccessUnlimited,
            ExtractionCodeBehavior,
            ExtractionCodeBehavior == ShareExtractionCodeBehavior.Fixed ? FixedExtractionCode.Trim() : null,
            ExistingSharePolicy);
    }

    private static void ValidateSelectionDecisions(
        IReadOnlyList<WizardAccountOption> accounts,
        string parameterName)
    {
        if (accounts.Any(account =>
                account.SelectionDecision != BatchAccountSelectionDecision.Selected &&
                account.SelectionDecision != BatchAccountSelectionDecision.NotSelected))
            throw new ArgumentException("每个候选账号都必须明确选择或不选择。", parameterName);
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
        if (selected.Any(account => account.ShareEnabled) && !resultCapabilities.CanUseShareActions)
            return string.IsNullOrWhiteSpace(resultCapabilities.ShareStatusMessage)
                ? "分享功能尚未通过现场验证；请先关闭分享或完成能力验收。"
                : resultCapabilities.ShareStatusMessage;
        if (ExtractionCodeBehavior == ShareExtractionCodeBehavior.Fixed &&
            string.IsNullOrWhiteSpace(FixedExtractionCode))
            return "固定提取码模式必须填写固定提取码。";
        if (string.IsNullOrWhiteSpace(JobName)) return "请输入任务名称。";
        if (string.IsNullOrWhiteSpace(DefaultTargetPath)) return "请选择默认目标目录。";
        if (UseBulkPrecheck && (PrecheckAccountId is null || Accounts.All(x => x.AccountId != PrecheckAccountId)))
            return "请选择一个可用账号作为预检账号。";
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
        if (!Accounts.Any(account => account.IsSelected && account.HasCurrentConfirmation))
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

    private void ApplyMutation(WizardDraftMutation mutation)
    {
        readyResourceCount = mutation.ReadyRows;
        conflictRowCount = mutation.ConflictRows;
        needsResolutionRowCount = mutation.NeedsResolutionRows;
        InvalidateConfirmation();
        RaiseAll();
    }

    private bool SetAndInvalidate<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName)) return false;
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
        ConfirmedResourceCount = 0;
        ConfirmedOperationCount = 0;
        TransferCount = 0;
        ShareCount = 0;
        TotalActionCount = 0;
        ResultColumnCount = 0;
        RequiredFreeBytes = 0;
        AvailableFreeBytes = 0;
        suppressInvalidation = true;
        try
        {
            foreach (var account in Accounts) account.ClearConfirmation();
        }
        finally
        {
            suppressInvalidation = false;
        }
        draftGeneration++;
        isFinalConfirmationChecked = false;
        OnPropertyChanged(nameof(IsFinalConfirmationChecked));
        OnPropertyChanged(nameof(WarningMessage));
        ValidationMessage = EvaluateValidation();
        RaiseDerived();
    }

    private void ApplyConfirmationSummary(WizardConfirmationSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (summary.ResourceCount < 0 || summary.OperationCount < 0 || summary.TransferCount < 0 ||
            summary.ShareCount < 0 || summary.TotalActionCount < 0 || summary.ResultColumnCount < 0 ||
            summary.RequiredFreeBytes < 0 || summary.AvailableFreeBytes < 0 ||
            summary.OperationCount != summary.TransferCount ||
            summary.TotalActionCount != checked(summary.TransferCount + summary.ShareCount))
            throw new BatchDraftValidationException("预览汇总计数无效。");

        var selected = Accounts.Where(account => account.IsSelected).ToDictionary(account => account.AccountId);
        var confirmations = summary.Accounts?.ToArray()
            ?? throw new BatchDraftValidationException("预览缺少账号确认快照。");
        if (confirmations.Select(value => value.AccountId).Distinct().Count() != confirmations.Length ||
            confirmations.Length != selected.Count ||
            confirmations.Any(value => !selected.ContainsKey(value.AccountId)))
            throw new BatchDraftValidationException("预览账号确认快照与当前选择不一致。");
        if (confirmations.Any(value => selected[value.AccountId].ShareEnabled != value.ShareEnabled))
            throw new BatchDraftValidationException("预览账号分享设置与当前草稿不一致。");
        if (confirmations.Any(value =>
                value.PlannedTransferCount is null or < 0 ||
                value.PlannedShareCount is null or < 0 ||
                value.ConfirmedTransferCount is null or < 0 ||
                value.ConfirmedShareCount is null or < 0))
            throw new BatchDraftValidationException("预览账号工作量无效。");
        try
        {
            var plannedTransfers = confirmations.Sum(value => value.PlannedTransferCount.GetValueOrDefault());
            var plannedShares = confirmations.Sum(value => value.PlannedShareCount.GetValueOrDefault());
            var confirmedTransfers = confirmations.Sum(value => value.ConfirmedTransferCount.GetValueOrDefault());
            var confirmedShares = confirmations.Sum(value => value.ConfirmedShareCount.GetValueOrDefault());
            if (plannedTransfers != summary.TransferCount || confirmedTransfers != summary.TransferCount ||
                plannedShares != summary.ShareCount || confirmedShares != summary.ShareCount ||
                confirmations.Any(value =>
                    value.PlannedShareCount > value.PlannedTransferCount ||
                    value.ConfirmedShareCount > value.ConfirmedTransferCount ||
                    (!value.ShareEnabled &&
                        (value.PlannedShareCount != 0 || value.ConfirmedShareCount != 0))))
                throw new BatchDraftValidationException("预览账号工作量与汇总不一致。");
        }
        catch (OverflowException)
        {
            throw new BatchDraftValidationException("预览账号工作量超出支持范围。");
        }

        suppressInvalidation = true;
        try
        {
            foreach (var confirmation in confirmations)
                selected[confirmation.AccountId].ApplyConfirmation(confirmation);
            RequiredFreeBytes = summary.RequiredFreeBytes;
            AvailableFreeBytes = summary.AvailableFreeBytes;
            ConfirmedResourceCount = summary.ResourceCount;
            ConfirmedOperationCount = summary.OperationCount;
            TransferCount = summary.TransferCount;
            ShareCount = summary.ShareCount;
            TotalActionCount = summary.TotalActionCount;
            ResultColumnCount = summary.ResultColumnCount;
        }
        finally
        {
            suppressInvalidation = false;
        }
        ValidationMessage = EvaluateValidation();
        RaiseAll();
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(JobName));
        OnPropertyChanged(nameof(DefaultTargetPath));
        OnPropertyChanged(nameof(TransferMode));
        OnPropertyChanged(nameof(QuotaBasis));
        OnPropertyChanged(nameof(ShortagePolicy));
        OnPropertyChanged(nameof(ExcelAccountHandling));
        OnPropertyChanged(nameof(TransferModeText));
        OnPropertyChanged(nameof(QuotaBasisText));
        OnPropertyChanged(nameof(ShortagePolicyText));
        OnPropertyChanged(nameof(FolderCreationModeText));
        OnPropertyChanged(nameof(DuplicatePolicyText));
        OnPropertyChanged(nameof(ValidationModeText));
        OnPropertyChanged(nameof(ReadyResourceCount));
        OnPropertyChanged(nameof(ResourceCount));
        OnPropertyChanged(nameof(OperationCount));
        OnPropertyChanged(nameof(AssignedCount));
        OnPropertyChanged(nameof(UnassignedCount));
        OnPropertyChanged(nameof(SuccessTargetCount));
        OnPropertyChanged(nameof(ReserveCandidateCount));
        OnPropertyChanged(nameof(WarningMessage));
        OnPropertyChanged(nameof(CanMoveNext));
        RaiseDerived();
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(CanCreateJob));
        OnPropertyChanged(nameof(ConfirmationAccounts));
        NextStepCommand?.NotifyCanExecuteChanged();
        PreviousStepCommand?.NotifyCanExecuteChanged();
        CreateJobCommand?.NotifyCanExecuteChanged();
        PrepareConfirmationCommand?.NotifyCanExecuteChanged();
        LoadFirstPreviewPageCommand?.NotifyCanExecuteChanged();
        LoadNextPreviewPageCommand?.NotifyCanExecuteChanged();
        RunPrecheckCommand?.NotifyCanExecuteChanged();
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
        if (index == currentStepIndex) return;
        InvalidateConfirmation();
        Steps[currentStepIndex].IsCurrent = false;
        CurrentStepIndex = index;
        Steps[currentStepIndex].IsCurrent = true;
        OnPropertyChanged(nameof(IsImportStepVisible));
        OnPropertyChanged(nameof(IsPrecheckStepVisible));
        OnPropertyChanged(nameof(IsDestinationStepVisible));
        OnPropertyChanged(nameof(IsAllocationStepVisible));
        OnPropertyChanged(nameof(IsConfirmationStepVisible));
        OnPropertyChanged(nameof(CanMoveNext));
        PreviousStepCommand.NotifyCanExecuteChanged();
    }

    private bool IsCurrentDraft(Guid session, long generation) =>
        session == importSessionId && generation == draftGeneration;

    private long BeginBusy()
    {
        var token = ++busyGeneration;
        IsBusy = true;
        return token;
    }

    private void EndBusy(long token)
    {
        if (token == busyGeneration) IsBusy = false;
    }

    private void ResetBusy()
    {
        busyGeneration++;
        IsBusy = false;
    }

    private static bool IsEligible(WizardAccountOption account) =>
        account.IsEnabled && !account.IsDeleted && string.Equals(account.Status, "Active", StringComparison.Ordinal);

    private sealed class AvailableAdapterAvailability : IAdapterAvailability
    {
        public static AvailableAdapterAvailability Instance { get; } = new();
        public bool CanStartTransfers => true;
        public string StatusMessage => string.Empty;
    }

    private sealed class AvailableResultCapabilities : IJobResultCapabilityAvailability
    {
        public static AvailableResultCapabilities Instance { get; } = new();
        public bool CanUseTransferActions => true;
        public bool CanUseShareActions => true;
        public string TransferStatusMessage => string.Empty;
        public string ShareStatusMessage => string.Empty;
    }
}
