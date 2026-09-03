using BaiduBatchTransfer.App.Features.Settings;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.App.Updates;
using System.Collections.Concurrent;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class SettingsViewModelTests
{
    private const string DownloadConfirmation =
        "下载完成后软件将安全关闭、安装更新并自动重启，是否继续？";
    private const string RestartConfirmation =
        "软件将安全关闭、安装已下载的更新并自动重启，是否继续？";

    [Fact]
    public void InitialSnapshotPopulatesTheUpdateCard()
    {
        var service = new FakeUpdateService(Snapshot(
            ApplicationUpdateStatus.Available,
            currentVersion: "1.2.3",
            latestVersion: "1.3.0",
            releaseNotes: "修复传输稳定性。",
            userMessage: "发现新版本。"));
        using var viewModel = Create(service);

        Assert.Equal("1.2.3", viewModel.CurrentVersion);
        Assert.Equal("发现新版本。", viewModel.StatusMessage);
        Assert.Equal("1.3.0", viewModel.LatestVersion);
        Assert.Equal("修复传输稳定性。", viewModel.ReleaseNotes);
        Assert.Equal(0, viewModel.DownloadProgress);
        Assert.Equal("立即更新并重启", viewModel.UpdateActionText);
        Assert.True(viewModel.UpdateAndRestartCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(ApplicationUpdateStatus.NotConfigured)]
    [InlineData(ApplicationUpdateStatus.Unsupported)]
    public void PermanentlyUnavailableStatesDisableBothUpdateCommands(ApplicationUpdateStatus status)
    {
        using var viewModel = Create(new FakeUpdateService(Snapshot(status)));

        Assert.False(viewModel.CheckForUpdatesCommand.CanExecute(null));
        Assert.False(viewModel.UpdateAndRestartCommand.CanExecute(null));
    }

    [Fact]
    public void SubscriptionBeforeInitialReadDoesNotLoseReadyToRestartTransition()
    {
        var service = new FakeUpdateService(Snapshot(ApplicationUpdateStatus.Idle));
        service.SnapshotRead = () =>
        {
            service.SnapshotRead = null;
            service.Raise(Snapshot(
                ApplicationUpdateStatus.ReadyToRestart,
                latestVersion: "2.0.0",
                progress: 100));
        };

        using var viewModel = Create(service);

        Assert.Equal("重启完成更新", viewModel.UpdateActionText);
        Assert.Equal("2.0.0", viewModel.LatestVersion);
        Assert.True(viewModel.UpdateAndRestartCommand.CanExecute(null));
    }

    [Fact]
    public async Task CheckCommandDisablesWhileRunningAndRestoresWithAvailableUpdate()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<ApplicationUpdateSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeUpdateService(Snapshot(ApplicationUpdateStatus.Idle))
        {
            CheckHandler = _ =>
            {
                started.TrySetResult();
                return completion.Task;
            }
        };
        using var viewModel = Create(service);

        var checking = viewModel.CheckForUpdatesCommand.ExecuteAsync(null);
        await started.Task;

        Assert.False(viewModel.CheckForUpdatesCommand.CanExecute(null));
        Assert.False(viewModel.UpdateAndRestartCommand.CanExecute(null));

        completion.SetResult(Snapshot(
            ApplicationUpdateStatus.Available,
            latestVersion: "2.0.0",
            releaseNotes: "新版说明",
            userMessage: "发现新版本。"));
        await checking;

        Assert.True(viewModel.CheckForUpdatesCommand.CanExecute(null));
        Assert.True(viewModel.UpdateAndRestartCommand.CanExecute(null));
        Assert.Equal("2.0.0", viewModel.LatestVersion);
        Assert.Equal(1, service.CheckCalls);
        Assert.True(service.LastCheckWasManual);
    }

    [Fact]
    public async Task DecliningConfirmationNeverDownloadsOrRequestsClose()
    {
        var service = new FakeUpdateService(Snapshot(ApplicationUpdateStatus.Available));
        var dialog = new FakeDialog(false);
        var closeRequester = new FakeCloseRequester();
        using var viewModel = Create(service, dialog, closeRequester);

        await viewModel.UpdateAndRestartCommand.ExecuteAsync(null);

        Assert.Equal(DownloadConfirmation, Assert.Single(dialog.Messages));
        Assert.Equal(0, service.DownloadCalls);
        Assert.Equal(0, service.PrepareRestartCalls);
        Assert.Equal(0, closeRequester.Requests);
    }

    [Fact]
    public async Task ConfirmedAvailableUpdateDownloadsTracksProgressPreparesAndClosesOnce()
    {
        var trace = new List<string>();
        var service = new FakeUpdateService(Snapshot(ApplicationUpdateStatus.Available))
        {
            PrepareRestartResult = true,
            Trace = trace
        };
        service.DownloadHandler = _ =>
        {
            service.Raise(Snapshot(ApplicationUpdateStatus.Downloading, progress: -10));
            service.Raise(Snapshot(ApplicationUpdateStatus.Downloading, progress: 47));
            service.Raise(Snapshot(ApplicationUpdateStatus.Downloading, progress: 140));
            return Task.FromResult(Snapshot(
                ApplicationUpdateStatus.ReadyToRestart,
                latestVersion: "2.0.0",
                progress: 100,
                userMessage: "更新已下载，重启后完成安装。"));
        };
        var observedProgress = new List<int> { 0 };
        var dialog = new FakeDialog(trace, true);
        var closeRequester = new FakeCloseRequester(trace);
        using var viewModel = Create(service, dialog, closeRequester);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SettingsViewModel.DownloadProgress))
                observedProgress.Add(viewModel.DownloadProgress);
        };

        await viewModel.UpdateAndRestartCommand.ExecuteAsync(null);

        Assert.Equal(DownloadConfirmation, Assert.Single(dialog.Messages));
        Assert.Equal(1, service.DownloadCalls);
        Assert.Equal(1, service.PrepareRestartCalls);
        Assert.Equal(1, closeRequester.Requests);
        Assert.Contains(0, observedProgress);
        Assert.Contains(47, observedProgress);
        Assert.Contains(100, observedProgress);
        Assert.All(observedProgress, progress => Assert.InRange(progress, 0, 100));
        Assert.Equal("重启完成更新", viewModel.UpdateActionText);
        Assert.Equal(["confirm", "download", "prepare", "close"], trace);
    }

    [Fact]
    public async Task FailedRestartPreparationNeverRequestsClose()
    {
        var service = new FakeUpdateService(Snapshot(
            ApplicationUpdateStatus.ReadyToRestart,
            progress: 100));
        var closeRequester = new FakeCloseRequester();
        using var viewModel = Create(service, new FakeDialog(true), closeRequester);

        await viewModel.UpdateAndRestartCommand.ExecuteAsync(null);

        Assert.Equal(1, service.PrepareRestartCalls);
        Assert.Equal(0, closeRequester.Requests);
    }

    [Fact]
    public async Task ReadyToRestartConfirmsThenSkipsCheckAndDownload()
    {
        var service = new FakeUpdateService(Snapshot(
            ApplicationUpdateStatus.ReadyToRestart,
            latestVersion: "2.0.0",
            progress: 100,
            userMessage: "更新已下载，重启后完成安装。"))
        {
            PrepareRestartResult = true
        };
        var dialog = new FakeDialog(true);
        var closeRequester = new FakeCloseRequester();
        using var viewModel = Create(service, dialog, closeRequester);

        Assert.Equal("重启完成更新", viewModel.UpdateActionText);
        Assert.True(viewModel.UpdateAndRestartCommand.CanExecute(null));

        await viewModel.UpdateAndRestartCommand.ExecuteAsync(null);

        Assert.Equal(RestartConfirmation, Assert.Single(dialog.Messages));
        Assert.Equal(0, service.CheckCalls);
        Assert.Equal(0, service.DownloadCalls);
        Assert.Equal(1, service.PrepareRestartCalls);
        Assert.Equal(1, closeRequester.Requests);
    }

    [Fact]
    public void AutomaticServiceNotificationOnlyUpdatesUiAndNeverDownloads()
    {
        var service = new FakeUpdateService(Snapshot(ApplicationUpdateStatus.Idle));
        using var viewModel = Create(service);

        service.Raise(Snapshot(
            ApplicationUpdateStatus.Available,
            latestVersion: "3.0.0",
            releaseNotes: "自动检查发现的说明",
            userMessage: "发现新版本。"));

        Assert.Equal("3.0.0", viewModel.LatestVersion);
        Assert.Equal("自动检查发现的说明", viewModel.ReleaseNotes);
        Assert.Equal(0, service.CheckCalls);
        Assert.Equal(0, service.DownloadCalls);
    }

    [Fact]
    public async Task BackgroundNotificationsReturnToCapturedContext()
    {
        var previous = SynchronizationContext.Current;
        var context = new QueuedSynchronizationContext();
        var service = new FakeUpdateService(Snapshot(ApplicationUpdateStatus.Idle));
        SettingsViewModel? viewModel = null;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            viewModel = Create(service);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await Task.Run(() => service.Raise(Snapshot(
            ApplicationUpdateStatus.Available,
            latestVersion: "4.0.0",
            userMessage: "发现新版本。")));

        Assert.Equal("—", viewModel.LatestVersion);
        Assert.Equal(1, context.PendingCount);

        context.Drain();

        Assert.Equal("4.0.0", viewModel.LatestVersion);
        viewModel.Dispose();
    }

    [Fact]
    public async Task OlderPostedSnapshotCannotOverwriteNewerUiSnapshot()
    {
        var previous = SynchronizationContext.Current;
        var context = new QueuedSynchronizationContext();
        var service = new FakeUpdateService(Snapshot(ApplicationUpdateStatus.Idle));
        SettingsViewModel viewModel;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            viewModel = Create(service);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await Task.Run(() => service.Raise(Snapshot(
            ApplicationUpdateStatus.Downloading,
            latestVersion: "2.0.0",
            progress: 40)));
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            service.Raise(Snapshot(
                ApplicationUpdateStatus.ReadyToRestart,
                latestVersion: "2.0.0",
                progress: 100));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Assert.Equal("重启完成更新", viewModel.UpdateActionText);
        context.Drain();

        Assert.Equal("重启完成更新", viewModel.UpdateActionText);
        Assert.Equal(100, viewModel.DownloadProgress);
        viewModel.Dispose();
    }

    [Fact]
    public async Task DisposeUnsubscribesAndMakesAlreadyPostedCallbacksHarmless()
    {
        var previous = SynchronizationContext.Current;
        var context = new QueuedSynchronizationContext();
        var service = new FakeUpdateService(Snapshot(ApplicationUpdateStatus.Idle));
        SettingsViewModel viewModel;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            viewModel = Create(service);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await Task.Run(() => service.Raise(Snapshot(
            ApplicationUpdateStatus.Available,
            latestVersion: "posted-before-dispose")));
        viewModel.Dispose();
        context.Drain();
        service.Raise(Snapshot(
            ApplicationUpdateStatus.Available,
            latestVersion: "raised-after-dispose"));

        Assert.Equal("—", viewModel.LatestVersion);
        Assert.False(viewModel.CheckForUpdatesCommand.CanExecute(null));
        Assert.False(viewModel.UpdateAndRestartCommand.CanExecute(null));
        Assert.Equal(0, service.DisposeCalls);
    }

    [Fact]
    public async Task DisposeCancelsPendingConfirmationBeforeDownloadOrClose()
    {
        var service = new FakeUpdateService(Snapshot(ApplicationUpdateStatus.Available));
        var dialog = new BlockingDialog();
        var closeRequester = new FakeCloseRequester();
        var viewModel = Create(service, dialog, closeRequester);

        var operation = viewModel.UpdateAndRestartCommand.ExecuteAsync(null);
        await dialog.Started.Task;
        viewModel.Dispose();
        await operation;

        Assert.Equal(0, service.DownloadCalls);
        Assert.Equal(0, service.PrepareRestartCalls);
        Assert.Equal(0, closeRequester.Requests);
    }

    private static SettingsViewModel Create(
        FakeUpdateService service,
        IDialogService? dialog = null,
        IApplicationCloseRequester? closeRequester = null) =>
        new(service, dialog ?? new FakeDialog(true), closeRequester ?? new FakeCloseRequester());

    private static ApplicationUpdateSnapshot Snapshot(
        ApplicationUpdateStatus status,
        string currentVersion = "1.0.0",
        string? latestVersion = null,
        string? releaseNotes = null,
        int progress = 0,
        string? userMessage = null) =>
        new(
            status,
            currentVersion,
            latestVersion,
            releaseNotes,
            progress,
            userMessage ?? status.ToString());

    private sealed class FakeUpdateService(ApplicationUpdateSnapshot snapshot)
        : IApplicationUpdateService, IDisposable
    {
        private ApplicationUpdateSnapshot snapshot = snapshot;
        public ApplicationUpdateSnapshot Snapshot
        {
            get
            {
                var value = snapshot;
                SnapshotRead?.Invoke();
                return value;
            }
            private set => snapshot = value;
        }
        public Action? SnapshotRead { get; set; }
        public Func<CancellationToken, Task<ApplicationUpdateSnapshot>>? CheckHandler { get; set; }
        public Func<CancellationToken, Task<ApplicationUpdateSnapshot>>? DownloadHandler { get; set; }
        public bool PrepareRestartResult { get; init; }
        public List<string>? Trace { get; init; }
        public int CheckCalls { get; private set; }
        public int DownloadCalls { get; private set; }
        public int PrepareRestartCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public bool LastCheckWasManual { get; private set; }

        public event EventHandler<ApplicationUpdateSnapshot>? SnapshotChanged;

        public Task<ApplicationUpdateSnapshot> CheckAsync(bool manual, CancellationToken cancellationToken)
        {
            CheckCalls++;
            LastCheckWasManual = manual;
            return CheckHandler?.Invoke(cancellationToken) ?? Task.FromResult(Snapshot);
        }

        public Task<ApplicationUpdateSnapshot> DownloadAsync(CancellationToken cancellationToken)
        {
            Trace?.Add("download");
            DownloadCalls++;
            return DownloadHandler?.Invoke(cancellationToken) ?? Task.FromResult(Snapshot);
        }

        public bool TryPrepareRestart()
        {
            Trace?.Add("prepare");
            PrepareRestartCalls++;
            return PrepareRestartResult;
        }

        public void Raise(ApplicationUpdateSnapshot value)
        {
            Snapshot = value;
            SnapshotChanged?.Invoke(this, value);
        }

        public void Dispose() => DisposeCalls++;
    }

    private sealed class FakeDialog : IDialogService
    {
        private readonly Queue<bool> answers;
        private readonly List<string>? trace;

        public FakeDialog(params bool[] answers) : this(null, answers) { }

        public FakeDialog(List<string>? trace, params bool[] answers)
        {
            this.trace = trace;
            this.answers = new Queue<bool>(answers);
        }

        public List<string> Messages { get; } = [];

        public Task<bool> ConfirmAsync(
            string title,
            string message,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace?.Add("confirm");
            Assert.Equal("软件更新", title);
            Messages.Add(message);
            return Task.FromResult(answers.Count == 0 || answers.Dequeue());
        }

        public Task ShowMessageAsync(
            string title,
            string message,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string?> PickExcelFileAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class BlockingDialog : IDialogService
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<bool> ConfirmAsync(
            string title,
            string message,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return true;
        }

        public Task ShowMessageAsync(
            string title,
            string message,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string?> PickExcelFileAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class FakeCloseRequester(List<string>? trace = null) : IApplicationCloseRequester
    {
        public int Requests { get; private set; }
        public void RequestClose()
        {
            trace?.Add("close");
            Requests++;
        }
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> callbacks = [];

        public int PendingCount => callbacks.Count;

        public override void Post(SendOrPostCallback d, object? state) => callbacks.Enqueue((d, state));

        public void Drain()
        {
            while (callbacks.TryDequeue(out var callback))
                callback.Callback(callback.State);
        }
    }
}
