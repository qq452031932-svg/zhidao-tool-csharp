using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Tests.Jobs;

public sealed class CreateStandaloneShareBatchServiceTests
{
    [Fact]
    public async Task EachOwnedSelectionCreatesOneIndependentDurableOperationWithSharedSettings()
    {
        var store = new FakeStore();
        var notifications = new FakeNotifications();
        var service = new CreateStandaloneShareBatchService(store, notifications);
        var account = Guid.NewGuid();
        var selections = Enumerable.Range(1, 3).Select(index => new OwnedRemoteSelection(
            account,
            new RemoteItemIdentitySet([new RemoteItemIdentity($"remote-{index}", $"item-{index}")]),
            $"item-{index}", RemoteResourceKind.File, new RemotePath("/apps"))).ToArray();
        var operationIds = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();

        var envelope = service.BuildDraft(
            Guid.NewGuid(), operationIds, "C:\\results", selections,
            ShareExtractionCodeBehavior.Fixed, "A1b2", DateTimeOffset.UtcNow);
        var receipt = await service.CreateAsync(envelope);

        Assert.Equal(3, receipt.Operations.Length);
        Assert.All(receipt.Operations, operation =>
        {
            Assert.Single(operation.ExactRemoteItems.Items);
            Assert.Equal(ShareExtractionCodeBehavior.Fixed, operation.ExtractionCodeBehavior);
            Assert.Equal("A1b2", operation.FixedExtractionCode);
            Assert.Equal(ExecutionProfileSnapshot.LegacyForAccount(account), operation.ExecutionProfile);
        });
        Assert.Equal(envelope.Batch.Id, Assert.Single(notifications.BatchIds));
    }

    [Fact]
    public void DraftRejectsOperationIdentityCountMismatch()
    {
        var service = new CreateStandaloneShareBatchService(new FakeStore(), new FakeNotifications());
        var selection = new OwnedRemoteSelection(
            Guid.NewGuid(), new RemoteItemIdentitySet([new RemoteItemIdentity("remote", "item")]),
            "item", RemoteResourceKind.File, new RemotePath("/"));

        Assert.Throws<ArgumentException>(() => service.BuildDraft(
            Guid.NewGuid(), [], "C:\\results", [selection],
            ShareExtractionCodeBehavior.Random, null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ExistingResumedOrRetriedBatchCanWakeTheCoordinatorAgain()
    {
        var notifications = new FakeNotifications();
        var service = new CreateStandaloneShareBatchService(new FakeStore(), notifications);
        var batchId = Guid.NewGuid();

        service.NotifyExistingBatch(batchId);

        Assert.Equal(batchId, Assert.Single(notifications.BatchIds));
    }

    [Theory]
    [InlineData(ExistingShareDecision.Reuse)]
    [InlineData(ExistingShareDecision.Regenerate)]
    [InlineData(ExistingShareDecision.Cancel)]
    public void ExistingShareDecisionIsPersistedIntoEveryIndependentOperation(
        ExistingShareDecision decision)
    {
        var service = new CreateStandaloneShareBatchService(new FakeStore(), new FakeNotifications());
        var account = Guid.NewGuid();
        var selections = Enumerable.Range(1, 2).Select(index => new OwnedRemoteSelection(
            account,
            new RemoteItemIdentitySet([new RemoteItemIdentity($"remote-{index}", $"item-{index}")]),
            $"item-{index}", RemoteResourceKind.File, new RemotePath("/apps"))).ToArray();

        var draft = service.BuildDraft(
            Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid()], "C:\\results", selections,
            ShareExtractionCodeBehavior.Random, null, DateTimeOffset.UtcNow, decision);

        Assert.All(draft.Operations, operation => Assert.Equal(decision, operation.ExistingShareDecision));
    }

    private sealed class FakeNotifications : IStandaloneShareBatchNotifications
    {
        public List<Guid> BatchIds { get; } = [];
        public void Notify(Guid batchId) => BatchIds.Add(batchId);
    }

    private sealed class FakeStore : IStandaloneShareStore
    {
        public Task<StandaloneShareBatchCreationReceipt> CreateOrGetBatchAsync(StandaloneSharePendingBatchEnvelope envelope, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StandaloneShareBatchCreationReceipt(
                envelope.DraftFingerprint, envelope.Batch, envelope.Operations, false));
        public Task<StandaloneShareBatchCreationReceipt?> GetBatchCreationAsync(Guid batchId, string draftFingerprint, CancellationToken cancellationToken = default) => Task.FromResult<StandaloneShareBatchCreationReceipt?>(null);
        public Task<Page<StandaloneShareBatchRecord>> GetBatchesAsync(PageQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<StandaloneShareOperationRecord>> GetOperationsAsync(Guid batchId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StandaloneShareBatchSnapshot?> GetBatchSnapshotAsync(Guid batchId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StandaloneShareMutationResult<StandaloneShareBatchRecord>> TryPauseBatchAsync(Guid batchId, long expectedRevision, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StandaloneShareMutationResult<StandaloneShareBatchRecord>> TryResumeBatchAsync(Guid batchId, long expectedRevision, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StandaloneShareMutationResult<StandaloneShareOperationRecord>> TryRetryFailedOperationAsync(Guid operationId, int expectedGeneration, long expectedRevision, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
