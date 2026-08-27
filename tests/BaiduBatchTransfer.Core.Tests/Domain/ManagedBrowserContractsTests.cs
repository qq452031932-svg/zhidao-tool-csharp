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
        Assert.Equal("Google LLC", chrome.Publisher);
        Assert.Equal("Google Chrome", chrome.ProductName);
        Assert.Equal("file-identity", chrome.FileIdentity);
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

    [Fact]
    public void StoredBrowserSnapshotsExposeValidatedStateAndRevision()
    {
        var installation = CreateInstallation(BrowserFamily.Chrome);
        var storedInstallation = new StoredBrowserInstallation(
            installation, BrowserInstallationLifecycleState.Retired, revision: 3);
        var session = CreateReference(BrowserFamily.Chrome);
        var storedSession = new StoredManagedBrowserSession(
            session, ManagedBrowserSessionLifecycleState.Active, revision: 4);

        Assert.Same(installation, storedInstallation.Identity);
        Assert.Equal(BrowserInstallationLifecycleState.Retired, storedInstallation.State);
        Assert.Equal(3, storedInstallation.Revision);
        Assert.Same(session, storedSession.Identity);
        Assert.Equal(ManagedBrowserSessionLifecycleState.Active, storedSession.State);
        Assert.Equal(4, storedSession.Revision);
        Assert.Throws<ArgumentOutOfRangeException>(() => new StoredBrowserInstallation(
            installation, BrowserInstallationLifecycleState.Retired, revision: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StoredManagedBrowserSession(
            session, (ManagedBrowserSessionLifecycleState)99, revision: 0));
    }

    [Fact]
    public void ActiveSlotCasRequestsSupportInitialAndReplacementButRejectStaleScopes()
    {
        var currentInstallation = new StoredBrowserInstallation(
            CreateInstallation(BrowserFamily.Chrome), BrowserInstallationLifecycleState.Active, 8);
        var targetInstallation = new StoredBrowserInstallation(
            new BrowserInstallationIdentity(
                Guid.Parse("21000000-0000-0000-0000-000000000001"), BrowserFamily.Edge,
                "C:\\Browsers\\Edge", "C:\\Browsers\\Edge\\msedge.exe",
                "Microsoft Corporation", "Microsoft Edge", "edge-file"),
            BrowserInstallationLifecycleState.Retired, 0);

        var initial = new ManagedBrowserInstallationActivationRequest(null, targetInstallation);
        var replacement = new ManagedBrowserInstallationActivationRequest(currentInstallation, targetInstallation);
        Assert.Null(initial.ExpectedCurrent);
        Assert.Same(targetInstallation, initial.Target);
        Assert.Same(currentInstallation, replacement.ExpectedCurrent);
        Assert.Throws<ArgumentException>(() => new ManagedBrowserInstallationActivationRequest(
            currentInstallation, currentInstallation));

        var currentSession = new StoredManagedBrowserSession(
            CreateReference(BrowserFamily.Chrome), ManagedBrowserSessionLifecycleState.Active, 5);
        var targetSession = new StoredManagedBrowserSession(
            new ManagedBrowserSessionReference(
                Guid.Parse("31000000-0000-0000-0000-000000000001"), InstallationId, AccountId,
                "stable-edge", BrowserFamily.Chrome, "profile-2", "ownership-2"),
            ManagedBrowserSessionLifecycleState.Retired, 0);
        var sessionReplacement = new ManagedBrowserSessionActivationRequest(currentSession, targetSession);
        Assert.Same(currentSession, sessionReplacement.ExpectedCurrent);
        Assert.Same(targetSession, sessionReplacement.Target);
        Assert.Throws<ArgumentException>(() => new ManagedBrowserSessionActivationRequest(
            currentSession,
            new StoredManagedBrowserSession(
                new ManagedBrowserSessionReference(
                    Guid.Parse("32000000-0000-0000-0000-000000000001"), InstallationId,
                    Guid.Parse("10000000-0000-0000-0000-000000000002"), "other-account",
                    BrowserFamily.Chrome, "profile-3", "ownership-3"),
                ManagedBrowserSessionLifecycleState.Retired, 0)));

        var conflict = ManagedBrowserActiveSlotTransitionResult<StoredBrowserInstallation>.Conflict(currentInstallation);
        Assert.Equal(ManagedBrowserTransitionStatus.Conflict, conflict.Status);
        Assert.Same(currentInstallation, conflict.Current);

        var applied = ManagedBrowserActiveSlotTransitionResult<StoredBrowserInstallation>.Applied(targetInstallation);
        Assert.Equal(ManagedBrowserTransitionStatus.Applied, applied.Status);
        Assert.Same(targetInstallation, applied.Current);
        var notFound = ManagedBrowserActiveSlotTransitionResult<StoredBrowserInstallation>.NotFound();
        Assert.Equal(ManagedBrowserTransitionStatus.NotFound, notFound.Status);
        Assert.Null(notFound.Current);

        Assert.Throws<ArgumentException>(() => new ManagedBrowserSessionActivationRequest(
            currentSession,
            new StoredManagedBrowserSession(
                new ManagedBrowserSessionReference(
                    Guid.Parse("33000000-0000-0000-0000-000000000001"), InstallationId, AccountId,
                    "stable-edge-family", BrowserFamily.Edge, "profile-4", "ownership-4"),
                ManagedBrowserSessionLifecycleState.Retired, 0)));
    }

    [Fact]
    public void FamilySwitchRequestAcceptsOnlyExactRetiredReusableInstallation()
    {
        var currentInstallation = new StoredBrowserInstallation(
            CreateInstallation(BrowserFamily.Chrome), BrowserInstallationLifecycleState.Active, 7);
        var currentSession = new StoredManagedBrowserSession(
            CreateReference(BrowserFamily.Chrome), ManagedBrowserSessionLifecycleState.Active, 8);
        var retiredEdgeIdentity = new BrowserInstallationIdentity(
            Guid.Parse("21000000-0000-0000-0000-000000000001"), BrowserFamily.Edge,
            "C:\\Browsers\\Edge", "C:\\Browsers\\Edge\\msedge.exe",
            "Microsoft Corporation", "Microsoft Edge", "edge-file");
        var retiredEdge = new StoredBrowserInstallation(
            retiredEdgeIdentity, BrowserInstallationLifecycleState.Retired, 4);
        var observedEdge = new BrowserInstallationIdentity(
            Guid.Parse("22000000-0000-0000-0000-000000000001"), BrowserFamily.Edge,
            "c:\\browsers\\edge", "c:\\browsers\\edge\\msedge.exe",
            "Microsoft Corporation", "Microsoft Edge", "edge-file");
        var targetSession = new ManagedBrowserSessionReference(
            Guid.Parse("31000000-0000-0000-0000-000000000001"), observedEdge.InstallationId,
            AccountId, "stable-uid", BrowserFamily.Edge, "profile-2", "ownership-2");

        var request = new ManagedBrowserFamilySwitchTransitionRequest(
            currentInstallation, currentSession, observedEdge, targetSession, "stable-uid", retiredEdge);

        Assert.Same(retiredEdge, request.ExpectedTargetInstallation);
        Assert.Throws<ArgumentException>(() => new ManagedBrowserFamilySwitchTransitionRequest(
            currentInstallation, currentSession, observedEdge, targetSession, "stable-uid",
            new StoredBrowserInstallation(
                retiredEdgeIdentity, BrowserInstallationLifecycleState.Active, retiredEdge.Revision)));
        Assert.Throws<ArgumentException>(() => new ManagedBrowserFamilySwitchTransitionRequest(
            currentInstallation, currentSession, observedEdge, targetSession, "stable-uid",
            new StoredBrowserInstallation(
                new BrowserInstallationIdentity(
                    retiredEdgeIdentity.InstallationId, BrowserFamily.Edge,
                    retiredEdgeIdentity.InstallationDirectory, retiredEdgeIdentity.ExecutablePath,
                    retiredEdgeIdentity.Publisher, retiredEdgeIdentity.ProductName, "different-file"),
                BrowserInstallationLifecycleState.Retired,
                retiredEdge.Revision)));
    }

    [Theory]
    [InlineData(" ", "Google Chrome", "file-identity")]
    [InlineData(" Google LLC", "Google Chrome", "file-identity")]
    [InlineData("Google LLC ", "Google Chrome", "file-identity")]
    [InlineData("\tGoogle LLC", "Google Chrome", "file-identity")]
    [InlineData("Google LLC", "Google Chrome\n", "file-identity")]
    [InlineData("Google LLC", "Google Chrome", "file-identity ")]
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
    public void ManagedBrowserSessionReferenceAndSnapshotsRejectWhitespaceNearMisses()
    {
        Assert.Throws<ArgumentException>(() => new ManagedBrowserSessionReference(
            SessionId, InstallationId, AccountId, " stable-uid", BrowserFamily.Chrome,
            "profile-1", "ownership-1"));
        Assert.Throws<ArgumentException>(() => new ManagedBrowserSessionReference(
            SessionId, InstallationId, AccountId, "stable-uid\t", BrowserFamily.Chrome,
            "profile-1", "ownership-1"));
        Assert.Throws<ArgumentException>(() => new ManagedBrowserSessionReference(
            SessionId, InstallationId, AccountId, "stable-uid", BrowserFamily.Chrome,
            " profile-1", "ownership-1"));
        Assert.Throws<ArgumentException>(() => new ManagedBrowserSessionReference(
            SessionId, InstallationId, AccountId, "stable-uid", BrowserFamily.Chrome,
            "profile-1", "ownership-1\n"));
        Assert.Throws<ArgumentException>(() => new ExecutionProfileSnapshot(
            BaiduExecutionEngine.WebView2V1, BrowserFamily.LegacyWebView2, " legacy-profile"));
        Assert.Throws<ArgumentException>(() => new ExecutionProfileSnapshot(
            BaiduExecutionEngine.ManagedChromiumV1, BrowserFamily.Chrome, "profile-1\t", InstallationId, SessionId));
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
        Func<BrowserInstallationIdentity, CancellationToken, Task<StoredBrowserInstallation>> createInstallation = store.CreateInstallationAsync;
        Func<Guid, CancellationToken, Task<StoredBrowserInstallation?>> loadInstallation = store.LoadInstallationAsync;
        Func<string, CancellationToken, Task<StoredBrowserInstallation?>> loadInstallationByFileIdentity =
            store.LoadInstallationByFileIdentityAsync;
        Func<CancellationToken, Task<StoredBrowserInstallation?>> loadActiveInstallation = store.LoadActiveInstallationAsync;
        Func<ManagedBrowserInstallationActivationRequest, CancellationToken, Task<ManagedBrowserActiveSlotTransitionResult<StoredBrowserInstallation>>> activateInstallation = store.TryReplaceActiveInstallationAsync;
        Func<ManagedBrowserTransitionRequest, CancellationToken, Task<ManagedBrowserTransitionResult>> retireInstallation = store.TryRetireInstallationAsync;
        Func<ManagedBrowserSessionReference, CancellationToken, Task<StoredManagedBrowserSession>> createSession = store.CreateSessionAsync;
        Func<Guid, CancellationToken, Task<StoredManagedBrowserSession?>> loadSession = store.LoadSessionAsync;
        Func<Guid, BrowserFamily, CancellationToken, Task<StoredManagedBrowserSession?>> loadActiveSession = store.LoadActiveSessionAsync;
        Func<ManagedBrowserSessionActivationRequest, CancellationToken, Task<ManagedBrowserActiveSlotTransitionResult<StoredManagedBrowserSession>>> activateSession = store.TryReplaceActiveSessionAsync;
        Func<ManagedBrowserTransitionRequest, CancellationToken, Task<ManagedBrowserTransitionResult>> retireSession = store.TryRetireSessionAsync;
        Func<ManagedBrowserCleanupClaimRequest, CancellationToken, Task<ManagedBrowserTransitionResult>> claimCleanup =
            store.TryClaimRetiredIdentityForCleanupAsync;
        Func<ManagedBrowserRetentionReference, CancellationToken, Task> addRetention = store.AddRetentionAsync;
        Func<ManagedBrowserRetentionReference, CancellationToken, Task> releaseRetention = store.ReleaseRetentionAsync;
        Func<Guid, Guid, CancellationToken, Task<bool>> eligible = store.IsRetirementEligibleAsync;

        Assert.NotNull(createInstallation);
        Assert.NotNull(loadInstallation);
        Assert.NotNull(loadInstallationByFileIdentity);
        Assert.NotNull(loadActiveInstallation);
        Assert.NotNull(activateInstallation);
        Assert.NotNull(retireInstallation);
        Assert.NotNull(createSession);
        Assert.NotNull(loadSession);
        Assert.NotNull(loadActiveSession);
        Assert.NotNull(activateSession);
        Assert.NotNull(retireSession);
        Assert.NotNull(claimCleanup);
        Assert.NotNull(addRetention);
        Assert.NotNull(releaseRetention);
        Assert.NotNull(eligible);
    }

    [Fact]
    public void CleanupManifestContractExposesCreateLoadAndCasWithoutLeakingProfilePaths()
    {
        var manifest = new ManagedProfileCleanupManifest(
            Guid.NewGuid(), AccountId, SessionId,
            "C:\\Sensitive\\profile", "C:\\Sensitive\\quarantine", "ownership-1",
            null);
        var stored = new StoredManagedProfileCleanupManifest(
            manifest, ManagedProfileCleanupState.Prepared, revision: 0);
        var transition = new ManagedProfileCleanupTransitionRequest(
            manifest.ManifestId, expectedRevision: 0,
            ManagedProfileCleanupState.Prepared,
            ManagedProfileCleanupState.QuarantineMoveIntent);
        IManagedBrowserStateStore store = new ContractStore();
        Func<ManagedProfileCleanupManifest, CancellationToken, Task<StoredManagedProfileCleanupManifest>> create =
            store.CreateCleanupManifestAsync;
        Func<Guid, CancellationToken, Task<StoredManagedProfileCleanupManifest?>> load =
            store.LoadCleanupManifestAsync;
        Func<ManagedProfileCleanupTransitionRequest, CancellationToken, Task<ManagedBrowserTransitionResult>> change =
            store.TryTransitionCleanupManifestAsync;

        Assert.Equal(ManagedProfileCleanupState.Prepared, stored.State);
        Assert.Equal(0, stored.Revision);
        Assert.Equal(ManagedProfileCleanupState.QuarantineMoveIntent, transition.TargetState);
        Assert.DoesNotContain("Sensitive", manifest.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ownership-1", manifest.ToString(), StringComparison.Ordinal);
        Assert.NotNull(create);
        Assert.NotNull(load);
        Assert.NotNull(change);
        Assert.Throws<ArgumentException>(() => new ManagedProfileCleanupManifest(
            Guid.Empty, AccountId, SessionId, "source", "quarantine", "owner", null));
        Assert.Throws<ArgumentException>(() => new ManagedProfileCleanupTransitionRequest(
            manifest.ManifestId, 0, ManagedProfileCleanupState.Prepared,
            ManagedProfileCleanupState.Prepared));
    }

    [Theory]
    [InlineData(ManagedProfileCleanupState.Prepared, ManagedProfileCleanupState.QuarantineMoveIntent, null)]
    [InlineData(ManagedProfileCleanupState.QuarantineMoveIntent, ManagedProfileCleanupState.Quarantined, null)]
    [InlineData(ManagedProfileCleanupState.QuarantineMoveIntent, ManagedProfileCleanupState.CleanupPending, null)]
    [InlineData(ManagedProfileCleanupState.Quarantined, ManagedProfileCleanupState.DeleteIntent, "confirmation-hash")]
    [InlineData(ManagedProfileCleanupState.DeleteIntent, ManagedProfileCleanupState.Deleted, null)]
    [InlineData(ManagedProfileCleanupState.DeleteIntent, ManagedProfileCleanupState.CleanupPending, null)]
    public void CleanupManifestTransitionAllowsOnlyApprovedCrashSafeEdges(
        ManagedProfileCleanupState expected,
        ManagedProfileCleanupState target,
        string? confirmationTokenHash)
    {
        var request = new ManagedProfileCleanupTransitionRequest(
            Guid.NewGuid(), 4, expected, target, confirmationTokenHash);

        Assert.Equal(expected, request.ExpectedState);
        Assert.Equal(target, request.TargetState);
        Assert.Equal(confirmationTokenHash, request.ConfirmationTokenHash);
    }

    [Theory]
    [InlineData(ManagedProfileCleanupState.Prepared, ManagedProfileCleanupState.Quarantined, null)]
    [InlineData(ManagedProfileCleanupState.Prepared, ManagedProfileCleanupState.Deleted, null)]
    [InlineData(ManagedProfileCleanupState.QuarantineMoveIntent, ManagedProfileCleanupState.DeleteIntent, "confirmation-hash")]
    [InlineData(ManagedProfileCleanupState.Quarantined, ManagedProfileCleanupState.QuarantineMoveIntent, null)]
    [InlineData(ManagedProfileCleanupState.Quarantined, ManagedProfileCleanupState.Deleted, null)]
    [InlineData(ManagedProfileCleanupState.Quarantined, ManagedProfileCleanupState.DeleteIntent, null)]
    [InlineData(ManagedProfileCleanupState.Quarantined, ManagedProfileCleanupState.DeleteIntent, "")]
    [InlineData(ManagedProfileCleanupState.DeleteIntent, ManagedProfileCleanupState.Quarantined, null)]
    [InlineData(ManagedProfileCleanupState.CleanupPending, ManagedProfileCleanupState.Deleted, null)]
    [InlineData(ManagedProfileCleanupState.Deleted, ManagedProfileCleanupState.Prepared, null)]
    [InlineData(ManagedProfileCleanupState.Prepared, ManagedProfileCleanupState.QuarantineMoveIntent, "unexpected-hash")]
    public void CleanupManifestTransitionRejectsSkippedBackwardAndUnconfirmedEdges(
        ManagedProfileCleanupState expected,
        ManagedProfileCleanupState target,
        string? confirmationTokenHash)
    {
        Assert.Throws<ArgumentException>(() => new ManagedProfileCleanupTransitionRequest(
            Guid.NewGuid(), 0, expected, target, confirmationTokenHash));
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
        Assert.Throws<ArgumentException>(() => ManagedBrowserTransitionResult.Conflict(null));
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

        var claim = new ManagedBrowserCleanupClaimRequest(
            InstallationId, SessionId, AccountId, expectedInstallationRevision: 3,
            expectedSessionRevision: 4, Guid.NewGuid(), expectedManifestRevision: 5);
        Assert.Equal(InstallationId, claim.InstallationId);
        Assert.Equal(SessionId, claim.SessionId);
        Assert.Equal(AccountId, claim.AccountId);
        Assert.Equal(3, claim.ExpectedInstallationRevision);
        Assert.Equal(4, claim.ExpectedSessionRevision);
        Assert.Equal(5, claim.ExpectedManifestRevision);
        Assert.DoesNotContain(InstallationId.ToString(), claim.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SessionId.ToString(), claim.ToString(), StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ManagedBrowserCleanupClaimRequest(
            InstallationId, SessionId, AccountId, -1, 0, Guid.NewGuid(), 0));
    }

    private static BrowserInstallationIdentity CreateInstallation(BrowserFamily family) => new(
        InstallationId,
        family,
        family == BrowserFamily.Chrome ? "C:\\Browsers\\Chrome" : "C:\\Browsers\\Edge",
        family == BrowserFamily.Chrome ? "C:\\Browsers\\Chrome\\chrome.exe" : "C:\\Browsers\\Edge\\msedge.exe",
        family == BrowserFamily.Chrome ? "Google LLC" : "Microsoft Corporation",
        family == BrowserFamily.Chrome ? "Google Chrome" : "Microsoft Edge",
        "file-identity");

    private static ManagedBrowserSessionReference CreateReference(BrowserFamily family) => new(
        SessionId, InstallationId, AccountId, "stable-uid", family, "profile-1", "ownership-1");

    private sealed class ContractStore : IManagedBrowserStateStore
    {
        public Task<StoredBrowserInstallation> CreateInstallationAsync(BrowserInstallationIdentity installation, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<StoredBrowserInstallation?> LoadInstallationAsync(Guid installationId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<StoredBrowserInstallation?> LoadInstallationByFileIdentityAsync(string fileIdentity, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<StoredBrowserInstallation?> LoadActiveInstallationAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ManagedBrowserActiveSlotTransitionResult<StoredBrowserInstallation>> TryReplaceActiveInstallationAsync(ManagedBrowserInstallationActivationRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ManagedBrowserTransitionResult> TryRetireInstallationAsync(ManagedBrowserTransitionRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<StoredManagedBrowserSession> CreateSessionAsync(ManagedBrowserSessionReference session, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<StoredManagedBrowserSession?> LoadSessionAsync(Guid sessionId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<StoredManagedBrowserSession?> LoadActiveSessionAsync(Guid accountId, BrowserFamily browserFamily, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ManagedBrowserActiveSlotTransitionResult<StoredManagedBrowserSession>> TryReplaceActiveSessionAsync(ManagedBrowserSessionActivationRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ManagedBrowserTransitionResult> TryRetireSessionAsync(ManagedBrowserTransitionRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ManagedBrowserTransitionResult> TryClaimRetiredIdentityForCleanupAsync(ManagedBrowserCleanupClaimRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<StoredManagedProfileCleanupManifest> CreateCleanupManifestAsync(ManagedProfileCleanupManifest manifest, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<StoredManagedProfileCleanupManifest?> LoadCleanupManifestAsync(Guid manifestId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ManagedBrowserTransitionResult> TryTransitionCleanupManifestAsync(ManagedProfileCleanupTransitionRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task AddRetentionAsync(ManagedBrowserRetentionReference reference, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task ReleaseRetentionAsync(ManagedBrowserRetentionReference reference, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<bool> IsRetirementEligibleAsync(Guid installationId, Guid sessionId, CancellationToken cancellationToken) => throw new NotImplementedException();
    }
}
