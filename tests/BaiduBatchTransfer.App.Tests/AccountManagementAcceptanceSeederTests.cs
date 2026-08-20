using System.Reflection;
using BaiduBatchTransfer.App;
using BaiduBatchTransfer.Infrastructure.Database;

namespace BaiduBatchTransfer.App.Tests;

public sealed class AccountManagementAcceptanceSeederTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "bbt-account-acceptance-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SeederRejectsExistingRootAndUnsupportedScenario()
    {
        Directory.CreateDirectory(root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SeedAsync(root, "keep-history"));
        Directory.Delete(root, true);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SeedAsync(root, "unknown"));
    }

    [Fact]
    public async Task SeederRejectsAbsoluteRootOutsideSystemTemp()
    {
        var nonTempRoot = Path.Combine(
            AppContext.BaseDirectory,
            "bbt-nontemp-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                SeedAsync(nonTempRoot, "keep-history"));
        }
        finally
        {
            if (Directory.Exists(nonTempRoot)) Directory.Delete(nonTempRoot, true);
        }
    }

    [Theory]
    [InlineData("keep-history", 1, 1, 0)]
    [InlineData("cleanup-history", 2, 4, 0)]
    [InlineData("active-work", 1, 0, 1)]
    public async Task SeederCreatesMarkedRepositoryBackedScenario(
        string scenario,
        long expectedBatches,
        long expectedOperations,
        long expectedActiveBAllocations)
    {
        var scenarioRoot = root + "-" + scenario;
        await SeedAsync(scenarioRoot, scenario);

        Assert.Equal(
            StartupDataRootSelector.AcceptanceMarkerHeader + $"\nscenario={scenario}\n",
            await File.ReadAllTextAsync(Path.Combine(
                scenarioRoot,
                StartupDataRootSelector.AcceptanceMarkerFileName)));

        var paths = new AppDataPaths(scenarioRoot);
        var factory = new SqliteConnectionFactory(paths.DatabasePath);
        Assert.Equal(3, await ScalarAsync(factory, "SELECT COUNT(*) FROM Accounts WHERE Status='Active' AND IsEnabled=1;"));
        Assert.Equal(expectedBatches, await ScalarAsync(factory, "SELECT COUNT(*) FROM BatchJobs;"));
        Assert.Equal(expectedOperations, await ScalarAsync(factory, "SELECT COUNT(*) FROM TransferOperations;"));
        Assert.Equal(expectedActiveBAllocations, await ScalarAsync(factory, """
            SELECT COUNT(*) FROM AccountAllocations al
            JOIN Accounts a ON a.Id=al.AccountId
            JOIN BatchJobs b ON b.Id=al.BatchJobId
            WHERE a.DisplayName='账号 B' AND b.Status IN ('Queued','Active','Paused','AwaitingCandidates','PausedReady');
            """));
    }

    public void Dispose()
    {
        var parent = Path.GetDirectoryName(root)!;
        if (!Directory.Exists(parent)) return;
        foreach (var candidate in Directory.EnumerateDirectories(
                     parent,
                     Path.GetFileName(root) + "*",
                     SearchOption.TopDirectoryOnly))
        {
            Directory.Delete(candidate, true);
        }
    }

    private static async Task SeedAsync(string acceptanceRoot, string scenario)
    {
        var seeder = typeof(AccountManagementAcceptanceSeederTests).Assembly.GetType(
            "BaiduBatchTransfer.App.Tests.AccountManagementAcceptanceSeeder");
        Assert.NotNull(seeder);
        var method = seeder.GetMethod("SeedAsync", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(null, [acceptanceRoot, scenario, CancellationToken.None]));
        await task;
    }

    private static async Task<long> ScalarAsync(SqliteConnectionFactory factory, string sql)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
