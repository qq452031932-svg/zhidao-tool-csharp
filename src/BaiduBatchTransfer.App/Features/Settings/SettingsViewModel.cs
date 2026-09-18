using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.App.Updates;
using BaiduBatchTransfer.App.Features.Licensing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaiduBatchTransfer.App.Features.Settings;

public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private const string DialogTitle = "软件更新";
    private const string DownloadConfirmation =
        "下载完成后软件将安全关闭、安装更新并自动重启，是否继续？";
    private const string RestartConfirmation =
        "软件将安全关闭、安装已下载的更新并自动重启，是否继续？";

    private readonly IApplicationUpdateService updateService;
    private readonly IDialogService dialogService;
    private readonly IApplicationCloseRequester closeRequester;
    private readonly SynchronizationContext synchronizationContext;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object lifecycleGate = new();
    private readonly object snapshotGate = new();
    private readonly Func<CancellationToken, Task<bool>>? presentActivationAsync;
    private readonly Func<LicenseSettingsSnapshot>? readLicenseSettings;
    private readonly Action<Action>? unsubscribeLicenseChanged;
    private Action? licenseChangedHandler;
    private readonly Action<string>? copyToClipboard;
    private string machineCode = string.Empty;
    private string licensePackageName = "未激活";
    private string licenseExpiresAt = "—";
    private string licenseAuthorizationStatus = "未授权";
    private string licenseMachineBindingStatus = "未绑定";
    private bool canActivateLicense;
    private ApplicationUpdateStatus updateStatus;
    private string currentVersion = string.Empty;
    private string statusMessage = string.Empty;
    private string latestVersion = "—";
    private string releaseNotes = "暂无更新说明。";
    private int downloadProgress;
    private bool isOperationBusy;
    private bool preparingRestart;
    private bool closeRequested;
    private long receivedSnapshotSequence;
    private long appliedSnapshotSequence;
    private int disposed;

    public SettingsViewModel(
        IApplicationUpdateService updateService,
        IDialogService dialogService,
        IApplicationCloseRequester closeRequester)
        : this(
            updateService,
            dialogService,
            closeRequester,
            SynchronizationContext.Current ?? throw new InvalidOperationException(
                "SettingsViewModel requires a UI SynchronizationContext."),
            ApplicationBuildInfo.Current)
    {
    }

    public SettingsViewModel(
        IApplicationUpdateService updateService,
        IDialogService dialogService,
        IApplicationCloseRequester closeRequester,
        SynchronizationContext synchronizationContext,
        ApplicationBuildInfo? buildInfo = null,
        LicenseSettingsSnapshot? licenseSettings = null,
        Func<CancellationToken, Task<bool>>? presentActivationAsync = null,
        Func<LicenseSettingsSnapshot>? readLicenseSettings = null,
        Action<Action>? subscribeLicenseChanged = null,
        Action<Action>? unsubscribeLicenseChanged = null,
        Func<string>? readMachineCode = null,
        Action<string>? copyToClipboard = null)
    {
        this.updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
        this.dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        this.closeRequester = closeRequester ?? throw new ArgumentNullException(nameof(closeRequester));
        this.synchronizationContext = synchronizationContext ??
            throw new ArgumentNullException(nameof(synchronizationContext));
        BuildLabel = (buildInfo ?? ApplicationBuildInfo.Current).IsOtaAcceptanceBuild
            ? "OTA 本地验收版"
            : string.Empty;
        this.presentActivationAsync = presentActivationAsync;
        this.readLicenseSettings = readLicenseSettings;
        ActivateLicenseCommand = new AsyncRelayCommand(ActivateLicenseAsync, () => CanActivateLicense);
        ApplyLicense(licenseSettings ?? readLicenseSettings?.Invoke() ?? LicenseSettingsSnapshot.NotActivated);
        this.unsubscribeLicenseChanged = unsubscribeLicenseChanged;
        this.copyToClipboard = copyToClipboard;
        // 机器码用于用户联系管理员解绑时定位设备；读取失败不应阻断设置页。
        try { machineCode = readMachineCode?.Invoke() ?? string.Empty; }
        catch { machineCode = string.Empty; }
        CopyMachineCodeCommand = new RelayCommand(
            () => copyToClipboard?.Invoke(machineCode),
            () => copyToClipboard is not null && machineCode.Length > 0);
        if (subscribeLicenseChanged is not null && readLicenseSettings is not null)
        {
            // 授权可能在设置页之外完成（启动时的激活窗），必须跟随运行时状态刷新。
            licenseChangedHandler = () => RunOnUi(() => ApplyLicense(readLicenseSettings()));
            subscribeLicenseChanged(licenseChangedHandler);
        }

        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync, CanCheckForUpdates);
        UpdateAndRestartCommand = new AsyncRelayCommand(UpdateAndRestartAsync, CanUpdateAndRestart);

        updateService.SnapshotChanged += OnSnapshotChanged;
        var initializationSequence = NextSnapshotSequence();
        ApplySnapshot(updateService.Snapshot, initializationSequence);
    }

    public IAsyncRelayCommand CheckForUpdatesCommand { get; }

    public IAsyncRelayCommand UpdateAndRestartCommand { get; }

    public string BuildLabel { get; }

    public string LicensePackageName
    {
        get => licensePackageName;
        private set => SetProperty(ref licensePackageName, value);
    }

    public string LicenseExpiresAt
    {
        get => licenseExpiresAt;
        private set => SetProperty(ref licenseExpiresAt, value);
    }

    public string LicenseAuthorizationStatus
    {
        get => licenseAuthorizationStatus;
        private set => SetProperty(ref licenseAuthorizationStatus, value);
    }

    public string LicenseMachineBindingStatus
    {
        get => licenseMachineBindingStatus;
        private set => SetProperty(ref licenseMachineBindingStatus, value);
    }

    // 未授权时才显示激活入口。此前激活窗只在启动时弹一次，
    // 用户点了“暂不激活”就只能重启软件才能再激活。
    public bool CanActivateLicense
    {
        get => canActivateLicense;
        private set
        {
            if (SetProperty(ref canActivateLicense, value)) ActivateLicenseCommand.NotifyCanExecuteChanged();
        }
    }

    public IAsyncRelayCommand ActivateLicenseCommand { get; }

    public IRelayCommand CopyMachineCodeCommand { get; }

    public bool HasMachineCode => machineCode.Length > 0;

    // 完整哈希在界面上放不下，显示首尾各 8 位；复制时给完整值。
    public string MachineCodeDisplay => machineCode.Length == 0
        ? "不可用"
        : machineCode.Length <= 20 ? machineCode : $"{machineCode[..8]}……{machineCode[^8..]}";

    public string CurrentVersion
    {
        get => currentVersion;
        private set => SetProperty(ref currentVersion, value);
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public string LatestVersion
    {
        get => latestVersion;
        private set => SetProperty(ref latestVersion, value);
    }

    public string ReleaseNotes
    {
        get => releaseNotes;
        private set => SetProperty(ref releaseNotes, value);
    }

    public int DownloadProgress
    {
        get => downloadProgress;
        private set => SetProperty(ref downloadProgress, value);
    }

    public string UpdateActionText => updateStatus == ApplicationUpdateStatus.ReadyToRestart
        ? "重启完成更新"
        : "立即更新并重启";

    public void Dispose()
    {
        lock (lifecycleGate)
        {
            if (disposed != 0)
                return;

            Volatile.Write(ref disposed, 1);
            updateService.SnapshotChanged -= OnSnapshotChanged;
            if (licenseChangedHandler is not null) unsubscribeLicenseChanged?.Invoke(licenseChangedHandler);
            licenseChangedHandler = null;
        }

        CheckForUpdatesCommand.Cancel();
        UpdateAndRestartCommand.Cancel();
        lifetime.Cancel();
        lifetime.Dispose();
        NotifyCommandState();
    }

    private bool CanCheckForUpdates() =>
        Volatile.Read(ref disposed) == 0 &&
        !isOperationBusy &&
        updateStatus is not ApplicationUpdateStatus.Checking and
            not ApplicationUpdateStatus.Downloading and
            not ApplicationUpdateStatus.NotConfigured and
            not ApplicationUpdateStatus.Unsupported;

    private bool CanUpdateAndRestart() =>
        Volatile.Read(ref disposed) == 0 &&
        !isOperationBusy &&
        updateStatus is ApplicationUpdateStatus.Available or
            ApplicationUpdateStatus.ReadyToRestart;

    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource operation;
        lock (lifecycleGate)
        {
            if (disposed != 0)
                return;
            operation = CancellationTokenSource.CreateLinkedTokenSource(
                lifetime.Token,
                cancellationToken);
        }

        using (operation)
        {
            SetOperationBusy(true);
            try
            {
                var result = await updateService.CheckAsync(manual: true, operation.Token)
                    .ConfigureAwait(true);
                if (Volatile.Read(ref disposed) == 0)
                    ApplySnapshot(result);
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            {
            }
            finally
            {
                if (Volatile.Read(ref disposed) == 0)
                    SetOperationBusy(false);
            }
        }
    }

    private async Task UpdateAndRestartAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource operation;
        lock (lifecycleGate)
        {
            if (disposed != 0)
                return;
            operation = CancellationTokenSource.CreateLinkedTokenSource(
                lifetime.Token,
                cancellationToken);
        }

        using (operation)
        {
            var isPendingRestart = updateStatus == ApplicationUpdateStatus.ReadyToRestart;
            SetOperationBusy(true);
            try
            {
                var confirmed = await dialogService.ConfirmAsync(
                        DialogTitle,
                        isPendingRestart ? RestartConfirmation : DownloadConfirmation,
                        operation.Token)
                    .ConfigureAwait(true);
                if (!confirmed || Volatile.Read(ref disposed) != 0)
                    return;

                if (!isPendingRestart)
                {
                    var downloaded = await updateService.DownloadAsync(operation.Token)
                        .ConfigureAwait(true);
                    if (Volatile.Read(ref disposed) != 0)
                        return;

                    ApplySnapshot(downloaded);
                    if (downloaded.Status != ApplicationUpdateStatus.ReadyToRestart)
                        return;
                }

                PrepareRestartAndRequestClose();
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            {
            }
            finally
            {
                if (Volatile.Read(ref disposed) == 0)
                    SetOperationBusy(false);
            }
        }
    }

    private void OnSnapshotChanged(object? sender, ApplicationUpdateSnapshot snapshot)
    {
        if (Volatile.Read(ref disposed) != 0)
            return;

        var sequence = NextSnapshotSequence();
        try
        {
            synchronizationContext.Post(
                static state =>
                {
                    var update = (PostedSnapshot)state!;
                    if (Volatile.Read(ref update.ViewModel.disposed) == 0)
                        update.ViewModel.ApplySnapshot(update.Snapshot, update.Sequence);
                },
                new PostedSnapshot(this, snapshot, sequence));
        }
        catch (InvalidOperationException) when (Volatile.Read(ref disposed) != 0)
        {
            // The captured UI context can stop accepting work during shutdown.
        }
    }

    private void PrepareRestartAndRequestClose()
    {
        lock (lifecycleGate)
        {
            if (disposed != 0 || preparingRestart || closeRequested)
                return;
            preparingRestart = true;
        }

        var prepared = false;
        var shouldRequestClose = false;
        try
        {
            prepared = updateService.TryPrepareRestart();
        }
        finally
        {
            lock (lifecycleGate)
            {
                preparingRestart = false;
                if (disposed == 0 && prepared && !closeRequested)
                {
                    closeRequested = true;
                    shouldRequestClose = true;
                }
            }
        }

        if (!shouldRequestClose)
            return;

        try
        {
            closeRequester.RequestClose();
        }
        finally
        {
            lock (lifecycleGate)
                closeRequested = false;
        }
    }

    private void ApplySnapshot(ApplicationUpdateSnapshot snapshot) =>
        ApplySnapshot(snapshot, NextSnapshotSequence());

    private void ApplySnapshot(ApplicationUpdateSnapshot snapshot, long sequence)
    {
        if (Volatile.Read(ref disposed) != 0)
            return;

        lock (snapshotGate)
        {
            if (sequence <= appliedSnapshotSequence)
                return;
            appliedSnapshotSequence = sequence;

            var previousStatus = updateStatus;
            updateStatus = snapshot.Status;
            CurrentVersion = snapshot.CurrentVersion ?? string.Empty;
            StatusMessage = snapshot.UserMessage ?? string.Empty;
            LatestVersion = snapshot.LatestVersion ?? "—";
            ReleaseNotes = snapshot.ReleaseNotes ?? "暂无更新说明。";
            DownloadProgress = Math.Clamp(snapshot.DownloadProgress, 0, 100);

            if (previousStatus != updateStatus)
                OnPropertyChanged(nameof(UpdateActionText));
            NotifyCommandState();
        }
    }

    private long NextSnapshotSequence() =>
        Interlocked.Increment(ref receivedSnapshotSequence);

    private void SetOperationBusy(bool value)
    {
        if (isOperationBusy == value)
            return;

        isOperationBusy = value;
        NotifyCommandState();
    }

    private void NotifyCommandState()
    {
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
        UpdateAndRestartCommand.NotifyCanExecuteChanged();
    }

    private sealed record PostedSnapshot(
        SettingsViewModel ViewModel,
        ApplicationUpdateSnapshot Snapshot,
        long Sequence);

    private void ApplyLicense(LicenseSettingsSnapshot license)
    {
        LicensePackageName = string.IsNullOrWhiteSpace(license.PackageName)
            ? license.VerificationDeferred ? "暂时无法读取" : "未激活"
            : license.PackageName;
        LicenseExpiresAt = license.ExpiryDisplay;
        LicenseAuthorizationStatus = license.IsAuthorized
            ? "已授权"
            : license.VerificationDeferred
                ? "暂时无法连接授权服务器，稍后自动重试"
                : "未授权";
        LicenseMachineBindingStatus = license.IsMachineBound
            ? "已绑定当前电脑"
            : license.VerificationDeferred ? "暂时无法确认" : "未绑定";
        // 只要还没授权就给入口，连不上服务器也给。卡密恰好到期、同时服务器又不可达
        // 的用户，否则就没有任何地方能输入新卡密了——那是把唯一的门锁上。
        // 连不上时点开激活窗，窗口自己会说"无法连接授权服务器，请检查网络后重试"。
        CanActivateLicense = !license.IsAuthorized && presentActivationAsync is not null;
    }

    private async Task ActivateLicenseAsync(CancellationToken cancellationToken)
    {
        if (presentActivationAsync is null) return;
        await presentActivationAsync(cancellationToken).ConfigureAwait(true);
        // 无论激活成功与否都重新读取：失败时状态保持不变，成功时立即反映到界面。
        if (readLicenseSettings is not null) ApplyLicense(readLicenseSettings());
    }

    private void RunOnUi(Action action)
    {
        if (SynchronizationContext.Current == synchronizationContext) action();
        else synchronizationContext.Post(_ => action(), null);
    }
}
