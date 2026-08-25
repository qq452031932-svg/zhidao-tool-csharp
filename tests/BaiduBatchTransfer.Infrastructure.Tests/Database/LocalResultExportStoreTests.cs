using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Infrastructure.Database;

namespace BaiduBatchTransfer.Infrastructure.Tests.Database;

public sealed class LocalResultExportStoreTests : IDisposable
{
    // Ownership note: LocalResultExports owns only local artifact publication state.
    // Retrying it must never mutate transfer/share execution rows or infer remote success.
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bbt-result-export-{Guid.NewGuid():N}");

    [Fact]
    public async Task CreateOrGetAndSuccessAreUniqueAcrossCommitThenErrorRetry()
    {
        var graph = await CreateAsync();
        var request = LocalResultExportRequest.Create(
            LocalResultOwnerKind.TransferBatch, graph.BatchId, LocalResultExportKind.ReplacementWorkbook,
            1, "result_001.xlsx", graph.Now);

        var first = await graph.Store.CreateOrGetAsync(request);
        var afterUnknownCommit = await graph.Store.CreateOrGetAsync(request);
        var lease = await graph.Store.TryAcquireAsync(
            request.Key, first.Revision, Guid.NewGuid(), graph.Now, graph.Now.AddMinutes(1));
        Assert.NotNull(lease);
        Assert.True(await graph.Store.TryCompleteAsync(
            first.Id, lease.Key.Generation, lease.LeaseGeneration, lease.LeaseOwnerId, lease.Revision,
            lease.LeaseExpiresAt, "result_001.xlsx", graph.Now.AddSeconds(2)));

        Assert.Equal(first.Id, afterUnknownCommit.Id);
        var completed = await graph.Store.GetAsync(request.Key);
        Assert.Equal(LocalResultExportStatus.Succeeded, completed!.Status);
        Assert.Null(await graph.Store.TryAcquireAsync(
            request.Key, completed.Revision, Guid.NewGuid(), graph.Now.AddMinutes(2), graph.Now.AddMinutes(3)));
    }

    [Fact]
    public async Task FailureRetriesOnlyExportLeaseAndRejectsStaleLeaseCompletion()
    {
        var graph = await CreateAsync();
        var request = LocalResultExportRequest.Create(
            LocalResultOwnerKind.TransferBatch, graph.BatchId, LocalResultExportKind.ReplacementWorkbook,
            3, "result.xlsx", graph.Now);
        var created = await graph.Store.CreateOrGetAsync(request);
        var first = (await graph.Store.TryAcquireAsync(
            request.Key, created.Revision, Guid.NewGuid(), graph.Now, graph.Now.AddMinutes(1)))!;
        Assert.True(await graph.Store.TryFailAsync(
            created.Id, first.Key.Generation, first.LeaseGeneration, first.LeaseOwnerId, first.Revision,
            first.LeaseExpiresAt, "LOCAL_WRITE_FAILED", graph.Now.AddSeconds(1)));
        var failed = (await graph.Store.GetAsync(request.Key))!;

        var second = await graph.Store.TryAcquireAsync(
            request.Key, failed.Revision, Guid.NewGuid(), graph.Now.AddSeconds(2), graph.Now.AddMinutes(2));

        Assert.NotNull(second);
        Assert.True(second.LeaseGeneration > first.LeaseGeneration);
        Assert.False(await graph.Store.TryCompleteAsync(
            created.Id, first.Key.Generation, first.LeaseGeneration, first.LeaseOwnerId, first.Revision,
            first.LeaseExpiresAt, "late.xlsx", graph.Now.AddSeconds(3)));
        Assert.Equal("Queued", await BatchStatusAsync(graph.Factory, graph.BatchId));
    }

    [Fact]
    public async Task ExpiredInProgressRecoversToPendingWithBoundedBatch()
    {
        var graph = await CreateAsync();
        for (var generation = 1; generation <= 2; generation++)
        {
            var request = LocalResultExportRequest.Create(
                LocalResultOwnerKind.TransferBatch, graph.BatchId, LocalResultExportKind.ReplacementWorkbook,
                generation, $"result-{generation}.xlsx", graph.Now.AddSeconds(generation));
            var created = await graph.Store.CreateOrGetAsync(request);
            Assert.NotNull(await graph.Store.TryAcquireAsync(
                request.Key, created.Revision, Guid.NewGuid(), graph.Now.AddSeconds(10), graph.Now.AddSeconds(11)));
        }

        Assert.Equal(1, await graph.Store.RecoverExpiredAsync(graph.Now.AddMinutes(1), 1));
        var page = await graph.Store.GetPageAsync(new PageQuery(1, 10));
        Assert.Single(page.Items, item => item.Status == LocalResultExportStatus.Pending);
        Assert.Single(page.Items, item => item.Status == LocalResultExportStatus.InProgress);
    }

    [Fact]
    public async Task ExpiredLeaseRejectsLateCompletionBeforeRecovery()
    {
        var graph = await CreateAsync();
        var request = LocalResultExportRequest.Create(
            LocalResultOwnerKind.TransferBatch, graph.BatchId, LocalResultExportKind.ReplacementWorkbook,
            1, "late-result.xlsx", graph.Now);
        var created = await graph.Store.CreateOrGetAsync(request);
        var lease = (await graph.Store.TryAcquireAsync(
            request.Key, created.Revision, Guid.NewGuid(), graph.Now, graph.Now.AddSeconds(1)))!;

        Assert.False(await graph.Store.TryCompleteAsync(
            created.Id, lease.Key.Generation, lease.LeaseGeneration, lease.LeaseOwnerId, lease.Revision,
            lease.LeaseExpiresAt, "late-result.xlsx", graph.Now.AddSeconds(2)));
        Assert.Equal(LocalResultExportStatus.InProgress, (await graph.Store.GetAsync(request.Key))!.Status);
    }

    [Fact]
    public async Task LeaseOwnerAndRevisionAreRequiredForCompletionButHiddenFromReadModels()
    {
        var graph = await CreateAsync();
        var request = LocalResultExportRequest.Create(
            LocalResultOwnerKind.TransferBatch, graph.BatchId, LocalResultExportKind.ReplacementWorkbook,
            5, "owned-result.xlsx", graph.Now);
        var created = await graph.Store.CreateOrGetAsync(request);
        var owner = Guid.NewGuid();
        var lease = (await graph.Store.TryAcquireAsync(
            request.Key, created.Revision, owner, graph.Now, graph.Now.AddMinutes(1)))!;

        Assert.Equal(owner, lease.LeaseOwnerId);
        Assert.DoesNotContain(owner.ToString("D"), lease.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            typeof(LocalResultExportRecord).GetProperties(),
            property => property.Name == "LeaseOwnerId");
        Assert.False(await graph.Store.TryCompleteAsync(
            lease.ExportId, lease.Key.Generation, lease.LeaseGeneration, Guid.NewGuid(), lease.Revision,
            lease.LeaseExpiresAt, "owned-result.xlsx", graph.Now.AddSeconds(1)));
        Assert.False(await graph.Store.TryCompleteAsync(
            lease.ExportId, lease.Key.Generation, lease.LeaseGeneration, owner, lease.Revision - 1,
            lease.LeaseExpiresAt, "owned-result.xlsx", graph.Now.AddSeconds(1)));
        Assert.True(await graph.Store.TryCompleteAsync(
            lease.ExportId, lease.Key.Generation, lease.LeaseGeneration, owner, lease.Revision,
            lease.LeaseExpiresAt, "owned-result.xlsx", graph.Now.AddSeconds(1)));

        await using var connection = await graph.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(LocalResultExports);";
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        Assert.Contains("LeaseOwnerId", columns, StringComparer.Ordinal);
    }

    [Fact]
    public async Task RecoveryUsesExactExpiredLeaseCasAndOldOwnerCannotFinishAfterReopen()
    {
        var graph = await CreateAsync();
        var request = LocalResultExportRequest.Create(
            LocalResultOwnerKind.TransferBatch, graph.BatchId, LocalResultExportKind.ReplacementWorkbook,
            6, "recovered.xlsx", graph.Now);
        var created = await graph.Store.CreateOrGetAsync(request);
        var oldOwner = Guid.NewGuid();
        var oldLease = (await graph.Store.TryAcquireAsync(
            request.Key, created.Revision, oldOwner, graph.Now, graph.Now.AddSeconds(1)))!;
        var reopened = new SqliteLocalResultExportStore(graph.Factory);

        Assert.Equal(1, await reopened.RecoverExpiredAsync(graph.Now.AddSeconds(1), 10));
        Assert.False(await reopened.TryFailAsync(
            oldLease.ExportId, oldLease.Key.Generation, oldLease.LeaseGeneration, oldOwner,
            oldLease.Revision, oldLease.LeaseExpiresAt, "LATE_FAILURE", graph.Now.AddSeconds(2)));
        var pending = (await reopened.GetAsync(request.Key))!;
        var newOwner = Guid.NewGuid();
        var newLease = await reopened.TryAcquireAsync(
            request.Key, pending.Revision, newOwner, graph.Now.AddSeconds(2), graph.Now.AddMinutes(1));

        Assert.NotNull(newLease);
        Assert.Equal(newOwner, newLease.LeaseOwnerId);
        Assert.True(newLease.LeaseGeneration > oldLease.LeaseGeneration);
    }

    [Theory]
    [InlineData("..\\secret.xlsx")]
    [InlineData("C:\\private\\secret.xlsx")]
    [InlineData("folder/result.xlsx")]
    public void UnsafeFileNameIsRejectedWithoutEchoingInput(string unsafeName)
    {
        var error = Assert.Throws<ArgumentException>(() => LocalResultExportRequest.Create(
            LocalResultOwnerKind.TransferBatch, Guid.NewGuid(), LocalResultExportKind.ReplacementWorkbook,
            1, unsafeName, DateTimeOffset.UtcNow));
        Assert.DoesNotContain(unsafeName, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OwnerProjection_OnlyDiscoversTerminalBatchWithPersistedDestination()
    {
        var graph = await CreateAsync();
        var owners = new SqliteLocalResultExportOwnerRepository(graph.Factory);
        var key = new LocalResultExportKey(
            LocalResultOwnerKind.TransferBatch, graph.BatchId,
            LocalResultExportKind.ReplacementWorkbook, 1);

        Assert.Null(await owners.GetAsync(key));
        await new SqliteLocalResultDestinationStore(graph.Factory).SetAsync(
            LocalResultDestinationRecord.Create(graph.BatchId, @"C:\Results", graph.Now));
        var queued = await owners.GetAsync(key);
        Assert.NotNull(queued);
        Assert.False(queued.IsTerminal);
        Assert.Empty((await owners.GetReadyPageAsync(new PageQuery(1, 10))).Items);

        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE BatchJobs SET Status='Completed',CompletedAt=$now WHERE Id=$id;";
            command.Parameters.AddWithValue("$id", graph.BatchId.ToString("D"));
            command.Parameters.AddWithValue("$now", graph.Now.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        var ready = Assert.Single((await owners.GetReadyPageAsync(new PageQuery(1, 10))).Items);
        Assert.Equal(key, ready.Key);
        Assert.True(ready.IsTerminal);
        Assert.Equal(@"C:\Results", ready.DestinationDirectory);
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
        return new(factory, new SqliteLocalResultExportStore(factory), batchId,
            new DateTimeOffset(2026, 8, 25, 2, 0, 0, TimeSpan.Zero));
    }

    private static async Task<string> BatchStatusAsync(SqliteConnectionFactory factory, Guid batchId)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Status FROM BatchJobs WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", batchId.ToString("D"));
        return (string)(await command.ExecuteScalarAsync())!;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed record Graph(
        SqliteConnectionFactory Factory,
        SqliteLocalResultExportStore Store,
        Guid BatchId,
        DateTimeOffset Now);
}
