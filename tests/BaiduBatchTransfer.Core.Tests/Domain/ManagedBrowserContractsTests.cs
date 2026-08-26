using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Tests.Domain;

public sealed class ManagedBrowserContractsTests
{
    private static readonly Guid AccountId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid InstallationId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid SessionId = Guid.Parse("30000000-0000-0000-0000-000000000001");

    [Theory]
    [InlineData(BaiduExecutionEngine.WebView2V1, BrowserFamily.LegacyWebView2)]
    [InlineData(BaiduExecutionEngine.ManagedChromiumV1, BrowserFamily.Chrome)]
    [InlineData(BaiduExecutionEngine.ManagedChromiumV1, BrowserFamily.Edge)]
    public void ExecutionProfileSnapshot_AcceptsLegalEngineAndFamilyCombinations(
        BaiduExecutionEngine engine,
        BrowserFamily family)
    {
        var snapshot = engine == BaiduExecutionEngine.WebView2V1
            ? new ExecutionProfileSnapshot(engine, family, "legacy-profile")
            : new ExecutionProfileSnapshot(engine, family, "profile-1", InstallationId, SessionId);

        Assert.Equal(engine, snapshot.Engine);
        Assert.Equal(family, snapshot.BrowserFamily);
        Assert.NotEmpty(snapshot.ProfileId);
    }

    [Fact]
    public void ExecutionProfileSnapshot_RejectsIllegalCombinationsAndMissingIdentity()
    {
        Assert.Throws<ArgumentException>(() => new ExecutionProfileSnapshot(
            BaiduExecutionEngine.WebView2V1, BrowserFamily.Chrome, "legacy-profile"));
        Assert.Throws<ArgumentException>(() => new ExecutionProfileSnapshot(
            BaiduExecutionEngine.ManagedChromiumV1, BrowserFamily.LegacyWebView2,
            "profile-1", InstallationId, SessionId));
        Assert.Throws<ArgumentException>(() => new ExecutionProfileSnapshot(
            BaiduExecutionEngine.ManagedChromiumV1, BrowserFamily.Chrome,
            "profile-1", null, SessionId));
        Assert.Throws<ArgumentException>(() => new ExecutionProfileSnapshot(
            BaiduExecutionEngine.ManagedChromiumV1, BrowserFamily.Chrome,
            "profile-1", InstallationId, null));
        Assert.Throws<ArgumentException>(() => new ExecutionProfileSnapshot(
            BaiduExecutionEngine.WebView2V1, BrowserFamily.LegacyWebView2, " "));
    }

    [Fact]
    public void BrowserInstallationIdentity_AcceptsChromeAndEdgeAndRejectsLegacy()
    {
        var chrome = CreateInstallation(BrowserFamily.Chrome);
        var edge = CreateInstallation(BrowserFamily.Edge);

        Assert.Equal(InstallationId, chrome.InstallationId);
        Assert.Equal(BrowserFamily.Chrome, chrome.BrowserFamily);
        Assert.Equal(BrowserFamily.Edge, edge.BrowserFamily);
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateInstallation(BrowserFamily.LegacyWebView2));
    }

    [Theory]
    [InlineData("C:\\Browsers\\Chrome\\chrome.exe", "C:\\Browsers\\Other")]
    [InlineData("C:\\Browsers\\Chrome\\", "C:\\Browsers\\Chrome")]
    [InlineData("C:\\Browsers\\Chrome\\chrome.exe", "C:\\Browsers\\Chrome\\")]
    [InlineData("C:\\Browsers\\Chrome", "C:\\Browsers\\Chrome")]
    public void BrowserInstallationIdentity_RequiresImmediateRegularExecutableInsideDirectory(
        string executablePath,
        string installationDirectory)
    {
        Assert.Throws<ArgumentException>(() => new BrowserInstallationIdentity(
            InstallationId,
            BrowserFamily.Chrome,
            installationDirectory,
            executablePath,
            "Google LLC",
            "Google Chrome",
            "file-identity"));
    }

    [Theory]
    [InlineData(BrowserFamily.Chrome, "msedge.exe", "Google LLC", "Google Chrome")]
    [InlineData(BrowserFamily.Edge, "chrome.exe", "Microsoft Corporation", "Microsoft Edge")]
    [InlineData(BrowserFamily.Chrome, "chrome.exe", "Microsoft Corporation", "Google Chrome")]
    [InlineData(BrowserFamily.Edge, "msedge.exe", "Google LLC", "Microsoft Edge")]
    [InlineData(BrowserFamily.Chrome, "chrome.exe", "Google LLC", "Microsoft Edge")]
    public void BrowserInstallationIdentity_RequiresExactFamilyFilePublisherAndProductTuple(
        BrowserFamily family,
        string executableName,
        string publisher,
        string productName)
    {
        Assert.Throws<ArgumentException>(() => new BrowserInstallationIdentity(
            InstallationId,
            family,
            family == BrowserFamily.Chrome ? "C:\\Browsers\\Chrome" : "C:\\Browsers\\Edge",
            $"C:\\Browsers\\{family}\\{executableName}",
            publisher,
            productName,
            "file-identity"));
    }

    [Theory]
    [InlineData(" ", "Google Chrome", "file-identity")]
    [InlineData("Google LLC", " ", "file-identity")]
    [InlineData("Google LLC", "Google Chrome", " ")]
    [InlineData("Google\nLLC", "Google Chrome", "file-identity")]
    [InlineData("Google LLC", "Google Chrome", "file\0identity")]
    public void BrowserInstallationIdentity_RejectsInvalidIdentityStrings(
        string publisher,
        string productName,
        string fileIdentity)
    {
        Assert.Throws<ArgumentException>(() => new BrowserInstallationIdentity(
            InstallationId,
            BrowserFamily.Chrome,
            "C:\\Browsers\\Chrome",
            "C:\\Browsers\\Chrome\\chrome.exe",
            publisher,
            productName,
            fileIdentity));
    }

    [Fact]
    public void BrowserInstallationIdentity_RejectsOverlongIdentityStrings()
    {
        var overlongIdentity = new string('x', 257);

        Assert.Throws<ArgumentException>(() => new BrowserInstallationIdentity(
            InstallationId,
            BrowserFamily.Chrome,
            "C:\\Browsers\\Chrome",
            "C:\\Browsers\\Chrome\\chrome.exe",
            "Google LLC",
            "Google Chrome",
            overlongIdentity));

        Assert.Throws<ArgumentException>(() => new BrowserInstallationIdentity(
            InstallationId,
            BrowserFamily.Chrome,
            "C:\\Browsers\\Chrome",
            "C:\\Browsers\\Chrome\\chrome.exe",
            overlongIdentity,
            "Google Chrome",
            "file-identity"));
        Assert.Throws<ArgumentException>(() => new BrowserInstallationIdentity(
            InstallationId,
            BrowserFamily.Chrome,
            "C:\\Browsers\\Chrome",
            "C:\\Browsers\\Chrome\\chrome.exe",
            "Google LLC",
            overlongIdentity,
            "file-identity"));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("C:\\Browsers\\Chrome", "")]
    [InlineData("C:\\Browsers\\Chrome", "C:\\Browsers\\Chrome\\..\\chrome.exe")]
    [InlineData("C:\\Browsers\\Chrome", "chrome.exe")]
    public void BrowserInstallationIdentity_RejectsEmptyOrNoncanonicalPaths(
        string directory,
        string executablePath)
    {
        Assert.Throws<ArgumentException>(() => new BrowserInstallationIdentity(
            InstallationId,
            BrowserFamily.Chrome,
            directory,
            executablePath,
            "Google LLC",
            "Google Chrome",
            "file-identity"));
    }

    [Fact]
    public void BrowserInstallationIdentity_RejectsEmptyInstallationId()
    {
        Assert.Throws<ArgumentException>(() => new BrowserInstallationIdentity(
            Guid.Empty,
            BrowserFamily.Chrome,
            "C:\\Browsers\\Chrome",
            "C:\\Browsers\\Chrome\\chrome.exe",
            "Google LLC",
            "Google Chrome",
            "file-identity"));
    }

    [Fact]
    public void ManagedBrowserSessionReference_RequiresImmutableNonemptyIdentityFields()
    {
        var reference = new ManagedBrowserSessionReference(
            SessionId, InstallationId, AccountId, "stable-uid", BrowserFamily.Chrome,
            "profile-1", "ownership-1");

        Assert.Equal(SessionId, reference.SessionId);
        Assert.Equal(InstallationId, reference.InstallationId);
        Assert.Equal(AccountId, reference.AccountId);
        Assert.Equal("stable-uid", reference.StableBaiduUserId);
        Assert.Equal("profile-1", reference.ProfileId);
        Assert.Equal("ownership-1", reference.ProfileOwnershipId);

        Assert.Throws<ArgumentException>(() => new ManagedBrowserSessionReference(
            Guid.Empty, InstallationId, AccountId, "stable-uid", BrowserFamily.Chrome,
            "profile-1", "ownership-1"));
        Assert.Throws<ArgumentException>(() => new ManagedBrowserSessionReference(
            SessionId, Guid.Empty, AccountId, "stable-uid", BrowserFamily.Chrome,
            "profile-1", "ownership-1"));
        Assert.Throws<ArgumentException>(() => new ManagedBrowserSessionReference(
            SessionId, InstallationId, AccountId, "", BrowserFamily.Chrome,
            "profile-1", "ownership-1"));
        Assert.Throws<ArgumentException>(() => new ManagedBrowserSessionReference(
            SessionId, InstallationId, AccountId, "stable-uid", BrowserFamily.Chrome,
            "", "ownership-1"));
        Assert.Throws<ArgumentException>(() => new ManagedBrowserSessionReference(
            SessionId, InstallationId, AccountId, "stable-uid", BrowserFamily.Chrome,
            "profile-1", ""));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ManagedBrowserSessionReference(
            SessionId, InstallationId, AccountId, "stable-uid", BrowserFamily.LegacyWebView2,
            "profile-1", "ownership-1"));
    }

    [Fact]
    public void ManagedContextCarriesReferenceWithoutCookies()
    {
        var reference = new ManagedBrowserSessionReference(
            SessionId, InstallationId, AccountId, "stable-uid", BrowserFamily.Chrome,
            "profile-1", "ownership-1");

        var context = BaiduExecutionContext.Managed(reference);

        Assert.Equal(BaiduExecutionEngine.ManagedChromiumV1, context.Engine);
        Assert.Null(context.LegacySession);
        Assert.Same(reference, context.ManagedSession);
        Assert.DoesNotContain("stable-uid", context.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("profile-1", context.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ownership-1", context.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyContextCarriesAccountSessionAndCannotCarryManagedReference()
    {
        var accountSession = new AccountSession(
            AccountId,
            "stable-uid",
            new[] { new SessionCookie("BDUSS", "sensitive-cookie", ".baidu.com", "/", null, true, true) },
            DateTimeOffset.UtcNow);
        var context = BaiduExecutionContext.Legacy(accountSession);

        Assert.Equal(BaiduExecutionEngine.WebView2V1, context.Engine);
        Assert.Same(accountSession, context.LegacySession);
        Assert.Null(context.ManagedSession);
        Assert.DoesNotContain("sensitive-cookie", context.ToString(), StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => BaiduExecutionContext.Legacy(null!));
        Assert.Throws<ArgumentNullException>(() => BaiduExecutionContext.Managed(null!));
    }

    [Fact]
    public void PublicContractToStringsRedactSensitiveIdentityValues()
    {
        var installation = CreateInstallation(BrowserFamily.Chrome);
        var reference = new ManagedBrowserSessionReference(
            SessionId, InstallationId, AccountId, "stable-uid", BrowserFamily.Chrome,
            "profile-1", "ownership-1");
        var snapshot = new ExecutionProfileSnapshot(
            BaiduExecutionEngine.ManagedChromiumV1, BrowserFamily.Chrome,
            "profile-1", InstallationId, SessionId);
        var transition = new ManagedBrowserTransitionRequest(InstallationId, expectedRevision: 4);
        var retention = new ManagedBrowserRetentionReference(
            ManagedBrowserRetentionOwnerKind.Operation, AccountId, InstallationId, SessionId);

        foreach (var value in new object[] { installation, reference, snapshot, transition, retention })
        {
            var text = value.ToString()!;
            Assert.DoesNotContain("C:\\Browsers", text, StringComparison.Ordinal);
            Assert.DoesNotContain("chrome.exe", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Google LLC", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Google Chrome", text, StringComparison.Ordinal);
            Assert.DoesNotContain("file-identity", text, StringComparison.Ordinal);
            Assert.DoesNotContain("stable-uid", text, StringComparison.Ordinal);
            Assert.DoesNotContain("profile-1", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ownership-1", text, StringComparison.Ordinal);
            Assert.DoesNotContain(InstallationId.ToString(), text, StringComparison.Ordinal);
            Assert.DoesNotContain(SessionId.ToString(), text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ManagedBrowserStateStore_ExposesCreateOnlyCasLookupAndRetentionSignatures()
    {
        IManagedBrowserStateStore store = new ContractStore();
        Func<BrowserInstallationIdentity, CancellationToken, Task<BrowserInstallationIdentity>> createInstallation = store.CreateInstallationAsync;
        Func<Guid, CancellationToken, Task<BrowserInstallationIdentity?>> loadInstallation = store.LoadInstallationAsync;
        Func<CancellationToken, Task<BrowserInstallationIdentity?>> loadActiveInstallation = store.LoadActiveInstallationAsync;
        Func<ManagedBrowserTransitionRequest, CancellationToken, Task<ManagedBrowserTransitionResult>> activateInstallation = store.TryActivateInstallationAsync;
        Func<ManagedBrowserTransitionRequest, CancellationToken, Task<ManagedBrowserTransitionResult>> retireInstallation = store.TryRetireInstallationAsync;
        Func<ManagedBrowserSessionReference, CancellationToken, Task<ManagedBrowserSessionReference>> createSession = store.CreateSessionAsync;
        Func<Guid, CancellationToken, Task<ManagedBrowserSessionReference?>> loadSession = store.LoadSessionAsync;
        Func<Guid, BrowserFamily, CancellationToken, Task<ManagedBrowserSessionReference?>> loadActiveSession = store.LoadActiveSessionAsync;
        Func<ManagedBrowserTransitionRequest, CancellationToken, Task<ManagedBrowserTransitionResult>> activateSession = store.TryActivateSessionAsync;
        Func<ManagedBrowserTransitionRequest, CancellationToken, Task<ManagedBrowserTransitionResult>> retireSession = store.TryRetireSessionAsync;
        Func<ManagedBrowserRetentionReference, CancellationToken, Task> addRetention = store.AddRetentionAsync;
        Func<ManagedBrowserRetentionReference, CancellationToken, Task> releaseRetention = store.ReleaseRetentionAsync;
        Func<Guid, Guid, CancellationToken, Task<bool>> eligible = store.IsRetirementEligibleAsync;

        Assert.NotNull(createInstallation);
        Assert.NotNull(loadInstallation);
        Assert.NotNull(loadActiveInstallation);
        Assert.NotNull(activateInstallation);
        Assert.NotNull(retireInstallation);
        Assert.NotNull(createSession);
        Assert.NotNull(loadSession);
        Assert.NotNull(loadActiveSession);
        Assert.NotNull(activateSession);
        Assert.NotNull(retireSession);
        Assert.NotNull(addRetention);
        Assert.NotNull(releaseRetention);
        Assert.NotNull(eligible);
    }

    [Fact]
    public void ManagedBrowserTransitionAndRetentionContractsValidateIdentityAndStatus()
    {
        var transition = new ManagedBrowserTransitionRequest(InstallationId, expectedRevision: 4);
        Assert.Equal(InstallationId, transition.IdentityId);
        Assert.Equal(4, transition.ExpectedRevision);
        Assert.Equal(ManagedBrowserTransitionStatus.Applied, ManagedBrowserTransitionResult.Applied(5).Status);
        Assert.Equal(5, ManagedBrowserTransitionResult.Applied(5).CurrentRevision);
        Assert.Equal(ManagedBrowserTransitionStatus.Conflict, ManagedBrowserTransitionResult.Conflict(6).Status);
        Assert.Equal(ManagedBrowserTransitionStatus.NotFound, ManagedBrowserTransitionResult.NotFound().Status);
        Assert.Throws<ArgumentException>(() => new ManagedBrowserTransitionRequest(Guid.Empty, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ManagedBrowserTransitionRequest(InstallationId, -1));

        var operationRetention = new ManagedBrowserRetentionReference(
            ManagedBrowserRetentionOwnerKind.Operation,
            AccountId,
            InstallationId,
            SessionId);
        var attemptRetention = new ManagedBrowserRetentionReference(
            ManagedBrowserRetentionOwnerKind.Attempt,
            SessionId,
            InstallationId,
            SessionId);
        var cleanupRetention = new ManagedBrowserRetentionReference(
            ManagedBrowserRetentionOwnerKind.CleanupManifest,
            InstallationId,
            InstallationId,
            SessionId);

        Assert.Equal(ManagedBrowserRetentionOwnerKind.Operation, operationRetention.OwnerKind);
        Assert.Equal(ManagedBrowserRetentionOwnerKind.Attempt, attemptRetention.OwnerKind);
        Assert.Equal(ManagedBrowserRetentionOwnerKind.CleanupManifest, cleanupRetention.OwnerKind);
        Assert.Throws<ArgumentException>(() => new ManagedBrowserRetentionReference(
            ManagedBrowserRetentionOwnerKind.Operation, Guid.Empty, InstallationId, SessionId));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ManagedBrowserRetentionReference(
            (ManagedBrowserRetentionOwnerKind)99, AccountId, InstallationId, SessionId));
    }

    private static BrowserInstallationIdentity CreateInstallation(BrowserFamily family) => new(
        InstallationId,
        family,
        family == BrowserFamily.Chrome ? "C:\\Browsers\\Chrome" : "C:\\Browsers\\Edge",
        family == BrowserFamily.Chrome ? "C:\\Browsers\\Chrome\\chrome.exe" : "C:\\Browsers\\Edge\\msedge.exe",
        family == BrowserFamily.Chrome ? "Google LLC" : "Microsoft Corporation",
        family == BrowserFamily.Chrome ? "Google Chrome" : "Microsoft Edge",
        "file-identity");

    private sealed class ContractStore : IManagedBrowserStateStore
    {
        public Task<BrowserInstallationIdentity> CreateInstallationAsync(BrowserInstallationIdentity installation, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<BrowserInstallationIdentity?> LoadInstallationAsync(Guid installationId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<BrowserInstallationIdentity?> LoadActiveInstallationAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ManagedBrowserTransitionResult> TryActivateInstallationAsync(ManagedBrowserTransitionRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ManagedBrowserTransitionResult> TryRetireInstallationAsync(ManagedBrowserTransitionRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ManagedBrowserSessionReference> CreateSessionAsync(ManagedBrowserSessionReference session, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ManagedBrowserSessionReference?> LoadSessionAsync(Guid sessionId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ManagedBrowserSessionReference?> LoadActiveSessionAsync(Guid accountId, BrowserFamily browserFamily, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ManagedBrowserTransitionResult> TryActivateSessionAsync(ManagedBrowserTransitionRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ManagedBrowserTransitionResult> TryRetireSessionAsync(ManagedBrowserTransitionRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task AddRetentionAsync(ManagedBrowserRetentionReference reference, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task ReleaseRetentionAsync(ManagedBrowserRetentionReference reference, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<bool> IsRetirementEligibleAsync(Guid installationId, Guid sessionId, CancellationToken cancellationToken) => throw new NotImplementedException();
    }
}
