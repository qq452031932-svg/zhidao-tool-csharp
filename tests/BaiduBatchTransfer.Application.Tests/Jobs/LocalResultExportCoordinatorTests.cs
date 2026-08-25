using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Application.Tests.Jobs;

public sealed class LocalResultExportCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Export_PersistsTargetBeforeLeaseAndCompletesExactlyOnce()
    {
        var graph = Fixture();

        var first = await graph.Coordinator.ExportStandaloneAsync(graph.BatchId);
        var second = await graph.Coordinator.ExportStandaloneAsync(graph.BatchId);

        Assert.Equal(LocalResultExportDisposition.Exported, first.Disposition);
        Assert.Equal(LocalResultExportDisposition.AlreadySucceeded, second.Disposition);
        Assert.Equal(["choose", "acquire", "inspect", "publish", "complete"], graph.Events);
        Assert.Equal("百度分享结果_20260825_160000.xlsx", graph.Store.Record!.TargetFileName);
        Assert.Equal(LocalResultExportStatus.Succeeded, graph.Store.Record.Status);
        Assert.Equal(graph.Store.Record.TargetFileName, graph.Artifacts.PublishedTarget);
    }

    [Fact]
    public async Task AcceptanceObserverFailure_DoesNotChangeDurableExportOutcome()
    {
        var observer = new ThrowingObserver();
        var graph = Fixture(observer: observer);

        var first = await graph.Coordinator.ExportStandaloneAsync(graph.BatchId);
        var second = await graph.Coordinator.ExportStandaloneAsync(graph.BatchId);

        Assert.Equal(LocalResultExportDisposition.Exported, first.Disposition);
        Assert.Equal(LocalResultExportDisposition.AlreadySucceeded, second.Disposition);
        Assert.Equal(LocalResultExportStatus.Succeeded, graph.Store.Record!.Status);
        Assert.Equal(2, observer.CallCount);
    }

    [Fact]
    public async Task ExportWithoutWorkbookMembershipProofDoesNotNotifyAcceptanceObserver()
    {
        var observer = new CountingObserver();
        var graph = Fixture(observer: observer);
        graph.Owners.ProvesCandidates = false;

        var result = await graph.Coordinator.ExportStandaloneAsync(graph.BatchId);

        Assert.Equal(LocalResultExportDisposition.Exported, result.Disposition);
        Assert.Equal(LocalResultExportStatus.Succeeded, graph.Store.Record!.Status);
        Assert.Equal(0, observer.CallCount);
        Assert.Empty(graph.Artifacts.PublishedEvidence);
    }

    [Fact]
    public async Task RepositoryProofIsCapturedInThePublishedArtifactBeforeAcceptanceNotification()
    {
        var observer = new CountingObserver();
        var graph = Fixture(observer: observer);

        var result = await graph.Coordinator.ExportStandaloneAsync(graph.BatchId);

        Assert.Equal(LocalResultExportDisposition.Exported, result.Disposition);
        Assert.Single(graph.Artifacts.PublishedEvidence);
        Assert.Equal(1, observer.CallCount);
    }

    [Fact]
    public async Task Recovery_ValidPublishedTargetCompletesWithoutRewriting()
    {
        var graph = Fixture(LocalResultArtifactState.Valid);
        await graph.Store.CreateOrGetAsync(LocalResultExportRequest.Create(
            LocalResultOwnerKind.StandaloneShareBatch, graph.BatchId,
            LocalResultExportKind.StandaloneShareWorkbook, 1,
            "already.xlsx", Now));
        graph.Store.MarkRecoveredAfterExpiredLease();

        var result = await graph.Coordinator.ExportStandaloneAsync(graph.BatchId);

        Assert.Equal(LocalResultExportDisposition.Exported, result.Disposition);
        Assert.Null(graph.Artifacts.PublishedTarget);
        Assert.Equal(["acquire", "inspect", "complete"], graph.Events);
        Assert.Equal("already.xlsx", graph.Store.Record!.FinalFileName);
    }

    [Fact]
    public async Task UnexpectedValidFileOnFirstLease_IsNotAcceptedAsOurArtifact()
    {
        var graph = Fixture(LocalResultArtifactState.Valid);

        var result = await graph.Coordinator.ExportStandaloneAsync(graph.BatchId);

        Assert.Equal(LocalResultExportDisposition.Failed, result.Disposition);
        Assert.Equal("UNEXPECTED_EXISTING_RESULT", graph.Store.Record!.SanitizedErrorCode);
        Assert.Null(graph.Store.Record.FinalFileName);
    }

    [Fact]
    public async Task OrdinaryFailedRetry_DoesNotAcceptAValidWorkbookThatAppearedLater()
    {
        var graph = Fixture();
        graph.Artifacts.PublishError = new IOException("simulated publish race");

        Assert.Equal(
            LocalResultExportDisposition.Failed,
            (await graph.Coordinator.ExportStandaloneAsync(graph.BatchId)).Disposition);
        graph.Artifacts.PublishError = null;
        graph.Artifacts.State = LocalResultArtifactState.Valid;

        var retry = await graph.Coordinator.ExportStandaloneAsync(graph.BatchId);

        Assert.Equal(LocalResultExportDisposition.Failed, retry.Disposition);
        Assert.Equal("UNEXPECTED_EXISTING_RESULT", graph.Store.Record!.SanitizedErrorCode);
        Assert.Null(graph.Store.Record.FinalFileName);
    }

    [Fact]
    public async Task ExportEntry_RecoversAnExpiredInProgressLeaseWithoutAnotherRestart()
    {
        var graph = Fixture(LocalResultArtifactState.Valid);
        await graph.Store.CreateOrGetAsync(LocalResultExportRequest.Create(
            LocalResultOwnerKind.StandaloneShareBatch, graph.BatchId,
            LocalResultExportKind.StandaloneShareWorkbook, 1,
            "recovered.xlsx", Now));
        graph.Store.MarkInProgressExpired();

        var result = await graph.Coordinator.ExportStandaloneAsync(graph.BatchId);

        Assert.Equal(LocalResultExportDisposition.Exported, result.Disposition);
        Assert.Equal("recovered.xlsx", graph.Store.Record!.FinalFileName);
    }

    [Fact]
    public async Task ProcessLocalExportSerializesRecoveryBehindAnActivePublish()
    {
        var graph = Fixture();
        graph.Artifacts.BlockPublish = true;
        var export = graph.Coordinator.ExportStandaloneAsync(graph.BatchId);
        await graph.Artifacts.PublishStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var recoverCallsWhilePublishing = graph.Store.RecoverCallCount;

        var recovery = graph.Coordinator.RecoverAsync();
        await Task.Delay(25);

        Assert.False(recovery.IsCompleted);
        Assert.Equal(recoverCallsWhilePublishing, graph.Store.RecoverCallCount);
        graph.Artifacts.ReleasePublish.TrySetResult();
        await export;
        await recovery;
    }

    [Fact]
    public async Task NonTerminalOwner_DoesNotCreateOrAcquireExport()
    {
        var graph = Fixture();
        graph.Owners.IsTerminal = false;

        var result = await graph.Coordinator.ExportStandaloneAsync(graph.BatchId);

        Assert.Equal(LocalResultExportDisposition.NotReady, result.Disposition);
        Assert.Null(graph.Store.Record);
        Assert.Empty(graph.Events);
    }

    [Fact]
    public async Task ExistingInvalidTarget_FailsWithoutOverwrite()
    {
        var graph = Fixture(LocalResultArtifactState.Invalid);

        var result = await graph.Coordinator.ExportStandaloneAsync(graph.BatchId);

        Assert.Equal(LocalResultExportDisposition.Failed, result.Disposition);
        Assert.Null(graph.Artifacts.PublishedTarget);
        Assert.Equal(LocalResultExportStatus.Failed, graph.Store.Record!.Status);
        Assert.Equal("INVALID_EXISTING_RESULT", graph.Store.Record.SanitizedErrorCode);
    }

    [Fact]
    public async Task ShutdownCancellation_ReleasesLeaseAsFailedAndRethrows()
    {
        var graph = Fixture();
        graph.Artifacts.PublishError = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => graph.Coordinator.ExportStandaloneAsync(graph.BatchId));

        Assert.Equal(LocalResultExportStatus.Failed, graph.Store.Record!.Status);
        Assert.Equal("EXPORT_CANCELLED", graph.Store.Record.SanitizedErrorCode);
    }

    [Fact]
    public async Task StartupRecovery_DiscoversTerminalOwnerAndCreatesMissingExport()
    {
        var graph = Fixture();

        var completed = await graph.Coordinator.RecoverAsync();

        Assert.Equal(1, completed);
        Assert.Equal(LocalResultExportStatus.Succeeded, graph.Store.Record!.Status);
    }

    private static Graph Fixture(
        LocalResultArtifactState state = LocalResultArtifactState.Missing,
        ILocalResultExportObserver? observer = null)
    {
        var batchId = Guid.NewGuid();
        var events = new List<string>();
        var store = new FakeStore(events);
        var owners = new FakeOwners(batchId);
        var artifacts = new FakeArtifacts(events) { State = state };
        var coordinator = new LocalResultExportCoordinator(
            store, owners, artifacts,
            new FixedTimeProvider(Now), TimeSpan.FromMinutes(5), observer: observer);
        return new(batchId, coordinator, store, owners, artifacts, events);
    }

    private sealed record Graph(
        Guid BatchId,
        LocalResultExportCoordinator Coordinator,
        FakeStore Store,
        FakeOwners Owners,
        FakeArtifacts Artifacts,
        List<string> Events);

    private sealed class FakeOwners(Guid batchId) : ILocalResultExportOwnerRepository
    {
        public bool IsTerminal { get; set; } = true;
        public bool ProvesCandidates { get; set; } = true;

        public Task<LocalResultExportOwnerSnapshot?> GetAsync(
            LocalResultExportKey key, CancellationToken cancellationToken = default) =>
            Task.FromResult<LocalResultExportOwnerSnapshot?>(key.OwnerId == batchId
                ? new(key, @"C:\Results", IsTerminal, 1)
                : null);

        public Task<Page<LocalResultExportOwnerSnapshot>> GetReadyPageAsync(
            PageQuery query, CancellationToken cancellationToken = default)
        {
            var items = IsTerminal
                ? new[]
                {
                    new LocalResultExportOwnerSnapshot(new(
                        LocalResultOwnerKind.StandaloneShareBatch, batchId,
                        LocalResultExportKind.StandaloneShareWorkbook, 1), @"C:\Results", true, 1),
                }
                : [];
            return Task.FromResult(new Page<LocalResultExportOwnerSnapshot>(
                items, items.Length, query.PageNumber, query.PageSize));
        }

        public Task<IReadOnlyList<LocalResultOperationEvidence>> GetProvenOperationEvidenceAsync(
            LocalResultExportKey key,
            IReadOnlyList<LocalResultOperationIdentity> candidates,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalResultOperationEvidence>>(ProvesCandidates
                ? candidates.Select(candidate => new LocalResultOperationEvidence(
                    candidate.AdapterKind, candidate.AccountId, candidate.OperationId,
                    candidate.Generation, new string('a', 64))).ToArray()
                : []);

    }

    private sealed class FakeArtifacts(List<string> events) : ILocalResultExportArtifactService
    {
        public LocalResultArtifactState State { get; set; }
        public Exception? PublishError { get; set; }
        public bool BlockPublish { get; set; }
        public TaskCompletionSource PublishStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleasePublish { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? PublishedTarget { get; private set; }
        public IReadOnlyList<LocalResultOperationEvidence> PublishedEvidence { get; private set; } = [];

        public Task<string> ChooseTargetFileNameAsync(
            LocalResultExportOwnerSnapshot owner, CancellationToken cancellationToken = default)
        {
            events.Add("choose");
            return Task.FromResult("百度分享结果_20260825_160000.xlsx");
        }

        public Task<LocalResultArtifactState> InspectAsync(
            LocalResultExportOwnerSnapshot owner, string targetFileName,
            CancellationToken cancellationToken = default)
        {
            events.Add("inspect");
            return Task.FromResult(State);
        }

        public async Task<LocalResultArtifactPublishReceipt> PublishAsync(
            LocalResultExportOwnerSnapshot owner, string targetFileName,
            CancellationToken cancellationToken = default,
            IReadOnlyList<LocalResultOperationEvidence>? provenOperations = null)
        {
            events.Add("publish");
            if (PublishError is not null) throw PublishError;
            PublishStarted.TrySetResult();
            if (BlockPublish) await ReleasePublish.Task.WaitAsync(cancellationToken);
            PublishedTarget = targetFileName;
            PublishedEvidence = provenOperations?.ToArray() ?? [];
            return new LocalResultArtifactPublishReceipt(targetFileName, 1);
        }

        public Task<LocalResultExportCompletedEvidence?> GetVerifiedEvidenceAsync(
            LocalResultExportOwnerSnapshot owner,
            string targetFileName,
            IReadOnlyList<LocalResultOperationIdentity> candidates,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(PublishedEvidence.Count > 0
                ? new LocalResultExportCompletedEvidence(
                owner.Key,
                PublishedEvidence,
                new string('b', 64),
                new string('c', 64))
                : null);
    }

    private sealed class FakeStore(List<string> events) : ILocalResultExportStore
    {
        public LocalResultExportRecord? Record { get; private set; }
        public int RecoverCallCount { get; private set; }
        private Guid leaseOwner;

        public void MarkRecoveredAfterExpiredLease()
        {
            Record = Record! with
            {
                Status = LocalResultExportStatus.Pending,
                SanitizedErrorCode = "RECOVERED_EXPIRED_LEASE",
                LeaseGeneration = 1,
                Revision = Record!.Revision + 2,
            };
        }

        public void MarkInProgressExpired()
        {
            Record = Record! with
            {
                Status = LocalResultExportStatus.InProgress,
                LeaseGeneration = 1,
                LeaseExpiresAt = Now.AddSeconds(-1),
                Revision = Record!.Revision + 1,
            };
        }

        public Task<LocalResultExportRecord> CreateOrGetAsync(
            LocalResultExportRequest request, CancellationToken cancellationToken = default)
        {
            Record ??= new(Guid.NewGuid(), request.Key, LocalResultExportStatus.Pending,
                request.TargetFileName, null, null, 0, null, 0, request.CreatedAt, request.CreatedAt);
            return Task.FromResult(Record);
        }

        public Task<LocalResultExportRecord?> GetAsync(
            LocalResultExportKey key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Record?.Key == key ? Record : null);

        public Task<Page<LocalResultExportRecord>> GetPageAsync(
            PageQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Page<LocalResultExportRecord>(
                Record is null ? [] : [Record], Record is null ? 0 : 1, query.PageNumber, query.PageSize));

        public Task<LocalResultExportLease?> TryAcquireAsync(
            LocalResultExportKey key, long expectedRevision, Guid leaseOwnerId,
            DateTimeOffset now, DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default)
        {
            if (Record is null || Record.Revision != expectedRevision ||
                Record.Status is not (LocalResultExportStatus.Pending or LocalResultExportStatus.Failed))
                return Task.FromResult<LocalResultExportLease?>(null);
            events.Add("acquire");
            leaseOwner = leaseOwnerId;
            Record = Record with
            {
                Status = LocalResultExportStatus.InProgress,
                LeaseGeneration = Record.LeaseGeneration + 1,
                LeaseExpiresAt = leaseExpiresAt,
                Revision = Record.Revision + 1,
                UpdatedAt = now,
            };
            return Task.FromResult<LocalResultExportLease?>(new(
                Record.Id, key, Record.LeaseGeneration, leaseOwnerId,
                leaseExpiresAt, Record.Revision));
        }

        public Task<bool> TryCompleteAsync(
            Guid exportId, int expectedGeneration, long expectedLeaseGeneration,
            Guid expectedLeaseOwnerId, long expectedRevision, DateTimeOffset expectedLeaseExpiresAt,
            string finalFileName, DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            if (!Matches(exportId, expectedGeneration, expectedLeaseGeneration,
                    expectedLeaseOwnerId, expectedRevision, expectedLeaseExpiresAt))
                return Task.FromResult(false);
            events.Add("complete");
            Record = Record! with
            {
                Status = LocalResultExportStatus.Succeeded,
                FinalFileName = finalFileName,
                SanitizedErrorCode = null,
                LeaseExpiresAt = null,
                Revision = Record!.Revision + 1,
                UpdatedAt = updatedAt,
            };
            return Task.FromResult(true);
        }

        public Task<bool> TryFailAsync(
            Guid exportId, int expectedGeneration, long expectedLeaseGeneration,
            Guid expectedLeaseOwnerId, long expectedRevision, DateTimeOffset expectedLeaseExpiresAt,
            string sanitizedErrorCode, DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            if (!Matches(exportId, expectedGeneration, expectedLeaseGeneration,
                    expectedLeaseOwnerId, expectedRevision, expectedLeaseExpiresAt))
                return Task.FromResult(false);
            Record = Record! with
            {
                Status = LocalResultExportStatus.Failed,
                SanitizedErrorCode = sanitizedErrorCode,
                LeaseExpiresAt = null,
                Revision = Record!.Revision + 1,
                UpdatedAt = updatedAt,
            };
            return Task.FromResult(true);
        }

        public Task<int> RecoverExpiredAsync(
            DateTimeOffset now, int maximumCount, CancellationToken cancellationToken = default)
        {
            RecoverCallCount++;
            if (Record is not { Status: LocalResultExportStatus.InProgress, LeaseExpiresAt: { } expires } ||
                expires > now)
                return Task.FromResult(0);
            Record = Record with
            {
                Status = LocalResultExportStatus.Pending,
                SanitizedErrorCode = "RECOVERED_EXPIRED_LEASE",
                LeaseExpiresAt = null,
                Revision = Record.Revision + 1,
            };
            return Task.FromResult(1);
        }

        private bool Matches(
            Guid id, int generation, long leaseGeneration, Guid owner,
            long revision, DateTimeOffset expires) =>
            Record is not null && Record.Id == id && Record.Key.Generation == generation &&
            Record.LeaseGeneration == leaseGeneration && leaseOwner == owner &&
            Record.Revision == revision && Record.LeaseExpiresAt == expires;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class ThrowingObserver : ILocalResultExportObserver
    {
        public int CallCount { get; private set; }
        public IReadOnlyList<LocalResultOperationIdentity> GetTrackedOperations(LocalResultExportKey exportKey) =>
            [new("BaiduShare", Guid.NewGuid(), Guid.NewGuid(), 1)];

        public Task OnLocalResultExportCompletedAsync(
            LocalResultExportCompletedEvidence evidence,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException("simulated acceptance telemetry failure");
        }
    }

    private sealed class CountingObserver : ILocalResultExportObserver
    {
        public int CallCount { get; private set; }

        public IReadOnlyList<LocalResultOperationIdentity> GetTrackedOperations(LocalResultExportKey exportKey) =>
            [new("BaiduShare", Guid.NewGuid(), Guid.NewGuid(), 1)];

        public Task OnLocalResultExportCompletedAsync(
            LocalResultExportCompletedEvidence evidence,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }
}
