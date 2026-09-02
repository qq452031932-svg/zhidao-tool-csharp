using BaiduBatchTransfer.Infrastructure.Database;
using BaiduBatchTransfer.Infrastructure.Database.Repositories;

namespace BaiduBatchTransfer.Infrastructure.Tests.Database;

public sealed class ShareAnalyticsPreferenceStoreTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"bbt-share-analytics-preference-{Guid.NewGuid():N}");

    [Fact]
    public async Task InitialPreferenceIsNull()
    {
        var (_, store) = await CreateAsync();

        Assert.Null(await store.LoadLastViewedAccountAsync());
    }

    [Fact]
    public async Task AccountPreferenceRoundTripsAndCanBeCleared()
    {
        var (_, store) = await CreateAsync();
        var accountId = await AddAccountAsync();

        await store.SaveLastViewedAccountAsync(accountId);
        Assert.Equal(accountId, await new SqliteShareAnalyticsPreferenceStore(
            new SqliteConnectionFactory(Path.Combine(root, "preferences.db")))
            .LoadLastViewedAccountAsync());

        await store.SaveLastViewedAccountAsync(null);
        Assert.Null(await store.LoadLastViewedAccountAsync());
    }

    [Fact]
    public async Task AccountDeletionAutomaticallyClearsPreference()
    {
        var (factory, store) = await CreateAsync();
        var accountId = await AddAccountAsync();
        await store.SaveLastViewedAccountAsync(accountId);

        await using (var connection = await factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM Accounts WHERE Id=$id;";
            command.Parameters.AddWithValue("$id", accountId.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        Assert.Null(await store.LoadLastViewedAccountAsync());
    }

    [Fact]
    public async Task EmptyAccountIdIsRejected()
    {
        var (_, store) = await CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SaveLastViewedAccountAsync(Guid.Empty));
    }

    private async Task<(SqliteConnectionFactory Factory, SqliteShareAnalyticsPreferenceStore Store)> CreateAsync()
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "preferences.db"));
        await new DatabaseMigrator(
            factory,
            new DatabaseBackupService(factory, Path.Combine(root, "backups")),
            "test").MigrateAsync();
        return (factory, new SqliteShareAnalyticsPreferenceStore(factory));
    }

    private async Task<Guid> AddAccountAsync()
    {
        var accountId = Guid.NewGuid();
        var factory = new SqliteConnectionFactory(Path.Combine(root, "preferences.db"));
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Accounts(
              Id,BaiduUserId,DisplayName,LoginMethod,Status,DailyLimit,DailyExecutedCount,
              SortOrder,IsEnabled,DailyCountDateKey,IsDeleted)
            VALUES($id,$uid,'fixture','WebView2','Active',0,0,0,1,'2026-09-02',0);
            """;
        command.Parameters.AddWithValue("$id", accountId.ToString("D"));
        command.Parameters.AddWithValue("$uid", accountId.ToString("N"));
        await command.ExecuteNonQueryAsync();
        return accountId;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
