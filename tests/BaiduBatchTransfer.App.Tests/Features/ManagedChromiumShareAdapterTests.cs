using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Security;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedChromiumShareAdapterTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DiscoveryUsesAReadOnlyExactSessionAndReturnsItsCompleteSnapshot()
    {
        var fixture = new Fixture(WebAutomationAttemptState.Preparing);

        var result = await fixture.Adapter().DiscoverPermanentSharesForOperationAsync(
            fixture.Context, fixture.ManagedSession, fixture.Request, default);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.AllItems.Count);
        Assert.Single(result.Value.ExactMatches);
        Assert.True(fixture.Factory.LastRecoveryOnly);
        Assert.Equal(0, fixture.Factory.LastSession!.CreateCalls);
    }

    [Fact]
    public async Task MultiObjectTransferLinkedShareUsesTheCompleteExactSet()
    {
        var fixture = new Fixture(WebAutomationAttemptState.Preparing, multiple: true);

        var result = await fixture.Adapter().DiscoverPermanentSharesForOperationAsync(
            fixture.Context, fixture.ManagedSession, fixture.Request, default);

        Assert.True(result.IsSuccess);
        var exact = Assert.Single(result.Value!.ExactMatches);
        Assert.Equal(fixture.Items, exact.RemoteItems);
        Assert.Equal(2, exact.RemoteItems.Count);
    }

    [Fact]
    public async Task ResultPendingConfirmationOnlyReadsAndAdvancesTheExactAttempt()
    {
        var fixture = new Fixture(WebAutomationAttemptState.ResultPending);
        fixture.Acceptance.Value = fixture.Accepted;

        var result = await fixture.Adapter().ConfirmExactShareForOperationAsync(
            fixture.Context with { IsRecovery = true }, fixture.ManagedSession, fixture.Request, default);

        Assert.True(result.IsSuccess);
        Assert.Equal(WebAutomationAttemptState.Succeeded, fixture.Attempts.Current.State);
        Assert.True(fixture.Factory.LastRecoveryOnly);
        Assert.Equal(1, fixture.Factory.LastSession!.ListingReads);
        Assert.Equal(0, fixture.Factory.LastSession.CreateCalls);
        Assert.Equal(0, fixture.Factory.LastSession.PersistBaselineCalls);
        Assert.Equal(0, fixture.Factory.LastSession.MarkSubmittingCalls);
    }

    [Fact]
    public async Task UnknownSubmittingRecoveryUsesOnlyReadOnlySessionsAndNeverClicksAgain()
    {
        var fixture = new Fixture(WebAutomationAttemptState.Submitting);

        var result = await fixture.Adapter().ConfirmExactShareForOperationAsync(
            fixture.Context with { IsRecovery = true }, fixture.ManagedSession, fixture.Request, default);

        Assert.True(result.IsSuccess);
        Assert.Equal(WebAutomationAttemptState.Succeeded, fixture.Attempts.Current.State);
        Assert.Equal(2, fixture.Factory.CreateCalls);
        Assert.All(fixture.Factory.Created, session =>
        {
            Assert.Equal(0, session.CreateCalls);
            Assert.Equal(0, session.PersistBaselineCalls);
            Assert.Equal(0, session.MarkSubmittingCalls);
        });
    }

    [Fact]
    public async Task StandaloneOrItemDriftIsRejectedBeforeOpeningAnySession()
    {
        var fixture = new Fixture(WebAutomationAttemptState.Preparing);
        var sut = fixture.Adapter();
        var drift = new PermanentShareRequest(new RemoteItemIdentitySet([new("wrong-exact", null)]));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.PreparePermanentShareForOperationAsync(
                fixture.Context with { OwnerKind = WebAutomationOwnerKind.StandaloneShare },
                fixture.ManagedSession, fixture.Request, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.PreparePermanentShareForOperationAsync(
                fixture.Context, fixture.ManagedSession, drift, default));

        Assert.Equal(0, fixture.Factory.CreateCalls);
    }

    [Fact]
    public async Task ConcreteFactoryFailsClosedBeforeLifecycleHandleOrHelperWhenHandoffIsMissing()
    {
        var fixture = new Fixture(WebAutomationAttemptState.Preparing);
        var probes = 0;
        var factory = new ManagedShareAutomationSessionFactory(
            new ManagedTransferShareHandoffRegistry(),
            (_, _) => { probes++; return Task.FromResult<ManagedBrowserLifecycleSession?>(null); },
            _ => { probes++; return null; },
            _ => { probes++; return false; },
            (_, _) => { probes++; throw new InvalidOperationException("helper must not start"); },
            fixture.Attempts,
            fixture.Acceptance);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await factory.CreateAsync(
                fixture.Context, fixture.ManagedSession, fixture.Request,
                recoveryOnly: true, null, null, default));

        Assert.Equal(0, probes);
    }

    [Fact]
    public async Task ConcreteFactoryRetainsExactHandoffAcrossSessionLeaseAndRemovesOnlyOnRelease()
    {
        var fixture = new Fixture(WebAutomationAttemptState.Preparing);
        var installation = new BrowserInstallationIdentity(
            fixture.ManagedSession.InstallationId, BrowserFamily.Chrome,
            @"C:\SyntheticBrowser", @"C:\SyntheticBrowser\chrome.exe",
            "Google LLC", "Google Chrome", "synthetic-file");
        var profile = ManagedBrowserProfileDescriptor.FromStoredSession(
            fixture.ManagedSession, @"C:\SyntheticAppData");
        var handle = new ManagedChromiumSessionHandle(
            31415, installation, profile,
            new DevToolsActivePort(9222, Guid.NewGuid(), new Uri("http://127.0.0.1:9222/"),
                new Uri("ws://127.0.0.1:9222/devtools/browser/synthetic")),
            "fixed-tab");
        var lifecycle = new ManagedBrowserLifecycleSession(
            fixture.ManagedSession, 11, 13, profile.CanonicalSourcePath);
        var registry = new ManagedTransferShareHandoffRegistry();
        var key = new ManagedTransferShareHandoffKey(
            fixture.Context.BatchId, fixture.Context.AccountId, fixture.Items.Fingerprint);
        Assert.True(registry.TryRegister(new ManagedTransferShareHandoff(
            key, Guid.NewGuid(), 1, fixture.ManagedSession, lifecycle, handle,
            new RemoteDirectory("directory-1", new RemotePath("/prepared")), fixture.Items)));
        var connection = new FakeConnection();
        var factory = new ManagedShareAutomationSessionFactory(
            registry,
            (_, _) => Task.FromResult<ManagedBrowserLifecycleSession?>(lifecycle),
            _ => handle,
            candidate => ReferenceEquals(candidate, handle),
            (_, _) => ValueTask.FromResult(new ManagedBrowserOperationHelper(connection, () => { })),
            fixture.Attempts,
            fixture.Acceptance);

        await using (await factory.CreateAsync(
            fixture.Context, fixture.ManagedSession, fixture.Request,
            recoveryOnly: true, null, null, default))
        {
            Assert.True(registry.Contains(key));
        }
        Assert.True(connection.Disposed);
        Assert.True(registry.Contains(key));

        factory.ReleaseExactHandoff(fixture.Context, fixture.ManagedSession, fixture.Request);

        Assert.False(registry.Contains(key));
    }

    private sealed class Fixture
    {
        internal Fixture(WebAutomationAttemptState state, bool multiple = false)
        {
            Items = new RemoteItemIdentitySet(multiple
                ? [new("destination-exact-id-1", "one"), new("destination-exact-id-2", "two")]
                : [new("destination-exact-id", "application.apk")]);
            Request = new PermanentShareRequest(Items, "a1b2");
            ManagedSession = new ManagedBrowserSessionReference(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "uid-1", BrowserFamily.Chrome,
                Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            Profile = ExecutionProfileSnapshot.Managed(
                ManagedSession.BrowserFamily, ManagedSession.ProfileId,
                ManagedSession.InstallationId, ManagedSession.SessionId);
            Context = new ShareOperationAutomationContext(
                Guid.NewGuid(), Guid.NewGuid(), ManagedSession.AccountId, 1,
                WebAutomationOwnerKind.TransferShare, Items, PersistedExecutionProfile: Profile);
            Baseline = WebAutomationBaseline.Create(["old-share"]);
            var settings = new EffectiveShareSettings(
                true, true, ShareExtractionCodeBehavior.Fixed, "a1b2");
            Accepted = ShareOfficialAcceptance.Accepted(
                "request-1", "new-share", "https://pan.baidu.com/s/new-share",
                Items, settings, Fingerprint('b'));
            Listing = new ShareOfficialListing(true, true,
            [
                new ShareOfficialListEntry(
                    "old-share", "https://pan.baidu.com/s/old-share",
                    new RemoteItemIdentitySet([new("old-item", null)]), settings),
                new ShareOfficialListEntry(
                    "new-share", "https://pan.baidu.com/s/new-share", Items, settings)
            ], Fingerprint('c'));
            Attempts = new MemoryAttemptStore(new WebAutomationAttemptRecord(
                Context.OperationId, Context.AccountId, Context.Generation,
                WebAutomationOwnerKind.TransferShare, state, null,
                state == WebAutomationAttemptState.Preparing ? null : Baseline,
                state == WebAutomationAttemptState.Preparing ? null : Fingerprint('a'),
                false, 0, Now, Now,
                state is WebAutomationAttemptState.ResultPending or WebAutomationAttemptState.Succeeded
                    ? Items : null,
                executionProfile: Profile));
            Acceptance = new AcceptanceStore(Attempts);
            Factory = new SessionFactory(Listing);
        }

        internal RemoteItemIdentitySet Items { get; }
        internal PermanentShareRequest Request { get; }
        internal ManagedBrowserSessionReference ManagedSession { get; }
        internal ExecutionProfileSnapshot Profile { get; }
        internal ShareOperationAutomationContext Context { get; }
        internal WebAutomationBaseline Baseline { get; }
        internal ShareOfficialAcceptance Accepted { get; }
        internal ShareOfficialListing Listing { get; }
        internal MemoryAttemptStore Attempts { get; }
        internal AcceptanceStore Acceptance { get; }
        internal SessionFactory Factory { get; }
        internal ManagedChromiumShareAdapter Adapter() =>
            new(Factory, Attempts, Acceptance, new ShareAutomationFlow(new FixedTimeProvider(Now)),
                new FixedTimeProvider(Now));
    }

    private sealed class SessionFactory(ShareOfficialListing listing) : IManagedShareAutomationSessionFactory
    {
        internal int CreateCalls { get; private set; }
        internal bool LastRecoveryOnly { get; private set; }
        internal FakeSession? LastSession { get; private set; }
        internal List<FakeSession> Created { get; } = [];
        public ValueTask<IManagedShareAutomationSessionLease> CreateAsync(
            ShareOperationAutomationContext context, ManagedBrowserSessionReference session,
            PermanentShareRequest request, bool recoveryOnly, WebAutomationBaseline? persistedBaseline,
            string? baselineObservationFingerprint, CancellationToken cancellationToken)
        {
            CreateCalls++;
            LastRecoveryOnly = recoveryOnly;
            LastSession = new FakeSession(listing);
            Created.Add(LastSession);
            return ValueTask.FromResult<IManagedShareAutomationSessionLease>(new Lease(LastSession));
        }
        public void ReleaseExactHandoff(
            ShareOperationAutomationContext context, ManagedBrowserSessionReference session,
            PermanentShareRequest request) { }
    }

    private sealed class Lease(IShareAutomationSession session) : IManagedShareAutomationSessionLease
    {
        public IShareAutomationSession Session { get; } = session;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSession(ShareOfficialListing listing) : IShareAutomationSession
    {
        internal int ListingReads { get; private set; }
        internal int PersistBaselineCalls { get; private set; }
        internal int MarkSubmittingCalls { get; private set; }
        internal int CreateCalls { get; private set; }
        public Task SelectExactRemoteItemsAsSingleGroupAsync(RemoteItemIdentitySet remoteItems, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ConfigurePermanentShareAsync(PermanentShareRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ShareOfficialListing> ReadCompleteStableOfficialShareListAsync(CancellationToken cancellationToken)
        { ListingReads++; return Task.FromResult(listing); }
        public Task PersistBaselineAsync(WebAutomationBaseline baseline, string observationFingerprint, CancellationToken cancellationToken)
        { PersistBaselineCalls++; return Task.CompletedTask; }
        public Task MarkSubmittingAsync(CancellationToken cancellationToken)
        { MarkSubmittingCalls++; return Task.CompletedTask; }
        public Task<ShareOfficialAcceptance> CreateShareOnceAsync(CancellationToken cancellationToken)
        { CreateCalls++; throw new InvalidOperationException("Read-only test session cannot submit."); }
        public Task PersistOfficialAcceptanceAsync(ShareOfficialAcceptance acceptance, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Read-only test session cannot persist acceptance.");
    }

    private sealed class AcceptanceStore(MemoryAttemptStore attempts) : IShareAutomationAcceptanceEvidenceStore
    {
        internal ShareOfficialAcceptance? Value { get; set; }
        public Task PersistAsync(ShareOperationAutomationContext context, ShareOfficialAcceptance acceptance, CancellationToken cancellationToken = default)
        { Value = acceptance; return Task.CompletedTask; }
        public Task<ShareOfficialAcceptance?> GetAsync(ShareOperationAutomationContext context, CancellationToken cancellationToken = default) => Task.FromResult(Value);
        public async Task<bool> TryPersistRecoveredAsync(ShareOperationAutomationContext context, ShareOfficialAcceptance acceptance, CancellationToken cancellationToken = default)
        {
            var changed = await attempts.TryPersistOfficialAcceptanceAsync(
                context.OperationId, context.AccountId, context.Generation, attempts.Current.Revision,
                acceptance.ExactRemoteItems!, acceptance.ObservationFingerprint, Now, cancellationToken);
            if (changed) Value = acceptance;
            return changed;
        }
    }

    private sealed class MemoryAttemptStore(WebAutomationAttemptRecord current) : IWebAutomationAttemptStore
    {
        internal WebAutomationAttemptRecord Current { get; private set; } = current;
        public Task<bool> BeginAsync(WebAutomationAttemptRecord attempt, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<WebAutomationAttemptRecord?> GetAsync(Guid operationId, Guid accountId, int generation, CancellationToken cancellationToken = default) => Task.FromResult<WebAutomationAttemptRecord?>(Current);
        public Task<Page<WebAutomationAttemptRecord>> GetRecoveryPageAsync(PageQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryPersistBaselineAsync(Guid operationId, Guid accountId, int generation, long expectedRevision, WebAutomationBaseline baseline, string observationFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryAdvanceAsync(Guid operationId, Guid accountId, int generation, WebAutomationAttemptState expectedState, long expectedRevision, WebAutomationAttemptState nextState, string? observationFingerprint, bool isWaitingForUser, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            if (Current.State != expectedState || Current.Revision != expectedRevision) return Task.FromResult(false);
            Current = New(nextState, Current.AcceptedRemoteItems, observationFingerprint ?? Current.ObservationFingerprint!);
            return Task.FromResult(true);
        }
        public Task<bool> TryPersistOfficialAcceptanceAsync(Guid operationId, Guid accountId, int generation, long expectedRevision, RemoteItemIdentitySet acceptedRemoteItems, string observationFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            if (Current.State != WebAutomationAttemptState.Submitting || Current.Revision != expectedRevision) return Task.FromResult(false);
            Current = New(WebAutomationAttemptState.ResultPending, acceptedRemoteItems, observationFingerprint);
            return Task.FromResult(true);
        }
        public Task<bool> TryResumeFromUserHandoffAsync(Guid operationId, Guid accountId, int generation, long expectedRevision, WebAutomationAttemptState resumeStage, string? observationFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryResolveUserHandoffAsync(Guid operationId, Guid accountId, int generation, long expectedRevision, WebAutomationAttemptState resumeStage, WebAutomationAttemptState terminalState, string? observationFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        private WebAutomationAttemptRecord New(WebAutomationAttemptState state, RemoteItemIdentitySet? accepted, string fingerprint) => new(
            Current.OperationId, Current.AccountId, Current.Generation, Current.OwnerKind, state, null,
            Current.Baseline, fingerprint, false, Current.Revision + 1, Current.CreatedAt, Now,
            accepted, executionProfile: Current.ExecutionProfile);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeConnection : IManagedBrowserHelperConnection
    {
        internal bool Disposed { get; private set; }
        public Task<ManagedBrowserResponse> SendAsync(
            ManagedBrowserRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask DisposeAsync()
        { Disposed = true; return ValueTask.CompletedTask; }
    }

    private static string Fingerprint(char value) => new(value, 64);
}
