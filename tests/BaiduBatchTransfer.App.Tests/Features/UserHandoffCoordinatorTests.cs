using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class UserHandoffCoordinatorTests
{
    [Fact]
    public async Task TransferFlowPersistsCompleteBaselineAndAdvancesExactStagesWithCas()
    {
        var fixture = new HandoffFixture(WebAutomationAttemptState.Preparing);
        var baseline = WebAutomationBaseline.Create(["remote-b", "remote-a"]);

        await fixture.Coordinator.PersistBaselineAsync(
            fixture.Identity, baseline, fixture.Fingerprint);
        await fixture.Coordinator.AdvanceStageAsync(
            fixture.Identity,
            WebAutomationAttemptState.BaselinePersisted,
            WebAutomationAttemptState.Submitting,
            new string('b', 64));
        await fixture.Coordinator.AdvanceStageAsync(
            fixture.Identity,
            WebAutomationAttemptState.Submitting,
            WebAutomationAttemptState.ResultPending,
            new string('c', 64));

        Assert.Equal(WebAutomationAttemptState.ResultPending, fixture.Store.Current.State);
        Assert.Equal(["remote-a", "remote-b"],
            fixture.Store.Current.Baseline!.Items.Select(item => item.ExactId));
        Assert.Equal(new string('c', 64), fixture.Store.Current.ObservationFingerprint);
    }

    [Fact]
    public async Task Resume_RestoresExactPersistentStageOnce()
    {
        var fixture = new HandoffFixture(WebAutomationAttemptState.BaselinePersisted);
        var handoff = await fixture.Coordinator.EnterAsync(
            fixture.Identity,
            WebAutomationAttemptState.BaselinePersisted,
            fixture.Fingerprint);

        var first = await fixture.Coordinator.ResolveAsync(
            fixture.Identity,
            handoff,
            WebAutomationUserChoice.Resume);
        var duplicate = await fixture.Coordinator.ResolveAsync(
            fixture.Identity,
            handoff,
            WebAutomationUserChoice.Resume);

        Assert.Equal(UserHandoffResolution.Resumed, first.Resolution);
        Assert.Equal(UserHandoffResolution.Stale, duplicate.Resolution);
        Assert.Equal(WebAutomationAttemptState.BaselinePersisted, fixture.Store.Current.State);
        Assert.False(fixture.Store.Current.IsWaitingForUser);
    }

    [Theory]
    [InlineData(WebAutomationAttemptState.Preparing, WebAutomationAttemptState.DefinitelyNotSubmitted)]
    [InlineData(WebAutomationAttemptState.BaselinePersisted, WebAutomationAttemptState.DefinitelyNotSubmitted)]
    [InlineData(WebAutomationAttemptState.Submitting, WebAutomationAttemptState.NeedsDecision)]
    [InlineData(WebAutomationAttemptState.ResultPending, WebAutomationAttemptState.NeedsDecision)]
    public async Task Skip_PersistsOnlySafeTerminalState(
        WebAutomationAttemptState resumeStage,
        WebAutomationAttemptState expected)
    {
        var fixture = new HandoffFixture(resumeStage);
        var handoff = await fixture.Coordinator.EnterAsync(fixture.Identity, resumeStage, fixture.Fingerprint);

        var result = await fixture.Coordinator.ResolveAsync(
            fixture.Identity,
            handoff,
            WebAutomationUserChoice.Skip);

        Assert.Equal(UserHandoffResolution.Skipped, result.Resolution);
        Assert.Equal(expected, result.PersistentState);
        Assert.Equal(expected, fixture.Store.Current.State);
        Assert.False(fixture.Store.Current.IsWaitingForUser);
    }

    [Fact]
    public async Task Cancel_PersistsSafeStateBeforeCancellingOnlyTheBoundBatch()
    {
        var fixture = new HandoffFixture(WebAutomationAttemptState.Submitting);
        var handoff = await fixture.Coordinator.EnterAsync(
            fixture.Identity,
            WebAutomationAttemptState.Submitting,
            fixture.Fingerprint);

        var result = await fixture.Coordinator.ResolveAsync(
            fixture.Identity,
            handoff,
            WebAutomationUserChoice.CancelBatch);

        Assert.Equal(UserHandoffResolution.BatchCancelled, result.Resolution);
        Assert.Equal(WebAutomationAttemptState.NeedsDecision, fixture.Store.Current.State);
        Assert.Equal([fixture.Identity], fixture.Cancellation.Cancelled);
        Assert.True(fixture.Cancellation.StateWasPersistentWhenCalled);
    }

    [Fact]
    public async Task CancelPersistenceFailureLeavesHandoffPending()
    {
        var fixture = new HandoffFixture(WebAutomationAttemptState.Preparing);
        var handoff = await fixture.Coordinator.EnterAsync(
            fixture.Identity,
            WebAutomationAttemptState.Preparing,
            fixture.Fingerprint);
        fixture.Cancellation.ThrowOnCancel = true;

        await Assert.ThrowsAsync<IOException>(() => fixture.Coordinator.ResolveAsync(
            fixture.Identity,
            handoff,
            WebAutomationUserChoice.CancelBatch));

        Assert.Equal(WebAutomationAttemptState.UserHandoff, fixture.Store.Current.State);
        Assert.True(fixture.Store.Current.IsWaitingForUser);
    }

    [Fact]
    public async Task CancelUsesThePersistedStateReturnedByDurableCancellation()
    {
        var fixture = new HandoffFixture(WebAutomationAttemptState.Preparing);
        var handoff = await fixture.Coordinator.EnterAsync(
            fixture.Identity,
            WebAutomationAttemptState.Preparing,
            fixture.Fingerprint);
        fixture.Cancellation.PersistentStateOverride = WebAutomationAttemptState.NeedsDecision;

        var result = await fixture.Coordinator.ResolveAsync(
            fixture.Identity,
            handoff,
            WebAutomationUserChoice.CancelBatch);

        Assert.Equal(UserHandoffResolution.BatchCancelled, result.Resolution);
        Assert.Equal(WebAutomationAttemptState.NeedsDecision, result.PersistentState);
    }

    [Fact]
    public async Task DuplicateCancelCannotReuseAResolvedHandoff()
    {
        var fixture = new HandoffFixture(WebAutomationAttemptState.Preparing);
        var handoff = await fixture.Coordinator.EnterAsync(
            fixture.Identity,
            WebAutomationAttemptState.Preparing,
            fixture.Fingerprint);

        var first = await fixture.Coordinator.ResolveAsync(
            fixture.Identity, handoff, WebAutomationUserChoice.CancelBatch);
        var duplicate = await fixture.Coordinator.ResolveAsync(
            fixture.Identity, handoff, WebAutomationUserChoice.CancelBatch);

        Assert.Equal(UserHandoffResolution.BatchCancelled, first.Resolution);
        Assert.Equal(UserHandoffResolution.Stale, duplicate.Resolution);
        Assert.Single(fixture.Cancellation.Cancelled);
    }

    [Fact]
    public async Task WrongOperationAccountOrGenerationCannotResumeHandoff()
    {
        var fixture = new HandoffFixture(WebAutomationAttemptState.Preparing);
        var handoff = await fixture.Coordinator.EnterAsync(
            fixture.Identity,
            WebAutomationAttemptState.Preparing,
            fixture.Fingerprint);

        foreach (var wrong in new[]
                 {
                     fixture.Identity with { OperationId = Guid.NewGuid() },
                     fixture.Identity with { AccountId = Guid.NewGuid() },
                     fixture.Identity with { Generation = fixture.Identity.Generation + 1 },
                 })
        {
            var result = await fixture.Coordinator.ResolveAsync(
                wrong,
                handoff,
                WebAutomationUserChoice.Resume);
            Assert.Equal(UserHandoffResolution.Stale, result.Resolution);
        }

        Assert.Equal(WebAutomationAttemptState.UserHandoff, fixture.Store.Current.State);
    }

    private sealed class HandoffFixture
    {
        public HandoffFixture(WebAutomationAttemptState state)
        {
            Identity = new(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                1,
                WebAutomationOwnerKind.Transfer);
            var now = DateTimeOffset.UtcNow;
            var baseline = state is WebAutomationAttemptState.Preparing
                ? null
                : WebAutomationBaseline.Create(["remote-id"]);
            Store = new InMemoryAttemptStore(new(
                Identity.OperationId,
                Identity.AccountId,
                Identity.Generation,
                Identity.OwnerKind,
                state,
                null,
                baseline,
                Fingerprint,
                false,
                2,
                now.AddMinutes(-1),
                now));
            Cancellation = new RecordingBatchCancellation(Store);
            Coordinator = new UserHandoffCoordinator(Store, Cancellation, TimeProvider.System);
        }

        public string Fingerprint { get; } = new('a', 64);
        public WebAutomationRequestIdentity Identity { get; }
        public InMemoryAttemptStore Store { get; }
        public RecordingBatchCancellation Cancellation { get; }
        public UserHandoffCoordinator Coordinator { get; }
    }

    private sealed class RecordingBatchCancellation(InMemoryAttemptStore store)
        : IWebAutomationBatchCancellation
    {
        public List<WebAutomationRequestIdentity> Cancelled { get; } = [];
        public bool StateWasPersistentWhenCalled { get; private set; }
        public bool ThrowOnCancel { get; set; }
        public WebAutomationAttemptState? PersistentStateOverride { get; set; }

        public Task<WebAutomationBatchCancellationResult> CancelAsync(
            WebAutomationRequestIdentity identity,
            long expectedAttemptRevision,
            WebAutomationAttemptState expectedResumeStage,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnCancel) throw new IOException("synthetic durable cancellation failure");
            if (!store.ResolveBatchCancellation(identity, expectedAttemptRevision, expectedResumeStage))
                return Task.FromResult(new WebAutomationBatchCancellationResult(
                    false,
                    WebAutomationAttemptState.UserHandoff));
            if (PersistentStateOverride is { } forced) store.OverrideTerminalState(forced);
            StateWasPersistentWhenCalled = store.Current.State is
                WebAutomationAttemptState.DefinitelyNotSubmitted or WebAutomationAttemptState.NeedsDecision;
            Cancelled.Add(identity);
            return Task.FromResult(new WebAutomationBatchCancellationResult(true, store.Current.State));
        }

        public Task<bool> IsCancelledAsync(
            Guid batchId,
            WebAutomationOwnerKind ownerKind,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public IDisposable Register(
            Guid batchId,
            WebAutomationOwnerKind ownerKind,
            Action cancellationRequested) => new NoopDisposable();

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }

    internal sealed class InMemoryAttemptStore(WebAutomationAttemptRecord initial)
        : IWebAutomationAttemptStore
    {
        private readonly object gate = new();
        public WebAutomationAttemptRecord Current { get; private set; } = initial;

        public bool ResolveBatchCancellation(
            WebAutomationRequestIdentity identity,
            long expectedAttemptRevision,
            WebAutomationAttemptState expectedResumeStage)
        {
            lock (gate)
            {
                if (!Matches(identity.OperationId, identity.AccountId, identity.Generation) ||
                    Current.OwnerKind != identity.OwnerKind ||
                    Current.State != WebAutomationAttemptState.UserHandoff ||
                    Current.ResumeStage != expectedResumeStage ||
                    Current.Revision != expectedAttemptRevision)
                {
                    return false;
                }
                var terminal = expectedResumeStage is WebAutomationAttemptState.Preparing or WebAutomationAttemptState.BaselinePersisted
                    ? WebAutomationAttemptState.DefinitelyNotSubmitted
                    : WebAutomationAttemptState.NeedsDecision;
                Current = new(
                    Current.OperationId, Current.AccountId, Current.Generation, Current.OwnerKind,
                    terminal, null, Current.Baseline, Current.ObservationFingerprint, false,
                    Current.Revision + 1, Current.CreatedAt, DateTimeOffset.UtcNow);
                return true;
            }
        }

        public void OverrideTerminalState(WebAutomationAttemptState state)
        {
            lock (gate)
            {
                Current = new(
                    Current.OperationId, Current.AccountId, Current.Generation, Current.OwnerKind,
                    state, null, Current.Baseline, Current.ObservationFingerprint, false,
                    Current.Revision, Current.CreatedAt, DateTimeOffset.UtcNow);
            }
        }

        public Task<bool> BeginAsync(WebAutomationAttemptRecord attempt, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<WebAutomationAttemptRecord?> GetAsync(Guid operationId, Guid accountId, int generation, CancellationToken cancellationToken = default)
        {
            lock (gate)
                return Task.FromResult<WebAutomationAttemptRecord?>(Matches(operationId, accountId, generation) ? Current : null);
        }

        public Task<Page<WebAutomationAttemptRecord>> GetRecoveryPageAsync(PageQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Page<WebAutomationAttemptRecord>([Current], 1, query.PageNumber, query.PageSize));

        public Task<bool> TryPersistBaselineAsync(Guid operationId, Guid accountId, int generation, long expectedRevision, WebAutomationBaseline baseline, string observationFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                if (!Matches(operationId, accountId, generation) ||
                    Current.State != WebAutomationAttemptState.Preparing ||
                    Current.Revision != expectedRevision)
                    return Task.FromResult(false);
                Current = new(
                    operationId, accountId, generation, Current.OwnerKind,
                    WebAutomationAttemptState.BaselinePersisted, null, baseline,
                    observationFingerprint, false, Current.Revision + 1,
                    Current.CreatedAt, updatedAt);
                return Task.FromResult(true);
            }
        }

        public Task<bool> TryAdvanceAsync(Guid operationId, Guid accountId, int generation, WebAutomationAttemptState expectedState, long expectedRevision, WebAutomationAttemptState nextState, string? observationFingerprint, bool isWaitingForUser, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                if (!Matches(operationId, accountId, generation) || Current.State != expectedState || Current.Revision != expectedRevision)
                    return Task.FromResult(false);
                Current = new(
                    operationId, accountId, generation, Current.OwnerKind, nextState,
                    nextState == WebAutomationAttemptState.UserHandoff ? expectedState : null,
                    Current.Baseline, observationFingerprint ?? Current.ObservationFingerprint,
                    isWaitingForUser, Current.Revision + 1, Current.CreatedAt, updatedAt);
                return Task.FromResult(true);
            }
        }

        public Task<bool> TryResumeFromUserHandoffAsync(Guid operationId, Guid accountId, int generation, long expectedRevision, WebAutomationAttemptState resumeStage, string? observationFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                if (!Matches(operationId, accountId, generation) || Current.State != WebAutomationAttemptState.UserHandoff ||
                    Current.ResumeStage != resumeStage || Current.Revision != expectedRevision)
                    return Task.FromResult(false);
                Current = new(
                    operationId, accountId, generation, Current.OwnerKind, resumeStage, null,
                    Current.Baseline, observationFingerprint ?? Current.ObservationFingerprint,
                    false, Current.Revision + 1, Current.CreatedAt, updatedAt);
                return Task.FromResult(true);
            }
        }

        public Task<bool> TryResolveUserHandoffAsync(Guid operationId, Guid accountId, int generation, long expectedRevision, WebAutomationAttemptState resumeStage, WebAutomationAttemptState terminalState, string? observationFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                if (!Matches(operationId, accountId, generation) || Current.State != WebAutomationAttemptState.UserHandoff ||
                    Current.ResumeStage != resumeStage || Current.Revision != expectedRevision)
                    return Task.FromResult(false);
                Current = new(
                    operationId, accountId, generation, Current.OwnerKind, terminalState, null,
                    Current.Baseline, observationFingerprint ?? Current.ObservationFingerprint,
                    false, Current.Revision + 1, Current.CreatedAt, updatedAt);
                return Task.FromResult(true);
            }
        }

        private bool Matches(Guid operationId, Guid accountId, int generation) =>
            Current.OperationId == operationId && Current.AccountId == accountId && Current.Generation == generation;
    }
}
