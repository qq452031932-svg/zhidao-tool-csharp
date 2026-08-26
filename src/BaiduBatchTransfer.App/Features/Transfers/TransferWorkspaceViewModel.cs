using System.ComponentModel;
using System.IO;
using BaiduBatchTransfer.App.Features.Common;
using BaiduBatchTransfer.App.Features.Imports;
using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaiduBatchTransfer.App.Features.Transfers;

public sealed class TransferWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly IResultSaveDialog resultDirectoryDialog;
    private readonly ITransferResultDestinationSink destinationSink;
    private readonly Func<Guid, CancellationToken, Task<ResultWorkspaceHandoffOutcome>> handoffBatchAsync;
    private readonly IAdapterAvailability adapterAvailability;
    private readonly TransferWorkspaceOptions options;
    private readonly CancellationTokenSource lifetime = new();
    private TransferAccountMode? accountMode;
    private bool autoShare;
    private string shareResultDirectory = string.Empty;
    private string statusMessage = "请先导入链接，再选择账号和保存目录。";
    private bool isStarting;
    private bool isClosed;
    private bool synchronizingAccounts;
    private Guid? createdBatchJobId;

    public TransferWorkspaceViewModel(
        ImportViewModel import,
        CreateJobWizardViewModel wizard,
        WorkspaceRunPanelViewModel runPanel,
        IResultSaveDialog resultDirectoryDialog,
        ITransferResultDestinationSink destinationSink,
        Func<Guid, CancellationToken, Task<ResultWorkspaceHandoffOutcome>> handoffBatchAsync,
        IAdapterAvailability adapterAvailability,
        TransferWorkspaceOptions? options = null)
    {
        Import = import ?? throw new ArgumentNullException(nameof(import));
        Wizard = wizard ?? throw new ArgumentNullException(nameof(wizard));
        RunPanel = runPanel ?? throw new ArgumentNullException(nameof(runPanel));
        this.resultDirectoryDialog = resultDirectoryDialog ?? throw new ArgumentNullException(nameof(resultDirectoryDialog));
        this.destinationSink = destinationSink ?? throw new ArgumentNullException(nameof(destinationSink));
        this.handoffBatchAsync = handoffBatchAsync ?? throw new ArgumentNullException(nameof(handoffBatchAsync));
        this.adapterAvailability = adapterAvailability ?? throw new ArgumentNullException(nameof(adapterAvailability));
        this.options = (options ?? new TransferWorkspaceOptions()).Validate();
        StartCommand = new AsyncRelayCommand(StartAsync, () => CanStart);
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
        Import.DraftReady += OnDraftReady;
        Import.PropertyChanged += OnComposedPropertyChanged;
        Wizard.PropertyChanged += OnComposedPropertyChanged;
    }

    public TransferWorkspaceViewModel(
        ImportViewModel import,
        CreateJobWizardViewModel wizard,
        WorkspaceRunPanelViewModel runPanel,
        IResultSaveDialog resultDirectoryDialog,
        ITransferResultDestinationSink destinationSink,
        Func<Guid, CancellationToken, Task> handoffBatchAsync,
        IAdapterAvailability adapterAvailability,
        TransferWorkspaceOptions? options = null)
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
            options)
    {
    }

    public ImportViewModel Import { get; }
    public CreateJobWizardViewModel Wizard { get; }
    public WorkspaceRunPanelViewModel RunPanel { get; }
    public TransferAccountMode? AccountMode
    {
        get => accountMode;
        private set
        {
            if (!SetProperty(ref accountMode, value)) return;
            OnPropertyChanged(nameof(AccountModeText));
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
    public bool AutoShare
    {
        get => autoShare;
        set
        {
            if (!SetProperty(ref autoShare, value)) return;
            foreach (var account in Wizard.Accounts) account.ShareEnabled = value;
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
    public string StatusMessage { get => statusMessage; private set => SetProperty(ref statusMessage, value); }
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
        }
    }
    public bool IsClosed => isClosed;
    public bool CanEditConfiguration => !isClosed && !IsStarting;
    public Guid? CreatedBatchJobId => createdBatchJobId;
    public bool CanStart => !isClosed && !IsStarting && string.IsNullOrEmpty(EvaluateConfiguration());
    public string ConfigurationMessage => EvaluateConfiguration();
    public IAsyncRelayCommand StartCommand { get; }
    public IAsyncRelayCommand SelectSingleAccountCommand { get; }
    public IAsyncRelayCommand SelectReplicationCommand { get; }
    public IAsyncRelayCommand SelectDistributionCommand { get; }
    public IAsyncRelayCommand PickShareResultDirectoryCommand { get; }

    public void ApplyImportedDraft(ImportDraftReady draft)
    {
        if (isClosed) return;
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
        foreach (var account in Wizard.Accounts) account.ShareEnabled = AutoShare;
        AccountMode = Wizard.Accounts.Count == 1 ? TransferAccountMode.SingleAccount : null;
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
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        var transferMode = mode == TransferAccountMode.MultiAccountReplication
            ? TransferMode.Replication
            : TransferMode.Distribution;
        await Wizard.SelectModeAsync(transferMode, linked.Token).ConfigureAwait(true);
        if (linked.IsCancellationRequested || isClosed || Wizard.TransferMode != transferMode) return;
        AccountMode = mode;
        StatusMessage = mode switch
        {
            TransferAccountMode.SingleAccount => "已选择单账号转存。",
            TransferAccountMode.MultiAccountReplication => "已选择多账号复制。",
            _ => "已选择多账号分流。",
        };
    }

    public async Task PickShareResultDirectoryAsync(CancellationToken cancellationToken = default)
    {
        if (!AutoShare || isClosed || IsStarting) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        var selected = await resultDirectoryDialog.PickDestinationDirectoryAsync(linked.Token).ConfigureAwait(true);
        if (linked.IsCancellationRequested || isClosed || string.IsNullOrWhiteSpace(selected)) return;
        ShareResultDirectory = selected;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (isClosed || IsStarting || createdBatchJobId is not null) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        IsStarting = true;
        try
        {
            var saved = await Import.SaveCurrentRowEditAsync(linked.Token).ConfigureAwait(true);
            if (!IsAlive(linked) || !saved) return;

            var validation = EvaluateConfiguration();
            if (validation.Length != 0)
            {
                StatusMessage = validation;
                return;
            }
            var resultDirectory = AutoShare ? ShareResultDirectory : null;

            Wizard.UseBulkPrecheck = adapterAvailability.RequiresBulkPrecheckBeforeStart;
            if (Wizard.UseBulkPrecheck)
            {
                var selectedAccount = Wizard.Accounts.First(account => account.IsSelected);
                Wizard.PrecheckAccountId = selectedAccount.AccountId;
                StatusMessage = "正在检查链接和账号配置……";
                var precheck = await Wizard.RunBackgroundPrecheckAsync(linked.Token).ConfigureAwait(true);
                if (!IsAlive(linked)) return;
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

            await Wizard.PrepareConfirmationAsync(linked.Token).ConfigureAwait(true);
            if (!IsAlive(linked)) return;
            if (Wizard.ConfirmationToken is null)
            {
                StatusMessage = string.IsNullOrWhiteSpace(Wizard.ValidationMessage)
                    ? "无法准备转存，请检查链接、账号和保存目录。"
                    : Wizard.ValidationMessage;
                return;
            }

            var batchJobId = await Wizard.ConfirmPreparedFromUserIntentAsync(linked.Token).ConfigureAwait(true);
            if (batchJobId is not { } batch) return;
            RecordCreatedBatch(batch);
            await CompletePostCommitAsync(batch, resultDirectory).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
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

    public void Close()
    {
        if (isClosed) return;
        isClosed = true;
        lifetime.Cancel();
        Import.Close();
        Import.DraftReady -= OnDraftReady;
        Import.PropertyChanged -= OnComposedPropertyChanged;
        Wizard.PropertyChanged -= OnComposedPropertyChanged;
        RunPanel.Close();
        OnPropertyChanged(nameof(IsClosed));
        RaiseStartState();
    }

    public void Dispose()
    {
        Close();
        RunPanel.Dispose();
        Import.Dispose();
        lifetime.Dispose();
    }

    private string EvaluateConfiguration()
    {
        if (createdBatchJobId is not null) return "当前导入数据的批次已创建。";
        if (Import.SessionId is not { } session || Wizard.CurrentImportSessionId != session)
            return "请先导入至少一条有效链接。";
        if (Wizard.ReadyResourceCount <= 0) return "没有可转存的有效链接。";
        if (Wizard.Accounts.Count == 0) return "请先添加可用百度账号。";
        var selected = Wizard.Accounts.Where(account => account.IsSelected).ToArray();
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

    private void RecordCreatedBatch(Guid batchJobId)
    {
        createdBatchJobId = batchJobId;
        if (!isClosed)
        {
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
        var handedOff = await TryPostCommitAsync(
            token => handoffBatchAsync(batchJobId, token),
            static outcome => outcome.Succeeded).ConfigureAwait(true);
        if (isClosed) return;
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

    private void SynchronizeAccountState()
    {
        if (synchronizingAccounts) return;
        synchronizingAccounts = true;
        try
        {
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

    private void RaiseStartState()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(ConfigurationMessage));
        OnPropertyChanged(nameof(CanEditConfiguration));
        StartCommand.NotifyCanExecuteChanged();
    }
}
