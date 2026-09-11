using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Tests.Scheduling;

public sealed class BaiduExecutionContextResolverTests
{
    private static readonly Guid AccountId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private const string Uid = "synthetic-stable-uid";

    [Theory]
    [InlineData(BrowserFamily.Chrome)]
    [InlineData(BrowserFamily.Edge)]
    public async Task ManagedExactActiveBindingPreservesTheStoredSessionReference(BrowserFamily family)
    {
        var fixture = Managed(family);
        var result = await fixture.Resolver.ResolveAsync(AccountId, fixture.Profile, default);
        Assert.Equal(BaiduExecutionResolutionStatus.Ready, result.Status);
        Assert.Same(fixture.Bindings.Snapshot.Session!.Identity, result.Context!.ManagedSession);
        Assert.Null(result.Context.LegacySession);
        Assert.Equal(BaiduExecutionEngine.ManagedChromiumV1, result.Context.Engine);
        Assert.Equal(0, fixture.Legacy.Loads);
        Assert.Equal(AccountId, fixture.Bindings.RequestedAccount);
        Assert.Same(fixture.Profile, fixture.Bindings.RequestedProfile);
        Assert.Equal(1, fixture.Bindings.Loads);
    }

    public static IEnumerable<object?[]> UnavailableAccounts()
    {
        foreach (var managed in new[] { false, true })
        {
            yield return new object?[] { managed, null };
            yield return new object?[] { managed, Account(enabled: false) };
            yield return new object?[] { managed, Account(deleted: true) };
            foreach (var status in new[] { "Disabled", "Deleted", "Invalid", "active", "Unknown" })
                yield return new object?[] { managed, Account(status: status) };
        }
    }

    [Theory]
    [MemberData(nameof(UnavailableAccounts))]
    public async Task UnavailableAccountFailsClosedWithoutLoadingLegacy(bool managed, BaiduExecutionAccount? account)
    {
        var fixture = Managed();
        fixture.Bindings.Snapshot = new(account, fixture.Bindings.Snapshot.Installation, fixture.Bindings.Snapshot.Session);
        AssertUnavailable(await fixture.Resolver.ResolveAsync(AccountId,
            managed ? fixture.Profile : ExecutionProfileSnapshot.LegacyForAccount(AccountId), default),
            BaiduExecutionResolutionStatus.AccountUnavailable);
        Assert.Equal(0, fixture.Legacy.Loads);
    }

    [Theory]
    [InlineData(false, "wrong-account")]
    [InlineData(true, "wrong-account")]
    [InlineData(false, "empty-uid")]
    [InlineData(true, "empty-uid")]
    [InlineData(false, "blank-uid")]
    [InlineData(true, "blank-uid")]
    public async Task InvalidAccountIdentityFailsClosed(bool managed, string mismatch)
    {
        var fixture = Managed();
        var account = mismatch == "wrong-account" ? Account(id: Guid.NewGuid()) :
            Account(uid: mismatch == "empty-uid" ? "" : " ");
        fixture.Bindings.Snapshot = new(account, fixture.Bindings.Snapshot.Installation, fixture.Bindings.Snapshot.Session);
        AssertUnavailable(await fixture.Resolver.ResolveAsync(AccountId,
            managed ? fixture.Profile : ExecutionProfileSnapshot.LegacyForAccount(AccountId), default),
            BaiduExecutionResolutionStatus.AccountIdentityMismatch);
        Assert.Equal(0, fixture.Legacy.Loads);
    }

    [Theory]
    [InlineData("installation")]
    [InlineData("session")]
    [InlineData("both")]
    public async Task MissingManagedStateIsUnavailableWithoutLegacyFallback(string missing)
    {
        var fixture = Managed();
        fixture.Bindings.Snapshot = new(Account(), missing is "installation" or "both" ? null : fixture.Bindings.Snapshot.Installation,
            missing is "session" or "both" ? null : fixture.Bindings.Snapshot.Session);
        AssertUnavailable(await fixture.Resolver.ResolveAsync(AccountId, fixture.Profile, default), BaiduExecutionResolutionStatus.BrowserUnavailable);
        Assert.Equal(1, fixture.Bindings.Loads);
        Assert.Equal(0, fixture.Legacy.Loads);
    }

    [Theory]
    [InlineData(ManagedBrowserSessionLifecycleState.Retired)]
    [InlineData(ManagedBrowserSessionLifecycleState.QuarantinePending)]
    [InlineData(ManagedBrowserSessionLifecycleState.Quarantined)]
    [InlineData(ManagedBrowserSessionLifecycleState.CleanupPending)]
    [InlineData(ManagedBrowserSessionLifecycleState.Deleted)]
    public async Task EveryNonActiveManagedSessionFailsClosed(ManagedBrowserSessionLifecycleState state)
    {
        var fixture = Managed();
        fixture.Bindings.Snapshot = new(Account(), fixture.Bindings.Snapshot.Installation,
            new(fixture.Bindings.Snapshot.Session!.Identity, state, 4));
        AssertUnavailable(await fixture.Resolver.ResolveAsync(AccountId, fixture.Profile, default), BaiduExecutionResolutionStatus.BrowserUnavailable);
        Assert.Equal(1, fixture.Bindings.Loads);
        Assert.Equal(0, fixture.Legacy.Loads);
    }

    [Fact]
    public async Task RetiredInstallationFailsClosed()
    {
        var fixture = Managed();
        fixture.Bindings.Snapshot = new(Account(), new(fixture.Bindings.Snapshot.Installation!.Identity,
            BrowserInstallationLifecycleState.Retired, 4), fixture.Bindings.Snapshot.Session);
        AssertUnavailable(await fixture.Resolver.ResolveAsync(AccountId, fixture.Profile, default), BaiduExecutionResolutionStatus.BrowserUnavailable);
        Assert.Equal(1, fixture.Bindings.Loads);
        Assert.Equal(0, fixture.Legacy.Loads);
    }

    [Theory]
    [InlineData("installation-id")]
    [InlineData("installation-family")]
    [InlineData("session-id")]
    [InlineData("session-installation-id")]
    [InlineData("session-account")]
    [InlineData("session-family")]
    [InlineData("session-profile")]
    [InlineData("session-profile-case")]
    [InlineData("session-uid")]
    [InlineData("session-uid-case")]
    public async Task EveryManagedTupleMismatchFailsClosed(string mismatch)
    {
        var fixture = Managed();
        var original = fixture.Bindings.Snapshot;
        var identity = original.Session!.Identity;
        var installation = mismatch == "installation-id" ? Installation(Guid.NewGuid(), BrowserFamily.Chrome) :
            mismatch == "installation-family" ? Installation(identity.InstallationId, BrowserFamily.Edge) : original.Installation!;
        var changed = new ManagedBrowserSessionReference(
            mismatch == "session-id" ? Guid.NewGuid() : identity.SessionId,
            mismatch == "session-installation-id" ? Guid.NewGuid() : identity.InstallationId,
            mismatch == "session-account" ? Guid.NewGuid() : identity.AccountId,
            mismatch == "session-uid" ? "other-uid" : mismatch == "session-uid-case" ? Uid.ToUpperInvariant() : Uid,
            mismatch == "session-family" ? BrowserFamily.Edge : identity.BrowserFamily,
            mismatch == "session-profile" ? "other-profile" : mismatch == "session-profile-case" ? identity.ProfileId.ToUpperInvariant() : identity.ProfileId,
            identity.ProfileOwnershipId);
        fixture.Bindings.Snapshot = new(Account(), installation, new(changed, ManagedBrowserSessionLifecycleState.Active, 3));
        AssertUnavailable(await fixture.Resolver.ResolveAsync(AccountId, fixture.Profile, default), BaiduExecutionResolutionStatus.AccountIdentityMismatch);
        Assert.Equal(0, fixture.Legacy.Loads);
    }

    [Fact]
    public async Task CanonicalLegacyProfileLoadsOnlyTheExactAccountAndPreservesSession()
    {
        var fixture = Managed();
        fixture.Legacy.Session = LegacySession();
        var result = await fixture.Resolver.ResolveAsync(AccountId, ExecutionProfileSnapshot.LegacyForAccount(AccountId), default);
        Assert.Equal(BaiduExecutionResolutionStatus.Ready, result.Status);
        Assert.Equal(BaiduExecutionEngine.WebView2V1, result.Context!.Engine);
        Assert.Same(fixture.Legacy.Session, result.Context.LegacySession);
        Assert.Null(result.Context.ManagedSession);
        Assert.Equal(AccountId.ToString("D"), fixture.Legacy.RequestedAccount);
        Assert.Equal(1, fixture.Legacy.Loads);
    }

    [Fact]
    public async Task MissingLegacySessionRequiresLogin()
    {
        var fixture = Managed();
        AssertUnavailable(await fixture.Resolver.ResolveAsync(AccountId, ExecutionProfileSnapshot.LegacyForAccount(AccountId), default),
            BaiduExecutionResolutionStatus.LoginRequired);
        Assert.Equal(1, fixture.Legacy.Loads);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("uid")]
    [InlineData("uid-case")]
    [InlineData("unbound")]
    public async Task WrongOrUnboundLegacySessionFailsIdentityValidation(string mismatch)
    {
        var fixture = Managed();
        fixture.Legacy.Session = mismatch == "unbound" ? new AccountSession(AccountId.ToString("D"), Cookies(), DateTimeOffset.UtcNow) :
            LegacySession(mismatch == "account" ? Guid.NewGuid() : AccountId,
                mismatch == "uid" ? "other-uid" : mismatch == "uid-case" ? Uid.ToUpperInvariant() : Uid);
        AssertUnavailable(await fixture.Resolver.ResolveAsync(AccountId, ExecutionProfileSnapshot.LegacyForAccount(AccountId), default),
            BaiduExecutionResolutionStatus.AccountIdentityMismatch);
    }

    [Theory]
    [InlineData("custom")]
    [InlineData("wrong-account")]
    [InlineData("case")]
    public async Task NonCanonicalLegacyProfileDoesNotLoadAStoredSession(string mismatch)
    {
        var fixture = Managed();
        var profile = mismatch == "wrong-account" ? ExecutionProfileSnapshot.LegacyForAccount(Guid.NewGuid()) :
            ExecutionProfileSnapshot.Legacy(mismatch == "case" ? ExecutionProfileSnapshot.LegacyForAccount(AccountId).ProfileId.ToUpperInvariant() : "custom");
        AssertUnavailable(await fixture.Resolver.ResolveAsync(AccountId, profile, default), BaiduExecutionResolutionStatus.AccountIdentityMismatch);
        Assert.Equal(0, fixture.Legacy.Loads);
    }

    [Fact]
    public async Task InvalidInputsAndPreCancellationDoNotTouchEitherStore()
    {
        var fixture = Managed();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Resolver.ResolveAsync(Guid.Empty, fixture.Profile, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => fixture.Resolver.ResolveAsync(AccountId, null!, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Resolver.ResolveAsync(AccountId, fixture.Profile, cancellation.Token));
        Assert.Equal(0, fixture.Bindings.Loads);
        Assert.Equal(0, fixture.Legacy.Loads);
        Assert.Throws<ArgumentNullException>(() => new BaiduExecutionContextResolver(null!, fixture.Legacy));
        Assert.Throws<ArgumentNullException>(() => new BaiduExecutionContextResolver(fixture.Bindings, null!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterBindingLoadPreventsResolution(bool managed)
    {
        var fixture = Managed();
        using var cancellation = new CancellationTokenSource();
        fixture.Bindings.AfterLoad = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Resolver.ResolveAsync(AccountId,
            managed ? fixture.Profile : ExecutionProfileSnapshot.LegacyForAccount(AccountId), cancellation.Token));
        Assert.Equal(cancellation.Token, fixture.Bindings.Token);
        Assert.Equal(0, fixture.Legacy.Loads);
    }

    [Fact]
    public async Task CancellationAfterLegacyLoadPreventsReadyResult()
    {
        var fixture = Managed();
        using var cancellation = new CancellationTokenSource();
        fixture.Legacy.Session = LegacySession();
        fixture.Legacy.AfterLoad = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Resolver.ResolveAsync(AccountId,
            ExecutionProfileSnapshot.LegacyForAccount(AccountId), cancellation.Token));
        Assert.Equal(cancellation.Token, fixture.Legacy.Token);
    }

    [Fact]
    public void ResultsRequireValidConstructionAndNeverExposeIdentityOrSecrets()
    {
        Assert.Throws<ArgumentNullException>(() => BaiduExecutionResolution.Ready(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => BaiduExecutionResolution.Unavailable(BaiduExecutionResolutionStatus.Ready));
        Assert.Throws<ArgumentOutOfRangeException>(() => BaiduExecutionResolution.Unavailable((BaiduExecutionResolutionStatus)999));
        Assert.Empty(typeof(BaiduExecutionResolution).GetConstructors());
        var context = BaiduExecutionContext.Legacy(LegacySession());
        var ready = BaiduExecutionResolution.Ready(context);
        Assert.Equal(BaiduExecutionResolutionStatus.Ready, ready.Status);
        Assert.Same(context, ready.Context);
        foreach (var status in Enum.GetValues<BaiduExecutionResolutionStatus>().Where(x => x != BaiduExecutionResolutionStatus.Ready))
        {
            var result = BaiduExecutionResolution.Unavailable(status);
            AssertUnavailable(result, status);
            Assert.Equal(result.SafeMessage, BaiduExecutionResolution.Unavailable(status).SafeMessage);
        }
        Assert.DoesNotContain(Uid, ready.ToString());
        Assert.DoesNotContain(AccountId.ToString("D"), ready.ToString());
        Assert.DoesNotContain("synthetic-secret-cookie", ready.ToString());
    }

    private static void AssertUnavailable(BaiduExecutionResolution result, BaiduExecutionResolutionStatus status)
    {
        Assert.Equal(status, result.Status);
        Assert.Null(result.Context);
        Assert.False(string.IsNullOrWhiteSpace(result.SafeMessage));
        Assert.DoesNotContain(Uid, result.SafeMessage);
    }

    private static BaiduExecutionAccount Account(Guid? id = null, string uid = Uid, string status = "Active", bool enabled = true, bool deleted = false) =>
        new(id ?? AccountId, uid, status, enabled, deleted);

    private static AccountSession LegacySession(Guid? id = null, string uid = Uid) => new(id ?? AccountId, uid, Cookies(), DateTimeOffset.UtcNow);
    private static SessionCookie[] Cookies() => [new("BDUSS", "synthetic-secret-cookie", ".baidu.com", "/", null, true, true)];

    private static Fixture Managed(BrowserFamily family = BrowserFamily.Chrome)
    {
        var installation = Installation(Guid.NewGuid(), family);
        var identity = new ManagedBrowserSessionReference(Guid.NewGuid(), installation.Identity.InstallationId,
            AccountId, Uid, family, "synthetic-profile", "synthetic-ownership");
        var bindings = new BindingStore(new(Account(), installation, new(identity, ManagedBrowserSessionLifecycleState.Active, 2)));
        var legacy = new LegacyStore();
        return new(bindings, legacy, new(bindings, legacy), ExecutionProfileSnapshot.Managed(family, identity.ProfileId, identity.InstallationId, identity.SessionId));
    }

    private static StoredBrowserInstallation Installation(Guid id, BrowserFamily family)
    {
        var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "synthetic-execution-installation", id.ToString("N")));
        return new(new(id, family, directory, Path.Combine(directory, family == BrowserFamily.Chrome ? "chrome.exe" : "msedge.exe"),
            family == BrowserFamily.Chrome ? "Google LLC" : "Microsoft Corporation",
            family == BrowserFamily.Chrome ? "Google Chrome" : "Microsoft Edge", "synthetic-file"), BrowserInstallationLifecycleState.Active, 1);
    }

    private sealed record Fixture(BindingStore Bindings, LegacyStore Legacy, BaiduExecutionContextResolver Resolver, ExecutionProfileSnapshot Profile);

    private sealed class BindingStore(BaiduExecutionBindingSnapshot snapshot) : IBaiduExecutionBindingStore
    {
        public BaiduExecutionBindingSnapshot Snapshot { get; set; } = snapshot;
        public int Loads { get; private set; }
        public Guid RequestedAccount { get; private set; }
        public ExecutionProfileSnapshot? RequestedProfile { get; private set; }
        public CancellationToken Token { get; private set; }
        public Action? AfterLoad { get; set; }
        public Task<BaiduExecutionBindingSnapshot> LoadAsync(Guid accountId, ExecutionProfileSnapshot profile, CancellationToken cancellationToken)
        {
            Loads++;
            RequestedAccount = accountId;
            RequestedProfile = profile;
            Token = cancellationToken;
            AfterLoad?.Invoke();
            return Task.FromResult(Snapshot);
        }
    }

    private sealed class LegacyStore : IBaiduSessionStore
    {
        public AccountSession? Session { get; set; }
        public int Loads { get; private set; }
        public string? RequestedAccount { get; private set; }
        public CancellationToken Token { get; private set; }
        public Action? AfterLoad { get; set; }
        public Task<AccountSession?> LoadAsync(string accountId, CancellationToken cancellationToken)
        {
            Loads++;
            RequestedAccount = accountId;
            Token = cancellationToken;
            AfterLoad?.Invoke();
            return Task.FromResult(Session);
        }
        public Task SaveAsync(AccountSession session, CancellationToken cancellationToken) => throw new InvalidOperationException("No session writes allowed.");
        public Task RemoveAsync(string accountId, CancellationToken cancellationToken) => throw new InvalidOperationException("No session writes allowed.");
    }
}
