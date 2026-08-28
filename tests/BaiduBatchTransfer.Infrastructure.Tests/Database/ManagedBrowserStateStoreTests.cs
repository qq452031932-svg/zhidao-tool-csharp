using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Tests.Database;

public sealed class ManagedBrowserStateStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bbt-managed-browser-{Guid.NewGuid():N}");

    [Fact]
    public async Task InstallationCreateAndActiveReplacementPreserveRetiredIdentityHistory()
    {
        var (factory, store) = await CreateStoreAsync();
        var chrome = Installation(BrowserFamily.Chrome, "chrome-one", "file-one");
        var edge = Installation(BrowserFamily.Edge, "edge-two", "file-two");
        var nextChrome = Installation(BrowserFamily.Chrome, "chrome-three", "file-three");

        var createdChrome = await store.CreateInstallationAsync(chrome, default);
        Assert.Equal(BrowserInstallationLifecycleState.Retired, createdChrome.State);
        Assert.Equal(0, createdChrome.Revision);
        Assert.Same(chrome, createdChrome.Identity);
        Assert.Equal(chrome.ExecutablePath,
            (await store.LoadInstallationAsync(chrome.InstallationId, default))!.Identity.ExecutablePath);

        var activatedChrome = await store.TryReplaceActiveInstallationAsync(
            new ManagedBrowserInstallationActivationRequest(null, createdChrome), default);
        Assert.Equal(ManagedBrowserTransitionStatus.Applied, activatedChrome.Status);
        Assert.Equal(BrowserInstallationLifecycleState.Active, activatedChrome.Current!.State);
        Assert.Equal(1, activatedChrome.Current.Revision);

        var createdEdge = await store.CreateInstallationAsync(edge, default);
        var activatedEdge = await store.TryReplaceActiveInstallationAsync(
            new ManagedBrowserInstallationActivationRequest(activatedChrome.Current, createdEdge), default);
        Assert.Equal(ManagedBrowserTransitionStatus.Applied, activatedEdge.Status);
        Assert.Equal(edge.InstallationId, activatedEdge.Current!.Identity.InstallationId);
        Assert.Equal(edge.ExecutablePath, (await store.LoadActiveInstallationAsync(default))!.Identity.ExecutablePath);

        var retiredChrome = await store.LoadInstallationAsync(chrome.InstallationId, default);
        Assert.NotNull(retiredChrome);
        Assert.Equal(BrowserInstallationLifecycleState.Retired, retiredChrome.State);
        Assert.Equal(2, retiredChrome.Revision);
        Assert.Equal(chrome.InstallationDirectory, retiredChrome.Identity.InstallationDirectory);
        Assert.Equal(chrome.FileIdentity, retiredChrome.Identity.FileIdentity);

        var createdNextChrome = await store.CreateInstallationAsync(nextChrome, default);
        var stale = await store.TryReplaceActiveInstallationAsync(
            new ManagedBrowserInstallationActivationRequest(activatedChrome.Current, createdNextChrome), default);
        Assert.Equal(ManagedBrowserTransitionStatus.Conflict, stale.Status);
        Assert.Equal(edge.InstallationId, stale.Current!.Identity.InstallationId);
        Assert.Equal(BrowserInstallationLifecycleState.Retired,
            (await store.LoadInstallationAsync(nextChrome.InstallationId, default))!.State);

        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM BrowserInstallations;";
        Assert.Equal(3L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task SessionReplacementIsAccountAndFamilyScopedAndTombstonedLookupFailsClosed()
    {
        var (factory, store) = await CreateStoreAsync();
        var accountOne = Guid.NewGuid();
        var accountTwo = Guid.NewGuid();
        await SeedAccountAsync(factory, accountOne, "uid-one");
        await SeedAccountAsync(factory, accountTwo, "uid-two");
        var installation = await store.CreateInstallationAsync(
            Installation(BrowserFamily.Chrome, "chrome-session", "session-file"), default);
        installation = (await store.TryReplaceActiveInstallationAsync(
            new ManagedBrowserInstallationActivationRequest(null, installation), default)).Current!;
        var firstIdentity = Session(
            installation.Identity.InstallationId, accountOne, "uid-one", BrowserFamily.Chrome,
            "profile-one", "ownership-one");
        var secondIdentity = Session(
            installation.Identity.InstallationId, accountOne, "uid-one", BrowserFamily.Chrome,
            "profile-two", "ownership-two");
        var otherAccountIdentity = Session(
            installation.Identity.InstallationId, accountTwo, "uid-two", BrowserFamily.Chrome,
            "profile-three", "ownership-three");

        var first = await store.CreateSessionAsync(firstIdentity, default);
        Assert.Equal(ManagedBrowserSessionLifecycleState.Retired, first.State);
        Assert.Equal(0, first.Revision);
        var firstActive = (await store.TryReplaceActiveSessionAsync(
            new ManagedBrowserSessionActivationRequest(null, first), default)).Current!;
        Assert.Equal(ManagedBrowserSessionLifecycleState.Active, firstActive.State);
        Assert.Equal(1, firstActive.Revision);

        var second = await store.CreateSessionAsync(secondIdentity, default);
        var replaced = await store.TryReplaceActiveSessionAsync(
            new ManagedBrowserSessionActivationRequest(firstActive, second), default);
        Assert.Equal(ManagedBrowserTransitionStatus.Applied, replaced.Status);
        Assert.Equal(secondIdentity.SessionId, replaced.Current!.Identity.SessionId);
        Assert.Equal("profile-two",
            (await store.LoadActiveSessionAsync(accountOne, BrowserFamily.Chrome, default))!.Identity.ProfileId);
        var retired = await store.LoadSessionAsync(firstIdentity.SessionId, default);
        Assert.Equal(ManagedBrowserSessionLifecycleState.Retired, retired!.State);
        Assert.Equal(2, retired.Revision);
        Assert.Equal("profile-one", retired.Identity.ProfileId);
        Assert.Equal("ownership-one", retired.Identity.ProfileOwnershipId);

        var other = await store.CreateSessionAsync(otherAccountIdentity, default);
        var otherActive = await store.TryReplaceActiveSessionAsync(
            new ManagedBrowserSessionActivationRequest(null, other), default);
        Assert.Equal(ManagedBrowserTransitionStatus.Applied, otherActive.Status);
        Assert.Equal(otherAccountIdentity.SessionId,
            (await store.LoadActiveSessionAsync(accountTwo, BrowserFamily.Chrome, default))!.Identity.SessionId);
        Assert.Equal(secondIdentity.SessionId,
            (await store.LoadActiveSessionAsync(accountOne, BrowserFamily.Chrome, default))!.Identity.SessionId);

        await using (var connection = await factory.OpenConnectionAsync())
        {
            await using var tombstone = connection.CreateCommand();
            tombstone.CommandText = """
                UPDATE Accounts SET Status='Deleted',IsEnabled=0,IsDeleted=1,DeletedAt=$now
                WHERE Id=$id;
                """;
            tombstone.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            tombstone.Parameters.AddWithValue("$id", accountOne.ToString("D"));
            await tombstone.ExecuteNonQueryAsync();
        }

        Assert.Null(await store.LoadActiveSessionAsync(accountOne, BrowserFamily.Chrome, default));
        Assert.Equal(ManagedBrowserSessionLifecycleState.Active,
            (await store.LoadSessionAsync(secondIdentity.SessionId, default))!.State);
    }

    [Fact]
    public async Task ActiveSessionLookupFailsClosedAfterTheAccountStableUidChanges()
    {
        var (factory, store) = await CreateStoreAsync();
        var accountId = Guid.NewGuid();
        await SeedAccountAsync(factory, accountId, "uid-before");
        var installation = await store.CreateInstallationAsync(
            Installation(BrowserFamily.Chrome, "chrome-uid-change", "uid-change-file"), default);
        installation = (await store.TryReplaceActiveInstallationAsync(
            new ManagedBrowserInstallationActivationRequest(null, installation), default)).Current!;
        var identity = Session(
            installation.Identity.InstallationId, accountId, "uid-before", BrowserFamily.Chrome,
            "profile-uid-change", "ownership-uid-change");
        var session = await store.CreateSessionAsync(identity, default);
        session = (await store.TryReplaceActiveSessionAsync(
            new ManagedBrowserSessionActivationRequest(null, session), default)).Current!;
        Assert.Equal(identity.SessionId,
            (await store.LoadActiveSessionAsync(accountId, BrowserFamily.Chrome, default))!.Identity.SessionId);

        await using (var connection = await factory.OpenConnectionAsync())
        {
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE Accounts SET BaiduUserId='uid-after' WHERE Id=$account;";
            update.Parameters.AddWithValue("$account", accountId.ToString("D"));
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        Assert.Null(await store.LoadActiveSessionAsync(accountId, BrowserFamily.Chrome, default));
        Assert.Equal("uid-before", (await store.LoadSessionAsync(identity.SessionId, default))!.Identity.StableBaiduUserId);
    }

    [Fact]
    public async Task SessionUidWriteGuardAndActiveLookupFailClosedForPersistedUidCorruption()
    {
        var (factory, store) = await CreateStoreAsync();
        var accountId = Guid.NewGuid();
        var otherAccountId = Guid.NewGuid();
        await SeedAccountAsync(factory, accountId, "uid-valid");
        await SeedAccountAsync(factory, otherAccountId, "uid-other");
        var installation = await store.CreateInstallationAsync(
            Installation(BrowserFamily.Chrome, "chrome-uid-corrupt", "uid-corrupt-file"), default);
        installation = (await store.TryReplaceActiveInstallationAsync(
            new ManagedBrowserInstallationActivationRequest(null, installation), default)).Current!;
        var identity = Session(
            installation.Identity.InstallationId, accountId, "uid-valid", BrowserFamily.Chrome,
            "profile-uid-corrupt", "ownership-uid-corrupt");
        var session = await store.CreateSessionAsync(identity, default);
        _ = await store.TryReplaceActiveSessionAsync(
            new ManagedBrowserSessionActivationRequest(null, session), default);

        await using (var connection = await factory.OpenConnectionAsync())
        {
            await using var rejected = connection.CreateCommand();
            rejected.CommandText = "UPDATE ManagedBrowserSessions SET BaiduUserId='uid-corrupt' WHERE SessionId=$session;";
            rejected.Parameters.AddWithValue("$session", identity.SessionId.ToString("D"));
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => rejected.ExecuteNonQueryAsync());

            await using var rebound = connection.CreateCommand();
            rebound.CommandText = """
                UPDATE ManagedBrowserSessions SET AccountId=$account,BaiduUserId='uid-other'
                WHERE SessionId=$session;
                """;
            rebound.Parameters.AddWithValue("$account", otherAccountId.ToString("D"));
            rebound.Parameters.AddWithValue("$session", identity.SessionId.ToString("D"));
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => rebound.ExecuteNonQueryAsync());

            await using var corrupt = connection.CreateCommand();
            corrupt.CommandText = """
                DROP TRIGGER TR_ManagedBrowserSessions_IdentityImmutable;
                UPDATE ManagedBrowserSessions SET BaiduUserId='uid-corrupt' WHERE SessionId=$session;
                """;
            corrupt.Parameters.AddWithValue("$session", identity.SessionId.ToString("D"));
            Assert.Equal(1, await corrupt.ExecuteNonQueryAsync());
        }

        Assert.Null(await store.LoadActiveSessionAsync(accountId, BrowserFamily.Chrome, default));
    }

    [Fact]
    public async Task RetentionRequiresRealOwnersAndHistoricalOwnersKeepRetiredIdentityIneligible()
    {
        var (factory, store) = await CreateStoreAsync();
        var accountId = Guid.NewGuid();
        await SeedAccountAsync(factory, accountId, "uid-retention");
        var installation = await store.CreateInstallationAsync(
            Installation(BrowserFamily.Chrome, "chrome-retention", "retention-file"), default);
        installation = (await store.TryReplaceActiveInstallationAsync(
            new ManagedBrowserInstallationActivationRequest(null, installation), default)).Current!;
        var sessionIdentity = Session(
            installation.Identity.InstallationId, accountId, "uid-retention", BrowserFamily.Chrome,
            "profile-retention", "ownership-retention");
        var session = await store.CreateSessionAsync(sessionIdentity, default);
        session = (await store.TryReplaceActiveSessionAsync(
            new ManagedBrowserSessionActivationRequest(null, session), default)).Current!;

        var arbitrary = new ManagedBrowserRetentionReference(
            ManagedBrowserRetentionOwnerKind.Operation, Guid.NewGuid(),
            installation.Identity.InstallationId, sessionIdentity.SessionId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddRetentionAsync(arbitrary, default));

        var (operationId, manifest) = await SeedManagedOwnersAsync(
            factory, store, accountId, installation.Identity.InstallationId, sessionIdentity);
        Assert.Equal(3L, await CountRetentionsAsync(factory));

        Assert.False(await store.IsRetirementEligibleAsync(
            installation.Identity.InstallationId, sessionIdentity.SessionId, default));
        var retiredSession = await store.TryRetireSessionAsync(
            new ManagedBrowserTransitionRequest(sessionIdentity.SessionId, session.Revision), default);
        Assert.Equal(ManagedBrowserTransitionStatus.Applied, retiredSession.Status);
        Assert.Equal(2, retiredSession.CurrentRevision);
        var staleSession = await store.TryRetireSessionAsync(
            new ManagedBrowserTransitionRequest(sessionIdentity.SessionId, session.Revision), default);
        Assert.Equal(ManagedBrowserTransitionStatus.Conflict, staleSession.Status);
        Assert.Equal(2, staleSession.CurrentRevision);

        var retiredInstallation = await store.TryRetireInstallationAsync(
            new ManagedBrowserTransitionRequest(installation.Identity.InstallationId, installation.Revision), default);
        Assert.Equal(ManagedBrowserTransitionStatus.Applied, retiredInstallation.Status);
        Assert.Equal(2, retiredInstallation.CurrentRevision);
        Assert.False(await store.IsRetirementEligibleAsync(
            installation.Identity.InstallationId, sessionIdentity.SessionId, default));

        var operationRetention = new ManagedBrowserRetentionReference(
            ManagedBrowserRetentionOwnerKind.Operation, operationId,
            installation.Identity.InstallationId, sessionIdentity.SessionId);
        var attemptRetention = new ManagedBrowserRetentionReference(
            ManagedBrowserRetentionOwnerKind.Attempt, operationId,
            installation.Identity.InstallationId, sessionIdentity.SessionId);
        var manifestRetention = new ManagedBrowserRetentionReference(
            ManagedBrowserRetentionOwnerKind.CleanupManifest, manifest.Manifest.ManifestId,
            installation.Identity.InstallationId, sessionIdentity.SessionId);
        await store.ReleaseRetentionAsync(operationRetention, default);
        await store.ReleaseRetentionAsync(attemptRetention, default);
        await store.ReleaseRetentionAsync(manifestRetention, default);
        Assert.Equal(0L, await CountRetentionsAsync(factory));
        Assert.False(await store.IsRetirementEligibleAsync(
            installation.Identity.InstallationId, sessionIdentity.SessionId, default));

        await store.AddRetentionAsync(operationRetention, default);
        await store.AddRetentionAsync(attemptRetention, default);
        await store.AddRetentionAsync(manifestRetention, default);
        Assert.Equal(3L, await CountRetentionsAsync(factory));

        await DeleteManagedOperationAndAttemptAsync(factory, operationId);
        Assert.Equal(1L, await CountRetentionsAsync(factory));
        Assert.False(await store.IsRetirementEligibleAsync(
            installation.Identity.InstallationId, sessionIdentity.SessionId, default));

        var retiredInstallationState = (await store.LoadInstallationAsync(
            installation.Identity.InstallationId, default))!;
        var retiredSessionState = (await store.LoadSessionAsync(sessionIdentity.SessionId, default))!;
        Assert.Equal(ManagedBrowserTransitionStatus.Applied,
            (await store.TryClaimRetiredIdentityForCleanupAsync(
                new ManagedBrowserCleanupClaimRequest(
                    retiredInstallationState.Identity.InstallationId,
                    retiredSessionState.Identity.SessionId,
                    accountId,
                    retiredInstallationState.Revision,
                    retiredSessionState.Revision,
                    manifest.Manifest.ManifestId,
                    manifest.Revision), default)).Status);
        var current = (await store.LoadCleanupManifestAsync(manifest.Manifest.ManifestId, default))!;
        foreach (var target in new[]
                 {
                     ManagedProfileCleanupState.Quarantined,
                     ManagedProfileCleanupState.DeleteIntent,
                     ManagedProfileCleanupState.Deleted,
                 })
        {
            var transition = await store.TryTransitionCleanupManifestAsync(
                new ManagedProfileCleanupTransitionRequest(
                    current.Manifest.ManifestId, current.Revision, current.State, target,
                    target == ManagedProfileCleanupState.DeleteIntent ? "confirmed-retention-delete" : null), default);
            Assert.Equal(ManagedBrowserTransitionStatus.Applied, transition.Status);
            current = (await store.LoadCleanupManifestAsync(current.Manifest.ManifestId, default))!;
        }
        Assert.Equal(0L, await CountRetentionsAsync(factory));
        Assert.False(await store.IsRetirementEligibleAsync(
            installation.Identity.InstallationId, sessionIdentity.SessionId, default));

        Assert.Equal(ManagedBrowserTransitionStatus.NotFound,
            (await store.TryRetireInstallationAsync(
                new ManagedBrowserTransitionRequest(Guid.NewGuid(), 0), default)).Status);
    }

    [Fact]
    public async Task CleanupManifestScopePersistsAcrossRestartAndTransitionsWithCas()
    {
        var (factory, store) = await CreateStoreAsync();
        var accountId = Guid.NewGuid();
        var otherAccountId = Guid.NewGuid();
        await SeedAccountAsync(factory, accountId, "uid-cleanup");
        await SeedAccountAsync(factory, otherAccountId, "uid-other-cleanup");
        var installation = await store.CreateInstallationAsync(
            Installation(BrowserFamily.Edge, "edge-cleanup", "cleanup-file"), default);
        installation = (await store.TryReplaceActiveInstallationAsync(
            new ManagedBrowserInstallationActivationRequest(null, installation), default)).Current!;
        var sessionIdentity = Session(
            installation.Identity.InstallationId, accountId, "uid-cleanup", BrowserFamily.Edge,
            "profile-cleanup", "ownership-cleanup");
        var session = await store.CreateSessionAsync(sessionIdentity, default);
        session = (await store.TryReplaceActiveSessionAsync(
            new ManagedBrowserSessionActivationRequest(null, session), default)).Current!;

        var wrongAccount = CleanupManifest(otherAccountId, sessionIdentity, "wrong-account");
        var wrongAccountFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateCleanupManifestAsync(wrongAccount, default));
        Assert.DoesNotContain(wrongAccount.SourceProfilePath, wrongAccountFailure.ToString(), StringComparison.Ordinal);

        var wrongOwnership = new ManagedProfileCleanupManifest(
            Guid.NewGuid(), accountId, sessionIdentity.SessionId,
            Path.Combine(root, "source-secret-wrong-owner"),
            Path.Combine(root, "quarantine-secret-wrong-owner"), "not-the-session-owner", null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateCleanupManifestAsync(wrongOwnership, default));

        var manifest = CleanupManifest(accountId, sessionIdentity, "valid");
        var created = await store.CreateCleanupManifestAsync(manifest, default);
        Assert.Equal(ManagedProfileCleanupState.Prepared, created.State);
        Assert.Equal(0, created.Revision);

        var restarted = new SqliteManagedBrowserStateStore(factory);
        var loaded = await restarted.LoadCleanupManifestAsync(manifest.ManifestId, default);
        Assert.NotNull(loaded);
        Assert.Equal(manifest.ManifestId, loaded.Manifest.ManifestId);
        Assert.Equal(manifest.SourceProfilePath, loaded.Manifest.SourceProfilePath);
        var byAccount = await restarted.LoadLatestCleanupManifestAsync(accountId, default);
        Assert.Equal(manifest.ManifestId, byAccount!.Manifest.ManifestId);
        Assert.Null(await restarted.LoadLatestCleanupManifestAsync(otherAccountId, default));

        Assert.Equal(ManagedBrowserTransitionStatus.Applied,
            (await store.TryRetireSessionAsync(
                new ManagedBrowserTransitionRequest(session.Identity.SessionId, session.Revision), default)).Status);
        Assert.Equal(ManagedBrowserTransitionStatus.Applied,
            (await store.TryRetireInstallationAsync(
                new ManagedBrowserTransitionRequest(
                    installation.Identity.InstallationId, installation.Revision), default)).Status);
        session = (await restarted.LoadSessionAsync(session.Identity.SessionId, default))!;
        installation = (await restarted.LoadInstallationAsync(
            installation.Identity.InstallationId, default))!;
        var request = new ManagedBrowserCleanupClaimRequest(
            installation.Identity.InstallationId, session.Identity.SessionId, accountId,
            installation.Revision, session.Revision, manifest.ManifestId, created.Revision);
        var applied = await restarted.TryClaimRetiredIdentityForCleanupAsync(request, default);
        Assert.Equal(ManagedBrowserTransitionStatus.Applied, applied.Status);
        Assert.Equal(1, applied.CurrentRevision);
        var stale = await restarted.TryClaimRetiredIdentityForCleanupAsync(request, default);
        Assert.Equal(ManagedBrowserTransitionStatus.Conflict, stale.Status);
        Assert.Equal(1, stale.CurrentRevision);
        Assert.Equal(ManagedProfileCleanupState.QuarantineMoveIntent,
            (await restarted.LoadCleanupManifestAsync(manifest.ManifestId, default))!.State);
        Assert.Equal(ManagedBrowserTransitionStatus.NotFound,
            (await restarted.TryClaimRetiredIdentityForCleanupAsync(
                new ManagedBrowserCleanupClaimRequest(
                    installation.Identity.InstallationId, session.Identity.SessionId, accountId,
                    installation.Revision, session.Revision, Guid.NewGuid(), 0), default)).Status);
    }

    [Fact]
    public async Task CleanupClaimAtomicallyFencesTheExactRetiredIdentityPair()
    {
        var fixture = await CreateRetiredCleanupFixtureAsync("claim");

        var claimed = await fixture.Store.TryClaimRetiredIdentityForCleanupAsync(
            fixture.ClaimRequest(), default);

        Assert.Equal(ManagedBrowserTransitionStatus.Applied, claimed.Status);
        Assert.Equal(1, claimed.CurrentRevision);
        Assert.Equal(ManagedProfileCleanupState.QuarantineMoveIntent,
            (await fixture.Store.LoadCleanupManifestAsync(fixture.Manifest.Manifest.ManifestId, default))!.State);
        var session = await fixture.Store.LoadSessionAsync(fixture.Session.Identity.SessionId, default);
        Assert.Equal(ManagedBrowserSessionLifecycleState.QuarantinePending, session!.State);
        Assert.Equal(fixture.Session.Revision + 1, session.Revision);
        Assert.Equal(1L, await ScalarLongAsync(fixture.Factory,
            "SELECT COUNT(*) FROM ManagedBrowserCleanupClaims WHERE ManifestId=$manifest;",
            ("$manifest", fixture.Manifest.Manifest.ManifestId.ToString("D"))));
    }

    [Fact]
    public async Task CleanupClaimAllowsRetiredSessionOnSharedActiveInstallation()
    {
        var fixture = await CreateRetiredCleanupFixtureAsync("shared-active-installation", retireInstallation: false);
        var otherAccountId = Guid.NewGuid();
        await SeedAccountAsync(fixture.Factory, otherAccountId, "uid-shared-active-other");
        var otherIdentity = Session(
            fixture.Installation.Identity.InstallationId, otherAccountId, "uid-shared-active-other",
            BrowserFamily.Chrome, "profile-shared-active-other", "ownership-shared-active-other");
        var otherSession = await fixture.Store.CreateSessionAsync(otherIdentity, default);
        otherSession = (await fixture.Store.TryReplaceActiveSessionAsync(
            new ManagedBrowserSessionActivationRequest(null, otherSession), default)).Current!;

        var claimed = await fixture.Store.TryClaimRetiredIdentityForCleanupAsync(
            fixture.ClaimRequest(), default);

        Assert.Equal(ManagedBrowserTransitionStatus.Applied, claimed.Status);
        Assert.Equal(BrowserInstallationLifecycleState.Active,
            (await fixture.Store.LoadInstallationAsync(
                fixture.Installation.Identity.InstallationId, default))!.State);
        Assert.Equal(ManagedBrowserSessionLifecycleState.Active,
            (await fixture.Store.LoadSessionAsync(otherIdentity.SessionId, default))!.State);
        var otherFixture = fixture with
        {
            AccountId = otherAccountId,
            Session = otherSession,
        };
        _ = await InsertManagedTransferOwnerAsync(otherFixture);
    }

    [Fact]
    public async Task PreparedCleanupManifestRejectsPrematureConfirmationEvidence()
    {
        var (factory, store) = await CreateStoreAsync();
        var accountId = Guid.NewGuid();
        await SeedAccountAsync(factory, accountId, "uid-premature-confirmation");
        var installation = await store.CreateInstallationAsync(
            Installation(BrowserFamily.Chrome, "chrome-premature-confirmation", "file-premature-confirmation"),
            default);
        var session = Session(
            installation.Identity.InstallationId, accountId, "uid-premature-confirmation",
            BrowserFamily.Chrome, "profile-premature-confirmation", "owner-premature-confirmation");
        _ = await store.CreateSessionAsync(session, default);
        var manifest = new ManagedProfileCleanupManifest(
            Guid.NewGuid(), accountId, session.SessionId,
            Path.Combine(root, "source-premature-confirmation"),
            Path.Combine(root, "quarantine-premature-confirmation"),
            session.ProfileOwnershipId, "premature-confirmation-hash");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateCleanupManifestAsync(manifest, default));
    }

    [Fact]
    public async Task CleanupClaimConflictsWithExistingOwnerWithoutPartiallyUpdatingState()
    {
        var fixture = await CreateRetiredCleanupFixtureAsync(
            "owner-conflict",
            beforeRetirement: async activeFixture =>
            {
                _ = await InsertManagedTransferOwnerAsync(activeFixture);
            });

        var claimed = await fixture.Store.TryClaimRetiredIdentityForCleanupAsync(
            fixture.ClaimRequest(), default);

        Assert.Equal(ManagedBrowserTransitionStatus.Conflict, claimed.Status);
        Assert.Equal(ManagedProfileCleanupState.Prepared,
            (await fixture.Store.LoadCleanupManifestAsync(fixture.Manifest.Manifest.ManifestId, default))!.State);
        Assert.Equal(ManagedBrowserSessionLifecycleState.Retired,
            (await fixture.Store.LoadSessionAsync(fixture.Session.Identity.SessionId, default))!.State);
        Assert.Equal(0L, await ScalarLongAsync(fixture.Factory,
            "SELECT COUNT(*) FROM ManagedBrowserCleanupClaims;"));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task CleanupClaimRejectsStaleIdentityRevisionWithoutPartialUpdates(
        bool staleInstallation,
        bool staleSession,
        bool staleManifest)
    {
        var fixture = await CreateRetiredCleanupFixtureAsync(
            $"stale-{staleInstallation}-{staleSession}");
        var request = new ManagedBrowserCleanupClaimRequest(
            fixture.Installation.Identity.InstallationId,
            fixture.Session.Identity.SessionId,
            fixture.AccountId,
            fixture.Installation.Revision + (staleInstallation ? 1 : 0),
            fixture.Session.Revision + (staleSession ? 1 : 0),
            fixture.Manifest.Manifest.ManifestId,
            fixture.Manifest.Revision + (staleManifest ? 1 : 0));

        var claimed = await fixture.Store.TryClaimRetiredIdentityForCleanupAsync(request, default);

        Assert.Equal(ManagedBrowserTransitionStatus.Conflict, claimed.Status);
        Assert.Equal(ManagedProfileCleanupState.Prepared,
            (await fixture.Store.LoadCleanupManifestAsync(fixture.Manifest.Manifest.ManifestId, default))!.State);
        Assert.Equal(ManagedBrowserSessionLifecycleState.Retired,
            (await fixture.Store.LoadSessionAsync(fixture.Session.Identity.SessionId, default))!.State);
        Assert.Equal(0L, await ScalarLongAsync(fixture.Factory,
            "SELECT COUNT(*) FROM ManagedBrowserCleanupClaims;"));
    }

    [Fact]
    public async Task CleanupClaimFenceRejectsNewOwnerAttemptAndManifestReferences()
    {
        var fixture = await CreateRetiredCleanupFixtureAsync("fence");
        Assert.Equal(ManagedBrowserTransitionStatus.Applied,
            (await fixture.Store.TryClaimRetiredIdentityForCleanupAsync(
                fixture.ClaimRequest(), default)).Status);

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            InsertManagedTransferOwnerAsync(fixture));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            InsertManagedRemoteSelectionAttemptAsync(fixture));
        var nextManifest = new ManagedProfileCleanupManifest(
            Guid.NewGuid(), fixture.AccountId, fixture.Session.Identity.SessionId,
            Path.Combine(root, "source-fenced"), Path.Combine(root, "quarantine-fenced"),
            fixture.Session.Identity.ProfileOwnershipId, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.CreateCleanupManifestAsync(nextManifest, default));
    }

    [Fact]
    public async Task CleanupStateMachineCoordinatesSessionAndReleasesClaimOnlyAfterConfirmedDeletion()
    {
        var fixture = await CreateRetiredCleanupFixtureAsync("state-success");
        Assert.Equal(ManagedBrowserTransitionStatus.Applied,
            (await fixture.Store.TryClaimRetiredIdentityForCleanupAsync(
                fixture.ClaimRequest(), default)).Status);

        var current = (await fixture.Store.LoadCleanupManifestAsync(
            fixture.Manifest.Manifest.ManifestId, default))!;
        var quarantined = await fixture.Store.TryTransitionCleanupManifestAsync(
            new ManagedProfileCleanupTransitionRequest(
                current.Manifest.ManifestId, current.Revision, current.State,
                ManagedProfileCleanupState.Quarantined), default);
        Assert.Equal(ManagedBrowserTransitionStatus.Applied, quarantined.Status);
        Assert.Equal(ManagedBrowserSessionLifecycleState.Quarantined,
            (await fixture.Store.LoadSessionAsync(fixture.Session.Identity.SessionId, default))!.State);

        current = (await fixture.Store.LoadCleanupManifestAsync(current.Manifest.ManifestId, default))!;
        var deleteIntent = await fixture.Store.TryTransitionCleanupManifestAsync(
            new ManagedProfileCleanupTransitionRequest(
                current.Manifest.ManifestId, current.Revision, current.State,
                ManagedProfileCleanupState.DeleteIntent, "confirmed-delete-hash"), default);
        Assert.Equal(ManagedBrowserTransitionStatus.Applied, deleteIntent.Status);
        current = (await fixture.Store.LoadCleanupManifestAsync(current.Manifest.ManifestId, default))!;
        Assert.Equal("confirmed-delete-hash", current.Manifest.ConfirmationTokenHash);
        Assert.Equal(ManagedBrowserSessionLifecycleState.Quarantined,
            (await fixture.Store.LoadSessionAsync(fixture.Session.Identity.SessionId, default))!.State);
        Assert.Equal(1L, await ScalarLongAsync(fixture.Factory,
            "SELECT COUNT(*) FROM ManagedBrowserCleanupClaims;"));

        var deleted = await fixture.Store.TryTransitionCleanupManifestAsync(
            new ManagedProfileCleanupTransitionRequest(
                current.Manifest.ManifestId, current.Revision, current.State,
                ManagedProfileCleanupState.Deleted), default);
        Assert.Equal(ManagedBrowserTransitionStatus.Applied, deleted.Status);
        Assert.Equal(ManagedBrowserSessionLifecycleState.Deleted,
            (await fixture.Store.LoadSessionAsync(fixture.Session.Identity.SessionId, default))!.State);
        Assert.Equal(0L, await ScalarLongAsync(fixture.Factory,
            "SELECT COUNT(*) FROM ManagedBrowserCleanupClaims;"));
    }

    [Fact]
    public async Task DeletedSessionPermanentlyRejectsNewManagedOwnerAndAttemptSnapshots()
    {
        var fixture = await CreateRetiredCleanupFixtureAsync("deleted-fence", retireInstallation: false);
        Assert.Equal(ManagedBrowserTransitionStatus.Applied,
            (await fixture.Store.TryClaimRetiredIdentityForCleanupAsync(
                fixture.ClaimRequest(), default)).Status);
        var current = (await fixture.Store.LoadCleanupManifestAsync(
            fixture.Manifest.Manifest.ManifestId, default))!;
        foreach (var target in new[]
                 {
                     ManagedProfileCleanupState.Quarantined,
                     ManagedProfileCleanupState.DeleteIntent,
                     ManagedProfileCleanupState.Deleted,
                 })
        {
            Assert.Equal(ManagedBrowserTransitionStatus.Applied,
                (await fixture.Store.TryTransitionCleanupManifestAsync(
                    new ManagedProfileCleanupTransitionRequest(
                        current.Manifest.ManifestId, current.Revision, current.State, target,
                        target == ManagedProfileCleanupState.DeleteIntent ? "deleted-fence-confirmation" : null),
                    default)).Status);
            current = (await fixture.Store.LoadCleanupManifestAsync(current.Manifest.ManifestId, default))!;
        }

        Assert.Equal(ManagedBrowserSessionLifecycleState.Deleted,
            (await fixture.Store.LoadSessionAsync(fixture.Session.Identity.SessionId, default))!.State);
        Assert.Equal(0L, await ScalarLongAsync(fixture.Factory,
            "SELECT COUNT(*) FROM ManagedBrowserCleanupClaims;"));

        await AssertManagedOwnerAndAttemptInsertsRejectedAsync(fixture);
    }

    [Fact]
    public async Task CleanupStateMachineKeepsClaimForMoveAndDeleteFailureBranches()
    {
        var moveFailure = await CreateRetiredCleanupFixtureAsync("move-failure");
        _ = await moveFailure.Store.TryClaimRetiredIdentityForCleanupAsync(
            moveFailure.ClaimRequest(), default);
        var moveManifest = (await moveFailure.Store.LoadCleanupManifestAsync(
            moveFailure.Manifest.Manifest.ManifestId, default))!;
        Assert.Equal(ManagedBrowserTransitionStatus.Applied,
            (await moveFailure.Store.TryTransitionCleanupManifestAsync(
                new ManagedProfileCleanupTransitionRequest(
                    moveManifest.Manifest.ManifestId, moveManifest.Revision, moveManifest.State,
                    ManagedProfileCleanupState.CleanupPending), default)).Status);
        Assert.Equal(ManagedBrowserSessionLifecycleState.CleanupPending,
            (await moveFailure.Store.LoadSessionAsync(moveFailure.Session.Identity.SessionId, default))!.State);
        Assert.Equal(1L, await ScalarLongAsync(moveFailure.Factory,
            "SELECT COUNT(*) FROM ManagedBrowserCleanupClaims;"));

        var deleteFailure = await CreateRetiredCleanupFixtureAsync("delete-failure");
        _ = await deleteFailure.Store.TryClaimRetiredIdentityForCleanupAsync(
            deleteFailure.ClaimRequest(), default);
        var deleteManifest = (await deleteFailure.Store.LoadCleanupManifestAsync(
            deleteFailure.Manifest.Manifest.ManifestId, default))!;
        _ = await deleteFailure.Store.TryTransitionCleanupManifestAsync(
            new ManagedProfileCleanupTransitionRequest(
                deleteManifest.Manifest.ManifestId, deleteManifest.Revision, deleteManifest.State,
                ManagedProfileCleanupState.Quarantined), default);
        deleteManifest = (await deleteFailure.Store.LoadCleanupManifestAsync(
            deleteManifest.Manifest.ManifestId, default))!;
        _ = await deleteFailure.Store.TryTransitionCleanupManifestAsync(
            new ManagedProfileCleanupTransitionRequest(
                deleteManifest.Manifest.ManifestId, deleteManifest.Revision, deleteManifest.State,
                ManagedProfileCleanupState.DeleteIntent, "delete-failure-hash"), default);
        deleteManifest = (await deleteFailure.Store.LoadCleanupManifestAsync(
            deleteManifest.Manifest.ManifestId, default))!;
        Assert.Equal(ManagedBrowserTransitionStatus.Applied,
            (await deleteFailure.Store.TryTransitionCleanupManifestAsync(
                new ManagedProfileCleanupTransitionRequest(
                    deleteManifest.Manifest.ManifestId, deleteManifest.Revision, deleteManifest.State,
                    ManagedProfileCleanupState.CleanupPending), default)).Status);
        Assert.Equal(ManagedBrowserSessionLifecycleState.CleanupPending,
            (await deleteFailure.Store.LoadSessionAsync(deleteFailure.Session.Identity.SessionId, default))!.State);
        Assert.Equal(1L, await ScalarLongAsync(deleteFailure.Factory,
            "SELECT COUNT(*) FROM ManagedBrowserCleanupClaims WHERE ManifestId=$manifest;",
            ("$manifest", deleteFailure.Manifest.Manifest.ManifestId.ToString("D"))));
    }

    [Fact]
    public async Task CleanupStateMachineDatabaseGuardRejectsSkippingBacktrackingAndHashTampering()
    {
        var fixture = await CreateRetiredCleanupFixtureAsync("state-guard");
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => ExecuteAsync(
            fixture.Factory,
            "UPDATE ManagedProfileCleanupManifests SET State='Deleted',Revision=Revision+1 WHERE ManifestId=$manifest;",
            ("$manifest", fixture.Manifest.Manifest.ManifestId.ToString("D"))));
        _ = await fixture.Store.TryClaimRetiredIdentityForCleanupAsync(fixture.ClaimRequest(), default);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => ExecuteAsync(
            fixture.Factory,
            "UPDATE ManagedProfileCleanupManifests SET State='DeleteIntent',ConfirmationTokenHash='hash',Revision=Revision+1 WHERE ManifestId=$manifest;",
            ("$manifest", fixture.Manifest.Manifest.ManifestId.ToString("D"))));

        var current = (await fixture.Store.LoadCleanupManifestAsync(
            fixture.Manifest.Manifest.ManifestId, default))!;
        _ = await fixture.Store.TryTransitionCleanupManifestAsync(
            new ManagedProfileCleanupTransitionRequest(
                current.Manifest.ManifestId, current.Revision, current.State,
                ManagedProfileCleanupState.Quarantined), default);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => ExecuteAsync(
            fixture.Factory,
            "UPDATE ManagedProfileCleanupManifests SET State='DeleteIntent',Revision=Revision+1 WHERE ManifestId=$manifest;",
            ("$manifest", fixture.Manifest.Manifest.ManifestId.ToString("D"))));
        foreach (var invalidHash in new[] { "   ", new string('x', 513) })
        {
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => ExecuteAsync(
                fixture.Factory,
                "UPDATE ManagedProfileCleanupManifests SET State='DeleteIntent',ConfirmationTokenHash=$hash,Revision=Revision+1 WHERE ManifestId=$manifest;",
                ("$hash", invalidHash),
                ("$manifest", fixture.Manifest.Manifest.ManifestId.ToString("D"))));
        }
        current = (await fixture.Store.LoadCleanupManifestAsync(current.Manifest.ManifestId, default))!;
        _ = await fixture.Store.TryTransitionCleanupManifestAsync(
            new ManagedProfileCleanupTransitionRequest(
                current.Manifest.ManifestId, current.Revision, current.State,
                ManagedProfileCleanupState.DeleteIntent, "immutable-hash"), default);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => ExecuteAsync(
            fixture.Factory,
            "UPDATE ManagedProfileCleanupManifests SET ConfirmationTokenHash='replacement',Revision=Revision+1 WHERE ManifestId=$manifest;",
            ("$manifest", fixture.Manifest.Manifest.ManifestId.ToString("D"))));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => ExecuteAsync(
            fixture.Factory,
            "UPDATE ManagedProfileCleanupManifests SET State='Quarantined',Revision=Revision+1 WHERE ManifestId=$manifest;",
            ("$manifest", fixture.Manifest.Manifest.ManifestId.ToString("D"))));
    }

    [Fact]
    public async Task CorruptCleanupManifestFailsClosedAndCleanupOperationsPropagateCancellation()
    {
        var (factory, store) = await CreateStoreAsync();
        var accountId = Guid.NewGuid();
        await SeedAccountAsync(factory, accountId, "uid-corrupt-cleanup");
        var installation = await store.CreateInstallationAsync(
            Installation(BrowserFamily.Chrome, "chrome-corrupt-cleanup", "corrupt-cleanup-file"), default);
        var sessionIdentity = Session(
            installation.Identity.InstallationId, accountId, "uid-corrupt-cleanup", BrowserFamily.Chrome,
            "profile-corrupt-cleanup", "ownership-corrupt-cleanup");
        var session = await store.CreateSessionAsync(sessionIdentity, default);
        var manifest = CleanupManifest(accountId, sessionIdentity, "corrupt");
        _ = await store.CreateCleanupManifestAsync(manifest, default);

        await using (var connection = await factory.OpenConnectionAsync())
        {
            await using var corrupt = connection.CreateCommand();
            corrupt.CommandText = """
                DROP TRIGGER TR_ManagedProfileCleanupManifests_StateMachine;
                PRAGMA ignore_check_constraints=ON;
                UPDATE ManagedProfileCleanupManifests SET State='UnknownState' WHERE ManifestId=$id;
                PRAGMA ignore_check_constraints=OFF;
                """;
            corrupt.Parameters.AddWithValue("$id", manifest.ManifestId.ToString("D"));
            await corrupt.ExecuteNonQueryAsync();
        }
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.LoadCleanupManifestAsync(manifest.ManifestId, default));
        Assert.DoesNotContain(manifest.SourceProfilePath, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(manifest.ExpectedOwnershipId, failure.ToString(), StringComparison.Ordinal);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.CreateCleanupManifestAsync(CleanupManifest(accountId, sessionIdentity, "cancelled"), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.LoadCleanupManifestAsync(manifest.ManifestId, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.TryTransitionCleanupManifestAsync(
                new ManagedProfileCleanupTransitionRequest(
                    manifest.ManifestId, 0, ManagedProfileCleanupState.Prepared,
                    ManagedProfileCleanupState.QuarantineMoveIntent), cancelled.Token));
    }

    [Fact]
    public async Task CorruptActiveRowsAndEnumsFailClosedWithoutLeakingIdentityAndCancellationPropagates()
    {
        var (factory, store) = await CreateStoreAsync();
        const string secretFileIdentity = "secret-file-identity";
        var firstIdentity = Installation(BrowserFamily.Chrome, "secret-browser-directory", secretFileIdentity);
        var secondIdentity = Installation(BrowserFamily.Edge, "other-secret-directory", "other-secret-file");
        var first = await store.CreateInstallationAsync(firstIdentity, default);
        first = (await store.TryReplaceActiveInstallationAsync(
            new ManagedBrowserInstallationActivationRequest(null, first), default)).Current!;
        await store.CreateInstallationAsync(secondIdentity, default);

        var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateInstallationAsync(firstIdentity, default));
        Assert.DoesNotContain(firstIdentity.InstallationDirectory, duplicate.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(firstIdentity.ExecutablePath, duplicate.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secretFileIdentity, duplicate.ToString(), StringComparison.Ordinal);

        await using (var connection = await factory.OpenConnectionAsync())
        {
            await using var corrupt = connection.CreateCommand();
            corrupt.CommandText = """
                DROP INDEX UX_BrowserInstallations_OneActive;
                UPDATE BrowserInstallations SET State='Active' WHERE InstallationId=$id;
                """;
            corrupt.Parameters.AddWithValue("$id", secondIdentity.InstallationId.ToString("D"));
            await corrupt.ExecuteNonQueryAsync();
        }
        var multiple = await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.LoadActiveInstallationAsync(default));
        Assert.DoesNotContain(firstIdentity.InstallationDirectory, multiple.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secondIdentity.InstallationDirectory, multiple.ToString(), StringComparison.OrdinalIgnoreCase);

        await using (var connection = await factory.OpenConnectionAsync())
        {
            await using var corrupt = connection.CreateCommand();
            corrupt.CommandText = """
                PRAGMA ignore_check_constraints=ON;
                UPDATE BrowserInstallations SET State='UnknownState' WHERE InstallationId=$id;
                PRAGMA ignore_check_constraints=OFF;
                """;
            corrupt.Parameters.AddWithValue("$id", first.Identity.InstallationId.ToString("D"));
            await corrupt.ExecuteNonQueryAsync();
        }
        var unknown = await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.LoadInstallationAsync(first.Identity.InstallationId, default));
        Assert.DoesNotContain(firstIdentity.InstallationDirectory, unknown.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secretFileIdentity, unknown.ToString(), StringComparison.Ordinal);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.LoadInstallationAsync(first.Identity.InstallationId, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.AddRetentionAsync(
            new ManagedBrowserRetentionReference(
                ManagedBrowserRetentionOwnerKind.Operation, Guid.NewGuid(),
                first.Identity.InstallationId, Guid.NewGuid()), cancelled.Token));
    }

    [Fact]
    public async Task FamilySwitchExceptionAfterOldRetirementRollsBackEveryVisibleWrite()
    {
        var fixture = await CreateFamilySwitchFixtureAsync();
        var crashing = new SqliteManagedBrowserStateStore(
            fixture.Factory,
            _ => throw new InvalidOperationException("simulated crash"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            crashing.TrySwitchFamilyAsync(fixture.Request, default));

        Assert.Equal(BrowserInstallationLifecycleState.Active,
            (await fixture.Store.LoadInstallationAsync(
                fixture.OldInstallation.Identity.InstallationId, default))!.State);
        Assert.Equal(ManagedBrowserSessionLifecycleState.Active,
            (await fixture.Store.LoadSessionAsync(
                fixture.OldSession.Identity.SessionId, default))!.State);
        Assert.Null(await fixture.Store.LoadInstallationAsync(
            fixture.Request.TargetInstallation.InstallationId, default));
        Assert.Null(await fixture.Store.LoadSessionAsync(
            fixture.Request.TargetSession.SessionId, default));
    }

    [Fact]
    public async Task ConcurrentFamilySwitchHasOneWinnerAndNoLosingRows()
    {
        var fixture = await CreateFamilySwitchFixtureAsync();
        var otherInstallation = Installation(BrowserFamily.Edge, "edge-other", "edge-other-file");
        var otherSession = Session(
            otherInstallation.InstallationId,
            fixture.AccountId,
            fixture.StableUid,
            BrowserFamily.Edge,
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"));
        var otherRequest = new ManagedBrowserFamilySwitchTransitionRequest(
            fixture.OldInstallation,
            fixture.OldSession,
            otherInstallation,
            otherSession,
            fixture.StableUid);

        var results = await Task.WhenAll(
            fixture.Store.TrySwitchFamilyAsync(fixture.Request, default),
            new SqliteManagedBrowserStateStore(fixture.Factory)
                .TrySwitchFamilyAsync(otherRequest, default));

        Assert.Single(results, result => result.Status == ManagedBrowserFamilySwitchTransitionStatus.Applied);
        Assert.Single(results, result => result.Status == ManagedBrowserFamilySwitchTransitionStatus.Conflict);
        Assert.Equal(2, await ScalarLongAsync(fixture.Factory, "SELECT COUNT(*) FROM BrowserInstallations;"));
        Assert.Equal(2, await ScalarLongAsync(fixture.Factory, "SELECT COUNT(*) FROM ManagedBrowserSessions;"));
        Assert.Equal(1, await ScalarLongAsync(fixture.Factory,
            "SELECT COUNT(*) FROM BrowserInstallations WHERE State='Active';"));
        Assert.Equal(1, await ScalarLongAsync(fixture.Factory,
            "SELECT COUNT(*) FROM ManagedBrowserSessions WHERE State='Active' AND AccountId=$account;",
            ("$account", fixture.AccountId.ToString("D"))));
    }

    [Fact]
    public async Task RepeatingExactFamilySwitchIntentIsIdempotentlyApplied()
    {
        var fixture = await CreateFamilySwitchFixtureAsync();

        var first = await fixture.Store.TrySwitchFamilyAsync(fixture.Request, default);
        var targetInstallation = await fixture.Store.LoadInstallationAsync(
            fixture.Request.TargetInstallation.InstallationId, default);
        var targetSession = await fixture.Store.LoadSessionAsync(
            fixture.Request.TargetSession.SessionId, default);
        var repeated = await fixture.Store.TrySwitchFamilyAsync(fixture.Request, default);

        Assert.Equal(ManagedBrowserFamilySwitchTransitionStatus.Applied, first.Status);
        Assert.Equal(ManagedBrowserFamilySwitchTransitionStatus.Applied, repeated.Status);
        AssertInstallationEqual(targetInstallation!, await fixture.Store.LoadInstallationAsync(
            fixture.Request.TargetInstallation.InstallationId, default));
        AssertSessionEqual(targetSession!, await fixture.Store.LoadSessionAsync(
            fixture.Request.TargetSession.SessionId, default));
        Assert.Equal(2, await ScalarLongAsync(fixture.Factory, "SELECT COUNT(*) FROM BrowserInstallations;"));
        Assert.Equal(2, await ScalarLongAsync(fixture.Factory, "SELECT COUNT(*) FROM ManagedBrowserSessions;"));
    }

    [Fact]
    public async Task FamilySwitchBackReusesRetiredInstallationAndCreatesFreshOwnedProfile()
    {
        var fixture = await CreateFamilySwitchFixtureAsync();
        Assert.Equal(ManagedBrowserFamilySwitchTransitionStatus.Applied,
            (await fixture.Store.TrySwitchFamilyAsync(fixture.Request, default)).Status);
        var edgeInstallation = (await fixture.Store.LoadInstallationAsync(
            fixture.Request.TargetInstallation.InstallationId, default))!;
        var edgeSession = (await fixture.Store.LoadSessionAsync(
            fixture.Request.TargetSession.SessionId, default))!;
        var retiredChrome = (await fixture.Store.LoadInstallationAsync(
            fixture.OldInstallation.Identity.InstallationId, default))!;
        var reobservedChrome = new BrowserInstallationIdentity(
            Guid.NewGuid(),
            retiredChrome.Identity.BrowserFamily,
            retiredChrome.Identity.InstallationDirectory,
            retiredChrome.Identity.ExecutablePath,
            retiredChrome.Identity.Publisher,
            retiredChrome.Identity.ProductName,
            retiredChrome.Identity.FileIdentity);
        var freshChromeSession = Session(
            reobservedChrome.InstallationId,
            fixture.AccountId,
            fixture.StableUid,
            BrowserFamily.Chrome,
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"));
        var request = new ManagedBrowserFamilySwitchTransitionRequest(
            edgeInstallation,
            edgeSession,
            reobservedChrome,
            freshChromeSession,
            fixture.StableUid,
            expectedTargetInstallation: retiredChrome);

        var result = await fixture.Store.TrySwitchFamilyAsync(request, default);

        Assert.Equal(ManagedBrowserFamilySwitchTransitionStatus.Applied, result.Status);
        Assert.Equal(2, await ScalarLongAsync(fixture.Factory, "SELECT COUNT(*) FROM BrowserInstallations;"));
        Assert.Equal(3, await ScalarLongAsync(fixture.Factory, "SELECT COUNT(*) FROM ManagedBrowserSessions;"));
        var activeChrome = (await fixture.Store.LoadInstallationAsync(
            retiredChrome.Identity.InstallationId, default))!;
        Assert.Equal(BrowserInstallationLifecycleState.Active, activeChrome.State);
        Assert.Equal(retiredChrome.Revision + 1, activeChrome.Revision);
        var fresh = (await fixture.Store.LoadSessionAsync(freshChromeSession.SessionId, default))!;
        Assert.Equal(ManagedBrowserSessionLifecycleState.Active, fresh.State);
        Assert.Equal(retiredChrome.Identity.InstallationId, fresh.Identity.InstallationId);
        Assert.Equal(freshChromeSession.ProfileId, fresh.Identity.ProfileId);
        Assert.Equal(freshChromeSession.ProfileOwnershipId, fresh.Identity.ProfileOwnershipId);
        var originalChromeSession = (await fixture.Store.LoadSessionAsync(
            fixture.OldSession.Identity.SessionId, default))!;
        Assert.Equal(ManagedBrowserSessionLifecycleState.Retired, originalChromeSession.State);
        Assert.Equal(fixture.OldSession.Identity.ProfileId, originalChromeSession.Identity.ProfileId);
        Assert.Equal(fixture.OldSession.Identity.ProfileOwnershipId,
            originalChromeSession.Identity.ProfileOwnershipId);
        Assert.Equal(ManagedBrowserSessionLifecycleState.Retired,
            (await fixture.Store.LoadSessionAsync(edgeSession.Identity.SessionId, default))!.State);

        var repeated = await fixture.Store.TrySwitchFamilyAsync(request, default);

        Assert.Equal(ManagedBrowserFamilySwitchTransitionStatus.Applied, repeated.Status);
        AssertInstallationEqual(activeChrome, await fixture.Store.LoadInstallationAsync(
            retiredChrome.Identity.InstallationId, default));
        AssertSessionEqual(fresh, await fixture.Store.LoadSessionAsync(
            freshChromeSession.SessionId, default));
    }

    [Fact]
    public async Task ConcurrentFamilySwitchBackToRetiredInstallationHasOneWinner()
    {
        var fixture = await CreateFamilySwitchFixtureAsync();
        Assert.Equal(ManagedBrowserFamilySwitchTransitionStatus.Applied,
            (await fixture.Store.TrySwitchFamilyAsync(fixture.Request, default)).Status);
        var edgeInstallation = (await fixture.Store.LoadInstallationAsync(
            fixture.Request.TargetInstallation.InstallationId, default))!;
        var edgeSession = (await fixture.Store.LoadSessionAsync(
            fixture.Request.TargetSession.SessionId, default))!;
        var retiredChrome = (await fixture.Store.LoadInstallationAsync(
            fixture.OldInstallation.Identity.InstallationId, default))!;

        ManagedBrowserFamilySwitchTransitionRequest Request(string suffix)
        {
            var observed = new BrowserInstallationIdentity(
                Guid.NewGuid(),
                retiredChrome.Identity.BrowserFamily,
                retiredChrome.Identity.InstallationDirectory,
                retiredChrome.Identity.ExecutablePath,
                retiredChrome.Identity.Publisher,
                retiredChrome.Identity.ProductName,
                retiredChrome.Identity.FileIdentity);
            return new ManagedBrowserFamilySwitchTransitionRequest(
                edgeInstallation,
                edgeSession,
                observed,
                Session(
                    observed.InstallationId,
                    fixture.AccountId,
                    fixture.StableUid,
                    BrowserFamily.Chrome,
                    $"{suffix}{Guid.NewGuid():N}",
                    Guid.NewGuid().ToString("N")),
                fixture.StableUid,
                expectedTargetInstallation: retiredChrome);
        }

        var results = await Task.WhenAll(
            fixture.Store.TrySwitchFamilyAsync(Request("a"), default),
            new SqliteManagedBrowserStateStore(fixture.Factory)
                .TrySwitchFamilyAsync(Request("b"), default));

        Assert.Single(results, result => result.Status == ManagedBrowserFamilySwitchTransitionStatus.Applied);
        Assert.Single(results, result => result.Status == ManagedBrowserFamilySwitchTransitionStatus.Conflict);
        Assert.Equal(2, await ScalarLongAsync(fixture.Factory, "SELECT COUNT(*) FROM BrowserInstallations;"));
        Assert.Equal(3, await ScalarLongAsync(fixture.Factory, "SELECT COUNT(*) FROM ManagedBrowserSessions;"));
        Assert.Equal(1, await ScalarLongAsync(fixture.Factory,
            "SELECT COUNT(*) FROM BrowserInstallations WHERE State='Active';"));
        Assert.Equal(1, await ScalarLongAsync(fixture.Factory,
            "SELECT COUNT(*) FROM ManagedBrowserSessions WHERE State='Active' AND AccountId=$account;",
            ("$account", fixture.AccountId.ToString("D"))));
    }

    [Fact]
    public async Task RecoveringAttemptBlocksFamilySwitchWithoutChangingSnapshotOrCreatingRows()
    {
        var fixture = await CreateFamilySwitchFixtureAsync();
        var (operationId, _) = await SeedManagedOwnersAsync(
            fixture.Factory,
            fixture.Store,
            fixture.AccountId,
            fixture.OldInstallation.Identity.InstallationId,
            fixture.OldSession.Identity);

        var result = await fixture.Store.TrySwitchFamilyAsync(fixture.Request, default);

        Assert.Equal(ManagedBrowserFamilySwitchTransitionStatus.ActiveWorkBlocked, result.Status);
        Assert.Null(await fixture.Store.LoadInstallationAsync(
            fixture.Request.TargetInstallation.InstallationId, default));
        Assert.Null(await fixture.Store.LoadSessionAsync(
            fixture.Request.TargetSession.SessionId, default));
        AssertInstallationEqual(fixture.OldInstallation,
            await fixture.Store.LoadInstallationAsync(fixture.OldInstallation.Identity.InstallationId, default));
        AssertSessionEqual(fixture.OldSession,
            await fixture.Store.LoadSessionAsync(fixture.OldSession.Identity.SessionId, default));
        Assert.Equal(fixture.OldSession.Identity.SessionId.ToString("D"), await ScalarTextAsync(
            fixture.Factory,
            "SELECT SessionId FROM WebAutomationAttempts WHERE OperationId=$operation;",
            ("$operation", operationId.ToString("D"))));
    }

    [Fact]
    public async Task TerminalManagedHistoryAndItsRetentionsDoNotBlockFamilySwitchOrRewriteSnapshots()
    {
        var fixture = await CreateFamilySwitchFixtureAsync();
        var ownerIds = await SeedTerminalFamilySwitchHistoryAsync(fixture);

        var result = await fixture.Store.TrySwitchFamilyAsync(fixture.Request, default);

        Assert.Equal(ManagedBrowserFamilySwitchTransitionStatus.Applied, result.Status);
        Assert.Equal(3, await ScalarLongAsync(fixture.Factory, """
            SELECT COUNT(*) FROM WebAutomationAttempts
            WHERE OperationId IN ($transfer,$share,$standalone)
              AND State='DefinitelyNotSubmitted'
              AND BrowserFamily=$family AND ProfileId=$profile
              AND InstallationId=$installation AND SessionId=$session;
            """,
            ("$transfer", ownerIds.Transfer.ToString("D")),
            ("$share", ownerIds.Share.ToString("D")),
            ("$standalone", ownerIds.Standalone.ToString("D")),
            ("$family", fixture.OldSession.Identity.BrowserFamily.ToString()),
            ("$profile", fixture.OldSession.Identity.ProfileId),
            ("$installation", fixture.OldInstallation.Identity.InstallationId.ToString("D")),
            ("$session", fixture.OldSession.Identity.SessionId.ToString("D"))));
        Assert.Equal(3, await ScalarLongAsync(fixture.Factory, """
            SELECT
              (SELECT COUNT(*) FROM TransferOperations WHERE Id=$transfer AND Status='Succeeded'
                AND BrowserFamily=$family AND ProfileId=$profile
                AND InstallationId=$installation AND SessionId=$session)
              +
              (SELECT COUNT(*) FROM ShareOperations WHERE Id=$share AND Status='Succeeded'
                AND BrowserFamily=$family AND ProfileId=$profile
                AND InstallationId=$installation AND SessionId=$session)
              +
              (SELECT COUNT(*) FROM StandaloneShareOperations WHERE Id=$standalone AND Status='Succeeded'
                AND BrowserFamily=$family AND ProfileId=$profile
                AND InstallationId=$installation AND SessionId=$session);
            """,
            ("$transfer", ownerIds.Transfer.ToString("D")),
            ("$share", ownerIds.Share.ToString("D")),
            ("$standalone", ownerIds.Standalone.ToString("D")),
            ("$family", fixture.OldSession.Identity.BrowserFamily.ToString()),
            ("$profile", fixture.OldSession.Identity.ProfileId),
            ("$installation", fixture.OldInstallation.Identity.InstallationId.ToString("D")),
            ("$session", fixture.OldSession.Identity.SessionId.ToString("D"))));
        Assert.Equal(6, await ScalarLongAsync(fixture.Factory, """
            SELECT COUNT(*) FROM ManagedBrowserRetentions
            WHERE InstallationId=$installation AND SessionId=$session;
            """,
            ("$installation", fixture.OldInstallation.Identity.InstallationId.ToString("D")),
            ("$session", fixture.OldSession.Identity.SessionId.ToString("D"))));
    }

    [Theory]
    [InlineData(FamilySwitchOwnerKind.Transfer)]
    [InlineData(FamilySwitchOwnerKind.Share)]
    [InlineData(FamilySwitchOwnerKind.StandaloneShare)]
    [InlineData(FamilySwitchOwnerKind.Attempt)]
    public async Task FamilySwitchCannotBypassNonTerminalOwnerFenceAfterRetentionRelease(
        FamilySwitchOwnerKind ownerKind)
    {
        var fixture = await CreateFamilySwitchFixtureAsync();
        var retentions = await SeedNonTerminalFamilySwitchOwnerAsync(fixture, ownerKind);
        foreach (var retention in retentions)
            await fixture.Store.ReleaseRetentionAsync(retention, default);
        Assert.Equal(0, await ScalarLongAsync(fixture.Factory, """
            SELECT COUNT(*) FROM ManagedBrowserRetentions
            WHERE InstallationId=$installation AND SessionId=$session;
            """,
            ("$installation", fixture.OldInstallation.Identity.InstallationId.ToString("D")),
            ("$session", fixture.OldSession.Identity.SessionId.ToString("D"))));

        var result = await fixture.Store.TrySwitchFamilyAsync(fixture.Request, default);

        Assert.Equal(ManagedBrowserFamilySwitchTransitionStatus.ActiveWorkBlocked, result.Status);
        Assert.Null(await fixture.Store.LoadInstallationAsync(
            fixture.Request.TargetInstallation.InstallationId, default));
        Assert.Null(await fixture.Store.LoadSessionAsync(
            fixture.Request.TargetSession.SessionId, default));
        AssertInstallationEqual(fixture.OldInstallation,
            await fixture.Store.LoadInstallationAsync(fixture.OldInstallation.Identity.InstallationId, default));
        AssertSessionEqual(fixture.OldSession,
            await fixture.Store.LoadSessionAsync(fixture.OldSession.Identity.SessionId, default));
    }

    [Theory]
    [InlineData(FamilySwitchOwnerKind.Transfer)]
    [InlineData(FamilySwitchOwnerKind.Share)]
    [InlineData(FamilySwitchOwnerKind.StandaloneShare)]
    [InlineData(FamilySwitchOwnerKind.Attempt)]
    public async Task FamilySwitchCommitFencesEveryNewWriteToRetiredSession(
        FamilySwitchOwnerKind ownerKind)
    {
        var fixture = await CreateFamilySwitchFixtureAsync();
        var ids = FamilySwitchOwnerIds.Create();
        await SeedFamilySwitchOwnerGraphAsync(fixture, ids, $"retired-write-{ownerKind}");
        if (ownerKind is FamilySwitchOwnerKind.Share or FamilySwitchOwnerKind.Attempt)
            await SeedTerminalTransferOwnerAsync(fixture, ids, $"retired-parent-{ownerKind}");

        var switched = await fixture.Store.TrySwitchFamilyAsync(fixture.Request, default);

        Assert.Equal(ManagedBrowserFamilySwitchTransitionStatus.Applied, switched.Status);
        var exception = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            fixture.Factory,
            NewRetiredOwnerWriteSql(ownerKind),
            FamilySwitchOwnerParameters(fixture, ids)));
        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.Equal(0, await ScalarLongAsync(fixture.Factory, ownerKind switch
        {
            FamilySwitchOwnerKind.Transfer =>
                "SELECT COUNT(*) FROM TransferOperations WHERE Id=$id;",
            FamilySwitchOwnerKind.Share =>
                "SELECT COUNT(*) FROM ShareOperations WHERE Id=$id;",
            FamilySwitchOwnerKind.StandaloneShare =>
                "SELECT COUNT(*) FROM StandaloneShareOperations WHERE Id=$id;",
            FamilySwitchOwnerKind.Attempt =>
                "SELECT COUNT(*) FROM WebAutomationAttempts WHERE OperationId=$id;",
            _ => throw new ArgumentOutOfRangeException(nameof(ownerKind)),
        }, ("$id", ownerKind switch
        {
            FamilySwitchOwnerKind.Transfer => ids.Transfer.ToString("D"),
            FamilySwitchOwnerKind.Share => ids.Share.ToString("D"),
            FamilySwitchOwnerKind.StandaloneShare => ids.Standalone.ToString("D"),
            FamilySwitchOwnerKind.Attempt => ids.Transfer.ToString("D"),
            _ => throw new ArgumentOutOfRangeException(nameof(ownerKind)),
        })));
        Assert.Equal(ManagedBrowserSessionLifecycleState.Retired,
            (await fixture.Store.LoadSessionAsync(
                fixture.OldSession.Identity.SessionId, default))!.State);
        Assert.Equal(ManagedBrowserSessionLifecycleState.Active,
            (await fixture.Store.LoadSessionAsync(
                fixture.Request.TargetSession.SessionId, default))!.State);
    }

    [Fact]
    public async Task ConcurrentFamilySwitchAndNewManagedWriteHaveOneLinearizedWinner()
    {
        var fixture = await CreateFamilySwitchFixtureAsync();
        var ids = FamilySwitchOwnerIds.Create();
        await SeedFamilySwitchOwnerGraphAsync(fixture, ids, "switch-write-race");

        var switchTask = fixture.Store.TrySwitchFamilyAsync(fixture.Request, default);
        var insertTask = Record.ExceptionAsync(() => ExecuteAsync(
            fixture.Factory,
            NewRetiredOwnerWriteSql(FamilySwitchOwnerKind.Transfer),
            FamilySwitchOwnerParameters(fixture, ids)));

        await Task.WhenAll(switchTask, insertTask);

        var switched = await switchTask;
        var insertError = await insertTask;
        if (switched.Status == ManagedBrowserFamilySwitchTransitionStatus.Applied)
        {
            Assert.Equal(19, Assert.IsType<SqliteException>(insertError).SqliteErrorCode);
            Assert.Equal(0, await ScalarLongAsync(fixture.Factory,
                "SELECT COUNT(*) FROM TransferOperations WHERE Id=$id;",
                ("$id", ids.Transfer.ToString("D"))));
            Assert.Equal(ManagedBrowserSessionLifecycleState.Retired,
                (await fixture.Store.LoadSessionAsync(
                    fixture.OldSession.Identity.SessionId, default))!.State);
        }
        else
        {
            Assert.Equal(ManagedBrowserFamilySwitchTransitionStatus.ActiveWorkBlocked, switched.Status);
            Assert.Null(insertError);
            Assert.Equal(1, await ScalarLongAsync(fixture.Factory,
                "SELECT COUNT(*) FROM TransferOperations WHERE Id=$id;",
                ("$id", ids.Transfer.ToString("D"))));
            Assert.Equal(ManagedBrowserSessionLifecycleState.Active,
                (await fixture.Store.LoadSessionAsync(
                    fixture.OldSession.Identity.SessionId, default))!.State);
            Assert.Null(await fixture.Store.LoadInstallationAsync(
                fixture.Request.TargetInstallation.InstallationId, default));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task FamilySwitchReadinessReadsRealRecoveringWorkBeforeAnyBrowserStop()
    {
        var fixture = await CreateFamilySwitchFixtureAsync();
        Assert.True(await fixture.Store.CanSwitchFamilyAsync(fixture.AccountId, default));
        await SeedManagedOwnersAsync(fixture.Factory, fixture.Store, fixture.AccountId,
            fixture.OldInstallation.Identity.InstallationId, fixture.OldSession.Identity);
        Assert.False(await fixture.Store.CanSwitchFamilyAsync(fixture.AccountId, default));
        AssertSessionEqual(fixture.OldSession, await fixture.Store.LoadSessionAsync(fixture.OldSession.Identity.SessionId, default));
    }

    [Fact]
    public async Task FamilySwitchReadinessAllowsTerminalHistoryWithoutRewritingIt()
    {
        var fixture = await CreateFamilySwitchFixtureAsync();
        await SeedTerminalFamilySwitchHistoryAsync(fixture);
        Assert.True(await fixture.Store.CanSwitchFamilyAsync(fixture.AccountId, default));
        AssertSessionEqual(fixture.OldSession, await fixture.Store.LoadSessionAsync(fixture.OldSession.Identity.SessionId, default));
    }

    private async Task<(SqliteConnectionFactory Factory, SqliteManagedBrowserStateStore Store)> CreateStoreAsync()
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "state.db"));
        await new DatabaseMigrator(
            factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
        return (factory, new SqliteManagedBrowserStateStore(factory));
    }

    private static void AssertInstallationEqual(
        StoredBrowserInstallation expected,
        StoredBrowserInstallation? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.Identity.InstallationId, actual.Identity.InstallationId);
        Assert.Equal(expected.Identity.BrowserFamily, actual.Identity.BrowserFamily);
        Assert.Equal(expected.Identity.InstallationDirectory, actual.Identity.InstallationDirectory);
        Assert.Equal(expected.Identity.ExecutablePath, actual.Identity.ExecutablePath);
        Assert.Equal(expected.Identity.Publisher, actual.Identity.Publisher);
        Assert.Equal(expected.Identity.ProductName, actual.Identity.ProductName);
        Assert.Equal(expected.Identity.FileIdentity, actual.Identity.FileIdentity);
    }

    private static void AssertSessionEqual(
        StoredManagedBrowserSession expected,
        StoredManagedBrowserSession? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.Identity.SessionId, actual.Identity.SessionId);
        Assert.Equal(expected.Identity.InstallationId, actual.Identity.InstallationId);
        Assert.Equal(expected.Identity.AccountId, actual.Identity.AccountId);
        Assert.Equal(expected.Identity.StableBaiduUserId, actual.Identity.StableBaiduUserId);
        Assert.Equal(expected.Identity.BrowserFamily, actual.Identity.BrowserFamily);
        Assert.Equal(expected.Identity.ProfileId, actual.Identity.ProfileId);
        Assert.Equal(expected.Identity.ProfileOwnershipId, actual.Identity.ProfileOwnershipId);
    }

    private async Task<FamilySwitchFixture> CreateFamilySwitchFixtureAsync()
    {
        var (factory, store) = await CreateStoreAsync();
        var accountId = Guid.NewGuid();
        var stableUid = $"uid-family-{Guid.NewGuid():N}";
        await SeedAccountAsync(factory, accountId, stableUid);
        var oldInstallation = await store.CreateInstallationAsync(
            Installation(BrowserFamily.Chrome, "chrome-family", "chrome-family-file"), default);
        oldInstallation = (await store.TryReplaceActiveInstallationAsync(
            new ManagedBrowserInstallationActivationRequest(null, oldInstallation), default)).Current!;
        var oldIdentity = Session(
            oldInstallation.Identity.InstallationId,
            accountId,
            stableUid,
            BrowserFamily.Chrome,
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"));
        var oldSession = await store.CreateSessionAsync(oldIdentity, default);
        oldSession = (await store.TryReplaceActiveSessionAsync(
            new ManagedBrowserSessionActivationRequest(null, oldSession), default)).Current!;
        var targetInstallation = Installation(BrowserFamily.Edge, "edge-family", "edge-family-file");
        var targetSession = Session(
            targetInstallation.InstallationId,
            accountId,
            stableUid,
            BrowserFamily.Edge,
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"));
        var request = new ManagedBrowserFamilySwitchTransitionRequest(
            oldInstallation,
            oldSession,
            targetInstallation,
            targetSession,
            stableUid);
        return new FamilySwitchFixture(
            factory, store, accountId, stableUid, oldInstallation, oldSession, request);
    }

    private static async Task<FamilySwitchOwnerIds> SeedTerminalFamilySwitchHistoryAsync(
        FamilySwitchFixture fixture)
    {
        var ids = FamilySwitchOwnerIds.Create();
        await SeedFamilySwitchOwnerGraphAsync(fixture, ids, "terminal");
        await ExecuteAsync(fixture.Factory, """
            INSERT INTO TransferOperations(Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,
              NormalizedTargetPath,CreateFolderName,AssignedAccountId,AllocationSource,Status,OutcomeKind,AttemptCount,
              OperationFingerprint,ConfirmationDescriptorJson,AdapterRequestId,RemoteItemIdsJson,SubmitState,
              TargetSlotState,DailyCountedAt,SubmitStartedAt,CreatedAt,UpdatedAt,
              ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES($transfer,$resource,$batch,1,1,'/','/','',$account,'Fixture','Succeeded','NewTransfer',2,
              'terminal-transfer','{}','request','[]','ResultReceived','Fulfilled',$now,$now,$now,$now,
              'ManagedChromium-v1',$family,$profile,$installation,$session);
            INSERT INTO ShareOperations(Id,TransferOperationId,ResourceItemId,BatchJobId,AccountId,OutputOrdinal,
              Status,ExactItemSetFingerprint,SettingsFingerprint,Generation,SubmissionAcceptance,CreatedAt,UpdatedAt,
              ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES($share,$transfer,$resource,$batch,$account,1,'Succeeded','terminal-items','terminal-settings',2,
              'Unknown',$now,$now,'ManagedChromium-v1',$family,$profile,$installation,$session);
            INSERT INTO StandaloneShareOperations(Id,BatchId,AccountId,SequenceNumber,DisplayName,ResourceKind,
              ParentPath,ExactItemSetFingerprint,Generation,Status,SubmissionState,SubmissionAcceptance,CreatedAt,UpdatedAt,
              ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES($standalone,$standaloneBatch,$account,1,'terminal','File','/','terminal-item',2,'Succeeded',
              'Completed','Unknown',$now,$now,'ManagedChromium-v1',$family,$profile,$installation,$session);
            INSERT INTO WebAutomationAttempts(OperationId,AccountId,Generation,OwnerKind,State,
              Revision,CreatedAt,UpdatedAt,ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES
              ($transfer,$account,1,'Transfer','DefinitelyNotSubmitted',0,$now,$now,
                'ManagedChromium-v1',$family,$profile,$installation,$session),
              ($share,$account,1,'TransferShare','DefinitelyNotSubmitted',0,$now,$now,
                'ManagedChromium-v1',$family,$profile,$installation,$session),
              ($standalone,$account,1,'StandaloneShare','DefinitelyNotSubmitted',0,$now,$now,
                'ManagedChromium-v1',$family,$profile,$installation,$session);
            """, FamilySwitchOwnerParameters(fixture, ids));
        return ids;
    }

    private static Task SeedTerminalTransferOwnerAsync(
        FamilySwitchFixture fixture,
        FamilySwitchOwnerIds ids,
        string fingerprint) => ExecuteAsync(fixture.Factory, """
            INSERT INTO TransferOperations(Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,
              NormalizedTargetPath,CreateFolderName,AssignedAccountId,AllocationSource,Status,OutcomeKind,AttemptCount,
              OperationFingerprint,ConfirmationDescriptorJson,AdapterRequestId,RemoteItemIdsJson,SubmitState,
              TargetSlotState,DailyCountedAt,SubmitStartedAt,CreatedAt,UpdatedAt,
              ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES($transfer,$resource,$batch,1,1,'/','/','',$account,'Fixture','Succeeded','NewTransfer',1,
              $fingerprint,'{}','request','[]','ResultReceived','Fulfilled',$now,$now,$now,$now,
              'ManagedChromium-v1',$family,$profile,$installation,$session);
            """,
            FamilySwitchOwnerParameters(fixture, ids)
                .Append(("$fingerprint", (object)fingerprint))
                .ToArray());

    private static string NewRetiredOwnerWriteSql(FamilySwitchOwnerKind ownerKind) => ownerKind switch
    {
        FamilySwitchOwnerKind.Transfer => """
            INSERT INTO TransferOperations(Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,
              NormalizedTargetPath,CreateFolderName,AssignedAccountId,AllocationSource,Status,AttemptCount,
              OperationFingerprint,SubmitState,TargetSlotState,CreatedAt,UpdatedAt,
              ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES($transfer,$resource,$batch,1,1,'/','/','',$account,'Fixture','Waiting',0,
              'retired-transfer','NotPrepared','Unoccupied',$now,$now,
              'ManagedChromium-v1',$family,$profile,$installation,$session);
            """,
        FamilySwitchOwnerKind.Share => """
            INSERT INTO ShareOperations(Id,TransferOperationId,ResourceItemId,BatchJobId,AccountId,OutputOrdinal,
              Status,ExactItemSetFingerprint,SettingsFingerprint,Generation,SubmissionAcceptance,CreatedAt,UpdatedAt,
              ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES($share,$transfer,$resource,$batch,$account,1,'WaitingShare','retired-share','terminal-settings',1,
              'Unknown',$now,$now,'ManagedChromium-v1',$family,$profile,$installation,$session);
            """,
        FamilySwitchOwnerKind.StandaloneShare => """
            INSERT INTO StandaloneShareOperations(Id,BatchId,AccountId,SequenceNumber,DisplayName,ResourceKind,
              ParentPath,ExactItemSetFingerprint,Status,SubmissionState,SubmissionAcceptance,CreatedAt,UpdatedAt,
              ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES($standalone,$standaloneBatch,$account,1,'retired','File','/','retired-item','Queued',
              'NotStarted','Unknown',$now,$now,'ManagedChromium-v1',$family,$profile,$installation,$session);
            """,
        FamilySwitchOwnerKind.Attempt => """
            INSERT INTO WebAutomationAttempts(OperationId,AccountId,Generation,OwnerKind,State,
              Revision,CreatedAt,UpdatedAt,ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES($transfer,$account,1,'Transfer','Preparing',0,$now,$now,
              'ManagedChromium-v1',$family,$profile,$installation,$session);
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(ownerKind)),
    };

    private static async Task<IReadOnlyList<ManagedBrowserRetentionReference>>
        SeedNonTerminalFamilySwitchOwnerAsync(
            FamilySwitchFixture fixture,
            FamilySwitchOwnerKind ownerKind)
    {
        var ids = FamilySwitchOwnerIds.Create();
        await SeedFamilySwitchOwnerGraphAsync(fixture, ids, $"active-{ownerKind}");
        var sql = ownerKind switch
        {
            FamilySwitchOwnerKind.Transfer => """
                INSERT INTO TransferOperations(Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,
                  NormalizedTargetPath,CreateFolderName,AssignedAccountId,AllocationSource,Status,AttemptCount,
                  OperationFingerprint,SubmitState,TargetSlotState,CreatedAt,UpdatedAt,
                  ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
                VALUES($transfer,$resource,$batch,1,1,'/','/','',$account,'Fixture','Waiting',0,
                  'active-transfer','NotPrepared','Unoccupied',$now,$now,
                  'ManagedChromium-v1',$family,$profile,$installation,$session);
                """,
            FamilySwitchOwnerKind.Share => """
                INSERT INTO TransferOperations(Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,
                  NormalizedTargetPath,CreateFolderName,AssignedAccountId,AllocationSource,Status,OutcomeKind,AttemptCount,
                  OperationFingerprint,ConfirmationDescriptorJson,SubmitState,TargetSlotState,DailyCountedAt,
                  SubmitStartedAt,CreatedAt,UpdatedAt,ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
                VALUES($transfer,$resource,$batch,1,1,'/','/','',$account,'Fixture','Succeeded','NewTransfer',1,
                  'share-parent','{}','ResultReceived','Fulfilled',$now,$now,$now,$now,
                  'ManagedChromium-v1',$family,$profile,$installation,$session);
                INSERT INTO ShareOperations(Id,TransferOperationId,ResourceItemId,BatchJobId,AccountId,OutputOrdinal,
                  Status,ExactItemSetFingerprint,SettingsFingerprint,Generation,SubmissionAcceptance,CreatedAt,UpdatedAt,
                  ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
                VALUES($share,$transfer,$resource,$batch,$account,1,'WaitingShare','active-share','terminal-settings',1,
                  'Unknown',$now,$now,'ManagedChromium-v1',$family,$profile,$installation,$session);
                """,
            FamilySwitchOwnerKind.StandaloneShare => """
                INSERT INTO StandaloneShareOperations(Id,BatchId,AccountId,SequenceNumber,DisplayName,ResourceKind,
                  ParentPath,ExactItemSetFingerprint,Status,SubmissionState,SubmissionAcceptance,CreatedAt,UpdatedAt,
                  ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
                VALUES($standalone,$standaloneBatch,$account,1,'active','File','/','active-item','Queued',
                  'NotStarted','Unknown',$now,$now,'ManagedChromium-v1',$family,$profile,$installation,$session);
                """,
            FamilySwitchOwnerKind.Attempt => """
                INSERT INTO TransferOperations(Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,
                  NormalizedTargetPath,CreateFolderName,AssignedAccountId,AllocationSource,Status,OutcomeKind,AttemptCount,
                  OperationFingerprint,ConfirmationDescriptorJson,SubmitState,TargetSlotState,DailyCountedAt,
                  SubmitStartedAt,CreatedAt,UpdatedAt,ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
                VALUES($transfer,$resource,$batch,1,1,'/','/','',$account,'Fixture','Succeeded','NewTransfer',2,
                  'attempt-parent','{}','ResultReceived','Fulfilled',$now,$now,$now,$now,
                  'ManagedChromium-v1',$family,$profile,$installation,$session);
                INSERT INTO WebAutomationAttempts(OperationId,AccountId,Generation,OwnerKind,State,
                  Revision,CreatedAt,UpdatedAt,ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
                VALUES($transfer,$account,1,'Transfer','Preparing',0,$now,$now,
                  'ManagedChromium-v1',$family,$profile,$installation,$session);
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(ownerKind)),
        };
        await ExecuteAsync(fixture.Factory, sql, FamilySwitchOwnerParameters(fixture, ids));
        var operationIds = ownerKind switch
        {
            FamilySwitchOwnerKind.Transfer => new[] { ids.Transfer },
            FamilySwitchOwnerKind.Share => new[] { ids.Transfer, ids.Share },
            FamilySwitchOwnerKind.StandaloneShare => new[] { ids.Standalone },
            FamilySwitchOwnerKind.Attempt => new[] { ids.Transfer },
            _ => throw new ArgumentOutOfRangeException(nameof(ownerKind)),
        };
        var retentions = operationIds.Select(id => new ManagedBrowserRetentionReference(
            ManagedBrowserRetentionOwnerKind.Operation,
            id,
            fixture.OldInstallation.Identity.InstallationId,
            fixture.OldSession.Identity.SessionId)).ToList();
        if (ownerKind == FamilySwitchOwnerKind.Attempt)
            retentions.Add(new ManagedBrowserRetentionReference(
                ManagedBrowserRetentionOwnerKind.Attempt,
                ids.Transfer,
                fixture.OldInstallation.Identity.InstallationId,
                fixture.OldSession.Identity.SessionId));
        return retentions;
    }

    private static async Task SeedFamilySwitchOwnerGraphAsync(
        FamilySwitchFixture fixture,
        FamilySwitchOwnerIds ids,
        string suffix)
    {
        await ExecuteAsync(fixture.Factory, """
            INSERT INTO BatchJobs(Id,Name,SourceType,TransferMode,QuotaBasis,ShortagePolicy,ResourceCount,
              OperationCount,DefaultTargetPath,FolderCreationMode,DuplicatePolicy,ValidationMode,Status,CreatedAt,QueueOrder)
            VALUES($batch,$name,'Text','Distribution','TaskCount','ProportionalReduction',1,1,'/','None','Skip',
              'Immediate','Queued',$now,1);
            INSERT INTO ResourceItems(Id,BatchJobId,CandidateSequence,ShareUrl,NormalizedShareId,ExtractionCode,
              FolderCreationModeOverride,GlobalValidityStatus,CandidateStatus,CreatedAt,UpdatedAt)
            VALUES($resource,$batch,1,'https://pan.baidu.com/s/family-owner',$normalized,'1111','None','Valid','Expanded',$now,$now);
            INSERT INTO BatchShareSettings(BatchJobId,IsPermanent,IsAccessUnlimited,ExtractionCodeMode,
              FixedExtractionCode,ExistingSharePolicy,SettingsFingerprint,CreatedAt)
            VALUES($batch,1,1,'Random',NULL,'Reuse','terminal-settings',$now);
            INSERT INTO BatchOutputAccounts(BatchJobId,AccountId,OutputOrdinal,OutputLabel,
              PlannedTransferCount,PlannedShareCount,ShareEnabled)
            VALUES($batch,$account,1,'family-owner',1,1,1);
            INSERT INTO StandaloneShareBatches(Id,DraftFingerprint,ResultDirectoryReference,Status,
              OperationCount,Revision,CreatedAt,UpdatedAt)
            VALUES($standaloneBatch,$draft,$result,'Active',1,0,$now,$now);
            """,
            FamilySwitchOwnerParameters(fixture, ids)
                .Append(("$name", (object)$"family-{suffix}"))
                .Append(("$normalized", $"family-{suffix}"))
                .Append(("$draft", $"draft-{suffix}"))
                .Append(("$result", $"result-{suffix}"))
                .ToArray());
    }

    private static (string Name, object Value)[] FamilySwitchOwnerParameters(
        FamilySwitchFixture fixture,
        FamilySwitchOwnerIds ids) =>
    [
        ("$batch", ids.Batch.ToString("D")),
        ("$resource", ids.Resource.ToString("D")),
        ("$transfer", ids.Transfer.ToString("D")),
        ("$share", ids.Share.ToString("D")),
        ("$standaloneBatch", ids.StandaloneBatch.ToString("D")),
        ("$standalone", ids.Standalone.ToString("D")),
        ("$account", fixture.AccountId.ToString("D")),
        ("$family", fixture.OldSession.Identity.BrowserFamily.ToString()),
        ("$profile", fixture.OldSession.Identity.ProfileId),
        ("$installation", fixture.OldInstallation.Identity.InstallationId.ToString("D")),
        ("$session", fixture.OldSession.Identity.SessionId.ToString("D")),
        ("$now", DateTimeOffset.UtcNow.ToString("O")),
    ];

    private BrowserInstallationIdentity Installation(BrowserFamily family, string directoryName, string fileIdentity)
    {
        var directory = Path.GetFullPath(Path.Combine(root, directoryName));
        var executableName = family == BrowserFamily.Chrome ? "chrome.exe" : "msedge.exe";
        return new BrowserInstallationIdentity(
            Guid.NewGuid(),
            family,
            directory,
            Path.Combine(directory, executableName),
            family == BrowserFamily.Chrome ? "Google LLC" : "Microsoft Corporation",
            family == BrowserFamily.Chrome ? "Google Chrome" : "Microsoft Edge",
            fileIdentity);
    }

    private static ManagedBrowserSessionReference Session(
        Guid installationId,
        Guid accountId,
        string stableUid,
        BrowserFamily family,
        string profileId,
        string ownershipId) =>
        new(Guid.NewGuid(), installationId, accountId, stableUid, family, profileId, ownershipId);

    private ManagedProfileCleanupManifest CleanupManifest(
        Guid accountId,
        ManagedBrowserSessionReference session,
        string suffix) =>
        new(Guid.NewGuid(), accountId, session.SessionId,
            Path.Combine(root, $"source-secret-{suffix}"),
            Path.Combine(root, $"quarantine-secret-{suffix}"),
            session.ProfileOwnershipId, null);

    private async Task<(Guid OperationId, StoredManagedProfileCleanupManifest Manifest)> SeedManagedOwnersAsync(
        SqliteConnectionFactory factory,
        SqliteManagedBrowserStateStore store,
        Guid accountId,
        Guid installationId,
        ManagedBrowserSessionReference session)
    {
        var batchId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        await using (var connection = await factory.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO BatchJobs(Id,Name,SourceType,TransferMode,QuotaBasis,ShortagePolicy,ResourceCount,
                  OperationCount,DefaultTargetPath,FolderCreationMode,DuplicatePolicy,ValidationMode,Status,CreatedAt,QueueOrder)
                VALUES($batch,'job','Text','Distribution','TaskCount','ProportionalReduction',1,1,'/','None','Skip',
                  'Immediate','Queued',$now,1);
                INSERT INTO ResourceItems(Id,BatchJobId,CandidateSequence,ShareUrl,NormalizedShareId,ExtractionCode,
                  FolderCreationModeOverride,GlobalValidityStatus,CandidateStatus,CreatedAt,UpdatedAt)
                VALUES($resource,$batch,1,'https://pan.baidu.com/s/managed','managed','1111','None','Valid','Expanded',$now,$now);
                INSERT INTO TransferOperations(Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,
                  NormalizedTargetPath,CreateFolderName,AssignedAccountId,AllocationSource,Status,AttemptCount,
                  OperationFingerprint,SubmitState,TargetSlotState,CreatedAt,UpdatedAt,
                  ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
                VALUES($operation,$resource,$batch,1,1,'/','/','',$account,'Automatic','Waiting',1,'managed-fp',
                  'NotPrepared','Unoccupied',$now,$now,'ManagedChromium-v1',$family,$profile,$installation,$session);
                INSERT INTO WebAutomationAttempts(OperationId,AccountId,Generation,OwnerKind,State,
                  Revision,CreatedAt,UpdatedAt,ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
                VALUES($operation,$account,1,'Transfer','Preparing',0,$now,$now,
                  'ManagedChromium-v1',$family,$profile,$installation,$session);
                """;
            command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
            command.Parameters.AddWithValue("$resource", resourceId.ToString("D"));
            command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
            command.Parameters.AddWithValue("$account", accountId.ToString("D"));
            command.Parameters.AddWithValue("$family", session.BrowserFamily.ToString());
            command.Parameters.AddWithValue("$profile", session.ProfileId);
            command.Parameters.AddWithValue("$installation", installationId.ToString("D"));
            command.Parameters.AddWithValue("$session", session.SessionId.ToString("D"));
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        var manifest = await store.CreateCleanupManifestAsync(
            CleanupManifest(accountId, session, "retention"), default);
        return (operationId, manifest);
    }

    private async Task<CleanupFixture> CreateRetiredCleanupFixtureAsync(
        string suffix,
        bool retireInstallation = true,
        Func<CleanupFixture, Task>? beforeRetirement = null)
    {
        var (factory, store) = await CreateStoreAsync();
        var accountId = Guid.NewGuid();
        await SeedAccountAsync(factory, accountId, $"uid-{suffix}");
        var installation = await store.CreateInstallationAsync(
            Installation(BrowserFamily.Chrome, $"chrome-{suffix}", $"file-{suffix}"), default);
        installation = (await store.TryReplaceActiveInstallationAsync(
            new ManagedBrowserInstallationActivationRequest(null, installation), default)).Current!;
        var identity = Session(
            installation.Identity.InstallationId, accountId, $"uid-{suffix}", BrowserFamily.Chrome,
            $"profile-{suffix}", $"ownership-{suffix}");
        var session = await store.CreateSessionAsync(identity, default);
        session = (await store.TryReplaceActiveSessionAsync(
            new ManagedBrowserSessionActivationRequest(null, session), default)).Current!;
        var manifestIdentity = new ManagedProfileCleanupManifest(
            Guid.NewGuid(), accountId, identity.SessionId,
            Path.Combine(root, $"source-{suffix}"), Path.Combine(root, $"quarantine-{suffix}"),
            identity.ProfileOwnershipId, null);
        var manifest = await store.CreateCleanupManifestAsync(manifestIdentity, default);
        if (beforeRetirement is not null)
            await beforeRetirement(new CleanupFixture(
                factory, store, accountId, installation, session, manifest));
        var retiredSession = await store.TryRetireSessionAsync(
            new ManagedBrowserTransitionRequest(identity.SessionId, session.Revision), default);
        Assert.Equal(ManagedBrowserTransitionStatus.Applied, retiredSession.Status);
        session = (await store.LoadSessionAsync(identity.SessionId, default))!;
        if (retireInstallation)
        {
            var retiredInstallation = await store.TryRetireInstallationAsync(
                new ManagedBrowserTransitionRequest(installation.Identity.InstallationId, installation.Revision), default);
            Assert.Equal(ManagedBrowserTransitionStatus.Applied, retiredInstallation.Status);
            installation = (await store.LoadInstallationAsync(installation.Identity.InstallationId, default))!;
        }
        return new CleanupFixture(factory, store, accountId, installation, session, manifest);
    }

    private static async Task AssertManagedOwnerAndAttemptInsertsRejectedAsync(CleanupFixture fixture)
    {
        var batchId = Guid.NewGuid();
        var transferResourceId = Guid.NewGuid();
        var shareResourceId = Guid.NewGuid();
        var legacyTransferId = Guid.NewGuid();
        var standaloneBatchId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");
        await ExecuteAsync(fixture.Factory, """
            INSERT INTO BatchJobs(Id,Name,SourceType,TransferMode,QuotaBasis,ShortagePolicy,ResourceCount,
              OperationCount,DefaultTargetPath,FolderCreationMode,DuplicatePolicy,ValidationMode,Status,CreatedAt,QueueOrder)
            VALUES($batch,'deleted-fence','Text','Distribution','TaskCount','ProportionalReduction',2,2,'/','None','Skip',
              'Immediate','Queued',$now,1);
            INSERT INTO ResourceItems(Id,BatchJobId,CandidateSequence,ShareUrl,NormalizedShareId,ExtractionCode,
              FolderCreationModeOverride,GlobalValidityStatus,CandidateStatus,CreatedAt,UpdatedAt)
            VALUES
              ($transferResource,$batch,1,'https://pan.baidu.com/s/deleted-transfer','deleted-transfer','1111','None','Valid','Expanded',$now,$now),
              ($shareResource,$batch,2,'https://pan.baidu.com/s/deleted-share','deleted-share','1111','None','Valid','Expanded',$now,$now);
            INSERT INTO BatchShareSettings(BatchJobId,IsPermanent,IsAccessUnlimited,ExtractionCodeMode,
              FixedExtractionCode,ExistingSharePolicy,SettingsFingerprint,CreatedAt)
            VALUES($batch,1,1,'Random',NULL,'Reuse','deleted-fence-settings',$now);
            INSERT INTO BatchOutputAccounts(BatchJobId,AccountId,OutputOrdinal,OutputLabel,
              PlannedTransferCount,PlannedShareCount,ShareEnabled)
            VALUES($batch,$account,1,'deleted-fence-output',2,1,1);
            INSERT INTO TransferOperations(Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,
              NormalizedTargetPath,CreateFolderName,AssignedAccountId,AllocationSource,Status,AttemptCount,
              OperationFingerprint,SubmitState,TargetSlotState,CreatedAt,UpdatedAt,
              ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES($legacyTransfer,$shareResource,$batch,2,2,'/','/','',$account,'Fixture','Waiting',0,
              'deleted-fence-legacy','NotPrepared','Unoccupied',$now,$now,
              'WebView2-v1','LegacyWebView2',$legacyProfile,NULL,NULL);
            INSERT INTO StandaloneShareBatches(Id,DraftFingerprint,ResultDirectoryReference,Status,
              OperationCount,Revision,CreatedAt,UpdatedAt)
            VALUES($standaloneBatch,'deleted-fence-draft','deleted-fence-result','Active',1,0,$now,$now);
            """,
            ("$batch", batchId.ToString("D")),
            ("$transferResource", transferResourceId.ToString("D")),
            ("$shareResource", shareResourceId.ToString("D")),
            ("$legacyTransfer", legacyTransferId.ToString("D")),
            ("$standaloneBatch", standaloneBatchId.ToString("D")),
            ("$account", fixture.AccountId.ToString("D")),
            ("$legacyProfile", $"legacy-webview2-account-{fixture.AccountId:N}"),
            ("$now", now));

        var snapshotParameters = new (string Name, object Value)[]
        {
            ("$account", fixture.AccountId.ToString("D")),
            ("$family", fixture.Session.Identity.BrowserFamily.ToString()),
            ("$profile", fixture.Session.Identity.ProfileId),
            ("$installation", fixture.Installation.Identity.InstallationId.ToString("D")),
            ("$session", fixture.Session.Identity.SessionId.ToString("D")),
            ("$now", now),
        };
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => ExecuteAsync(
            fixture.Factory,
            """
            INSERT INTO TransferOperations(Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,
              NormalizedTargetPath,CreateFolderName,AssignedAccountId,AllocationSource,Status,AttemptCount,
              OperationFingerprint,SubmitState,TargetSlotState,CreatedAt,UpdatedAt,
              ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES($operation,$resource,$batch,1,1,'/','/','',$account,'Fixture','Waiting',0,
              'deleted-fence-transfer','NotPrepared','Unoccupied',$now,$now,
              'ManagedChromium-v1',$family,$profile,$installation,$session);
            """,
            snapshotParameters.Append(("$operation", Guid.NewGuid().ToString("D")))
                .Append(("$resource", transferResourceId.ToString("D")))
                .Append(("$batch", batchId.ToString("D"))).ToArray()));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => ExecuteAsync(
            fixture.Factory,
            """
            INSERT INTO ShareOperations(Id,TransferOperationId,ResourceItemId,BatchJobId,AccountId,OutputOrdinal,
              Status,SettingsFingerprint,Generation,SubmissionAcceptance,CreatedAt,UpdatedAt,
              ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES($operation,$transfer,$resource,$batch,$account,1,'WaitingTransfer','deleted-fence-settings',1,
              'Unknown',$now,$now,'ManagedChromium-v1',$family,$profile,$installation,$session);
            """,
            snapshotParameters.Append(("$operation", Guid.NewGuid().ToString("D")))
                .Append(("$transfer", legacyTransferId.ToString("D")))
                .Append(("$resource", shareResourceId.ToString("D")))
                .Append(("$batch", batchId.ToString("D"))).ToArray()));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => ExecuteAsync(
            fixture.Factory,
            """
            INSERT INTO StandaloneShareOperations(Id,BatchId,AccountId,SequenceNumber,DisplayName,ResourceKind,
              ParentPath,ExactItemSetFingerprint,Status,SubmissionState,SubmissionAcceptance,CreatedAt,UpdatedAt,
              ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES($operation,$batch,$account,1,'deleted-fence','File','/','deleted-fence-item','Queued',
              'NotStarted','Unknown',$now,$now,'ManagedChromium-v1',$family,$profile,$installation,$session);
            """,
            snapshotParameters.Append(("$operation", Guid.NewGuid().ToString("D")))
                .Append(("$batch", standaloneBatchId.ToString("D"))).ToArray()));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => ExecuteAsync(
            fixture.Factory,
            """
            INSERT INTO WebAutomationAttempts(OperationId,AccountId,Generation,OwnerKind,State,
              Revision,CreatedAt,UpdatedAt,ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES($operation,$account,1,'RemoteSelection','Preparing',0,$now,$now,
              'ManagedChromium-v1',$family,$profile,$installation,$session);
            """,
            snapshotParameters.Append(("$operation", Guid.NewGuid().ToString("D"))).ToArray()));
    }

    private static async Task<Guid> InsertManagedTransferOwnerAsync(CleanupFixture fixture)
    {
        var batchId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        await using var connection = await fixture.Factory.OpenConnectionAsync();
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO BatchJobs(Id,Name,SourceType,TransferMode,QuotaBasis,ShortagePolicy,ResourceCount,
              OperationCount,DefaultTargetPath,FolderCreationMode,DuplicatePolicy,ValidationMode,Status,CreatedAt,QueueOrder)
            VALUES($batch,'job','Text','Distribution','TaskCount','ProportionalReduction',1,1,'/','None','Skip',
              'Immediate','Queued',$now,1);
            INSERT INTO ResourceItems(Id,BatchJobId,CandidateSequence,ShareUrl,NormalizedShareId,ExtractionCode,
              FolderCreationModeOverride,GlobalValidityStatus,CandidateStatus,CreatedAt,UpdatedAt)
            VALUES($resource,$batch,1,'https://pan.baidu.com/s/claim','claim','1111','None','Valid','Expanded',$now,$now);
            INSERT INTO TransferOperations(Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,
              NormalizedTargetPath,CreateFolderName,AssignedAccountId,AllocationSource,Status,AttemptCount,
              OperationFingerprint,SubmitState,TargetSlotState,CreatedAt,UpdatedAt,
              ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES($operation,$resource,$batch,1,1,'/','/','',$account,'Automatic','Waiting',0,'claim-fp',
              'NotPrepared','Unoccupied',$now,$now,'ManagedChromium-v1',$family,$profile,$installation,$session);
            """;
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        command.Parameters.AddWithValue("$resource", resourceId.ToString("D"));
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.Parameters.AddWithValue("$account", fixture.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$family", fixture.Session.Identity.BrowserFamily.ToString());
        command.Parameters.AddWithValue("$profile", fixture.Session.Identity.ProfileId);
        command.Parameters.AddWithValue("$installation", fixture.Installation.Identity.InstallationId.ToString("D"));
        command.Parameters.AddWithValue("$session", fixture.Session.Identity.SessionId.ToString("D"));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return operationId;
    }

    private static async Task InsertManagedRemoteSelectionAttemptAsync(CleanupFixture fixture)
    {
        await using var connection = await fixture.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO WebAutomationAttempts(OperationId,AccountId,Generation,OwnerKind,State,
              Revision,CreatedAt,UpdatedAt,ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES($operation,$account,1,'RemoteSelection','Preparing',0,$now,$now,
              'ManagedChromium-v1',$family,$profile,$installation,$session);
            """;
        command.Parameters.AddWithValue("$operation", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$account", fixture.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$family", fixture.Session.Identity.BrowserFamily.ToString());
        command.Parameters.AddWithValue("$profile", fixture.Session.Identity.ProfileId);
        command.Parameters.AddWithValue("$installation", fixture.Installation.Identity.InstallationId.ToString("D"));
        command.Parameters.AddWithValue("$session", fixture.Session.Identity.SessionId.ToString("D"));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarLongAsync(
        SqliteConnectionFactory factory,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ScalarTextAsync(
        SqliteConnectionFactory factory,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnectionFactory factory,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return await command.ExecuteNonQueryAsync();
    }

    private sealed record CleanupFixture(
        SqliteConnectionFactory Factory,
        SqliteManagedBrowserStateStore Store,
        Guid AccountId,
        StoredBrowserInstallation Installation,
        StoredManagedBrowserSession Session,
        StoredManagedProfileCleanupManifest Manifest)
    {
        public ManagedBrowserCleanupClaimRequest ClaimRequest() => new(
            Installation.Identity.InstallationId,
            Session.Identity.SessionId,
            AccountId,
            Installation.Revision,
            Session.Revision,
            Manifest.Manifest.ManifestId,
            Manifest.Revision);
    }

    private sealed record FamilySwitchFixture(
        SqliteConnectionFactory Factory,
        SqliteManagedBrowserStateStore Store,
        Guid AccountId,
        string StableUid,
        StoredBrowserInstallation OldInstallation,
        StoredManagedBrowserSession OldSession,
        ManagedBrowserFamilySwitchTransitionRequest Request);

    public enum FamilySwitchOwnerKind
    {
        Transfer,
        Share,
        StandaloneShare,
        Attempt,
    }

    private sealed record FamilySwitchOwnerIds(
        Guid Batch,
        Guid Resource,
        Guid Transfer,
        Guid Share,
        Guid StandaloneBatch,
        Guid Standalone)
    {
        public static FamilySwitchOwnerIds Create() => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());
    }

    private static async Task DeleteManagedOperationAndAttemptAsync(
        SqliteConnectionFactory factory,
        Guid operationId)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM WebAutomationAttempts WHERE OperationId=$operation;
            DELETE FROM TransferOperations WHERE Id=$operation;
            """;
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountRetentionsAsync(SqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ManagedBrowserRetentions;";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task SeedAccountAsync(
        SqliteConnectionFactory factory,
        Guid accountId,
        string stableUid)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Accounts(
              Id,BaiduUserId,DisplayName,LoginMethod,Status,DailyLimit,DailyExecutedCount,
              SortOrder,IsEnabled,DailyCountDateKey,IsDeleted)
            VALUES($id,$uid,'account','WebView2','Active',0,0,1,1,'2026-08-01',0);
            """;
        command.Parameters.AddWithValue("$id", accountId.ToString("D"));
        command.Parameters.AddWithValue("$uid", stableUid);
        await command.ExecuteNonQueryAsync();
    }
}
