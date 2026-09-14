using System.ComponentModel;
using System.Collections.Specialized;
using System.IO;
using BaiduBatchTransfer.App.Features.Common;
using BaiduBatchTransfer.App.Features.Imports;
using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Imports;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SuperPan.Licensing.Client;

namespace BaiduBatchTransfer.App.Features.Transfers;

public sealed class TransferWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly IResultSaveDialog resultDirectoryDialog;
    private readonly IDialogService? dialogService;
    private readonly ITransferResultDestinationSink destinationSink;
    private readonly ILocalResultDirectoryPreparer resultDirectoryPreparer;
    private readonly ITransferTargetDirectoryPicker? targetDirectoryPicker;
    private readonly Func<Guid, CancellationToken, Task<ResultWorkspaceHandoffOutcome>> handoffBatchAsync;
    private readonly IAdapterAvailability adapterAvailability;
    private readonly TransferWorkspaceOptions options;
    private readonly RemoteWriteExecutionMode remoteWriteMode;
    private readonly Func<Guid, CancellationToken, Task<ManagedTransferAcceptanceRunOutcome>>? acceptanceRunAsync;
    private readonly ILicenseAuthorizationGate? licenseGate;
    private readonly CancellationTokenSource lifetime = new();
    private readonly WorkspaceLogBuffer logBuffer = new();
    private readonly SemaphoreSlim modeSelectionGate = new(1, 1);
    private TransferAccountMode? accountMode;
    private bool autoShare;
    private string shareResultDirectory = string.Empty;
    private string statusMessage = "请先导入链接，再选择账号和保存目录。";
    private bool isStarting;
    private bool isClosed;
    private bool synchronizingAccounts;
    private int accountSynchronizationSuppression;
    private long modeSelectionVersion;
    private CancellationTokenSource? modeSelectionCancellation;
    private Guid? createdBatchJobId;
    private Guid? simplifiedSelectedAccountId;
    private readonly HashSet<Guid> simplifiedSelectedAccountIds = [];
    private int selectedDataTabIndex;
    private readonly HashSet<Guid> batchPendingRowIds = [];
    private int expectedOperationsPerResource;
    private bool createdBatchIsTerminal;
    private readonly LocalHistoryCleanupViewModel historyCleanup;
    private readonly LocalImportDraftCleanupService? importDraftCleanupService;
    private readonly ICurrentImportCleanupInteraction currentImportCleanupInteraction;
    private readonly object currentImportCleanupSync = new();
    private readonly CurrentImportActivityGate currentImportActivityGate = new();
    private IDisposable? currentImportCleanupLease;
    private bool currentImportCleanupBusy;

    public TransferWorkspaceViewModel(
        ImportViewModel import,
        CreateJobWizardViewModel wizard,
        WorkspaceRunPanelViewModel runPanel,
        IResultSaveDialog resultDirectoryDialog,
        ITransferResultDestinationSink destinationSink,
        Func<Guid, CancellationToken, Task<ResultWorkspaceHandoffOutcome>> handoffBatchAsync,
        IAdapterAvailability adapterAvailability,
        TransferWorkspaceOptions? options = null,
        RemoteWriteExecutionMode remoteWriteMode = RemoteWriteExecutionMode.Full,
        Func<Guid, CancellationToken, Task<ManagedTransferAcceptanceRunOutcome>>? acceptanceRunAsync = null,
        ILocalResultDirectoryPreparer? resultDirectoryPreparer = null,
        ITransferTargetDirectoryPicker? targetDirectoryPicker = null,
        IDialogService? dialogService = null,
        ILicenseAuthorizationGate? licenseGate = null,
        LocalTerminalHistoryService? historyCleanupService = null,
        ILocalHistoryCleanupInteraction? historyCleanupInteraction = null,
        LocalImportDraftCleanupService? importDraftCleanupService = null,
        ICurrentImportCleanupInteraction? currentImportCleanupInteraction = null)
    {
        Import = import ?? throw new ArgumentNullException(nameof(import));
        Wizard = wizard ?? throw new ArgumentNullException(nameof(wizard));
        RunPanel = runPanel ?? throw new ArgumentNullException(nameof(runPanel));
        this.resultDirectoryDialog = resultDirectoryDialog ?? throw new ArgumentNullException(nameof(resultDirectoryDialog));
        this.dialogService = dialogService;
        this.destinationSink = destinationSink ?? throw new ArgumentNullException(nameof(destinationSink));
        this.resultDirectoryPreparer = resultDirectoryPreparer ?? new LocalResultDirectoryPreparer();
        this.targetDirectoryPicker = targetDirectoryPicker;
        this.handoffBatchAsync = handoffBatchAsync ?? throw new ArgumentNullException(nameof(handoffBatchAsync));
        this.adapterAvailability = adapterAvailability ?? throw new ArgumentNullException(nameof(adapterAvailability));
        this.options = (options ?? new TransferWorkspaceOptions()).Validate();
        this.remoteWriteMode = remoteWriteMode;
        this.acceptanceRunAsync = acceptanceRunAsync;
        this.licenseGate = licenseGate;
        this.importDraftCleanupService = importDraftCleanupService;
        this.currentImportCleanupInteraction = currentImportCleanupInteraction ?? RefusingCurrentImportCleanupInteraction.Instance;
        Import.AttachActivityGate(currentImportActivityGate);
        Wizard.AttachActivityGate(currentImportActivityGate);
        currentImportActivityGate.StateChanged += OnCurrentImportActivityChanged;
        historyCleanup = new(historyCleanupService, historyCleanupInteraction, LocalHistoryKind.Transfer,
            () => !isClosed && !IsStarting && !Import.IsBusy && !Wizard.IsBusy &&
                !RunPanel.IsClosed && !RunPanel.IsBusy && !RunPanel.Results.IsBusy && !RunPanel.Results.IsSaving,
            RunPanel.ReloadHistoryAsync, text => StatusMessage = text);
        StartCommand = new AsyncRelayCommand(StartAsync, () => CanStart);
        ClearLogsCommand = new RelayCommand(ClearLogs, () => CanClearLogs);
        SelectSingleAccountCommand = new AsyncRelayCommand(
            () => SelectAccountModeAsync(TransferAccountMode.SingleAccount),
            () => CanEditConfiguration);
        SelectReplicationCommand = new AsyncRelayCommand(
            () => SelectAccountModeAsync(TransferAccountMode.MultiAccountReplication),
            () => CanEditConfiguration);
        SelectDistributionCommand = new AsyncRelayCommand(
            () => SelectAccountModeAsync(TransferAccountMode.MultiAccountDistribution),
            () => CanEditConfiguration);
        PickShareResultDirectoryCommand = new AsyncRelayCommand(
            PickShareResultDirectoryAsync,
            () => AutoShare && CanEditConfiguration);
        PickTargetDirectoryCommand = new AsyncRelayCommand(
            PickTargetDirectoryAsync,
            () => CanEditConfiguration);
        ClearCurrentImportCommand = new AsyncRelayCommand(ClearCurrentImportAsync, () => CanClearCurrentImport);
        Import.DraftReady += OnDraftReady;
        Import.PropertyChanged += OnComposedPropertyChanged;
        Import.PropertyChanged += OnStatusSourcePropertyChanged;
        Wizard.PropertyChanged += OnComposedPropertyChanged;
        RunPanel.PropertyChanged += OnStatusSourcePropertyChanged;
        RunPanel.BatchTerminal += OnBatchTerminal;
        RunPanel.Results.PropertyChanged += OnStatusSourcePropertyChanged;
        RunPanel.Results.ManualRefreshCompleted += OnManualResultsRefreshCompleted;
        RunPanel.Results.Items.CollectionChanged += OnResultItemsChanged;
        Import.Rows.CollectionChanged += OnPendingRowsChanged;
        ((INotifyCollectionChanged)Logs).CollectionChanged += OnLogsChanged;
        historyCleanup.PropertyChanged += OnHistoryCleanupPropertyChanged;
        AppendPresentationLog(StatusMessage);
        AppendPresentationLog(Import.StatusMessage);
        AppendPresentationLog(RunPanel.StatusMessage);
        AppendPresentationLog(RunPanel.Results.StatusMessage);
        if (IsSimplifiedDirectMode)
        {
            ConfigureSimplifiedDirectMode();
        }
        else if (remoteWriteMode == RemoteWriteExecutionMode.AcceptanceOnly)
        {
            Wizard.AcceptanceOnly = true;
            AutoShare = true;
            if (!string.IsNullOrWhiteSpace(this.options.DefaultResultDirectory))
                ShareResultDirectory = Path.GetFullPath(this.options.DefaultResultDirectory);
        }
    }

    public TransferWorkspaceViewModel(
        ImportViewModel import,
        CreateJobWizardViewModel wizard,
        WorkspaceRunPanelViewModel runPanel,
        IResultSaveDialog resultDirectoryDialog,
        ITransferResultDestinationSink destinationSink,
        Func<Guid, CancellationToken, Task> handoffBatchAsync,
        IAdapterAvailability adapterAvailability,
            TransferWorkspaceOptions? options = null,
            RemoteWriteExecutionMode remoteWriteMode = RemoteWriteExecutionMode.Full,
            Func<Guid, CancellationToken, Task<ManagedTransferAcceptanceRunOutcome>>? acceptanceRunAsync = null,
            ILocalResultDirectoryPreparer? resultDirectoryPreparer = null,
            ITransferTargetDirectoryPicker? targetDirectoryPicker = null,
            IDialogService? dialogService = null,
            ILicenseAuthorizationGate? licenseGate = null,
            LocalTerminalHistoryService? historyCleanupService = null,
            ILocalHistoryCleanupInteraction? historyCleanupInteraction = null,
            LocalImportDraftCleanupService? importDraftCleanupService = null,
            ICurrentImportCleanupInteraction? currentImportCleanupInteraction = null)
        : this(
            import,
            wizard,
            runPanel,
            resultDirectoryDialog,
            destinationSink,
            async (batchJobId, cancellationToken) =>
            {
                await handoffBatchAsync(batchJobId, cancellationToken).ConfigureAwait(false);
                return new(ResultWorkspaceHandoffStatus.Succeeded);
            },
            adapterAvailability,
            options,
            remoteWriteMode,
            acceptanceRunAsync,
            resultDirectoryPreparer,
            targetDirectoryPicker,
            dialogService,
            licenseGate,
            historyCleanupService,
            historyCleanupInteraction,
            importDraftCleanupService,
            currentImportCleanupInteraction)
    {
    }

    public ImportViewModel Import { get; }
    public CreateJobWizardViewModel Wizard { get; }
    public WorkspaceRunPanelViewModel RunPanel { get; }
    public System.Collections.ObjectModel.ReadOnlyObservableCollection<WorkspaceLogEntry> Logs => logBuffer.Entries;
    public IReadOnlyList<int> ConcurrencyOptions { get; } = [1, 2, 3];
    public bool IsSimplifiedDirectMode =>
        options.SimplifiedDirectMode && remoteWriteMode != RemoteWriteExecutionMode.AcceptanceOnly;
    public int MaxConcurrency
    {
        get => Wizard.MaxConcurrency;
        set
        {
            if (Wizard.MaxConcurrency == value) return;
            Wizard.MaxConcurrency = value;
            OnPropertyChanged();
            RaiseStartState();
        }
    }
    public TransferAccountMode? AccountMode
    {
        get => accountMode;
        private set
        {
            if (!SetProperty(ref accountMode, value)) return;
            NotifyAccountModePresentation();
            RaiseStartState();
        }
    }
    public string AccountModeText => AccountMode switch
    {
        TransferAccountMode.SingleAccount => "单账号",
        TransferAccountMode.MultiAccountReplication => "多账号复制",
        TransferAccountMode.MultiAccountDistribution => "多账号分流",
        _ => "请选择账号模式",
    };
    public bool IsSingleAccountMode => AccountMode == TransferAccountMode.SingleAccount;
    public bool IsReplicationMode => AccountMode == TransferAccountMode.MultiAccountReplication;
    public bool IsDistributionMode => AccountMode == TransferAccountMode.MultiAccountDistribution;
    public bool AutoShare
    {
        get => autoShare;
        set
        {
            if ((IsSimplifiedDirectMode || remoteWriteMode == RemoteWriteExecutionMode.AcceptanceOnly) && !value)
                value = true;
            if (!SetProperty(ref autoShare, value)) return;
            if (IsSimplifiedDirectMode) SynchronizeAccountState();
            else foreach (var account in Wizard.Accounts) account.ShareEnabled = value;
            OnPropertyChanged(nameof(IsShareConfigurationVisible));
            RaiseStartState();
        }
    }
    public bool IsShareConfigurationVisible => AutoShare;
    public string ShareResultDirectory
    {
        get => shareResultDirectory;
        set
        {
            if (!SetProperty(ref shareResultDirectory, value?.Trim() ?? string.Empty)) return;
            Wizard.AcceptanceResultDirectory = shareResultDirectory;
            Wizard.ResultDirectory = shareResultDirectory;
            RaiseStartState();
        }
    }
    public ShareExtractionCodeBehavior ExtractionCodeBehavior
    {
        get => Wizard.ExtractionCodeBehavior;
        set
        {
            if (Wizard.ExtractionCodeBehavior == value) return;
            Wizard.ExtractionCodeBehavior = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UsesFixedExtractionCode));
            RaiseStartState();
        }
    }
    public bool UsesFixedExtractionCode
    {
        get => ExtractionCodeBehavior == ShareExtractionCodeBehavior.Fixed;
        set => ExtractionCodeBehavior = value
            ? ShareExtractionCodeBehavior.Fixed
            : ShareExtractionCodeBehavior.Random;
    }
    public string FixedExtractionCode
    {
        get => Wizard.FixedExtractionCode;
        set
        {
            if (Wizard.FixedExtractionCode == (value ?? string.Empty)) return;
            Wizard.FixedExtractionCode = value ?? string.Empty;
            OnPropertyChanged();
            RaiseStartState();
        }
    }
    public string StatusMessage
    {
        get => statusMessage;
        private set
        {
            if (!SetProperty(ref statusMessage, value)) return;
            OnPropertyChanged(nameof(UserFacingStatusMessage));
            if (!isClosed) AppendPresentationLog(value);
        }
    }
    public string UserFacingStatusMessage => MapPresentationLogMessage(StatusMessage) ?? string.Empty;
    public bool IsStarting
    {
        get => isStarting;
        private set
        {
            if (!SetProperty(ref isStarting, value)) return;
            OnPropertyChanged(nameof(CanEditConfiguration));
            RaiseStartState();
            SelectSingleAccountCommand.NotifyCanExecuteChanged();
            SelectReplicationCommand.NotifyCanExecuteChanged();
            SelectDistributionCommand.NotifyCanExecuteChanged();
            PickShareResultDirectoryCommand.NotifyCanExecuteChanged();
            PickTargetDirectoryCommand.NotifyCanExecuteChanged();
        }
    }
    public bool IsClosed => isClosed;
    public bool CanEditConfiguration => !isClosed && !IsStarting && !historyCleanup.IsBusy && !currentImportCleanupBusy;
    public Guid? CreatedBatchJobId => createdBatchJobId;
    public int SelectedDataTabIndex
    {
        get => selectedDataTabIndex;
        set
        {
            if (value is < 0 or > 2) return;
            SetProperty(ref selectedDataTabIndex, value);
        }
    }
    public bool CanStart =>
        !isClosed && !IsStarting && !historyCleanup.IsBusy && !currentImportCleanupBusy &&
        !Import.IsBusy && string.IsNullOrEmpty(EvaluateConfiguration());
    public bool CanClearLogs =>
        !isClosed && !IsStarting && !Import.IsBusy &&
        !RunPanel.IsBusy && !RunPanel.Results.IsBusy && !RunPanel.Results.IsSaving &&
        Logs.Count > 0;
    public string ConfigurationMessage => EvaluateConfiguration();
    public IAsyncRelayCommand StartCommand { get; }
    public IRelayCommand ClearLogsCommand { get; }
    public IAsyncRelayCommand SelectSingleAccountCommand { get; }
    public IAsyncRelayCommand SelectReplicationCommand { get; }
    public IAsyncRelayCommand SelectDistributionCommand { get; }
    public IAsyncRelayCommand PickShareResultDirectoryCommand { get; }
    public IAsyncRelayCommand PickTargetDirectoryCommand { get; }
    public IAsyncRelayCommand ClearCurrentImportCommand { get; }
    public LocalHistoryCleanupPreview? TransferHistoryPreview => historyCleanup.Preview;
    public bool CanClearTransferHistory => historyCleanup.CanClear;
    public IAsyncRelayCommand ClearTransferHistoryCommand => historyCleanup.ClearCommand;
    public Task ClearTransferHistoryAsync(CancellationToken cancellationToken = default) => historyCleanup.ClearAsync(cancellationToken);
    public Task RefreshHistoryPreviewAsync(CancellationToken cancellationToken = default) => historyCleanup.RefreshPreviewAsync(cancellationToken);
    public bool CanClearCurrentImport =>
        importDraftCleanupService is not null && Import.SessionId is { } session &&
        Wizard.CurrentImportSessionId == session && !currentImportCleanupBusy &&
        !currentImportActivityGate.IsCleanupActive && !currentImportActivityGate.HasActiveActivities &&
        !isClosed && !IsStarting && !historyCleanup.IsBusy && Import.CanEdit && Wizard.CanEdit;

    public async Task ClearCurrentImportAsync(CancellationToken cancellationToken = default)
    {
        if (!CanClearCurrentImport || Import.SessionId is not { } capturedSession) return;
        var capturedRowCount = Import.TotalRows;
        var cleanupAcquired = false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        try
        {
            var confirmed = await currentImportCleanupInteraction
                .ConfirmAsync(capturedRowCount, linked.Token)
                .ConfigureAwait(true);
            if (!confirmed)
            {
                return;
            }
            if (!TryAcquireCurrentImportCleanup(capturedSession))
            {
                if (!isClosed) StatusMessage = "当前正在处理其他操作，请稍后重试";
                return;
            }
            cleanupAcquired = true;
            try
            {
                var result = await importDraftCleanupService!
                    .DeleteAsync(capturedSession, linked.Token)
                    .ConfigureAwait(true);
                if (isClosed) return;
                if (result.Disposition is LocalImportDraftCleanupDisposition.Deleted or
                    LocalImportDraftCleanupDisposition.AlreadyAbsent)
                {
                    if (Import.SessionId != capturedSession || Wizard.CurrentImportSessionId != capturedSession)
                    {
                        if (!isClosed) StatusMessage = "当前导入已变化，新的导入未受影响。";
                        return;
                    }
                    if (!Import.TryResetAfterSuccessfulCleanup(capturedSession) ||
                        !Wizard.TryDetachAfterSuccessfulCleanup(capturedSession))
                    {
                        if (!isClosed) StatusMessage = "当前导入已变化，新的导入未受影响。";
                        return;
                    }
                    createdBatchJobId = null;
                    createdBatchIsTerminal = false;
                    batchPendingRowIds.Clear();
                    expectedOperationsPerResource = 0;
                    simplifiedSelectedAccountId = null;
                    simplifiedSelectedAccountIds.Clear();
                    OnPropertyChanged(nameof(CreatedBatchJobId));
                    if (!isClosed) StatusMessage = "已清空当前导入，可重新选择 Excel";
                    RaiseStartState();
                    return;
                }
                if (linked.IsCancellationRequested) return;
                StatusMessage = result.Disposition switch
                {
                    LocalImportDraftCleanupDisposition.BatchConflict =>
                        "当前导入已关联转存任务，请等待任务结束后使用“清空转存历史”。",
                    LocalImportDraftCleanupDisposition.LocalDependencyConflict =>
                        "另一个本地导入依赖当前数据，请先检查并保留相关导入。",
                    _ => "无法清空当前导入，请稍后重试。",
                };
            }
            finally
            {
                ReleaseCurrentImportCleanup();
            }
        }
        catch (OperationCanceledException)
        {
            if (cleanupAcquired && !isClosed) StatusMessage = "已取消清空当前导入。";
        }
        catch
        {
            if (!isClosed) StatusMessage = "无法清空当前导入，请稍后重试。";
        }
    }

    private bool TryAcquireCurrentImportCleanup(Guid capturedSession)
    {
        var lease = currentImportActivityGate.TryEnterCleanup(() =>
        {
            lock (currentImportCleanupSync)
                return !currentImportCleanupBusy && !isClosed && !IsStarting && !historyCleanup.IsBusy &&
                    Import.SessionId == capturedSession && Wizard.CurrentImportSessionId == capturedSession &&
                    !Import.IsBusy && !Wizard.IsBusy && Import.CanEdit && Wizard.CanEdit;
        });
        if (lease is null) return false;
        lock (currentImportCleanupSync)
        {
            currentImportCleanupLease = lease;
            currentImportCleanupBusy = true;
            Import.SetExternalCleanupBusy(true);
            Wizard.SetExternalCleanupBusy(true);
        }
        NotifyCurrentImportCleanupState();
        return true;
    }

    private void ReleaseCurrentImportCleanup()
    {
        IDisposable? lease;
        lock (currentImportCleanupSync)
        {
            if (!currentImportCleanupBusy) return;
            currentImportCleanupBusy = false;
            Import.SetExternalCleanupBusy(false);
            Wizard.SetExternalCleanupBusy(false);
            lease = currentImportCleanupLease;
            currentImportCleanupLease = null;
        }
        lease?.Dispose();
        NotifyCurrentImportCleanupState();
    }

    private void OnCurrentImportActivityChanged(object? sender, EventArgs e)
    {
        if (isClosed) return;
        OnPropertyChanged(nameof(CanClearCurrentImport));
        ClearCurrentImportCommand.NotifyCanExecuteChanged();
    }

    private void NotifyCurrentImportCleanupState()
    {
        OnPropertyChanged(nameof(CanClearCurrentImport));
        OnPropertyChanged(nameof(CanEditConfiguration));
        ClearCurrentImportCommand.NotifyCanExecuteChanged();
        RaiseStartState();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (isClosed) return;
        await RunPanel.InitializeAsync(cancellationToken).ConfigureAwait(true);
        await RefreshHistoryPreviewAsync(cancellationToken).ConfigureAwait(true);
    }

    private void OnHistoryCleanupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CanClearTransferHistory));
        if (e.PropertyName == nameof(LocalHistoryCleanupViewModel.Preview))
            OnPropertyChanged(nameof(TransferHistoryPreview));
        if (e.PropertyName != nameof(LocalHistoryCleanupViewModel.IsBusy)) return;
        RunPanel.IsHistoryCleanupBusy = historyCleanup.IsBusy;
        RaiseStartState();
        SelectSingleAccountCommand.NotifyCanExecuteChanged();
        SelectReplicationCommand.NotifyCanExecuteChanged();
        SelectDistributionCommand.NotifyCanExecuteChanged();
        PickShareResultDirectoryCommand.NotifyCanExecuteChanged();
        PickTargetDirectoryCommand.NotifyCanExecuteChanged();
    }

    public void ApplyImportedDraft(ImportDraftReady draft)
    {
        if (isClosed) return;
        SelectedDataTabIndex = 0;
        if (Import.SessionId != draft.SessionId) Import.AttachSession(draft.SessionId);
        if (Wizard.CurrentImportSessionId == draft.SessionId)
        {
            Wizard.RefreshImportSnapshot(
                draft.Statistics.Ready,
                draft.Statistics.Conflict,
                draft.Statistics.NeedsResolution,
                draft.Statistics.HasExcelAccountColumn,
                draft.Statistics.UnresolvedAccountMappings,
                draft.Statistics.MappedAccountIds);
            StatusMessage = draft.Statistics.Conflict + draft.Statistics.NeedsResolution > 0
                ? "导入内容中还有冲突或待处理记录，请先修正或排除。"
                : "导入数据已更新。";
            RaiseStartState();
            return;
        }
        if (createdBatchJobId is not null)
        {
            createdBatchJobId = null;
            batchPendingRowIds.Clear();
            createdBatchIsTerminal = false;
            OnPropertyChanged(nameof(CreatedBatchJobId));
        }
        Wizard.Initialize(new(
            draft.SessionId,
            $"{options.JobNamePrefix} {DateTime.Now:yyyy-MM-dd HH:mm}",
            draft.Statistics.Ready,
            draft.Statistics.HasExcelAccountColumn,
            draft.Statistics.UnresolvedAccountMappings,
            draft.Statistics.Conflict,
            draft.Statistics.NeedsResolution,
            options.DefaultTargetDirectory,
            draft.Statistics.MappedAccountIds));
        Wizard.AcceptanceOnly = remoteWriteMode == RemoteWriteExecutionMode.AcceptanceOnly;
        Wizard.AcceptanceResultDirectory = ShareResultDirectory;
        Wizard.ResultDirectory = ShareResultDirectory;
        if (IsSimplifiedDirectMode)
        {
            ConfigureSimplifiedDirectMode();
        }
        else if (Wizard.AcceptanceOnly)
        {
            AutoShare = true;
            Wizard.QuotaBasis = QuotaBasis.TaskCount;
        }
        if (!IsSimplifiedDirectMode)
        {
            foreach (var account in Wizard.Accounts) account.ShareEnabled = AutoShare;
            AccountMode = Wizard.Accounts.Count == 1 ? TransferAccountMode.SingleAccount : null;
        }
        StatusMessage = draft.Statistics.Conflict + draft.Statistics.NeedsResolution > 0
            ? "导入内容中还有冲突或待处理记录，请先修正或排除。"
            : "导入完成，请选择参与账号和保存目录。";
        RaiseStartState();
    }

    public async Task SelectAccountModeAsync(
        TransferAccountMode mode,
        CancellationToken cancellationToken = default)
    {
        if (!CanEditConfiguration) return;
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        var requestVersion = Interlocked.Increment(ref modeSelectionVersion);
        var previous = Interlocked.Exchange(ref modeSelectionCancellation, request);
        previous?.Cancel();
        var transferMode = mode == TransferAccountMode.MultiAccountReplication
            ? TransferMode.Replication
            : TransferMode.Distribution;
        var gateHeld = false;
        try
        {
            try
            {
                await modeSelectionGate.WaitAsync(request.Token).ConfigureAwait(true);
                gateHeld = true;
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested)
            {
                if (!isClosed) NotifyAccountModePresentation();
                return;
            }

            if (request.IsCancellationRequested || isClosed ||
                requestVersion != Volatile.Read(ref modeSelectionVersion))
            {
                if (!isClosed) NotifyAccountModePresentation();
                return;
            }

            if (Wizard.TransferMode == transferMode)
            {
                CompleteAccountModeSelection(mode);
                return;
            }

            if (IsSimplifiedDirectMode) Interlocked.Increment(ref accountSynchronizationSuppression);
            try
            {
                await Wizard.SelectModeAsync(transferMode, request.Token).ConfigureAwait(true);
            }
            finally
            {
                if (IsSimplifiedDirectMode) Interlocked.Decrement(ref accountSynchronizationSuppression);
            }

            if (request.IsCancellationRequested || isClosed ||
                requestVersion != Volatile.Read(ref modeSelectionVersion) ||
                Wizard.TransferMode != transferMode)
            {
                if (!isClosed) NotifyAccountModePresentation();
                return;
            }

            CompleteAccountModeSelection(mode);
        }
        finally
        {
            if (gateHeld) modeSelectionGate.Release();
            Interlocked.CompareExchange(ref modeSelectionCancellation, null, request);
            request.Dispose();
        }
    }

    public async Task PickShareResultDirectoryAsync(CancellationToken cancellationToken = default)
    {
        if (!AutoShare || isClosed || IsStarting) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        var selected = await resultDirectoryDialog.PickDestinationDirectoryAsync(linked.Token).ConfigureAwait(true);
        if (linked.IsCancellationRequested || isClosed || string.IsNullOrWhiteSpace(selected)) return;
        ShareResultDirectory = selected;
    }

    public async Task PickTargetDirectoryAsync(CancellationToken cancellationToken = default)
    {
        if (isClosed || IsStarting) return;
        var selectedAccounts = Wizard.Accounts.Where(account => account.IsSelected).ToArray();
        if (selectedAccounts.Length == 0)
        {
            StatusMessage = "请先选择参与账号。";
            return;
        }
        if (targetDirectoryPicker is null)
        {
            StatusMessage = "目录选择暂不可用。";
            return;
        }

        var participantIds = selectedAccounts
            .Select(account => account.AccountId)
            .ToHashSet();
        var browsingAccountId = selectedAccounts[0].AccountId;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        try
        {
            var chosen = await targetDirectoryPicker.PickAsync(
                    browsingAccountId,
                    Wizard.DefaultTargetPath,
                    linked.Token)
                .ConfigureAwait(true);
            if (linked.IsCancellationRequested || isClosed || string.IsNullOrWhiteSpace(chosen)) return;

            var canonicalPath = new RemotePath(chosen).Value;
            var nonParticipantPaths = Wizard.Accounts
                .Where(account => !participantIds.Contains(account.AccountId))
                .Select(account => (Account: account, account.DefaultTargetPath))
                .ToArray();
            Wizard.DefaultTargetPath = canonicalPath;
            foreach (var account in Wizard.Accounts.Where(
                         account => participantIds.Contains(account.AccountId)))
                account.DefaultTargetPath = canonicalPath;
            foreach (var (account, originalPath) in nonParticipantPaths)
                account.DefaultTargetPath = originalPath;
            StatusMessage = "目标目录已更新。";
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch
        {
            if (!isClosed) StatusMessage = "目录选择失败，请稍后重试。";
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (isClosed || IsStarting || currentImportCleanupBusy || createdBatchJobId is not null || Import.IsBusy) return;
        using var activity = currentImportActivityGate.TryEnterActivity();
        if (activity is null) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        IsStarting = true;
        StatusMessage = "正在准备转存任务……";
        try
        {
            var saved = await Import.SaveCurrentRowEditAsync(linked.Token).ConfigureAwait(true);
            if (!IsAlive(linked))
            {
                ReportCancellationIfWorkspaceOpen();
                return;
            }
            if (!saved)
            {
                StatusMessage = "当前修改未保存，转存未开始。";
                return;
            }
            if (Import.IsBusy)
            {
                StatusMessage = "导入数据仍在处理中，转存未开始。";
                return;
            }

            var validation = EvaluateConfiguration();
            if (validation.Length != 0)
            {
                StatusMessage = validation;
                return;
            }
            if (!await IsRemoteOperationAuthorizedAsync(linked.Token).ConfigureAwait(true)) return;
            var resultDirectory = AutoShare ? ShareResultDirectory : null;

            Wizard.UseBulkPrecheck = adapterAvailability.RequiresBulkPrecheckBeforeStart;
            if (Wizard.UseBulkPrecheck)
            {
                var selectedAccount = Wizard.Accounts.First(account => account.IsSelected);
                Wizard.PrecheckAccountId = selectedAccount.AccountId;
                StatusMessage = "正在检查链接和账号配置……";
                var precheck = await Wizard.RunBackgroundPrecheckAsync(linked.Token).ConfigureAwait(true);
                if (!IsAlive(linked))
                {
                    ReportCancellationIfWorkspaceOpen();
                    return;
                }
                if (precheck.Status == WizardPrecheckStatus.Unavailable)
                {
                    StatusMessage = "链接检查暂不可用，请稍后重试。";
                    return;
                }
                if (precheck.Status is WizardPrecheckStatus.PausedForLogin or WizardPrecheckStatus.PausedForVerification)
                {
                    StatusMessage = precheck.Status switch
                    {
                        WizardPrecheckStatus.PausedForLogin => "请先完成账号登录，再重新开始转存。",
                        WizardPrecheckStatus.PausedForVerification => "请先完成百度安全验证，再重新开始转存。",
                        _ => throw new ArgumentOutOfRangeException(nameof(precheck)),
                    };
                    return;
                }
                if (precheck.HasActionableFailures)
                {
                    StatusMessage = BuildActionableFailureMessage(precheck);
                    return;
                }
            }

            if (resultDirectory is not null)
            {
                try
                {
                    StatusMessage = "正在准备本机结果目录……";
                    await resultDirectoryPreparer.EnsureExistsAsync(resultDirectory, linked.Token)
                        .ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    StatusMessage = "本机结果目录无法创建或使用，请重新选择。";
                    return;
                }
            }

            StatusMessage = "正在准备转存任务……";
            await Wizard.PrepareConfirmationAsync(linked.Token).ConfigureAwait(true);
            if (!IsAlive(linked))
            {
                ReportCancellationIfWorkspaceOpen();
                return;
            }
            if (Wizard.ConfirmationToken is null)
            {
                StatusMessage = string.IsNullOrWhiteSpace(Wizard.ValidationMessage)
                    ? "无法准备转存，请检查链接、账号和保存目录。"
                    : MapWizardValidationMessage(Wizard.ValidationMessage);
                return;
            }

            // BatchDraftValidator skips precheck failures among Ready rows; other validation
            // failures reject the whole confirmation, so no row is marked before it succeeds.
            var includedRowIds = Import.Rows.Where(row => row.Disposition == "Ready" && row.PrecheckStatus != "Failed")
                .Select(row => row.RowId).ToArray();
            var expectedOperations = Wizard.TransferMode == TransferMode.Replication
                ? Wizard.Accounts.Count(account => account.IsSelected) : 1;
            var batchJobId = await Wizard.ConfirmPreparedFromUserIntentAsync(linked.Token).ConfigureAwait(true);
            if (batchJobId is not { } batch)
            {
                if (IsAlive(linked))
                {
                    StatusMessage = Wizard.LastConfirmationWasDeclined
                        ? "已取消转存。"
                        : string.IsNullOrWhiteSpace(Wizard.ValidationMessage)
                            ? "转存任务未创建，请确认后重试。"
                            : MapWizardValidationMessage(Wizard.ValidationMessage);
                }
                return;
            }
            batchPendingRowIds.UnionWith(includedRowIds);
            expectedOperationsPerResource = expectedOperations;
            createdBatchIsTerminal = false;
            RecordCreatedBatch(batch);
            StatusMessage = "任务已创建，正在打开进度与结果……";
            await CompletePostCommitAsync(batch, resultDirectory).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            ReportCancellationIfWorkspaceOpen();
        }
        catch (Exception)
        {
            if (IsAlive(linked)) StatusMessage = createdBatchJobId is null
                ? "转存未能开始，请检查配置后重试。"
                : "批次已创建，可稍后从进度与结果中恢复查看。";
        }
        finally
        {
            if (!isClosed) IsStarting = false;
        }
    }

    private async Task<bool> IsRemoteOperationAuthorizedAsync(CancellationToken cancellationToken)
    {
        if (licenseGate is null) return true;
        var result = await licenseGate
            .AuthorizeAsync(LicenseOperationKind.Transfer, cancellationToken)
            .ConfigureAwait(true);
        if (result.IsAuthorized) return true;
        StatusMessage = "软件尚未激活，联网功能暂不可用。";
        return false;
    }

    public void Close()
    {
        if (isClosed) return;
        historyCleanup.Close();
        isClosed = true;
        Interlocked.Increment(ref modeSelectionVersion);
        Interlocked.Exchange(ref modeSelectionCancellation, null)?.Cancel();
        lifetime.Cancel();
        Import.DraftReady -= OnDraftReady;
        Import.PropertyChanged -= OnComposedPropertyChanged;
        Import.PropertyChanged -= OnStatusSourcePropertyChanged;
        Wizard.PropertyChanged -= OnComposedPropertyChanged;
        RunPanel.PropertyChanged -= OnStatusSourcePropertyChanged;
        RunPanel.BatchTerminal -= OnBatchTerminal;
        RunPanel.Results.PropertyChanged -= OnStatusSourcePropertyChanged;
        RunPanel.Results.ManualRefreshCompleted -= OnManualResultsRefreshCompleted;
        RunPanel.Results.Items.CollectionChanged -= OnResultItemsChanged;
        Import.Rows.CollectionChanged -= OnPendingRowsChanged;
        ((INotifyCollectionChanged)Logs).CollectionChanged -= OnLogsChanged;
        currentImportActivityGate.StateChanged -= OnCurrentImportActivityChanged;
        Import.Close();
        RunPanel.Close();
        OnPropertyChanged(nameof(IsClosed));
        PickTargetDirectoryCommand.NotifyCanExecuteChanged();
        RaiseStartState();
    }

    public void Dispose()
    {
        Close();
        historyCleanup.PropertyChanged -= OnHistoryCleanupPropertyChanged;
        historyCleanup.Dispose();
        RunPanel.Dispose();
        Import.Dispose();
        lifetime.Dispose();
    }

    private void ClearLogs()
    {
        if (!CanClearLogs) return;
        logBuffer.Clear();
        if (!SetProperty(ref statusMessage, "运行日志已清空。", nameof(StatusMessage)))
            OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(UserFacingStatusMessage));
    }

    private string EvaluateConfiguration()
    {
        if (createdBatchJobId is not null) return "当前导入数据的批次已创建。";
        if (Import.SessionId is not { } session || Wizard.CurrentImportSessionId != session)
            return "请先导入至少一条有效链接。";
        if (Wizard.ReadyResourceCount <= 0) return "没有可转存的有效链接。";
        if (Wizard.Accounts.Count == 0) return "请先添加可用百度账号。";
        var selected = Wizard.Accounts.Where(account => account.IsSelected).ToArray();
        if (IsSimplifiedDirectMode &&
            (!AutoShare || !Wizard.DirectBackground || Wizard.QuotaBasis != QuotaBasis.TaskCount ||
             selected.Any(account => !account.ShareEnabled) ||
             Wizard.DuplicatePolicy != DuplicatePolicy.Rename ||
             Wizard.ExistingSharePolicy != "Regenerate" ||
             Wizard.ExtractionCodeBehavior != ShareExtractionCodeBehavior.Random ||
             Wizard.MaxConcurrency is < 1 or > 3))
            return "请确认参与账号、保存目录和结果目录。";
        if (remoteWriteMode == RemoteWriteExecutionMode.AcceptanceOnly &&
             (Wizard.ReadyResourceCount != 1 || selected.Length != 1 ||
             AccountMode != TransferAccountMode.SingleAccount || !AutoShare ||
             selected[0].ExecutionProfile.Engine != BaiduExecutionEngine.ManagedChromiumV1))
        {
            var engine = selected.Length == 1
                ? selected[0].ExecutionProfile.Engine.ToString()
                : "未选择";
            return $"现场验收配置未满足：有效链接 {Wizard.ReadyResourceCount}，参与账号 {selected.Length}，" +
                   $"模式 {AccountMode?.ToString() ?? "未选择"}，自动分享 {(AutoShare ? "已开启" : "未开启")}，" +
                   $"账号引擎 {engine}。";
        }
        if (selected.Length == 0) return "请至少选择一个参与账号。";
        if (AccountMode is null) return "请选择单账号、多账号复制或多账号分流。";
        if (AccountMode == TransferAccountMode.SingleAccount && selected.Length != 1)
            return "单账号模式只能选择一个参与账号。";
        if (AccountMode is TransferAccountMode.MultiAccountReplication or TransferAccountMode.MultiAccountDistribution &&
            selected.Length < 2)
            return "多账号模式至少需要两个参与账号。";
        if (AccountMode == TransferAccountMode.MultiAccountReplication && Wizard.TransferMode != TransferMode.Replication)
            return "账号模式尚未配置完成。";
        if (AccountMode != TransferAccountMode.MultiAccountReplication && Wizard.TransferMode != TransferMode.Distribution)
            return "账号模式尚未配置完成。";
        if (selected.Any(account => string.IsNullOrWhiteSpace(account.DefaultTargetPath)))
            return "每个参与账号都必须填写百度保存目录。";
        if (AutoShare && !IsValidLocalDirectory(ShareResultDirectory))
            return "自动分享需要选择本机结果目录。";
        if (AutoShare && ExtractionCodeBehavior == ShareExtractionCodeBehavior.Fixed &&
            !PermanentShareRequest.IsValidFixedExtractionCode(FixedExtractionCode))
            return "固定提取码必须是 4 位英文字母或数字。";
        if (!Wizard.CanContinue) return Wizard.ValidationMessage.Length == 0
            ? "请检查账号模式、分配数量和保存目录。"
            : Wizard.ValidationMessage;
        if (!Wizard.CanStartTransfers) return "百度转存能力尚未通过验证，当前不能开始。";
        return string.Empty;
    }

    private static bool IsValidLocalDirectory(string value) =>
        !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value);

    private static string BuildActionableFailureMessage(WizardPrecheckResult result)
    {
        var parts = new List<string>();
        if (result.InvalidLinkCount > 0) parts.Add($"无效链接 {result.InvalidLinkCount:N0} 条");
        if (result.MissingExtractionCodeCount > 0) parts.Add($"缺少提取码 {result.MissingExtractionCodeCount:N0} 条");
        if (result.AccountIssueCount > 0) parts.Add($"账号问题 {result.AccountIssueCount:N0} 项");
        if (result.ConflictCount > 0) parts.Add($"冲突 {result.ConflictCount:N0} 条");
        if (result.OtherFailureCount > 0) parts.Add($"暂时无法确认 {result.OtherFailureCount:N0} 条");
        if (!result.SnapshotIsConsistent) parts.Add("链接检查状态已变化");
        return $"请先处理：{string.Join("、", parts)}。";
    }

    private bool IsAlive(CancellationTokenSource operation) =>
        !isClosed && !lifetime.IsCancellationRequested && !operation.IsCancellationRequested;

    private void ReportCancellationIfWorkspaceOpen()
    {
        if (!isClosed && !lifetime.IsCancellationRequested)
            StatusMessage = "已取消转存。";
    }

    private void RecordCreatedBatch(Guid batchJobId)
    {
        createdBatchJobId = batchJobId;
        if (!isClosed)
        {
            foreach (var row in Import.Rows.Where(row => row.Disposition == "Ready" && batchPendingRowIds.Contains(row.RowId)))
                row.RuntimeStatusText = "已进入任务";
            SelectedDataTabIndex = 1;
            RunPanel.PresentBatch(batchJobId);
            OnPropertyChanged(nameof(CreatedBatchJobId));
            RaiseStartState();
        }
    }

    private async Task CompletePostCommitAsync(Guid batchJobId, string? resultDirectory)
    {
        var destinationSaved = resultDirectory is null;
        if (resultDirectory is not null)
        {
            destinationSaved = await TryPostCommitAsync(
                token => CompletedAsync(destinationSink.SetAsync(
                    new TransferResultDestination(batchJobId, resultDirectory), token)),
                static _ => true).ConfigureAwait(true);
        }
        ManagedTransferAcceptanceRunOutcome? acceptanceOutcome = null;
        if (remoteWriteMode == RemoteWriteExecutionMode.AcceptanceOnly)
        {
            if (acceptanceRunAsync is null)
            {
                StatusMessage = "批次已创建，但现场执行器不可用；未进行任何远端操作。";
                return;
            }
            StatusMessage = "正在执行这一条转存、分享并保存结果，请勿关闭浏览器。";
            acceptanceOutcome = await acceptanceRunAsync(batchJobId, lifetime.Token).ConfigureAwait(true);
        }
        var handedOff = await TryPostCommitAsync(
            token => handoffBatchAsync(batchJobId, token),
            static outcome => outcome.Succeeded).ConfigureAwait(true);
        if (isClosed) return;
        if (acceptanceOutcome is not null)
        {
            StatusMessage = acceptanceOutcome.Disposition switch
            {
                ManagedTransferAcceptanceRunDisposition.Completed when destinationSaved && handedOff =>
                    "这一条转存、分享和结果保存均已完成。",
                ManagedTransferAcceptanceRunDisposition.Completed =>
                    "远端操作已完成；结果目录或结果页面暂未完成，请稍后恢复查看。",
                ManagedTransferAcceptanceRunDisposition.AwaitingTransferResolution =>
                    "转存结果尚未确认；为避免重复写入，系统不会自动重试，请从结果中继续确认。",
                ManagedTransferAcceptanceRunDisposition.AwaitingFinalization =>
                    "分享结果尚未确认；为避免重复分享，系统不会自动重试，请从结果中继续确认。",
                ManagedTransferAcceptanceRunDisposition.AwaitingExport =>
                    "转存和分享已完成，本机结果文件尚待保存。",
                _ => "未取得这一条任务的独占执行权，未进行远端操作。",
            };
            return;
        }
        StatusMessage = (destinationSaved, handedOff) switch
        {
            (true, true) => "转存批次已创建，可在下方查看进度与结果。",
            (false, true) => "批次已创建，结果目录暂未保存。",
            (true, false) => "批次已创建，可稍后从进度与结果中恢复查看。",
            _ => "批次已创建，结果目录暂未保存；可稍后从进度与结果中恢复查看。",
        };
    }

    private async Task<bool> TryPostCommitAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Func<T, bool> isSuccessful)
    {
        using var completion = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        Task<T>? pending = null;
        try
        {
            pending = operation(completion.Token);
            var result = await pending.WaitAsync(
                TimeSpan.FromSeconds(options.PostCommitTimeoutSeconds),
                lifetime.Token).ConfigureAwait(true);
            return isSuccessful(result);
        }
        catch (TimeoutException)
        {
            completion.Cancel();
            ObserveLateFault(pending);
            return false;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            completion.Cancel();
            ObserveLateFault(pending);
            return false;
        }
        catch (Exception) { return false; }
    }

    private static async Task<bool> CompletedAsync(Task operation)
    {
        await operation.ConfigureAwait(false);
        return true;
    }

    private static void ObserveLateFault(Task? task)
    {
        if (task is null) return;
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void OnDraftReady(object? sender, ImportDraftReady draft) => ApplyImportedDraft(draft);

    private void OnComposedPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, Wizard)) SynchronizeAccountState();
        OnPropertyChanged(nameof(CanEditConfiguration));
        RaiseStartState();
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => NotifyClearLogsState();

    private void NotifyClearLogsState()
    {
        historyCleanup.NotifyAvailabilityChanged();
        OnPropertyChanged(nameof(CanClearLogs));
        ClearLogsCommand.NotifyCanExecuteChanged();
    }

    private void OnBatchTerminal(object? sender, WorkspaceBatchTerminalEvent terminal)
    {
        if (isClosed) return;
        _ = RefreshHistoryPreviewAsync();
        if (terminal.BatchJobId == createdBatchJobId)
        {
            createdBatchIsTerminal = true;
            UpdatePendingRuntimeStatuses();
        }
        var message = terminal is { CompleteSuccessCount: { } succeeded, TerminalProblemCount: { } problems }
            ? $"转存任务已完成：完成 {succeeded:N0} 条，失败 {problems:N0} 条。"
            : "转存任务已完成：统计暂未读取。";
        var level = terminal switch
        {
            { CompleteSuccessCount: not null, TerminalProblemCount: > 0 } => WorkspaceLogLevel.Error,
            { CompleteSuccessCount: not null, TerminalProblemCount: 0 } => WorkspaceLogLevel.Success,
            _ => WorkspaceLogLevel.Warning,
        };
        logBuffer.AppendSafe(message, level);
        _ = ShowTerminalMessageAsync(message);
    }

    private async Task ShowTerminalMessageAsync(string message)
    {
        if (isClosed || dialogService is null) return;
        try
        {
            await dialogService.ShowMessageAsync("转存任务已完成", message, lifetime.Token).ConfigureAwait(true);
        }
        catch (Exception) { }
    }

    private void OnResultItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (RunPanel.Results.BatchJobId == createdBatchJobId) UpdatePendingRuntimeStatuses();
    }

    private void OnPendingRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdatePendingRuntimeStatuses();

    private void UpdatePendingRuntimeStatuses()
    {
        if (isClosed || createdBatchJobId is null) return;
        IReadOnlyList<JobResultRow> results = RunPanel.Results.BatchJobId == createdBatchJobId
            ? RunPanel.Results.Items.ToArray() : [];
        foreach (var row in Import.Rows.Where(row => row.Disposition == "Ready" && batchPendingRowIds.Contains(row.RowId)))
            row.RuntimeStatusText = PendingRowRuntimeStatus.Evaluate(
                row.RowId, results, expectedOperationsPerResource, createdBatchIsTerminal);
    }

    private void OnStatusSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (isClosed) return;
        historyCleanup.NotifyAvailabilityChanged();
        if ((ReferenceEquals(sender, RunPanel) && e.PropertyName == nameof(WorkspaceRunPanelViewModel.IsBusy) && !RunPanel.IsBusy) ||
            (ReferenceEquals(sender, RunPanel.Results) && e.PropertyName == nameof(JobResultsViewModel.IsSaving) && !RunPanel.Results.IsSaving))
            _ = RefreshHistoryPreviewAsync();
        if ((ReferenceEquals(sender, RunPanel) && e.PropertyName == nameof(WorkspaceRunPanelViewModel.IsBusy)) ||
            (ReferenceEquals(sender, RunPanel.Results) &&
             e.PropertyName is nameof(JobResultsViewModel.IsBusy) or nameof(JobResultsViewModel.IsSaving)))
            NotifyClearLogsState();
        if (ReferenceEquals(sender, RunPanel.Results) &&
            e.PropertyName == nameof(JobResultsViewModel.BatchJobId) &&
            RunPanel.Results.BatchJobId != Guid.Empty)
        {
            SelectedDataTabIndex = 1;
            return;
        }
        if (e.PropertyName != nameof(StatusMessage)) return;
        if (ReferenceEquals(sender, Import)) AppendPresentationLog(Import.StatusMessage);
        else if (ReferenceEquals(sender, RunPanel)) AppendPresentationLog(RunPanel.StatusMessage);
        else if (ReferenceEquals(sender, RunPanel.Results)) AppendPresentationLog(RunPanel.Results.StatusMessage);
    }

    private void AppendPresentationLog(string? message) =>
        logBuffer.AppendSafe(MapPresentationLogMessage(message));

    private void OnManualResultsRefreshCompleted(object? sender, EventArgs e)
    {
        if (!isClosed) _ = RefreshHistoryPreviewAsync();
    }

    private static string? MapPresentationLogMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return message;
        if (ContainsAny(message, "现场执行器", "adapter", "helper"))
            return "转存服务暂不可用，请稍后重试。";
        if (ContainsAny(message, "现场验收", "账号引擎", "ManagedChromium", "NotValidated", "acceptance"))
            return "当前账号暂不可执行，请检查账号状态。";
        return message;
    }

    private static string MapWizardValidationMessage(string message)
    {
        if (ContainsAny(message, "result directory"))
            return "本机结果目录无效，请重新选择。";
        if (ContainsAny(message, "Direct-background"))
            return "批量转存配置不完整，请检查模式、同名文件处理和自动分享设置。";
        if (ContainsAny(message, "quota", "success target total", "locked assignment count"))
            return "账号任务配额与有效链接数量不匹配，请重新分配。";
        if (ContainsAny(message, "missing its resolved output title"))
            return "部分分享结果缺少标题，请检查导入数据后再试。";
        if (ContainsAny(message, "conflicting", "unresolved", "duplicate normalized resource"))
            return "导入数据仍有冲突或未解决项，请先处理后再试。";
        if (ContainsAny(message, "account is unavailable", "account outside the confirmed batch"))
            return "参与账号已失效或不可用，请重新选择账号。";
        if (ContainsAny(message,
                "import draft does not exist",
                "import draft is not ready",
                "import counters no longer match",
                "missing its normalized identity",
                "no included ready resources"))
            return "导入数据已失效或不存在，请重新导入后再试。";
        if (ContainsAny(message, "confirmed target path is invalid"))
            return "百度保存目录无效，请重新填写。";
        if (message.Any(character => character is >= '\u4e00' and <= '\u9fff'))
            return message;
        return "无法准备转存，请检查链接、账号和保存目录后重试。";
    }

    private static bool ContainsAny(string message, params string[] values) =>
        values.Any(value => message.Contains(value, StringComparison.OrdinalIgnoreCase));

    private void SynchronizeAccountState()
    {
        if (synchronizingAccounts || Volatile.Read(ref accountSynchronizationSuppression) > 0) return;
        synchronizingAccounts = true;
        try
        {
            if (IsSimplifiedDirectMode)
            {
                SynchronizeSimplifiedAccountSelection();
                return;
            }
            foreach (var account in Wizard.Accounts)
                if (account.ShareEnabled != AutoShare) account.ShareEnabled = AutoShare;
            if (Wizard.TransferMode is null) AccountMode = null;
            else if (Wizard.Accounts.Count == 1 && Wizard.Accounts[0].IsSelected)
                AccountMode = TransferAccountMode.SingleAccount;
        }
        finally
        {
            synchronizingAccounts = false;
        }
    }

    private void NotifyAccountModePresentation()
    {
        OnPropertyChanged(nameof(AccountModeText));
        OnPropertyChanged(nameof(IsSingleAccountMode));
        OnPropertyChanged(nameof(IsReplicationMode));
        OnPropertyChanged(nameof(IsDistributionMode));
    }

    private void CompleteAccountModeSelection(TransferAccountMode mode)
    {
        AccountMode = mode;
        SynchronizeAccountState();
        StatusMessage = mode switch
        {
            TransferAccountMode.SingleAccount => "已选择单账号转存。",
            TransferAccountMode.MultiAccountReplication => "已选择多账号复制。",
            _ => "已选择多账号分流。",
        };
    }

    private void SynchronizeSimplifiedAccountSelection()
    {
        var selected = Wizard.Accounts.Where(account => account.IsSelected).ToArray();
        var newlySelected = selected
            .Where(account => !simplifiedSelectedAccountIds.Contains(account.AccountId))
            .ToArray();
        if (newlySelected.Length > 0)
            simplifiedSelectedAccountId = newlySelected[^1].AccountId;
        else if (simplifiedSelectedAccountId is not { } latestId ||
                 selected.All(account => account.AccountId != latestId))
            simplifiedSelectedAccountId = selected.LastOrDefault()?.AccountId;

        if (selected.Length == 0)
        {
            simplifiedSelectedAccountId = null;
            simplifiedSelectedAccountIds.Clear();
            foreach (var account in Wizard.Accounts)
            {
                if (account.TargetItemCount != 0) account.TargetItemCount = 0;
                if (account.ShareEnabled) account.ShareEnabled = false;
            }
            return;
        }

        AccountMode ??= TransferAccountMode.SingleAccount;
        Wizard.QuotaBasis = QuotaBasis.TaskCount;
        if (AccountMode == TransferAccountMode.SingleAccount)
        {
            var active = selected.First(account => account.AccountId == simplifiedSelectedAccountId);
            foreach (var account in Wizard.Accounts)
            {
                var isActive = account.AccountId == active.AccountId;
                if (account.IsSelected != isActive) account.IsSelected = isActive;
                if (account.TargetItemCount != (isActive ? Wizard.ReadyResourceCount : 0))
                    account.TargetItemCount = isActive ? Wizard.ReadyResourceCount : 0;
                if (account.ShareEnabled != (isActive && AutoShare))
                    account.ShareEnabled = isActive && AutoShare;
            }

            if (Wizard.TransferMode != TransferMode.Distribution)
                Wizard.SelectMode(TransferMode.Distribution);
        }
        else if (AccountMode == TransferAccountMode.MultiAccountReplication)
        {
            foreach (var account in Wizard.Accounts)
            {
                var isSelected = account.IsSelected;
                if (account.TargetItemCount != (isSelected ? Wizard.ReadyResourceCount : 0))
                    account.TargetItemCount = isSelected ? Wizard.ReadyResourceCount : 0;
                if (account.ShareEnabled != (isSelected && AutoShare))
                    account.ShareEnabled = isSelected && AutoShare;
            }
        }
        else
        {
            var quotient = Wizard.ReadyResourceCount / selected.Length;
            var remainder = Wizard.ReadyResourceCount % selected.Length;
            var selectedIndex = 0;
            foreach (var account in Wizard.Accounts)
            {
                var target = account.IsSelected
                    ? quotient + (selectedIndex++ < remainder ? 1 : 0)
                    : 0;
                if (account.TargetItemCount != target) account.TargetItemCount = target;
                if (account.ShareEnabled != (account.IsSelected && AutoShare))
                    account.ShareEnabled = account.IsSelected && AutoShare;
            }
        }

        simplifiedSelectedAccountIds.Clear();
        foreach (var account in Wizard.Accounts.Where(account => account.IsSelected))
            simplifiedSelectedAccountIds.Add(account.AccountId);
    }

    private void ConfigureSimplifiedDirectMode()
    {
        Wizard.AcceptanceOnly = false;
        Wizard.DirectBackground = true;
        Wizard.MaxConcurrency = options.DefaultMaxConcurrency;
        Wizard.DuplicatePolicy = DuplicatePolicy.Rename;
        Wizard.ExistingSharePolicy = "Regenerate";
        Wizard.ExtractionCodeBehavior = ShareExtractionCodeBehavior.Random;
        Wizard.QuotaBasis = QuotaBasis.TaskCount;
        AutoShare = true;
        if (!string.IsNullOrWhiteSpace(options.DefaultResultDirectory) &&
            string.IsNullOrWhiteSpace(ShareResultDirectory))
            ShareResultDirectory = Path.GetFullPath(options.DefaultResultDirectory);
        Wizard.ResultDirectory = ShareResultDirectory;
        SynchronizeAccountState();
    }

    private void RaiseStartState()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(ConfigurationMessage));
        OnPropertyChanged(nameof(CanEditConfiguration));
        OnPropertyChanged(nameof(CanClearCurrentImport));
        StartCommand.NotifyCanExecuteChanged();
        ClearCurrentImportCommand.NotifyCanExecuteChanged();
        NotifyClearLogsState();
    }
}
