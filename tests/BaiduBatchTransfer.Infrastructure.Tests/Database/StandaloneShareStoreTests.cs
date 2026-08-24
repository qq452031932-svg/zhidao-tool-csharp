using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;

namespace BaiduBatchTransfer.Infrastructure.Tests.Database;

public sealed class StandaloneShareStoreTests : IDisposable
{
    // Ownership note: these rows are share-only facts. Every user selection creates exactly one
    // operation and one exact remote-item row; no TransferOperation is fabricated and the existing
    // transfer-triggered ShareOperations foreign-key boundary remains untouched.
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bbt-standalone-share-{Guid.NewGuid():N}");

    [Fact]
    public async Task CreateOrGetIsCommitThenErrorSafeAndEverySelectionHasExactlyOneOperation()
    {
        var graph = await CreateAsync();
        var envelope = Envelope(graph, 3, graph.Now);

        var created = await graph.Store.CreateOrGetBatchAsync(envelope);
        var retried = await new SqliteStandaloneShareStore(graph.Factory).CreateOrGetBatchAsync(envelope);

        Assert.False(created.AlreadyExisted);
        Assert.True(retried.AlreadyExisted);
        Assert.Equal(created.Batch.Id, retried.Batch.Id);
        Assert.Equal(3, retried.Operations.Length);
        Assert.All(retried.Operations, operation => Assert.Equal(1, operation.ExactRemoteItems.Count));
        Assert.Equal(0L, await ScalarAsync(graph.Factory, "SELECT COUNT(*) FROM TransferOperations;"));
        Assert.Equal(3L, await ScalarAsync(graph.Factory, "SELECT COUNT(*) FROM StandaloneShareRemoteItems;"));
    }

    [Fact]
    public async Task HistoryIsDurableBoundedAndPagedNewestFirst()
    {
        var graph = await CreateAsync();
        var older = Envelope(graph, 1, graph.Now);
        var newer = Envelope(graph, 1, graph.Now.AddMinutes(1));
        await graph.Store.CreateOrGetBatchAsync(older);
        await graph.Store.CreateOrGetBatchAsync(newer);

        var first = await new SqliteStandaloneShareStore(graph.Factory).GetBatchesAsync(new PageQuery(1, 1));
        var second = await graph.Store.GetBatchesAsync(new PageQuery(2, 1));

        Assert.Equal(2, first.TotalCount);
        Assert.Equal(newer.Batch.Id, Assert.Single(first.Items).Id);
        Assert.Equal(older.Batch.Id, Assert.Single(second.Items).Id);
    }

    [Fact]
    public async Task PauseResumeAndRetryUseRevisionGenerationAndSafeSubmissionEvidence()
    {
        var graph = await CreateAsync();
        var receipt = await graph.Store.CreateOrGetBatchAsync(Envelope(graph, 2, graph.Now));
        var paused = await graph.Store.TryPauseBatchAsync(receipt.Batch.Id, 0, graph.Now.AddSeconds(1));
        Assert.Equal(StandaloneShareMutationDisposition.Applied, paused.Disposition);
        Assert.Equal(StandaloneShareMutationDisposition.Conflict,
            (await graph.Store.TryPauseBatchAsync(receipt.Batch.Id, 0, graph.Now.AddSeconds(2))).Disposition);
        var resumed = await graph.Store.TryResumeBatchAsync(
            receipt.Batch.Id, paused.Value!.Revision, graph.Now.AddSeconds(3));
        Assert.Equal(StandaloneShareMutationDisposition.Applied, resumed.Disposition);

        var lease = await graph.Store.TryLeaseNextAsync(
            receipt.Batch.Id, graph.Now.AddSeconds(4), graph.Now.AddMinutes(1));
        Assert.NotNull(lease);
        Assert.True(await graph.Store.TryFailAsync(
            lease.OperationId, lease.AccountId, lease.Generation, lease.LeaseGeneration,
            lease.Revision, SubmissionAcceptance.DefinitelyNotAccepted,
            "REMOTE_NOT_SUBMITTED", graph.Now.AddSeconds(5)));
        var failed = (await graph.Store.GetOperationsAsync(receipt.Batch.Id))
            .Single(operation => operation.Id == lease.OperationId);

        var retried = await graph.Store.TryRetryFailedOperationAsync(
            failed.Id, failed.Generation, failed.Revision, graph.Now.AddSeconds(6));

        Assert.Equal(StandaloneShareMutationDisposition.Applied, retried.Disposition);
        Assert.Equal(failed.Generation + 1, retried.Value!.Generation);
        Assert.Equal(StandaloneShareOperationStatus.Queued, retried.Value.Status);
        Assert.Equal(StandaloneShareMutationDisposition.Conflict,
            (await graph.Store.TryRetryFailedOperationAsync(
                failed.Id, failed.Generation, failed.Revision, graph.Now.AddSeconds(7))).Disposition);
    }

    [Fact]
    public async Task LeaseAndTransitionsRejectWrongAccountGenerationRevisionAndInvalidOrder()
    {
        var graph = await CreateAsync();
        var receipt = await graph.Store.CreateOrGetBatchAsync(Envelope(graph, 1, graph.Now));
        var lease = (await graph.Store.TryLeaseNextAsync(
            receipt.Batch.Id, graph.Now.AddSeconds(1), graph.Now.AddMinutes(1)))!;

        Assert.False(await graph.Store.TryCompleteBaselineAsync(
            lease.OperationId, Guid.NewGuid(), lease.Generation, lease.LeaseGeneration, lease.Revision,
            ["existing-1"], Fingerprint('a'), graph.Now.AddSeconds(2)));
        Assert.False(await graph.Store.TryCompleteBaselineAsync(
            lease.OperationId, lease.AccountId, lease.Generation + 1, lease.LeaseGeneration, lease.Revision,
            ["existing-1"], Fingerprint('a'), graph.Now.AddSeconds(2)));
        Assert.False(await graph.Store.TryMarkResultPendingAsync(
            lease.OperationId, lease.AccountId, lease.Generation, lease.LeaseGeneration, lease.Revision,
            graph.Now.AddSeconds(2)));
        Assert.True(await graph.Store.TryCompleteBaselineAsync(
            lease.OperationId, lease.AccountId, lease.Generation, lease.LeaseGeneration, lease.Revision,
            ["existing-2", "existing-1"], Fingerprint('a'), graph.Now.AddSeconds(2)));
        var prepared = (await graph.Store.GetOperationsAsync(receipt.Batch.Id)).Single();
        Assert.False(await graph.Store.TryMarkSubmittingAsync(
            prepared.Id, prepared.AccountId, prepared.Generation, lease.LeaseGeneration,
            prepared.Revision - 1, graph.Now.AddSeconds(3)));
        Assert.True(await graph.Store.TryMarkSubmittingAsync(
            prepared.Id, prepared.AccountId, prepared.Generation, lease.LeaseGeneration,
            prepared.Revision, graph.Now.AddSeconds(3)));
    }

    [Fact]
    public async Task ExpiredLeaseRejectsLateBrowserStageEvent()
    {
        var graph = await CreateAsync();
        var receipt = await graph.Store.CreateOrGetBatchAsync(Envelope(graph, 1, graph.Now));
        var lease = (await graph.Store.TryLeaseNextAsync(
            receipt.Batch.Id, graph.Now.AddSeconds(1), graph.Now.AddSeconds(2)))!;

        Assert.False(await graph.Store.TryCompleteBaselineAsync(
            lease.OperationId, lease.AccountId, lease.Generation, lease.LeaseGeneration, lease.Revision,
            [], Fingerprint('e'), graph.Now.AddSeconds(3)));
    }

    [Fact]
    public async Task ResultPendingAcceptsOnlyCurrentAccountGenerationAndStoresOneRedactedResult()
    {
        var graph = await CreateAsync();
        var receipt = await graph.Store.CreateOrGetBatchAsync(Envelope(graph, 1, graph.Now));
        var lease = (await graph.Store.TryLeaseNextAsync(
            receipt.Batch.Id, graph.Now.AddSeconds(1), graph.Now.AddMinutes(1)))!;
        Assert.True(await graph.Store.TryCompleteBaselineAsync(
            lease.OperationId, lease.AccountId, lease.Generation, lease.LeaseGeneration, lease.Revision,
            [], Fingerprint('b'), graph.Now.AddSeconds(2)));
        var prepared = (await graph.Store.GetOperationsAsync(receipt.Batch.Id)).Single();
        Assert.True(await graph.Store.TryMarkSubmittingAsync(
            prepared.Id, prepared.AccountId, prepared.Generation, lease.LeaseGeneration,
            prepared.Revision, graph.Now.AddSeconds(3)));
        var submitting = (await graph.Store.GetOperationsAsync(receipt.Batch.Id)).Single();
        Assert.True(await graph.Store.TryMarkResultPendingAsync(
            submitting.Id, submitting.AccountId, submitting.Generation, lease.LeaseGeneration,
            submitting.Revision, graph.Now.AddSeconds(4)));
        var pending = (await graph.Store.GetOperationsAsync(receipt.Batch.Id)).Single();
        var result = new StandaloneShareResultRecord(
            Guid.NewGuid(), pending.Id, pending.BatchId, pending.AccountId, pending.Generation,
            pending.ExactRemoteItems.Fingerprint, Fingerprint('c'), "share-id",
            "https://pan.baidu.com/s/private", "a1b2", true, true,
            graph.Now.AddSeconds(5), graph.Now.AddSeconds(5));

        Assert.False(await graph.Store.TryAcceptResultAsync(
            result with { AccountId = Guid.NewGuid() }, lease.LeaseGeneration, pending.Revision,
            graph.Now.AddSeconds(5)));
        Assert.True(await graph.Store.TryAcceptResultAsync(
            result, lease.LeaseGeneration, pending.Revision, graph.Now.AddSeconds(5)));
        Assert.False(await graph.Store.TryAcceptResultAsync(
            result, lease.LeaseGeneration, pending.Revision, graph.Now.AddSeconds(6)));
        var restored = await graph.Store.GetResultAsync(pending.Id, pending.Generation);
        Assert.NotNull(restored);
        Assert.DoesNotContain(restored.ShareUrl, restored.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(restored.ExtractionCode, restored.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TerminalFailureCompletesBatchAndUnknownSubmissionCannotUseFailedRetry()
    {
        var graph = await CreateAsync();
        var receipt = await graph.Store.CreateOrGetBatchAsync(Envelope(graph, 1, graph.Now));
        var lease = (await graph.Store.TryLeaseNextAsync(
            receipt.Batch.Id, graph.Now.AddSeconds(1), graph.Now.AddMinutes(1)))!;
        Assert.True(await graph.Store.TryCompleteBaselineAsync(
            lease.OperationId, lease.AccountId, lease.Generation, lease.LeaseGeneration, lease.Revision,
            [], Fingerprint('d'), graph.Now.AddSeconds(2)));
        var prepared = (await graph.Store.GetOperationsAsync(receipt.Batch.Id)).Single();
        Assert.True(await graph.Store.TryMarkSubmittingAsync(
            prepared.Id, prepared.AccountId, prepared.Generation, lease.LeaseGeneration,
            prepared.Revision, graph.Now.AddSeconds(3)));
        var submitting = (await graph.Store.GetOperationsAsync(receipt.Batch.Id)).Single();

        Assert.True(await graph.Store.TryFailAsync(
            submitting.Id, submitting.AccountId, submitting.Generation, lease.LeaseGeneration,
            submitting.Revision, SubmissionAcceptance.Unknown, "SUBMIT_RESULT_UNKNOWN",
            graph.Now.AddSeconds(4)));

        var snapshot = (await graph.Store.GetBatchSnapshotAsync(receipt.Batch.Id))!;
        Assert.Equal(StandaloneShareBatchStatus.CompletedWithFailures, snapshot.Batch.Status);
        var failed = Assert.Single(snapshot.Operations);
        Assert.Equal(StandaloneShareMutationDisposition.Unsafe,
            (await graph.Store.TryRetryFailedOperationAsync(
                failed.Id, failed.Generation, failed.Revision, graph.Now.AddSeconds(5))).Disposition);
    }

    private static StandaloneSharePendingBatchEnvelope Envelope(Graph graph, int count, DateTimeOffset now)
    {
        var batchId = Guid.NewGuid();
        var batch = StandaloneShareBatchRecord.Create(
            batchId, Path.Combine(graph.Root, $"results-{batchId:N}"), count, now);
        var operations = Enumerable.Range(1, count).Select(index =>
            StandaloneShareOperationRecord.Create(
                Guid.NewGuid(), batchId,
                new OwnedRemoteSelection(
                    graph.AccountId,
                    new RemoteItemIdentitySet([new RemoteItemIdentity($"remote-{batchId:N}-{index}", $"item-{index}")]),
                    $"item-{index}", RemoteResourceKind.File, new RemotePath("/apps")),
                index, now)).ToArray();
        return StandaloneSharePendingBatchEnvelope.Create(batch, operations);
    }

    private async Task<Graph> CreateAsync()
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "app.db"));
        await new DatabaseMigrator(factory,
            new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
        var accountId = Guid.NewGuid();
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Accounts(Id,BaiduUserId,DisplayName,LoginMethod,Status,DailyLimit,DailyExecutedCount,
              SortOrder,IsEnabled,DailyCountDateKey,IsDeleted)
            VALUES($id,'local-user','account','WebView2','Active',0,0,0,1,'2026-08-25',0);
            """;
        command.Parameters.AddWithValue("$id", accountId.ToString("D"));
        await command.ExecuteNonQueryAsync();
        return new(root, factory, new SqliteStandaloneShareStore(factory), accountId,
            new DateTimeOffset(2026, 8, 25, 3, 0, 0, TimeSpan.Zero));
    }

    private static string Fingerprint(char value) => new(value, 64);

    private static async Task<long> ScalarAsync(SqliteConnectionFactory factory, string sql)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed record Graph(
        string Root,
        SqliteConnectionFactory Factory,
        SqliteStandaloneShareStore Store,
        Guid AccountId,
        DateTimeOffset Now);
}
