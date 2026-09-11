using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;
using BaiduBatchTransfer.Infrastructure.Database.Repositories;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Tests.Database;

public sealed class AccountShareAnalyticsRepositoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bbt-share-cache-{Guid.NewGuid():N}");

    [Fact]
    public async Task CacheIsStrictlyAccountScopedAndAccountDeletionCascades()
    {
        var (factory, repository, accountA, accountB) = await CreateAsync();
        var generationA = await repository.ReserveGenerationAsync(accountA, default);
        var generationB = await repository.ReserveGenerationAsync(accountB, default);
        await repository.CommitCompleteSyncAsync(Commit(accountA, generationA, "share-a", AccountShareRemoteStatus.Valid, 10), default);
        await repository.CommitCompleteSyncAsync(Commit(accountB, generationB, "share-b", AccountShareRemoteStatus.Valid, 20), default);

        Assert.Equal("share-a", Assert.Single((await repository.LoadCachedAsync(accountA, default)).Items).ShareId);
        Assert.Equal("share-b", Assert.Single((await repository.LoadCachedAsync(accountB, default)).Items).ShareId);

        await using (var connection = await factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM Accounts WHERE Id=$id;";
            command.Parameters.AddWithValue("$id", accountA.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }
        Assert.Empty((await repository.LoadCachedAsync(accountA, default)).Items);
        Assert.Equal("share-b", Assert.Single((await repository.LoadCachedAsync(accountB, default)).Items).ShareId);
    }

    [Fact]
    public async Task CompleteCommitArchivesAbsentRecordsAndKeepsExplicitInvalidRecordsCurrent()
    {
        var (_, repository, account, _) = await CreateAsync();
        var first = await repository.ReserveGenerationAsync(account, default);
        await repository.CommitCompleteSyncAsync(new(account, first, At(2),
        [
            Observation("gone", AccountShareRemoteStatus.Valid, 10),
            Observation("kept", AccountShareRemoteStatus.Valid, 20),
        ]), default);

        var second = await repository.ReserveGenerationAsync(account, default);
        Assert.True(await repository.CommitCompleteSyncAsync(new(account, second, At(3),
        [
            Observation("kept", AccountShareRemoteStatus.Invalid, 25),
        ]), default));

        var snapshot = await repository.LoadCachedAsync(account, default);
        var kept = Assert.Single(snapshot.Items);
        Assert.Equal("kept", kept.ShareId);
        Assert.Equal(AccountShareRemoteStatus.Invalid, kept.Status);
        Assert.Equal(At(3), snapshot.LastSuccessfulSyncAtUtc);
    }

    [Fact]
    public async Task SameBeijingDayReplacesCumulativeSnapshotAndDirectBucketsKeepTheirDates()
    {
        var (_, repository, account, _) = await CreateAsync();
        var first = await repository.ReserveGenerationAsync(account, default);
        await repository.CommitCompleteSyncAsync(new(account, first, At(2),
        [Observation("share", AccountShareRemoteStatus.Valid, 10, new Dictionary<DateOnly, long> { [new(2026, 9, 1)] = 4 })]), default);
        var second = await repository.ReserveGenerationAsync(account, default);
        await repository.CommitCompleteSyncAsync(new(account, second, At(2).AddHours(3),
        [Observation("share", AccountShareRemoteStatus.Valid, 13, new Dictionary<DateOnly, long> { [new(2026, 8, 31)] = 3 })]), default);

        var history = Assert.Single(Assert.Single((await repository.LoadCachedAsync(account, default)).Items).Metrics,
            metric => metric.Metric == AccountShareMetric.Transfer);
        Assert.Equal(13, history.HistoricalTotal);
        Assert.Equal(13, history.CumulativeTotals[new(2026, 9, 2)]);
        Assert.Equal(4, history.DirectDailyValues[new(2026, 9, 1)]);
        Assert.Equal(3, history.DirectDailyValues[new(2026, 8, 31)]);
    }

    [Fact]
    public async Task ReservedGenerationsAreDurableAndOlderCompletionCannotOverwriteNewerData()
    {
        var (factory, repository, account, _) = await CreateAsync();
        var older = await repository.ReserveGenerationAsync(account, default);
        var newer = await new SqliteAccountShareAnalyticsRepository(factory).ReserveGenerationAsync(account, default);
        Assert.Equal(older + 1, newer);
        Assert.True(await repository.CommitCompleteSyncAsync(Commit(account, newer, "new", AccountShareRemoteStatus.Valid, 20), default));
        Assert.False(await repository.CommitCompleteSyncAsync(Commit(account, older, "old", AccountShareRemoteStatus.Valid, 10), default));
        Assert.Equal("new", Assert.Single((await repository.LoadCachedAsync(account, default)).Items).ShareId);
    }

    [Fact]
    public async Task FailedAtomicCommitPreservesPreviousCacheAndSyncTime()
    {
        var (factory, repository, account, _) = await CreateAsync();
        var first = await repository.ReserveGenerationAsync(account, default);
        await repository.CommitCompleteSyncAsync(Commit(account, first, "old", AccountShareRemoteStatus.Valid, 10), default);
        await using (var connection = await factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER RejectShareSnapshot BEFORE INSERT ON AccountShareMetricSnapshots BEGIN SELECT RAISE(ABORT,'fixture'); END;";
            await command.ExecuteNonQueryAsync();
        }
        var second = await repository.ReserveGenerationAsync(account, default);
        await Assert.ThrowsAsync<SqliteException>(() => repository.CommitCompleteSyncAsync(
            Commit(account, second, "new", AccountShareRemoteStatus.Valid, 20), default));

        var snapshot = await repository.LoadCachedAsync(account, default);
        Assert.Equal("old", Assert.Single(snapshot.Items).ShareId);
        Assert.Equal(At(2), snapshot.LastSuccessfulSyncAtUtc);
    }

    private async Task<(SqliteConnectionFactory Factory, SqliteAccountShareAnalyticsRepository Repository, Guid AccountA, Guid AccountB)> CreateAsync()
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "cache.db"));
        await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        await using var connection = await factory.OpenConnectionAsync();
        foreach (var account in new[] { accountA, accountB })
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO Accounts(Id,BaiduUserId,DisplayName,LoginMethod,Status,DailyLimit,DailyExecutedCount,SortOrder,IsEnabled,DailyCountDateKey,IsDeleted) VALUES($id,$uid,'fixture','WebView2','Active',0,0,0,1,'2026-09-02',0);";
            command.Parameters.AddWithValue("$id", account.ToString("D"));
            command.Parameters.AddWithValue("$uid", account.ToString("N"));
            await command.ExecuteNonQueryAsync();
        }
        return (factory, new(factory), accountA, accountB);
    }

    private static AccountShareSyncCommit Commit(Guid account, long generation, string shareId, AccountShareRemoteStatus status, long total) =>
        new(account, generation, At(2), [Observation(shareId, status, total)]);

    private static AccountShareObservation Observation(string id, AccountShareRemoteStatus status, long total,
        IReadOnlyDictionary<DateOnly, long>? direct = null) => new(
        id, $"title-{id}", new Uri($"https://pan.baidu.com/s/{id}"), At(1), status,
        Enum.GetValues<AccountShareMetric>().Select(metric =>
            new AccountShareMetricObservation(metric, total + (long)metric, metric == AccountShareMetric.Transfer ? direct : null)).ToArray());

    private static DateTimeOffset At(int beijingDay) =>
        new(2026, 9, beijingDay, 4, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
