using BaiduBatchTransfer.App.Features.Shares;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

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
        Assert.Equal("暂时无法开始分享，请稍后重试。", vm.StatusMessage);
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

        public Exception? CreateError { get; init; }
        public Action? AfterCreate { get; init; }
        public Func<Guid, CancellationToken, Task>? BeforeGetOperations { get; set; }
        public TaskCompletionSource DelayedLoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(StandaloneShareBatchRecord Batch, IReadOnlyList<StandaloneShareOperationRecord> Operations)> Created { get; } = [];
        public List<(Guid BatchId, long Revision)> PauseCalls { get; } = [];
        public List<(Guid BatchId, long Revision)> ResumeCalls { get; } = [];
        public List<(Guid OperationId, int Generation, long Revision)> RetryCalls { get; } = [];

        public Task CreateBatchAsync(
            StandaloneShareBatchRecord batch,
            IReadOnlyList<StandaloneShareOperationRecord> operationRecords,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CreateError is not null) throw CreateError;
            Created.Add((batch, operationRecords.ToArray()));
            batches.Add(batch);
            operations.Add(batch.Id, operationRecords.ToList());
            AfterCreate?.Invoke();
            return Task.CompletedTask;
        }

        public Task<Page<StandaloneShareBatchRecord>> GetBatchesAsync(
            PageQuery query,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            query.Validate();
            var ordered = batches.OrderByDescending(item => item.CreatedAt).ToArray();
            return Task.FromResult(new Page<StandaloneShareBatchRecord>(
                ordered.Skip(query.Offset).Take(query.PageSize).ToArray(),
                ordered.Length,
                query.PageNumber,
                query.PageSize));
        }

        public async Task<IReadOnlyList<StandaloneShareOperationRecord>> GetOperationsAsync(
            Guid batchId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BeforeGetOperations is not null)
            {
                DelayedLoadStarted.TrySetResult();
                await BeforeGetOperations(batchId, cancellationToken);
            }
            return operations.TryGetValue(batchId, out var found)
                ? found.OrderBy(item => item.SequenceNumber).ToArray()
                : [];
        }

        public Task<StandaloneShareMutationResult<StandaloneShareBatchRecord>> TryPauseBatchAsync(
            Guid batchId,
            long expectedRevision,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            PauseCalls.Add((batchId, expectedRevision));
            return Task.FromResult(ChangeBatch(
                batchId, expectedRevision, StandaloneShareBatchStatus.Paused,
                [StandaloneShareBatchStatus.Queued, StandaloneShareBatchStatus.Active], updatedAt));
        }

        public Task<StandaloneShareMutationResult<StandaloneShareBatchRecord>> TryResumeBatchAsync(
            Guid batchId,
            long expectedRevision,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            ResumeCalls.Add((batchId, expectedRevision));
            return Task.FromResult(ChangeBatch(
                batchId, expectedRevision, StandaloneShareBatchStatus.Queued,
                [StandaloneShareBatchStatus.Paused], updatedAt));
        }

        public Task<StandaloneShareMutationResult<StandaloneShareOperationRecord>> TryRetryFailedOperationAsync(
            Guid operationId,
            int expectedGeneration,
            long expectedRevision,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            RetryCalls.Add((operationId, expectedGeneration, expectedRevision));
            var pair = operations.SelectMany(pair => pair.Value.Select((item, index) => (pair.Key, item, index)))
                .FirstOrDefault(value => value.item.Id == operationId);
            if (pair.item is null)
                return Task.FromResult(new StandaloneShareMutationResult<StandaloneShareOperationRecord>(
                    StandaloneShareMutationDisposition.NotFound, null));
            if (pair.item.Generation != expectedGeneration || pair.item.Revision != expectedRevision)
                return Task.FromResult(new StandaloneShareMutationResult<StandaloneShareOperationRecord>(
                    StandaloneShareMutationDisposition.Conflict, pair.item));
            if (!pair.item.CanSafelyRetry)
                return Task.FromResult(new StandaloneShareMutationResult<StandaloneShareOperationRecord>(
                    StandaloneShareMutationDisposition.Unsafe, pair.item));
            var updated = RebuildOperation(
                pair.item,
                StandaloneShareOperationStatus.Queued,
                StandaloneShareSubmissionState.NotStarted,
                pair.item.Generation + 1,
                pair.item.Revision + 1);
            operations[pair.Key][pair.index] = updated;
            return Task.FromResult(new StandaloneShareMutationResult<StandaloneShareOperationRecord>(
                StandaloneShareMutationDisposition.Applied, updated));
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
