using BaiduBatchTransfer.App.Features.Shares;
using BaiduBatchTransfer.App.Features.Common;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using SuperPan.Licensing.Client;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ShareWorkspaceViewModelTests
{
    private static readonly ShareAccountOption AccountA = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"), "账号甲");
    private static readonly ShareAccountOption AccountB = new(
        Guid.Parse("22222222-2222-2222-2222-222222222222"), "账号乙");

    [Fact]
    public async Task LicenseFailureStopsShareStartBeforeDraftOrDurableCreation()
    {
        var store = new FakeStore();
        var gate = new RecordingLicenseGate(false);
        var vm = Fixture(store: store, activeAccount: AccountA, licenseGate: gate);
        vm.Add(Selection(AccountA, "a-1"));
        vm.ResultDirectory = @"C:\结果";

        await vm.StartAsync();

        Assert.Equal([LicenseOperationKind.Share], gate.Operations);
        Assert.Empty(store.Created);
        Assert.False(vm.HasPendingBatchConfirmation);
        Assert.Equal("软件尚未激活，联网功能暂不可用。", vm.StatusMessage);
    }

    [Fact]
    public async Task LicenseFailureStopsShareResumeBeforeDurableMutation()
    {
        var store = new FakeStore();
        store.SeedBatch(StandaloneShareBatchStatus.Paused);
        var gate = new RecordingLicenseGate(false);
        var vm = Fixture(store: store, licenseGate: gate);
        await vm.InitializeAsync();

        await vm.ResumeAsync();

        Assert.Equal([LicenseOperationKind.Share], gate.Operations);
        Assert.Empty(store.ResumeCalls);
    }

    [Fact]
    public async Task LicenseFailureStopsShareRetryBeforeDurableMutation()
    {
        var store = new FakeStore();
        var batch = store.SeedBatch(StandaloneShareBatchStatus.CompletedWithFailures);
        var failed = store.ReplaceOnlyOperation(batch.Id, operation => RebuildOperation(
            operation,
            StandaloneShareOperationStatus.Failed,
            StandaloneShareSubmissionState.DefinitelyNotAccepted));
        var gate = new RecordingLicenseGate(false);
        var vm = Fixture(store: store, licenseGate: gate);
        await vm.InitializeAsync();
        vm.SelectedOperation = failed;

        await vm.RetryFailedAsync();

        Assert.Equal([LicenseOperationKind.Share], gate.Operations);
        Assert.Empty(store.RetryCalls);
    }

    [Fact]
    public async Task StatusChangesProduceSafeLogsAndConsecutiveDuplicatesCoalesce()
    {
        var vm = Fixture(picker: new FakePicker((_, _) => []), activeAccount: AccountA);
        var initialCount = vm.Logs.Count;

        await vm.PickResourcesAsync();
        await vm.PickResourcesAsync();

        Assert.Equal(initialCount + 1, vm.Logs.Count);
        Assert.Equal("没有添加资源。", vm.Logs[^1].Message);
        Assert.Equal(WorkspaceLogLevel.Information, vm.Logs[^1].Level);
    }

    [Fact]
    public void ClearLogsCommandClearsOnlyRuntimeLogsAndReportsThroughStatus()
    {
        using var vm = Fixture(activeAccount: AccountA);

        Assert.True(vm.CanClearLogs);
        Assert.True(vm.ClearLogsCommand.CanExecute(null));

        vm.ClearLogsCommand.Execute(null);

        Assert.Empty(vm.Logs);
        Assert.Equal("运行日志已清空。", vm.StatusMessage);
        Assert.Equal("运行日志已清空。", vm.UserFacingStatusMessage);
        Assert.False(vm.CanClearLogs);
        Assert.False(vm.ClearLogsCommand.CanExecute(null));
    }

    [Fact]
    public async Task ClearLogsCommandIsDisabledWhileBusyAndAfterClose()
    {
        var store = new FakeStore
        {
            BeforeGetBatches = cancellationToken => Task.Delay(Timeout.Infinite, cancellationToken),
        };
        using var vm = Fixture(store: store, activeAccount: AccountA);

        var loading = vm.InitializeAsync();
        await store.DelayedHistoryStarted.Task;

        Assert.False(vm.CanClearLogs);
        Assert.False(vm.ClearLogsCommand.CanExecute(null));

        vm.Close();
        await loading;

        Assert.False(vm.CanClearLogs);
        Assert.False(vm.ClearLogsCommand.CanExecute(null));
    }

    [Fact]
    public void RuntimeLogHeaderPlacesAccessibleClearButtonBesideRecentLimit()
    {
        var path = Path.Combine(FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "Features", "Shares", "ShareWorkspaceView.xaml");
        var document = XDocument.Load(path);
        var button = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "Button" &&
            (string?)element.Attribute("Command") == "{Binding ClearLogsCommand}");
        var header = Assert.IsType<XElement>(button.Parent);

        Assert.Equal("清空日志", (string?)button.Attribute("Content"));
        Assert.Equal("清空运行日志", (string?)button.Attribute("AutomationProperties.Name"));
        Assert.Contains(header.Elements(), element => (string?)element.Attribute("Text") == "运行日志");
        Assert.Contains(header.Elements(), element => (string?)element.Attribute("Text") == "最近 100 条");
    }

    [Fact]
    public async Task DisposePreventsLateStatusFromAppendingToLogs()
    {
        var store = new FakeStore
        {
            BeforeGetBatches = cancellationToken => Task.Delay(Timeout.Infinite, cancellationToken),
        };
        var vm = Fixture(store: store, activeAccount: AccountA);
        var loading = vm.InitializeAsync();
        await store.DelayedHistoryStarted.Task;
        vm.Dispose();
        var countAtDispose = vm.Logs.Count;

        await loading;

        Assert.Equal(countAtDispose, vm.Logs.Count);
    }

    [Fact]
    public void Selection_CannotBeMovedToAnotherAccount()
    {
        var vm = Fixture(activeAccount: AccountB);

        var error = Assert.Throws<InvalidOperationException>(() => vm.Add(Selection(AccountA, "a-1")));

        Assert.Equal("所选资源不属于当前账号，请切回对应账号重新选择。", error.Message);
        Assert.Empty(vm.SelectionGroups);
    }

    [Fact]
    public void SelectionCollections_AreReadOnlyThroughThePublicApi()
    {
        var vm = Fixture(activeAccount: AccountA);
        vm.Add(Selection(AccountA, "a-1"));

        var publicGroups = Assert.IsAssignableFrom<IList>(vm.SelectionGroups);
        var publicSelections = Assert.IsAssignableFrom<IList>(vm.SelectionGroups.Single().Selections);

        Assert.True(publicGroups.IsReadOnly);
        Assert.True(publicSelections.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => publicGroups.Add(new ShareSelectionGroup(AccountA.AccountId, AccountA.DisplayName)));
        Assert.Throws<NotSupportedException>(() => publicSelections.Add(Selection(AccountA, "a-2")));
    }

    [Fact]
    public async Task Start_RevalidatesTheWholeSnapshotAndRejectsInjectedCrossAccountData()
    {
        var store = new FakeStore();
        var vm = Fixture(store: store, activeAccount: AccountA);
        var injected = new ShareSelectionGroup(AccountA.AccountId, AccountA.DisplayName);
        InjectSelection(injected, Selection(AccountB, "b-foreign"));
        InjectGroup(vm, injected);
        vm.ResultDirectory = @"C:\结果";

        await vm.StartAsync();

        Assert.Empty(store.Created);
        Assert.Equal("所选资源与账号不一致，请重新选择。", vm.StatusMessage);
    }

    [Fact]
    public void CanStart_RequiresAccountSelectionResourceAndLocalResultDirectory()
    {
        var vm = Fixture(activeAccount: null);
        Assert.False(vm.CanStart);

        vm.ActiveAccount = AccountA;
        Assert.False(vm.CanStart);
        vm.Add(Selection(AccountA, "a-1"));
        Assert.False(vm.CanStart);
        vm.ResultDirectory = @"C:\结果";
        Assert.True(vm.CanStart);

        vm.ActiveAccount = null;
        Assert.False(vm.CanStart);
    }

    [Fact]
    public void ActiveAccount_MustComeFromTheEligibleAccountList()
    {
        var vm = Fixture(activeAccount: null);
        var unknown = new ShareAccountOption(Guid.NewGuid(), "未知账号");

        Assert.Throws<InvalidOperationException>(() => vm.ActiveAccount = unknown);
        Assert.Null(vm.ActiveAccount);
    }

    [Fact]
    public async Task Picker_AlwaysReceivesTheActiveAccountAndGroupsSelectionsByOwner()
    {
        var picker = new FakePicker((accountId, _) => accountId == AccountA.AccountId
            ? [Selection(AccountA, "a-1")]
            : [Selection(AccountB, "b-1"), Selection(AccountB, "b-2")]);
        var vm = Fixture(picker: picker, activeAccount: AccountA);

        await vm.PickResourcesAsync();
        vm.ActiveAccount = AccountB;
        await vm.PickResourcesAsync();

        Assert.Equal([AccountA.AccountId, AccountB.AccountId], picker.AccountIds);
        Assert.Collection(
            vm.SelectionGroups,
            group =>
            {
                Assert.Equal(AccountA.AccountId, group.AccountId);
                Assert.Single(group.Selections);
            },
            group =>
            {
                Assert.Equal(AccountB.AccountId, group.AccountId);
                Assert.Equal(2, group.Selections.Count);
            });
    }

    [Fact]
    public async Task Picker_ReturningAnotherAccountsResource_IsRejectedWithoutPartialAddition()
    {
        var picker = new FakePicker((_, _) =>
            [Selection(AccountA, "a-1"), Selection(AccountB, "b-1")]);
        var vm = Fixture(picker: picker, activeAccount: AccountA);

        await vm.PickResourcesAsync();

        Assert.Empty(vm.SelectionGroups);
        Assert.Equal("选择结果与当前账号不一致，请重新选择。", vm.StatusMessage);
    }

    [Fact]
    public async Task Start_CreatesOneIndependentOperationPerUserSelection()
    {
        var store = new FakeStore();
        var vm = Fixture(store: store, activeAccount: AccountA);
        vm.Add(Selection(AccountA, "a-1"));
        vm.Add(Selection(AccountA, "a-2", RemoteResourceKind.Folder));
        vm.ActiveAccount = AccountB;
        vm.Add(Selection(AccountB, "b-1"));
        vm.ResultDirectory = @"C:\分享结果";

        await vm.StartAsync();

        var created = Assert.Single(store.Created);
        Assert.Equal(@"C:\分享结果", created.Batch.ResultDirectoryReference);
        Assert.Equal(3, created.Operations.Count);
        Assert.Equal([1, 2, 3], created.Operations.Select(item => item.SequenceNumber));
        Assert.All(created.Operations, item => Assert.Single(item.ExactRemoteItems.Items));
        Assert.Equal(
            [AccountA.AccountId, AccountA.AccountId, AccountB.AccountId],
            created.Operations.Select(item => item.AccountId));
        Assert.Empty(vm.SelectionGroups);
        Assert.Single(vm.Batches);
        Assert.Equal(created.Batch.Id, vm.SelectedBatch?.Id);
    }

    [Fact]
    public async Task Start_PersistsFixedExtractionCodeSettingsForEveryIndependentOperation()
    {
        var store = new FakeStore();
        var vm = Fixture(store: store, activeAccount: AccountA);
        vm.Add(Selection(AccountA, "a-1"));
        vm.Add(Selection(AccountA, "a-2"));
        vm.ResultDirectory = @"C:\分享结果";
        vm.UseFixedExtractionCode = true;
        vm.FixedExtractionCode = "A1b2";

        await vm.StartAsync();

        var created = Assert.Single(store.Created);
        Assert.All(created.Operations, operation =>
        {
            Assert.Equal(ShareExtractionCodeBehavior.Fixed, operation.ExtractionCodeBehavior);
            Assert.Equal("A1b2", operation.FixedExtractionCode);
        });
    }

    [Theory]
    [InlineData(ExistingShareDecision.Reuse)]
    [InlineData(ExistingShareDecision.Regenerate)]
    [InlineData(ExistingShareDecision.Cancel)]
    public async Task Start_PersistsSelectedExistingShareDecisionForEveryOperation(
        ExistingShareDecision decision)
    {
        var store = new FakeStore();
        var vm = Fixture(store: store, activeAccount: AccountA);
        vm.Add(Selection(AccountA, "a-1"));
        vm.Add(Selection(AccountA, "a-2"));
        vm.ResultDirectory = @"C:\分享结果";
        vm.SelectedExistingShareDecision = Assert.Single(
            vm.ExistingShareDecisionOptions, option => option.Decision == decision);

        await vm.StartAsync();

        Assert.All(Assert.Single(store.Created).Operations,
            operation => Assert.Equal(decision, operation.ExistingShareDecision));
    }

    [Fact]
    public async Task Initialize_LoadsBoundedHistoryAndCurrentOperations()
    {
        var store = new FakeStore();
        var oldest = store.SeedBatch(StandaloneShareBatchStatus.Completed, createdMinutesAgo: 10);
        var newest = store.SeedBatch(StandaloneShareBatchStatus.Active, createdMinutesAgo: 1);
        store.SeedBatch(StandaloneShareBatchStatus.Queued, createdMinutesAgo: 5);
        var vm = Fixture(store: store, historyPageSize: 2);

        await vm.InitializeAsync();

        Assert.Equal(2, vm.Batches.Count);
        Assert.Equal(newest.Id, vm.Batches[0].Id);
        Assert.DoesNotContain(vm.Batches, item => item.Id == oldest.Id);
        Assert.Equal(newest.Id, vm.SelectedBatch?.Id);
        Assert.All(vm.Operations, item => Assert.Equal(newest.Id, item.BatchId));
    }

    [Fact]
    public async Task HistoryPaginationLoadsDatabasePagesAndDisablesCommandsAtBoundaries()
    {
        var store = new FakeStore();
        for (var minutes = 5; minutes >= 1; minutes--)
            store.SeedBatch(StandaloneShareBatchStatus.Completed, createdMinutesAgo: minutes);
        var vm = Fixture(store: store, historyPageSize: 2);

        await vm.InitializeAsync();
        Assert.Equal(1, vm.HistoryPageNumber);
        Assert.False(vm.CanLoadPreviousHistoryPage);
        Assert.True(vm.CanLoadNextHistoryPage);

        await vm.NextHistoryPageCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.HistoryPageNumber);
        Assert.Equal("第 2 页", vm.HistoryPageText);
        Assert.True(vm.CanLoadPreviousHistoryPage);
        Assert.True(vm.CanLoadNextHistoryPage);
        Assert.Equal(2, vm.Batches.Count);

        await vm.NextHistoryPageCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.HistoryPageNumber);
        Assert.False(vm.CanLoadNextHistoryPage);
        Assert.Single(vm.Batches);

        await vm.PreviousHistoryPageCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.HistoryPageNumber);
        Assert.Equal([1, 2, 3, 2], store.HistoryQueries.Select(query => query.PageNumber));
    }

    [Fact]
    public async Task HistoryPageSurvivesBatchSwitchAndRetryWhileExplicitRefreshReturnsToNewestPage()
    {
        var store = new FakeStore();
        for (var minutes = 5; minutes >= 1; minutes--)
            store.SeedBatch(StandaloneShareBatchStatus.CompletedWithFailures, createdMinutesAgo: minutes);
        var vm = Fixture(store: store, historyPageSize: 2);
        await vm.InitializeAsync();
        await vm.NextHistoryPageAsync();
        Assert.Equal(2, vm.HistoryPageNumber);

        var pageBatch = vm.Batches[1];
        vm.SelectedBatch = pageBatch;
        await vm.LoadSelectedBatchAsync();
        Assert.Equal(2, vm.HistoryPageNumber);
        var safe = store.ReplaceOnlyOperation(pageBatch.Id, operation => RebuildOperation(
            operation,
            StandaloneShareOperationStatus.Failed,
            StandaloneShareSubmissionState.DefinitelyNotAccepted));
        vm.SelectedOperation = safe;

        await vm.RetryFailedAsync();

        Assert.Equal(2, vm.HistoryPageNumber);
        Assert.Equal(pageBatch.Id, vm.SelectedBatch!.Id);
        await vm.InitializeAsync();
        Assert.Equal(1, vm.HistoryPageNumber);
        Assert.Equal([1, 2, 1], store.HistoryQueries.Select(query => query.PageNumber));
    }

    [Fact]
    public async Task CreatingFromAnOlderHistoryPageReloadsTheAuthoritativeNewestDatabasePage()
    {
        var store = new FakeStore();
        for (var minutes = 4; minutes >= 1; minutes--)
            store.SeedBatch(StandaloneShareBatchStatus.Completed, createdMinutesAgo: minutes);
        var vm = Fixture(store: store, activeAccount: AccountA, historyPageSize: 2);
        await vm.InitializeAsync();
        await vm.NextHistoryPageAsync();
        Assert.Equal(2, vm.HistoryPageNumber);
        vm.Add(Selection(AccountA, "new-share"));
        vm.ResultDirectory = @"C:\分享结果";

        await vm.StartAsync();

        var created = Assert.Single(store.Created).Batch;
        Assert.Equal(1, vm.HistoryPageNumber);
        Assert.Equal(created.Id, vm.SelectedBatch!.Id);
        Assert.Equal(created.Id, vm.Batches[0].Id);
        Assert.Equal([1, 2, 1], store.HistoryQueries.Select(query => query.PageNumber));
    }

    [Fact]
    public async Task PauseAndResume_UseStoreRevisionCompareAndSwapResults()
    {
        var store = new FakeStore();
        var batch = store.SeedBatch(StandaloneShareBatchStatus.Active);
        var vm = Fixture(store: store);
        await vm.InitializeAsync();

        await vm.PauseAsync();
        Assert.Equal(StandaloneShareBatchStatus.Paused, vm.SelectedBatch?.Status);
        Assert.Equal((batch.Id, 0L), Assert.Single(store.PauseCalls));

        await vm.ResumeAsync();
        Assert.Equal(StandaloneShareBatchStatus.Queued, vm.SelectedBatch?.Status);
        Assert.Equal((batch.Id, 1L), Assert.Single(store.ResumeCalls));
    }

    [Fact]
    public async Task Retry_IsEnabledOnlyForStoreProjectedSafeFailureAndUsesGenerationFence()
    {
        var store = new FakeStore();
        var batch = store.SeedBatch(StandaloneShareBatchStatus.CompletedWithFailures);
        var safe = store.ReplaceOnlyOperation(batch.Id, operation => RebuildOperation(
            operation,
            StandaloneShareOperationStatus.Failed,
            StandaloneShareSubmissionState.DefinitelyNotAccepted));
        var vm = Fixture(store: store);
        await vm.InitializeAsync();
        vm.SelectedOperation = safe;

        Assert.True(vm.CanRetryFailed);
        await vm.RetryFailedAsync();

        var call = Assert.Single(store.RetryCalls);
        Assert.Equal((safe.Id, safe.Generation, safe.Revision), call);
        Assert.Equal(StandaloneShareOperationStatus.Queued, vm.SelectedOperation?.Status);
        Assert.Equal(safe.Generation + 1, vm.SelectedOperation?.Generation);
        Assert.Equal(StandaloneShareBatchStatus.Queued, vm.SelectedBatch?.Status);
        Assert.True(vm.CanPause);
    }

    [Fact]
    public async Task Retry_DoesNotCallStoreWhenRemoteSubmissionMightHaveBeenAccepted()
    {
        var store = new FakeStore();
        var batch = store.SeedBatch(StandaloneShareBatchStatus.Active);
        var unsafeFailure = store.ReplaceOnlyOperation(batch.Id, operation => RebuildOperation(
            operation,
            StandaloneShareOperationStatus.Failed,
            StandaloneShareSubmissionState.Accepted));
        var vm = Fixture(store: store);
        await vm.InitializeAsync();
        vm.SelectedOperation = unsafeFailure;

        Assert.False(vm.CanRetryFailed);
        await vm.RetryFailedAsync();

        Assert.Empty(store.RetryCalls);
    }

    [Fact]
    public async Task Retry_IsDisabledWhenTheSelectedOperationBelongsToAnotherBatch()
    {
        var store = new FakeStore();
        var older = store.SeedBatch(StandaloneShareBatchStatus.Active, createdMinutesAgo: 10);
        var newest = store.SeedBatch(StandaloneShareBatchStatus.Active, createdMinutesAgo: 1);
        var safe = store.ReplaceOnlyOperation(newest.Id, operation => RebuildOperation(
            operation,
            StandaloneShareOperationStatus.Failed,
            StandaloneShareSubmissionState.DefinitelyNotAccepted));
        var vm = Fixture(store: store);
        await vm.InitializeAsync();
        vm.SelectedOperation = safe;

        vm.SelectedBatch = older;

        Assert.False(vm.CanRetryFailed);
    }

    [Fact]
    public async Task OlderOperationLoad_CannotOverwriteANewerBatchSelection()
    {
        var store = new FakeStore();
        var older = store.SeedBatch(StandaloneShareBatchStatus.Active, createdMinutesAgo: 10);
        var newest = store.SeedBatch(StandaloneShareBatchStatus.Active, createdMinutesAgo: 1);
        var vm = Fixture(store: store);
        await vm.InitializeAsync();
        Assert.All(vm.Operations, operation => Assert.Equal(newest.Id, operation.BatchId));

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeGetOperations = (batchId, _) => batchId == older.Id ? release.Task : Task.CompletedTask;
        vm.SelectedBatch = older;
        var loading = vm.LoadSelectedBatchAsync();
        await store.DelayedLoadStarted.Task;

        vm.SelectedBatch = newest;
        release.SetResult();
        await loading;

        Assert.Equal(newest.Id, vm.SelectedBatch.Id);
        Assert.All(vm.Operations, operation => Assert.Equal(newest.Id, operation.BatchId));
    }

    [Fact]
    public async Task InitializeCompletion_CannotOverwriteASelectionMadeAfterTheRequestStarted()
    {
        var store = new FakeStore();
        var older = store.SeedBatch(StandaloneShareBatchStatus.Active, createdMinutesAgo: 10);
        store.SeedBatch(StandaloneShareBatchStatus.Active, createdMinutesAgo: 1);
        var vm = Fixture(store: store);
        await vm.InitializeAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeGetBatches = _ => release.Task;

        var loading = vm.InitializeAsync();
        await store.DelayedHistoryStarted.Task;
        vm.SelectedBatch = older;
        var statusAfterSelection = vm.StatusMessage;
        release.SetResult();
        await loading;

        Assert.Equal(older.Id, vm.SelectedBatch?.Id);
        Assert.Equal(statusAfterSelection, vm.StatusMessage);
    }

    [Fact]
    public async Task LoadCompletion_IsRejectedAfterSwitchingAwayAndBackToTheSameBatch()
    {
        var store = new FakeStore();
        var older = store.SeedBatch(StandaloneShareBatchStatus.Active, createdMinutesAgo: 10);
        var newest = store.SeedBatch(StandaloneShareBatchStatus.Active, createdMinutesAgo: 1);
        var vm = Fixture(store: store);
        await vm.InitializeAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeGetOperations = (batchId, _) => batchId == older.Id ? release.Task : Task.CompletedTask;

        vm.SelectedBatch = older;
        var loading = vm.LoadSelectedBatchAsync();
        await store.DelayedLoadStarted.Task;
        vm.SelectedBatch = newest;
        vm.SelectedBatch = older;
        var statusAfterSwitch = vm.StatusMessage;
        release.SetResult();
        await loading;

        Assert.Equal(older.Id, vm.SelectedBatch?.Id);
        Assert.All(vm.Operations, operation => Assert.Equal(newest.Id, operation.BatchId));
        Assert.Equal(statusAfterSwitch, vm.StatusMessage);
    }

    [Fact]
    public async Task PauseCompletion_CannotSelectItsOldTargetAfterTheUserSwitchesBatch()
    {
        var store = new FakeStore();
        var older = store.SeedBatch(StandaloneShareBatchStatus.Active, createdMinutesAgo: 10);
        var newest = store.SeedBatch(StandaloneShareBatchStatus.Active, createdMinutesAgo: 1);
        var vm = Fixture(store: store);
        await vm.InitializeAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforePause = (batchId, _) => batchId == older.Id ? release.Task : Task.CompletedTask;

        vm.SelectedBatch = older;
        var pausing = vm.PauseAsync();
        await store.DelayedPauseStarted.Task;
        vm.SelectedBatch = newest;
        var statusAfterSwitch = vm.StatusMessage;
        release.SetResult();
        await pausing;

        Assert.Equal(newest.Id, vm.SelectedBatch?.Id);
        Assert.Equal(statusAfterSwitch, vm.StatusMessage);
    }

    [Fact]
    public async Task ResumeCompletion_CannotSelectItsOldTargetAfterTheUserSwitchesBatch()
    {
        var store = new FakeStore();
        var older = store.SeedBatch(StandaloneShareBatchStatus.Paused, createdMinutesAgo: 10);
        var newest = store.SeedBatch(StandaloneShareBatchStatus.Active, createdMinutesAgo: 1);
        var vm = Fixture(store: store);
        await vm.InitializeAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeResume = (batchId, _) => batchId == older.Id ? release.Task : Task.CompletedTask;

        vm.SelectedBatch = older;
        var resuming = vm.ResumeAsync();
        await store.DelayedResumeStarted.Task;
        vm.SelectedBatch = newest;
        var statusAfterSwitch = vm.StatusMessage;
        release.SetResult();
        await resuming;

        Assert.Equal(newest.Id, vm.SelectedBatch?.Id);
        Assert.Equal(statusAfterSwitch, vm.StatusMessage);
    }

    [Fact]
    public async Task RetryCompletion_CannotRestoreItsOldOperationAfterTheUserSwitchesBatch()
    {
        var store = new FakeStore();
        var older = store.SeedBatch(StandaloneShareBatchStatus.Active, createdMinutesAgo: 10);
        var newest = store.SeedBatch(StandaloneShareBatchStatus.Active, createdMinutesAgo: 1);
        var safe = store.ReplaceOnlyOperation(newest.Id, operation => RebuildOperation(
            operation,
            StandaloneShareOperationStatus.Failed,
            StandaloneShareSubmissionState.DefinitelyNotAccepted));
        var vm = Fixture(store: store);
        await vm.InitializeAsync();
        vm.SelectedOperation = safe;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeRetry = (operationId, _) => operationId == safe.Id ? release.Task : Task.CompletedTask;

        var retrying = vm.RetryFailedAsync();
        await store.DelayedRetryStarted.Task;
        vm.SelectedBatch = older;
        var statusAfterSwitch = vm.StatusMessage;
        release.SetResult();
        await retrying;

        Assert.Equal(older.Id, vm.SelectedBatch?.Id);
        Assert.Null(vm.SelectedOperation);
        Assert.Equal(statusAfterSwitch, vm.StatusMessage);
    }

    [Fact]
    public async Task LoadSelectedBatch_RefreshesItsDurableHeaderBeforeShowingOperations()
    {
        var store = new FakeStore();
        var original = store.SeedBatch(StandaloneShareBatchStatus.Active);
        var vm = Fixture(store: store);
        await vm.InitializeAsync();
        var durable = store.ReplaceBatch(original.Id, StandaloneShareBatchStatus.Paused);

        await vm.LoadSelectedBatchAsync();

        Assert.Equal(durable.Revision, vm.SelectedBatch?.Revision);
        Assert.Equal(StandaloneShareBatchStatus.Paused, vm.SelectedBatch?.Status);
    }

    [Fact]
    public async Task BusyWorkspace_DisablesSelectionHistoryAndOperationPickers()
    {
        var store = new FakeStore();
        store.SeedBatch(StandaloneShareBatchStatus.Active);
        var vm = Fixture(store: store);
        await vm.InitializeAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeGetOperations = (_, _) => release.Task;

        var loading = vm.LoadSelectedBatchAsync();
        await store.DelayedLoadStarted.Task;

        Assert.False(vm.CanEditSelection);
        Assert.False(vm.CanSelectHistory);
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "Features", "Shares", "ShareWorkspaceView.xaml"));
        Assert.True(xaml.Split("IsEnabled=\"{Binding CanSelectHistory}\"").Length - 1 >= 2);

        release.SetResult();
        await loading;
    }

    [Fact]
    public async Task ResultDirectoryCommand_UsesChosenLocalDirectory()
    {
        var vm = Fixture(resultDialog: new FakeResultDialog(@"D:\导出"));

        await vm.ChooseResultDirectoryAsync();

        Assert.Equal(@"D:\导出", vm.ResultDirectory);
    }

    [Fact]
    public async Task CancelledPicker_LeavesWorkspaceUsableAndDoesNotAddResources()
    {
        var picker = new FakePicker((_, token) => throw new OperationCanceledException(token));
        var vm = Fixture(picker: picker, activeAccount: AccountA);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await vm.PickResourcesAsync(cancellation.Token);

        Assert.False(vm.IsBusy);
        Assert.Empty(vm.SelectionGroups);
        Assert.Equal("已取消选择。", vm.StatusMessage);
    }

    [Fact]
    public async Task StoreError_DoesNotLoseSelectionsOrExposeTechnicalDetails()
    {
        var store = new FakeStore { CreateError = new InvalidOperationException("secret C:\\private token") };
        var vm = Fixture(store: store, activeAccount: AccountA);
        vm.Add(Selection(AccountA, "a-1"));
        vm.ResultDirectory = @"C:\结果";

        await vm.StartAsync();

        Assert.False(vm.IsBusy);
        Assert.Single(vm.SelectionGroups.Single().Selections);
        Assert.Equal("创建结果需要确认，请再次点击开始分享。", vm.StatusMessage);
        Assert.True(vm.HasPendingBatchConfirmation);
        Assert.DoesNotContain("secret", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationAfterStoreCommit_DoesNotLeaveSelectionsThatCouldCreateADuplicateBatch()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new FakeStore { AfterCreate = cancellation.Cancel };
        var vm = Fixture(store: store, activeAccount: AccountA);
        vm.Add(Selection(AccountA, "a-1"));
        vm.ResultDirectory = @"C:\结果";

        await vm.StartAsync(cancellation.Token);

        Assert.Single(store.Created);
        Assert.Empty(vm.SelectionGroups);
        Assert.Single(vm.Batches);
        Assert.Equal("分享任务已加入队列。", vm.StatusMessage);
    }

    [Fact]
    public async Task CommitThenThrow_RetryUsesTheSameBatchAndConvergesWithoutADuplicate()
    {
        var store = new FakeStore
        {
            ErrorAfterFirstCommit = new InvalidOperationException("connection lost after commit"),
        };
        var vm = Fixture(store: store, activeAccount: AccountA);
        vm.Add(Selection(AccountA, "a-1"));
        vm.ResultDirectory = @"C:\结果";

        await vm.StartAsync();
        var committedId = Assert.Single(store.Created).Batch.Id;
        await vm.StartAsync();

        Assert.Single(store.Created);
        Assert.Equal(committedId, vm.SelectedBatch?.Id);
        Assert.Empty(vm.SelectionGroups);
        Assert.Equal("分享任务已加入队列。", vm.StatusMessage);
    }

    [Fact]
    public async Task CommitThenCancellation_RetryReconcilesTheStableBatchId()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new FakeStore
        {
            AfterFirstCommit = cancellation.Cancel,
            ErrorAfterFirstCommit = new OperationCanceledException(cancellation.Token),
        };
        var vm = Fixture(store: store, activeAccount: AccountA);
        vm.Add(Selection(AccountA, "a-1"));
        vm.ResultDirectory = @"C:\结果";

        await vm.StartAsync(cancellation.Token);
        var committedId = Assert.Single(store.Created).Batch.Id;
        await vm.StartAsync();

        Assert.Single(store.Created);
        Assert.Equal(committedId, vm.SelectedBatch?.Id);
        Assert.Empty(vm.SelectionGroups);
    }

    [Fact]
    public async Task UnconfirmedCreate_LocksDraftUntilResetProvesNothingWasCommitted()
    {
        var store = new FakeStore { CreateError = new InvalidOperationException("before commit") };
        var vm = Fixture(store: store, activeAccount: AccountA);
        vm.Add(Selection(AccountA, "a-1"));
        vm.ResultDirectory = @"C:\旧结果";
        await vm.StartAsync();

        Assert.True(vm.HasPendingBatchConfirmation);
        Assert.False(vm.CanEditSelection);
        Assert.Throws<InvalidOperationException>(() => vm.Add(Selection(AccountA, "a-2")));
        Assert.Throws<InvalidOperationException>(() => vm.ResultDirectory = @"C:\新结果");

        store.CreateError = null;
        await vm.ResetPendingDraftAsync();

        Assert.False(vm.HasPendingBatchConfirmation);
        Assert.True(vm.CanEditSelection);
        vm.ResultDirectory = @"C:\新结果";
        Assert.Equal(@"C:\新结果", vm.ResultDirectory);
    }

    [Fact]
    public async Task ResetPendingDraft_WhenCommitActuallyHappened_ConvergesInsteadOfDiscardingIt()
    {
        var store = new FakeStore
        {
            ErrorAfterFirstCommit = new InvalidOperationException("after commit"),
        };
        var vm = Fixture(store: store, activeAccount: AccountA);
        vm.Add(Selection(AccountA, "a-1"));
        vm.ResultDirectory = @"C:\结果";
        await vm.StartAsync();
        var committed = Assert.Single(store.Created).Batch;

        await vm.ResetPendingDraftAsync();

        Assert.False(vm.HasPendingBatchConfirmation);
        Assert.Equal(committed.Id, vm.SelectedBatch?.Id);
        Assert.Empty(vm.SelectionGroups);
        Assert.Single(store.Created);
    }

    [Fact]
    public async Task PendingDraft_IsNotSubmittedIfItsAccountBecameUnavailableBeforeRetry()
    {
        var store = new FakeStore { CreateError = new InvalidOperationException("before commit") };
        var vm = Fixture(store: store, activeAccount: AccountA);
        vm.Add(Selection(AccountA, "a-1"));
        vm.ResultDirectory = @"C:\结果";
        await vm.StartAsync();
        store.CreateError = null;
        vm.ReloadAccounts([AccountB]);

        await vm.StartAsync();

        Assert.Empty(store.Created);
        Assert.True(vm.HasPendingBatchConfirmation);
        Assert.Equal("所选账号当前不可用，请先检查账号。", vm.StatusMessage);
    }

    [Fact]
    public void ShellComposition_UsesTheRealShareWorkspaceWithoutAThirdWorkspace()
    {
        var bootstrap = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "AppBootstrap.cs"));

        Assert.Contains("new ShareWorkspaceView(shareViewModel)", bootstrap);
        Assert.DoesNotContain("分享功能将在此工作区提供。", bootstrap);
        Assert.DoesNotContain("ShowResultsCommand", bootstrap);
        Assert.DoesNotContain("ShowImportCommand", bootstrap);
    }

    [Fact]
    public async Task SaveResults_UsesSelectedTerminalBatchAndItsPersistedDestination()
    {
        var store = new FakeStore();
        var exports = new FakeLocalResultExports();
        var vm = Fixture(store: store, exports: exports);
        var batch = store.SeedBatch(StandaloneShareBatchStatus.CompletedWithFailures);
        vm.SelectedBatch = batch;
        await vm.LoadSelectedBatchAsync();

        await vm.SaveResultsAsync();

        Assert.Equal([batch.Id], exports.BatchIds);
        Assert.Equal("分享结果已保存。", vm.StatusMessage);
        Assert.True(vm.CanSaveResults);
    }

    [Fact]
    public async Task SaveResults_IsDisabledUntilSelectedBatchIsTerminal()
    {
        var store = new FakeStore();
        var exports = new FakeLocalResultExports();
        var vm = Fixture(store: store, exports: exports);
        vm.SelectedBatch = store.SeedBatch(StandaloneShareBatchStatus.Active);

        await vm.SaveResultsAsync();

        Assert.False(vm.CanSaveResults);
        Assert.Empty(exports.BatchIds);
    }

    [Fact]
    public void ShareWorkspace_ExposesAccessibleSaveResultsAction()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "Features", "Shares", "ShareWorkspaceView.xaml"));

        Assert.Contains("Content=\"保存结果\"", xaml);
        Assert.Contains("Command=\"{Binding SaveResultsCommand}\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"保存分享结果\"", xaml);
    }

    [Fact]
    public void ShareWorkspace_ShowsProgressInSimpleChineseAndExposesAccessibleNames()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "Features", "Shares", "ShareWorkspaceView.xaml"));

        Assert.Contains("等待分享", xaml);
        Assert.Contains("正在分享", xaml);
        Assert.Contains("等待确认", xaml);
        Assert.Contains("已完成", xaml);
        Assert.Contains("AutomationProperties.Name=\"分享任务历史\"", xaml);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml);
    }

    [Fact]
    public void ShareWorkspace_UsesCompactToolbarDominantTabsAndFixedSafeLog()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "Features", "Shares", "ShareWorkspaceView.xaml"));
        var document = XDocument.Parse(xaml);
        var xNamespace = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var content = document.Root!.Elements().Single(element => element.Name.LocalName == "Grid");
        var names = content.DescendantsAndSelf()
            .Select(element => (string?)element.Attribute(xNamespace + "Name"))
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("ShareToolbar", names);
        Assert.Contains("ShareDataRegion", names);
        Assert.Contains("ShareLogRegion", names);
        Assert.Equal(new string?[] { "Auto", "*", "170" }, content.Elements()
            .Single(element => element.Name.LocalName == "Grid.RowDefinitions")
            .Elements().Select(element => (string?)element.Attribute("Height")).ToArray());
        Assert.DoesNotContain(content.Elements(), element => element.Name.LocalName == "ScrollViewer");
        Assert.DoesNotContain("<Popup", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AdvancedSettingsExpander\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanUseAdvancedSettings}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AdvancedSettingsScrollViewer\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ShareToolbarWrap\"", xaml, StringComparison.Ordinal);
        Assert.Contains("永久有效 · 不限次数", xaml, StringComparison.Ordinal);
        Assert.Contains("不勾选时自动生成随机提取码", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"待分享资源\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"分享结果\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding PendingRows}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CommandParameter=\"{Binding Selection}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding ResultRows}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"完成时间\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"结果保存\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"分享链接\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"提取码\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"新分享链接\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"新提取码\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource DenseDataGridStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("RowHeight=\"34\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ColumnHeaderHeight=\"38\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"暂停\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"继续\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"重试失败项\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"保存结果\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"取消保存\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding CancelSaveResultsCommand}\"", xaml, StringComparison.Ordinal);

        var logRegion = content.Descendants().Single(element =>
            (string?)element.Attribute(xNamespace + "Name") == "ShareLogRegion");
        Assert.Contains(logRegion.Descendants(), element =>
            element.Name.LocalName == "ListBox" &&
            (string?)element.Attribute("ItemsSource") == "{Binding Logs}" &&
            (string?)element.Attribute("VirtualizingPanel.VirtualizationMode") == "Recycling");
        Assert.Contains("x:Name=\"ShareStatusLiveRegion\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding UserFacingStatusMessage}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding StatusMessage}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Visibility=\"Collapsed\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("LastErrorCode", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("StableErrorCode", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void PendingRowsExposeSelectionAccountPolicyAndReadiness()
    {
        var vm = Fixture(activeAccount: AccountA);
        var selection = Selection(AccountA, "pending-1");

        vm.Add(selection);

        var row = Assert.Single(vm.PendingRows);
        Assert.Same(selection, row.Selection);
        Assert.Equal(selection.DisplayName, row.Title);
        Assert.Equal(selection.ParentPath.Value, row.ParentPath);
        Assert.Equal(AccountA.DisplayName, row.AccountDisplayName);
        Assert.Equal(vm.SelectedExistingShareDecision.DisplayName, row.ExistingSharePolicyDisplay);
        Assert.Equal("准备就绪", row.ReadinessText);

        vm.SelectedExistingShareDecision = vm.ExistingShareDecisionOptions.Single(option =>
            option.Decision == ExistingShareDecision.Regenerate);

        Assert.Equal("重新生成分享", Assert.Single(vm.PendingRows).ExistingSharePolicyDisplay);
    }

    [Fact]
    public async Task ResultRowsFollowAllProjectionPagesAndReadTheExactStandaloneExportKey()
    {
        var store = new FakeStore();
        var seeded = store.SeedBatchWithOperations(StandaloneShareBatchStatus.Completed, 2);
        var first = store.ReplaceOperation(
            seeded.Batch.Id, 0,
            operation => RebuildOperation(
                operation,
                StandaloneShareOperationStatus.Succeeded,
                StandaloneShareSubmissionState.Completed));
        var second = store.ReplaceOperation(
            seeded.Batch.Id, 1,
            operation => RebuildOperation(
                operation,
                StandaloneShareOperationStatus.Succeeded,
                StandaloneShareSubmissionState.Completed));
        var projections = new FakeResultProjectionRepository();
        projections.Pages[0] = new StandaloneShareResultProjectionPage(
            [Projection(first, null, null)], first.SequenceNumber);
        projections.Pages[first.SequenceNumber] = new StandaloneShareResultProjectionPage(
            [Projection(second, "https://pan.baidu.com/s/current", "A1b2")], null);
        var exportStore = new FakeLocalResultExportStore
        {
            Result = ExportRecord(seeded.Batch.Id, LocalResultExportStatus.Succeeded),
        };
        var vm = Fixture(store: store, projectionRepository: projections, exportStore: exportStore);
        vm.SelectedBatch = seeded.Batch;

        await vm.LoadSelectedBatchAsync();

        Assert.Equal([0, first.SequenceNumber], projections.AfterSequenceNumbers);
        Assert.All(projections.PageSizes, pageSize => Assert.InRange(pageSize, 1, 1_000));
        Assert.Equal(2, vm.ResultRows.Count);
        Assert.Null(vm.ResultRows[0].NewShareUrl);
        Assert.Null(vm.ResultRows[0].NewExtractionCode);
        Assert.Equal("https://pan.baidu.com/s/current", vm.ResultRows[1].NewShareUrl);
        Assert.Equal("A1b2", vm.ResultRows[1].NewExtractionCode);
        Assert.Equal(second.UpdatedAt, vm.ResultRows[1].UpdatedAt);
        Assert.Equal("已保存", vm.ResultRows[1].ResultSaveStatus);
        Assert.Equal(new LocalResultExportKey(
            LocalResultOwnerKind.StandaloneShareBatch,
            seeded.Batch.Id,
            LocalResultExportKind.StandaloneShareWorkbook,
            1), Assert.Single(exportStore.Keys));
    }

    [Fact]
    public async Task MissingOrUnmatchedProjectionNeverLeaksUrlAndExportReadFailureIsBlank()
    {
        var store = new FakeStore();
        var seeded = store.SeedBatchWithOperations(StandaloneShareBatchStatus.CompletedWithFailures, 1);
        var projections = new FakeResultProjectionRepository();
        var unrelated = StandaloneShareOperationRecord.Create(
            Guid.NewGuid(), seeded.Batch.Id, Selection(AccountA, "unrelated"), 99, DateTimeOffset.UtcNow);
        projections.Pages[0] = new StandaloneShareResultProjectionPage(
            [Projection(unrelated, "https://pan.baidu.com/s/must-not-leak", "zzzz")], null);
        var exportStore = new FakeLocalResultExportStore { Error = new IOException("private path") };
        var vm = Fixture(store: store, projectionRepository: projections, exportStore: exportStore);
        vm.SelectedBatch = seeded.Batch;

        await vm.LoadSelectedBatchAsync();

        var row = Assert.Single(vm.ResultRows);
        Assert.Null(row.NewShareUrl);
        Assert.Null(row.NewExtractionCode);
        Assert.Equal(string.Empty, row.ResultSaveStatus);
        Assert.Same(seeded.Batch, vm.SelectedBatch);
        Assert.Single(vm.Operations);
        Assert.DoesNotContain("must-not-leak", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedProjectionCursorFailsClosedWithoutKeepingPartialLinks()
    {
        var store = new FakeStore();
        var seeded = store.SeedBatchWithOperations(StandaloneShareBatchStatus.Completed, 2);
        var first = store.ReplaceOperation(
            seeded.Batch.Id, 0,
            operation => RebuildOperation(
                operation,
                StandaloneShareOperationStatus.Succeeded,
                StandaloneShareSubmissionState.Completed));
        var second = store.ReplaceOperation(
            seeded.Batch.Id, 1,
            operation => RebuildOperation(
                operation,
                StandaloneShareOperationStatus.Succeeded,
                StandaloneShareSubmissionState.Completed));
        var projections = new FakeResultProjectionRepository();
        projections.Pages[0] = new StandaloneShareResultProjectionPage(
            [Projection(first, "https://pan.baidu.com/s/partial-one", "1111")], first.SequenceNumber);
        projections.Pages[first.SequenceNumber] = new StandaloneShareResultProjectionPage(
            [Projection(second, "https://pan.baidu.com/s/partial-two", "2222")], first.SequenceNumber);
        var vm = Fixture(store: store, projectionRepository: projections);
        vm.SelectedBatch = seeded.Batch;

        await vm.LoadSelectedBatchAsync();

        Assert.Equal([0, first.SequenceNumber], projections.AfterSequenceNumbers);
        Assert.All(vm.ResultRows, row =>
        {
            Assert.Null(row.NewShareUrl);
            Assert.Null(row.NewExtractionCode);
        });
        Assert.Contains(vm.Logs, entry =>
            entry.Level == WorkspaceLogLevel.Warning &&
            entry.Message == "分享结果暂时无法完整显示，请稍后刷新。");
    }

    [Fact]
    public async Task SameOperationWithStaleSequenceNeverExposesProjectedLink()
    {
        var store = new FakeStore();
        var seeded = store.SeedBatchWithOperations(StandaloneShareBatchStatus.Completed, 1);
        var succeeded = store.ReplaceOperation(
            seeded.Batch.Id, 0,
            operation => RebuildOperation(
                operation,
                StandaloneShareOperationStatus.Succeeded,
                StandaloneShareSubmissionState.Completed));
        var projections = new FakeResultProjectionRepository();
        projections.Pages[0] = new StandaloneShareResultProjectionPage(
            [new StandaloneShareResultProjection(
                succeeded.Id,
                succeeded.SequenceNumber + 1,
                AccountA.DisplayName,
                succeeded.DisplayName,
                succeeded.Kind,
                StandaloneShareOperationStatus.Succeeded,
                "https://pan.baidu.com/s/stale-sequence",
                "1234",
                null)],
            null);
        var vm = Fixture(store: store, projectionRepository: projections);
        vm.SelectedBatch = seeded.Batch;

        await vm.LoadSelectedBatchAsync();

        var row = Assert.Single(vm.ResultRows);
        Assert.Null(row.NewShareUrl);
        Assert.Null(row.NewExtractionCode);
    }

    [Theory]
    [InlineData(StandaloneShareOperationStatus.Queued, StandaloneShareOperationStatus.Succeeded)]
    [InlineData(StandaloneShareOperationStatus.Succeeded, StandaloneShareOperationStatus.ResultPending)]
    public async Task NonSuccessfulOperationOrProjectionNeverExposesProjectedLink(
        StandaloneShareOperationStatus operationStatus,
        StandaloneShareOperationStatus projectionStatus)
    {
        var store = new FakeStore();
        var seeded = store.SeedBatchWithOperations(StandaloneShareBatchStatus.CompletedWithFailures, 1);
        var operation = store.ReplaceOperation(
            seeded.Batch.Id, 0,
            current => RebuildOperation(
                current,
                operationStatus,
                operationStatus == StandaloneShareOperationStatus.Succeeded
                    ? StandaloneShareSubmissionState.Completed
                    : StandaloneShareSubmissionState.NotStarted));
        var projections = new FakeResultProjectionRepository();
        projections.Pages[0] = new StandaloneShareResultProjectionPage(
            [new StandaloneShareResultProjection(
                operation.Id,
                operation.SequenceNumber,
                AccountA.DisplayName,
                operation.DisplayName,
                operation.Kind,
                projectionStatus,
                "https://pan.baidu.com/s/status-mismatch",
                "1234",
                null)],
            null);
        var vm = Fixture(store: store, projectionRepository: projections);
        vm.SelectedBatch = seeded.Batch;

        await vm.LoadSelectedBatchAsync();

        var row = Assert.Single(vm.ResultRows);
        Assert.Null(row.NewShareUrl);
        Assert.Null(row.NewExtractionCode);
    }

    [Fact]
    public async Task SaveResultsRefreshesExportStatusWithoutManualBatchReload()
    {
        var store = new FakeStore();
        var seeded = store.SeedBatchWithOperations(StandaloneShareBatchStatus.Completed, 1);
        var exportStore = new FakeLocalResultExportStore
        {
            Result = ExportRecord(seeded.Batch.Id, LocalResultExportStatus.Pending),
        };
        var exports = new FakeLocalResultExports
        {
            AfterExport = _ => exportStore.Result = ExportRecord(
                seeded.Batch.Id, LocalResultExportStatus.Succeeded),
        };
        var vm = Fixture(store: store, exports: exports, exportStore: exportStore);
        vm.SelectedBatch = seeded.Batch;
        await vm.LoadSelectedBatchAsync();
        Assert.Equal("等待保存", Assert.Single(vm.ResultRows).ResultSaveStatus);

        await vm.SaveResultsAsync();

        Assert.Equal("已保存", Assert.Single(vm.ResultRows).ResultSaveStatus);
        Assert.Equal(2, exportStore.Keys.Count);
    }

    [Fact]
    public void ResultDisplayRowToStringNeverContainsUserContentOrCredentials()
    {
        var operation = StandaloneShareOperationRecord.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Selection(AccountA, "private-item"),
            1,
            DateTimeOffset.UtcNow);
        var row = new ShareResultDisplayRow(
            operation,
            "私人标题",
            "私人账号",
            "已完成",
            "https://pan.baidu.com/s/private-link",
            "A1b2",
            operation.UpdatedAt,
            "已保存");

        var text = row.ToString();

        Assert.Contains(operation.Id.ToString("D"), text, StringComparison.Ordinal);
        Assert.DoesNotContain("私人标题", text, StringComparison.Ordinal);
        Assert.DoesNotContain("私人账号", text, StringComparison.Ordinal);
        Assert.DoesNotContain("private-link", text, StringComparison.Ordinal);
        Assert.DoesNotContain("A1b2", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectingAnotherBatchImmediatelyClearsPreviouslyLoadedDetails()
    {
        var store = new FakeStore();
        var oldSeed = store.SeedBatchWithOperations(StandaloneShareBatchStatus.Completed, 1);
        var oldOperation = store.ReplaceOperation(
            oldSeed.Batch.Id, 0,
            operation => RebuildOperation(
                operation,
                StandaloneShareOperationStatus.Succeeded,
                StandaloneShareSubmissionState.Completed));
        var nextBatch = store.SeedBatch(StandaloneShareBatchStatus.Completed, createdMinutesAgo: 2);
        var projections = new FakeResultProjectionRepository();
        projections.Pages[0] = new StandaloneShareResultProjectionPage(
            [Projection(oldOperation, "https://pan.baidu.com/s/old-batch", "old1")], null);
        var vm = Fixture(store: store, projectionRepository: projections, exports: new FakeLocalResultExports());
        vm.SelectedBatch = oldSeed.Batch;
        await vm.LoadSelectedBatchAsync();
        Assert.Equal("https://pan.baidu.com/s/old-batch", Assert.Single(vm.ResultRows).NewShareUrl);

        vm.SelectedBatch = nextBatch;

        Assert.Empty(vm.Operations);
        Assert.Empty(vm.ResultRows);
        Assert.Null(vm.SelectedOperation);
        Assert.False(vm.CanSaveResults);
    }

    [Fact]
    public async Task SwitchingBeforeSaveCannotExportOrRebuildFromPreviousBatchDetails()
    {
        var store = new FakeStore();
        var oldSeed = store.SeedBatchWithOperations(StandaloneShareBatchStatus.Completed, 1);
        var oldOperation = store.ReplaceOperation(
            oldSeed.Batch.Id, 0,
            operation => RebuildOperation(
                operation,
                StandaloneShareOperationStatus.Succeeded,
                StandaloneShareSubmissionState.Completed));
        var nextBatch = store.SeedBatch(StandaloneShareBatchStatus.Completed, createdMinutesAgo: 2);
        var projections = new FakeResultProjectionRepository();
        projections.Pages[0] = new StandaloneShareResultProjectionPage(
            [Projection(oldOperation, "https://pan.baidu.com/s/old-before-save", "old2")], null);
        var exports = new FakeLocalResultExports();
        var vm = Fixture(store: store, projectionRepository: projections, exports: exports);
        vm.SelectedBatch = oldSeed.Batch;
        await vm.LoadSelectedBatchAsync();

        vm.SelectedBatch = nextBatch;
        await vm.SaveResultsAsync();

        Assert.Empty(exports.BatchIds);
        Assert.Empty(vm.Operations);
        Assert.Empty(vm.ResultRows);
    }

    [Fact]
    public async Task SkippedProjectionSequenceFailsClosedForTheWholeBatch()
    {
        var store = new FakeStore();
        var seeded = store.SeedBatchWithOperations(StandaloneShareBatchStatus.Completed, 2);
        var first = store.ReplaceOperation(
            seeded.Batch.Id, 0,
            operation => RebuildOperation(operation, StandaloneShareOperationStatus.Succeeded,
                StandaloneShareSubmissionState.Completed));
        var second = store.ReplaceOperation(
            seeded.Batch.Id, 1,
            operation => RebuildOperation(operation, StandaloneShareOperationStatus.Succeeded,
                StandaloneShareSubmissionState.Completed));
        var projections = new FakeResultProjectionRepository();
        projections.Pages[0] = new StandaloneShareResultProjectionPage(
            [Projection(second, "https://pan.baidu.com/s/skipped", "2222")], null);
        var vm = Fixture(store: store, projectionRepository: projections);
        vm.SelectedBatch = seeded.Batch;

        await vm.LoadSelectedBatchAsync();

        Assert.All(vm.ResultRows, row => Assert.Null(row.NewShareUrl));
        Assert.Contains(vm.Logs, entry => entry.Message == "分享结果暂时无法完整显示，请稍后刷新。");
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task EmptyPageWithIncreasingCursorFailsClosed()
    {
        var store = new FakeStore();
        var seeded = store.SeedBatchWithOperations(StandaloneShareBatchStatus.Completed, 1);
        var projections = new FakeResultProjectionRepository();
        projections.Pages[0] = new StandaloneShareResultProjectionPage([], 1);
        var vm = Fixture(store: store, projectionRepository: projections);
        vm.SelectedBatch = seeded.Batch;

        await vm.LoadSelectedBatchAsync();

        Assert.Null(Assert.Single(vm.ResultRows).NewShareUrl);
        Assert.Equal([0], projections.AfterSequenceNumbers);
        Assert.Contains(vm.Logs, entry => entry.Message == "分享结果暂时无法完整显示，请稍后刷新。");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrUnmatchedProjectionRowFailsClosedForTheWholeBatch(bool unmatched)
    {
        var store = new FakeStore();
        var seeded = store.SeedBatchWithOperations(StandaloneShareBatchStatus.Completed, 2);
        var first = store.ReplaceOperation(
            seeded.Batch.Id, 0,
            operation => RebuildOperation(operation, StandaloneShareOperationStatus.Succeeded,
                StandaloneShareSubmissionState.Completed));
        var second = store.ReplaceOperation(
            seeded.Batch.Id, 1,
            operation => RebuildOperation(operation, StandaloneShareOperationStatus.Succeeded,
                StandaloneShareSubmissionState.Completed));
        var pageItems = new List<StandaloneShareResultProjection>
        {
            Projection(first, "https://pan.baidu.com/s/first", "1111"),
        };
        if (unmatched)
        {
            pageItems.Add(new StandaloneShareResultProjection(
                Guid.NewGuid(), second.SequenceNumber, AccountA.DisplayName, second.DisplayName,
                second.Kind, StandaloneShareOperationStatus.Succeeded,
                "https://pan.baidu.com/s/unmatched", "2222", null));
        }
        var projections = new FakeResultProjectionRepository();
        projections.Pages[0] = new StandaloneShareResultProjectionPage(pageItems, null);
        var vm = Fixture(store: store, projectionRepository: projections);
        vm.SelectedBatch = seeded.Batch;

        await vm.LoadSelectedBatchAsync();

        Assert.All(vm.ResultRows, row => Assert.Null(row.NewShareUrl));
        Assert.Contains(vm.Logs, entry => entry.Message == "分享结果暂时无法完整显示，请稍后刷新。");
    }

    [Fact]
    public async Task MoreThanTenThousandLoadedOperationsNeverRequestsOrExposesProjection()
    {
        var store = new FakeStore();
        var seeded = store.SeedBatchWithOperations(StandaloneShareBatchStatus.Completed, 10_001);
        var projections = new FakeResultProjectionRepository();
        var vm = Fixture(store: store, projectionRepository: projections);
        vm.SelectedBatch = seeded.Batch;

        await vm.LoadSelectedBatchAsync();

        Assert.Empty(projections.AfterSequenceNumbers);
        Assert.Equal(10_001, vm.ResultRows.Count);
        Assert.All(vm.ResultRows, row => Assert.Null(row.NewShareUrl));
        Assert.Contains(vm.Logs, entry => entry.Message == "分享结果暂时无法完整显示，请稍后刷新。");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShareWorkbenchKeepsDataUsableAndLogFixedAtMinimumSizeOnSta(bool expanded)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var xaml = File.ReadAllText(Path.Combine(
                    FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "Features", "Shares", "ShareWorkspaceView.xaml"));
                xaml = xaml.Replace(
                    " x:Class=\"BaiduBatchTransfer.App.Features.Shares.ShareWorkspaceView\"",
                    string.Empty,
                    StringComparison.Ordinal);
                var openingTagEnd = xaml.IndexOf('>');
                const string resources = """
                    <UserControl.Resources>
                        <ResourceDictionary>
                            <ResourceDictionary.MergedDictionaries>
                                <ResourceDictionary Source="/BaiduBatchTransfer.App;component/Styles/SuperPanTheme.xaml" />
                            </ResourceDictionary.MergedDictionaries>
                            <BooleanToVisibilityConverter x:Key="BooleanToVisibilityConverter" />
                        </ResourceDictionary>
                    </UserControl.Resources>
                    """;
                xaml = xaml.Insert(openingTagEnd + 1, resources);
                var view = Assert.IsType<UserControl>(XamlReader.Parse(xaml));
                var advanced = Assert.IsType<Expander>(view.FindName("AdvancedSettingsExpander"));
                advanced.IsExpanded = expanded;
                var available = new Size(716, 500);

                view.Measure(available);
                view.Arrange(new Rect(available));
                view.UpdateLayout();

                var toolbar = Assert.IsType<Border>(view.FindName("ShareToolbar"));
                var wrap = Assert.IsType<WrapPanel>(view.FindName("ShareToolbarWrap"));
                var data = Assert.IsType<Border>(view.FindName("ShareDataRegion"));
                var log = Assert.IsType<Border>(view.FindName("ShareLogRegion"));
                Assert.InRange(view.DesiredSize.Width, 1, available.Width);
                Assert.InRange(toolbar.ActualWidth, 1, available.Width);
                Assert.InRange(wrap.DesiredSize.Width, 1, toolbar.ActualWidth);
                Assert.InRange(log.ActualHeight, 169.5, 170.5);
                Assert.True(data.ActualHeight >= 80, $"Data region height was {data.ActualHeight:N1}px.");
                if (expanded)
                {
                    var scroll = Assert.IsType<ScrollViewer>(view.FindName("AdvancedSettingsScrollViewer"));
                    Assert.InRange(scroll.ActualHeight, 1, 110);
                }
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA share measure test timed out.");
        if (failure is not null) throw new TargetInvocationException(failure);
    }

    private static ShareWorkspaceViewModel Fixture(
        FakeStore? store = null,
        FakePicker? picker = null,
        IResultSaveDialog? resultDialog = null,
        ShareAccountOption? activeAccount = null,
        int historyPageSize = 20,
        ILocalResultExportCoordinator? exports = null,
        IStandaloneShareResultProjectionRepository? projectionRepository = null,
        ILocalResultExportStore? exportStore = null,
        ILicenseAuthorizationGate? licenseGate = null)
    {
        var vm = new ShareWorkspaceViewModel(
            [AccountA, AccountB],
            picker ?? new FakePicker((_, _) => []),
            store ?? new FakeStore(),
            resultDialog ?? new FakeResultDialog(null),
            TimeProvider.System,
            historyPageSize,
            localResultExports: exports,
            resultProjectionRepository: projectionRepository,
            localResultExportStore: exportStore,
            licenseGate: licenseGate);
        vm.ActiveAccount = activeAccount;
        return vm;
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

    private static OwnedRemoteSelection Selection(
        ShareAccountOption account,
        string exactId,
        RemoteResourceKind kind = RemoteResourceKind.File) =>
        new(
            account.AccountId,
            new RemoteItemIdentitySet([new RemoteItemIdentity(exactId, $"资源-{exactId}")]),
            $"资源-{exactId}",
            kind,
            new RemotePath("/资料"));

    private static StandaloneShareOperationRecord RebuildOperation(
        StandaloneShareOperationRecord operation,
        StandaloneShareOperationStatus status,
        StandaloneShareSubmissionState submissionState,
        int? generation = null,
        long? revision = null) =>
        new(
            operation.Id, operation.BatchId, operation.AccountId, operation.ExactRemoteItems,
            operation.DisplayName, operation.Kind, operation.ParentPath, operation.SequenceNumber,
            generation ?? operation.Generation, status, submissionState, revision ?? operation.Revision,
            operation.CreatedAt, DateTimeOffset.UtcNow);

    private static StandaloneShareResultProjection Projection(
        StandaloneShareOperationRecord operation,
        string? url,
        string? code) => new(
            operation.Id,
            operation.SequenceNumber,
            AccountA.DisplayName,
            operation.DisplayName,
            operation.Kind,
            operation.Status,
            url,
            code,
            null);

    private static LocalResultExportRecord ExportRecord(Guid batchId, LocalResultExportStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        return new LocalResultExportRecord(
            Guid.NewGuid(),
            new LocalResultExportKey(
                LocalResultOwnerKind.StandaloneShareBatch,
                batchId,
                LocalResultExportKind.StandaloneShareWorkbook,
                1),
            status,
            "results.xlsx",
            status == LocalResultExportStatus.Succeeded ? "results.xlsx" : null,
            null,
            0,
            null,
            0,
            now,
            now);
    }

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

    private static void InjectGroup(ShareWorkspaceViewModel viewModel, ShareSelectionGroup group)
    {
        var field = typeof(ShareWorkspaceViewModel).GetField(
            "selectionGroups", BindingFlags.Instance | BindingFlags.NonPublic);
        var target = field?.GetValue(viewModel) ?? viewModel.SelectionGroups;
        ((IList)target).Add(group);
    }

    private static void InjectSelection(ShareSelectionGroup group, OwnedRemoteSelection selection)
    {
        var field = typeof(ShareSelectionGroup).GetField(
            "selections", BindingFlags.Instance | BindingFlags.NonPublic);
        var target = field?.GetValue(group) ?? group.Selections;
        ((IList)target).Add(selection);
    }

    private sealed class FakePicker(
        Func<Guid, CancellationToken, IReadOnlyList<OwnedRemoteSelection>> pick) : IRemoteResourcePicker
    {
        public List<Guid> AccountIds { get; } = [];

        public Task<IReadOnlyList<OwnedRemoteSelection>> PickAsync(
            Guid accountId,
            CancellationToken cancellationToken = default)
        {
            AccountIds.Add(accountId);
            return Task.FromResult(pick(accountId, cancellationToken));
        }
    }

    private sealed class FakeResultDialog(string? result) : IResultSaveDialog
    {
        public Task<string?> PickDestinationDirectoryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed class FakeLocalResultExports : ILocalResultExportCoordinator
    {
        public List<Guid> BatchIds { get; } = [];
        public Action<Guid>? AfterExport { get; init; }

        public Task<LocalResultExportOutcome> ExportStandaloneAsync(
            Guid batchId, CancellationToken cancellationToken = default)
        {
            BatchIds.Add(batchId);
            AfterExport?.Invoke(batchId);
            return Task.FromResult(new LocalResultExportOutcome(LocalResultExportDisposition.Exported, "results.xlsx"));
        }

        public Task<LocalResultExportOutcome> ExportTransferAsync(
            Guid batchId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> RecoverAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class FakeResultProjectionRepository : IStandaloneShareResultProjectionRepository
    {
        public Dictionary<int, StandaloneShareResultProjectionPage> Pages { get; } = [];
        public List<int> AfterSequenceNumbers { get; } = [];
        public List<int> PageSizes { get; } = [];

        public Task<StandaloneShareResultProjectionPage> GetPageAsync(
            Guid batchId,
            int afterSequenceNumber,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AfterSequenceNumbers.Add(afterSequenceNumber);
            PageSizes.Add(pageSize);
            return Task.FromResult(Pages.TryGetValue(afterSequenceNumber, out var page)
                ? page
                : new StandaloneShareResultProjectionPage([], null));
        }
    }

    private sealed class FakeLocalResultExportStore : ILocalResultExportStore
    {
        public LocalResultExportRecord? Result { get; set; }
        public Exception? Error { get; init; }
        public List<LocalResultExportKey> Keys { get; } = [];

        public Task<LocalResultExportRecord?> GetAsync(
            LocalResultExportKey key,
            CancellationToken cancellationToken = default)
        {
            Keys.Add(key);
            if (Error is not null) throw Error;
            return Task.FromResult(Result);
        }

        public Task<LocalResultExportRecord> CreateOrGetAsync(
            LocalResultExportRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Page<LocalResultExportRecord>> GetPageAsync(
            PageQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalResultExportLease?> TryAcquireAsync(
            LocalResultExportKey key,
            long expectedRevision,
            Guid leaseOwnerId,
            DateTimeOffset now,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryCompleteAsync(
            Guid exportId,
            int expectedGeneration,
            long expectedLeaseGeneration,
            Guid expectedLeaseOwnerId,
            long expectedRevision,
            DateTimeOffset expectedLeaseExpiresAt,
            string finalFileName,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryFailAsync(
            Guid exportId,
            int expectedGeneration,
            long expectedLeaseGeneration,
            Guid expectedLeaseOwnerId,
            long expectedRevision,
            DateTimeOffset expectedLeaseExpiresAt,
            string sanitizedErrorCode,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> RecoverExpiredAsync(
            DateTimeOffset now,
            int maximumCount,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeStore : IStandaloneShareStore
    {
        private readonly List<StandaloneShareBatchRecord> batches = [];
        private readonly Dictionary<Guid, List<StandaloneShareOperationRecord>> operations = [];

        public Exception? CreateError { get; set; }
        public Action? AfterCreate { get; init; }
        public Action? AfterFirstCommit { get; init; }
        public Exception? ErrorAfterFirstCommit { get; set; }
        public Func<CancellationToken, Task>? BeforeGetBatches { get; set; }
        public Func<Guid, CancellationToken, Task>? BeforeGetOperations { get; set; }
        public Func<Guid, CancellationToken, Task>? BeforePause { get; set; }
        public Func<Guid, CancellationToken, Task>? BeforeResume { get; set; }
        public Func<Guid, CancellationToken, Task>? BeforeRetry { get; set; }
        public TaskCompletionSource DelayedHistoryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DelayedLoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DelayedPauseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DelayedResumeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DelayedRetryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(StandaloneShareBatchRecord Batch, IReadOnlyList<StandaloneShareOperationRecord> Operations)> Created { get; } = [];
        public List<(Guid BatchId, long Revision)> PauseCalls { get; } = [];
        public List<(Guid BatchId, long Revision)> ResumeCalls { get; } = [];
        public List<(Guid OperationId, int Generation, long Revision)> RetryCalls { get; } = [];
        public List<PageQuery> HistoryQueries { get; } = [];

        public Task<StandaloneShareBatchCreationReceipt> CreateOrGetBatchAsync(
            StandaloneSharePendingBatchEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CreateError is not null) throw CreateError;
            var existing = Created.FirstOrDefault(item => item.Batch.Id == envelope.Batch.Id);
            if (existing.Batch is not null)
            {
                var existingEnvelope = StandaloneSharePendingBatchEnvelope.Create(existing.Batch, existing.Operations);
                if (!string.Equals(existingEnvelope.DraftFingerprint, envelope.DraftFingerprint, StringComparison.Ordinal))
                    throw new InvalidOperationException("idempotency conflict");
                return Task.FromResult(new StandaloneShareBatchCreationReceipt(
                    existingEnvelope.DraftFingerprint, existing.Batch, existing.Operations, alreadyExisted: true));
            }
            var batch = envelope.Batch;
            var operationRecords = envelope.Operations;
            Created.Add((batch, operationRecords.ToArray()));
            batches.Add(batch);
            operations.Add(batch.Id, operationRecords.ToList());
            AfterCreate?.Invoke();
            if (Created.Count == 1 && ErrorAfterFirstCommit is { } error)
            {
                ErrorAfterFirstCommit = null;
                AfterFirstCommit?.Invoke();
                throw error;
            }
            return Task.FromResult(new StandaloneShareBatchCreationReceipt(
                envelope.DraftFingerprint, batch, operationRecords, alreadyExisted: false));
        }

        public Task<StandaloneShareBatchCreationReceipt?> GetBatchCreationAsync(
            Guid batchId,
            string draftFingerprint,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = Created.FirstOrDefault(item => item.Batch.Id == batchId);
            if (existing.Batch is null) return Task.FromResult<StandaloneShareBatchCreationReceipt?>(null);
            var envelope = StandaloneSharePendingBatchEnvelope.Create(existing.Batch, existing.Operations);
            return Task.FromResult<StandaloneShareBatchCreationReceipt?>(
                string.Equals(envelope.DraftFingerprint, draftFingerprint, StringComparison.Ordinal)
                    ? new StandaloneShareBatchCreationReceipt(
                        envelope.DraftFingerprint, existing.Batch, existing.Operations, alreadyExisted: true)
                    : null);
        }

        public async Task<Page<StandaloneShareBatchRecord>> GetBatchesAsync(
            PageQuery query,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            query.Validate();
            HistoryQueries.Add(query);
            var ordered = batches.OrderByDescending(item => item.CreatedAt).ToArray();
            if (BeforeGetBatches is not null)
            {
                DelayedHistoryStarted.TrySetResult();
                await BeforeGetBatches(cancellationToken);
            }
            return new Page<StandaloneShareBatchRecord>(
                ordered.Skip(query.Offset).Take(query.PageSize).ToArray(),
                ordered.Length,
                query.PageNumber,
                query.PageSize);
        }

        public async Task<IReadOnlyList<StandaloneShareOperationRecord>> GetOperationsAsync(
            Guid batchId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<StandaloneShareOperationRecord> snapshot =
                operations.TryGetValue(batchId, out var found)
                    ? found.OrderBy(item => item.SequenceNumber).ToArray()
                    : [];
            if (BeforeGetOperations is not null)
            {
                DelayedLoadStarted.TrySetResult();
                await BeforeGetOperations(batchId, cancellationToken);
            }
            return snapshot;
        }

        public async Task<StandaloneShareBatchSnapshot?> GetBatchSnapshotAsync(
            Guid batchId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = batches.FirstOrDefault(item => item.Id == batchId);
            if (batch is null || !operations.TryGetValue(batchId, out var found)) return null;
            var snapshot = new StandaloneShareBatchSnapshot(
                batch, found.OrderBy(item => item.SequenceNumber).ToArray());
            if (BeforeGetOperations is not null)
            {
                DelayedLoadStarted.TrySetResult();
                await BeforeGetOperations(batchId, cancellationToken);
            }
            return snapshot;
        }

        public async Task<StandaloneShareMutationResult<StandaloneShareBatchRecord>> TryPauseBatchAsync(
            Guid batchId,
            long expectedRevision,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            PauseCalls.Add((batchId, expectedRevision));
            if (BeforePause is not null)
            {
                DelayedPauseStarted.TrySetResult();
                await BeforePause(batchId, cancellationToken);
            }
            return ChangeBatch(
                batchId, expectedRevision, StandaloneShareBatchStatus.Paused,
                [StandaloneShareBatchStatus.Queued, StandaloneShareBatchStatus.Active], updatedAt);
        }

        public async Task<StandaloneShareMutationResult<StandaloneShareBatchRecord>> TryResumeBatchAsync(
            Guid batchId,
            long expectedRevision,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            ResumeCalls.Add((batchId, expectedRevision));
            if (BeforeResume is not null)
            {
                DelayedResumeStarted.TrySetResult();
                await BeforeResume(batchId, cancellationToken);
            }
            return ChangeBatch(
                batchId, expectedRevision, StandaloneShareBatchStatus.Queued,
                [StandaloneShareBatchStatus.Paused], updatedAt);
        }

        public async Task<StandaloneShareMutationResult<StandaloneShareOperationRecord>> TryRetryFailedOperationAsync(
            Guid operationId,
            int expectedGeneration,
            long expectedRevision,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            RetryCalls.Add((operationId, expectedGeneration, expectedRevision));
            if (BeforeRetry is not null)
            {
                DelayedRetryStarted.TrySetResult();
                await BeforeRetry(operationId, cancellationToken);
            }
            var pair = operations.SelectMany(pair => pair.Value.Select((item, index) => (pair.Key, item, index)))
                .FirstOrDefault(value => value.item.Id == operationId);
            if (pair.item is null)
                return new StandaloneShareMutationResult<StandaloneShareOperationRecord>(
                    StandaloneShareMutationDisposition.NotFound, null);
            if (pair.item.Generation != expectedGeneration || pair.item.Revision != expectedRevision)
                return new StandaloneShareMutationResult<StandaloneShareOperationRecord>(
                    StandaloneShareMutationDisposition.Conflict, pair.item);
            if (!pair.item.CanSafelyRetry)
                return new StandaloneShareMutationResult<StandaloneShareOperationRecord>(
                    StandaloneShareMutationDisposition.Unsafe, pair.item);
            var updated = RebuildOperation(
                pair.item,
                StandaloneShareOperationStatus.Queued,
                StandaloneShareSubmissionState.NotStarted,
                pair.item.Generation + 1,
                pair.item.Revision + 1);
            operations[pair.Key][pair.index] = updated;
            var batchIndex = batches.FindIndex(item => item.Id == pair.Key);
            if (batchIndex >= 0 && batches[batchIndex].Status == StandaloneShareBatchStatus.CompletedWithFailures)
            {
                var batch = batches[batchIndex];
                batches[batchIndex] = new StandaloneShareBatchRecord(
                    batch.Id, batch.ResultDirectoryReference, StandaloneShareBatchStatus.Queued,
                    batch.OperationCount, batch.Revision + 1, batch.CreatedAt, updatedAt);
            }
            return new StandaloneShareMutationResult<StandaloneShareOperationRecord>(
                StandaloneShareMutationDisposition.Applied, updated);
        }

        public StandaloneShareBatchRecord SeedBatch(
            StandaloneShareBatchStatus status,
            int createdMinutesAgo = 0)
        {
            var createdAt = DateTimeOffset.UtcNow.AddMinutes(-createdMinutesAgo);
            var batch = new StandaloneShareBatchRecord(
                Guid.NewGuid(), @"C:\结果", status, 1, 0, createdAt, createdAt);
            batches.Add(batch);
            operations.Add(batch.Id,
            [
                StandaloneShareOperationRecord.Create(
                    Guid.NewGuid(), batch.Id, Selection(AccountA, $"item-{batch.Id:N}"), 1, createdAt),
            ]);
            return batch;
        }

        public (StandaloneShareBatchRecord Batch, IReadOnlyList<StandaloneShareOperationRecord> Operations)
            SeedBatchWithOperations(StandaloneShareBatchStatus status, int operationCount)
        {
            var createdAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            var batch = new StandaloneShareBatchRecord(
                Guid.NewGuid(), @"C:\结果", status, operationCount, 0, createdAt, createdAt);
            var seededOperations = Enumerable.Range(1, operationCount)
                .Select(sequence => StandaloneShareOperationRecord.Create(
                    Guid.NewGuid(), batch.Id, Selection(AccountA, $"item-{sequence}"), sequence,
                    createdAt.AddSeconds(sequence)))
                .ToArray();
            batches.Add(batch);
            operations.Add(batch.Id, seededOperations.ToList());
            return (batch, seededOperations);
        }

        public StandaloneShareOperationRecord ReplaceOnlyOperation(
            Guid batchId,
            Func<StandaloneShareOperationRecord, StandaloneShareOperationRecord> replace)
        {
            var updated = replace(Assert.Single(operations[batchId]));
            operations[batchId][0] = updated;
            return updated;
        }

        public StandaloneShareOperationRecord ReplaceOperation(
            Guid batchId,
            int index,
            Func<StandaloneShareOperationRecord, StandaloneShareOperationRecord> replace)
        {
            var updated = replace(operations[batchId][index]);
            operations[batchId][index] = updated;
            return updated;
        }

        public StandaloneShareBatchRecord ReplaceBatch(
            Guid batchId,
            StandaloneShareBatchStatus status)
        {
            var index = batches.FindIndex(item => item.Id == batchId);
            var current = batches[index];
            var updated = new StandaloneShareBatchRecord(
                current.Id, current.ResultDirectoryReference, status, current.OperationCount,
                current.Revision + 1, current.CreatedAt, DateTimeOffset.UtcNow);
            batches[index] = updated;
            return updated;
        }

        private StandaloneShareMutationResult<StandaloneShareBatchRecord> ChangeBatch(
            Guid batchId,
            long expectedRevision,
            StandaloneShareBatchStatus next,
            IReadOnlyList<StandaloneShareBatchStatus> allowed,
            DateTimeOffset updatedAt)
        {
            var index = batches.FindIndex(item => item.Id == batchId);
            if (index < 0) return new(StandaloneShareMutationDisposition.NotFound, null);
            var current = batches[index];
            if (current.Revision != expectedRevision)
                return new(StandaloneShareMutationDisposition.Conflict, current);
            if (!allowed.Contains(current.Status))
                return new(StandaloneShareMutationDisposition.InvalidState, current);
            var updated = new StandaloneShareBatchRecord(
                current.Id, current.ResultDirectoryReference, next, current.OperationCount,
                current.Revision + 1, current.CreatedAt, updatedAt);
            batches[index] = updated;
            return new(StandaloneShareMutationDisposition.Applied, updated);
        }
    }
}
