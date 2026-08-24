using BaiduBatchTransfer.App.Features.Shares;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using System.Collections;
using System.Reflection;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ShareWorkspaceViewModelTests
{
    private static readonly ShareAccountOption AccountA = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"), "账号甲");
    private static readonly ShareAccountOption AccountB = new(
        Guid.Parse("22222222-2222-2222-2222-222222222222"), "账号乙");

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
        var batch = store.SeedBatch(StandaloneShareBatchStatus.Active);
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

    private static ShareWorkspaceViewModel Fixture(
        FakeStore? store = null,
        FakePicker? picker = null,
        IResultSaveDialog? resultDialog = null,
        ShareAccountOption? activeAccount = null,
        int historyPageSize = 20)
    {
        var vm = new ShareWorkspaceViewModel(
            [AccountA, AccountB],
            picker ?? new FakePicker((_, _) => []),
            store ?? new FakeStore(),
            resultDialog ?? new FakeResultDialog(null),
            TimeProvider.System,
            historyPageSize);
        vm.ActiveAccount = activeAccount;
        return vm;
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

        public StandaloneShareOperationRecord ReplaceOnlyOperation(
            Guid batchId,
            Func<StandaloneShareOperationRecord, StandaloneShareOperationRecord> replace)
        {
            var updated = replace(Assert.Single(operations[batchId]));
            operations[batchId][0] = updated;
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
