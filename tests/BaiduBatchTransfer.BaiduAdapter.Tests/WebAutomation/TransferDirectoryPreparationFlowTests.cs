using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.Tests.WebAutomation;

public sealed class TransferDirectoryPreparationFlowTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
    private static readonly string Fingerprint = new('a', 64);
    private static readonly AdapterCapabilityVersion Capability = new(
        "BaiduTransfer", "transfer-candidate-v2", "transfer-selector-v2", "build-1");

    [Fact]
    public async Task ExistingDirectoryIsTraversedWithoutCreationAndPickerIsCancelled()
    {
        var request = Request("/目标/成品");
        var store = new MemoryEvidenceStore(Attempt(request));
        var session = new FixtureSession(store,
            Root(Folder("target", "目标")),
            Listing("target", "/目标", [Folder("finished", "成品")], ["target"]),
            Listing("finished", "/目标/成品", [], ["target", "finished"]));

        var outcome = await new TransferDirectoryPreparationFlow(new FixedTimeProvider(Now))
            .ExecuteAsync(session, store, request);

        Assert.Equal(TransferDirectoryPreparationStatus.Prepared, outcome.Status);
        Assert.Equal("finished", outcome.Directory!.RemoteId);
        Assert.Equal("/目标/成品", outcome.Directory.Path.Value);
        Assert.Equal(0, session.CreateCount);
        Assert.Equal(1, session.CancelCount);
        Assert.NotNull(store.Current.SourceEvidence);
    }

    [Fact]
    public async Task TwoMissingSegmentsPersistIntentBeforeEveryCreateAndConfirmExactChildren()
    {
        var request = Request("/甲/乙");
        var store = new MemoryEvidenceStore(Attempt(request));
        var session = new FixtureSession(store,
            Root(),
            Root(Folder("a", "甲")),
            Listing("a", "/甲", [], ["a"]),
            Listing("a", "/甲", [Folder("b", "乙")], ["a"]),
            Listing("b", "/甲/乙", [], ["a", "b"]));

        var outcome = await new TransferDirectoryPreparationFlow(new FixedTimeProvider(Now))
            .ExecuteAsync(session, store, request);

        Assert.Equal(TransferDirectoryPreparationStatus.Prepared, outcome.Status);
        Assert.Equal(2, session.CreateCount);
        Assert.All(store.Mutations, mutation => Assert.Equal(
            TransferDirectoryMutationState.Confirmed, mutation.State));
        Assert.Equal(["/甲", "/甲/乙"], store.Mutations.Select(item => item.CanonicalChildPath.Value));
    }

    [Fact]
    public async Task SegmentBoundaryFalsePrefixReturnsToVerifiedRoot()
    {
        var request = Request("/目标/成品");
        var store = new MemoryEvidenceStore(Attempt(request));
        var session = new FixtureSession(store,
            Listing("other", "/目标旧", [], ["other"]),
            Root(Folder("target", "目标")),
            Listing("target", "/目标", [Folder("finished", "成品")], ["target"]),
            Listing("finished", "/目标/成品", [], ["target", "finished"]));

        var outcome = await new TransferDirectoryPreparationFlow(new FixedTimeProvider(Now))
            .ExecuteAsync(session, store, request);

        Assert.Equal(TransferDirectoryPreparationStatus.Prepared, outcome.Status);
        Assert.Equal(1, session.OpenRootCount);
    }

    [Fact]
    public async Task SameNameFileStopsWithoutCreatingDirectory()
    {
        var request = Request("/冲突");
        var store = new MemoryEvidenceStore(Attempt(request));
        var session = new FixtureSession(store, Root(File("file", "冲突")));

        var outcome = await new TransferDirectoryPreparationFlow(new FixedTimeProvider(Now))
            .ExecuteAsync(session, store, request);

        Assert.Equal(TransferDirectoryPreparationStatus.NeedsAttention, outcome.Status);
        Assert.Equal(BaiduErrorKind.NotValidated, outcome.ErrorKind);
        Assert.Equal(0, session.CreateCount);
    }

    [Fact]
    public async Task FolderCandidatesTreatTheCompleteListingAsEligibleFoldersOnly()
    {
        var request = Request("/甲");
        var store = new MemoryEvidenceStore(Attempt(request));
        var session = new FixtureSession(store,
            Listing("root", "/", [Folder("jia", "甲")], [], TransferDirectoryListingScope.FolderCandidates),
            Listing("jia", "/甲", [], ["jia"], TransferDirectoryListingScope.FolderCandidates));

        var outcome = await new TransferDirectoryPreparationFlow(new FixedTimeProvider(Now))
            .ExecuteAsync(session, store, request);

        Assert.Equal(TransferDirectoryPreparationStatus.Prepared, outcome.Status);
        Assert.Equal(0, session.CreateCount);
    }

    [Fact]
    public async Task LostCreateResponseLeavesOutcomeUnknownAndReportsOnlyConfirmedPartialPath()
    {
        var request = Request("/不确定");
        var store = new MemoryEvidenceStore(Attempt(request));
        var session = new FixtureSession(store, Root()) { ThrowAfterCreate = true };

        var outcome = await new TransferDirectoryPreparationFlow(new FixedTimeProvider(Now))
            .ExecuteAsync(session, store, request);

        Assert.Equal(TransferDirectoryPreparationStatus.NeedsAttention, outcome.Status);
        Assert.Equal("/", outcome.ConfirmedPartialPath!.Value);
        Assert.Equal(TransferDirectoryMutationState.OutcomeUnknown, Assert.Single(store.Mutations).State);
    }

    [Fact]
    public async Task PriorGenerationUnknownIntentIsReconciledBeforeSourceAndNeverCreatedAgain()
    {
        var request = Request("/已创建") with { Generation = 2 };
        var store = new MemoryEvidenceStore(Attempt(request));
        store.Mutations.Add(new TransferDirectoryMutationRecord(
            request.OperationId, request.AccountId, 1, 0, "root", null,
            new RemotePath("/已创建"), "generation-0", "uid-1", Capability,
            TransferDirectoryMutationState.OutcomeUnknown, 3));
        var session = new FixtureSession(store,
            Root(Folder("created", "已创建")));

        var outcome = await new TransferDirectoryPreparationFlow(new FixedTimeProvider(Now))
            .ExecuteAsync(session, store, request);

        Assert.Equal(TransferDirectoryPreparationStatus.NeedsAttention, outcome.Status);
        Assert.Equal("/已创建", outcome.ConfirmedPartialPath!.Value);
        Assert.Equal(0, session.SourceOpenCount);
        Assert.Equal(0, session.CreateCount);
        Assert.Equal(1, session.RecoveryReadCount);
        Assert.Equal(TransferDirectoryMutationState.Confirmed, Assert.Single(store.Mutations).State);
    }

    [Fact]
    public async Task SameGenerationRestartReusesPersistedSourceEvidenceWithoutUnlockingAgain()
    {
        var request = Request("/目标");
        var source = new TransferSourceEvidence(
            new RemoteItemIdentitySet([new("source", "来源")]), 1, true, true,
            "source-generation", "uid-1", Capability, Fingerprint);
        var attempt = new WebAutomationAttemptRecord(
            request.OperationId, request.AccountId, request.Generation,
            WebAutomationOwnerKind.Transfer, WebAutomationAttemptState.Preparing,
            null, null, source.ObservationFingerprint, false, 1,
            Now.AddMinutes(-2), Now.AddMinutes(-1), sourceEvidence: source);
        var store = new MemoryEvidenceStore(attempt);
        var session = new FixtureSession(store,
            Root(Folder("target", "目标")),
            Listing("target", "/目标", [], ["target"]));

        var outcome = await new TransferDirectoryPreparationFlow(new FixedTimeProvider(Now))
            .ExecuteAsync(session, store, request);

        Assert.Equal(TransferDirectoryPreparationStatus.Prepared, outcome.Status);
        Assert.Equal(0, session.SourceOpenCount);
    }

    [Fact]
    public async Task SourceObservationFailurePreservesOnlyTypedSafeDiagnosticCode()
    {
        var request = Request("/目标");
        var store = new MemoryEvidenceStore(Attempt(request));
        var session = new FixtureSession(store)
        {
            SourceFailure = new BaiduAutomationNotValidatedException(
                BaiduAutomationDenialReason.ObservationNotValidated,
                "untrusted https://pan.baidu.com/s/private?pwd=1234 uid=secret",
                BaiduCandidateDiagnosticCode.TrustedRootMissing),
        };

        var outcome = await new TransferDirectoryPreparationFlow(new FixedTimeProvider(Now))
            .ExecuteAsync(session, store, request);

        Assert.Equal(TransferDirectoryPreparationStatus.NeedsAttention, outcome.Status);
        Assert.Equal(BaiduErrorKind.NotValidated, outcome.ErrorKind);
        Assert.Equal(BaiduCandidateDiagnosticCode.TrustedRootMissing, outcome.SafeDiagnosticCode);
        Assert.DoesNotContain("private", outcome.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", outcome.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static TransferDirectoryPreparationRequest Request(string path) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Share(), new RemotePath(path), Capability);

    private static WebAutomationAttemptRecord Attempt(TransferDirectoryPreparationRequest request) =>
        WebAutomationAttemptRecord.Create(request.OperationId, request.AccountId, request.Generation,
            WebAutomationOwnerKind.Transfer, Now.AddMinutes(-1));

    private static ShareReference Share() => new(
        "fictional", "https://pan.baidu.com/s/fictional?pwd=1111", "1111");

    private static TransferRemoteItem Folder(string id, string name) => new(
        new RemoteItemIdentity(id, name), TransferRemoteItemKind.Folder, name);

    private static TransferRemoteItem File(string id, string name) => new(
        new RemoteItemIdentity(id, name), TransferRemoteItemKind.File, name);

    private static TransferDirectoryListing Root(params TransferRemoteItem[] items) =>
        Listing("root", "/", items, []);

    private static TransferDirectoryListing Listing(
        string id, string path, IReadOnlyList<TransferRemoteItem> items, IReadOnlyList<string> ancestors,
        TransferDirectoryListingScope scope = TransferDirectoryListingScope.AllItems) =>
        TransferDirectoryListing.Exact(
            new RemoteDirectory(id, new RemotePath(path)), ancestors, items, items.Count,
            reachedEnd: true, isStable: true, "generation-0", "uid-1", Fingerprint, scope);

    private sealed class FixtureSession(
        MemoryEvidenceStore store,
        params TransferDirectoryListing[] listings) : ITransferDirectoryPreparationSession
    {
        private readonly Queue<TransferDirectoryListing> listings = new(listings);
        public int SourceOpenCount { get; private set; }
        public int CreateCount { get; private set; }
        public int CancelCount { get; private set; }
        public int OpenRootCount { get; private set; }
        public int RecoveryReadCount { get; private set; }
        public bool ThrowAfterCreate { get; init; }
        public Exception? SourceFailure { get; init; }

        public Task OpenSourceAsync(ShareReference share, CancellationToken cancellationToken)
        {
            SourceOpenCount++;
            return Task.CompletedTask;
        }

        public Task<TransferSourceListing> ReadCompleteSourceAsync(CancellationToken cancellationToken) =>
            SourceFailure is not null
                ? Task.FromException<TransferSourceListing>(SourceFailure)
                : Task.FromResult(new TransferSourceListing(new TransferSourceEvidence(
                    new RemoteItemIdentitySet([new("source", "来源")]), 1, true, true,
                    "source-generation", "uid-1", Capability, Fingerprint)));

        public Task EnterExtractionCodeAsync(string extractionCode, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<TransferDirectoryListing> OpenDestinationPickerAsync(CancellationToken cancellationToken) =>
            Next();

        public Task<TransferDirectoryListing> OpenVerifiedRootAsync(CancellationToken cancellationToken)
        {
            OpenRootCount++;
            return Next();
        }

        public Task<TransferDirectoryListing> EnterDirectoryAsync(
            RemoteDirectory directory, CancellationToken cancellationToken) => Next();

        public Task BeginCreateDirectoryAsync(string segment, CancellationToken cancellationToken)
        {
            Assert.Contains(store.Mutations, mutation =>
                mutation.State == TransferDirectoryMutationState.Intent &&
                mutation.CanonicalChildPath.Value.EndsWith('/' + segment, StringComparison.Ordinal));
            CreateCount++;
            return Task.CompletedTask;
        }

        public Task ConfirmDirectoryCreationAsync(CancellationToken cancellationToken) =>
            ThrowAfterCreate
                ? Task.FromException(new InvalidOperationException("synthetic lost response"))
                : Task.CompletedTask;

        public Task<TransferDirectoryListing> ReadCurrentDirectoryAsync(CancellationToken cancellationToken) =>
            Next();

        public Task CancelDestinationPickerAsync(CancellationToken cancellationToken)
        {
            CancelCount++;
            return Task.CompletedTask;
        }

        public Task<TransferDirectoryListing> ReadExactParentForRecoveryAsync(
            RemoteDirectory parent, IReadOnlyList<string> expectedAncestors,
            TransferDirectoryRecoveryAuthorization authorization,
            CancellationToken cancellationToken)
        {
            RecoveryReadCount++;
            return Next();
        }

        private Task<TransferDirectoryListing> Next() => Task.FromResult(listings.Dequeue());
    }

    private sealed class MemoryEvidenceStore(WebAutomationAttemptRecord current) :
        ITransferDirectoryPreparationEvidenceStore
    {
        public WebAutomationAttemptRecord Current { get; private set; } = current;
        public List<TransferDirectoryMutationRecord> Mutations { get; } = [];

        public Task<WebAutomationAttemptRecord?> GetAttemptAsync(
            TransferDirectoryPreparationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<WebAutomationAttemptRecord?>(Current);

        public Task<IReadOnlyList<TransferDirectoryMutationRecord>> GetUnresolvedMutationsAsync(
            Guid operationId, Guid accountId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TransferDirectoryMutationRecord>>(
                Mutations.Where(item => item.State != TransferDirectoryMutationState.Confirmed).ToArray());

        public Task<TransferDirectoryRecoveryAuthorization?> AuthorizeRecoveryAsync(
            TransferDirectoryRecoveryAuthorizationRequest request,
            CancellationToken cancellationToken)
        {
            var mutation = request.Mutation;
            var segments = mutation.CanonicalChildPath.Value.Split(
                '/', StringSplitOptions.RemoveEmptyEntries);
            var parentPath = segments.Length <= 1 ? new RemotePath("/") :
                new RemotePath('/' + string.Join('/', segments.Take(segments.Length - 1)));
            return Task.FromResult<TransferDirectoryRecoveryAuthorization?>(new(
                mutation.OperationId, request.BatchId, mutation.AccountId, mutation.Generation,
                mutation.OwningAttemptRevision, mutation.ParentExactId, parentPath,
                mutation.CanonicalChildPath, mutation.BaiduUserId, mutation.Capability,
                Now.AddMinutes(2)));
        }

        public Task<bool> PersistSourceEvidenceAsync(
            TransferDirectoryPreparationRequest request, long expectedRevision,
            TransferSourceEvidence evidence, DateTimeOffset updatedAt, CancellationToken cancellationToken)
        {
            if (Current.Revision != expectedRevision) return Task.FromResult(false);
            Current = new WebAutomationAttemptRecord(
                Current.OperationId, Current.AccountId, Current.Generation, Current.OwnerKind,
                Current.State, Current.ResumeStage, Current.Baseline, evidence.ObservationFingerprint,
                Current.IsWaitingForUser, Current.Revision + 1, Current.CreatedAt, updatedAt,
                sourceEvidence: evidence);
            return Task.FromResult(true);
        }

        public Task<bool> BeginMutationAsync(
            TransferDirectoryMutationRecord mutation, DateTimeOffset updatedAt,
            CancellationToken cancellationToken)
        {
            Mutations.Add(mutation);
            return Task.FromResult(true);
        }

        public Task<bool> ResolveMutationAsync(
            TransferDirectoryMutationRecord expected, TransferDirectoryMutationState nextState,
            string? childExactId, DateTimeOffset updatedAt, CancellationToken cancellationToken)
        {
            var index = Mutations.IndexOf(expected);
            if (index < 0) return Task.FromResult(false);
            Mutations[index] = new TransferDirectoryMutationRecord(
                expected.OperationId, expected.AccountId, expected.Generation, expected.SegmentOrdinal,
                expected.ParentExactId, childExactId, expected.CanonicalChildPath,
                expected.ListingGenerationToken, expected.BaiduUserId, expected.Capability,
                nextState, expected.OwningAttemptRevision);
            return Task.FromResult(true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
