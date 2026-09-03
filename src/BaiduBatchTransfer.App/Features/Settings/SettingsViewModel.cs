using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.App.Updates;
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
    private readonly SynchronizationContext? synchronizationContext;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object lifecycleGate = new();
    private readonly object snapshotGate = new();
    private ApplicationUpdateStatus updateStatus;
    private string currentVersion = string.Empty;
    private string statusMessage = string.Empty;
    private string latestVersion = "—";
    private string releaseNotes = "暂无更新说明。";
    private int downloadProgress;
    private bool isOperationBusy;
    private long receivedSnapshotSequence;
    private long appliedSnapshotSequence;
    private int disposed;

    public SettingsViewModel(
        IApplicationUpdateService updateService,
        IDialogService dialogService,
        IApplicationCloseRequester closeRequester)
    {
        this.updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
        this.dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        this.closeRequester = closeRequester ?? throw new ArgumentNullException(nameof(closeRequester));
        synchronizationContext = SynchronizationContext.Current;

        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync, CanCheckForUpdates);
        UpdateAndRestartCommand = new AsyncRelayCommand(UpdateAndRestartAsync, CanUpdateAndRestart);

        updateService.SnapshotChanged += OnSnapshotChanged;
        var initializationSequence = NextSnapshotSequence();
        ApplySnapshot(updateService.Snapshot, initializationSequence);
    }

    public IAsyncRelayCommand CheckForUpdatesCommand { get; }

    public IAsyncRelayCommand UpdateAndRestartCommand { get; }

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

                lock (lifecycleGate)
                {
                    if (disposed == 0 && updateService.TryPrepareRestart())
                        closeRequester.RequestClose();
                }
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
        if (synchronizationContext is null ||
            ReferenceEquals(SynchronizationContext.Current, synchronizationContext))
        {
            ApplySnapshot(snapshot, sequence);
            return;
        }

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
        catch (InvalidOperationException)
        {
            // The captured UI context can stop accepting work during shutdown.
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
}
