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
    public async Task RetiredIdentityIsEligibleOnlyAfterEveryRetentionOwnerReleases()
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
        Assert.True(await store.IsRetirementEligibleAsync(
            installation.Identity.InstallationId, sessionIdentity.SessionId, default));

        var retentions = new[]
        {
            new ManagedBrowserRetentionReference(
                ManagedBrowserRetentionOwnerKind.Operation, Guid.NewGuid(),
                installation.Identity.InstallationId, sessionIdentity.SessionId),
            new ManagedBrowserRetentionReference(
                ManagedBrowserRetentionOwnerKind.Attempt, Guid.NewGuid(),
                installation.Identity.InstallationId, sessionIdentity.SessionId),
            new ManagedBrowserRetentionReference(
                ManagedBrowserRetentionOwnerKind.CleanupManifest, Guid.NewGuid(),
                installation.Identity.InstallationId, sessionIdentity.SessionId),
        };
        foreach (var retention in retentions) await store.AddRetentionAsync(retention, default);
        await store.AddRetentionAsync(retentions[0], default);
        Assert.False(await store.IsRetirementEligibleAsync(
            installation.Identity.InstallationId, sessionIdentity.SessionId, default));

        await store.ReleaseRetentionAsync(retentions[0], default);
        Assert.False(await store.IsRetirementEligibleAsync(
            installation.Identity.InstallationId, sessionIdentity.SessionId, default));
        await store.ReleaseRetentionAsync(retentions[1], default);
        Assert.False(await store.IsRetirementEligibleAsync(
            installation.Identity.InstallationId, sessionIdentity.SessionId, default));
        await store.ReleaseRetentionAsync(retentions[2], default);
        await store.ReleaseRetentionAsync(retentions[2], default);
        Assert.True(await store.IsRetirementEligibleAsync(
            installation.Identity.InstallationId, sessionIdentity.SessionId, default));

        Assert.Equal(ManagedBrowserTransitionStatus.NotFound,
            (await store.TryRetireInstallationAsync(
                new ManagedBrowserTransitionRequest(Guid.NewGuid(), 0), default)).Status);
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
