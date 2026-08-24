using BaiduBatchTransfer.App.Features.Common;
using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Jobs;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class WorkspaceRunPanelViewModelTests
{
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

    private static WorkspaceRunPanelViewModel Create(JobResultsViewModel results, IBatchRunControlStore store) =>
        new(
            results,
            new PauseBatchService(store),
            new ResumeBatchService(store),
            new RetryFailedTransferService(store));

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
        public Task<JobResultsSummary> LoadSummaryAsync(Guid batchJobId, CancellationToken cancellationToken = default) => Task.FromResult(new JobResultsSummary(Page.TotalCount, Page.TotalCount, 0, 0));
        public Task<JobResultsPage> LoadPageAsync(Guid batchJobId, long? afterCursor, int pageSize, CancellationToken cancellationToken = default) => Task.FromResult(Page);
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

    private class FakeRunControlStore : IBatchRunControlStore
    {
        public List<Guid> PausedBatches { get; } = [];
        public List<Guid> ResumedBatches { get; } = [];
        public List<Guid> RetriedOperations { get; } = [];
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
