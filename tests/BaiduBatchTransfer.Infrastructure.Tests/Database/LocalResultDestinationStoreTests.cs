using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Database;

namespace BaiduBatchTransfer.Infrastructure.Tests.Database;

public sealed class LocalResultDestinationStoreTests : IDisposable
{
    // Ownership note: this table links an existing transfer BatchJob to a local-only directory.
    // It does not duplicate transfer status/evidence, and paths are redacted from model output/errors.
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bbt-result-destination-{Guid.NewGuid():N}");

    [Fact]
    public async Task DestinationPersistsAcrossStoreInstancesAndNeverAppearsInToString()
    {
        var graph = await CreateAsync();
        var path = Path.Combine(root, "private", "exports");
        var record = LocalResultDestinationRecord.Create(graph.BatchId, path, graph.Now);

        await graph.Store.SetAsync(record);
        var restored = await new SqliteLocalResultDestinationStore(graph.Factory).GetAsync(graph.BatchId);

        Assert.Equal(Path.GetFullPath(path), restored!.DirectoryReference);
        Assert.DoesNotContain(path, restored.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", restored.ToString());
    }

    [Fact]
    public async Task UnknownTransferBatchIsRejectedWithoutEchoingThePath()
    {
        var graph = await CreateAsync();
        var secret = Path.Combine(root, "do-not-echo");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => graph.Store.SetAsync(
            LocalResultDestinationRecord.Create(Guid.NewGuid(), secret, graph.Now)));

        Assert.DoesNotContain(secret, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Graph> CreateAsync()
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "app.db"));
        await new DatabaseMigrator(factory,
            new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
        var batchId = Guid.NewGuid();
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO BatchJobs(Id,Name,SourceType,TransferMode,QuotaBasis,ShortagePolicy,ResourceCount,
              OperationCount,DefaultTargetPath,FolderCreationMode,DuplicatePolicy,ValidationMode,Status,CreatedAt,QueueOrder)
            VALUES($id,'job','Text','Distribution','TaskCount','ProportionalReduction',0,0,'/','None','Skip',
              'Immediate','Queued',$now,1);
            """;
        command.Parameters.AddWithValue("$id", batchId.ToString("D"));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
        return new(factory, new SqliteLocalResultDestinationStore(factory), batchId,
            new DateTimeOffset(2026, 8, 25, 1, 0, 0, TimeSpan.Zero));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed record Graph(
        SqliteConnectionFactory Factory,
        SqliteLocalResultDestinationStore Store,
        Guid BatchId,
        DateTimeOffset Now);
}
