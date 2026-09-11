using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Tests.Scheduling;

public sealed partial class OperationExecutorTests
{
    [Theory]
    [InlineData("adapter")]
    [InlineData("resolver")]
    [InlineData("store")]
    [InlineData("dailyLimit")]
    public void ResolvedConstructorRequiresDependencies(string missing)
    {
        var (snapshot, session) = ManagedSnapshot();
        var store = new RecordingExecutionStore(snapshot);
        var adapter = new ExecutionBoundTransferAdapter(new OperationBoundRecordingAdapter(), new ManagedRecordingAdapter());
        var exception = Assert.Throws<ArgumentNullException>(() => new OperationExecutor(
            missing == "adapter" ? null! : adapter,
            missing == "resolver" ? null! : ReadyResolver(session),
            missing == "store" ? null! : store,
            missing == "dailyLimit" ? null! : new AccountDailyLimitService(store)));
        Assert.Equal(missing == "resolver" ? "contextResolver" : missing, exception.ParamName);
    }

    [Fact]
    public async Task RealResolverAndExecutorUseManagedBindingWithoutLegacyStore()
    {
        var (snapshot, session) = ManagedSnapshot();
        var store = new RecordingExecutionStore(snapshot);
        var installation = new BrowserInstallationIdentity(session.InstallationId, BrowserFamily.Chrome,
            @"C:\SyntheticChrome", @"C:\SyntheticChrome\chrome.exe", "Google LLC", "Google Chrome", "synthetic-file-id");
        var bindingStore = new FixedBindingStore(new BaiduExecutionBindingSnapshot(
            new BaiduExecutionAccount(snapshot.AccountId, session.StableBaiduUserId, "Active", true, false),
            new StoredBrowserInstallation(installation, BrowserInstallationLifecycleState.Active, 1),
            new StoredManagedBrowserSession(session, ManagedBrowserSessionLifecycleState.Active, 1)));
        var legacyStore = new ForbiddenLegacySessionStore();
        var resolver = new BaiduExecutionContextResolver(bindingStore, legacyStore);

        await ResolvedExecutor(store, new ManagedRecordingAdapter(), resolver).ExecuteAsync(snapshot.OperationId);

        Assert.True(store.ExactCompleted);
        Assert.Equal(0, legacyStore.LoadCalls);
        Assert.Same(snapshot.ExecutionProfile, bindingStore.Profile);
    }

    [Theory]
    [InlineData(BrowserFamily.Chrome, false)]
    [InlineData(BrowserFamily.Edge, false)]
    [InlineData(BrowserFamily.Chrome, true)]
    [InlineData(BrowserFamily.Edge, true)]
    public async Task ResolvedManagedExecutionPreservesExactEvidenceAndSubmissionOrder(BrowserFamily family, bool multiple)
    {
        var (snapshot, session) = ManagedSnapshot(family);
        var timeline = new List<string>();
        var store = new RecordingExecutionStore(snapshot, timeline) { RejectSplitSuccessWrites = true };
        var items = new RemoteItemIdentitySet(multiple
            ? [new RemoteItemIdentity("folder-id", "folder"), new RemoteItemIdentity("apk-id", "item.apk")]
            : [new RemoteItemIdentity("apk-id", "item.apk")]);
        var managed = new ManagedRecordingAdapter(timeline)
        {
            SaveResult = BaiduAdapterResult<TransferReceipt>.Success(TransferReceipt.WithExactEvidence(Now, "request", items)),
        };
        var legacy = new OperationBoundRecordingAdapter();
        var resolver = new FixedExecutionResolver(BaiduExecutionResolution.Ready(BaiduExecutionContext.Managed(session)))
        {
            Timeline = timeline,
        };
        using var cancellation = new CancellationTokenSource();

        await ResolvedExecutor(store, managed, resolver, legacy).ExecuteAsync(snapshot.OperationId, cancellation.Token);

        Assert.Same(items, store.ExactRemoteItems);
        Assert.True(store.ExactCompleted);
        Assert.Equal(["Resolve", "Preparing", "Ensure", "Prepare", "Boundary", "Save", "ExactCompleted"], timeline);
        Assert.Same(snapshot.ExecutionProfile, resolver.Profile);
        Assert.Equal(snapshot.AccountId, resolver.AccountId);
        Assert.Equal(cancellation.Token, resolver.Token);
        Assert.Equal(3, managed.Contexts.Count);
        Assert.All(managed.Contexts, context =>
        {
            Assert.Equal(snapshot.OperationId, context.OperationId);
            Assert.Equal(snapshot.BatchId, context.BatchId);
            Assert.Equal(snapshot.AccountId, context.AccountId);
            Assert.Equal(4, context.Generation);
            Assert.True(context.IsRecovery);
            Assert.Same(snapshot.ExecutionProfile, context.ExecutionProfile);
            Assert.Same(snapshot.Share, context.SourceShare);
        });
        Assert.All(managed.Sessions, actual => Assert.Same(session, actual));
        Assert.All(managed.Tokens, token => Assert.Equal(cancellation.Token, token));
        Assert.Same(managed.Directory, managed.SavedDestination);
        Assert.Equal("/base/outer", managed.Directory!.Path.Value);
        Assert.Equal(4, store.BoundaryExpectedAttemptCount);
        Assert.Equal(0, legacy.EnsureCalls);
        Assert.Equal(0, legacy.SaveCalls);
    }

    [Fact]
    public async Task ResolvedLegacyExecutionUsesBoundLegacyAdapterOnly()
    {
        var snapshot = Snapshot(null) with { BatchId = Guid.NewGuid() };
        var session = await new MemorySessionStore(snapshot.AccountId).LoadAsync("unused", default);
        var store = new RecordingExecutionStore(snapshot);
        var managed = new ManagedRecordingAdapter();
        var legacy = new OperationBoundRecordingAdapter();
        var resolver = new FixedExecutionResolver(BaiduExecutionResolution.Ready(BaiduExecutionContext.Legacy(session!)));

        await ResolvedExecutor(store, managed, resolver, legacy).ExecuteAsync(snapshot.OperationId);

        Assert.True(store.Succeeded);
        Assert.Equal(1, legacy.PrepareCalls);
        Assert.Equal(1, legacy.SaveCalls);
        Assert.Empty(managed.Contexts);
    }

    [Theory]
    [InlineData(BaiduExecutionResolutionStatus.AccountUnavailable, "UserInterventionRequired")]
    [InlineData(BaiduExecutionResolutionStatus.LoginRequired, "ExpiredLogin")]
    [InlineData(BaiduExecutionResolutionStatus.BrowserUnavailable, "UserInterventionRequired")]
    [InlineData(BaiduExecutionResolutionStatus.AccountIdentityMismatch, "UserInterventionRequired")]
    public async Task UnavailableResolutionPausesBeforePreparingOrCharging(BaiduExecutionResolutionStatus status, string code)
    {
        var (snapshot, _) = ManagedSnapshot();
        var store = new RecordingExecutionStore(snapshot);
        var managed = new ManagedRecordingAdapter();
        var resolver = new FixedExecutionResolver(BaiduExecutionResolution.Unavailable(status));

        await ResolvedExecutor(store, managed, resolver).ExecuteAsync(snapshot.OperationId);

        Assert.True(store.PreSubmissionPaused);
        Assert.Equal(code, store.AccountPauseCode);
        Assert.Equal(SubmissionAcceptance.DefinitelyNotAccepted, store.PausedAcceptance);
        Assert.Null(store.ObservedSessionCapturedAtUtc);
        Assert.Null(store.PreparingExpectedAttemptCount);
        Assert.Null(store.BoundaryExpectedAttemptCount);
        Assert.Empty(managed.Contexts);
    }

    [Fact]
    public async Task OldConstructorRejectsManagedSnapshotWithoutReadingCookies()
    {
        var (snapshot, _) = ManagedSnapshot();
        var store = new RecordingExecutionStore(snapshot);
        var sessions = new ForbiddenLegacySessionStore();
        var adapter = new RecordingAdapter();

        await Executor(store, adapter, sessions).ExecuteAsync(snapshot.OperationId);

        Assert.Equal(0, sessions.LoadCalls);
        Assert.Equal(0, adapter.EnsureCalls);
        Assert.Equal("UserInterventionRequired", store.AccountPauseCode);
        Assert.Null(store.PreparingExpectedAttemptCount);
    }

    [Fact]
    public async Task DirectCookieOptInUsesBoundCookieSessionForManagedSnapshot()
    {
        var (managedSnapshot, _) = ManagedSnapshot();
        var snapshot = managedSnapshot with { RequiresExactShareEvidence = false };
        var store = new RecordingExecutionStore(snapshot);
        var adapter = new RecordingAdapter();
        var sessions = new MemorySessionStore(snapshot.AccountId);
        var executor = new OperationExecutor(
            adapter,
            sessions,
            store,
            new AccountDailyLimitService(store, new FixedTimeProvider(Now)),
            RetryPolicy.Default,
            new RecordingDelay(),
            new FixedTimeProvider(Now),
            allowLegacySessionForAnyProfile: true);

        await executor.ExecuteAsync(snapshot.OperationId);

        Assert.Equal(1, adapter.EnsureCalls);
        Assert.Equal(1, adapter.SaveCalls);
        Assert.True(store.Succeeded);
        Assert.False(store.AccountPaused);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeOrDuringResolutionDoesNotPrepare(bool cancelDuring)
    {
        var (snapshot, session) = ManagedSnapshot();
        var store = new RecordingExecutionStore(snapshot);
        using var cancellation = new CancellationTokenSource();
        var resolver = new FixedExecutionResolver(BaiduExecutionResolution.Ready(BaiduExecutionContext.Managed(session)))
        {
            BeforeReturn = cancelDuring ? cancellation.Cancel : null,
        };
        if (!cancelDuring) cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ResolvedExecutor(store, new ManagedRecordingAdapter(), resolver).ExecuteAsync(snapshot.OperationId, cancellation.Token));

        Assert.Empty(store.Events);
        Assert.Equal(cancelDuring ? 1 : 0, resolver.Events.Count);
    }

    [Fact]
    public async Task WrongResolvedSessionCannotReachEitherAdapterOrSubmission()
    {
        var (snapshot, _) = ManagedSnapshot();
        var (_, wrongSession) = ManagedSnapshot();
        var store = new RecordingExecutionStore(snapshot);
        var managed = new ManagedRecordingAdapter();
        var legacy = new OperationBoundRecordingAdapter();
        var resolver = new FixedExecutionResolver(BaiduExecutionResolution.Ready(BaiduExecutionContext.Managed(wrongSession)));

        await ResolvedExecutor(store, managed, resolver, legacy).ExecuteAsync(snapshot.OperationId);

        Assert.Empty(managed.Contexts);
        Assert.Equal(0, legacy.EnsureCalls);
        Assert.Null(store.BoundaryExpectedAttemptCount);
        Assert.False(store.ExactCompleted);
    }

    [Theory]
    [InlineData(false, BaiduErrorKind.InvalidLink)]
    [InlineData(true, BaiduErrorKind.InvalidLink)]
    [InlineData(false, BaiduErrorKind.ExpiredLogin)]
    [InlineData(true, BaiduErrorKind.ExpiredLogin)]
    public async Task ManagedPreparationFailuresNeverSubmit(bool duringPreparation, BaiduErrorKind error)
    {
        var (snapshot, session) = ManagedSnapshot();
        var store = new RecordingExecutionStore(snapshot);
        var managed = new ManagedRecordingAdapter
        {
            DirectoryError = duringPreparation ? null : error,
            PreparationError = duringPreparation ? error : null,
        };

        await ResolvedExecutor(store, managed, ReadyResolver(session)).ExecuteAsync(snapshot.OperationId);

        Assert.Equal(0, managed.SaveCalls);
        Assert.Null(store.BoundaryExpectedAttemptCount);
        Assert.Null(store.ObservedSessionCapturedAtUtc);
        if (error == BaiduErrorKind.ExpiredLogin) Assert.Equal("ExpiredLogin", store.AccountPauseCode);
        else Assert.Equal("InvalidLink", store.FailureCode);
    }

    [Fact]
    public async Task ManagedUnknownDirectoryMutationUsesDedicatedPendingStateAndNeverSubmits()
    {
        var (snapshot, session) = ManagedSnapshot();
        var store = new RecordingExecutionStore(snapshot);
        var managed = new ManagedRecordingAdapter
        {
            DirectoryOutcome = TransferDirectoryPreparationOutcome.NeedsAttention(
                new RemotePath("/base"), BaiduErrorKind.UnknownResponse),
        };

        await ResolvedExecutor(store, managed, ReadyResolver(session)).ExecuteAsync(snapshot.OperationId);

        Assert.True(store.PreparationOutcomeUnknown);
        Assert.Equal(SubmissionAcceptance.Unknown, store.PendingAcceptance);
        Assert.Equal(0, managed.SaveCalls);
        Assert.Null(store.BoundaryExpectedAttemptCount);
    }

    [Fact]
    public async Task ManagedUnknownPreparationMutationUsesDedicatedPendingStateAndNeverSubmits()
    {
        var (snapshot, session) = ManagedSnapshot();
        var store = new RecordingExecutionStore(snapshot);
        var managed = new ManagedRecordingAdapter
        {
            PreparationResult = BaiduAdapterResult<TransferPreparationReceipt>.Failure(
                BaiduErrorKind.UnknownResponse,
                "private",
                submissionAcceptance: SubmissionAcceptance.Unknown),
        };

        await ResolvedExecutor(store, managed, ReadyResolver(session)).ExecuteAsync(snapshot.OperationId);

        Assert.True(store.PreparationOutcomeUnknown);
        Assert.Equal(SubmissionAcceptance.Unknown, store.PendingAcceptance);
        Assert.Equal(0, managed.SaveCalls);
        Assert.Null(store.BoundaryExpectedAttemptCount);
        Assert.DoesNotContain("private", store.FailureMessage ?? string.Empty);
    }

    [Fact]
    public async Task ManagedDailyLimitRefusalReturnsToWaitingWithoutSaving()
    {
        var (snapshot, session) = ManagedSnapshot();
        var store = new RecordingExecutionStore(snapshot) { AcceptBoundary = false };
        var managed = new ManagedRecordingAdapter();

        await ResolvedExecutor(store, managed, ReadyResolver(session)).ExecuteAsync(snapshot.OperationId);

        Assert.True(store.WaitingForDailyLimit);
        Assert.Equal(0, managed.SaveCalls);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("throw")]
    [InlineData("cancel")]
    public async Task ManagedAmbiguousSaveRemainsPendingAndNeverFallsBack(string mode)
    {
        var (snapshot, session) = ManagedSnapshot();
        var store = new RecordingExecutionStore(snapshot);
        var managed = new ManagedRecordingAdapter
        {
            SaveResult = BaiduAdapterResult<TransferReceipt>.Failure(BaiduErrorKind.UnknownResponse, "private", submissionAcceptance: SubmissionAcceptance.Unknown),
            SaveException = mode == "throw" ? new InvalidOperationException("private") : mode == "cancel" ? new OperationCanceledException() : null,
        };
        var legacy = new OperationBoundRecordingAdapter();
        var executor = ResolvedExecutor(store, managed, ReadyResolver(session), legacy);

        if (mode == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(snapshot.OperationId));
        else await executor.ExecuteAsync(snapshot.OperationId);

        Assert.True(store.ResultPending);
        Assert.Equal(SubmissionAcceptance.Unknown, store.PendingAcceptance);
        Assert.Equal(1, managed.SaveCalls);
        Assert.Equal(0, legacy.SaveCalls);
        Assert.DoesNotContain("private", store.FailureMessage!);
    }

    [Fact]
    public async Task ManagedPostSubmissionLoginPauseHasNoFabricatedCookieTimestamp()
    {
        var (snapshot, session) = ManagedSnapshot();
        var store = new RecordingExecutionStore(snapshot);
        var managed = new ManagedRecordingAdapter
        {
            SaveResult = BaiduAdapterResult<TransferReceipt>.Failure(BaiduErrorKind.ExpiredLogin, null,
                submissionAcceptance: SubmissionAcceptance.Unknown),
        };

        await ResolvedExecutor(store, managed, ReadyResolver(session)).ExecuteAsync(snapshot.OperationId);

        Assert.True(store.AccountPaused);
        Assert.Equal(SubmissionAcceptance.Unknown, store.PausedAcceptance);
        Assert.Null(store.ObservedSessionCapturedAtUtc);
    }

    private static (OperationExecutionSnapshot Snapshot, ManagedBrowserSessionReference Session) ManagedSnapshot(BrowserFamily family = BrowserFamily.Chrome)
    {
        var snapshot = Snapshot("outer");
        var session = new ManagedBrowserSessionReference(Guid.NewGuid(), Guid.NewGuid(), snapshot.AccountId,
            "baidu-user", family, "managed-profile", "ownership");
        return (snapshot with
        {
            BatchId = Guid.NewGuid(), AttemptCount = 4, AttemptGenerationPrepared = true, IsAutomationRecovery = true,
            RequiresExactShareEvidence = true,
            PersistedExecutionProfile = ExecutionProfileSnapshot.Managed(family, session.ProfileId, session.InstallationId, session.SessionId),
        }, session);
    }

    private static FixedExecutionResolver ReadyResolver(ManagedBrowserSessionReference session) =>
        new(BaiduExecutionResolution.Ready(BaiduExecutionContext.Managed(session)));

    private static OperationExecutor ResolvedExecutor(RecordingExecutionStore store, ManagedRecordingAdapter managed,
        IBaiduExecutionContextResolver resolver, OperationBoundRecordingAdapter? legacy = null) =>
        new(new ExecutionBoundTransferAdapter(legacy ?? new OperationBoundRecordingAdapter(), managed), resolver, store,
            new AccountDailyLimitService(store, new FixedTimeProvider(Now)), RetryPolicy.Default,
            new RecordingDelay(), new FixedTimeProvider(Now));

    private sealed class FixedExecutionResolver(BaiduExecutionResolution result) : IBaiduExecutionContextResolver
    {
        public List<string> Events { get; } = [];
        public ExecutionProfileSnapshot? Profile { get; private set; }
        public Guid AccountId { get; private set; }
        public CancellationToken Token { get; private set; }
        public Action? BeforeReturn { get; init; }
        public List<string>? Timeline { get; init; }
        public Task<BaiduExecutionResolution> ResolveAsync(Guid accountId, ExecutionProfileSnapshot profile, CancellationToken cancellationToken)
        {
            Events.Add("Resolve"); Timeline?.Add("Resolve"); AccountId = accountId; Profile = profile; Token = cancellationToken;
            BeforeReturn?.Invoke();
            return Task.FromResult(result);
        }
    }

    private sealed class FixedBindingStore(BaiduExecutionBindingSnapshot binding) : IBaiduExecutionBindingStore
    {
        public ExecutionProfileSnapshot? Profile { get; private set; }
        public Task<BaiduExecutionBindingSnapshot> LoadAsync(Guid accountId, ExecutionProfileSnapshot profile, CancellationToken cancellationToken)
        {
            Profile = profile;
            return Task.FromResult(binding);
        }
    }

    private sealed class ForbiddenLegacySessionStore : IBaiduSessionStore
    {
        public int LoadCalls { get; private set; }
        public Task<AccountSession?> LoadAsync(string accountId, CancellationToken cancellationToken)
        {
            LoadCalls++;
            throw new InvalidOperationException("Managed execution must not read legacy cookies.");
        }
        public Task SaveAsync(AccountSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveAsync(string accountId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ManagedRecordingAdapter(List<string>? timeline = null) : IManagedChromiumTransferAdapter
    {
        public List<TransferOperationAutomationContext> Contexts { get; } = [];
        public List<ManagedBrowserSessionReference> Sessions { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public RemoteDirectory? Directory { get; private set; }
        public RemoteDirectory? SavedDestination { get; private set; }
        public BaiduErrorKind? DirectoryError { get; init; }
        public TransferDirectoryPreparationOutcome? DirectoryOutcome { get; init; }
        public BaiduErrorKind? PreparationError { get; init; }
        public BaiduAdapterResult<TransferPreparationReceipt>? PreparationResult { get; init; }
        public Exception? SaveException { get; init; }
        public int SaveCalls { get; private set; }
        public BaiduAdapterResult<TransferReceipt> SaveResult { get; init; } =
            BaiduAdapterResult<TransferReceipt>.Success(TransferReceipt.WithExactEvidence(Now, "request",
                new RemoteItemIdentitySet([new RemoteItemIdentity("exact-file", "file.apk")])));

        private void Record(string stage, TransferOperationAutomationContext operation, ManagedBrowserSessionReference session, CancellationToken token)
        {
            Contexts.Add(operation); Sessions.Add(session); Tokens.Add(token); timeline?.Add(stage);
        }
        public Task<BaiduAdapterResult<TransferDirectoryPreparationOutcome>> EnsureDirectoryForOperationAsync(
            TransferOperationAutomationContext operation, ManagedBrowserSessionReference session, RemotePath path, CancellationToken token)
        {
            Record("Ensure", operation, session, token);
            Directory = new RemoteDirectory("directory-id", path);
            return Task.FromResult(DirectoryError is { } error
                ? BaiduAdapterResult<TransferDirectoryPreparationOutcome>.Failure(error, null)
                : BaiduAdapterResult<TransferDirectoryPreparationOutcome>.Success(
                    DirectoryOutcome ?? TransferDirectoryPreparationOutcome.Prepared(Directory)));
        }
        public Task<BaiduAdapterResult<TransferPreparationReceipt>> PrepareWholeShareForOperationAsync(
            TransferOperationAutomationContext operation, ManagedBrowserSessionReference session, ShareReference share,
            RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken token)
        {
            Record("Prepare", operation, session, token);
            return Task.FromResult(PreparationResult ?? (PreparationError is { } error
                ? BaiduAdapterResult<TransferPreparationReceipt>.Failure(error, null,
                    submissionAcceptance: SubmissionAcceptance.DefinitelyNotAccepted)
                : BaiduAdapterResult<TransferPreparationReceipt>.Success(TransferPreparationReceipt.Ready)));
        }
        public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareForOperationAsync(
            TransferOperationAutomationContext operation, ManagedBrowserSessionReference session, ShareReference share,
            RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken token)
        {
            Record("Save", operation, session, token); SaveCalls++; SavedDestination = destination;
            if (SaveException is not null) throw SaveException;
            return Task.FromResult(SaveResult);
        }
        public Task<BaiduAdapterResult<TransferConfirmationResult>> ConfirmExactTransferForOperationAsync(
            TransferOperationAutomationContext operation, ManagedBrowserSessionReference session, TransferConfirmationQuery query,
            CancellationToken token) => throw new NotSupportedException();
    }
}
