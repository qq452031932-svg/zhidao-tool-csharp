using System.Collections.Immutable;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Tests.Scheduling;

public sealed class ExecutionBoundShareAdapterTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ManagedPortDoesNotInheritCookieAdapter()
    {
        Assert.Empty(typeof(IManagedChromiumShareAdapter).GetInterfaces());
        Assert.Equal(4, typeof(IManagedChromiumShareAdapter).GetMethods().Length);
    }

    [Theory]
    [InlineData(BaiduExecutionEngine.WebView2V1)]
    [InlineData(BaiduExecutionEngine.ManagedChromiumV1)]
    public async Task RoutesEverySharePhaseOnlyToThePersistedExecutionSnapshot(BaiduExecutionEngine engine)
    {
        var accountId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var items = new RemoteItemIdentitySet([
            new("destination-exact-id-1", "application.apk"),
            new("destination-exact-id-2", "assets")]);
        var request = new PermanentShareRequest(items, "a1b2");
        var legacySession = new AccountSession(accountId, "synthetic-uid", Cookies(), Now);
        var managedSession = new ManagedBrowserSessionReference(
            Guid.NewGuid(), Guid.NewGuid(), accountId, "synthetic-uid", BrowserFamily.Chrome,
            "synthetic-profile", "synthetic-ownership");
        var execution = engine == BaiduExecutionEngine.WebView2V1
            ? BaiduExecutionContext.Legacy(legacySession)
            : BaiduExecutionContext.Managed(managedSession);
        var profile = engine == BaiduExecutionEngine.WebView2V1
            ? ExecutionProfileSnapshot.LegacyForAccount(accountId)
            : ExecutionProfileSnapshot.Managed(
                managedSession.BrowserFamily, managedSession.ProfileId,
                managedSession.InstallationId, managedSession.SessionId);
        var context = new ShareOperationAutomationContext(
            operationId, batchId, accountId, 3, WebAutomationOwnerKind.TransferShare,
            items, PersistedExecutionProfile: profile).Validate();
        var legacy = new LegacyAdapter();
        var managed = new ManagedAdapter();
        var sut = new ExecutionBoundShareAdapter(legacy, managed);

        var discovered = await sut.DiscoverPermanentSharesForOperationAsync(
            context, execution, request, RetryPolicy.Default, new NoDelay(), default);
        var prepared = await sut.PreparePermanentShareForOperationAsync(context, execution, request, default);
        var submitted = await sut.SubmitPermanentShareForOperationAsync(context, execution, request, default);
        var confirmed = await sut.ConfirmExactShareForOperationAsync(
            context with { IsRecovery = true }, execution, request, default);

        Assert.True(discovered.IsSuccess);
        Assert.True(prepared.IsSuccess);
        Assert.True(submitted.IsSuccess);
        Assert.True(confirmed.IsSuccess);
        var selected = engine == BaiduExecutionEngine.WebView2V1 ? legacy.Calls : managed.Calls;
        var other = engine == BaiduExecutionEngine.WebView2V1 ? managed.Calls : legacy.Calls;
        Assert.Equal(["discover", "prepare", "submit", "confirm"], selected);
        Assert.Empty(other);
        Assert.Same(request, engine == BaiduExecutionEngine.WebView2V1 ? legacy.LastRequest : managed.LastRequest);
        Assert.Same(engine == BaiduExecutionEngine.WebView2V1 ? legacySession : managedSession,
            engine == BaiduExecutionEngine.WebView2V1 ? legacy.LastSession : managed.LastSession);
    }

    [Theory]
    [InlineData("engine")]
    [InlineData("account")]
    [InlineData("session")]
    [InlineData("installation")]
    [InlineData("profile-case")]
    [InlineData("items")]
    [InlineData("standalone")]
    public async Task RejectsBindingOrExactItemDriftBeforeDispatch(string mismatch)
    {
        var accountId = Guid.NewGuid();
        var session = new ManagedBrowserSessionReference(
            Guid.NewGuid(), Guid.NewGuid(), accountId, "synthetic-uid", BrowserFamily.Chrome,
            "synthetic-profile", "synthetic-ownership");
        var items = new RemoteItemIdentitySet([new("destination-exact-id", null)]);
        var context = new ShareOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 1,
            mismatch == "standalone" ? WebAutomationOwnerKind.StandaloneShare : WebAutomationOwnerKind.TransferShare,
            items, PersistedExecutionProfile: ExecutionProfileSnapshot.Managed(
                session.BrowserFamily, session.ProfileId, session.InstallationId, session.SessionId));
        var execution = mismatch == "engine"
            ? BaiduExecutionContext.Legacy(new AccountSession(accountId, "synthetic-uid", Cookies(), Now))
            : BaiduExecutionContext.Managed(new ManagedBrowserSessionReference(
                mismatch == "session" ? Guid.NewGuid() : session.SessionId,
                mismatch == "installation" ? Guid.NewGuid() : session.InstallationId,
                mismatch == "account" ? Guid.NewGuid() : session.AccountId,
                session.StableBaiduUserId, session.BrowserFamily,
                mismatch == "profile-case" ? session.ProfileId.ToUpperInvariant() : session.ProfileId,
                session.ProfileOwnershipId));
        var request = new PermanentShareRequest(mismatch == "items"
            ? new RemoteItemIdentitySet([new("different-exact-id", null)])
            : items);
        var legacy = new LegacyAdapter();
        var managed = new ManagedAdapter();
        var sut = new ExecutionBoundShareAdapter(legacy, managed);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.SubmitPermanentShareForOperationAsync(context, execution, request, default));

        Assert.Empty(legacy.Calls);
        Assert.Empty(managed.Calls);
    }

    private static SessionCookie[] Cookies() =>
        [new SessionCookie("BDUSS", "synthetic-only", ".baidu.com", "/", null, true, true)];

    private static ShareCreationReceipt Receipt(PermanentShareRequest request) => new(
        Now, "adapter-request", request, "share-id", "https://pan.baidu.com/s/share-id",
        new EffectiveShareSettings(true, true,
            request.UsesRandomExtractionCode ? ShareExtractionCodeBehavior.Random : ShareExtractionCodeBehavior.Fixed,
            request.FixedExtractionCode ?? "z9x8"));

    private sealed class NoDelay : IAsyncDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ManagedAdapter : IManagedChromiumShareAdapter
    {
        public List<string> Calls { get; } = [];
        public object? LastSession { get; private set; }
        public PermanentShareRequest? LastRequest { get; private set; }

        public Task<BaiduAdapterResult<ShareDiscoverySnapshot>> DiscoverPermanentSharesForOperationAsync(
            ShareOperationAutomationContext context, ManagedBrowserSessionReference session,
            PermanentShareRequest request, CancellationToken token) =>
            Record("discover", session, request, BaiduAdapterResult<ShareDiscoverySnapshot>.Success(
                new(WebAutomationBaseline.Create([]), [], [])));

        public Task<BaiduAdapterResult<SharePreparationReceipt>> PreparePermanentShareForOperationAsync(
            ShareOperationAutomationContext context, ManagedBrowserSessionReference session,
            PermanentShareRequest request, CancellationToken token) =>
            Record("prepare", session, request,
                BaiduAdapterResult<SharePreparationReceipt>.Success(SharePreparationReceipt.Ready));

        public Task<BaiduAdapterResult<ShareCreationReceipt>> SubmitPermanentShareForOperationAsync(
            ShareOperationAutomationContext context, ManagedBrowserSessionReference session,
            PermanentShareRequest request, CancellationToken token) =>
            Record("submit", session, request, BaiduAdapterResult<ShareCreationReceipt>.Success(Receipt(request)));

        public Task<BaiduAdapterResult<ShareCreationReceipt>> ConfirmExactShareForOperationAsync(
            ShareOperationAutomationContext context, ManagedBrowserSessionReference session,
            PermanentShareRequest request, CancellationToken token) =>
            Record("confirm", session, request, BaiduAdapterResult<ShareCreationReceipt>.Success(Receipt(request)));

        private Task<BaiduAdapterResult<T>> Record<T>(string phase, object session,
            PermanentShareRequest request, BaiduAdapterResult<T> result)
        {
            Calls.Add(phase); LastSession = session; LastRequest = request;
            return Task.FromResult(result);
        }
    }

    private sealed class LegacyAdapter : IOperationBoundBaiduShareAdapter
    {
        public List<string> Calls { get; } = [];
        public object? LastSession { get; private set; }
        public PermanentShareRequest? LastRequest { get; private set; }

        public Task<BaiduAdapterResult<BaiduSharePage>> ListSharesAsync(
            AccountSession session, string? cursor, CancellationToken token)
        {
            Calls.Add("discover"); LastSession = session;
            return Task.FromResult(BaiduAdapterResult<BaiduSharePage>.Success(new([], null)));
        }

        public Task<BaiduAdapterResult<SharePreparationReceipt>> PreparePermanentShareForOperationAsync(
            ShareOperationAutomationContext context, AccountSession session,
            PermanentShareRequest request, CancellationToken token) =>
            Record("prepare", session, request,
                BaiduAdapterResult<SharePreparationReceipt>.Success(SharePreparationReceipt.Ready));

        public Task<BaiduAdapterResult<ShareCreationReceipt>> SubmitPermanentShareForOperationAsync(
            ShareOperationAutomationContext context, AccountSession session,
            PermanentShareRequest request, CancellationToken token) =>
            Record("submit", session, request, BaiduAdapterResult<ShareCreationReceipt>.Success(Receipt(request)));

        public Task<BaiduAdapterResult<ShareCreationReceipt>> ConfirmExactShareForOperationAsync(
            ShareOperationAutomationContext context, AccountSession session,
            PermanentShareRequest request, CancellationToken token) =>
            Record("confirm", session, request, BaiduAdapterResult<ShareCreationReceipt>.Success(Receipt(request)));

        public Task<BaiduAdapterResult<ShareCreationReceipt>> CreatePermanentShareAsync(
            AccountSession session, PermanentShareRequest request, CancellationToken token) =>
            throw new InvalidOperationException("Unexpected unbound submit.");

        private Task<BaiduAdapterResult<T>> Record<T>(string phase, object session,
            PermanentShareRequest request, BaiduAdapterResult<T> result)
        {
            Calls.Add(phase); LastSession = session; LastRequest = request;
            return Task.FromResult(result);
        }
    }
}
