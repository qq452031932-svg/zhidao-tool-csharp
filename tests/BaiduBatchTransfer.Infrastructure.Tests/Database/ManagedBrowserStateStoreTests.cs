using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;

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

        var current = manifest;
        foreach (var target in new[]
                 {
                     ManagedProfileCleanupState.QuarantineMoveIntent,
                     ManagedProfileCleanupState.Quarantined,
                     ManagedProfileCleanupState.DeleteIntent,
                     ManagedProfileCleanupState.CleanupPending,
                     ManagedProfileCleanupState.Deleted,
                 })
        {
            var transition = await store.TryTransitionCleanupManifestAsync(
                new ManagedProfileCleanupTransitionRequest(
                    current.Manifest.ManifestId, current.Revision, current.State, target), default);
            Assert.Equal(ManagedBrowserTransitionStatus.Applied, transition.Status);
            current = (await store.LoadCleanupManifestAsync(current.Manifest.ManifestId, default))!;
        }
        Assert.Equal(0L, await CountRetentionsAsync(factory));
        Assert.True(await store.IsRetirementEligibleAsync(
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
        _ = await store.TryReplaceActiveSessionAsync(
            new ManagedBrowserSessionActivationRequest(null, session), default);

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

        var request = new ManagedProfileCleanupTransitionRequest(
            manifest.ManifestId, 0, ManagedProfileCleanupState.Prepared,
            ManagedProfileCleanupState.QuarantineMoveIntent);
        var applied = await restarted.TryTransitionCleanupManifestAsync(request, default);
        Assert.Equal(ManagedBrowserTransitionStatus.Applied, applied.Status);
        Assert.Equal(1, applied.CurrentRevision);
        var stale = await restarted.TryTransitionCleanupManifestAsync(request, default);
        Assert.Equal(ManagedBrowserTransitionStatus.Conflict, stale.Status);
        Assert.Equal(1, stale.CurrentRevision);
        Assert.Equal(ManagedProfileCleanupState.QuarantineMoveIntent,
            (await restarted.LoadCleanupManifestAsync(manifest.ManifestId, default))!.State);
        Assert.Equal(ManagedBrowserTransitionStatus.NotFound,
            (await restarted.TryTransitionCleanupManifestAsync(
                new ManagedProfileCleanupTransitionRequest(
                    Guid.NewGuid(), 0, ManagedProfileCleanupState.Prepared,
                    ManagedProfileCleanupState.QuarantineMoveIntent), default)).Status);
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
            corrupt.CommandText = "PRAGMA ignore_check_constraints=ON; UPDATE ManagedProfileCleanupManifests SET State='UnknownState' WHERE ManifestId=$id; PRAGMA ignore_check_constraints=OFF;";
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

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private async Task<(SqliteConnectionFactory Factory, SqliteManagedBrowserStateStore Store)> CreateStoreAsync()
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "state.db"));
        await new DatabaseMigrator(
            factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
        return (factory, new SqliteManagedBrowserStateStore(factory));
    }

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
            session.ProfileOwnershipId, $"confirmation-{suffix}");

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
