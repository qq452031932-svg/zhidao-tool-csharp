using System.Collections.Immutable;
using System.Reflection;
using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class WebViewBaiduShareAdapterTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LegacyCreateFailsClosedWithoutOpeningAutomation()
    {
        var accountId = Guid.NewGuid();
        var broker = new RecordingBroker();
        var adapter = Adapter(accountId, broker);

        var result = await adapter.CreatePermanentShareAsync(
            Session(accountId), Request(), CancellationToken.None);

        Assert.IsAssignableFrom<IOperationBoundBaiduShareAdapter>(adapter);
        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduErrorKind.NotValidated, result.ErrorKind);
        Assert.Equal(SubmissionAcceptance.DefinitelyNotAccepted, result.SubmissionAcceptance);
        Assert.Null(broker.Identity);
    }

    [Theory]
    [InlineData(WebAutomationOwnerKind.TransferShare)]
    [InlineData(WebAutomationOwnerKind.StandaloneShare)]
    public async Task PrepareQueuesExactOwnerAccountProfileAndPersistsBaseline(
        WebAutomationOwnerKind ownerKind)
    {
        var accountId = Guid.NewGuid();
        var context = Context(accountId, ownerKind);
        var attempts = new AttemptStore(Attempt(context, WebAutomationAttemptState.Preparing, null));
        var automation = new ShareSession(Listing(ExistingEntry()));
        var broker = new CallbackBroker(attempts, automation);
        var profiles = new RecordingProfilePolicy(accountId);
        var adapter = Adapter(accountId, broker, attempts, new AcceptanceStore(), profiles);

        var result = await adapter.PreparePermanentShareForOperationAsync(
            context, Session(accountId), Request(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(new WebAutomationRequestIdentity(
            context.OperationId, context.BatchId, accountId, context.Generation, ownerKind), broker.Identity);
        Assert.Equal(profiles.Path, broker.ProfilePath);
        Assert.Equal(WebAutomationAttemptState.BaselinePersisted, attempts.Current.State);
        Assert.Equal(["share-existing"], attempts.Current.Baseline!.Items.Select(item => item.ExactId));
        Assert.Equal(["select-group", "configure", "read-list", "persist-baseline"], automation.Events);
        Assert.DoesNotContain("mark-submitting", automation.Events);
        Assert.DoesNotContain("submit", automation.Events);
    }

    [Fact]
    public async Task MismatchedAccountOrExactItemsIsRejectedBeforeQueueing()
    {
        var accountId = Guid.NewGuid();
        var context = Context(accountId, WebAutomationOwnerKind.StandaloneShare);
        var broker = new RecordingBroker();
        var adapter = Adapter(accountId, broker);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.PreparePermanentShareForOperationAsync(
                context, Session(Guid.NewGuid()), Request(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.PreparePermanentShareForOperationAsync(
                context, Session(accountId), Request("different-item"), CancellationToken.None));

        Assert.Null(broker.Identity);
    }

    [Fact]
    public async Task ProductionVisibleSessionWithoutTypedShareContractFailsClosed()
    {
        var accountId = Guid.NewGuid();
        var context = Context(accountId, WebAutomationOwnerKind.StandaloneShare);
        var attempts = new AttemptStore(Attempt(context, WebAutomationAttemptState.Preparing, null));
        var adapter = Adapter(
            accountId,
            new CallbackBroker(attempts, new VisibleOnlySession()),
            attempts,
            new AcceptanceStore());

        var result = await adapter.PreparePermanentShareForOperationAsync(
            context, Session(accountId), Request(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduErrorKind.NotValidated, result.ErrorKind);
        Assert.Equal(SubmissionAcceptance.DefinitelyNotAccepted, result.SubmissionAcceptance);
        Assert.Equal(WebAutomationAttemptState.Preparing, attempts.Current.State);
    }

    [Fact]
    public async Task SubmitRequiresAnExactBaselinePersistedAttempt()
    {
        var accountId = Guid.NewGuid();
        var context = Context(accountId, WebAutomationOwnerKind.TransferShare);
        var attempts = new AttemptStore(Attempt(context, WebAutomationAttemptState.Preparing, null));
        var broker = new RecordingBroker();
        var adapter = Adapter(accountId, broker, attempts, new AcceptanceStore());

        var result = await adapter.SubmitPermanentShareForOperationAsync(
            context, Session(accountId), Request(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(SubmissionAcceptance.DefinitelyNotAccepted, result.SubmissionAcceptance);
        Assert.Null(broker.Identity);
    }

    [Fact]
    public async Task SubmitPersistsOfficialAcceptanceBeforeAfterListingAndAdvancesToSucceeded()
    {
        var accountId = Guid.NewGuid();
        var context = Context(accountId, WebAutomationOwnerKind.StandaloneShare);
        var baseline = WebAutomationBaseline.Create(["share-existing"]);
        var attempts = new AttemptStore(Attempt(context, WebAutomationAttemptState.BaselinePersisted, baseline));
        var evidence = new AcceptanceStore();
        var automation = new ShareSession(Listing(ExistingEntry(), AcceptedEntry()));
        var adapter = Adapter(
            accountId,
            new CallbackBroker(attempts, automation),
            attempts,
            evidence);

        var result = await adapter.SubmitPermanentShareForOperationAsync(
            context, Session(accountId), Request(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(WebAutomationAttemptState.Succeeded, attempts.Current.State);
        Assert.Equal(Request().RemoteItems, attempts.Current.AcceptedRemoteItems);
        Assert.NotNull(await evidence.GetAsync(context, CancellationToken.None));
        Assert.Equal(
            ["select-group", "configure", "mark-submitting", "submit", "persist-acceptance", "read-list"],
            automation.Events);
    }

    [Fact]
    public async Task AmbiguousSubmitKeepsPersistentResultPendingAndFullAcceptanceEvidence()
    {
        var accountId = Guid.NewGuid();
        var context = Context(accountId, WebAutomationOwnerKind.StandaloneShare);
        var baseline = WebAutomationBaseline.Create(["share-existing"]);
        var attempts = new AttemptStore(Attempt(context, WebAutomationAttemptState.BaselinePersisted, baseline));
        var evidence = new AcceptanceStore();
        var automation = new ShareSession(Listing(ExistingEntry()));
        var adapter = Adapter(
            accountId,
            new CallbackBroker(attempts, automation),
            attempts,
            evidence);

        var result = await adapter.SubmitPermanentShareForOperationAsync(
            context, Session(accountId), Request(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(SubmissionAcceptance.Unknown, result.SubmissionAcceptance);
        Assert.Equal(WebAutomationAttemptState.ResultPending, attempts.Current.State);
        var accepted = await evidence.GetAsync(context, CancellationToken.None);
        Assert.Equal("share-new", accepted!.ShareId);
        Assert.Equal("https://pan.baidu.com/s/share-new", accepted.ShareUrl);
        Assert.Equal("R4nD", accepted.EffectiveSettings!.ExtractionCode);
    }

    [Fact]
    public async Task ConfirmReadsOnlyAndCasAdvancesExactRecoveryToSucceeded()
    {
        var accountId = Guid.NewGuid();
        var context = Context(accountId, WebAutomationOwnerKind.StandaloneShare);
        var baseline = WebAutomationBaseline.Create(["share-existing"]);
        var attempts = new AttemptStore(Attempt(
            context,
            WebAutomationAttemptState.ResultPending,
            baseline,
            Request().RemoteItems));
        var evidence = new AcceptanceStore(Acceptance());
        var automation = new ShareSession(Listing(ExistingEntry(), AcceptedEntry()));
        var adapter = Adapter(
            accountId,
            new CallbackBroker(attempts, automation),
            attempts,
            evidence);

        var result = await adapter.ConfirmExactShareForOperationAsync(
            context, Session(accountId), Request(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(WebAutomationAttemptState.Succeeded, attempts.Current.State);
        Assert.Equal(["read-list"], automation.Events);
        Assert.Equal(0, automation.SubmitCount);
    }

    [Fact]
    public async Task AmbiguousRecoveryStaysResultPendingWithoutClicking()
    {
        var accountId = Guid.NewGuid();
        var context = Context(accountId, WebAutomationOwnerKind.TransferShare);
        var baseline = WebAutomationBaseline.Create(["share-existing"]);
        var attempts = new AttemptStore(Attempt(
            context,
            WebAutomationAttemptState.ResultPending,
            baseline,
            Request().RemoteItems));
        var automation = new ShareSession(Listing(ExistingEntry()));
        var adapter = Adapter(
            accountId,
            new CallbackBroker(attempts, automation),
            attempts,
            new AcceptanceStore(Acceptance()));

        var result = await adapter.ConfirmExactShareForOperationAsync(
            context, Session(accountId), Request(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(SubmissionAcceptance.Unknown, result.SubmissionAcceptance);
        Assert.Equal(WebAutomationAttemptState.ResultPending, attempts.Current.State);
        Assert.Equal(["read-list"], automation.Events);
        Assert.Equal(0, automation.SubmitCount);
    }

    [Theory]
    [InlineData(WebAutomationAttemptState.UserHandoff, WebAutomationAttemptState.Submitting)]
    [InlineData(WebAutomationAttemptState.Submitting, null)]
    public async Task UncertainEffectiveStageFailsClosedWithoutQueueing(
        WebAutomationAttemptState state,
        WebAutomationAttemptState? resumeStage)
    {
        var accountId = Guid.NewGuid();
        var context = Context(accountId, WebAutomationOwnerKind.StandaloneShare);
        var attempts = new AttemptStore(Attempt(
            context,
            state,
            WebAutomationBaseline.Create(["share-existing"]),
            null,
            resumeStage));
        var broker = new RecordingBroker();
        var adapter = Adapter(accountId, broker, attempts, new AcceptanceStore());

        var result = await adapter.ConfirmExactShareForOperationAsync(
            context, Session(accountId), Request(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(SubmissionAcceptance.Unknown, result.SubmissionAcceptance);
        Assert.Null(broker.Identity);
    }

    private static WebViewBaiduShareAdapter Adapter(
        Guid accountId,
        IVisibleWebAutomationBroker broker,
        IWebAutomationAttemptStore? attempts = null,
        IShareAutomationAcceptanceEvidenceStore? evidence = null,
        IAccountProfilePathPolicy? profiles = null) =>
        new(
            new ReadOnlyShareAdapter(),
            broker,
            profiles ?? new RecordingProfilePolicy(accountId),
            new ShareAutomationFlow(new FixedTimeProvider(Now)),
            "build-share-a",
            attempts,
            evidence);

    private static ShareOperationAutomationContext Context(
        Guid accountId,
        WebAutomationOwnerKind ownerKind) =>
        new(Guid.NewGuid(), Guid.NewGuid(), accountId, 3, ownerKind, Request().RemoteItems);

    private static PermanentShareRequest Request(params string[] exactIds) =>
        new(new RemoteItemIdentitySet(
            (exactIds.Length == 0 ? ["remote-a", "remote-b"] : exactIds)
            .Select(id => new RemoteItemIdentity(id, id))));

    private static AccountSession Session(Guid accountId) => new(
        accountId,
        "fictional-user",
        [new SessionCookie("BDUSS", "fictional", ".baidu.com", "/", null, true, true)],
        Now);

    private static ShareOfficialAcceptance Acceptance() => ShareOfficialAcceptance.Accepted(
        "request-1",
        "share-new",
        "https://pan.baidu.com/s/share-new",
        Request().RemoteItems,
        Settings(),
        Fingerprint('d'));

    private static ShareOfficialListEntry ExistingEntry() => new(
        "share-existing",
        "https://pan.baidu.com/s/share-existing",
        new RemoteItemIdentitySet([new("old-item", "旧资源")]),
        new EffectiveShareSettings(true, true, ShareExtractionCodeBehavior.Random, "O1d2"));

    private static ShareOfficialListEntry AcceptedEntry() => new(
        "share-new",
        "https://pan.baidu.com/s/share-new",
        Request().RemoteItems,
        Settings());

    private static EffectiveShareSettings Settings() =>
        new(true, true, ShareExtractionCodeBehavior.Random, "R4nD");

    private static ShareOfficialListing Listing(params ShareOfficialListEntry[] shares) =>
        new(true, true, shares, Fingerprint('b'));

    private static WebAutomationAttemptRecord Attempt(
        ShareOperationAutomationContext context,
        WebAutomationAttemptState state,
        WebAutomationBaseline? baseline,
        RemoteItemIdentitySet? acceptedItems = null,
        WebAutomationAttemptState? resumeStage = null) => new(
            context.OperationId,
            context.AccountId,
            context.Generation,
            context.OwnerKind,
            state,
            resumeStage,
            baseline,
            Fingerprint('a'),
            state == WebAutomationAttemptState.UserHandoff,
            2,
            Now.AddMinutes(-2),
            Now.AddMinutes(-1),
            acceptedItems);

    private sealed class RecordingBroker : IVisibleWebAutomationBroker
    {
        public WebAutomationRequestIdentity? Identity { get; private set; }

        public Task<VisibleWebAutomationResult<T>> EnqueueAsync<T>(
            VisibleWebAutomationRequest<T> request,
            CancellationToken cancellationToken = default)
        {
            Identity = request.Identity;
            throw new InvalidOperationException("This broker only records unexpected queueing.");
        }
    }

    private sealed class CallbackBroker(AttemptStore attempts, IVisibleWebAutomationSession session)
        : IVisibleWebAutomationBroker
    {
        public WebAutomationRequestIdentity? Identity { get; private set; }
        public string? ProfilePath { get; private set; }

        public async Task<VisibleWebAutomationResult<T>> EnqueueAsync<T>(
            VisibleWebAutomationRequest<T> request,
            CancellationToken cancellationToken = default)
        {
            Identity = request.Identity;
            ProfilePath = request.ProfilePath;
            var initialStage = attempts.Current.State;
            if (initialStage == WebAutomationAttemptState.UserHandoff)
            {
                initialStage = attempts.Current.ResumeStage!.Value;
                if (!await attempts.TryResumeFromUserHandoffAsync(
                        request.Identity.OperationId,
                        request.Identity.AccountId,
                        request.Identity.Generation,
                        attempts.Current.Revision,
                        initialStage,
                        attempts.Current.ObservationFingerprint,
                        Now,
                        cancellationToken))
                    throw new InvalidOperationException("Synthetic handoff resume failed.");
            }

            var context = (VisibleWebAutomationContext)Activator.CreateInstance(
                typeof(VisibleWebAutomationContext),
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [
                    request.Identity,
                    session,
                    new InlineDispatcher(),
                    new UserHandoffCoordinator(attempts, new NoBatchCancellation(), new FixedTimeProvider(Now)),
                    initialStage,
                    attempts.Current.ObservationFingerprint,
                ],
                culture: null)!;
            var value = await request.Action(session, context, cancellationToken);
            return new(VisibleWebAutomationDisposition.ActionCompleted, value, context.CurrentStage);
        }
    }

    private sealed class ShareSession(params ShareOfficialListing[] listings)
        : IVisibleWebAutomationSession, IShareAutomationSession
    {
        private readonly Queue<ShareOfficialListing> listings = new(listings);

        public List<string> Events { get; } = [];
        public int SubmitCount { get; private set; }
        public CancellationToken UserClosed => CancellationToken.None;
        public Task ShowAsync(Uri initialUri, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ShowHandoffAsync(WebAutomationHandoffPrompt prompt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<WebAutomationUserChoice> WaitForHandoffChoiceAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task SelectExactRemoteItemsAsSingleGroupAsync(RemoteItemIdentitySet remoteItems, CancellationToken cancellationToken) =>
            Record("select-group");

        public Task ConfigurePermanentShareAsync(PermanentShareRequest request, CancellationToken cancellationToken) =>
            Record("configure");

        public Task<ShareOfficialListing> ReadCompleteStableOfficialShareListAsync(CancellationToken cancellationToken)
        {
            Events.Add("read-list");
            return Task.FromResult(listings.Dequeue());
        }

        public Task PersistBaselineAsync(WebAutomationBaseline baseline, string observationFingerprint, CancellationToken cancellationToken) =>
            Record("persist-baseline");

        public Task MarkSubmittingAsync(CancellationToken cancellationToken) => Record("mark-submitting");

        public Task<ShareOfficialAcceptance> CreateShareOnceAsync(CancellationToken cancellationToken)
        {
            Events.Add("submit");
            SubmitCount++;
            return Task.FromResult(Acceptance());
        }

        public Task PersistOfficialAcceptanceAsync(ShareOfficialAcceptance acceptance, CancellationToken cancellationToken) =>
            Record("persist-acceptance");

        private Task Record(string name)
        {
            Events.Add(name);
            return Task.CompletedTask;
        }
    }

    private sealed class VisibleOnlySession : IVisibleWebAutomationSession
    {
        public CancellationToken UserClosed => CancellationToken.None;
        public Task ShowAsync(Uri initialUri, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ShowHandoffAsync(WebAutomationHandoffPrompt prompt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<WebAutomationUserChoice> WaitForHandoffChoiceAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AcceptanceStore(ShareOfficialAcceptance? initial = null)
        : IShareAutomationAcceptanceEvidenceStore
    {
        private ShareOfficialAcceptance? value = initial;

        public Task PersistAsync(
            ShareOperationAutomationContext context,
            ShareOfficialAcceptance acceptance,
            CancellationToken cancellationToken = default)
        {
            value = acceptance;
            return Task.CompletedTask;
        }

        public Task<ShareOfficialAcceptance?> GetAsync(
            ShareOperationAutomationContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(value);
    }

    private sealed class AttemptStore(WebAutomationAttemptRecord attempt) : IWebAutomationAttemptStore
    {
        public WebAutomationAttemptRecord Current { get; private set; } = attempt;

        public Task<WebAutomationAttemptRecord?> GetAsync(Guid operationId, Guid accountId, int generation, CancellationToken cancellationToken = default) =>
            Task.FromResult<WebAutomationAttemptRecord?>(Matches(operationId, accountId, generation) ? Current : null);

        public Task<bool> TryPersistBaselineAsync(Guid operationId, Guid accountId, int generation, long expectedRevision, WebAutomationBaseline baseline, string observationFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            if (!Matches(operationId, accountId, generation) || Current.State != WebAutomationAttemptState.Preparing || Current.Revision != expectedRevision)
                return Task.FromResult(false);
            Current = New(WebAutomationAttemptState.BaselinePersisted, baseline, null, observationFingerprint, updatedAt);
            return Task.FromResult(true);
        }

        public Task<bool> TryAdvanceAsync(Guid operationId, Guid accountId, int generation, WebAutomationAttemptState expectedState, long expectedRevision, WebAutomationAttemptState nextState, string? observationFingerprint, bool isWaitingForUser, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            if (!Matches(operationId, accountId, generation) || Current.State != expectedState || Current.Revision != expectedRevision)
                return Task.FromResult(false);
            Current = New(nextState, Current.Baseline, Current.AcceptedRemoteItems, observationFingerprint ?? Current.ObservationFingerprint!, updatedAt);
            return Task.FromResult(true);
        }

        public Task<bool> TryPersistOfficialAcceptanceAsync(Guid operationId, Guid accountId, int generation, long expectedRevision, RemoteItemIdentitySet acceptedRemoteItems, string observationFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            if (!Matches(operationId, accountId, generation) || Current.State != WebAutomationAttemptState.Submitting || Current.Revision != expectedRevision)
                return Task.FromResult(false);
            Current = New(WebAutomationAttemptState.ResultPending, Current.Baseline, acceptedRemoteItems, observationFingerprint, updatedAt);
            return Task.FromResult(true);
        }

        public Task<bool> TryResumeFromUserHandoffAsync(Guid operationId, Guid accountId, int generation, long expectedRevision, WebAutomationAttemptState resumeStage, string? observationFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            if (!Matches(operationId, accountId, generation) || Current.State != WebAutomationAttemptState.UserHandoff || Current.ResumeStage != resumeStage || Current.Revision != expectedRevision)
                return Task.FromResult(false);
            Current = New(resumeStage, Current.Baseline, Current.AcceptedRemoteItems, observationFingerprint ?? Current.ObservationFingerprint!, updatedAt);
            return Task.FromResult(true);
        }

        public Task<bool> BeginAsync(WebAutomationAttemptRecord attempt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Page<WebAutomationAttemptRecord>> GetRecoveryPageAsync(PageQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryResolveUserHandoffAsync(Guid operationId, Guid accountId, int generation, long expectedRevision, WebAutomationAttemptState resumeStage, WebAutomationAttemptState terminalState, string? observationFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private bool Matches(Guid operationId, Guid accountId, int generation) =>
            operationId == Current.OperationId && accountId == Current.AccountId && generation == Current.Generation;

        private WebAutomationAttemptRecord New(
            WebAutomationAttemptState state,
            WebAutomationBaseline? baseline,
            RemoteItemIdentitySet? accepted,
            string fingerprint,
            DateTimeOffset updatedAt) => new(
                Current.OperationId,
                Current.AccountId,
                Current.Generation,
                Current.OwnerKind,
                state,
                null,
                baseline,
                fingerprint,
                false,
                checked(Current.Revision + 1),
                Current.CreatedAt,
                updatedAt,
                accepted);
    }

    private sealed class InlineDispatcher : IWebAutomationUiDispatcher
    {
        public Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default) => action();
        public Task InvokeAsync(Func<Task> action, CancellationToken cancellationToken = default) => action();
    }

    private sealed class NoBatchCancellation : IWebAutomationBatchCancellation
    {
        public Task<WebAutomationBatchCancellationResult> CancelAsync(WebAutomationRequestIdentity identity, long expectedAttemptRevision, WebAutomationAttemptState expectedResumeStage, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsCancelledAsync(Guid batchId, WebAutomationOwnerKind ownerKind, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public IDisposable Register(Guid batchId, WebAutomationOwnerKind ownerKind, Action cancellationRequested) => new NoopDisposable();
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }

    private sealed class RecordingProfilePolicy(Guid accountId) : IAccountProfilePathPolicy
    {
        public string Path { get; } = System.IO.Path.GetFullPath(System.IO.Path.Combine("share-profiles", accountId.ToString("D")));
        public string CanonicalRoot => System.IO.Path.GetDirectoryName(Path)!;
        public string GetAccountProfilePath(Guid requested) => requested == accountId ? Path : throw new InvalidOperationException("wrong account");
        public void EnsureSafeAccountPath(Guid requested, string exactProfilePath)
        {
            if (requested != accountId || !string.Equals(exactProfilePath, Path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("unsafe profile");
        }
    }

    private sealed class ReadOnlyShareAdapter : IBaiduShareAdapter
    {
        public Task<BaiduAdapterResult<ShareCreationReceipt>> CreatePermanentShareAsync(AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<BaiduSharePage>> ListSharesAsync(AccountSession session, string? cursor, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private static string Fingerprint(char value) => new(value, 64);
}
