using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;

namespace BaiduBatchTransfer.Infrastructure.Tests.Database;

public sealed class ExecutionBindingStoreTests
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"synthetic-execution-binding-{Guid.NewGuid():N}");
    private readonly Guid accountId = Guid.NewGuid();
    private const string Uid = "synthetic-execution-uid";

    [Theory]
    [InlineData(BrowserFamily.Chrome)]
    [InlineData(BrowserFamily.Edge)]
    public async Task LoadReturnsExactActiveMetadataWithoutWriting(BrowserFamily family)
    {
        var (factory, store) = await CreateStoreAsync();
        var installation = await ActivateInstallationAsync(store, family);
        var session = await ActivateSessionAsync(store, installation);
        await using var observer = await factory.OpenConnectionAsync();
        await using var version = observer.CreateCommand();
        version.CommandText = "PRAGMA data_version;";
        var before = await version.ExecuteScalarAsync();

        var result = await ((IBaiduExecutionBindingStore)store).LoadAsync(accountId, Profile(session), default);

        Assert.Equal(before, await version.ExecuteScalarAsync());
        Assert.NotNull(result.Account);
        Assert.NotNull(result.Installation);
        Assert.NotNull(result.Session);
        Assert.Equal(accountId, result.Account!.Id);
        Assert.Equal(Uid, result.Account.BaiduUserId);
        Assert.Equal("Active", result.Account.Status);
        Assert.True(result.Account.IsEnabled);
        Assert.False(result.Account.IsDeleted);
        Assert.Equal(installation.Identity.InstallationId, result.Installation!.Identity.InstallationId);
        Assert.Equal(installation.Revision, result.Installation.Revision);
        Assert.Equal(BrowserInstallationLifecycleState.Active, result.Installation.State);
        Assert.Equal(session.Identity.SessionId, result.Session!.Identity.SessionId);
        Assert.Equal(session.Identity.ProfileOwnershipId, result.Session.Identity.ProfileOwnershipId);
        Assert.Equal(session.Revision, result.Session.Revision);
        Assert.Equal(ManagedBrowserSessionLifecycleState.Active, result.Session.State);
    }

    [Fact]
    public async Task ExactHistoricalSessionIsReturnedInsteadOfTheLatestActiveSession()
    {
        var (_, store) = await CreateStoreAsync();
        var installation = await ActivateInstallationAsync(store, BrowserFamily.Chrome);
        var original = await ActivateSessionAsync(store, installation);
        var replacement = await ActivateSessionAsync(store, installation, original);

        var result = await ((IBaiduExecutionBindingStore)store).LoadAsync(accountId, Profile(original), default);

        Assert.NotNull(result.Session);
        Assert.Equal(original.Identity.SessionId, result.Session!.Identity.SessionId);
        Assert.NotEqual(replacement.Identity.SessionId, result.Session.Identity.SessionId);
        Assert.Equal(ManagedBrowserSessionLifecycleState.Retired, result.Session.State);
        Assert.Equal(original.Revision + 1, result.Session.Revision);
        Assert.Equal(ManagedBrowserSessionLifecycleState.Active, (await store.LoadSessionAsync(replacement.Identity.SessionId, default))!.State);
    }

    [Fact]
    public async Task ExactRetiredInstallationIsReturnedInsteadOfActiveReplacement()
    {
        var (_, store) = await CreateStoreAsync();
        var original = await ActivateInstallationAsync(store, BrowserFamily.Chrome);
        var replacement = await ActivateInstallationAsync(store, BrowserFamily.Edge, original);
        var profile = ExecutionProfileSnapshot.Managed(BrowserFamily.Chrome, "synthetic-missing-profile", original.Identity.InstallationId, Guid.NewGuid());

        var result = await ((IBaiduExecutionBindingStore)store).LoadAsync(accountId, profile, default);

        Assert.NotNull(result.Installation);
        Assert.Equal(original.Identity.InstallationId, result.Installation!.Identity.InstallationId);
        Assert.NotEqual(replacement.Identity.InstallationId, result.Installation.Identity.InstallationId);
        Assert.Equal(BrowserInstallationLifecycleState.Retired, result.Installation.State);
        Assert.Null(result.Session);
    }

    [Fact]
    public async Task MissingExactIdsNeverFallBackToCurrentActiveRows()
    {
        var (_, store) = await CreateStoreAsync();
        var installation = await ActivateInstallationAsync(store, BrowserFamily.Chrome);
        await ActivateSessionAsync(store, installation);
        var result = await ((IBaiduExecutionBindingStore)store).LoadAsync(accountId,
            ExecutionProfileSnapshot.Managed(BrowserFamily.Chrome, "synthetic-absent-profile", Guid.NewGuid(), Guid.NewGuid()), default);
        Assert.NotNull(result.Account);
        Assert.Null(result.Installation);
        Assert.Null(result.Session);
    }

    [Theory]
    [InlineData("Active", 0, 0)]
    [InlineData("Invalid", 1, 0)]
    [InlineData("Disabled", 0, 0)]
    [InlineData("Deleted", 0, 1)]
    public async Task AccountAvailabilityMetadataIsPreservedWithoutFiltering(string status, int enabled, int deleted)
    {
        var (factory, store) = await CreateStoreAsync();
        await using (var connection = await factory.OpenConnectionAsync())
        {
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE Accounts SET Status=$status,IsEnabled=$enabled,IsDeleted=$deleted WHERE Id=$id;";
            update.Parameters.AddWithValue("$status", status);
            update.Parameters.AddWithValue("$enabled", enabled);
            update.Parameters.AddWithValue("$deleted", deleted);
            update.Parameters.AddWithValue("$id", accountId.ToString("D"));
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }
        var result = await ((IBaiduExecutionBindingStore)store).LoadAsync(accountId, ExecutionProfileSnapshot.LegacyForAccount(accountId), default);
        Assert.NotNull(result.Account);
        Assert.Equal(status, result.Account!.Status);
        Assert.Equal(enabled != 0, result.Account.IsEnabled);
        Assert.Equal(deleted != 0, result.Account.IsDeleted);
    }

    [Fact]
    public async Task UnknownAccountIsNotSubstituted()
    {
        var (_, store) = await CreateStoreAsync();
        var existing = await ((IBaiduExecutionBindingStore)store).LoadAsync(accountId, ExecutionProfileSnapshot.LegacyForAccount(accountId), default);
        Assert.NotNull(existing.Account);
        var missing = Guid.NewGuid();
        var result = await ((IBaiduExecutionBindingStore)store).LoadAsync(missing, ExecutionProfileSnapshot.LegacyForAccount(missing), default);
        Assert.Null(result.Account);
        Assert.Null(result.Installation);
        Assert.Null(result.Session);
    }

    [Fact]
    public async Task LegacyLoadNeedsNeitherSecretColumnsNorManagedTables()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(root, "metadata-only.db"));
        await using (var connection = await factory.OpenConnectionAsync())
        {
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                CREATE TABLE Accounts(Id TEXT PRIMARY KEY,BaiduUserId TEXT,Status TEXT,IsEnabled INTEGER,IsDeleted INTEGER);
                INSERT INTO Accounts VALUES($id,$uid,'Active',1,0);
                """;
            seed.Parameters.AddWithValue("$id", accountId.ToString("D"));
            seed.Parameters.AddWithValue("$uid", Uid);
            await seed.ExecuteNonQueryAsync();
        }
        var store = (IBaiduExecutionBindingStore)new SqliteManagedBrowserStateStore(factory);
        var result = await store.LoadAsync(accountId, ExecutionProfileSnapshot.LegacyForAccount(accountId), default);
        Assert.NotNull(result.Account);
        Assert.Equal(Uid, result.Account!.BaiduUserId);
        Assert.Null(result.Installation);
        Assert.Null(result.Session);
    }

    [Fact]
    public async Task InvalidInputsAndCancellationDoNotCreateOrOpenADatabase()
    {
        var databasePath = Path.Combine(root, "must-not-exist.db");
        var factory = new SqliteConnectionFactory(databasePath);
        var store = (IBaiduExecutionBindingStore)new SqliteManagedBrowserStateStore(factory);
        var profile = ExecutionProfileSnapshot.LegacyForAccount(accountId);
        await Assert.ThrowsAsync<ArgumentException>(() => store.LoadAsync(Guid.Empty, profile, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.LoadAsync(accountId, null!, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(accountId, profile, cancellation.Token));
        Assert.False(File.Exists(databasePath));
        Assert.False(Directory.Exists(root));
        Assert.Equal(0, factory.ActiveConnectionCount);
    }

    [Fact]
    public void SnapshotSurfaceIsImmutableRedactedAndExcludesAccountSecrets()
    {
        Assert.Equal(new[] { "BaiduUserId", "Id", "IsDeleted", "IsEnabled", "Status" },
            typeof(BaiduExecutionAccount).GetProperties().Select(x => x.Name).OrderBy(x => x).ToArray());
        Assert.All(typeof(BaiduExecutionAccount).GetProperties(), x => Assert.Null(x.SetMethod));
        Assert.All(typeof(BaiduExecutionBindingSnapshot).GetProperties(), x => Assert.Null(x.SetMethod));
        var account = new BaiduExecutionAccount(accountId, Uid, "untrusted-status-marker", true, false);
        var snapshot = new BaiduExecutionBindingSnapshot(account, null, null);
        Assert.Contains("[REDACTED]", account.ToString());
        Assert.Contains("[REDACTED]", snapshot.ToString());
        Assert.DoesNotContain(Uid, account.ToString());
        Assert.DoesNotContain(accountId.ToString("D"), snapshot.ToString());
        Assert.DoesNotContain("untrusted-status-marker", account.ToString());
    }

    private async Task<(SqliteConnectionFactory Factory, SqliteManagedBrowserStateStore Store)> CreateStoreAsync()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(root, "state.db"));
        await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
        await using var connection = await factory.OpenConnectionAsync();
        await using var seed = connection.CreateCommand();
        seed.CommandText = """
            INSERT INTO Accounts(Id,BaiduUserId,DisplayName,EncryptedSession,WebView2ProfileDirectory,LoginMethod,
                Status,DailyLimit,DailyExecutedCount,SortOrder,IsEnabled,DailyCountDateKey,IsDeleted)
            VALUES($id,$uid,'synthetic-account','synthetic-encrypted-secret','synthetic-legacy-path','WebView2',
                'Active',0,0,1,1,'2026-08-28',0);
            """;
        seed.Parameters.AddWithValue("$id", accountId.ToString("D"));
        seed.Parameters.AddWithValue("$uid", Uid);
        await seed.ExecuteNonQueryAsync();
        return (factory, new SqliteManagedBrowserStateStore(factory));
    }

    private async Task<StoredBrowserInstallation> ActivateInstallationAsync(SqliteManagedBrowserStateStore store,
        BrowserFamily family, StoredBrowserInstallation? current = null)
    {
        var directory = Path.Combine(root, "synthetic-installation", Guid.NewGuid().ToString("N"));
        var identity = new BrowserInstallationIdentity(Guid.NewGuid(), family, directory,
            Path.Combine(directory, family == BrowserFamily.Chrome ? "chrome.exe" : "msedge.exe"),
            family == BrowserFamily.Chrome ? "Google LLC" : "Microsoft Corporation",
            family == BrowserFamily.Chrome ? "Google Chrome" : "Microsoft Edge", Guid.NewGuid().ToString("N"));
        var created = await store.CreateInstallationAsync(identity, default);
        Assert.Equal(BrowserInstallationLifecycleState.Retired, created.State);
        var activation = await store.TryReplaceActiveInstallationAsync(new(current, created), default);
        Assert.Equal(ManagedBrowserTransitionStatus.Applied, activation.Status);
        return activation.Current!;
    }

    private async Task<StoredManagedBrowserSession> ActivateSessionAsync(SqliteManagedBrowserStateStore store,
        StoredBrowserInstallation installation, StoredManagedBrowserSession? current = null)
    {
        var identity = new ManagedBrowserSessionReference(Guid.NewGuid(), installation.Identity.InstallationId,
            accountId, Uid, installation.Identity.BrowserFamily, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        var created = await store.CreateSessionAsync(identity, default);
        Assert.Equal(ManagedBrowserSessionLifecycleState.Retired, created.State);
        var activation = await store.TryReplaceActiveSessionAsync(new(current, created), default);
        Assert.Equal(ManagedBrowserTransitionStatus.Applied, activation.Status);
        return activation.Current!;
    }

    private static ExecutionProfileSnapshot Profile(StoredManagedBrowserSession session) =>
        ExecutionProfileSnapshot.Managed(session.Identity.BrowserFamily, session.Identity.ProfileId,
            session.Identity.InstallationId, session.Identity.SessionId);
}
