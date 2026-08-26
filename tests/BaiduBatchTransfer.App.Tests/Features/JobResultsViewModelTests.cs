using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Services;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class JobResultsViewModelTests
{
    [Fact]
    public async Task HandoffLoadPropagatesFailureWhileOrdinaryUiLoadKeepsFriendlyStatus()
    {
        var workflow = new ThrowingLoadWorkflow();
        var vm = Create(workflow);

        await vm.InitializeAsync(Guid.NewGuid());
        Assert.Equal("任务结果读取失败，请稍后重试。", vm.StatusMessage);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            vm.InitializeForHandoffAsync(Guid.NewGuid()));
        Assert.Equal("任务结果读取失败，请稍后重试。", vm.StatusMessage);
    }
    [Fact]
    public async Task InitializesWithBoundedPageAndChineseFallbackStatuses()
    {
        var workflow = new FakeWorkflow
        {
            Summary = new(20, 20, 1, 2),
            Pages = [new([Row(1, transferText: "Succeeded", shareText: "")], 20, 1)],
        };
        var vm = Create(workflow);

        await vm.InitializeAsync(Guid.NewGuid());

        Assert.Equal(JobResultsViewModel.PageSize, workflow.RequestedPageSizes.Single());
        Assert.Equal("状态未知", Assert.Single(vm.Items).TransferStatusText);
        Assert.Equal("状态未知", vm.Items[0].ShareStatusText);
        Assert.Equal(20, vm.TotalCount);
        Assert.Equal(1, vm.FailedCount);
        Assert.True(vm.LoadNextPageCommand.CanExecute(null));
    }

    [Fact]
    public async Task DisplaysSanitizedPersistedDirectoryPreparationStatus()
    {
        var message = "目录创建结果不确定；已确认创建到：/base/甲";
        var workflow = new FakeWorkflow
        {
            Summary = new(1, 1, 0, 0),
            Pages = [new([Row(1) with { TransferDetailMessage = message }], 1, null)],
        };
        var vm = Create(workflow);

        await vm.InitializeAsync(Guid.NewGuid());

        Assert.Equal(message, Assert.Single(vm.Items).TransferDetailMessage);
    }

    [Fact]
    public async Task LatePageFromPreviousBatchDoesNotOverwriteNewBatch()
    {
        var firstBatch = Guid.NewGuid();
        var secondBatch = Guid.NewGuid();
        var workflow = new DelayedWorkflow(firstBatch, secondBatch);
        var vm = Create(workflow);

        var first = vm.InitializeAsync(firstBatch);
        await workflow.FirstStarted.Task;
        await vm.InitializeAsync(secondBatch);
        workflow.ReleaseFirst();
        await first;

        Assert.Equal(secondBatch, vm.BatchJobId);
        Assert.Equal(2, Assert.Single(vm.Items).CandidateSequence);
    }

    [Fact]
    public async Task DuplicateOperationFromNextPageIsRejectedWithoutAppending()
    {
        var row = Row(1);
        var workflow = new FakeWorkflow
        {
            Pages = [new([row], 2, 1), new([row], 2, null)],
        };
        var vm = Create(workflow);
        await vm.InitializeAsync(Guid.NewGuid());

        await vm.LoadNextPageAsync();

        Assert.Single(vm.Items);
        Assert.Equal("下一页读取失败，请稍后重试。", vm.StatusMessage);
    }

    [Fact]
    public async Task AutomaticExistingSharePromptIsRestoredOncePerPendingBatch()
    {
        var batch = Guid.NewGuid();
        var interaction = new FakeExistingShareInteraction(batch);
        var vm = Create(new FakeWorkflow(), existingShareInteraction: interaction);

        var first = await vm.RestorePendingExistingShareDecisionAsync();
        var second = await vm.RestorePendingExistingShareDecisionAsync();

        Assert.Equal(batch, first);
        Assert.Null(second);
        Assert.Equal(1, interaction.ResolveCalls);
        Assert.All(interaction.RequestedMaximumCounts, count => Assert.Equal(100, count));
    }

    [Fact]
    public async Task CompletedExistingShareDecisionAllowsLaterEvidenceInTheSameBatchToPromptAgain()
    {
        var batch = Guid.NewGuid();
        var interaction = new FakeExistingShareInteraction(batch) { Complete = true };
        var vm = Create(new FakeWorkflow(), existingShareInteraction: interaction);

        Assert.Equal(batch, await vm.RestorePendingExistingShareDecisionAsync());
        Assert.Equal(batch, await vm.RestorePendingExistingShareDecisionAsync());

        Assert.Equal(2, interaction.ResolveCalls);
    }

    [Fact]
    public async Task ShareRetryIsIndependentFromTransferGateAndUsesSelectedShareOperation()
    {
        var workflow = new FakeWorkflow { Pages = [new([Row(1)], 1, null)] };
        var capabilities = new FakeCapabilities { Share = true, Transfer = false };
        var vm = Create(workflow, capabilities);
        await vm.InitializeAsync(Guid.NewGuid());
        vm.SelectedItem = vm.Items[0];
        var selectedShare = vm.SelectedItem.ShareOperationId;

        Assert.True(vm.RetryShareOnlyCommand.CanExecute(null));
        Assert.False(vm.ReconcileTransferCommand.CanExecute(null));
        await vm.RetryShareOnlyCommand.ExecuteAsync(null);

        Assert.Equal(selectedShare, workflow.RetriedShareOperationId);
        Assert.Equal("分享已重新排队。", vm.StatusMessage);
        Assert.Equal(2, workflow.RequestedPageSizes.Count);
    }

    [Fact]
    public async Task PausedTransferAndShareResumeCommandsHaveIndependentCapabilityGates()
    {
        var paused = Row(1) with { CanResumeTransfer = true, CanResumeShare = true };
        var workflow = new FakeWorkflow { Pages = [new([paused], 1, null)] };
        var vm = Create(workflow, new FakeCapabilities { Transfer = true, Share = false });
        await vm.InitializeAsync(Guid.NewGuid());
        vm.SelectedItem = vm.Items[0];

        Assert.True(vm.ResumeTransferCommand.CanExecute(null));
        Assert.False(vm.ResumeShareCommand.CanExecute(null));

        await vm.ResumeTransferCommand.ExecuteAsync(null);
        Assert.Equal("转存任务已恢复。", vm.StatusMessage);
    }

    [Fact]
    public async Task SavingCanBeCancelledWithoutLeakingDestinationPath()
    {
        var workflow = new FakeWorkflow
        {
            Pages = [new([Row(1)], 1, null)],
            BlockExport = true,
        };
        var vm = Create(workflow, saveDialog: new FakeSaveDialog("C:\\private\\secret"));
        await vm.InitializeAsync(Guid.NewGuid());

        var saving = vm.SaveAsAsync();
        await workflow.ExportStarted.Task;
        Assert.True(vm.CancelSaveCommand.CanExecute(null));
        vm.CancelSaveCommand.Execute(null);
        await saving;

        Assert.Equal("另存结果表格已取消；临时文件会尽力清理。", vm.StatusMessage);
        Assert.DoesNotContain("private", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExportFromPreviousBatchCannotOverwriteNewBatchStatus()
    {
        var workflow = new FakeWorkflow
        {
            Pages = [new([Row(1)], 1, null)],
            BlockExport = true,
        };
        var vm = Create(workflow, saveDialog: new FakeSaveDialog("C:\\private\\secret"));
        await vm.InitializeAsync(Guid.NewGuid());
        var saving = vm.SaveAsAsync();
        await workflow.ExportStarted.Task;

        var newBatch = Guid.NewGuid();
        await vm.InitializeAsync(newBatch);
        await saving;

        Assert.Equal(newBatch, vm.BatchJobId);
        Assert.Equal("已读取 1 / 1 条操作结果。", vm.StatusMessage);
        Assert.False(vm.IsSaving);
    }

    [Fact]
    public void ViewUsesVirtualizedBoundedGridAndSeparateChineseStatusColumns()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "Features", "Jobs", "JobResultsView.xaml"));

        Assert.Contains("EnableRowVirtualization=\"True\"", xaml);
        Assert.Contains("VirtualizationMode=\"Recycling\"", xaml);
        Assert.Contains("Header=\"转存状态\"", xaml);
        Assert.Contains("Header=\"分享状态\"", xaml);
        Assert.Contains("Content=\"仅重试分享\"", xaml);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml);
    }

    private static JobResultsViewModel Create(
        IJobResultsWorkflow workflow,
        IJobResultCapabilityAvailability? capabilities = null,
        IResultSaveDialog? saveDialog = null,
        IExistingShareDecisionInteraction? existingShareInteraction = null) =>
        new(workflow, saveDialog ?? new FakeSaveDialog(null), capabilities ?? new FakeCapabilities { Share = true, Transfer = true },
            existingShareInteraction);

    private static JobResultRow Row(long sequence, string transferText = "转存成功", string shareText = "分享失败") =>
        new(Guid.NewGuid(), Guid.NewGuid(), sequence, "账号", "标题", transferText, shareText,
            null, "ShareFailed", DateTimeOffset.Parse("2026-08-20T00:00:00Z"), 1,
            DateTimeOffset.Parse("2026-08-20T00:00:00Z"), true, true, true);

    private static string FindRepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BaiduBatchTransfer.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException();
    }

    private sealed class FakeSaveDialog(string? directory) : IResultSaveDialog
    {
        public Task<string?> PickDestinationDirectoryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(directory);
    }

    private sealed class FakeCapabilities : IJobResultCapabilityAvailability
    {
        public bool Transfer { get; init; }
        public bool Share { get; init; }
        public bool CanUseTransferActions => Transfer;
        public bool CanUseShareActions => Share;
        public string TransferStatusMessage => Transfer ? "转存操作可用。" : "转存操作不可用。";
        public string ShareStatusMessage => Share ? "分享操作可用。" : "分享操作不可用。";
    }

    private sealed class FakeExistingShareInteraction(Guid batchId) : IExistingShareDecisionInteraction
    {
        public bool Complete { get; init; }
        public int ResolveCalls { get; private set; }
        public List<int> RequestedMaximumCounts { get; } = [];

        public Task<IReadOnlyList<Guid>> GetPendingBatchIdsAsync(int maximumCount = 1, CancellationToken cancellationToken = default)
        {
            RequestedMaximumCounts.Add(maximumCount);
            return Task.FromResult<IReadOnlyList<Guid>>([batchId]);
        }

        public Task<ExistingShareInteractionOutcome> ResolveAsync(Guid batchJobId, CancellationToken cancellationToken = default)
        {
            ResolveCalls++;
            return Task.FromResult(Complete
                ? new ExistingShareInteractionOutcome(true, false, false, "决定已保存。")
                : new ExistingShareInteractionOutcome(false, true, false, "已关闭确认。"));
        }
    }

    private sealed class FakeWorkflow : IJobResultsWorkflow
    {
        private int pageIndex;
        public JobResultsSummary Summary { get; init; } = new(1, 1, 0, 0);
        public IReadOnlyList<JobResultsPage> Pages { get; init; } = [new([Row(1)], 1, null)];
        public List<int> RequestedPageSizes { get; } = [];
        public Guid? RetriedShareOperationId { get; private set; }
        public bool BlockExport { get; init; }
        public TaskCompletionSource ExportStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<JobResultsSummary> LoadSummaryAsync(Guid batchJobId, CancellationToken cancellationToken = default) => Task.FromResult(Summary);
        public Task<JobResultsPage> LoadPageAsync(Guid batchJobId, long? afterCursor, int pageSize, CancellationToken cancellationToken = default)
        {
            RequestedPageSizes.Add(pageSize);
            return Task.FromResult(Pages[Math.Min(pageIndex++, Pages.Count - 1)]);
        }
        public Task<JobResultActionOutcome> RetryShareOnlyAsync(JobResultRow row, CancellationToken cancellationToken = default)
        {
            RetriedShareOperationId = row.ShareOperationId;
            return Task.FromResult(new JobResultActionOutcome(true, false, "分享已重新排队。"));
        }
        public Task<JobResultActionOutcome> ReconcileTransferAsync(Guid transferOperationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new JobResultActionOutcome(true, false, "转存结果已确认。"));
        public Task<JobResultActionOutcome> ReconcileShareAsync(Guid shareOperationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new JobResultActionOutcome(true, false, "分享结果已确认。"));
        public Task<JobResultActionOutcome> ResumeTransferAsync(JobResultRow row, CancellationToken cancellationToken = default) =>
            Task.FromResult(new JobResultActionOutcome(true, false, "转存任务已恢复。"));
        public Task<JobResultActionOutcome> ResumeShareAsync(JobResultRow row, CancellationToken cancellationToken = default) =>
            Task.FromResult(new JobResultActionOutcome(true, false, "分享任务已恢复。"));
        public async Task<long> ExportAsync(Guid batchJobId, string destinationDirectory, IProgress<long> progress, CancellationToken cancellationToken = default)
        {
            ExportStarted.TrySetResult();
            if (BlockExport) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            progress.Report(1);
            return 1;
        }
    }

    private sealed class ThrowingLoadWorkflow : IJobResultsWorkflow
    {
        public Task<JobResultsSummary> LoadSummaryAsync(Guid batchJobId, CancellationToken cancellationToken = default) =>
            Task.FromException<JobResultsSummary>(new InvalidOperationException("private database detail"));
        public Task<JobResultsPage> LoadPageAsync(Guid batchJobId, long? afterCursor, int pageSize, CancellationToken cancellationToken = default) =>
            Task.FromResult(new JobResultsPage([], 0, null));
        public Task<JobResultActionOutcome> RetryShareOnlyAsync(JobResultRow row, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ReconcileTransferAsync(Guid transferOperationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ReconcileShareAsync(Guid shareOperationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ResumeTransferAsync(JobResultRow row, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ResumeShareAsync(JobResultRow row, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> ExportAsync(Guid batchJobId, string destinationDirectory, IProgress<long> progress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class DelayedWorkflow(Guid firstBatch, Guid secondBatch) : IJobResultsWorkflow
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseFirst() => release.TrySetResult();
        public Task<JobResultsSummary> LoadSummaryAsync(Guid batchJobId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new JobResultsSummary(1, 1, 0, 0));
        public async Task<JobResultsPage> LoadPageAsync(Guid batchJobId, long? afterCursor, int pageSize, CancellationToken cancellationToken = default)
        {
            if (batchJobId == firstBatch)
            {
                FirstStarted.TrySetResult();
                await release.Task;
                return new([Row(1)], 1, null);
            }
            Assert.Equal(secondBatch, batchJobId);
            return new([Row(2)], 1, null);
        }
        public Task<JobResultActionOutcome> RetryShareOnlyAsync(JobResultRow row, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ReconcileTransferAsync(Guid transferOperationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ReconcileShareAsync(Guid shareOperationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ResumeTransferAsync(JobResultRow row, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ResumeShareAsync(JobResultRow row, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> ExportAsync(Guid batchJobId, string destinationDirectory, IProgress<long> progress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
