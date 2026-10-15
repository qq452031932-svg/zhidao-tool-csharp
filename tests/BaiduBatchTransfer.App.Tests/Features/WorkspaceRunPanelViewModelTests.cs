using BaiduBatchTransfer.App.Features.Common;
using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Application.Scheduling;
using SuperPan.Licensing.Client;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class WorkspaceRunPanelViewModelTests
{
    [Fact]
    public async Task CleanupKeepsNonterminalBatchButReportsFailedDetailsReloadWithoutOldStatistics()
    {
        var oldBatch = new WorkspaceBatchItem(Guid.NewGuid(), "可删除终态", "Completed");
        var retained = new WorkspaceBatchItem(Guid.NewGuid(), "保留非终态", "Paused");
        var history = new MutableHistorySource(oldBatch);
        var workflow = new FakeResultsWorkflow { Page = new([Row("Failed", "失败")], 5, null) };
        var results = CreateResults(workflow);
        var runStore = new FakeRunControlStore();
        using var panel = new WorkspaceRunPanelViewModel(results, new(runStore), new(runStore), new(runStore), historySource: history);
        await panel.InitializeAsync();
        Assert.Equal(5, results.OperationCount);
        var cleanupStore = new CleanupStore { Preview = new(BaiduBatchTransfer.Core.Abstractions.LocalHistoryKind.Transfer, 1, false, "terminal") };
        cleanupStore.OnDelete = () =>
        {
            history.Current = retained;
            workflow.FailSummary = true;
            cleanupStore.Preview = new(BaiduBatchTransfer.Core.Abstractions.LocalHistoryKind.Transfer, 0, false, null);
        };
        string? status = null;
        using var cleanup = new LocalHistoryCleanupViewModel(new(cleanupStore), new CleanupInteraction(),
            BaiduBatchTransfer.Core.Abstractions.LocalHistoryKind.Transfer, () => true, panel.ReloadHistoryAsync, text => status = text);
        await cleanup.ClearAsync();
        Assert.Single(cleanupStore.Deleted);
        Assert.Equal(retained.BatchJobId, Assert.Single(panel.Batches).BatchJobId);
        Assert.Equal(retained.BatchJobId, panel.SelectedBatch?.BatchJobId);
        Assert.Empty(results.Items);
        Assert.Equal(0, results.TotalCount + results.ResourceCount + results.OperationCount + results.FailedCount + results.CompleteSuccessCount);
        Assert.Contains("本地历史已删除，但界面刷新失败，请稍后重试", status);
        Assert.Contains("1", status);
    }
    [Fact]
    public async Task CancelledHistoryReloadRestoresAvailability()
    {
        var history = new DeferredHistorySource();
        var store = new FakeRunControlStore();
        using var vm = new WorkspaceRunPanelViewModel(CreateResults(new FakeResultsWorkflow()), new(store), new(store), new(store), historySource: history);
        using var cancellation = new CancellationTokenSource();
        var pending = vm.ReloadHistoryAsync(cancellation.Token);
        cancellation.Cancel();
        history.Release.SetResult([]);
        await pending;
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ForcedReloadRejectsLateHistoryFromPreviousRequest()
    {
        var history = new DeferredHistorySource();
        var store = new FakeRunControlStore();
        using var vm = new WorkspaceRunPanelViewModel(CreateResults(new FakeResultsWorkflow()), new(store), new(store), new(store), historySource: history);
        var pending = vm.InitializeAsync();
        history.Delay = false;
        await vm.ReloadHistoryAsync();
        history.Release.SetResult([new(Guid.NewGuid(), "已删除的旧批次", "Completed")]);
        await pending;
        Assert.Empty(vm.Batches);
        Assert.Null(vm.SelectedBatch);
        Assert.Equal(Guid.Empty, vm.Results.BatchJobId);
        Assert.False(vm.IsBusy);
    }

    private sealed class DeferredHistorySource : IWorkspaceBatchHistorySource
    {
        public bool Delay { get; set; } = true;
        public TaskCompletionSource<IReadOnlyList<WorkspaceBatchItem>> Release { get; } = new();
        public Task<IReadOnlyList<WorkspaceBatchItem>> LoadRecentAsync(int maximumCount, CancellationToken cancellationToken = default) =>
            Delay ? Release.Task : Task.FromResult<IReadOnlyList<WorkspaceBatchItem>>([]);
    }
    [Fact]
    public async Task ReloadEmptyHistoryClearsPreviouslySelectedResults()
    {
        var results = CreateResults(new FakeResultsWorkflow { Page = new([Row("Failed", "失败")], 1, null) });
        using var vm = Create(results, new FakeRunControlStore());
        await vm.ActivateBatchAsync(Guid.NewGuid());
        await vm.ReloadHistoryAsync();
        Assert.Empty(vm.Batches);
        Assert.Null(vm.SelectedBatch);
        Assert.Empty(results.Items);
        Assert.Equal(Guid.Empty, results.BatchJobId);
        Assert.False(vm.PauseCommand.CanExecute(null));
        Assert.Equal("尚未开始转存。", vm.StatusMessage);
    }

    [Fact]
    public async Task LicenseFailureStopsManualResumeBeforeDurableStateChanges()
    {
        var store = new FakeRunControlStore();
        var gate = new RecordingLicenseGate(false);
        using var vm = Create(CreateResults(new FakeResultsWorkflow()), store, gate);
        vm.PresentBatch(Guid.NewGuid(), batchStatus: "Paused");

        await vm.ResumeAsync();

        Assert.Equal([LicenseOperationKind.Transfer], gate.Operations);
        Assert.Empty(store.ResumedBatches);
        Assert.Equal("软件尚未激活，操作未开始。请在「设置中心 → 软件授权」中激活后重试。", vm.StatusMessage);
    }

    [Fact]
    public async Task LicenseFailureStopsManualRetryBeforeDurableStateChanges()
    {
        var workflow = new FakeResultsWorkflow { Page = new([Row("Failed", "失败")], 1, null) };
        var results = CreateResults(workflow);
        var store = new FakeRunControlStore();
        var gate = new RecordingLicenseGate(false);
        using var vm = Create(results, store, gate);
        await vm.ActivateBatchAsync(Guid.NewGuid());
        results.SelectedItem = Assert.Single(results.Items);

        await vm.RetryFailedTransferAsync();

        Assert.Equal([LicenseOperationKind.Transfer], gate.Operations);
        Assert.Empty(store.RetriedOperations);
        Assert.Equal("软件尚未激活，操作未开始。请在「设置中心 → 软件授权」中激活后重试。", vm.StatusMessage);
    }

    [Theory]
    [InlineData("Completed", false)]
    [InlineData("CompletedWithShortfall", false)]
    [InlineData("Cancelled", false)]
    [InlineData("Completed", true)]
    public async Task TerminalTransitionNotifiesOnceAfterSnapshotEvenWhenStatisticsFail(string status, bool failStatistics)
    {
        var batch = Guid.NewGuid();
        var workflow = new FakeResultsWorkflow();
        var results = CreateResults(workflow);
        var store = new FakeRunControlStore();
        var history = new MutableHistorySource(new(batch, "运行批次", "Active"));
        var delay = new ControlledDelay();
        using var vm = new WorkspaceRunPanelViewModel(results, new(store), new(store), new(store),
            historySource: history, automaticRefreshDelay: delay);
        var notifications = new List<WorkspaceBatchTerminalEvent>();
        var progressStatuses = new List<string>();
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        results.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(results.StatusMessage) &&
                results.StatusMessage.StartsWith("进度 ", StringComparison.Ordinal))
                progressStatuses.Add(results.StatusMessage);
        };
        vm.BatchTerminal += (_, value) =>
        {
            Assert.Equal(status, vm.SelectedBatch?.BatchStatus);
            Assert.False(results.IsBusy);
            if (!failStatistics)
                Assert.Equal("进度 5/5：成功 3，失败 2，剩余 0。", results.StatusMessage);
            notifications.Add(value);
            notified.TrySetResult();
        };
        await vm.InitializeAsync();
        workflow.FailSummary = failStatistics;
        workflow.BeforeSummary = () => Assert.Equal(status, vm.SelectedBatch?.BatchStatus);
        history.Current = history.Current with { BatchStatus = status };
        delay.Release();
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var terminal = Assert.Single(notifications);
        Assert.Equal(batch, terminal.BatchJobId);
        Assert.Equal(failStatistics ? null : (long?)3L, terminal.CompleteSuccessCount);
        Assert.Equal(failStatistics ? null : (long?)2L, terminal.TerminalProblemCount);
        Assert.Equal(failStatistics ? 1 : 2, workflow.PageLoadCount);
        Assert.Equal(failStatistics ? 0 : 1, progressStatuses.Count);
        workflow.FailSummary = false;
        await results.RefreshAsync();
        await vm.InitializeAsync();
        vm.SelectedBatch = vm.SelectedBatch! with { BatchStatus = "Active" };
        Assert.Single(notifications);
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("CompletedWithShortfall")]
    [InlineData("Cancelled")]
    [InlineData("Paused")]
    [InlineData("AwaitingCandidates")]
    [InlineData("PausedReady")]
    public async Task HistoricalTerminalAndNonterminalIdleStatesDoNotNotify(string status)
    {
        var results = CreateResults(new FakeResultsWorkflow());
        var store = new FakeRunControlStore();
        using var vm = new WorkspaceRunPanelViewModel(results, new(store), new(store), new(store),
            historySource: new FakeHistorySource([new(Guid.NewGuid(), "历史批次", status)]));
        var notifications = new List<WorkspaceBatchTerminalEvent>();
        vm.BatchTerminal += (_, value) => notifications.Add(value);
        await vm.InitializeAsync();
        await vm.LoadSelectedBatchAsync();
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task LifecycleRefreshReportsStatisticsFailureWithoutThrowing()
    {
        var workflow = new FakeResultsWorkflow();
        var results = CreateResults(workflow);
        await results.InitializeAsync(Guid.NewGuid());
        Assert.True(await results.RefreshForLifecycleAsync());
        Assert.Equal(3L, results.CompleteSuccessCount);
        Assert.Equal(2L, results.TerminalProblemCount);
        workflow.FailSummary = true;
        Assert.False(await results.RefreshForLifecycleAsync());
        Assert.False(results.IsBusy);
    }

    [Fact]
    public async Task FinalizedBatchSuppressesLaterManualProgressAndRecoveryStatuses()
    {
        var batch = Guid.NewGuid();
        var workflow = new FakeResultsWorkflow();
        var results = CreateResults(workflow);
        var store = new FakeRunControlStore();
        var history = new MutableHistorySource(new(batch, "运行批次", "Active"));
        var delay = new ControlledDelay();
        using var vm = new WorkspaceRunPanelViewModel(results, new(store), new(store), new(store),
            historySource: history, automaticRefreshDelay: delay);
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.BatchTerminal += (_, _) => terminal.TrySetResult();
        await vm.InitializeAsync();
        history.Current = history.Current with { BatchStatus = "Completed" };
        delay.Release();
        await terminal.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var terminalStatus = results.StatusMessage;
        var pageLoadsAtTerminal = workflow.PageLoadCount;
        var statusChanges = 0;
        results.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(results.StatusMessage)) statusChanges++;
        };

        workflow.FailSummary = true;
        Assert.False(await results.RefreshForLifecycleAsync());
        workflow.FailSummary = false;
        Assert.False(await results.RefreshForLifecycleAsync());

        Assert.Equal(terminalStatus, results.StatusMessage);
        Assert.Equal(0, statusChanges);
        Assert.Equal(pageLoadsAtTerminal, workflow.PageLoadCount);
    }

    [Fact]
    public async Task TerminalTransitionWaitsForInFlightManualRefreshThenAppliesFinalSnapshot()
    {
        var batch = Guid.NewGuid();
        var workflow = new SerializingResultsWorkflow();
        var results = CreateResults(workflow);
        var store = new FakeRunControlStore();
        var history = new MutableHistorySource(new(batch, "运行批次", "Active"));
        var delay = new ControlledDelay();
        using var vm = new WorkspaceRunPanelViewModel(results, new(store), new(store), new(store),
            historySource: history, automaticRefreshDelay: delay);
        var ordering = new List<string>();
        var terminal = new TaskCompletionSource<WorkspaceBatchTerminalEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        results.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(results.StatusMessage) &&
                results.StatusMessage.StartsWith("进度 ", StringComparison.Ordinal))
                ordering.Add(results.StatusMessage);
        };
        vm.BatchTerminal += (_, value) =>
        {
            ordering.Add("terminal");
            terminal.TrySetResult(value);
        };
        await vm.InitializeAsync();

        var manualRefresh = results.RefreshForLifecycleAsync();
        await workflow.ManualRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        history.Current = history.Current with { BatchStatus = "Completed" };
        delay.Release();
        await history.SecondRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Yield();
        var terminalOvertookManualRefresh = terminal.Task.IsCompleted;

        workflow.ReleaseManualRefresh();
        Assert.True(await manualRefresh.WaitAsync(TimeSpan.FromSeconds(2)));
        var terminalEvent = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(terminalOvertookManualRefresh);
        Assert.Equal(3, workflow.PageLoadCount);
        Assert.Equal(4, results.CompleteSuccessCount);
        Assert.Equal(1, results.TerminalProblemCount);
        Assert.Equal(4, terminalEvent.CompleteSuccessCount);
        Assert.Equal(1, terminalEvent.TerminalProblemCount);
        Assert.Equal([
            "进度 3/5：成功 2，失败 1，剩余 2。",
            "进度 5/5：成功 4，失败 1，剩余 0。",
            "terminal",
        ], ordering);
    }

    [Fact]
    public async Task PresentedBatchReachingTerminalBeforeResultLoadIsLoadedFinalizedAndNotified()
    {
        var batch = Guid.NewGuid();
        var workflow = new FakeResultsWorkflow();
        var results = CreateResults(workflow);
        var store = new FakeRunControlStore();
        var history = new MutableHistorySource(new(batch, "新批次", "Active"));
        var delay = new ControlledDelay();
        using var vm = new WorkspaceRunPanelViewModel(results, new(store), new(store), new(store),
            historySource: history, automaticRefreshDelay: delay);
        var ordering = new List<string>();
        var terminal = new TaskCompletionSource<WorkspaceBatchTerminalEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        results.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(results.StatusMessage) &&
                results.StatusMessage.StartsWith("进度 ", StringComparison.Ordinal))
                ordering.Add(results.StatusMessage);
        };
        vm.BatchTerminal += (_, value) =>
        {
            ordering.Add("terminal");
            terminal.TrySetResult(value);
        };

        vm.PresentBatch(batch, "新批次", "Active");
        Assert.Equal(Guid.Empty, results.BatchJobId);
        history.Current = history.Current with { BatchStatus = "Completed" };
        delay.Release();
        var terminalEvent = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Yield();

        Assert.Equal(batch, results.BatchJobId);
        Assert.Equal(3, results.CompleteSuccessCount);
        Assert.Equal(2, results.TerminalProblemCount);
        Assert.Equal(3, terminalEvent.CompleteSuccessCount);
        Assert.Equal(2, terminalEvent.TerminalProblemCount);
        Assert.Equal([
            "进度 5/5：成功 3，失败 2，剩余 0。",
            "terminal",
        ], ordering);
        Assert.False(await results.RefreshForLifecycleAsync());
        Assert.Equal("terminal", ordering[^1]);
    }

    [Fact]
    public async Task PresentedBatchTerminalLoadCannotOverwriteNewSelectionAfterAwait()
    {
        var terminalBatch = Guid.NewGuid();
        var retainedBatch = Guid.NewGuid();
        var workflow = new NonCooperativeTerminalLoadWorkflow(terminalBatch, retainedBatch);
        var results = CreateResults(workflow);
        await results.InitializeAsync(retainedBatch);
        var retainedStatus = results.StatusMessage;
        var store = new FakeRunControlStore();
        var history = new MutableHistorySource(new(terminalBatch, "结束中的批次", "Active"));
        var delay = new ControlledDelay();
        using var vm = new WorkspaceRunPanelViewModel(results, new(store), new(store), new(store),
            historySource: history, automaticRefreshDelay: delay);
        var terminalNotifications = new List<WorkspaceBatchTerminalEvent>();
        vm.BatchTerminal += (_, value) => terminalNotifications.Add(value);

        vm.PresentBatch(terminalBatch, "结束中的批次", "Active");
        history.Current = history.Current with { BatchStatus = "Completed" };
        delay.Release();
        await workflow.TerminalLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var refreshFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        results.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(results.IsBusy) && !results.IsBusy)
                refreshFinished.TrySetResult();
        };
        vm.PresentBatch(retainedBatch, "仍在查看的批次", "Paused");
        workflow.ReleaseTerminalLoad();
        await refreshFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(retainedBatch, vm.SelectedBatch?.BatchJobId);
        Assert.Equal(retainedBatch, results.BatchJobId);
        Assert.Equal(retainedStatus, results.StatusMessage);
        Assert.Empty(terminalNotifications);
    }

    [Fact]
    public async Task InFlightUnloadedTerminalFinalizationRejectsSelectedBatchReload()
    {
        var batch = Guid.NewGuid();
        var workflow = new NonCooperativeTerminalLoadWorkflow(batch, Guid.NewGuid());
        var results = CreateResults(workflow);
        var store = new FakeRunControlStore();
        var history = new MutableHistorySource(new(batch, "即将结束的批次", "Active"));
        var delay = new ControlledDelay();
        using var vm = new WorkspaceRunPanelViewModel(results, new(store), new(store), new(store),
            historySource: history, automaticRefreshDelay: delay);
        var ordering = new List<string>();
        var terminal = new TaskCompletionSource<WorkspaceBatchTerminalEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        results.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(results.StatusMessage) &&
                results.StatusMessage.StartsWith("进度 ", StringComparison.Ordinal))
                ordering.Add(results.StatusMessage);
        };
        vm.BatchTerminal += (_, value) =>
        {
            ordering.Add("terminal");
            terminal.TrySetResult(value);
        };

        vm.PresentBatch(batch, "即将结束的批次", "Active");
        history.Current = history.Current with { BatchStatus = "Completed" };
        delay.Release();
        await workflow.TerminalLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var commandEnabledDuringFinalization = vm.LoadSelectedBatchCommand.CanExecute(null);
        var selectedBatchLoad = vm.LoadSelectedBatchAsync();
        await Task.Yield();
        var terminalLoadsBeforeRelease = workflow.TerminalSummaryLoadCount;

        workflow.ReleaseTerminalLoad();
        await selectedBatchLoad.WaitAsync(TimeSpan.FromSeconds(2));
        var terminalEvent = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(commandEnabledDuringFinalization);
        Assert.Equal(1, terminalLoadsBeforeRelease);
        Assert.Equal(4, terminalEvent.CompleteSuccessCount);
        Assert.Equal(1, terminalEvent.TerminalProblemCount);
        Assert.Equal([
            "进度 5/5：成功 4，失败 1，剩余 0。",
            "terminal",
        ], ordering);
        Assert.False(await results.RefreshForLifecycleAsync());
        Assert.Equal("terminal", ordering[^1]);
    }

    [Fact]
    public async Task DisposeDuringTerminalStatisticsReadPreventsLateNotification()
    {
        var workflow = new FakeResultsWorkflow();
        var results = CreateResults(workflow);
        var store = new FakeRunControlStore();
        var history = new MutableHistorySource(new(Guid.NewGuid(), "批次", "Active"));
        var delay = new ControlledDelay();
        using var vm = new WorkspaceRunPanelViewModel(results, new(store), new(store), new(store),
            historySource: history, automaticRefreshDelay: delay);
        var notifications = new List<WorkspaceBatchTerminalEvent>();
        vm.BatchTerminal += (_, value) => notifications.Add(value);
        await vm.InitializeAsync();
        var summaryRelease = new TaskCompletionSource<JobResultsSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        workflow.SummaryOverride = summaryRelease.Task;
        history.Current = history.Current with { BatchStatus = "Completed" };
        delay.Release();
        await workflow.SecondPageLoad.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Completed", vm.SelectedBatch?.BatchStatus);
        var refreshFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        results.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(results.IsBusy) && !results.IsBusy) refreshFinished.TrySetResult();
        };
        vm.Dispose();
        summaryRelease.SetResult(new(5, 5, 1, 0, 3, 2));
        await refreshFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task CreatedAndRestoredBatchesRemainSelectable()
    {
        var results = CreateResults(new FakeResultsWorkflow());
        var store = new FakeRunControlStore();
        using var vm = Create(results, store);
        var created = Guid.NewGuid();
        var restored = Guid.NewGuid();

        vm.PresentBatch(created, "刚刚创建");
        vm.PresentBatch(restored, "待处理批次");
        vm.SelectedBatch = vm.Batches.Single(item => item.BatchJobId == created);
        await vm.LoadSelectedBatchAsync();

        Assert.Equal(2, vm.Batches.Count);
        Assert.Equal(created, results.BatchJobId);
        Assert.Equal(created, vm.SelectedBatch!.BatchJobId);
    }

    [Fact]
    public async Task DurableRecentBatchIsLoadedAndSelectedAfterViewModelRestart()
    {
        var durable = new WorkspaceBatchItem(Guid.NewGuid(), "普通转存批次", "Queued", DateTimeOffset.UtcNow);
        var results = CreateResults(new FakeResultsWorkflow());
        var store = new FakeRunControlStore();
        using var vm = new WorkspaceRunPanelViewModel(
            results,
            new PauseBatchService(store),
            new ResumeBatchService(store),
            new RetryFailedTransferService(store),
            historySource: new FakeHistorySource([durable]));

        await vm.InitializeAsync();

        Assert.Equal(durable.BatchJobId, vm.SelectedBatch?.BatchJobId);
        Assert.Equal(durable.BatchJobId, results.BatchJobId);
    }

    [Theory]
    [InlineData("Queued", true, false)]
    [InlineData("Active", true, false)]
    [InlineData("Paused", false, true)]
    [InlineData("Completed", false, false)]
    [InlineData("Failed", false, false)]
    public void PauseAndResumeCommandsUseDurableBatchStatus(
        string status,
        bool canPause,
        bool canResume)
    {
        using var vm = Create(CreateResults(new FakeResultsWorkflow()), new FakeRunControlStore());
        vm.PresentBatch(Guid.NewGuid(), batchStatus: status);

        Assert.Equal(canPause, vm.PauseCommand.CanExecute(null));
        Assert.Equal(canResume, vm.ResumeCommand.CanExecute(null));
    }

    [Fact]
    public async Task SubmittedFailedRowIsNotExposedAsSafeRetry()
    {
        var unsafeRow = Row("Failed", "失败") with { CanSafelyRetryTransfer = false };
        var results = CreateResults(new FakeResultsWorkflow { Page = new([unsafeRow], 1, null) });
        using var vm = Create(results, new FakeRunControlStore());
        await vm.ActivateBatchAsync(Guid.NewGuid());
        results.SelectedItem = Assert.Single(results.Items);

        Assert.False(vm.RetryFailedTransferCommand.CanExecute(null));
    }

    [Fact]
    public async Task PauseResumeAndSafeFailedRetryUseTask3AServices()
    {
        var workflow = new FakeResultsWorkflow
        {
            Page = new([
                Row("Failed", "失败"),
            ], 1, null),
        };
        var results = CreateResults(workflow);
        var store = new FakeRunControlStore();
        using var vm = Create(results, store);
        var batch = Guid.NewGuid();
        await vm.ActivateBatchAsync(batch);
        results.SelectedItem = Assert.Single(results.Items);

        await vm.PauseAsync();
        await vm.ResumeAsync();
        results.SelectedItem = Assert.Single(results.Items);
        var retriedOperation = results.SelectedItem.TransferOperationId;
        await vm.RetryFailedTransferAsync();

        Assert.Equal([batch], store.PausedBatches);
        Assert.Equal([batch], store.ResumedBatches);
        Assert.Equal([retriedOperation], store.RetriedOperations);
    }

    [Theory]
    [InlineData("ResultPending", "结果待确认")]
    [InlineData("NeedsDecision", "需要人工判断")]
    [InlineData("Submitting", "正在提交")]
    public async Task UnsafeTransferStateIsNeverExposedAsOrdinaryRetry(string rawStatus, string statusText)
    {
        var workflow = new FakeResultsWorkflow { Page = new([Row(rawStatus, statusText)], 1, null) };
        var results = CreateResults(workflow);
        var store = new FakeRunControlStore();
        using var vm = Create(results, store);
        await vm.ActivateBatchAsync(Guid.NewGuid());
        results.SelectedItem = Assert.Single(results.Items);

        Assert.False(vm.RetryFailedTransferCommand.CanExecute(null));
        await vm.RetryFailedTransferAsync();
        Assert.Empty(store.RetriedOperations);
    }

    [Fact]
    public async Task ClosePreventsLateRunActionMutation()
    {
        var results = CreateResults(new FakeResultsWorkflow());
        var store = new BlockingRunControlStore();
        using var vm = Create(results, store);
        await vm.ActivateBatchAsync(Guid.NewGuid());
        var status = vm.StatusMessage;

        var pause = vm.PauseAsync();
        await store.PauseStarted.Task;
        vm.Close();
        store.Release();
        await pause;

        Assert.Equal(status, vm.StatusMessage);
    }

    [Fact]
    public async Task CallerCancellationRestoresRunActionAvailability()
    {
        var results = CreateResults(new FakeResultsWorkflow());
        var store = new BlockingRunControlStore();
        using var vm = Create(results, store);
        await vm.ActivateBatchAsync(Guid.NewGuid());
        using var cancellation = new CancellationTokenSource();

        var pause = vm.PauseAsync(cancellation.Token);
        await store.PauseStarted.Task;
        cancellation.Cancel();
        store.Release();
        await pause;

        Assert.False(vm.IsBusy);
        Assert.True(vm.CanUseBatchActions);
    }

    [Fact]
    public async Task ActiveBatchRefreshesUntilHistoryReportsTerminalStatus()
    {
        var batch = Guid.NewGuid();
        var workflow = new FakeResultsWorkflow();
        var results = CreateResults(workflow);
        var store = new FakeRunControlStore();
        var history = new MutableHistorySource(
            new WorkspaceBatchItem(batch, "运行批次", "Active", DateTimeOffset.UtcNow));
        var delay = new ControlledDelay();
        using var vm = new WorkspaceRunPanelViewModel(
            results,
            new PauseBatchService(store),
            new ResumeBatchService(store),
            new RetryFailedTransferService(store),
            historySource: history,
            automaticRefreshDelay: delay,
            automaticRefreshInterval: TimeSpan.FromSeconds(1));
        var progressStatuses = new List<string>();
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        results.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(results.StatusMessage) &&
                results.StatusMessage.StartsWith("进度 ", StringComparison.Ordinal))
                progressStatuses.Add(results.StatusMessage);
        };
        vm.BatchTerminal += (_, _) => terminal.TrySetResult();

        await vm.InitializeAsync();
        history.Current = history.Current with { BatchStatus = "Completed" };
        delay.Release();
        await terminal.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Yield();

        Assert.Equal("Completed", vm.SelectedBatch?.BatchStatus);
        Assert.Equal(2, workflow.PageLoadCount);
        Assert.Equal(["进度 5/5：成功 3，失败 2，剩余 0。"], progressStatuses);
    }

    [Theory]
    [InlineData("Queued", "暂停", true, true)]
    [InlineData("Active", "暂停", true, true)]
    [InlineData("Paused", "继续", true, true)]
    [InlineData("Completed", "暂停", false, false)]
    [InlineData("CompletedWithShortfall", "暂停", false, false)]
    [InlineData("Cancelled", "暂停", false, false)]
    public void OnePauseResumeButtonAndEndFollowTheBatchStatus(string status, string text, bool canToggle, bool canEnd)
    {
        using var vm = CreateWithEnd(CreateResults(new FakeResultsWorkflow()), new FakeRunControlStore(), new ConfirmDialog());
        vm.PresentBatch(Guid.NewGuid(), batchStatus: status);

        Assert.Equal(text, vm.PauseResumeText);
        Assert.Equal(canToggle, vm.PauseResumeCommand.CanExecute(null));
        Assert.Equal(canEnd, vm.EndBatchCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheToggleButtonPausesThenResumesTheSelectedBatch()
    {
        var store = new FakeRunControlStore();
        using var vm = CreateWithEnd(CreateResults(new FakeResultsWorkflow()), store, new ConfirmDialog());
        var batch = Guid.NewGuid();
        vm.PresentBatch(batch, batchStatus: "Active");

        await vm.PauseResumeCommand.ExecuteAsync(null);
        Assert.Equal("继续", vm.PauseResumeText);
        await vm.PauseResumeCommand.ExecuteAsync(null);

        Assert.Equal([batch], store.PausedBatches);
        Assert.Equal([batch], store.ResumedBatches);
        Assert.Equal("暂停", vm.PauseResumeText);
    }

    [Fact]
    public void WithoutTheEndServiceTheEndButtonIsUnavailable()
    {
        using var vm = Create(CreateResults(new FakeResultsWorkflow()), new FakeRunControlStore());
        vm.PresentBatch(Guid.NewGuid(), batchStatus: "Active");

        Assert.False(vm.EndBatchCommand.CanExecute(null));
    }

    [Fact]
    public async Task EndingAsksFirstThenEndsTheSelectedBatchAndReportsItOnce()
    {
        var store = new FakeRunControlStore();
        store.EndResults.Enqueue(new(EndBatchDisposition.Ended, 1355, 0));
        var dialog = new ConfirmDialog();
        var results = CreateResults(new FakeResultsWorkflow());
        using var vm = CreateWithEnd(results, store, dialog);
        var batch = Guid.NewGuid();
        await vm.ActivateBatchAsync(batch);
        var terminals = new List<WorkspaceBatchTerminalEvent>();
        vm.BatchTerminal += (_, e) => terminals.Add(e);

        await vm.EndBatchCommand.ExecuteAsync(null);

        Assert.Contains("已经转存成功的不受影响", Assert.Single(dialog.Messages));
        Assert.Equal([batch], store.EndedBatches);
        Assert.Equal("Cancelled", vm.SelectedBatch!.BatchStatus);
        Assert.Contains("1,355 条未开始的记录已取消", vm.StatusMessage);
        Assert.Contains("清空转存历史", vm.StatusMessage);
        var terminal = Assert.Single(terminals);
        Assert.Equal((batch, "Cancelled"), (terminal.BatchJobId, terminal.BatchStatus));
        Assert.False(vm.EndBatchCommand.CanExecute(null));
        Assert.False(vm.PauseResumeCommand.CanExecute(null));
    }

    [Fact]
    public async Task DecliningTheEndConfirmationChangesNothing()
    {
        var store = new FakeRunControlStore();
        using var vm = CreateWithEnd(CreateResults(new FakeResultsWorkflow()), store, new ConfirmDialog { Answer = false });
        vm.PresentBatch(Guid.NewGuid(), batchStatus: "Active");

        await vm.EndBatchCommand.ExecuteAsync(null);

        Assert.Empty(store.EndedBatches);
        Assert.Equal("Active", vm.SelectedBatch!.BatchStatus);
    }

    [Fact]
    public async Task WorkStillSubmittingIsLeftToFinishAndTheBatchEndsByItselfAfterwards()
    {
        var store = new FakeRunControlStore();
        store.EndResults.Enqueue(new(EndBatchDisposition.Stopping, 10, 2));
        store.EndResults.Enqueue(new(EndBatchDisposition.Stopping, 0, 1));
        store.EndResults.Enqueue(new(EndBatchDisposition.Ended, 0, 0));
        var delay = new ControlledDelay();
        using var vm = CreateWithEnd(CreateResults(new FakeResultsWorkflow()), store, new ConfirmDialog(), delay);
        var batch = Guid.NewGuid();
        vm.PresentBatch(batch, batchStatus: "Active");
        var terminals = new List<WorkspaceBatchTerminalEvent>();
        vm.BatchTerminal += (_, e) => terminals.Add(e);

        await vm.EndBatchCommand.ExecuteAsync(null);

        Assert.True(vm.IsEndingBatch);
        // 只剩收尾，批次照常运行，进度继续自动刷新。
        Assert.Equal("Active", vm.SelectedBatch!.BatchStatus);
        Assert.Contains("还有 2 条已经提交", vm.StatusMessage);
        Assert.Contains("2 分钟", vm.StatusMessage);
        Assert.Empty(terminals);

        delay.Release();
        await WaitUntilAsync(() => !vm.IsEndingBatch);

        Assert.Equal([batch, batch, batch], store.EndedBatches);
        Assert.Equal("Cancelled", vm.SelectedBatch!.BatchStatus);
        Assert.Equal("Cancelled", Assert.Single(terminals).BatchStatus);
    }

    [Fact]
    public async Task PausingWhileWaitingToEndStopsTheAutomaticEnd()
    {
        var store = new FakeRunControlStore();
        store.EndResults.Enqueue(new(EndBatchDisposition.Stopping, 10, 2));
        var delay = new ControlledDelay();
        using var vm = CreateWithEnd(CreateResults(new FakeResultsWorkflow()), store, new ConfirmDialog(), delay);
        var batch = Guid.NewGuid();
        vm.PresentBatch(batch, batchStatus: "Active");
        await vm.EndBatchCommand.ExecuteAsync(null);
        Assert.True(vm.IsEndingBatch);

        await vm.PauseResumeCommand.ExecuteAsync(null);
        delay.Release();
        await Task.Yield();

        Assert.False(vm.IsEndingBatch);
        Assert.Equal([batch], store.PausedBatches);
        Assert.Equal([batch], store.EndedBatches);
        Assert.Equal("Paused", vm.SelectedBatch!.BatchStatus);
    }

    [Fact]
    public async Task ResumingBehindAnotherRunningBatchSaysItIsQueued()
    {
        var store = new FakeRunControlStore();
        var running = new WorkspaceBatchItem(Guid.NewGuid(), "批次 昨晚那批", "Active");
        var paused = new WorkspaceBatchItem(Guid.NewGuid(), "批次 今天这批", "Paused");
        using var vm = new WorkspaceRunPanelViewModel(
            CreateResults(new FakeResultsWorkflow()), new PauseBatchService(store), new ResumeBatchService(store),
            new RetryFailedTransferService(store), historySource: new FakeHistorySource([paused, running]));
        await vm.InitializeAsync();
        Assert.Equal(paused.BatchJobId, vm.SelectedBatch!.BatchJobId);

        await vm.PauseResumeCommand.ExecuteAsync(null);

        Assert.Equal([paused.BatchJobId], store.ResumedBatches);
        Assert.Contains("正在排队", vm.StatusMessage);
        Assert.Contains("批次 昨晚那批", vm.StatusMessage);
        Assert.Contains("「停止」", vm.StatusMessage);
    }

    [Fact]
    public async Task ResumingWithNothingElseRunningKeepsTheUsualMessage()
    {
        var store = new FakeRunControlStore();
        using var vm = Create(CreateResults(new FakeResultsWorkflow()), store);
        vm.PresentBatch(Guid.NewGuid(), batchStatus: "Paused");

        await vm.PauseResumeCommand.ExecuteAsync(null);

        Assert.Equal("批次已恢复并重新进入队列。", vm.StatusMessage);
    }

    [Fact]
    public void TransferResultsTabOffersPauseResumeAndEndNextToTheOtherBatchActions()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src"))) root = root.Parent;
        Assert.NotNull(root);
        var document = System.Xml.Linq.XDocument.Load(Path.Combine(
            root.FullName, "src", "BaiduBatchTransfer.App", "Features", "Transfers", "TransferWorkspaceView.xaml"));
        var x = System.Xml.Linq.XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        System.Xml.Linq.XElement Button(string name) => Assert.Single(document.Descendants(),
            node => node.Name.LocalName == "Button" && (string?)node.Attribute(x + "Name") == name);

        var toggle = Button("PauseResumeTransferButton");
        var end = Button("EndTransferBatchButton");
        Assert.Equal("{Binding RunPanel.PauseResumeText}", (string?)toggle.Attribute("Content"));
        Assert.Equal("{Binding RunPanel.PauseResumeCommand}", (string?)toggle.Attribute("Command"));
        Assert.Equal("{Binding RunPanel.EndBatchCommand}", (string?)end.Attribute("Command"));
        foreach (var button in new[] { toggle, end })
        {
            Assert.Contains(button.Ancestors(), node => node.Name.LocalName == "TabItem" && (string?)node.Attribute("Header") == "处理结果");
            Assert.NotNull(button.Attribute("ToolTip"));
            Assert.Equal("True", (string?)button.Attribute("ToolTipService.ShowOnDisabled"));
        }
        Assert.Equal("停止", (string?)end.Attribute("Content"));
        // 与重试、清空历史在同一行，顺序：暂停/继续、停止、重试所选失败项、清空转存历史。
        var row = toggle.Parent!.Elements().Where(node => node.Name.LocalName == "Button").ToArray();
        Assert.Equal(["PauseResumeTransferButton", "EndTransferBatchButton", "RetryFailedTransferButton"],
            row.Take(3).Select(node => (string?)node.Attribute(x + "Name")));
        Assert.Equal("清空转存历史", (string?)row[3].Attribute("Content"));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++) await Task.Delay(10);
        Assert.True(condition());
    }

    private static WorkspaceRunPanelViewModel CreateWithEnd(
        JobResultsViewModel results,
        IBatchRunControlStore store,
        IDialogService dialog,
        IAsyncDelay? delay = null) =>
        new(
            results,
            new PauseBatchService(store),
            new ResumeBatchService(store),
            new RetryFailedTransferService(store),
            automaticRefreshDelay: delay,
            endService: new EndBatchService(store),
            dialogService: dialog);

    private sealed class ConfirmDialog : IDialogService
    {
        public bool Answer { get; init; } = true;
        public List<string> Messages { get; } = [];
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.FromResult(Answer);
        }
        public Task ShowMessageAsync(string title, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> PickExcelFileAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private static WorkspaceRunPanelViewModel Create(
        JobResultsViewModel results,
        IBatchRunControlStore store,
        ILicenseAuthorizationGate? licenseGate = null) =>
        new(
            results,
            new PauseBatchService(store),
            new ResumeBatchService(store),
            new RetryFailedTransferService(store),
            licenseGate: licenseGate);

    private static JobResultsViewModel CreateResults(IJobResultsWorkflow workflow) =>
        new(workflow, new FakeResultSaveDialog(), new AvailableCapabilities());

    private static JobResultRow Row(string rawStatus, string statusText) => new(
        Guid.NewGuid(), null, 1, "账号", "资源", statusText, "未启用分享", null, null,
        DateTimeOffset.UtcNow, null, null, false, false, false,
        TransferStatus: rawStatus,
        CanSafelyRetryTransfer: rawStatus == "Failed");

    private sealed class FakeResultsWorkflow : IJobResultsWorkflow
    {
        public JobResultsPage Page { get; init; } = new([], 0, null);
        public int PageLoadCount { get; private set; }
        public TaskCompletionSource SecondPageLoad { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailSummary { get; set; }
        public Task<JobResultsSummary>? SummaryOverride { get; set; }
        public Action? BeforeSummary { get; set; }
        public Task<JobResultsSummary> LoadSummaryAsync(Guid batchJobId, CancellationToken cancellationToken = default)
        {
            BeforeSummary?.Invoke();
            if (FailSummary) throw new IOException("private failure detail");
            if (SummaryOverride is { } pending) return pending;
            return Task.FromResult(new JobResultsSummary(5, 5, 1, 0, 3, 2));
        }
        public Task<JobResultsPage> LoadPageAsync(Guid batchJobId, long? afterCursor, int pageSize, CancellationToken cancellationToken = default)
        {
            PageLoadCount++;
            if (PageLoadCount >= 2) SecondPageLoad.TrySetResult();
            return Task.FromResult(Page);
        }
        public Task<JobResultActionOutcome> RetryShareOnlyAsync(JobResultRow row, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ReconcileTransferAsync(Guid transferOperationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ReconcileShareAsync(Guid shareOperationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ResumeTransferAsync(JobResultRow row, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ResumeShareAsync(JobResultRow row, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> ExportAsync(Guid batchJobId, string destinationDirectory, IProgress<long> progress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SerializingResultsWorkflow : IJobResultsWorkflow
    {
        private readonly TaskCompletionSource<JobResultsSummary> manualSummary = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int summaryLoadCount;
        public int PageLoadCount { get; private set; }
        public TaskCompletionSource ManualRefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseManualRefresh() => manualSummary.TrySetResult(new(5, 5, 1, 0, 2, 1));
        public Task<JobResultsSummary> LoadSummaryAsync(Guid batchJobId, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref summaryLoadCount);
            if (call == 2)
            {
                ManualRefreshStarted.TrySetResult();
                return manualSummary.Task;
            }
            return Task.FromResult(call == 1
                ? new JobResultsSummary(5, 5, 0, 0, 0, 0)
                : new JobResultsSummary(5, 5, 1, 0, 4, 1));
        }
        public Task<JobResultsPage> LoadPageAsync(Guid batchJobId, long? afterCursor, int pageSize, CancellationToken cancellationToken = default)
        {
            PageLoadCount++;
            return Task.FromResult(new JobResultsPage([], 5, null));
        }
        public Task<JobResultActionOutcome> RetryShareOnlyAsync(JobResultRow row, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ReconcileTransferAsync(Guid transferOperationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ReconcileShareAsync(Guid shareOperationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ResumeTransferAsync(JobResultRow row, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ResumeShareAsync(JobResultRow row, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> ExportAsync(Guid batchJobId, string destinationDirectory, IProgress<long> progress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NonCooperativeTerminalLoadWorkflow(Guid terminalBatch, Guid retainedBatch) : IJobResultsWorkflow
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int terminalSummaryLoadCount;
        public TaskCompletionSource TerminalLoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int TerminalSummaryLoadCount => terminalSummaryLoadCount;
        public void ReleaseTerminalLoad() => release.TrySetResult();
        public async Task<JobResultsSummary> LoadSummaryAsync(Guid batchJobId, CancellationToken cancellationToken = default)
        {
            if (batchJobId == terminalBatch)
            {
                Interlocked.Increment(ref terminalSummaryLoadCount);
                TerminalLoadStarted.TrySetResult();
                await release.Task;
                return new(5, 5, 1, 0, 4, 1);
            }
            Assert.Equal(retainedBatch, batchJobId);
            return new(2, 2, 0, 0, 1, 0);
        }
        public Task<JobResultsPage> LoadPageAsync(Guid batchJobId, long? afterCursor, int pageSize, CancellationToken cancellationToken = default) =>
            Task.FromResult(new JobResultsPage([], batchJobId == terminalBatch ? 5 : 2, null));
        public Task<JobResultActionOutcome> RetryShareOnlyAsync(JobResultRow row, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ReconcileTransferAsync(Guid transferOperationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ReconcileShareAsync(Guid shareOperationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ResumeTransferAsync(JobResultRow row, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ResumeShareAsync(JobResultRow row, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> ExportAsync(Guid batchJobId, string destinationDirectory, IProgress<long> progress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeHistorySource(IReadOnlyList<WorkspaceBatchItem> items)
        : IWorkspaceBatchHistorySource
    {
        public Task<IReadOnlyList<WorkspaceBatchItem>> LoadRecentAsync(
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkspaceBatchItem>>(items.Take(limit).ToArray());
    }

    private sealed class MutableHistorySource(WorkspaceBatchItem current) : IWorkspaceBatchHistorySource
    {
        public WorkspaceBatchItem Current { get; set; } = current;
        public TaskCompletionSource SecondRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int readCount;

        public Task<IReadOnlyList<WorkspaceBatchItem>> LoadRecentAsync(
            int limit,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref readCount) >= 2) SecondRead.TrySetResult();
            return Task.FromResult<IReadOnlyList<WorkspaceBatchItem>>([Current]);
        }
    }

    private sealed class ControlledDelay : IAsyncDelay
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            release.Task.WaitAsync(cancellationToken);
        public void Release() => release.TrySetResult();
    }

    private class FakeRunControlStore : IBatchRunControlStore
    {
        public List<Guid> PausedBatches { get; } = [];
        public List<Guid> ResumedBatches { get; } = [];
        public List<Guid> RetriedOperations { get; } = [];
        public List<Guid> EndedBatches { get; } = [];
        public Queue<EndBatchResult> EndResults { get; } = new();
        public Task<EndBatchResult> TryEndBatchAsync(Guid batchJobId, DateTimeOffset endedAt, CancellationToken cancellationToken = default)
        {
            EndedBatches.Add(batchJobId);
            return Task.FromResult(EndResults.Count > 0 ? EndResults.Dequeue() : new EndBatchResult(EndBatchDisposition.Ended, 0, 0));
        }
        public virtual Task<PauseBatchDisposition> TryPauseBatchAsync(Guid batchJobId, CancellationToken cancellationToken = default)
        {
            PausedBatches.Add(batchJobId);
            return Task.FromResult(PauseBatchDisposition.Paused);
        }
        public virtual Task<ResumeBatchDisposition> TryResumeBatchAsync(Guid batchJobId, CancellationToken cancellationToken = default)
        {
            ResumedBatches.Add(batchJobId);
            return Task.FromResult(ResumeBatchDisposition.Queued);
        }
        public virtual Task<RetryFailedTransferDisposition> TryRetryFailedTransferAsync(Guid operationId, DateTimeOffset retriedAt, CancellationToken cancellationToken = default)
        {
            RetriedOperations.Add(operationId);
            return Task.FromResult(RetryFailedTransferDisposition.Requeued);
        }
    }

    private sealed class RecordingLicenseGate(bool isAuthorized) : ILicenseAuthorizationGate
    {
        public List<LicenseOperationKind> Operations { get; } = [];

        public Task<LicenseGateResult> AuthorizeAsync(
            LicenseOperationKind operation,
            CancellationToken cancellationToken)
        {
            Operations.Add(operation);
            return Task.FromResult(new LicenseGateResult(
                operation,
                isAuthorized,
                UsedCachedLease: false,
                isAuthorized ? LicenseCodes.Ok : "activation_required"));
        }
    }

    private sealed class BlockingRunControlStore : FakeRunControlStore
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PauseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<PauseBatchDisposition> TryPauseBatchAsync(Guid batchJobId, CancellationToken cancellationToken = default)
        {
            PauseStarted.TrySetResult();
            await release.Task;
            cancellationToken.ThrowIfCancellationRequested();
            return PauseBatchDisposition.Paused;
        }
        public void Release() => release.TrySetResult();
    }

    private sealed class FakeResultSaveDialog : IResultSaveDialog
    {
        public Task<string?> PickDestinationDirectoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class AvailableCapabilities : IJobResultCapabilityAvailability
    {
        public bool CanUseTransferActions => true;
        public bool CanUseShareActions => true;
        public string TransferStatusMessage => string.Empty;
        public string ShareStatusMessage => string.Empty;
    }
}
