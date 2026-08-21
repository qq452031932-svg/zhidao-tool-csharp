using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Tests.Database;

public sealed class ShareSchedulingStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bbt-share-store-{Guid.NewGuid():N}");

    [Fact]
    public async Task ShareDisabledHistoricalBatchCannotAcquireShareWork()
    {
        var graph = await CreateGraphAsync(includeShareSettings: false);
        var store = new SqliteShareSchedulingStore(graph.Factory);

        var reserved = await store.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2));

        Assert.Null(reserved);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DirectPendingHandoffWithoutBothExactItemsAndMatchingLineageCannotBeAcquired(
        bool addRemoteItem,
        bool addLineage)
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM ShareOperations WHERE Id=$id;";
            command.Parameters.AddWithValue("$id", graph.ShareOperationId.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        await using (var unit = await SqliteUnitOfWork.BeginAsync(graph.Factory))
        {
            await unit.Jobs.AddPendingShareOperationsAsync([new(
                graph.ShareOperationId, graph.TransferId, graph.ResourceId, graph.BatchId,
                graph.AccountId, 1, graph.SettingsFingerprint, graph.Now)]);
            await unit.CommitAsync();
        }
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var transaction = connection.BeginTransaction(deferred: false))
        {
            await using (var handoff = connection.CreateCommand())
            {
                handoff.Transaction = transaction;
                handoff.CommandText = "UPDATE ShareOperations SET Status='WaitingShare',ExactItemSetFingerprint=$items WHERE Id=$id;";
                handoff.Parameters.AddWithValue("$items", graph.ItemSetFingerprint);
                handoff.Parameters.AddWithValue("$id", graph.ShareOperationId.ToString("D"));
                Assert.Equal(1, await handoff.ExecuteNonQueryAsync());
            }
            if (addRemoteItem)
            {
                await using var remote = connection.CreateCommand();
                remote.Transaction = transaction;
                remote.CommandText = "INSERT INTO ShareOperationRemoteItems(ShareOperationId,ItemOrdinal,RemoteItemId,CanonicalSortKey,DisplayName) VALUES($id,0,'remote-bypass',$sort,'Bypass');";
                remote.Parameters.AddWithValue("$id", graph.ShareOperationId.ToString("D"));
                remote.Parameters.AddWithValue("$sort", new byte[] { 1 });
                Assert.Equal(1, await remote.ExecuteNonQueryAsync());
            }
            if (addLineage)
            {
                await using var lineage = connection.CreateCommand();
                lineage.Transaction = transaction;
                lineage.CommandText = "INSERT INTO ShareOperationGenerations(ShareOperationId,Generation,BatchJobId,ResourceItemId,AccountId,ExactItemSetFingerprint,SettingsFingerprint) VALUES($id,1,$batch,$resource,$account,$items,$settings);";
                lineage.Parameters.AddWithValue("$id", graph.ShareOperationId.ToString("D"));
                lineage.Parameters.AddWithValue("$batch", graph.BatchId.ToString("D"));
                lineage.Parameters.AddWithValue("$resource", graph.ResourceId.ToString("D"));
                lineage.Parameters.AddWithValue("$account", graph.AccountId.ToString("D"));
                lineage.Parameters.AddWithValue("$items", graph.ItemSetFingerprint);
                lineage.Parameters.AddWithValue("$settings", graph.SettingsFingerprint);
                Assert.Equal(1, await lineage.ExecuteNonQueryAsync());
            }
            await transaction.CommitAsync();
        }

        var store = new SqliteShareSchedulingStore(graph.Factory);
        Assert.Null(await store.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2)));
        Assert.Equal("WaitingShare", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
    }

    [Fact]
    public async Task SealedExactRemoteItemCannotBeDeleted()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        await using var connection = await graph.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ShareOperationRemoteItems WHERE ShareOperationId=$id AND ItemOrdinal=0;";
        command.Parameters.AddWithValue("$id", graph.ShareOperationId.ToString("D"));

        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(2L, await ScalarAsync<long>(graph.Factory,
            "SELECT COUNT(*) FROM ShareOperationRemoteItems WHERE ShareOperationId=$id;", graph.ShareOperationId));
    }

    [Fact]
    public async Task RemoteItemCannotBeMovedFromUnsealedOperationIntoSealedExactSet()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var unsealedShareId = await SeedUnsealedExactOperationAsync(graph);
        await using var connection = await graph.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ShareOperationRemoteItems SET ShareOperationId=$sealed,ItemOrdinal=2 WHERE ShareOperationId=$unsealed;";
        command.Parameters.AddWithValue("$sealed", graph.ShareOperationId.ToString("D"));
        command.Parameters.AddWithValue("$unsealed", unsealedShareId.ToString("D"));

        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(2L, await ScalarAsync<long>(graph.Factory,
            "SELECT COUNT(*) FROM ShareOperationRemoteItems WHERE ShareOperationId=$id;", graph.ShareOperationId));
        Assert.Equal(1L, await ScalarAsync<long>(graph.Factory,
            "SELECT COUNT(*) FROM ShareOperationRemoteItems WHERE ShareOperationId=$id;", unsealedShareId));
    }

    [Theory]
    [InlineData("Queued")]
    [InlineData("Paused")]
    [InlineData("Cancelled")]
    [InlineData("Completed")]
    public async Task OnlyAuthoritativelyActiveBatchCanReserveShareWork(string batchStatus)
    {
        var graph = await CreateGraphAsync(includeShareSettings: true, batchStatus);
        var store = new SqliteShareSchedulingStore(graph.Factory);

        Assert.Null(await store.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2)));
        Assert.Equal("WaitingShare", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
    }

    [Fact]
    public async Task WaitingShareClaimPlanUsesIndexesWithoutScanOrTemporarySort()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        await using var connection = await graph.Factory.OpenConnectionAsync();
        var queries = new[]
        {
            """
            EXPLAIN QUERY PLAN SELECT batch.Id FROM BatchJobs batch
            WHERE batch.Status='Active' AND EXISTS(
              SELECT 1 FROM ShareOperations candidate
              WHERE candidate.BatchJobId=batch.Id AND candidate.Status='WaitingShare'
                AND candidate.LeaseExpiresAt IS NULL)
            ORDER BY batch.QueueOrder,batch.CreatedAt,batch.Id LIMIT 1;
            """,
            """
            EXPLAIN QUERY PLAN SELECT operation.Id FROM ShareOperations operation
            WHERE operation.BatchJobId=$batch AND operation.Status='WaitingShare'
              AND operation.LeaseExpiresAt IS NULL
              AND EXISTS(SELECT 1 FROM BatchShareSettings settings
                WHERE settings.BatchJobId=operation.BatchJobId
                  AND settings.SettingsFingerprint=operation.SettingsFingerprint)
              AND EXISTS(SELECT 1 FROM BatchOutputAccounts output
                WHERE output.BatchJobId=operation.BatchJobId AND output.AccountId=operation.AccountId
                  AND output.OutputOrdinal=operation.OutputOrdinal AND output.ShareEnabled=1)
            ORDER BY operation.OutputOrdinal,operation.Id LIMIT 1;
            """,
            "EXPLAIN QUERY PLAN " + SqliteShareSchedulingStore.ExpiredBatchSelectSql,
            "EXPLAIN QUERY PLAN " + SqliteShareSchedulingStore.ExpiredOperationSelectSql,
        };
        foreach (var sql in queries)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$batch", graph.BatchId.ToString("D"));
            command.Parameters.AddWithValue("$now", graph.Now.ToString("O"));
            var details = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) details.Add(reader.GetString(3));
            var plan = string.Join(Environment.NewLine, details);
            Assert.DoesNotContain("SCAN ", plan, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TEMP B-TREE", plan, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("julianday", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task AcceptedResultKeysetPlanUsesPersistedCursorIndexWithoutScanOrTemporarySort()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        await using var connection = await graph.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + SqliteShareSchedulingStore.ResultKeysetSelectSql;
        command.Parameters.AddWithValue("$batch", graph.BatchId.ToString("D"));
        command.Parameters.AddWithValue("$afterSequence", 0);
        command.Parameters.AddWithValue("$afterOutput", 0);
        command.Parameters.AddWithValue("$afterResult", string.Empty);
        command.Parameters.AddWithValue("$limit", 10);
        var details = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) details.Add(reader.GetString(3));
        var plan = string.Join(Environment.NewLine, details);
        Assert.DoesNotContain("SCAN ", plan, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TEMP B-TREE", plan, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IX_BaiduShareResults_Batch_Accepted_Current_Cursor", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrphanShareOperationCannotCrossAnyShareWriteBoundary()
    {
        var graph = await CreateGraphAsync(includeShareSettings: false);
        var store = new SqliteShareSchedulingStore(graph.Factory);

        Assert.Null(await store.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2)));
        await SetOrphanStateAsync(graph, "Reserved");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendDiscoveryBaselinePageAsync(
            graph.ShareOperationId, 0, [new(graph.ShareOperationId, "orphan-share")]));
        Assert.False(await store.TryCompleteDiscoveryBaselineAsync(
            graph.ShareOperationId, 1, 0, "orphan-baseline", 0, graph.Now));
        Assert.False(await store.SaveDecisionSnapshotAsync(new(
            graph.BatchId, graph.AccountId, graph.SettingsFingerprint, "Cancel", "orphan-discovery", 0, 0, 0, "orphan-generation", graph.Now)));
        Assert.Equal(0, await CountAsync(graph.Factory, "ShareDiscoveryBaselines"));
        Assert.Equal(0, await CountAsync(graph.Factory, "BatchShareDecisionSnapshots"));

        await SetOrphanStateAsync(graph, "Preparing", baselineComplete: true);
        Assert.False(await store.TryMarkSubmittingAsync(graph.ShareOperationId, 1, 0, "{}", graph.Now));
        await SetOrphanStateAsync(graph, "Submitting", baselineComplete: true, submissionStarted: true);
        Assert.False(await store.TryRecordAdapterRequestAsync(graph.ShareOperationId, 1, 0, "orphan-request", graph.Now));
        Assert.False(await store.TryMarkResultPendingAsync(
            graph.ShareOperationId, 1, 0, graph.Now.AddMinutes(1), graph.Now));

        await SetOrphanStateAsync(graph, "Succeeded", baselineComplete: true);
        Assert.False(await store.TryStartNextGenerationAsync(
            graph.ShareOperationId, ShareOperationStatus.Succeeded, 1, 0, graph.Now.AddMinutes(2), graph.Now));

        await SetOrphanStateAsync(graph, "ResultPending", baselineComplete: true, submissionStarted: true);
        var result = new BaiduShareResultRecord(Guid.NewGuid(), graph.ShareOperationId, graph.BatchId,
            graph.ResourceId, graph.AccountId, graph.ItemSetFingerprint, graph.SettingsFingerprint, 1, "new-share",
            "https://pan.baidu.com/s/private", "abcd", true, true, graph.Now, graph.Now);
        Assert.False(await store.AcceptResultAsync(
            graph.ShareOperationId, ShareOperationStatus.ResultPending, 0, result, graph.Now));
        Assert.Equal(0, await CountAsync(graph.Factory, "BaiduShareResults"));
        Assert.Equal("ResultPending", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));

        await SetOrphanStateAsync(graph, "Preparing", baselineComplete: true);
        await InsertBaselineAsync(graph, "old-share");
        Assert.False(await store.ReuseExistingResultAsync(
            graph.ShareOperationId, 0, result with { ShareId = "old-share" }, graph.Now));
        Assert.Equal(0, await CountAsync(graph.Factory, "BaiduShareResults"));
        Assert.Equal("Preparing", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
    }

    [Fact]
    public async Task ExpiredPreSubmissionReservationCanBeRecoveredWithoutCreatingDuplicateWork()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var store = new SqliteShareSchedulingStore(graph.Factory);
        var initial = Assert.IsType<ShareOperationRecord>(await store.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2)));

        Assert.Null(await store.TryReserveNextAsync(graph.Now.AddMinutes(1), TimeSpan.FromMinutes(2)));
        var recovered = await store.TryReserveNextAsync(graph.Now.AddMinutes(3), TimeSpan.FromMinutes(2));

        Assert.NotNull(recovered);
        Assert.Equal(graph.ShareOperationId, recovered.Id);
        Assert.Equal(graph.Now.AddMinutes(5), recovered.LeaseExpiresAt);
        Assert.True(recovered.LeaseGeneration > initial.LeaseGeneration);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendDiscoveryBaselinePageAsync(
            graph.ShareOperationId, initial.LeaseGeneration, [new(graph.ShareOperationId, "stale-worker")]));
        Assert.Equal(0, await CountAsync(graph.Factory, "ShareDiscoveryBaselines"));
    }

    [Fact]
    public async Task ExpiredRecoverySkipsManyEarlierUnexpiredRowsAndReturnsEarliestCanonicalLease()
    {
        const int unexpiredCount = 250;
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var expiredId = Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffff0");
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = graph.Factory.DatabasePath,
            Pooling = false,
            ForeignKeys = false,
        };
        await using (var connection = new SqliteConnection(builder.ToString()))
        {
            await connection.OpenAsync();
            await using var transaction = connection.BeginTransaction();
            await using (var setup = connection.CreateCommand())
            {
                setup.Transaction = transaction;
                setup.CommandText = """
                    UPDATE ShareOperations SET Status='Cancelled' WHERE Id=$initial;
                    UPDATE BatchOutputAccounts SET PlannedTransferCount=$planned,PlannedShareCount=$planned
                    WHERE BatchJobId=$batch AND AccountId=$account;
                    """;
                setup.Parameters.AddWithValue("$initial", graph.ShareOperationId.ToString("D"));
                setup.Parameters.AddWithValue("$planned", unexpiredCount + 2);
                setup.Parameters.AddWithValue("$batch", graph.BatchId.ToString("D"));
                setup.Parameters.AddWithValue("$account", graph.AccountId.ToString("D"));
                await setup.ExecuteNonQueryAsync();
            }
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO ShareOperations(Id,TransferOperationId,ResourceItemId,BatchJobId,AccountId,OutputOrdinal,
                  Status,ExactItemSetFingerprint,SettingsFingerprint,Generation,SubmissionAcceptance,AttemptCount,
                  LeaseGeneration,LeaseExpiresAt,CreatedAt,UpdatedAt)
                VALUES($id,$transfer,$resource,$batch,$account,1,'Reserved',$items,$settings,1,'Unknown',0,1,$lease,$created,$created);
                """;
            for (var index = 1; index <= unexpiredCount + 1; index++)
            {
                var id = index <= unexpiredCount ? new Guid(index, 0, 0, new byte[8]) : expiredId;
                var remoteId = $"remote-{index}";
                var itemSetFingerprint = index <= unexpiredCount
                    ? $"items-{index}"
                    : new RemoteItemIdentitySet([new(remoteId, "Expired")]).Fingerprint;
                insert.Parameters.Clear();
                insert.Parameters.AddWithValue("$id", id.ToString("D"));
                insert.Parameters.AddWithValue("$transfer", Guid.NewGuid().ToString("D"));
                insert.Parameters.AddWithValue("$resource", graph.ResourceId.ToString("D"));
                insert.Parameters.AddWithValue("$batch", graph.BatchId.ToString("D"));
                insert.Parameters.AddWithValue("$account", graph.AccountId.ToString("D"));
                insert.Parameters.AddWithValue("$items", itemSetFingerprint);
                insert.Parameters.AddWithValue("$settings", graph.SettingsFingerprint);
                insert.Parameters.AddWithValue("$lease", (index <= unexpiredCount
                    ? graph.Now.AddHours(1).AddSeconds(index)
                    : graph.Now.AddMinutes(-2)).ToString("O"));
                insert.Parameters.AddWithValue("$created", graph.Now.ToString("O"));
                await insert.ExecuteNonQueryAsync();
                if (index > unexpiredCount)
                {
                    await using var remote = connection.CreateCommand();
                    remote.Transaction = transaction;
                    remote.CommandText = "INSERT INTO ShareOperationRemoteItems(ShareOperationId,ItemOrdinal,RemoteItemId,CanonicalSortKey,DisplayName) VALUES($id,0,$remote,$sort,'Expired');";
                    remote.Parameters.AddWithValue("$id", id.ToString("D"));
                    remote.Parameters.AddWithValue("$remote", remoteId);
                    remote.Parameters.AddWithValue("$sort", RemoteItemIdentitySet.GetCanonicalSortKey(remoteId));
                    Assert.Equal(1, await remote.ExecuteNonQueryAsync());
                    await using var evidence = connection.CreateCommand();
                    evidence.Transaction = transaction;
                    evidence.CommandText = "UPDATE ShareOperationGenerations SET ExactItemCount=1 WHERE ShareOperationId=$id AND Generation=1;";
                    evidence.Parameters.AddWithValue("$id", id.ToString("D"));
                    Assert.Equal(1, await evidence.ExecuteNonQueryAsync());
                }
            }
            await transaction.CommitAsync();
        }

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var recovered = Assert.IsType<ShareOperationRecord>(await new SqliteShareSchedulingStore(graph.Factory)
            .TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2)));
        timer.Stop();
        Assert.Equal(expiredId, recovered.Id);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"Expired recovery took {timer.Elapsed}.");
        Assert.EndsWith("+00:00", await ScalarAsync<string>(graph.Factory,
            "SELECT LeaseExpiresAt FROM ShareOperations WHERE Id=$id;", expiredId), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShareCreationRejectsMissingOrMismatchedExactItemsAndWrongOutputOrdinal()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var validationTransfer = new TransferOperationRecord(Guid.NewGuid(), graph.ResourceId, graph.BatchId, 2, 2,
            "/validation", "/validation", "", graph.AccountId, "Rule", "Cancelled", "Cancelled", 0,
            "transfer-validation", null, null, null, "NotPrepared", "Released", null, null, null, null, null,
            null, graph.Now, graph.Now);
        await using (var seed = await SqliteUnitOfWork.BeginAsync(graph.Factory))
        {
            await seed.Jobs.AddOperationsAsync([validationTransfer]);
            await seed.CommitAsync();
        }
        var template = new ShareOperationRecord(Guid.NewGuid(), validationTransfer.Id, graph.ResourceId, graph.BatchId,
            graph.AccountId, 1, "WaitingShare", "wrong-fingerprint", graph.SettingsFingerprint, 1, null, null, "Unknown", 0,
            null, null, null, null, null, graph.Now, graph.Now);

        await using (var unit = await SqliteUnitOfWork.BeginAsync(graph.Factory))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => unit.Jobs.AddShareOperationsAsync([template], []));
            await Assert.ThrowsAsync<ArgumentException>(() => unit.Jobs.AddShareOperationsAsync(
                [template], [new(template.Id, 0, "remote-a", "A")]));
            var validFingerprint = new RemoteItemIdentitySet([new("remote-a", "A")]).Fingerprint;
            await Assert.ThrowsAsync<ArgumentException>(() => unit.Jobs.AddShareOperationsAsync(
                [template with { ExactItemSetFingerprint = validFingerprint, SettingsFingerprint = " " }],
                [new(template.Id, 0, "remote-a", "A")]));
            await Assert.ThrowsAsync<SqliteException>(() => unit.Jobs.AddShareOperationsAsync(
                [template with { ExactItemSetFingerprint = validFingerprint, SettingsFingerprint = "wrong-settings" }],
                [new(template.Id, 0, "remote-a", "A")]));
        }

        var secondTransfer = new TransferOperationRecord(Guid.NewGuid(), graph.ResourceId, graph.BatchId, 3, 3,
            "/two", "/two", "", graph.AccountId, "Rule", "Cancelled", "Cancelled", 0, "transfer-two", null,
            null, null, "NotPrepared", "Released", null, null, null, null, null, null, graph.Now, graph.Now);
        var operationId = Guid.NewGuid();
        var fingerprint = new RemoteItemIdentitySet([new("remote-c", "C")]).Fingerprint;
        await using (var unit = await SqliteUnitOfWork.BeginAsync(graph.Factory))
        {
            await unit.Jobs.AddOperationsAsync([secondTransfer]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => unit.Jobs.AddShareOperationsAsync(
                [template with { Id = operationId, TransferOperationId = secondTransfer.Id, OutputOrdinal = 2,
                    ExactItemSetFingerprint = fingerprint }],
                [new(operationId, 0, "remote-c", "C")]));
        }
    }

    [Fact]
    public async Task DiscoveryBaselineIsIncompleteUntilExactGenerationAndCountAreCasCommitted()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var store = new SqliteShareSchedulingStore(graph.Factory);
        var reserved = Assert.IsType<ShareOperationRecord>(await store.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2)));
        await store.AppendDiscoveryBaselinePageAsync(graph.ShareOperationId, reserved.LeaseGeneration, [new(graph.ShareOperationId, "old-share-1")]);

        Assert.False(await store.TryCompleteDiscoveryBaselineAsync(
            graph.ShareOperationId, 1, reserved.LeaseGeneration, "baseline-fingerprint", 2, graph.Now.AddSeconds(1)));
        Assert.False((await store.GetDiscoveryBaselineStateAsync(graph.ShareOperationId))!.IsComplete);

        await store.AppendDiscoveryBaselinePageAsync(graph.ShareOperationId, reserved.LeaseGeneration, [new(graph.ShareOperationId, "old-share-2")]);
        Assert.True(await store.TryCompleteDiscoveryBaselineAsync(
            graph.ShareOperationId, 1, reserved.LeaseGeneration, "baseline-fingerprint", 2, graph.Now.AddSeconds(2)));
        await store.AppendDiscoveryBaselinePageAsync(graph.ShareOperationId, reserved.LeaseGeneration, [new(graph.ShareOperationId, "old-share-1")]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendDiscoveryBaselinePageAsync(
            graph.ShareOperationId, reserved.LeaseGeneration, [new(graph.ShareOperationId, "late-share")]));
        Assert.Equal(
            new ShareDiscoveryBaselineStateRecord(graph.ShareOperationId, 1, "baseline-fingerprint", 2, true, graph.Now.AddSeconds(2)),
            await store.GetDiscoveryBaselineStateAsync(graph.ShareOperationId));
    }

    [Fact]
    public async Task DecisionCasRejectsChangedDurableDiscoverySnapshotAcrossRestart()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var firstStore = new SqliteShareSchedulingStore(graph.Factory);
        var first = new BatchShareDiscoverySnapshotRecord(
            graph.BatchId, graph.AccountId, graph.SettingsFingerprint, "discovery-v1", 2, 1, 1,
            "generation-v1", graph.Now, 1);
        Assert.True(await firstStore.TrySaveDiscoverySnapshotAsync(first));

        var restarted = new SqliteShareSchedulingStore(graph.Factory);
        Assert.Equal(first, await restarted.GetDiscoverySnapshotAsync(
            graph.BatchId, graph.AccountId, graph.SettingsFingerprint));
        var changed = first with
        {
            DiscoveryFingerprint = "discovery-v2", DiscoveredCount = 3, CompatibleCount = 2,
            Generation = "generation-v2", Version = 2, CompletedAt = graph.Now.AddSeconds(1)
        };
        Assert.True(await restarted.TrySaveDiscoverySnapshotAsync(changed));
        Assert.False(await restarted.TrySaveDiscoverySnapshotAsync(first));

        Assert.False(await restarted.SaveDecisionSnapshotAsync(new(
            graph.BatchId, graph.AccountId, graph.SettingsFingerprint, "Regenerate", first.DiscoveryFingerprint,
            first.DiscoveredCount, first.CompatibleCount, first.IncompatibleCount, first.Generation, graph.Now, first.Version)));
        Assert.Equal(0, await CountAsync(graph.Factory, "BatchShareDecisionSnapshots"));
    }

    [Fact]
    public async Task ShareWorkRoundTripsExactItemsBaselinesDecisionAcceptedResultAndKeysetPages()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var store = new SqliteShareSchedulingStore(graph.Factory);

        Assert.Equal("Active", await ScalarAsync<string>(graph.Factory,
            "SELECT batch.Status FROM ShareOperations operation JOIN BatchJobs batch ON batch.Id=operation.BatchJobId WHERE operation.Id=$id;",
            graph.ShareOperationId));

        var reserved = await store.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2));
        Assert.NotNull(reserved);
        Assert.Equal(graph.ShareOperationId, reserved.Id);
        Assert.Equal("Reserved", reserved.Status);
        Assert.Equal(graph.SettingsFingerprint, reserved.SettingsFingerprint);
        Assert.Equal(graph.Now.AddMinutes(2), reserved.LeaseExpiresAt);

        var exact = await store.GetRemoteItemsKeysetPageAsync(graph.ShareOperationId, null, 1);
        Assert.Equal("remote-a", Assert.Single(exact.Items).RemoteItemId);
        Assert.Equal("remote-a", exact.NextCursor);
        var exactTail = await store.GetRemoteItemsKeysetPageAsync(graph.ShareOperationId, exact.NextCursor, 1);
        Assert.Equal("remote-b", Assert.Single(exactTail.Items).RemoteItemId);
        Assert.Null(exactTail.NextCursor);

        await store.AppendDiscoveryBaselinePageAsync(graph.ShareOperationId, reserved.LeaseGeneration, [
            new(graph.ShareOperationId, "share-2"),
            new(graph.ShareOperationId, "share-1")]);
        var baseline = await store.GetDiscoveryBaselineKeysetPageAsync(graph.ShareOperationId, null, 1);
        Assert.Equal("share-1", Assert.Single(baseline.Items).ShareId);
        Assert.Equal("share-1", baseline.NextCursor);
        Assert.True(await store.TryCompleteDiscoveryBaselineAsync(
            graph.ShareOperationId, 1, reserved.LeaseGeneration, "baseline-complete", 2, graph.Now));

        var decision = new BatchShareDecisionSnapshotRecord(
            graph.BatchId, graph.AccountId, graph.SettingsFingerprint, "Regenerate", "discovery-fingerprint", 4, 1, 2,
            "generation-token", graph.Now);
        var discovery = new BatchShareDiscoverySnapshotRecord(
            graph.BatchId, graph.AccountId, graph.SettingsFingerprint, "discovery-fingerprint", 4, 1, 2,
            "generation-token", graph.Now, 1);
        Assert.True(await store.TrySaveDiscoverySnapshotAsync(discovery));
        Assert.Equal(discovery, await new SqliteShareSchedulingStore(graph.Factory).GetDiscoverySnapshotAsync(
            graph.BatchId, graph.AccountId, graph.SettingsFingerprint));
        Assert.False(await store.SaveDecisionSnapshotAsync(
            decision with { SettingsFingerprint = "wrong-settings" }));
        Assert.True(await store.SaveDecisionSnapshotAsync(decision));
        Assert.Equal(decision, await store.GetDecisionSnapshotAsync(graph.BatchId, graph.AccountId));
        var restartedStore = new SqliteShareSchedulingStore(graph.Factory);
        Assert.True(await restartedStore.SaveDecisionSnapshotAsync(decision));
        Assert.Equal(1, await CountAsync(graph.Factory, "BatchShareDecisionSnapshots"));
        Assert.False(await store.SaveDecisionSnapshotAsync(
            decision with { DiscoveryFingerprint = "stale", Generation = "stale-generation" }));
        Assert.Equal(decision, await store.GetDecisionSnapshotAsync(graph.BatchId, graph.AccountId));

        var result = new BaiduShareResultRecord(
            Guid.Parse("10000000-0000-0000-0000-000000000001"), graph.ShareOperationId, graph.BatchId, graph.ResourceId, graph.AccountId,
            graph.ItemSetFingerprint, graph.SettingsFingerprint, 1, "share-new", "https://pan.baidu.com/s/private", "abcd",
            true, true, graph.Now, graph.Now);
        Assert.False(await store.AcceptResultAsync(graph.ShareOperationId, ShareOperationStatus.Preparing, reserved.LeaseGeneration,
            result with { AccountId = Guid.NewGuid() }, graph.Now));
        Assert.False(await store.AcceptResultAsync(graph.ShareOperationId, ShareOperationStatus.Preparing, reserved.LeaseGeneration,
            result with { SettingsFingerprint = "wrong-settings" }, graph.Now));
        Assert.False(await store.AcceptResultAsync(graph.ShareOperationId, ShareOperationStatus.Preparing, reserved.LeaseGeneration,
            result with { ShareId = "share-1" }, graph.Now));
        Assert.True(await store.TryMarkSubmittingAsync(
            graph.ShareOperationId, 1, reserved.LeaseGeneration, "{\"safe\":true}", graph.Now));
        Assert.False(await store.AcceptResultAsync(
            graph.ShareOperationId, ShareOperationStatus.Submitting, reserved.LeaseGeneration, result, graph.Now));
        Assert.Equal(0, await CountAsync(graph.Factory, "BaiduShareResults"));
        Assert.True(await store.TryMarkResultPendingAsync(
            graph.ShareOperationId, 1, reserved.LeaseGeneration, graph.Now.AddMinutes(1), graph.Now));
        Assert.True(await store.AcceptResultAsync(graph.ShareOperationId, ShareOperationStatus.ResultPending, reserved.LeaseGeneration, result, graph.Now));
        Assert.Equal(result, await store.GetAcceptedResultAsync(graph.ShareOperationId));
        Assert.False(await store.AcceptResultAsync(graph.ShareOperationId, ShareOperationStatus.Preparing, reserved.LeaseGeneration, result with { Id = Guid.NewGuid() }, graph.Now));

        var secondTransfer = new TransferOperationRecord(Guid.NewGuid(), graph.ResourceId, graph.BatchId, 2, 2, "/two", "/two", "",
            graph.AccountId, "Rule", "Cancelled", "Cancelled", 0, "transfer-two", null, null, null, "NotPrepared", "Released",
            null, null, null, null, null, null, graph.Now, graph.Now);
        var secondShareId = Guid.NewGuid();
        var secondItemSetFingerprint = new RemoteItemIdentitySet([new("remote-c", "C")]).Fingerprint;
        await using (var unit = await SqliteUnitOfWork.BeginAsync(graph.Factory))
        {
            await unit.Jobs.AddOperationsAsync([secondTransfer]);
            await unit.Jobs.AddShareOperationsAsync([
                new(secondShareId, secondTransfer.Id, graph.ResourceId, graph.BatchId, graph.AccountId, 1,
                    "WaitingShare", secondItemSetFingerprint, graph.SettingsFingerprint, 2, null, null, "Unknown", 0, null, null, null, null, null,
                    graph.Now, graph.Now)], [new(secondShareId, 0, "remote-c", "C")]);
            await unit.CommitAsync();
        }
        var secondReserved = Assert.IsType<ShareOperationRecord>(await store.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2)));
        await store.AppendDiscoveryBaselinePageAsync(secondShareId, secondReserved.LeaseGeneration, [new(secondShareId, "share-two", 2)]);
        Assert.True(await store.TryCompleteDiscoveryBaselineAsync(
            secondShareId, 2, secondReserved.LeaseGeneration, "second-baseline", 1, graph.Now));
        var secondResult = result with
        {
            Id = Guid.Parse("20000000-0000-0000-0000-000000000002"), ShareOperationId = secondShareId, ExactItemSetFingerprint = secondItemSetFingerprint,
            SettingsFingerprint = graph.SettingsFingerprint, Generation = 2, ShareId = "share-two"
        };
        Assert.True(await store.ReuseExistingResultAsync(secondShareId, secondReserved.LeaseGeneration, secondResult, graph.Now));

        var thirdTransfer = secondTransfer with
        {
            Id = Guid.NewGuid(), SequenceNumber = 3, AllocationOrdinal = 3,
            AssignedAccountId = graph.SecondAccountId, OperationFingerprint = "transfer-three"
        };
        var thirdShareId = Guid.NewGuid();
        var thirdItems = new RemoteItemIdentitySet([new("remote-d", "D")]).Fingerprint;
        await using (var unit = await SqliteUnitOfWork.BeginAsync(graph.Factory))
        {
            await unit.Jobs.AddOperationsAsync([thirdTransfer]);
            await unit.Jobs.AddShareOperationsAsync([
                new(thirdShareId, thirdTransfer.Id, graph.ResourceId, graph.BatchId, graph.SecondAccountId, 2,
                    "WaitingShare", thirdItems, graph.SettingsFingerprint, 1, null, null, "Unknown", 0,
                    null, null, null, null, null, graph.Now, graph.Now)],
                [new(thirdShareId, 0, "remote-d", "D")]);
            await unit.CommitAsync();
        }
        var thirdReserved = Assert.IsType<ShareOperationRecord>(
            await store.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2)));
        Assert.True(await store.TryCompleteDiscoveryBaselineAsync(
            thirdShareId, 1, thirdReserved.LeaseGeneration, "third-baseline", 0, graph.Now));
        Assert.True(await store.TryMarkSubmittingAsync(
            thirdShareId, 1, thirdReserved.LeaseGeneration, "{}", graph.Now));
        Assert.True(await store.TryRecordAdapterRequestAsync(
            thirdShareId, 1, thirdReserved.LeaseGeneration, "third-request", graph.Now));
        var thirdResult = result with
        {
            Id = Guid.Parse("30000000-0000-0000-0000-000000000003"), ShareOperationId = thirdShareId, AccountId = graph.SecondAccountId,
            ExactItemSetFingerprint = thirdItems, ShareId = "share-three"
        };
        Assert.True(await store.AcceptResultAsync(
            thirdShareId, ShareOperationStatus.Submitting, thirdReserved.LeaseGeneration, thirdResult, graph.Now));

        var fourthResource = new ResourceItemRecord(Guid.NewGuid(), graph.BatchId, null, 2, "source-url-2", "source-id-2",
            null, null, null, null, null, "None", "Valid", null, "Expanded", null, null, graph.Now, graph.Now,
            "Resolved title 2");
        var fourthTransfer = secondTransfer with
        {
            Id = Guid.NewGuid(), ResourceItemId = fourthResource.Id, SequenceNumber = 4, AllocationOrdinal = 4,
            OperationFingerprint = "transfer-four"
        };
        var fourthShareId = Guid.NewGuid();
        var fourthItems = new RemoteItemIdentitySet([new("remote-e", "E")]).Fingerprint;
        await using (var unit = await SqliteUnitOfWork.BeginAsync(graph.Factory))
        {
            await unit.Jobs.AddResourcesAsync([fourthResource]);
            await unit.Jobs.AddOperationsAsync([fourthTransfer]);
            await unit.Jobs.AddShareOperationsAsync([
                new(fourthShareId, fourthTransfer.Id, fourthResource.Id, graph.BatchId, graph.AccountId, 1,
                    "WaitingShare", fourthItems, graph.SettingsFingerprint, 1, null, null, "Unknown", 0,
                    null, null, null, null, null, graph.Now, graph.Now)],
                [new(fourthShareId, 0, "remote-e", "E")]);
            await unit.CommitAsync();
        }
        var fourthReserved = Assert.IsType<ShareOperationRecord>(
            await store.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2)));
        Assert.True(await store.TryCompleteDiscoveryBaselineAsync(
            fourthShareId, 1, fourthReserved.LeaseGeneration, "fourth-baseline", 0, graph.Now));
        Assert.True(await store.TryMarkSubmittingAsync(
            fourthShareId, 1, fourthReserved.LeaseGeneration, "{}", graph.Now));
        Assert.True(await store.TryRecordAdapterRequestAsync(
            fourthShareId, 1, fourthReserved.LeaseGeneration, "fourth-request", graph.Now));
        var fourthResult = result with
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000004"), ShareOperationId = fourthShareId,
            ResourceItemId = fourthResource.Id, ExactItemSetFingerprint = fourthItems, ShareId = "share-four"
        };
        Assert.True(await store.AcceptResultAsync(
            fourthShareId, ShareOperationStatus.Submitting, fourthReserved.LeaseGeneration, fourthResult, graph.Now));

        var export = await store.GetResultsKeysetPageAsync(new ShareResultKeysetQuery(graph.BatchId, 1));
        Assert.NotNull(export.NextCursor);
        var exported = new List<BaiduShareResultRecord>(export.Items);
        var cursor = export.NextCursor;
        while (cursor is not null)
        {
            var page = await store.GetResultsKeysetPageAsync(new ShareResultKeysetQuery(graph.BatchId, 1, cursor));
            exported.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        Assert.Equal(new[] { result.Id, secondResult.Id, thirdResult.Id, fourthResult.Id }, exported.Select(item => item.Id));
        Assert.Equal(2, exported.Select(item => item.AccountId).Distinct().Count());

        Assert.Equal(2L, await ScalarAsync<long>(graph.Factory,
            "SELECT CandidateSequence FROM BaiduShareResults WHERE Id=$id;", fourthResult.Id));
        Assert.Equal(1L, await ScalarAsync<long>(graph.Factory,
            "SELECT OutputOrdinal FROM BaiduShareResults WHERE Id=$id;", fourthResult.Id));
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var drift = connection.CreateCommand())
        {
            drift.CommandText = "UPDATE BaiduShareResults SET CandidateSequence=99 WHERE Id=$id;";
            drift.Parameters.AddWithValue("$id", fourthResult.Id.ToString("D"));
            await Assert.ThrowsAsync<SqliteException>(() => drift.ExecuteNonQueryAsync());
        }

        Assert.True(await store.TryStartNextGenerationAsync(
            graph.ShareOperationId, ShareOperationStatus.Succeeded, 1, reserved.LeaseGeneration, graph.Now.AddMinutes(2), graph.Now.AddSeconds(1)));
        Assert.Equal(2L, await ScalarAsync<long>(graph.Factory,
            "SELECT ExactItemCount FROM ShareOperationGenerations WHERE ShareOperationId=$id AND Generation=2;",
            graph.ShareOperationId));
        var regeneratedLease = reserved.LeaseGeneration + 1;
        var regenerated = result with { Id = Guid.NewGuid(), Generation = 2, ShareId = "share-regenerated" };
        Assert.True(await store.TryCompleteDiscoveryBaselineAsync(
            graph.ShareOperationId, 2, regeneratedLease, "regenerated-baseline", 0, graph.Now.AddSeconds(1)));
        Assert.True(await store.TryMarkSubmittingAsync(
            graph.ShareOperationId, 2, regeneratedLease, "{}", graph.Now.AddSeconds(1)));
        Assert.True(await store.TryRecordAdapterRequestAsync(
            graph.ShareOperationId, 2, regeneratedLease, "request-regenerated", graph.Now.AddSeconds(1)));
        Assert.False(await store.TryRecordAdapterRequestAsync(
            graph.ShareOperationId, 2, regeneratedLease, "different-request", graph.Now.AddSeconds(1)));
        Assert.True(await store.AcceptResultAsync(graph.ShareOperationId, ShareOperationStatus.Submitting, regeneratedLease, regenerated, graph.Now.AddSeconds(1)));
        Assert.Equal(regenerated, await store.GetAcceptedResultAsync(graph.ShareOperationId));
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*),SUM(IsCurrent) FROM BaiduShareResults WHERE ShareOperationId=$id;";
            command.Parameters.AddWithValue("$id", graph.ShareOperationId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2, reader.GetInt32(0));
            Assert.Equal(1, reader.GetInt32(1));
        }
    }

    [Fact]
    public async Task CapabilityAcceptanceRequiresExactVersionKeyAndPersistsOnlyNonsecretEvidence()
    {
        var graph = await CreateGraphAsync(includeShareSettings: false);
        var store = new SqliteAdapterCapabilityStore(graph.Factory);
        var acceptance = new AdapterCapabilityAcceptanceRecord(
            "BaiduShare", "2026-08", "8.0.0", graph.Now, new string('A', 64));

        await store.AcceptAsync(acceptance);

        Assert.Equal(acceptance, await store.GetAsync("BaiduShare", "2026-08", "8.0.0"));
        Assert.Null(await store.GetAsync("BaiduShare", "2026-09", "8.0.0"));
        await using var connection = await graph.Factory.OpenConnectionAsync();
        var columns = await ColumnsAsync(connection, "AdapterCapabilityAcceptances");
        Assert.Equal(
            new[] { "AdapterKind", "ProtocolVersion", "AdapterBuildVersion", "AcceptedAt", "EvidenceHash" },
            columns);
    }

    [Fact]
    public async Task ResultKeysetReadsDeepPagesFromTwoHundredThousandPersistedResultsWithoutLinearGrowth()
    {
        const int resultCount = 200_000;
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = graph.Factory.DatabasePath,
            Pooling = false,
            ForeignKeys = false,
        };
        await using (var connection = new SqliteConnection(builder.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH RECURSIVE sequence(value) AS (
                  VALUES(1) UNION ALL SELECT value+1 FROM sequence WHERE value<$count
                )
                INSERT INTO BaiduShareResults(
                  Id,ShareOperationId,BatchJobId,ResourceItemId,AccountId,CandidateSequence,OutputOrdinal,
                  ExactItemSetFingerprint,SettingsFingerprint,Generation,ShareId,ShareUrl,ExtractionCode,
                  IsAccepted,IsCurrent,SharedAt,UpdatedAt)
                SELECT lower(printf('%08x-0000-0000-0000-%012x',value,value)),$operation,$batch,$resource,
                  CASE WHEN value%2=1 THEN $firstAccount ELSE $secondAccount END,
                  ((value-1)/2)+1,CASE WHEN value%2=1 THEN 1 ELSE 2 END,
                  printf('items-%012d',value),$settings,1,printf('share-%012d',value),
                  'https://pan.baidu.com/s/redacted','abcd',1,1,$now,$now
                FROM sequence;
                """;
            command.Parameters.AddWithValue("$count", resultCount);
            command.Parameters.AddWithValue("$operation", graph.ShareOperationId.ToString("D"));
            command.Parameters.AddWithValue("$batch", graph.BatchId.ToString("D"));
            command.Parameters.AddWithValue("$resource", graph.ResourceId.ToString("D"));
            command.Parameters.AddWithValue("$firstAccount", graph.AccountId.ToString("D"));
            command.Parameters.AddWithValue("$secondAccount", graph.SecondAccountId.ToString("D"));
            command.Parameters.AddWithValue("$settings", graph.SettingsFingerprint);
            command.Parameters.AddWithValue("$now", graph.Now.ToString("O"));
            Assert.Equal(resultCount, await command.ExecuteNonQueryAsync());
        }

        static Guid ResultId(int value) => Guid.Parse($"{value:x8}-0000-0000-0000-{value:x12}");
        var cursor = new ShareResultCursor(99_999, 2, ResultId(199_998));
        var store = new SqliteShareSchedulingStore(graph.Factory);
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        CursorKeysetPage<BaiduShareResultRecord, ShareResultCursor>? page = null;
        for (var iteration = 0; iteration < 100; iteration++)
            page = await store.GetResultsKeysetPageAsync(new(graph.BatchId, 2, cursor));
        timer.Stop();
        var growth = Math.Max(0, GC.GetTotalMemory(forceFullCollection: true) - before);

        Assert.NotNull(page);
        Assert.Equal([ResultId(199_999), ResultId(200_000)], page.Items.Select(result => result.Id));
        Assert.Null(page.NextCursor);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"100 deep keyset reads took {timer.Elapsed}.");
        Assert.True(growth < 32L * 1024 * 1024, $"Managed growth was {growth:N0} bytes.");
    }

    [Fact]
    public async Task ResultLineageCannotDriftFromOwningOperationGeneration()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        await using var connection = await graph.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO BaiduShareResults(Id,ShareOperationId,BatchJobId,ResourceItemId,AccountId,CandidateSequence,OutputOrdinal,
              ExactItemSetFingerprint,SettingsFingerprint,Generation,ShareId,ShareUrl,ExtractionCode,
              IsAccepted,IsCurrent,SharedAt,UpdatedAt)
            VALUES($id,$operation,$batch,$resource,$account,1,1,$items,$settings,999,'share-drift',
              'https://pan.baidu.com/s/redacted','abcd',1,1,$now,$now);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$operation", graph.ShareOperationId.ToString("D"));
        command.Parameters.AddWithValue("$batch", graph.BatchId.ToString("D"));
        command.Parameters.AddWithValue("$resource", graph.ResourceId.ToString("D"));
        command.Parameters.AddWithValue("$account", graph.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$items", graph.ItemSetFingerprint);
        command.Parameters.AddWithValue("$settings", graph.SettingsFingerprint);
        command.Parameters.AddWithValue("$now", graph.Now.ToString("O"));

        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(0, await CountAsync(graph.Factory, "BaiduShareResults"));
    }

    private async Task<Graph> CreateGraphAsync(bool includeShareSettings, string batchStatus = "Active")
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, $"{Guid.NewGuid():N}.db"));
        await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
        var now = new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);
        var account = AccountRecord.Create(Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffff1"), $"user-{Guid.NewGuid():N}", "Account");
        var secondAccount = AccountRecord.Create(Guid.Parse("00000000-0000-0000-0000-000000000002"), $"user-{Guid.NewGuid():N}", "Second Account");
        var batch = new BatchJobRecord(Guid.NewGuid(), "Batch", "Excel", "Distribution", "TaskCount", "ProportionalReduction",
            1, 1, "/", "None", "Skip", "Immediate", batchStatus, now,
            batchStatus == "Active" ? now : null, null, null, 1, 4);
        var resource = new ResourceItemRecord(Guid.NewGuid(), batch.Id, null, 1, "source-url", "source-id", null, null,
            null, null, null, "None", "Valid", null, "Expanded", null, null, now, now, "Resolved title");
        var transfer = new TransferOperationRecord(Guid.NewGuid(), resource.Id, batch.Id, 1, 1, "/", "/", "", account.Id,
            "Rule", "Succeeded", "NewTransfer", 1, "transfer", "{}", "request", "[\"remote-a\",\"remote-b\"]",
            "ResultReceived", "Fulfilled", now, now, now, null, null, null, now, now);
        var shareId = Guid.NewGuid();
        var settingsFingerprint = ShareSettingsFingerprint.Compute(
            true, true, ShareExtractionCodeBehavior.Random, null);
        var itemSetFingerprint = new RemoteItemIdentitySet([
            new("remote-a", "A"),
            new("remote-b", "B")]).Fingerprint;
        var share = new ShareOperationRecord(shareId, transfer.Id, resource.Id, batch.Id, account.Id, 1,
            "WaitingShare", itemSetFingerprint, settingsFingerprint, 1, null, null, "Unknown", 0, null, null, null, null, null, now, now);

        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await unit.Accounts.UpsertAsync(account);
            await unit.Accounts.UpsertAsync(secondAccount);
            await unit.Jobs.AddAsync(batch);
            await unit.Jobs.AddResourcesAsync([resource]);
            await unit.Jobs.AddOperationsAsync([transfer]);
            if (includeShareSettings)
            {
                await unit.Jobs.SetShareSettingsAsync(new(batch.Id, true, true, "Random", null, "Ask", now, settingsFingerprint));
            }
            await unit.Jobs.AddOutputAccountsAsync([
                new(batch.Id, account.Id, 1, "Account", 3, 3, true),
                new(batch.Id, secondAccount.Id, 2, "Second Account", 1, 1, true)]);
            if (includeShareSettings)
            {
                await unit.Jobs.AddShareOperationsAsync([share], [
                    new(shareId, 1, "remote-b", "B"),
                    new(shareId, 0, "remote-a", "A")]);
            }
            await unit.CommitAsync();
        }
        if (!includeShareSettings)
            await SeedOrphanShareOperationAsync(factory, share);
        return new(factory, now, account.Id, secondAccount.Id, batch.Id, resource.Id, transfer.Id, shareId, itemSetFingerprint, settingsFingerprint);
    }

    private static async Task SeedOrphanShareOperationAsync(SqliteConnectionFactory factory, ShareOperationRecord share)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = factory.DatabasePath, Pooling = false, ForeignKeys = false };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ShareOperations(Id,TransferOperationId,ResourceItemId,BatchJobId,AccountId,OutputOrdinal,Status,
              ExactItemSetFingerprint,SettingsFingerprint,Generation,SubmissionAcceptance,AttemptCount,CreatedAt,UpdatedAt)
            VALUES($id,$transfer,$resource,$batch,$account,$ordinal,$status,$items,$settings,$generation,'Unknown',0,$created,$updated);
            """;
        command.Parameters.AddWithValue("$id", share.Id.ToString("D"));
        command.Parameters.AddWithValue("$transfer", share.TransferOperationId.ToString("D"));
        command.Parameters.AddWithValue("$resource", share.ResourceItemId.ToString("D"));
        command.Parameters.AddWithValue("$batch", share.BatchJobId.ToString("D"));
        command.Parameters.AddWithValue("$account", share.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$ordinal", share.OutputOrdinal);
        command.Parameters.AddWithValue("$status", share.Status);
        command.Parameters.AddWithValue("$items", share.ExactItemSetFingerprint);
        command.Parameters.AddWithValue("$settings", share.SettingsFingerprint);
        command.Parameters.AddWithValue("$generation", share.Generation);
        command.Parameters.AddWithValue("$created", share.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", share.UpdatedAt.ToString("O"));
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<Guid> SeedUnsealedExactOperationAsync(Graph graph)
    {
        var transferId = Guid.NewGuid();
        var shareId = Guid.NewGuid();
        await using (var unit = await SqliteUnitOfWork.BeginAsync(graph.Factory))
        {
            await unit.Jobs.AddOperationsAsync([new(
                transferId, graph.ResourceId, graph.BatchId, 2, 2, "/unsealed", "/unsealed", "",
                graph.AccountId, "Rule", "Succeeded", "NewTransfer", 1, "unsealed-transfer", "{}",
                "unsealed-request", "[\"remote-unsealed\"]", "ResultReceived", "Fulfilled", graph.Now,
                graph.Now, graph.Now, null, null, null, graph.Now, graph.Now)]);
            await unit.CommitAsync();
        }

        await using var connection = await graph.Factory.OpenConnectionAsync();
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var operation = connection.CreateCommand())
        {
            operation.Transaction = transaction;
            operation.CommandText = """
                INSERT INTO ShareOperations(Id,TransferOperationId,ResourceItemId,BatchJobId,AccountId,OutputOrdinal,Status,
                  ExactItemSetFingerprint,SettingsFingerprint,Generation,SubmissionAcceptance,AttemptCount,CreatedAt,UpdatedAt)
                VALUES($id,$transfer,$resource,$batch,$account,1,'WaitingShare','unsealed-items',$settings,1,'Unknown',0,$now,$now);
                """;
            operation.Parameters.AddWithValue("$id", shareId.ToString("D"));
            operation.Parameters.AddWithValue("$transfer", transferId.ToString("D"));
            operation.Parameters.AddWithValue("$resource", graph.ResourceId.ToString("D"));
            operation.Parameters.AddWithValue("$batch", graph.BatchId.ToString("D"));
            operation.Parameters.AddWithValue("$account", graph.AccountId.ToString("D"));
            operation.Parameters.AddWithValue("$settings", graph.SettingsFingerprint);
            operation.Parameters.AddWithValue("$now", graph.Now.ToString("O"));
            Assert.Equal(1, await operation.ExecuteNonQueryAsync());
        }
        await using (var remoteItem = connection.CreateCommand())
        {
            remoteItem.Transaction = transaction;
            remoteItem.CommandText = """
                INSERT INTO ShareOperationRemoteItems(ShareOperationId,ItemOrdinal,RemoteItemId,CanonicalSortKey,DisplayName)
                VALUES($id,0,'remote-unsealed',$sort,'Unsealed');
                """;
            remoteItem.Parameters.AddWithValue("$id", shareId.ToString("D"));
            remoteItem.Parameters.AddWithValue("$sort", new byte[] { 1 });
            Assert.Equal(1, await remoteItem.ExecuteNonQueryAsync());
        }
        await transaction.CommitAsync();
        return shareId;
    }

    private static async Task SetOrphanStateAsync(
        Graph graph, string status, bool baselineComplete = false, bool submissionStarted = false)
    {
        await using var connection = await graph.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ShareOperations SET Status=$status,
              BaselineGeneration=$baselineGeneration,BaselineFingerprint=$baselineFingerprint,
              BaselineShareCount=0,BaselineCompletedAt=$baselineCompleted,
              RequestDescriptorJson=$descriptor,SubmitStartedAt=$submitted,AdapterRequestId=NULL,
              LeaseExpiresAt=NULL,NextReconcileAt=NULL
            WHERE Id=$id;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$baselineGeneration", baselineComplete ? 1 : DBNull.Value);
        command.Parameters.AddWithValue("$baselineFingerprint", baselineComplete ? "orphan-baseline" : DBNull.Value);
        command.Parameters.AddWithValue("$baselineCompleted", baselineComplete ? graph.Now.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$descriptor", submissionStarted ? "{}" : DBNull.Value);
        command.Parameters.AddWithValue("$submitted", submissionStarted ? graph.Now.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$id", graph.ShareOperationId.ToString("D"));
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<int> CountAsync(SqliteConnectionFactory factory, string table)
    {
        var sql = table switch
        {
            "ShareDiscoveryBaselines" => "SELECT COUNT(*) FROM ShareDiscoveryBaselines;",
            "BatchShareDecisionSnapshots" => "SELECT COUNT(*) FROM BatchShareDecisionSnapshots;",
            "BaiduShareResults" => "SELECT COUNT(*) FROM BaiduShareResults;",
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task InsertBaselineAsync(Graph graph, string shareId)
    {
        await using var connection = await graph.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO ShareDiscoveryBaselines(ShareOperationId,Generation,ShareId) VALUES($id,1,$share);";
        command.Parameters.AddWithValue("$id", graph.ShareOperationId.ToString("D"));
        command.Parameters.AddWithValue("$share", shareId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<T> ScalarAsync<T>(SqliteConnectionFactory factory, string sql, Guid id)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        var value = await command.ExecuteScalarAsync() ?? throw new InvalidDataException("The scalar query returned null.");
        return (T)Convert.ChangeType(value, typeof(T));
    }

    private static async Task<IReadOnlyList<string>> ColumnsAsync(SqliteConnection connection, string table)
    {
        var values = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(reader.GetString(1));
        return values;
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private sealed record Graph(SqliteConnectionFactory Factory, DateTimeOffset Now, Guid AccountId, Guid SecondAccountId, Guid BatchId,
        Guid ResourceId, Guid TransferId, Guid ShareOperationId, string ItemSetFingerprint, string SettingsFingerprint);
}
