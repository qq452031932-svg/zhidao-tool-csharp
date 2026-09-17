using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Services;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using SuperPan.Licensing.Client;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class JobResultsViewModelTests
{
    [Fact]
    public async Task ExternalCleanupBusyDisablesEveryResultActionAndResetDoesNotReleaseLock()
    {
        var row = Row(1) with { CanResumeTransfer = true, CanResumeShare = true, CanReconcileTransfer = true, CanReconcileShare = true };
        var workflow = new FakeWorkflow { Pages = [new([row], 2, 1)] };
        var vm = Create(workflow, existingShareInteraction: new FakeExistingShareInteraction(Guid.NewGuid()));
        await vm.InitializeAsync(Guid.NewGuid());
        Assert.True(vm.CanLoadNextPage);
        Assert.True(vm.CanRetryShareOnly);
        vm.IsHistoryCleanupBusy = true;
        Assert.False(vm.CanLoadNextPage);
        Assert.False(vm.CanRetryShareOnly);
        Assert.False(vm.CanResumeTransfer);
        Assert.False(vm.CanResumeShare);
        Assert.False(vm.CanReconcileTransfer);
        Assert.False(vm.CanReconcileShare);
        Assert.False(vm.CanResolveExistingShares);
        Assert.False(vm.CanSaveAs);
        Assert.False(vm.CanRefresh);
        vm.Reset();
        Assert.True(vm.IsHistoryCleanupBusy);
        vm.IsHistoryCleanupBusy = false;
    }
    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancelled")]
    public async Task ResetInvalidatesPendingExistingShareQueryAndPreservesEmptyState(string completion)
    {
        var interaction = new DelayedPendingExistingShareInteraction();
        var workflow = new FakeWorkflow();
        var vm = Create(workflow, existingShareInteraction: interaction);
        await vm.InitializeAsync(Guid.NewGuid());
        using var cancellation = new CancellationTokenSource();
        var restoring = vm.RestorePendingExistingShareDecisionAsync(cancellation.Token);
        await interaction.Started.Task;

        vm.Reset();
        if (completion == "failure") interaction.Release.SetException(new IOException("private failure"));
        else if (completion == "cancelled")
        {
            cancellation.Cancel();
            interaction.Release.SetCanceled(cancellation.Token);
        }
        else interaction.Release.SetResult([Guid.NewGuid()]);

        Assert.Null(await restoring);
        Assert.Equal(Guid.Empty, vm.BatchJobId);
        Assert.Empty(vm.Items);
        Assert.Null(vm.SelectedItem);
        Assert.Equal(0, vm.TotalCount + vm.ResourceCount + vm.OperationCount + vm.FailedCount +
            vm.PausedCount + vm.CompleteSuccessCount + vm.TerminalProblemCount + vm.ExportedRows);
        Assert.Equal("请选择一个任务查看结果。", vm.StatusMessage);
        Assert.Equal(0, interaction.ResolveCalls);
        Assert.Single(workflow.RequestedPageSizes);
    }

    [Fact]
    public async Task ResetDuringPendingBatchInitializationDoesNotContinueRestore()
    {
        var batch = Guid.NewGuid();
        var interaction = new FakeExistingShareInteraction(batch);
        var workflow = new DelayedWorkflow(batch, Guid.NewGuid());
        var vm = Create(workflow, existingShareInteraction: interaction);
        var restoring = vm.RestorePendingExistingShareDecisionAsync();
        await workflow.FirstStarted.Task;
        vm.Reset();
        workflow.ReleaseFirst();

        Assert.Null(await restoring);
        Assert.Equal(Guid.Empty, vm.BatchJobId);
        Assert.Empty(vm.Items);
        Assert.Equal("请选择一个任务查看结果。", vm.StatusMessage);
        Assert.Equal(0, interaction.ResolveCalls);
    }

    private sealed class DelayedPendingExistingShareInteraction : IExistingShareDecisionInteraction
    {
        public TaskCompletionSource Started { get; } = new();
        public TaskCompletionSource<IReadOnlyList<Guid>> Release { get; } = new();
        public int ResolveCalls { get; private set; }
        public Task<IReadOnlyList<Guid>> GetPendingBatchIdsAsync(int maximumCount = 1, CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            return Release.Task;
        }
        public Task<ExistingShareInteractionOutcome> ResolveAsync(Guid batchJobId, CancellationToken cancellationToken = default)
        {
            ResolveCalls++;
            return Task.FromResult(new ExistingShareInteractionOutcome(false, true, false, "已关闭确认。"));
        }
    }
    [Fact]
    public async Task ResetRejectsLateClipboardStatus()
    {
        var clipboard = new DelayedClipboard();
        var row = Row(1) with { GeneratedShareUrl = "https://pan.baidu.com/s/old", GeneratedExtractionCode = "abcd" };
        var vm = Create(new FakeWorkflow { Pages = [new([row], 1, null)] }, clipboard: clipboard);
        await vm.InitializeAsync(Guid.NewGuid());
        var copying = vm.CopyGeneratedShareCommand.ExecuteAsync(null);
        vm.Reset();
        clipboard.Release.SetResult();
        await copying;
        Assert.Equal("请选择一个任务查看结果。", vm.StatusMessage);
    }

    [Fact]
    public async Task ResetRejectsRowActionWhoseLicenseCheckCompletesLate()
    {
        var gate = new DelayedLicenseGate();
        var workflow = new FakeWorkflow();
        var vm = Create(workflow, licenseGate: gate);
        await vm.InitializeAsync(Guid.NewGuid());
        var pending = vm.RetryShareOnlyCommand.ExecuteAsync(null);
        vm.Reset();
        gate.Release.SetResult(new(LicenseOperationKind.Share, true, false, LicenseCodes.Ok));
        await pending;
        Assert.Null(workflow.RetriedShareOperationId);
        Assert.Equal("请选择一个任务查看结果。", vm.StatusMessage);
    }

    private sealed class DelayedClipboard : ITextClipboard
    {
        public TaskCompletionSource Release { get; } = new();
        public Task SetTextAsync(string text, CancellationToken cancellationToken = default) => Release.Task;
    }

    private sealed class DelayedLicenseGate : ILicenseAuthorizationGate
    {
        public TaskCompletionSource<LicenseGateResult> Release { get; } = new();
        public Task<LicenseGateResult> AuthorizeAsync(LicenseOperationKind operation, CancellationToken cancellationToken) => Release.Task;
    }
    [Fact]
    public async Task ResetCancelsExportAndRejectsOldCopyParameter()
    {
        var workflow = new FakeWorkflow { BlockExport = true };
        var vm = Create(workflow, saveDialog: new FakeSaveDialog("C:\\results"), clipboard: new FakeClipboard());
        await vm.InitializeAsync(Guid.NewGuid());
        var oldRow = vm.Items[0] with { GeneratedShareUrl = "https://pan.baidu.com/s/old", GeneratedExtractionCode = "abcd" };
        var saving = vm.SaveAsAsync();
        await workflow.ExportStarted.Task;
        vm.Reset();
        await saving;
        Assert.False(vm.IsSaving);
        Assert.False(vm.CancelSaveCommand.CanExecute(null));
        Assert.False(vm.CopyGeneratedShareCommand.CanExecute(oldRow));
        Assert.Equal("请选择一个任务查看结果。", vm.StatusMessage);
    }

    [Fact]
    public async Task ResetDuringSaveDialogNeverStartsExportForDeletedBatch()
    {
        var dialog = new DelayedSaveDialog();
        var workflow = new FakeWorkflow();
        var vm = Create(workflow, saveDialog: dialog);
        await vm.InitializeAsync(Guid.NewGuid());
        var saving = vm.SaveAsAsync();
        vm.Reset();
        dialog.Release.SetResult("C:\\results");
        await saving;
        Assert.False(workflow.ExportStarted.Task.IsCompleted);
        Assert.Equal("请选择一个任务查看结果。", vm.StatusMessage);
    }

    private sealed class DelayedSaveDialog : IResultSaveDialog
    {
        public TaskCompletionSource<string?> Release { get; } = new();
        public Task<string?> PickDestinationDirectoryAsync(CancellationToken cancellationToken = default) => Release.Task;
    }
    [Fact]
    public async Task ResetClearsSnapshotStatisticsAndDisablesOldCommands()
    {
        var vm = Create(new FakeWorkflow { Summary = new(5, 8, 2, 1), Pages = [new([Row(1)], 8, 1)] });
        await vm.InitializeAsync(Guid.NewGuid());
        vm.Reset();
        Assert.Equal(Guid.Empty, vm.BatchJobId);
        Assert.Empty(vm.Items);
        Assert.Null(vm.SelectedItem);
        Assert.Equal(0, vm.TotalCount + vm.ResourceCount + vm.OperationCount + vm.FailedCount +
            vm.PausedCount + vm.CompleteSuccessCount + vm.TerminalProblemCount + vm.ExportedRows);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsSaving);
        Assert.False(vm.LoadNextPageCommand.CanExecute(null));
        Assert.False(vm.SaveAsCommand.CanExecute(null));
        Assert.False(vm.RefreshCommand.CanExecute(null));
        Assert.False(vm.RetryShareOnlyCommand.CanExecute(null));
        Assert.False(vm.ResolveExistingSharesCommand.CanExecute(null));
    }

    [Fact]
    public async Task ResetRejectsLatePageFromDeletedBatch()
    {
        var batch = Guid.NewGuid();
        var workflow = new DelayedWorkflow(batch, Guid.NewGuid());
        var vm = Create(workflow);
        var pending = vm.InitializeAsync(batch);
        await workflow.FirstStarted.Task;
        vm.Reset();
        workflow.ReleaseFirst();
        await pending;
        Assert.Empty(vm.Items);
        Assert.Equal(Guid.Empty, vm.BatchJobId);
        Assert.Equal("请选择一个任务查看结果。", vm.StatusMessage);
    }

    [Fact]

    public async Task SelectFailedLoadsAllPagesSelectsOnlyUnsuccessfulStatusesAndClearRemovesSelection()
    {
        var failed = Row(1) with { TransferStatus = "Failed", ShareStatus = "Failed" };
        var paused = Row(2) with { TransferStatus = "Paused", ShareStatus = "Waiting" };
        var pending = Row(3) with { TransferStatus = "ResultPending", ShareStatus = "ResultPending" };
        var waiting = Row(4) with { TransferStatus = "Waiting", ShareStatus = "Waiting" };
        var cancelled = Row(5) with { TransferStatus = "Cancelled", ShareStatus = "Cancelled" };
        var unknown = Row(6) with { TransferStatus = "FutureState", ShareStatus = null };
        var success = Row(7) with { TransferStatus = "Succeeded", ShareStatus = "Succeeded" };
        var workflow = new FakeWorkflow
        {
            Pages = [
                new([failed, success, paused], 7, 1),
                new([pending, waiting, cancelled, unknown], 7, null),
            ],
        };
        var vm = Create(workflow);
        await vm.InitializeAsync(Guid.NewGuid());

        await vm.SelectFailedCommand.ExecuteAsync(null);

        Assert.Equal(6, vm.SelectedFailedCount);
        Assert.DoesNotContain(success.TransferOperationId, vm.SelectedFailedOperationIds);
        Assert.Contains(failed.TransferOperationId, vm.SelectedFailedOperationIds);
        Assert.Contains(unknown.TransferOperationId, vm.SelectedFailedOperationIds);
        Assert.Equal(2, workflow.RequestedPageSizes.Count);

        await vm.ClearSelectionCommand.ExecuteAsync(null);

        Assert.Empty(vm.SelectedFailedOperationIds);
        Assert.Equal(0, vm.SelectedFailedCount);
    }

    [Fact]
    public async Task FailedSelectionSurvivesRefreshAndDoesNotStartAnyWorkflowAction()
    {
        var failed = Row(1) with { TransferStatus = "Failed", ShareStatus = "Failed" };
        var workflow = new FakeWorkflow { Pages = [new([failed], 1, null)] };
        var vm = Create(workflow);
        await vm.InitializeAsync(Guid.NewGuid());
        await vm.SelectFailedCommand.ExecuteAsync(null);

        await vm.RefreshAsync();

        Assert.Contains(failed.TransferOperationId, vm.SelectedFailedOperationIds);
        Assert.Equal(0, workflow.ResumedTransferCalls);
        Assert.Null(workflow.RetriedShareOperationId);
    }
    [Fact]
    public async Task LicenseFailureStopsManualShareRetryBeforeWorkflowMutation()
    {
        var workflow = new FakeWorkflow { Pages = [new([Row(1)], 1, null)] };
        var gate = new RecordingLicenseGate(false);
        var vm = Create(workflow, licenseGate: gate);
        await vm.InitializeAsync(Guid.NewGuid());
        vm.SelectedItem = Assert.Single(vm.Items);

        await vm.RetryShareOnlyCommand.ExecuteAsync(null);

        Assert.Equal([LicenseOperationKind.Share], gate.Operations);
        Assert.Null(workflow.RetriedShareOperationId);
        Assert.Equal("软件尚未激活，联网功能暂不可用。", vm.StatusMessage);
    }

    [Fact]
    public async Task LicenseFailureStopsManualTransferResumeBeforeWorkflowMutation()
    {
        var paused = Row(1) with { CanResumeTransfer = true };
        var workflow = new FakeWorkflow { Pages = [new([paused], 1, null)] };
        var gate = new RecordingLicenseGate(false);
        var vm = Create(workflow, licenseGate: gate);
        await vm.InitializeAsync(Guid.NewGuid());
        vm.SelectedItem = Assert.Single(vm.Items);

        await vm.ResumeTransferCommand.ExecuteAsync(null);

        Assert.Equal([LicenseOperationKind.Transfer], gate.Operations);
        Assert.Equal(0, workflow.ResumedTransferCalls);
    }

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
    public void ResultRowToStringRedactsShareUrlsAndExtractionCode()
    {
        var row = Row(1) with
        {
            OriginalShareUrl = "https://pan.baidu.com/s/original-secret",
            GeneratedShareUrl = "https://pan.baidu.com/s/generated-secret",
            GeneratedExtractionCode = "code-secret",
        };

        var text = row.ToString();

        Assert.DoesNotContain("original-secret", text);
        Assert.DoesNotContain("generated-secret", text);
        Assert.DoesNotContain("code-secret", text);
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
    public async Task LifecycleRefreshPublishesOnlyFirstAndChangedProgressSnapshots()
    {
        var workflow = new ScriptedProgressWorkflow(
            new(25, 25, 0, 0, 0, 0),
            new(25, 25, 0, 0, 0, 0),
            new(25, 25, 0, 0, 0, 0),
            new(25, 25, 2, 0, 6, 2),
            new(25, 25, 2, 0, 6, 2));
        var vm = Create(workflow);
        await vm.InitializeAsync(Guid.NewGuid());
        var statuses = new List<string>();
        var busyStates = new List<bool>();
        vm.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(vm.StatusMessage)) statuses.Add(vm.StatusMessage);
            if (eventArgs.PropertyName == nameof(vm.IsBusy)) busyStates.Add(vm.IsBusy);
        };

        Assert.True(await vm.RefreshForLifecycleAsync());
        Assert.True(await vm.RefreshForLifecycleAsync());
        await vm.RefreshAsync();
        await vm.RefreshAsync();

        Assert.Equal([
            "进度 0/25：成功 0，失败 0，剩余 25。",
            "进度 8/25：成功 6，失败 2，剩余 17。",
        ], statuses);
        Assert.Contains(true, busyStates);
        Assert.Contains(false, busyStates);
    }

    [Fact]
    public async Task UnchangedDirectRefreshPreservesArbitraryNonLifecycleStatus()
    {
        var row = Row(1) with
        {
            GeneratedShareUrl = "https://pan.baidu.com/s/generated",
            GeneratedExtractionCode = "a1b2",
        };
        var snapshot = new JobResultsSummary(1, 1, 0, 0, 1, 0);
        var workflow = new ScriptedProgressWorkflow(snapshot, snapshot, snapshot)
        {
            Page = new([row], 1, null),
        };
        var vm = Create(workflow, clipboard: new FakeClipboard());
        await vm.InitializeAsync(Guid.NewGuid());
        Assert.True(await vm.RefreshForLifecycleAsync());
        await vm.CopyGeneratedShareCommand.ExecuteAsync(Assert.Single(vm.Items));
        Assert.Equal("分享链接和提取码已复制。", vm.StatusMessage);
        var statusChanges = 0;
        vm.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(vm.StatusMessage)) statusChanges++;
        };

        Assert.True(await vm.RefreshForLifecycleAsync());

        Assert.Equal("分享链接和提取码已复制。", vm.StatusMessage);
        Assert.Equal(0, statusChanges);
    }

    [Fact]
    public async Task ConsecutiveRefreshFailuresLogOnceAndRecoveryForcesOneProgressSnapshot()
    {
        var unchanged = new JobResultsSummary(10, 10, 1, 0, 2, 1);
        var workflow = new ScriptedProgressWorkflow(new Func<Task<JobResultsSummary>>[]
        {
            () => Task.FromResult(unchanged),
            () => Task.FromResult(unchanged),
            () => Task.FromException<JobResultsSummary>(new IOException("secret first failure")),
            () => Task.FromException<JobResultsSummary>(new IOException("secret second failure")),
            () => Task.FromResult(unchanged),
            () => Task.FromResult(unchanged),
        });
        var vm = Create(workflow);
        await vm.InitializeAsync(Guid.NewGuid());
        var statuses = new List<string>();
        vm.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(vm.StatusMessage)) statuses.Add(vm.StatusMessage);
        };

        Assert.True(await vm.RefreshForLifecycleAsync());
        Assert.False(await vm.RefreshForLifecycleAsync());
        Assert.False(await vm.RefreshForLifecycleAsync());
        Assert.True(await vm.RefreshForLifecycleAsync());
        Assert.True(await vm.RefreshForLifecycleAsync());

        Assert.Equal([
            "进度 3/10：成功 2，失败 1，剩余 7。",
            "任务结果刷新失败，请稍后重试。",
            "进度 3/10：成功 2，失败 1，剩余 7。",
        ], statuses);
    }

    [Fact]
    public async Task SwitchingBatchesResetsFirstProgressSnapshotRule()
    {
        var snapshot = new JobResultsSummary(4, 4, 0, 0, 0, 0);
        var workflow = new ScriptedProgressWorkflow(snapshot, snapshot, snapshot, snapshot);
        var vm = Create(workflow);
        var progressStatuses = new List<string>();
        vm.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(vm.StatusMessage) && vm.StatusMessage.StartsWith("进度 ", StringComparison.Ordinal))
                progressStatuses.Add(vm.StatusMessage);
        };

        await vm.InitializeAsync(Guid.NewGuid());
        Assert.True(await vm.RefreshForLifecycleAsync());
        await vm.InitializeAsync(Guid.NewGuid());
        Assert.True(await vm.RefreshForLifecycleAsync());

        Assert.Equal([
            "进度 0/4：成功 0，失败 0，剩余 4。",
            "进度 0/4：成功 0，失败 0，剩余 4。",
        ], progressStatuses);
    }

    [Fact]
    public async Task StaleRefreshCannotOverwriteOrPublishAfterBatchSwitch()
    {
        var firstBatch = Guid.NewGuid();
        var secondBatch = Guid.NewGuid();
        var workflow = new DelayedRefreshWorkflow(firstBatch, secondBatch);
        var vm = Create(workflow);
        await vm.InitializeAsync(firstBatch);
        var progressStatuses = new List<string>();
        vm.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(vm.StatusMessage) && vm.StatusMessage.StartsWith("进度 ", StringComparison.Ordinal))
                progressStatuses.Add(vm.StatusMessage);
        };

        var staleRefresh = vm.RefreshForLifecycleAsync();
        await workflow.RefreshStarted.Task;
        await vm.InitializeAsync(secondBatch);
        workflow.ReleaseRefresh();

        Assert.False(await staleRefresh);
        Assert.Equal(secondBatch, vm.BatchJobId);
        Assert.Equal(2, vm.OperationCount);
        Assert.Equal(2, Assert.Single(vm.Items).CandidateSequence);
        Assert.Empty(progressStatuses);
    }

    [Fact]
    public async Task ProgressUsesSummaryCountsClampsRemainingAndContainsNoPrivateRowData()
    {
        var secrets = new[]
        {
            "private-account-id",
            "private-title",
            "https://pan.baidu.com/s/private-url",
            "private-code",
            "/private/path",
        };
        var privateRow = Row(1) with
        {
            AccountDisplayName = secrets[0],
            ResolvedTitle = secrets[1],
            OriginalShareUrl = secrets[2],
            GeneratedExtractionCode = secrets[3],
            TargetPath = secrets[4],
        };
        var workflow = new ScriptedProgressWorkflow(
            new(25, 25, 5, 0, 22, 5),
            new(25, 25, 5, 0, 22, 5))
        {
            Page = new([privateRow], 25, null),
        };
        var vm = Create(workflow);
        await vm.InitializeAsync(Guid.NewGuid());

        Assert.True(await vm.RefreshForLifecycleAsync());

        Assert.Equal("进度 27/25：成功 22，失败 5，剩余 0。", vm.StatusMessage);
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, vm.StatusMessage, StringComparison.Ordinal));
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
    public async Task ExportFailureNeverReportsSuccess()
    {
        var workflow = new FakeWorkflow
        {
            Pages = [new([Row(1)], 1, null)],
            ThrowExport = true,
        };
        var vm = Create(workflow, saveDialog: new FakeSaveDialog("C:\\results"));
        await vm.InitializeAsync(Guid.NewGuid());

        await vm.SaveAsAsync();

        Assert.Equal("结果表格保存失败，请检查目录后重试。", vm.StatusMessage);
        Assert.DoesNotContain("已保存", vm.StatusMessage, StringComparison.Ordinal);
        Assert.False(vm.IsSaving);
    }

    [Fact]
    public async Task CurrentBatchProjectsOriginalAndGeneratedShareValues()
    {
        var expected = Row(1) with
        {
            OriginalShareUrl = "https://pan.baidu.com/s/original",
            TargetPath = "/保存目录",
            GeneratedShareUrl = "https://pan.baidu.com/s/generated",
            GeneratedExtractionCode = "a1b2",
        };
        var vm = Create(new FakeWorkflow { Pages = [new([expected], 1, null)] });

        await vm.InitializeAsync(Guid.NewGuid());

        var actual = Assert.Single(vm.Items);
        Assert.Equal(expected.OriginalShareUrl, actual.OriginalShareUrl);
        Assert.Equal(expected.TargetPath, actual.TargetPath);
        Assert.Equal(expected.GeneratedShareUrl, actual.GeneratedShareUrl);
        Assert.Equal(expected.GeneratedExtractionCode, actual.GeneratedExtractionCode);
    }

    [Fact]
    public async Task InitialLoadSelectsFirstResultForImmediateCopy()
    {
        var row = Row(1) with
        {
            GeneratedShareUrl = "https://pan.baidu.com/s/generated",
            GeneratedExtractionCode = "a1b2",
        };
        var vm = Create(
            new FakeWorkflow { Pages = [new([row], 1, null)] },
            clipboard: new FakeClipboard());

        await vm.InitializeAsync(Guid.NewGuid());

        Assert.Same(Assert.Single(vm.Items), vm.SelectedItem);
        Assert.True(vm.CopyGeneratedShareCommand.CanExecute(null));
    }

    [Fact]
    public async Task SelectedGeneratedShareCanBeCopiedAsLinkAndExtractionCode()
    {
        var clipboard = new FakeClipboard();
        var expected = Row(1) with
        {
            GeneratedShareUrl = "https://pan.baidu.com/s/generated",
            GeneratedExtractionCode = "a1b2",
        };
        var vm = Create(
            new FakeWorkflow { Pages = [new([expected], 1, null)] },
            clipboard: clipboard);
        await vm.InitializeAsync(Guid.NewGuid());

        var selected = Assert.Single(vm.Items);
        Assert.Same(selected, vm.SelectedItem);
        Assert.True(vm.CopyGeneratedShareCommand.CanExecute(null));
        Assert.True(vm.CopyGeneratedShareCommand.CanExecute(selected));
        await vm.CopyGeneratedShareCommand.ExecuteAsync(selected);

        Assert.Equal("分享链接：https://pan.baidu.com/s/generated\r\n提取码：a1b2", clipboard.Text);
        Assert.Equal("分享链接和提取码已复制。", vm.StatusMessage);
    }

    [Fact]
    public async Task ClipboardFailureUsesSafeStatusWithoutEchoingShareData()
    {
        var secretUrl = "https://pan.baidu.com/s/private-secret";
        var expected = Row(1) with
        {
            GeneratedShareUrl = secretUrl,
            GeneratedExtractionCode = "z9x8",
        };
        var vm = Create(
            new FakeWorkflow { Pages = [new([expected], 1, null)] },
            clipboard: new FakeClipboard { Error = new InvalidOperationException("clipboard unavailable") });
        await vm.InitializeAsync(Guid.NewGuid());
        vm.SelectedItem = Assert.Single(vm.Items);

        await vm.CopyGeneratedShareCommand.ExecuteAsync(null);

        Assert.Equal("复制失败，请稍后重试。", vm.StatusMessage);
        Assert.DoesNotContain(secretUrl, vm.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("z9x8", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewUsesVirtualizedResultGridWithoutTechnicalRecoveryControls()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "Features", "Jobs", "JobResultsView.xaml"));

        Assert.Contains("EnableRowVirtualization=\"True\"", xaml);
        Assert.Contains("SelectedItem=\"{Binding SelectedItem, Mode=TwoWay}\"", xaml);
        Assert.Contains("VirtualizationMode=\"Recycling\"", xaml);
        Assert.Contains("Style=\"{StaticResource DenseDataGridStyle}\"", xaml);
        Assert.Contains("RowHeight=\"34\"", xaml);
        Assert.Contains("ColumnHeaderHeight=\"38\"", xaml);
        Assert.Contains("Header=\"原链接\"", xaml);
        Assert.Contains("Header=\"百度保存目录\"", xaml);
        Assert.Contains("Binding=\"{Binding TargetPath}\"", xaml);
        Assert.Contains("Header=\"转存状态\"", xaml);
        Assert.Contains("Header=\"分享状态\"", xaml);
        Assert.Contains("Header=\"新分享链接\"", xaml);
        Assert.Contains("Header=\"新提取码\"", xaml);
        Assert.DoesNotContain("Header=\"转存错误码\"", xaml);
        Assert.DoesNotContain("Header=\"分享错误码\"", xaml);
        Assert.DoesNotContain("Content=\"仅重试分享\"", xaml);
        Assert.DoesNotContain("Content=\"确认转存结果\"", xaml);
        Assert.DoesNotContain("Content=\"确认分享结果\"", xaml);
        Assert.DoesNotContain("Content=\"恢复转存\"", xaml);
        Assert.DoesNotContain("Content=\"恢复分享\"", xaml);
        Assert.DoesNotContain("Content=\"处理已有分享\"", xaml);
        Assert.DoesNotContain("TransferCapabilityMessage", xaml);
        Assert.DoesNotContain("ShareCapabilityMessage", xaml);
        Assert.Contains("Content=\"下一页\"", xaml);
        Assert.Contains("Content=\"刷新结果\"", xaml);
        Assert.Contains("Content=\"另存结果表格\"", xaml);
        Assert.Contains("Content=\"复制链接和提取码\"", xaml);
        Assert.Contains("Command=\"{Binding CopyGeneratedShareCommand}\"", xaml);
        Assert.Contains("x:Name=\"ResultsGrid\"", xaml);
        Assert.Contains("CommandParameter=\"{Binding SelectedItem, ElementName=ResultsGrid}\"", xaml);
        Assert.Contains("<MenuItem Header=\"更多\">", xaml);
        Assert.Contains("Header=\"重试分享\" Command=\"{Binding RetryShareOnlyCommand}\"", xaml);
        Assert.Contains("Header=\"继续转存\" Command=\"{Binding ResumeTransferCommand}\"", xaml);
        Assert.Contains("Header=\"继续分享\" Command=\"{Binding ResumeShareCommand}\"", xaml);
        Assert.Contains("Header=\"检查转存结果\" Command=\"{Binding ReconcileTransferCommand}\"", xaml);
        Assert.Contains("Header=\"检查分享结果\" Command=\"{Binding ReconcileShareCommand}\"", xaml);
        Assert.Contains("Header=\"处理已有分享\" Command=\"{Binding ResolveExistingSharesCommand}\"", xaml);
        Assert.Contains("Header=\"取消另存\" Command=\"{Binding CancelSaveCommand}\"", xaml);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml);
    }

    [Fact]
    public void ResultGridSelectionUpdatesViewModelAndEnablesCopyOnSta()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var row = Row(1) with
                {
                    GeneratedShareUrl = "https://pan.baidu.com/s/generated",
                    GeneratedExtractionCode = "a1b2",
                };
                var vm = Create(
                    new FakeWorkflow { Pages = [new([row], 1, null)] },
                    clipboard: new FakeClipboard());
                vm.InitializeAsync(Guid.NewGuid()).GetAwaiter().GetResult();
                var xaml = File.ReadAllText(Path.Combine(
                    FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "Features", "Jobs", "JobResultsView.xaml"));
                xaml = xaml.Replace(
                    " x:Class=\"BaiduBatchTransfer.App.Features.Jobs.JobResultsView\"",
                    string.Empty,
                    StringComparison.Ordinal);
                var openingTagEnd = xaml.IndexOf('>');
                const string resources = """
                    <UserControl.Resources>
                        <ResourceDictionary>
                            <ResourceDictionary.MergedDictionaries>
                                <ResourceDictionary Source="/知道综合工具;component/Styles/SuperPanTheme.xaml" />
                            </ResourceDictionary.MergedDictionaries>
                        </ResourceDictionary>
                    </UserControl.Resources>
                    """;
                xaml = xaml.Insert(openingTagEnd + 1, resources);
                var view = Assert.IsType<UserControl>(XamlReader.Parse(xaml));
                view.DataContext = vm;
                view.Measure(new Size(900, 500));
                view.Arrange(new Rect(0, 0, 900, 500));
                view.UpdateLayout();
                var grid = FindVisualChild<DataGrid>(view);

                grid.SelectedItem = Assert.Single(vm.Items);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);

                Assert.Same(grid.SelectedItem, vm.SelectedItem);
                Assert.True(vm.CopyGeneratedShareCommand.CanExecute(null));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA result selection test timed out.");
        if (failure is not null) throw new TargetInvocationException(failure);

        static T FindVisualChild<T>(DependencyObject root) where T : DependencyObject
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);
                if (child is T match) return match;
                try { return FindVisualChild<T>(child); }
                catch (InvalidOperationException) { }
            }
            throw new InvalidOperationException($"No {typeof(T).Name} was found.");
        }
    }

    private static JobResultsViewModel Create(
        IJobResultsWorkflow workflow,
        IJobResultCapabilityAvailability? capabilities = null,
        IResultSaveDialog? saveDialog = null,
        IExistingShareDecisionInteraction? existingShareInteraction = null,
        ITextClipboard? clipboard = null,
        ILicenseAuthorizationGate? licenseGate = null) =>
        new(workflow, saveDialog ?? new FakeSaveDialog(null), capabilities ?? new FakeCapabilities { Share = true, Transfer = true },
            existingShareInteraction, clipboard, licenseGate);

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

    private sealed class FakeClipboard : ITextClipboard
    {
        public string? Text { get; private set; }
        public Exception? Error { get; init; }

        public Task SetTextAsync(string text, CancellationToken cancellationToken = default)
        {
            if (Error is not null) throw Error;
            Text = text;
            return Task.CompletedTask;
        }
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

    private sealed class RecordingLicenseGate(bool isAuthorized) : ILicenseAuthorizationGate
    {
        public List<LicenseOperationKind> Operations { get; } = [];
        public Task<LicenseGateResult> AuthorizeAsync(LicenseOperationKind operation, CancellationToken cancellationToken)
        {
            Operations.Add(operation);
            return Task.FromResult(new LicenseGateResult(
                operation, isAuthorized, UsedCachedLease: false,
                isAuthorized ? LicenseCodes.Ok : "activation_required"));
        }
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
        public int ResumedTransferCalls { get; private set; }
        public bool BlockExport { get; init; }
        public bool ThrowExport { get; init; }
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
        public Task<JobResultActionOutcome> ResumeTransferAsync(JobResultRow row, CancellationToken cancellationToken = default)
        {
            ResumedTransferCalls++;
            return Task.FromResult(new JobResultActionOutcome(true, false, "转存任务已恢复。"));
        }
        public Task<JobResultActionOutcome> ResumeShareAsync(JobResultRow row, CancellationToken cancellationToken = default) =>
            Task.FromResult(new JobResultActionOutcome(true, false, "分享任务已恢复。"));
        public async Task<long> ExportAsync(Guid batchJobId, string destinationDirectory, IProgress<long> progress, CancellationToken cancellationToken = default)
        {
            ExportStarted.TrySetResult();
            if (BlockExport) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (ThrowExport) throw new IOException("simulated export failure");
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

    private sealed class ScriptedProgressWorkflow : IJobResultsWorkflow
    {
        private readonly Queue<Func<Task<JobResultsSummary>>> summaries;

        public ScriptedProgressWorkflow(params JobResultsSummary[] summaries)
            : this(summaries.Select(summary => (Func<Task<JobResultsSummary>>)(() => Task.FromResult(summary))).ToArray())
        {
        }

        public ScriptedProgressWorkflow(IEnumerable<Func<Task<JobResultsSummary>>> summaries) =>
            this.summaries = new Queue<Func<Task<JobResultsSummary>>>(summaries);

        public JobResultsPage Page { get; init; } = new([], 25, null);

        public Task<JobResultsSummary> LoadSummaryAsync(Guid batchJobId, CancellationToken cancellationToken = default) =>
            summaries.Dequeue()();
        public Task<JobResultsPage> LoadPageAsync(Guid batchJobId, long? afterCursor, int pageSize, CancellationToken cancellationToken = default) =>
            Task.FromResult(Page);
        public Task<JobResultActionOutcome> RetryShareOnlyAsync(JobResultRow row, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ReconcileTransferAsync(Guid transferOperationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ReconcileShareAsync(Guid shareOperationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ResumeTransferAsync(JobResultRow row, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ResumeShareAsync(JobResultRow row, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> ExportAsync(Guid batchJobId, string destinationDirectory, IProgress<long> progress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class DelayedRefreshWorkflow(Guid firstBatch, Guid secondBatch) : IJobResultsWorkflow
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int firstBatchPageLoads;
        public TaskCompletionSource RefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseRefresh() => release.TrySetResult();
        public Task<JobResultsSummary> LoadSummaryAsync(Guid batchJobId, CancellationToken cancellationToken = default) =>
            Task.FromResult(batchJobId == firstBatch
                ? new JobResultsSummary(99, 99, 10, 0, 80, 10)
                : new JobResultsSummary(2, 2, 0, 0, 0, 0));
        public async Task<JobResultsPage> LoadPageAsync(Guid batchJobId, long? afterCursor, int pageSize, CancellationToken cancellationToken = default)
        {
            if (batchJobId == firstBatch && Interlocked.Increment(ref firstBatchPageLoads) == 2)
            {
                RefreshStarted.TrySetResult();
                await release.Task;
                return new([Row(99)], 99, null);
            }
            Assert.Equal(batchJobId == firstBatch ? firstBatch : secondBatch, batchJobId);
            return new([Row(batchJobId == firstBatch ? 1 : 2)], batchJobId == firstBatch ? 1 : 2, null);
        }
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
