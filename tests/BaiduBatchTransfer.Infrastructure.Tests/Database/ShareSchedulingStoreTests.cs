using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Tests.Database;

public sealed class ShareSchedulingStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bbt-share-store-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReservationReturnsThePersistedManagedExecutionProfileSnapshot()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true, managedShare: true);

        var reserved = await new SqliteShareSchedulingStore(graph.Factory)
            .TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2));

        Assert.NotNull(reserved);
        Assert.Equal(BaiduExecutionEngine.ManagedChromiumV1, reserved.ExecutionProfile.Engine);
        Assert.Equal(BrowserFamily.Chrome, reserved.ExecutionProfile.BrowserFamily);
        Assert.Equal(graph.ShareExecutionProfile, reserved.ExecutionProfile);
    }

    [Fact]
    public async Task LoadMarksOnlyTheExactTransferLinkedAcceptanceShareAsEligible()
    {
        var graph = await CreateGraphAsync(
            includeShareSettings: true, managedShare: true, acceptanceOnly: true);
        var store = new SqliteShareSchedulingStore(graph.Factory);

        var exact = Assert.IsType<ShareExecutionContext>(await store.LoadAsync(graph.ShareOperationId));
        Assert.True(exact.AcceptanceOnly);

        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE TransferOperations SET RemoteItemIdsJson='[\"different-exact-id\"]' WHERE Id=$id;";
            command.Parameters.AddWithValue("$id", graph.TransferId.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var drifted = Assert.IsType<ShareExecutionContext>(await store.LoadAsync(graph.ShareOperationId));
        Assert.False(drifted.AcceptanceOnly);
    }

    [Fact]
    public async Task FreshShareSchemaOmitsUnusedResourceOperationIndexes()
    {
        var graph=await CreateGraphAsync(includeShareSettings:true);
        await using var connection=await graph.Factory.OpenConnectionAsync();
        await using var command=connection.CreateCommand();
        command.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_ShareOperations_Resource_Account_Status_Id';";
        Assert.Equal(0L,Convert.ToInt64(await command.ExecuteScalarAsync()));
        command.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_ShareOperations_Batch_Status_Resource_Id';";
        Assert.Equal(0L,Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task ShareDisabledHistoricalBatchCannotAcquireShareWork()
    {
        var graph = await CreateGraphAsync(includeShareSettings: false);
        var store = new SqliteShareSchedulingStore(graph.Factory);

        var reserved = await store.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2));

        Assert.Null(reserved);
    }

    [Fact]
    public async Task ShareReservationHonorsTransferAccountAndGlobalWorkerOccupancy()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE TransferOperations SET Status='Preparing',TargetSlotState='Reserved',LeaseExpiresAt=$lease
                WHERE Id=$id;
                """;
            command.Parameters.AddWithValue("$lease", graph.Now.AddMinutes(2).ToString("O"));
            command.Parameters.AddWithValue("$id", graph.TransferId.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var store = new SqliteShareSchedulingStore(graph.Factory);
        Assert.Null(await store.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2)));

        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE TransferOperations SET Status='Succeeded',TargetSlotState='Fulfilled',LeaseExpiresAt=NULL WHERE Id=$id;";
            command.Parameters.AddWithValue("$id", graph.TransferId.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        Assert.NotNull(await store.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2)));
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
            if (addRemoteItem)
            {
                await using var remote = connection.CreateCommand();
                remote.Transaction = transaction;
                remote.CommandText = "INSERT INTO ShareOperationRemoteItems(ShareOperationId,ItemOrdinal,RemoteItemId,CanonicalSortKey,DisplayName) VALUES($id,0,'remote-bypass',$sort,'Bypass');";
                remote.Parameters.AddWithValue("$id", graph.ShareOperationId.ToString("D"));
                remote.Parameters.AddWithValue("$sort", new byte[] { 1 });
                if(addLineage)Assert.Equal(1,await remote.ExecuteNonQueryAsync());
                else await Assert.ThrowsAsync<SqliteException>(()=>remote.ExecuteNonQueryAsync());
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
    public async Task ExactSetDiscoveryPlansStreamActiveCreateIndexWithoutTemporaryTrees()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        await using var connection = await graph.Factory.OpenConnectionAsync();
        var queries = new[]
        {
            SqliteShareSchedulingStore.DecisionExactSetKeysetSelectSql,
            SqliteShareSchedulingStore.CurrentExactSetsMatchRunSql,
            SqliteShareSchedulingStore.CurrentExactSetsMatchDiscoverySql,
        };
        foreach (var sql in queries)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + sql;
            command.Parameters.AddWithValue("$batch", graph.BatchId.ToString("D"));
            command.Parameters.AddWithValue("$account", graph.AccountId.ToString("D"));
            command.Parameters.AddWithValue("$settings", graph.SettingsFingerprint);
            command.Parameters.AddWithValue("$run", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$after", DBNull.Value);
            command.Parameters.AddWithValue("$limit", 501);
            var details = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) details.Add(reader.GetString(3));
            var plan = string.Join(Environment.NewLine, details);
            Assert.DoesNotContain("SCAN operation", plan, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TEMP B-TREE", plan, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("UX_ShareOperations_ActiveCreateKey", plan, StringComparison.Ordinal);
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
    public async Task StartupRestartRecoversUnexpiredReservedShareLeaseImmediately()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var firstProcess = new SqliteShareSchedulingStore(graph.Factory);
        var reserved = Assert.IsType<ShareOperationRecord>(
            await firstProcess.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2)));

        var restartedProcess = new SqliteShareSchedulingStore(graph.Factory);
        var recovered = await restartedProcess.RecoverInterruptedShareOperationsAsync(graph.Now.AddSeconds(1));

        Assert.Equal(new ShareLeaseRecoveryResult(1, 0), recovered);
        Assert.Equal("WaitingShare", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
        Assert.Null(await ScalarNullableAsync(graph.Factory,
            "SELECT LeaseExpiresAt FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
        var retried = Assert.IsType<ShareOperationRecord>(
            await restartedProcess.TryReserveNextAsync(graph.Now.AddSeconds(2), TimeSpan.FromMinutes(2)));
        Assert.Equal(reserved.Id, retried.Id);
        Assert.True(retried.LeaseGeneration > reserved.LeaseGeneration);
        Assert.Equal(1L, await ScalarAsync<long>(graph.Factory,
            "SELECT COUNT(*) FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
    }

    [Fact]
    public async Task PeriodicRecoveryDoesNotStealAnUnexpiredShareLease()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var store = new SqliteShareSchedulingStore(graph.Factory);
        await store.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2));

        var recovered = await store.RecoverExpiredShareOperationsAsync(graph.Now.AddSeconds(1));

        Assert.Equal(new ShareLeaseRecoveryResult(0, 0), recovered);
        Assert.Equal("Reserved", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
    }

    public static TheoryData<string, string> ShareCrashBoundaries => new()
    {
        { "reserve", "WaitingShare" },
        { "prepare", "WaitingShare" },
        { "submit", "ResultPending" },
        { "response-id", "ResultPending" },
    };

    [Theory]
    [MemberData(nameof(ShareCrashBoundaries))]
    public async Task RestartAtShareWriteBoundaryRetriesOnlySafeWorkAndQueuesUnknownForReconciliation(
        string boundary,
        string expectedStatus)
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var firstProcess = new SqliteShareSchedulingStore(graph.Factory);
        var lease = Assert.IsType<ShareOperationRecord>(
            await firstProcess.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2)));
        if (boundary != "reserve")
        {
            Assert.True(await firstProcess.TryMarkPreparingAsync(
                graph.ShareOperationId, 1, lease.LeaseGeneration, graph.Now));
        }
        if (boundary is "submit" or "response-id")
        {
            Assert.True(await firstProcess.TryCompleteDiscoveryBaselineAsync(
                graph.ShareOperationId, 1, lease.LeaseGeneration, "baseline", 0, graph.Now));
            Assert.True(await firstProcess.TryMarkSubmittingAsync(
                graph.ShareOperationId, 1, lease.LeaseGeneration, "{\"safe\":true}", graph.Now));
        }
        if (boundary == "response-id")
        {
            Assert.True(await firstProcess.TryRecordAdapterRequestAsync(
                graph.ShareOperationId, 1, lease.LeaseGeneration, "request-1", graph.Now));
        }

        var restartedProcess = new SqliteShareSchedulingStore(graph.Factory);
        var recovered = await restartedProcess.RecoverInterruptedShareOperationsAsync(graph.Now.AddSeconds(1));

        Assert.Equal(expectedStatus, await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
        Assert.Equal(0, await CountAsync(graph.Factory, "BaiduShareResults"));
        Assert.Equal(expectedStatus == "WaitingShare" ? 1 : 0, recovered.RetryableOperationCount);
        Assert.Equal(expectedStatus == "ResultPending" ? 1 : 0, recovered.ResultPendingOperationCount);
        if (expectedStatus == "WaitingShare")
        {
            var retried = Assert.IsType<ShareOperationRecord>(
                await restartedProcess.TryReserveNextAsync(graph.Now.AddSeconds(2), TimeSpan.FromMinutes(2)));
            Assert.Equal(graph.ShareOperationId, retried.Id);
            Assert.True(retried.LeaseGeneration > lease.LeaseGeneration);
        }
        else
        {
            Assert.Null(await restartedProcess.TryReserveNextAsync(
                graph.Now.AddSeconds(2), TimeSpan.FromMinutes(2)));
            Assert.Equal(graph.ShareOperationId,
                (await restartedProcess.TryGetDueResultPendingAsync(
                    graph.BatchId, graph.Now.AddSeconds(2), []))?.Id);
            var baseline = Assert.IsType<ShareDiscoveryBaselineStateRecord>(
                await restartedProcess.GetDiscoveryBaselineStateAsync(graph.ShareOperationId));
            Assert.Equal(1, baseline.Generation);
            Assert.Equal("baseline", baseline.Fingerprint);
        }
        Assert.Equal(1L, await ScalarAsync<long>(graph.Factory,
            "SELECT COUNT(*) FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
    }

    [Fact]
    public async Task AtomicShareResultSurvivesRestartAndCannotBePersistedTwice()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var firstProcess = new SqliteShareSchedulingStore(graph.Factory);
        var lease = Assert.IsType<ShareOperationRecord>(
            await firstProcess.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2)));
        Assert.True(await firstProcess.TryMarkPreparingAsync(
            graph.ShareOperationId, 1, lease.LeaseGeneration, graph.Now));
        Assert.True(await firstProcess.TryCompleteDiscoveryBaselineAsync(
            graph.ShareOperationId, 1, lease.LeaseGeneration, "baseline", 0, graph.Now));
        Assert.True(await firstProcess.TryMarkSubmittingAsync(
            graph.ShareOperationId, 1, lease.LeaseGeneration, "{\"safe\":true}", graph.Now));
        Assert.True(await firstProcess.TryRecordAdapterRequestAsync(
            graph.ShareOperationId, 1, lease.LeaseGeneration, "request-1", graph.Now));
        var result = AcceptedResult(graph);

        Assert.True(await firstProcess.AcceptResultAsync(
            graph.ShareOperationId, ShareOperationStatus.Submitting, lease.LeaseGeneration, result, graph.Now));

        var restartedProcess = new SqliteShareSchedulingStore(graph.Factory);
        Assert.Equal(new ShareLeaseRecoveryResult(0, 0),
            await restartedProcess.RecoverInterruptedShareOperationsAsync(graph.Now.AddSeconds(1)));
        Assert.Equal("Succeeded", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
        Assert.Equal(result, await restartedProcess.GetAcceptedResultAsync(graph.ShareOperationId));
        Assert.False(await restartedProcess.AcceptResultAsync(
            graph.ShareOperationId, ShareOperationStatus.Submitting, lease.LeaseGeneration,
            result with { Id = Guid.NewGuid() }, graph.Now.AddSeconds(1)));
        Assert.Equal(1, await CountAsync(graph.Factory, "BaiduShareResults"));
    }

    [Fact]
    public async Task PersistedShareResultCanRebuildAndFinalizeAfterRestartedCompletionCheck()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        await using (var unit = await SqliteUnitOfWork.BeginAsync(graph.Factory))
        {
            await unit.Jobs.AddAllocationsAsync([new(
                Guid.NewGuid(), graph.BatchId, graph.AccountId, 1, "/", "/",
                1, 0, 0, 1, 1, 1, 0, 1, 0, 0, 0, 0, 0, 0)]);
            await unit.CommitAsync();
        }
        var firstProcess = new SqliteShareSchedulingStore(graph.Factory);
        var lease = Assert.IsType<ShareOperationRecord>(
            await firstProcess.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2)));
        Assert.True(await firstProcess.TryMarkPreparingAsync(
            graph.ShareOperationId, 1, lease.LeaseGeneration, graph.Now));
        Assert.True(await firstProcess.TryCompleteDiscoveryBaselineAsync(
            graph.ShareOperationId, 1, lease.LeaseGeneration, "baseline", 0, graph.Now));
        Assert.True(await firstProcess.TryMarkSubmittingAsync(
            graph.ShareOperationId, 1, lease.LeaseGeneration, "{\"safe\":true}", graph.Now));
        Assert.True(await firstProcess.TryRecordAdapterRequestAsync(
            graph.ShareOperationId, 1, lease.LeaseGeneration, "request-1", graph.Now));
        Assert.True(await firstProcess.AcceptResultAsync(
            graph.ShareOperationId, ShareOperationStatus.Submitting, lease.LeaseGeneration,
            AcceptedResult(graph), graph.Now));

        var restartedTransferStore = new SqliteTransferSchedulingStore(graph.Factory);
        var rebuilt = await restartedTransferStore.RebuildAllocationCountersAsync();
        var finalized = await restartedTransferStore.TryFinalizeExhaustedBatchAsync(
            graph.BatchId, graph.Now.AddSeconds(1));

        Assert.Equal(new AllocationCounterRebuildResult(1, 0, 1), rebuilt);
        Assert.Equal(BatchExhaustionOutcome.Completed, finalized);
        Assert.Equal("Completed", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM BatchJobs WHERE Id=$id;", graph.BatchId));
        Assert.Equal(1, await CountAsync(graph.Factory, "BaiduShareResults"));
        Assert.Equal(BatchExhaustionOutcome.NotExhausted,
            await new SqliteTransferSchedulingStore(graph.Factory).TryFinalizeExhaustedBatchAsync(
                graph.BatchId, graph.Now.AddSeconds(2)));
    }

    [Theory]
    [InlineData("Reserved", "Unknown", false, "WaitingShare")]
    [InlineData("Preparing", "Unknown", false, "WaitingShare")]
    [InlineData("Submitting", "DefinitelyNotAccepted", true, "WaitingShare")]
    [InlineData("Submitting", "Unknown", true, "ResultPending")]
    public async Task StartupShareRecoveryDistinguishesSafeRetryFromUnknownAcceptance(
        string status,
        string acceptance,
        bool submitted,
        string expected)
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE ShareOperations SET Status=$status,SubmissionAcceptance=$acceptance,
                  LeaseExpiresAt=$expired,RequestDescriptorJson=$descriptor,SubmitStartedAt=$submitted,
                  BaselineGeneration=$baselineGeneration,BaselineFingerprint=$baselineFingerprint,
                  BaselineCompletedAt=$baselineCompleted
                WHERE Id=$id;
                """;
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$acceptance", acceptance);
            command.Parameters.AddWithValue("$expired", graph.Now.AddMinutes(-1).ToString("O"));
            command.Parameters.AddWithValue("$descriptor", submitted ? "{}" : DBNull.Value);
            command.Parameters.AddWithValue("$submitted", submitted ? graph.Now.AddMinutes(-2).ToString("O") : DBNull.Value);
            command.Parameters.AddWithValue("$baselineGeneration", submitted ? 1 : DBNull.Value);
            command.Parameters.AddWithValue("$baselineFingerprint", submitted ? "baseline" : DBNull.Value);
            command.Parameters.AddWithValue("$baselineCompleted", submitted ? graph.Now.AddMinutes(-3).ToString("O") : DBNull.Value);
            command.Parameters.AddWithValue("$id", graph.ShareOperationId.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var result = await new SqliteShareSchedulingStore(graph.Factory)
            .RecoverInterruptedShareOperationsAsync(graph.Now);

        Assert.Equal(expected, await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
        Assert.Equal(expected == "WaitingShare" ? 1 : 0, result.RetryableOperationCount);
        Assert.Equal(expected == "ResultPending" ? 1 : 0, result.ResultPendingOperationCount);
        Assert.Null(await ScalarNullableAsync(graph.Factory,
            "SELECT LeaseExpiresAt FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
    }

    [Fact]
    public async Task ExpiredRecoverySkipsEarlierUnexpiredRowAndReturnsEarliestCanonicalLease()
    {
        const int unexpiredCount = 1;
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
                INSERT OR IGNORE INTO BatchOutputAccounts(
                  BatchJobId,AccountId,OutputOrdinal,OutputLabel,PlannedTransferCount,PlannedShareCount,ShareEnabled)
                VALUES($batch,$account,$ordinal,$label,$planned,$planned,1);
                INSERT INTO ShareOperations(Id,TransferOperationId,ResourceItemId,BatchJobId,AccountId,OutputOrdinal,
                  Status,ExactItemSetFingerprint,SettingsFingerprint,Generation,SubmissionAcceptance,AttemptCount,
                  LeaseGeneration,LeaseExpiresAt,CreatedAt,UpdatedAt,
                  ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
                VALUES($id,$transfer,$resource,$batch,$account,$ordinal,'Reserved',$items,$settings,1,'Unknown',0,1,$lease,$created,$created,
                  'WebView2-v1','LegacyWebView2','legacy-webview2-account-' || lower(replace($account,'-','')),NULL,NULL);
                """;
            for (var index = 1; index <= unexpiredCount + 1; index++)
            {
                var id = index <= unexpiredCount ? new Guid(index, 0, 0, new byte[8]) : expiredId;
                var accountId = index <= unexpiredCount
                    ? new Guid(100_000 + index, 0, 0, new byte[8])
                    : graph.AccountId;
                var outputOrdinal = index <= unexpiredCount ? index + 2 : 1;
                var remoteId = $"remote-{index}";
                var itemSetFingerprint = index <= unexpiredCount
                    ? $"items-{index}"
                    : new RemoteItemIdentitySet([new(remoteId, "Expired")]).Fingerprint;
                insert.Parameters.Clear();
                insert.Parameters.AddWithValue("$id", id.ToString("D"));
                insert.Parameters.AddWithValue("$transfer", Guid.NewGuid().ToString("D"));
                insert.Parameters.AddWithValue("$resource", graph.ResourceId.ToString("D"));
                insert.Parameters.AddWithValue("$batch", graph.BatchId.ToString("D"));
                insert.Parameters.AddWithValue("$account", accountId.ToString("D"));
                insert.Parameters.AddWithValue("$ordinal", outputOrdinal);
                insert.Parameters.AddWithValue("$label", $"Performance output {outputOrdinal}");
                insert.Parameters.AddWithValue("$planned", unexpiredCount + 2);
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
    public async Task ExecutionContextAndSubmissionTransitionsAreLeaseGenerationFenced()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var store = new SqliteShareSchedulingStore(graph.Factory);
        var reserved = Assert.IsType<ShareOperationRecord>(
            await store.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2)));

        var context = Assert.IsType<ShareExecutionContext>(await store.LoadAsync(graph.ShareOperationId));
        Assert.True(context.Selected);
        Assert.True(context.ShareEnabled);
        Assert.Equal(graph.ItemSetFingerprint, context.ExactRemoteItems.Fingerprint);
        Assert.Equal("Random", context.Settings.ExtractionCodeMode);
        Assert.Null(context.ExistingShareDecision);

        Assert.False(await store.TryMarkPreparingAsync(
            graph.ShareOperationId, 1, reserved.LeaseGeneration + 1, graph.Now));
        Assert.True(await store.TryMarkPreparingAsync(
            graph.ShareOperationId, 1, reserved.LeaseGeneration, graph.Now));
        Assert.True(await store.TryCompleteDiscoveryBaselineAsync(
            graph.ShareOperationId, 1, reserved.LeaseGeneration, "empty", 0, graph.Now));
        Assert.True(await store.TryMarkSubmittingAsync(
            graph.ShareOperationId, 1, reserved.LeaseGeneration, "{\"safe\":true}", graph.Now));
        Assert.False(await store.TryRecordDefinitelyNotAcceptedRetryAsync(
            graph.ShareOperationId, 1, reserved.LeaseGeneration, 2, graph.Now));
        Assert.True(await store.TryRecordDefinitelyNotAcceptedRetryAsync(
            graph.ShareOperationId, 1, reserved.LeaseGeneration, 1, graph.Now));
        Assert.True(await store.TryBeginSubmissionAttemptAsync(
            graph.ShareOperationId, 1, reserved.LeaseGeneration, 1, graph.Now));
        Assert.True(await store.TryMarkResultPendingAsync(
            graph.ShareOperationId, 1, reserved.LeaseGeneration, graph.Now.AddMinutes(1), graph.Now));
        Assert.False(await store.TryFailAsync(
            graph.ShareOperationId, 1, reserved.LeaseGeneration + 1, "TransientService", "safe", graph.Now));

        Assert.Equal("ResultPending", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
        Assert.Equal(2L, await ScalarAsync<long>(graph.Factory,
            "SELECT AttemptCount FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
        Assert.Equal("Unknown", await ScalarAsync<string>(graph.Factory,
            "SELECT SubmissionAcceptance FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
    }

    [Fact]
    public async Task RestartedSqliteStoreReconcilesOnlyTheNewCompatibleShareAgainstPersistedBaseline()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var firstProcess = new SqliteShareSchedulingStore(graph.Factory);
        var reserved = Assert.IsType<ShareOperationRecord>(
            await firstProcess.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2)));
        var exactItems = new RemoteItemIdentitySet([
            new("remote-a", "A"),
            new("remote-b", "B"),
        ]);
        var oldShare = new BaiduShareRecord(
            "baseline-old", exactItems, "https://pan.baidu.com/s/baseline-old", "old1", true, true);
        var newShare = new BaiduShareRecord(
            "reconciled-new", exactItems, "https://pan.baidu.com/s/reconciled-new", "new1", true, true);

        await firstProcess.AppendDiscoveryBaselinePageAsync(
            graph.ShareOperationId,
            reserved.LeaseGeneration,
            [new(graph.ShareOperationId, oldShare.ShareId, 1, oldShare.ShareUrl, oldShare.ExtractionCode,
                oldShare.IsPermanent, oldShare.IsAccessUnlimited)]);
        Assert.True(await firstProcess.TryCompleteDiscoveryBaselineAsync(
            graph.ShareOperationId, 1, reserved.LeaseGeneration, "pre-create-baseline", 1, graph.Now));
        Assert.True(await firstProcess.TryMarkSubmittingAsync(
            graph.ShareOperationId, 1, reserved.LeaseGeneration, "{\"safe\":true}", graph.Now));
        Assert.True(await firstProcess.TryMarkResultPendingAsync(
            graph.ShareOperationId, 1, reserved.LeaseGeneration, graph.Now.AddMinutes(1), graph.Now));

        var restartedStore = new SqliteShareSchedulingStore(graph.Factory);
        var restartedPending = Assert.IsType<ShareExecutionContext>(
            await restartedStore.LoadPendingAsync(graph.ShareOperationId));
        Assert.Equal("ResultPending", restartedPending.Operation.Status);
        Assert.Equal("Unknown", restartedPending.Operation.SubmissionAcceptance);
        var restartedBaseline = await restartedStore.GetBaselinePageAsync(
            graph.ShareOperationId, null, 10);
        Assert.Equal(oldShare.ShareId, Assert.Single(restartedBaseline.Items).ShareId);
        var adapter = new RestartReconciliationAdapter([oldShare, newShare]);
        var service = new ShareOperationReconciliationService(
            adapter,
            new RestartReconciliationSessionStore(graph.AccountId, graph.Now),
            restartedStore,
            new NoDelay(),
            new ShareReconciliationOptions(1, 10, TimeSpan.Zero, TimeSpan.FromMinutes(1)),
            new FixedTimeProvider(graph.Now.AddMinutes(1)));

        var reconciliation = await service.ReconcileAsync(graph.ShareOperationId);

        Assert.Equal(ShareReconciliationDisposition.Accepted, reconciliation.Disposition);
        Assert.Equal(0, adapter.CreateCalls);
        Assert.Equal("Succeeded", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
        var accepted = Assert.IsType<BaiduShareResultRecord>(
            await restartedStore.GetAcceptedResultAsync(graph.ShareOperationId));
        Assert.Equal(newShare.ShareId, accepted.ShareId);
        Assert.NotEqual(oldShare.ShareId, accepted.ShareId);
        await using var connection = await graph.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*),SUM(IsCurrent) FROM BaiduShareResults WHERE ShareOperationId=$id;";
        command.Parameters.AddWithValue("$id", graph.ShareOperationId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
    }

    [Fact]
    public async Task DueReconciliationExcludesBusyAccountInsideSqliteAndWorkSourceReturnsSecondAccount()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var store = new SqliteShareSchedulingStore(graph.Factory);
        var firstLease = Assert.IsType<ShareOperationRecord>(
            await store.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2)));
        Assert.True(await store.TryCompleteDiscoveryBaselineAsync(
            graph.ShareOperationId, 1, firstLease.LeaseGeneration, "empty", 0, graph.Now));
        Assert.True(await store.TryMarkSubmittingAsync(
            graph.ShareOperationId, 1, firstLease.LeaseGeneration, "{\"safe\":true}", graph.Now));
        Assert.True(await store.TryMarkResultPendingAsync(
            graph.ShareOperationId, 1, firstLease.LeaseGeneration, graph.Now, graph.Now));

        var secondTransfer = new TransferOperationRecord(
            Guid.NewGuid(), graph.ResourceId, graph.BatchId, 2, 2, "/two", "/two", "",
            graph.SecondAccountId, "Rule", "Cancelled", "Cancelled", 0, "transfer-two", null, null, null,
            "NotPrepared", "Released", null, null, null, null, null, null, graph.Now, graph.Now);
        var secondShareId = Guid.NewGuid();
        var secondItems = new RemoteItemIdentitySet([new("remote-c", "C")]);
        var secondShare = new ShareOperationRecord(
            secondShareId, secondTransfer.Id, graph.ResourceId, graph.BatchId, graph.SecondAccountId, 2,
            "ResultPending", secondItems.Fingerprint, graph.SettingsFingerprint, 1, "{\"safe\":true}", null,
            "Unknown", 1, null, graph.Now, graph.Now, null, null, graph.Now, graph.Now, 1);
        await using (var unit = await SqliteUnitOfWork.BeginAsync(graph.Factory))
        {
            await unit.Jobs.AddOperationsAsync([secondTransfer]);
            await unit.Jobs.AddShareOperationsAsync(
                [secondShare], [new(secondShareId, 0, "remote-c", "C")]);
            await unit.CommitAsync();
        }

        Assert.Equal(graph.ShareOperationId,
            (await store.TryGetDueResultPendingAsync(graph.BatchId, graph.Now, []))?.Id);
        Assert.Equal(secondShareId,
            (await store.TryGetDueResultPendingAsync(graph.BatchId, graph.Now, [graph.AccountId]))?.Id);
        Assert.Null(await store.TryGetDueResultPendingAsync(
            graph.BatchId, graph.Now, [graph.AccountId, graph.SecondAccountId]));
        Assert.Null(await store.TryGetDueResultPendingAsync(
            graph.BatchId, graph.Now, [graph.AccountId, graph.SecondAccountId, Guid.NewGuid()]));

        var adapter = new RestartReconciliationAdapter([]);
        var sessions = new RestartReconciliationSessionStore(graph.AccountId, graph.Now);
        var executor = new ShareOperationExecutor(adapter, sessions, store, RetryPolicy.Default, new NoDelay());
        var reconciliation = new ShareOperationReconciliationService(
            adapter, sessions, store, new NoDelay(),
            new ShareReconciliationOptions(1, 10, TimeSpan.Zero, TimeSpan.FromMinutes(1)));
        var source = new ShareCoordinatorWorkSource(store, store, executor, reconciliation);

        var work = await source.TryTakeDueReconciliationAsync(
            graph.BatchId, graph.Now, [graph.AccountId]);

        Assert.NotNull(work);
        Assert.Equal(secondShareId, work.OperationId);
        Assert.Equal(graph.SecondAccountId, work.AccountId);
    }

    [Fact]
    public async Task ExplicitRejectionReconciliationUsesExactSnapshotAndPreservesSubmissionEvidence()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var store = new SqliteShareSchedulingStore(graph.Factory);
        await PrepareResultPendingAsync(store, graph);
        var context = Assert.IsType<ShareExecutionContext>(await store.LoadPendingAsync(graph.ShareOperationId));
        var operation = context.Operation;
        var snapshot = new ShareReconciliationFailureSnapshot(
            operation.BatchJobId, operation.ResourceItemId, operation.AccountId, operation.OutputOrdinal,
            operation.ExactItemSetFingerprint, operation.SettingsFingerprint, operation.AttemptCount,
            operation.UpdatedAt, operation.NextReconcileAt);

        Assert.False(await store.TryFailDefinitelyNotAcceptedAsync(
            operation.Id, operation.Generation, operation.LeaseGeneration,
            snapshot with { AttemptCount = snapshot.AttemptCount + 1 }, graph.Now.AddMinutes(1)));
        Assert.False(await store.TryFailDefinitelyNotAcceptedAsync(
            operation.Id, operation.Generation, operation.LeaseGeneration,
            snapshot with { SettingsFingerprint = "wrong-settings" }, graph.Now.AddMinutes(1)));
        Assert.True(await store.TryFailDefinitelyNotAcceptedAsync(
            operation.Id, operation.Generation, operation.LeaseGeneration,
            snapshot, graph.Now.AddMinutes(1)));
        Assert.False(await store.TryFailDefinitelyNotAcceptedAsync(
            operation.Id, operation.Generation, operation.LeaseGeneration,
            snapshot, graph.Now.AddMinutes(2)));

        Assert.Equal("Failed", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
        Assert.Equal("DefinitelyNotAccepted", await ScalarAsync<string>(graph.Factory,
            "SELECT SubmissionAcceptance FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
        Assert.Equal("{\"safe\":true}", await ScalarAsync<string>(graph.Factory,
            "SELECT RequestDescriptorJson FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
        Assert.Equal("OfficialRejection", await ScalarAsync<string>(graph.Factory,
            "SELECT LastErrorCode FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
        Assert.Equal(1L, await ScalarAsync<long>(graph.Factory,
            "SELECT LeaseExpiresAt IS NULL FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
    }

    [Theory]
    [InlineData(BaiduErrorKind.ExpiredLogin, "AccountSessionUnavailable", "等待账号登录后继续确认分享结果")]
    [InlineData(BaiduErrorKind.VerificationRequired, "AccountVerificationRequired", "账号完成安全验证后继续确认分享结果")]
    public async Task ListingAccountFailureAtomicallyInvalidatesAccountUntilReactivatedThenReconciles(
        BaiduErrorKind errorKind,
        string expectedIssue,
        string expectedMessage)
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var store = new SqliteShareSchedulingStore(graph.Factory);
        var lease = Assert.IsType<ShareOperationRecord>(
            await store.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2)));
        Assert.True(await store.TryCompleteDiscoveryBaselineAsync(
            graph.ShareOperationId, 1, lease.LeaseGeneration, "empty", 0, graph.Now));
        Assert.True(await store.TryMarkSubmittingAsync(
            graph.ShareOperationId, 1, lease.LeaseGeneration, "{\"safe\":true}", graph.Now));
        Assert.True(await store.TryRecordAdapterRequestAsync(
            graph.ShareOperationId, 1, lease.LeaseGeneration, "request-1", graph.Now));
        Assert.True(await store.TryMarkResultPendingAsync(
            graph.ShareOperationId, 1, lease.LeaseGeneration, graph.Now, graph.Now));
        var compatible = new BaiduShareRecord(
            "new-after-login",
            new([new("remote-a", "A"), new("remote-b", "B")]),
            "https://pan.baidu.com/s/new-after-login", "n3w1", true, true);
        var adapter = new SequencedReconciliationAdapter([
            BaiduAdapterResult<BaiduSharePage>.Failure(errorKind, "private session diagnostic"),
            BaiduAdapterResult<BaiduSharePage>.Success(new([compatible], null)),
        ]);
        var service = new ShareOperationReconciliationService(
            adapter,
            new RestartReconciliationSessionStore(graph.AccountId, graph.Now),
            store,
            new NoDelay(),
            new ShareReconciliationOptions(1, 10, TimeSpan.Zero, TimeSpan.FromMinutes(1)),
            new FixedTimeProvider(graph.Now.AddMinutes(1)));

        var paused = await service.ReconcileAsync(graph.ShareOperationId);

        Assert.Equal(ShareReconciliationDisposition.RequiresUser, paused.Disposition);
        Assert.Equal("Invalid", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM Accounts WHERE Id=$id;", graph.AccountId));
        Assert.Equal("ResultPending", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
        Assert.Equal("Unknown", await ScalarAsync<string>(graph.Factory,
            "SELECT SubmissionAcceptance FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
        Assert.Equal("{\"safe\":true}", await ScalarAsync<string>(graph.Factory,
            "SELECT RequestDescriptorJson FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
        Assert.Equal("request-1", await ScalarAsync<string>(graph.Factory,
            "SELECT AdapterRequestId FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
        Assert.Equal(expectedIssue, await ScalarAsync<string>(graph.Factory,
            "SELECT LastErrorCode FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
        Assert.Equal(expectedMessage, await ScalarAsync<string>(graph.Factory,
            "SELECT LastErrorMessage FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
        Assert.Null(await store.TryGetDueResultPendingAsync(
            graph.BatchId, graph.Now.AddMinutes(3), []));

        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE Accounts SET Status='Active' WHERE Id=$id;";
            command.Parameters.AddWithValue("$id", graph.AccountId.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        Assert.Equal(graph.ShareOperationId,
            (await store.TryGetDueResultPendingAsync(graph.BatchId, graph.Now.AddMinutes(3), []))?.Id);

        var accepted = await service.ReconcileAsync(graph.ShareOperationId);

        Assert.Equal(ShareReconciliationDisposition.Accepted, accepted.Disposition);
        Assert.Equal("Succeeded", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
        Assert.Equal(compatible.ShareId,
            (await store.GetAcceptedResultAsync(graph.ShareOperationId))?.ShareId);
        Assert.Equal(0, adapter.CreateCalls);
    }

    [Fact]
    public async Task ConcurrentReconciliationOfSameSqliteOperationPersistsOnlyOneAcceptedResult()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var store = new SqliteShareSchedulingStore(graph.Factory);
        var lease = Assert.IsType<ShareOperationRecord>(
            await store.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2)));
        Assert.True(await store.TryCompleteDiscoveryBaselineAsync(
            graph.ShareOperationId, 1, lease.LeaseGeneration, "empty", 0, graph.Now));
        Assert.True(await store.TryMarkSubmittingAsync(
            graph.ShareOperationId, 1, lease.LeaseGeneration, "{\"safe\":true}", graph.Now));
        Assert.True(await store.TryMarkResultPendingAsync(
            graph.ShareOperationId, 1, lease.LeaseGeneration, graph.Now, graph.Now));
        var candidate = new BaiduShareRecord(
            "one-current-result",
            new([new("remote-a", "A"), new("remote-b", "B")]),
            "https://pan.baidu.com/s/one-current-result", "same", true, true);
        var adapter = new ConcurrentSqliteReconciliationAdapter(candidate);
        var service = new ShareOperationReconciliationService(
            adapter,
            new RestartReconciliationSessionStore(graph.AccountId, graph.Now),
            store,
            new NoDelay(),
            new ShareReconciliationOptions(1, 10, TimeSpan.Zero, TimeSpan.FromMinutes(1)),
            new FixedTimeProvider(graph.Now.AddMinutes(1)));

        var results = await Task.WhenAll(
            service.ReconcileAsync(graph.ShareOperationId),
            service.ReconcileAsync(graph.ShareOperationId));

        Assert.Equal(2, adapter.MaximumConcurrentListings);
        Assert.Single(results, result => result.Disposition == ShareReconciliationDisposition.Accepted);
        Assert.Single(results, result => result.Disposition == ShareReconciliationDisposition.Stale);
        await using var connection = await graph.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*),SUM(IsCurrent) FROM BaiduShareResults WHERE ShareOperationId=$id;";
        command.Parameters.AddWithValue("$id", graph.ShareOperationId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
    }

    [Fact]
    public async Task KeepPendingWinningControlledSqliteRaceMakesConcurrentAcceptStale()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var sqlite = new SqliteShareSchedulingStore(graph.Factory);
        await PrepareResultPendingAsync(sqlite, graph);
        var store = new KeepFirstReconciliationStore(sqlite);
        var candidate = new BaiduShareRecord(
            "must-not-win-after-keep",
            new([new("remote-a", "A"), new("remote-b", "B")]),
            "https://pan.baidu.com/s/must-not-win-after-keep", "race", true, true);
        var adapter = new OrderedOutcomeAdapter(candidate, bothFailures: false);
        var service = new ShareOperationReconciliationService(
            adapter,
            new RestartReconciliationSessionStore(graph.AccountId, graph.Now),
            store,
            new NoDelay(),
            new ShareReconciliationOptions(1, 10, TimeSpan.Zero, TimeSpan.FromMinutes(1)),
            new FixedTimeProvider(graph.Now.AddMinutes(1)));

        var results = await Task.WhenAll(
            service.ReconcileAsync(graph.ShareOperationId),
            service.ReconcileAsync(graph.ShareOperationId));

        Assert.Single(results, result => result.Disposition == ShareReconciliationDisposition.RequiresUser);
        Assert.Single(results, result => result.Disposition == ShareReconciliationDisposition.Stale);
        Assert.Equal("ResultPending", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
        Assert.Equal("Invalid", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM Accounts WHERE Id=$id;", graph.AccountId));
        Assert.Equal(0, await CountAsync(graph.Factory, "BaiduShareResults"));
    }

    [Fact]
    public async Task AcceptWinningControlledSqliteRaceMakesConcurrentKeepStaleWithoutInvalidatingAccount()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var sqlite = new SqliteShareSchedulingStore(graph.Factory);
        await PrepareResultPendingAsync(sqlite, graph);
        var store = new AcceptFirstReconciliationStore(sqlite);
        var candidate = new BaiduShareRecord(
            "accept-wins",
            new([new("remote-a", "A"), new("remote-b", "B")]),
            "https://pan.baidu.com/s/accept-wins", "race", true, true);
        var service = new ShareOperationReconciliationService(
            new OrderedOutcomeAdapter(candidate, bothFailures: false),
            new RestartReconciliationSessionStore(graph.AccountId, graph.Now),
            store,
            new NoDelay(),
            new ShareReconciliationOptions(1, 10, TimeSpan.Zero, TimeSpan.FromMinutes(1)),
            new FixedTimeProvider(graph.Now.AddMinutes(1)));

        var results = await Task.WhenAll(
            service.ReconcileAsync(graph.ShareOperationId),
            service.ReconcileAsync(graph.ShareOperationId));

        Assert.Single(results, result => result.Disposition == ShareReconciliationDisposition.Accepted);
        Assert.Single(results, result => result.Disposition == ShareReconciliationDisposition.Stale);
        Assert.Equal("Succeeded", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
        Assert.Equal("Active", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM Accounts WHERE Id=$id;", graph.AccountId));
        Assert.Equal(1, await CountAsync(graph.Factory, "BaiduShareResults"));
    }

    [Fact]
    public async Task FirstKeepPendingWinningControlledSqliteRaceMakesSecondKeepStale()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var sqlite = new SqliteShareSchedulingStore(graph.Factory);
        await PrepareResultPendingAsync(sqlite, graph);
        var store = new KeepFirstReconciliationStore(sqlite);
        var adapter = new OrderedOutcomeAdapter(candidate: null, bothFailures: true);
        var service = new ShareOperationReconciliationService(
            adapter,
            new RestartReconciliationSessionStore(graph.AccountId, graph.Now),
            store,
            new NoDelay(),
            new ShareReconciliationOptions(1, 10, TimeSpan.Zero, TimeSpan.FromMinutes(1)),
            new FixedTimeProvider(graph.Now.AddMinutes(1)));

        var results = await Task.WhenAll(
            service.ReconcileAsync(graph.ShareOperationId),
            service.ReconcileAsync(graph.ShareOperationId));

        Assert.Single(results, result => result.Disposition == ShareReconciliationDisposition.RequiresUser);
        Assert.Single(results, result => result.Disposition == ShareReconciliationDisposition.Stale);
        Assert.Equal(0, await CountAsync(graph.Factory, "BaiduShareResults"));
    }

    [Fact]
    public async Task NewerActiveLoginIsNotInvalidatedByOlderObservedSessionFailure()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var sqlite = new SqliteShareSchedulingStore(graph.Factory);
        await PrepareResultPendingAsync(sqlite, graph);
        var refreshedAt = graph.Now.AddMinutes(5);
        var store = new RefreshAccountBeforeKeepStore(sqlite, graph.Factory, graph.AccountId, refreshedAt);
        var compatible = new BaiduShareRecord(
            "new-login-result",
            new([new("remote-a", "A"), new("remote-b", "B")]),
            "https://pan.baidu.com/s/new-login-result", "fresh", true, true);
        var adapter = new SequencedReconciliationAdapter([
            BaiduAdapterResult<BaiduSharePage>.Failure(BaiduErrorKind.ExpiredLogin, "old session failed"),
            BaiduAdapterResult<BaiduSharePage>.Success(new([compatible], null)),
        ]);
        var sessions = new SequencedCapturedSessionStore(graph.AccountId, [graph.Now, refreshedAt]);
        var service = new ShareOperationReconciliationService(
            adapter, sessions, store, new NoDelay(),
            new ShareReconciliationOptions(1, 10, TimeSpan.Zero, TimeSpan.FromMinutes(1)),
            new FixedTimeProvider(graph.Now.AddMinutes(1)));

        var oldFailure = await service.ReconcileAsync(graph.ShareOperationId);

        Assert.Equal(ShareReconciliationDisposition.RequiresUser, oldFailure.Disposition);
        Assert.Equal("Active", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM Accounts WHERE Id=$id;", graph.AccountId));
        Assert.Equal(refreshedAt, DateTimeOffset.Parse(await ScalarAsync<string>(graph.Factory,
            "SELECT LastValidatedAt FROM Accounts WHERE Id=$id;", graph.AccountId)));
        Assert.Equal("ResultPending", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
        Assert.Equal(graph.ShareOperationId,
            (await sqlite.TryGetDueResultPendingAsync(graph.BatchId, graph.Now.AddMinutes(3), []))?.Id);

        var accepted = await service.ReconcileAsync(graph.ShareOperationId);

        Assert.Equal(ShareReconciliationDisposition.Accepted, accepted.Disposition);
        Assert.Equal(compatible.ShareId,
            (await sqlite.GetAcceptedResultAsync(graph.ShareOperationId))?.ShareId);
        Assert.Equal("Active", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM Accounts WHERE Id=$id;", graph.AccountId));
    }

    [Fact]
    public async Task MissingSessionKeepsPendingWithoutInvalidatingPossiblyRefreshedAccount()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var sqlite = new SqliteShareSchedulingStore(graph.Factory);
        await PrepareResultPendingAsync(sqlite, graph);
        var refreshedAt = graph.Now.AddMinutes(5);
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE Accounts SET Status='Active',LastValidatedAt=$validated WHERE Id=$id;";
            command.Parameters.AddWithValue("$validated", refreshedAt.ToString("O"));
            command.Parameters.AddWithValue("$id", graph.AccountId.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        var service = new ShareOperationReconciliationService(
            new RestartReconciliationAdapter([]),
            new MissingSessionStore(),
            sqlite,
            new NoDelay(),
            new ShareReconciliationOptions(1, 10, TimeSpan.Zero, TimeSpan.FromMinutes(1)),
            new FixedTimeProvider(graph.Now.AddMinutes(1)));

        var result = await service.ReconcileAsync(graph.ShareOperationId);

        Assert.Equal(ShareReconciliationDisposition.RequiresUser, result.Disposition);
        Assert.Equal("Active", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM Accounts WHERE Id=$id;", graph.AccountId));
        Assert.Equal(refreshedAt, DateTimeOffset.Parse(await ScalarAsync<string>(graph.Factory,
            "SELECT LastValidatedAt FROM Accounts WHERE Id=$id;", graph.AccountId)));
        Assert.Equal("ResultPending", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
    }

    [Fact]
    public async Task DiscoveryPauseAndDecisionResumeAreAtomicAndStaleSnapshotsWriteNothing()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var store = new SqliteShareSchedulingStore(graph.Factory);
        var accounts = await store.GetDecisionAccountsAsync(graph.BatchId);
        var account = Assert.Single(accounts);
        Assert.Equal(graph.AccountId, account.AccountId);
        var sets = await store.GetDecisionExactSetFingerprintsKeysetPageAsync(
            graph.BatchId, graph.AccountId, null, 1);
        Assert.Equal(graph.ItemSetFingerprint, Assert.Single(sets.Items));
        Assert.True(await store.ContainsDecisionExactSetAsync(
            graph.BatchId, graph.AccountId, graph.ItemSetFingerprint));

        var first = new BatchShareDiscoverySnapshotRecord(
            graph.BatchId, graph.AccountId, graph.SettingsFingerprint, "discovery-one",
            1, 1, 0, "1:operation-generation", graph.Now, 1);
        var firstRun = await StageDiscoveryAsync(store, graph, permanent: true);
        Assert.True(await store.TrySaveDiscoveryAndPauseAsync(firstRun, [first]));
        Assert.Equal("Paused", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
        Assert.Equal(1L, await ScalarAsync<long>(graph.Factory,
            "SELECT LeaseGeneration FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendDiscoveryBaselinePageAsync(
            graph.ShareOperationId, 0, [new(graph.ShareOperationId, "stale-worker-share")]));
        Assert.Equal(0, await CountAsync(graph.Factory, "ShareDiscoveryBaselines"));
        var decision = new BatchShareDecisionSnapshotRecord(
            graph.BatchId, graph.AccountId, graph.SettingsFingerprint, "Reuse",
            first.DiscoveryFingerprint, first.DiscoveredCount, first.CompatibleCount,
            first.IncompatibleCount, first.Generation, graph.Now, first.Version);
        Assert.True(await store.ApplyDecisionSnapshotsAsync([decision]));
        Assert.Equal("WaitingShare", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));

        var second = first with
        {
            DiscoveryFingerprint = "discovery-two", DiscoveredCount = 2,
            CompatibleCount = 2, Version = 2, CompletedAt = graph.Now.AddSeconds(1),
        };
        var secondRun = await StageDiscoveryAsync(store, graph, permanent: true, shareId: "share-two");
        var secondAccountContext = Assert.Single(await store.GetDecisionAccountsAsync(graph.BatchId),
            value => value.AccountId == graph.AccountId);
        await store.StageDiscoveryPageAsync(secondRun, secondAccountContext,
            [new("share-three", new([new("remote-a", "A"), new("remote-b", "B")]),
                "https://pan.baidu.com/s/redacted-two", "c3d4", true, true)]);
        Assert.True(await store.TrySaveDiscoveryAndPauseAsync(secondRun, [second]));
        Assert.False(await store.ApplyDecisionSnapshotsAsync([decision]));
        Assert.Equal("Paused", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
        Assert.Equal(0, await CountAsync(graph.Factory, "BatchShareDecisionSnapshots"));
    }

    [Fact]
    public async Task CancelDecisionRemainsPausedAndIncompatibleReuseIsRejectedWithZeroDecisionWrites()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var store = new SqliteShareSchedulingStore(graph.Factory);
        var snapshot = new BatchShareDiscoverySnapshotRecord(
            graph.BatchId, graph.AccountId, graph.SettingsFingerprint, "only-incompatible",
            1, 0, 1, "1:operation-generation", graph.Now, 1);
        var run = await StageDiscoveryAsync(store, graph, permanent: false);
        Assert.True(await store.TrySaveDiscoveryAndPauseAsync(run, [snapshot]));
        Assert.Equal([graph.BatchId], await store.GetPendingDecisionBatchIdsAsync(10));
        var reuse = new BatchShareDecisionSnapshotRecord(
            graph.BatchId, graph.AccountId, graph.SettingsFingerprint, "Reuse",
            snapshot.DiscoveryFingerprint, 1, 0, 1, snapshot.Generation, graph.Now, 1);
        Assert.False(await store.ApplyDecisionSnapshotsAsync([reuse]));
        Assert.Equal(0, await CountAsync(graph.Factory, "BatchShareDecisionSnapshots"));

        var cancel = reuse with { Decision = "Cancel" };
        Assert.True(await store.ApplyDecisionSnapshotsAsync([cancel]));
        Assert.Equal("Paused", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
        Assert.Empty(await store.GetPendingDecisionBatchIdsAsync(10));

        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE ShareOperations
                SET ExactItemSetFingerprint='new-exact-set',LastErrorCode='AwaitingExistingShareDecision',UpdatedAt=$time
                WHERE Id=$id;
                """;
            command.Parameters.AddWithValue("$id", graph.ShareOperationId.ToString("D"));
            command.Parameters.AddWithValue("$time", graph.Now.AddMinutes(1).ToUniversalTime().ToString("O"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        Assert.Equal([graph.BatchId], await store.GetPendingDecisionBatchIdsAsync(10));

        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE BatchShareDecisionSnapshots SET Decision='Reuse' WHERE BatchJobId=$batch;
                UPDATE ShareOperations SET ExactItemSetFingerprint=$items WHERE Id=$id;
                """;
            command.Parameters.AddWithValue("$batch", graph.BatchId.ToString("D"));
            command.Parameters.AddWithValue("$id", graph.ShareOperationId.ToString("D"));
            command.Parameters.AddWithValue("$items", graph.ItemSetFingerprint);
            Assert.Equal(2, await command.ExecuteNonQueryAsync());
        }

        Assert.Equal([graph.BatchId], await store.GetPendingDecisionBatchIdsAsync(10));
    }

    [Fact]
    public async Task ZeroConflictDiscoveryNeverPausesOrAuthorizesCleanExactWork()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var store = new SqliteShareSchedulingStore(graph.Factory);
        var snapshot = new BatchShareDiscoverySnapshotRecord(
            graph.BatchId, graph.AccountId, graph.SettingsFingerprint, "empty-discovery",
            0, 0, 0, "1:operation-generation", graph.Now, 1);
        var run = Guid.NewGuid();
        var account=Assert.Single(await store.GetDecisionAccountsAsync(graph.BatchId),value=>value.AccountId==graph.AccountId);
        Assert.Equal(1,await store.BeginDiscoveryAsync(run,account));
        await store.StageDiscoveryPageAsync(run,account,[]);

        Assert.True(await store.TrySaveDiscoveryAndPauseAsync(run, [snapshot]));
        Assert.Equal("WaitingShare", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
        var reuse = new BatchShareDecisionSnapshotRecord(
            graph.BatchId, graph.AccountId, graph.SettingsFingerprint, "Reuse",
            snapshot.DiscoveryFingerprint, 0, 0, 0, snapshot.Generation, graph.Now, 1);
        Assert.True(await store.ApplyDecisionSnapshotsAsync([reuse]));
        var context = Assert.IsType<ShareExecutionContext>(await store.LoadAsync(graph.ShareOperationId));
        Assert.Null(context.ExistingShareDecision);
        Assert.Null(context.ConfirmedExistingShare);
    }

    [Fact]
    public async Task PreparingFailureAndRetryAttemptBoundariesAreDurableAndFenced()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var store = new SqliteShareSchedulingStore(graph.Factory);
        var reserved = Assert.IsType<ShareOperationRecord>(
            await store.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2)));
        Assert.True(await store.TryMarkPreparingAsync(
            graph.ShareOperationId, 1, reserved.LeaseGeneration, graph.Now));
        Assert.True(await store.TryFailAsync(
            graph.ShareOperationId, 1, reserved.LeaseGeneration, "InvalidCursor", "safe", graph.Now));
        Assert.Equal("Failed", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;", graph.ShareOperationId));
    }

    [Fact]
    public async Task SubmissionAndEveryRetryRecheckActiveBatchAndShareEnabledGate()
    {
        var disabled=await CreateGraphAsync(includeShareSettings:true);
        var disabledStore=new SqliteShareSchedulingStore(disabled.Factory);
        var disabledLease=Assert.IsType<ShareOperationRecord>(
            await disabledStore.TryReserveNextAsync(disabled.Now,TimeSpan.FromMinutes(2)));
        Assert.True(await disabledStore.TryMarkPreparingAsync(
            disabled.ShareOperationId,1,disabledLease.LeaseGeneration,disabled.Now));
        Assert.True(await disabledStore.TryCompleteDiscoveryBaselineAsync(
            disabled.ShareOperationId,1,disabledLease.LeaseGeneration,"empty",0,disabled.Now));
        await SetShareEnabledAsync(disabled,false);
        Assert.False(await disabledStore.TryMarkSubmittingAsync(
            disabled.ShareOperationId,1,disabledLease.LeaseGeneration,"{}",disabled.Now));

        var paused=await CreateGraphAsync(includeShareSettings:true);
        var pausedStore=new SqliteShareSchedulingStore(paused.Factory);
        var pausedLease=Assert.IsType<ShareOperationRecord>(
            await pausedStore.TryReserveNextAsync(paused.Now,TimeSpan.FromMinutes(2)));
        Assert.True(await pausedStore.TryMarkPreparingAsync(paused.ShareOperationId,1,pausedLease.LeaseGeneration,paused.Now));
        Assert.True(await pausedStore.TryCompleteDiscoveryBaselineAsync(
            paused.ShareOperationId,1,pausedLease.LeaseGeneration,"empty",0,paused.Now));
        Assert.True(await pausedStore.TryMarkSubmittingAsync(
            paused.ShareOperationId,1,pausedLease.LeaseGeneration,"{}",paused.Now));
        Assert.True(await pausedStore.TryRecordDefinitelyNotAcceptedRetryAsync(
            paused.ShareOperationId,1,pausedLease.LeaseGeneration,1,paused.Now));
        await using(var connection=await paused.Factory.OpenConnectionAsync())
        await using(var pause=connection.CreateCommand())
        {
            pause.CommandText="UPDATE BatchJobs SET Status='Paused' WHERE Id=$id;";
            pause.Parameters.AddWithValue("$id",paused.BatchId.ToString("D"));
            Assert.Equal(1,await pause.ExecuteNonQueryAsync());
        }
        Assert.False(await pausedStore.TryBeginSubmissionAttemptAsync(
            paused.ShareOperationId,1,pausedLease.LeaseGeneration,1,paused.Now));
        Assert.Equal("DefinitelyNotAccepted",await ScalarAsync<string>(paused.Factory,
            "SELECT SubmissionAcceptance FROM ShareOperations WHERE Id=$id;",paused.ShareOperationId));
        Assert.Equal(1L,await ScalarAsync<long>(paused.Factory,
            "SELECT AttemptCount FROM ShareOperations WHERE Id=$id;",paused.ShareOperationId));
    }

    [Fact]
    public async Task RestartedDiscoveryRemovesAbandonedSensitiveStagingBeforeContinuing()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var firstStore = new SqliteShareSchedulingStore(graph.Factory);
        var account = Assert.Single(await firstStore.GetDecisionAccountsAsync(graph.BatchId),
            value => value.AccountId == graph.AccountId);
        var exact = new RemoteItemIdentitySet([new("remote-a", "A"), new("remote-b", "B")]);
        var firstRun=Guid.NewGuid();
        Assert.Equal(1,await firstStore.BeginDiscoveryAsync(firstRun,account));
        await firstStore.StageDiscoveryPageAsync(firstRun, account,
            [new("abandoned", exact, "https://pan.baidu.com/s/private", "a1b2", true, true)]);
        Assert.Equal(1, await CountAsync(graph.Factory, "BatchShareDiscoveryStaging"));
        await using (var connection=await graph.Factory.OpenConnectionAsync())
        await using (var expire=connection.CreateCommand())
        {
            expire.CommandText="UPDATE BatchShareDiscoveryRuns SET ExpiresAt='2000-01-01T00:00:00Z';";
            Assert.Equal(1,await expire.ExecuteNonQueryAsync());
        }

        var restarted = new SqliteShareSchedulingStore(graph.Factory);
        var restartedRun=Guid.NewGuid();
        Assert.Equal(1,await restarted.BeginDiscoveryAsync(restartedRun,account));
        await restarted.StageDiscoveryPageAsync(restartedRun, account, []);

        Assert.Equal(0, await CountAsync(graph.Factory, "BatchShareDiscoveryStaging"));
    }

    [Fact]
    public async Task ConcurrentDiscoveryOwnerAndExactSetCasPreventPartialOrStaleAuthorization()
    {
        var graph=await CreateGraphAsync(includeShareSettings:true);
        var store=new SqliteShareSchedulingStore(graph.Factory);
        var account=Assert.Single(await store.GetDecisionAccountsAsync(graph.BatchId),value=>value.AccountId==graph.AccountId);
        var exact=new RemoteItemIdentitySet([new("remote-a","A"),new("remote-b","B")]);
        var firstRun=Guid.NewGuid();
        Assert.Equal(1,await store.BeginDiscoveryAsync(firstRun,account));
        await store.StageDiscoveryPageAsync(firstRun,account,
            [new("one",exact,"https://pan.baidu.com/s/private","a1b2",true,true)]);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.BeginDiscoveryAsync(Guid.NewGuid(),account));
        Assert.Equal(1,await CountAsync(graph.Factory,"BatchShareDiscoveryStaging"));

        await using(var connection=await graph.Factory.OpenConnectionAsync())
        await using(var bump=connection.CreateCommand())
        {
            bump.CommandText="UPDATE ShareOperations SET Status='Failed' WHERE Id=$id;";
            bump.Parameters.AddWithValue("$id",graph.ShareOperationId.ToString("D"));
            Assert.Equal(1,await bump.ExecuteNonQueryAsync());
        }
        var stale=new BatchShareDiscoverySnapshotRecord(graph.BatchId,graph.AccountId,graph.SettingsFingerprint,
            "stale",1,1,0,"1:exact-hash",graph.Now,1);
        Assert.False(await store.TrySaveDiscoveryAndPauseAsync(firstRun,[stale]));
        Assert.Equal(0,await CountAsync(graph.Factory,"BatchShareDiscoverySnapshots"));
        Assert.Equal("Failed",await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;",graph.ShareOperationId));
    }

    [Fact]
    public async Task CapabilityAcceptanceRequiresExactVersionKeyAndPersistsOnlyNonsecretEvidence()
    {
        var graph = await CreateGraphAsync(includeShareSettings: false);
        var store = new SqliteAdapterCapabilityStore(graph.Factory);
        var acceptance = new AdapterCapabilityAcceptanceRecord(
            "BaiduShare", "2026-08", "selectors-1", "8.0.0", graph.Now, new string('A', 64));

        await store.AcceptAsync(acceptance);

        Assert.Equal(acceptance, await store.GetAsync("BaiduShare", "2026-08", "selectors-1", "8.0.0"));
        Assert.Null(await store.GetAsync("BaiduShare", "2026-08", "selectors-2", "8.0.0"));
        await using var connection = await graph.Factory.OpenConnectionAsync();
        var columns = await ColumnsAsync(connection, "AdapterCapabilityAcceptances");
        Assert.Equal(
            new[] { "AdapterKind", "ProtocolVersion", "SelectorVersion", "AdapterBuildVersion", "AcceptedAt", "EvidenceHash" },
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

    private async Task<Graph> CreateGraphAsync(
        bool includeShareSettings,
        string batchStatus = "Active",
        bool managedShare = false,
        bool acceptanceOnly = false)
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, $"{Guid.NewGuid():N}.db"));
        await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
        var now = new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);
        var account = AccountRecord.Create(Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffff1"), $"user-{Guid.NewGuid():N}", "Account");
        var secondAccount = AccountRecord.Create(Guid.Parse("00000000-0000-0000-0000-000000000002"), $"user-{Guid.NewGuid():N}", "Second Account");
        var batch = new BatchJobRecord(Guid.NewGuid(), "Batch", "Excel", "Distribution", "TaskCount", "ProportionalReduction",
            1, 1, "/", "None", "Skip", "Immediate", batchStatus, now,
            batchStatus == "Active" ? now : null, null, null, 1,
            ShareOperationCount: acceptanceOnly ? 1 : 4, AcceptanceOnly: acceptanceOnly);
        var resource = new ResourceItemRecord(Guid.NewGuid(), batch.Id, null, 1, "source-url", "source-id", null, null,
            null, null, null, "None", "Valid", null, "Expanded", null, null, now, now, "Resolved title");
        var shareId = Guid.NewGuid();
        var settingsFingerprint = ShareSettingsFingerprint.Compute(
            true, true, ShareExtractionCodeBehavior.Random, null);
        var exactGraphItems = acceptanceOnly
            ? new RemoteItemIdentitySet([new("remote-a", "A")])
            : new RemoteItemIdentitySet([new("remote-a", "A"), new("remote-b", "B")]);
        var itemSetFingerprint = exactGraphItems.Fingerprint;
        ExecutionProfileSnapshot? managedExecutionProfile = null;
        if (managedShare)
        {
            await using (var accountUnit = await SqliteUnitOfWork.BeginAsync(factory))
            {
                await accountUnit.Accounts.UpsertAsync(account);
                await accountUnit.Accounts.UpsertAsync(secondAccount);
                await accountUnit.CommitAsync();
            }
            var state = new SqliteManagedBrowserStateStore(factory);
            var directory = Path.GetFullPath(Path.Combine(root, $"managed-share-chrome-{Guid.NewGuid():N}"));
            var installation = new BrowserInstallationIdentity(
                Guid.NewGuid(), BrowserFamily.Chrome, directory, Path.Combine(directory, "chrome.exe"),
                "Google LLC", "Google Chrome", $"managed-share-file-{Guid.NewGuid():N}");
            var storedInstallation = await state.CreateInstallationAsync(installation, default);
            Assert.Equal(ManagedBrowserTransitionStatus.Applied,
                (await state.TryReplaceActiveInstallationAsync(
                    new ManagedBrowserInstallationActivationRequest(null, storedInstallation), default)).Status);
            var session = new ManagedBrowserSessionReference(
                Guid.NewGuid(), installation.InstallationId, account.Id, account.BaiduUserId,
                BrowserFamily.Chrome, $"managed-share-profile-{Guid.NewGuid():N}",
                $"managed-share-ownership-{Guid.NewGuid():N}");
            var storedSession = await state.CreateSessionAsync(session, default);
            Assert.Equal(ManagedBrowserTransitionStatus.Applied,
                (await state.TryReplaceActiveSessionAsync(
                    new ManagedBrowserSessionActivationRequest(null, storedSession), default)).Status);
            managedExecutionProfile = ExecutionProfileSnapshot.Managed(
                BrowserFamily.Chrome, session.ProfileId, installation.InstallationId, session.SessionId);
        }
        if (acceptanceOnly && managedExecutionProfile is null)
            throw new InvalidOperationException("AcceptanceOnly test graphs require a managed snapshot.");
        var transfer = new TransferOperationRecord(Guid.NewGuid(), resource.Id, batch.Id, 1, 1, "/", "/", "", account.Id,
            "Rule", "Succeeded", "NewTransfer", 1, "transfer", "{}", "request",
            acceptanceOnly ? "[\"remote-a\"]" : "[\"remote-a\",\"remote-b\"]",
            "ResultReceived", "Fulfilled", now, now, now, null, null, null, now, now,
            managedExecutionProfile, acceptanceOnly);
        var share = new ShareOperationRecord(shareId, transfer.Id, resource.Id, batch.Id, account.Id, 1,
            acceptanceOnly ? "WaitingTransfer" : "WaitingShare", itemSetFingerprint, settingsFingerprint,
            1, null, null, "Unknown", 0, null, null, null, null, null,
            now, now, 0, managedExecutionProfile);

        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            if (!managedShare)
            {
                await unit.Accounts.UpsertAsync(account);
                await unit.Accounts.UpsertAsync(secondAccount);
            }
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
                if (!acceptanceOnly)
                    await unit.Jobs.AddShareOperationsAsync([share], [
                        new(shareId, 1, "remote-b", "B"),
                        new(shareId, 0, "remote-a", "A")]);
            }
            await unit.CommitAsync();
        }
        if (acceptanceOnly)
        {
            var acceptanceProfile = managedExecutionProfile!;
            await using var connection = await factory.OpenConnectionAsync();
            await using var transaction = connection.BeginTransaction();
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO ShareOperations(
                      Id,TransferOperationId,ResourceItemId,BatchJobId,AccountId,OutputOrdinal,Status,
                      ExactItemSetFingerprint,SettingsFingerprint,Generation,SubmissionAcceptance,AttemptCount,
                      CreatedAt,UpdatedAt,LeaseGeneration,ExecutionEngineVersion,BrowserFamily,ProfileId,
                      InstallationId,SessionId)
                    VALUES($id,$transfer,$resource,$batch,$account,1,'WaitingTransfer',NULL,$settings,1,
                      'Unknown',0,$now,$now,0,$engine,$family,$profile,$installation,$session);
                    """;
                insert.Parameters.AddWithValue("$id", shareId.ToString("D"));
                insert.Parameters.AddWithValue("$transfer", transfer.Id.ToString("D"));
                insert.Parameters.AddWithValue("$resource", resource.Id.ToString("D"));
                insert.Parameters.AddWithValue("$batch", batch.Id.ToString("D"));
                insert.Parameters.AddWithValue("$account", account.Id.ToString("D"));
                insert.Parameters.AddWithValue("$settings", settingsFingerprint);
                insert.Parameters.AddWithValue("$now", now.ToString("O"));
                insert.Parameters.AddWithValue("$engine", "ManagedChromium-v1");
                insert.Parameters.AddWithValue("$family", acceptanceProfile.BrowserFamily.ToString());
                insert.Parameters.AddWithValue("$profile", acceptanceProfile.ProfileId);
                insert.Parameters.AddWithValue("$installation", acceptanceProfile.InstallationId!.Value.ToString("D"));
                insert.Parameters.AddWithValue("$session", acceptanceProfile.SessionId!.Value.ToString("D"));
                Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            }
            await using (var handoff = connection.CreateCommand())
            {
                handoff.Transaction = transaction;
                handoff.CommandText = "UPDATE ShareOperations SET Status='WaitingShare',ExactItemSetFingerprint=$items WHERE Id=$id AND Status='WaitingTransfer';";
                handoff.Parameters.AddWithValue("$items", itemSetFingerprint);
                handoff.Parameters.AddWithValue("$id", shareId.ToString("D"));
                Assert.Equal(1, await handoff.ExecuteNonQueryAsync());
            }
            await using (var generation = connection.CreateCommand())
            {
                generation.Transaction = transaction;
                generation.CommandText = """
                    INSERT INTO ShareOperationGenerations(
                      ShareOperationId,Generation,BatchJobId,ResourceItemId,AccountId,
                      ExactItemSetFingerprint,SettingsFingerprint,ExactItemCount)
                    VALUES($id,1,$batch,$resource,$account,$items,$settings,NULL);
                    """;
                generation.Parameters.AddWithValue("$id", shareId.ToString("D"));
                generation.Parameters.AddWithValue("$batch", batch.Id.ToString("D"));
                generation.Parameters.AddWithValue("$resource", resource.Id.ToString("D"));
                generation.Parameters.AddWithValue("$account", account.Id.ToString("D"));
                generation.Parameters.AddWithValue("$items", itemSetFingerprint);
                generation.Parameters.AddWithValue("$settings", settingsFingerprint);
                Assert.Equal(1, await generation.ExecuteNonQueryAsync());
            }
            var acceptanceItems = new[] { (0, "remote-a", "A") };
            foreach (var (ordinal, remote, name) in acceptanceItems)
            {
                await using var item = connection.CreateCommand();
                item.Transaction = transaction;
                item.CommandText = "INSERT INTO ShareOperationRemoteItems(ShareOperationId,ItemOrdinal,RemoteItemId,CanonicalSortKey,DisplayName) VALUES($id,$ordinal,$remote,$sort,$name);";
                item.Parameters.AddWithValue("$id", shareId.ToString("D"));
                item.Parameters.AddWithValue("$ordinal", ordinal);
                item.Parameters.AddWithValue("$remote", remote);
                item.Parameters.AddWithValue("$sort", RemoteItemIdentitySet.GetCanonicalSortKey(remote));
                item.Parameters.AddWithValue("$name", name);
                Assert.Equal(1, await item.ExecuteNonQueryAsync());
            }
            await using (var seal = connection.CreateCommand())
            {
                seal.Transaction = transaction;
                seal.CommandText = "UPDATE ShareOperationGenerations SET ExactItemCount=1 WHERE ShareOperationId=$id AND Generation=1;";
                seal.Parameters.AddWithValue("$id", shareId.ToString("D"));
                Assert.Equal(1, await seal.ExecuteNonQueryAsync());
            }
            await using (var reserve = connection.CreateCommand())
            {
                reserve.Transaction = transaction;
                reserve.CommandText = "UPDATE ShareOperations SET Status='Reserved',LeaseGeneration=1,LeaseExpiresAt=$lease WHERE Id=$id;";
                reserve.Parameters.AddWithValue("$lease", now.AddMinutes(2).ToString("O"));
                reserve.Parameters.AddWithValue("$id", shareId.ToString("D"));
                Assert.Equal(1, await reserve.ExecuteNonQueryAsync());
            }
            await transaction.CommitAsync();
        }
        if (!includeShareSettings)
            await SeedOrphanShareOperationAsync(factory, share);
        return new(factory, now, account.Id, secondAccount.Id, batch.Id, resource.Id, transfer.Id, shareId,
            itemSetFingerprint, settingsFingerprint,
            managedExecutionProfile ?? ExecutionProfileSnapshot.LegacyForAccount(account.Id));
    }

    private static async Task<Guid> StageDiscoveryAsync(
        SqliteShareSchedulingStore store,
        Graph graph,
        bool permanent,
        string shareId = "share-one")
    {
        var account = Assert.Single(await store.GetDecisionAccountsAsync(graph.BatchId),
            value => value.AccountId == graph.AccountId);
        var run = Guid.NewGuid();
        var exact = new RemoteItemIdentitySet([new("remote-a", "A"), new("remote-b", "B")]);
        await store.BeginDiscoveryAsync(run,account);
        await store.StageDiscoveryPageAsync(run, account,
            [new(shareId, exact, "https://pan.baidu.com/s/redacted", "a1b2", permanent, true)]);
        return run;
    }

    private static async Task PrepareResultPendingAsync(SqliteShareSchedulingStore store, Graph graph)
    {
        var lease = Assert.IsType<ShareOperationRecord>(
            await store.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2)));
        Assert.True(await store.TryCompleteDiscoveryBaselineAsync(
            graph.ShareOperationId, 1, lease.LeaseGeneration, "empty", 0, graph.Now));
        Assert.True(await store.TryMarkSubmittingAsync(
            graph.ShareOperationId, 1, lease.LeaseGeneration, "{\"safe\":true}", graph.Now));
        Assert.True(await store.TryMarkResultPendingAsync(
            graph.ShareOperationId, 1, lease.LeaseGeneration, graph.Now, graph.Now));
    }

    private sealed class RestartReconciliationAdapter(IReadOnlyList<BaiduShareRecord> shares) : IBaiduShareAdapter
    {
        public int CreateCalls { get; private set; }

        public Task<BaiduAdapterResult<BaiduSharePage>> ListSharesAsync(
            AccountSession session,
            string? cursor,
            CancellationToken cancellationToken) =>
            Task.FromResult(BaiduAdapterResult<BaiduSharePage>.Success(new(shares, null)));

        public Task<BaiduAdapterResult<ShareCreationReceipt>> CreatePermanentShareAsync(
            AccountSession session,
            PermanentShareRequest request,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            throw new InvalidOperationException("Reconciliation must not create a share.");
        }
    }

    private sealed class SequencedReconciliationAdapter(
        IEnumerable<BaiduAdapterResult<BaiduSharePage>> responses) : IBaiduShareAdapter
    {
        private readonly Queue<BaiduAdapterResult<BaiduSharePage>> responses = new(responses);
        public int CreateCalls { get; private set; }

        public Task<BaiduAdapterResult<BaiduSharePage>> ListSharesAsync(
            AccountSession session,
            string? cursor,
            CancellationToken cancellationToken) => Task.FromResult(responses.Dequeue());

        public Task<BaiduAdapterResult<ShareCreationReceipt>> CreatePermanentShareAsync(
            AccountSession session,
            PermanentShareRequest request,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            throw new InvalidOperationException("Reconciliation must not create a share.");
        }
    }

    private sealed class ConcurrentSqliteReconciliationAdapter(BaiduShareRecord candidate) : IBaiduShareAdapter
    {
        private readonly TaskCompletionSource bothEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int concurrentListings;
        public int MaximumConcurrentListings { get; private set; }

        public async Task<BaiduAdapterResult<BaiduSharePage>> ListSharesAsync(
            AccountSession session,
            string? cursor,
            CancellationToken cancellationToken)
        {
            var concurrent = Interlocked.Increment(ref concurrentListings);
            MaximumConcurrentListings = Math.Max(MaximumConcurrentListings, concurrent);
            if (concurrent == 2) bothEntered.TrySetResult();
            try
            {
                await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
                return BaiduAdapterResult<BaiduSharePage>.Success(new([candidate], null));
            }
            finally
            {
                Interlocked.Decrement(ref concurrentListings);
            }
        }

        public Task<BaiduAdapterResult<ShareCreationReceipt>> CreatePermanentShareAsync(
            AccountSession session,
            PermanentShareRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Reconciliation must not create a share.");
    }

    private sealed class OrderedOutcomeAdapter(BaiduShareRecord? candidate, bool bothFailures) : IBaiduShareAdapter
    {
        private int calls;

        public Task<BaiduAdapterResult<BaiduSharePage>> ListSharesAsync(
            AccountSession session,
            string? cursor,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref calls);
            return Task.FromResult(bothFailures || call == 1
                ? BaiduAdapterResult<BaiduSharePage>.Failure(BaiduErrorKind.ExpiredLogin, "private")
                : BaiduAdapterResult<BaiduSharePage>.Success(new([candidate!], null)));
        }

        public Task<BaiduAdapterResult<ShareCreationReceipt>> CreatePermanentShareAsync(
            AccountSession session,
            PermanentShareRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Reconciliation must not create a share.");
    }

    private sealed class KeepFirstReconciliationStore(SqliteShareSchedulingStore inner) : IShareReconciliationStore
    {
        private readonly TaskCompletionSource bothLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource firstKeepCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int loadCalls;
        private int keepCalls;

        public async Task<ShareExecutionContext?> LoadPendingAsync(
            Guid operationId,
            CancellationToken cancellationToken = default)
        {
            var context = await inner.LoadPendingAsync(operationId, cancellationToken);
            if (Interlocked.Increment(ref loadCalls) == 2) bothLoaded.TrySetResult();
            await bothLoaded.Task.WaitAsync(cancellationToken);
            return context;
        }

        public Task<StringKeysetPage<ShareDiscoveryBaselineRecord>> GetBaselinePageAsync(
            Guid operationId,
            string? afterShareId,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            inner.GetBaselinePageAsync(operationId, afterShareId, pageSize, cancellationToken);

        public async Task<bool> TryAcceptAsync(
            Guid operationId,
            int generation,
            long leaseGeneration,
            ShareReconciliationWriteSnapshot expected,
            BaiduShareResultRecord result,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            await firstKeepCompleted.Task.WaitAsync(cancellationToken);
            return await inner.TryAcceptAsync(
                operationId, generation, leaseGeneration, expected, result, updatedAt, cancellationToken);
        }

        public async Task<bool> TryKeepPendingAsync(
            Guid operationId,
            int generation,
            long leaseGeneration,
            ShareReconciliationWriteSnapshot expected,
            ShareReconciliationIssue issue,
            DateTimeOffset? observedSessionCapturedAtUtc,
            DateTimeOffset nextReconcileAt,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref keepCalls);
            if (call > 1) await firstKeepCompleted.Task.WaitAsync(cancellationToken);
            var changed = await inner.TryKeepPendingAsync(
                operationId, generation, leaseGeneration, expected, issue, observedSessionCapturedAtUtc,
                nextReconcileAt, updatedAt, cancellationToken);
            if (call == 1) firstKeepCompleted.TrySetResult();
            return changed;
        }

        public Task<bool> TryFailDefinitelyNotAcceptedAsync(
            Guid operationId, int generation, long leaseGeneration,
            ShareReconciliationFailureSnapshot expected, DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) =>
            inner.TryFailDefinitelyNotAcceptedAsync(
                operationId, generation, leaseGeneration, expected, updatedAt, cancellationToken);
    }

    private sealed class RefreshAccountBeforeKeepStore(
        SqliteShareSchedulingStore inner,
        SqliteConnectionFactory factory,
        Guid accountId,
        DateTimeOffset refreshedAt) : IShareReconciliationStore
    {
        public Task<ShareExecutionContext?> LoadPendingAsync(
            Guid operationId,
            CancellationToken cancellationToken = default) => inner.LoadPendingAsync(operationId, cancellationToken);

        public Task<StringKeysetPage<ShareDiscoveryBaselineRecord>> GetBaselinePageAsync(
            Guid operationId,
            string? afterShareId,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            inner.GetBaselinePageAsync(operationId, afterShareId, pageSize, cancellationToken);

        public Task<bool> TryAcceptAsync(
            Guid operationId,
            int generation,
            long leaseGeneration,
            ShareReconciliationWriteSnapshot expected,
            BaiduShareResultRecord result,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) =>
            inner.TryAcceptAsync(
                operationId, generation, leaseGeneration, expected, result, updatedAt, cancellationToken);

        public async Task<bool> TryKeepPendingAsync(
            Guid operationId,
            int generation,
            long leaseGeneration,
            ShareReconciliationWriteSnapshot expected,
            ShareReconciliationIssue issue,
            DateTimeOffset? observedSessionCapturedAtUtc,
            DateTimeOffset nextReconcileAt,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            await using (var connection = await factory.OpenConnectionAsync(cancellationToken))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE Accounts SET Status='Active',LastValidatedAt=$validated WHERE Id=$id;";
                command.Parameters.AddWithValue("$validated", refreshedAt.ToString("O"));
                command.Parameters.AddWithValue("$id", accountId.ToString("D"));
                Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
            }
            return await inner.TryKeepPendingAsync(
                operationId, generation, leaseGeneration, expected, issue, observedSessionCapturedAtUtc,
                nextReconcileAt, updatedAt, cancellationToken);
        }

        public Task<bool> TryFailDefinitelyNotAcceptedAsync(
            Guid operationId, int generation, long leaseGeneration,
            ShareReconciliationFailureSnapshot expected, DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) =>
            inner.TryFailDefinitelyNotAcceptedAsync(
                operationId, generation, leaseGeneration, expected, updatedAt, cancellationToken);
    }

    private sealed class AcceptFirstReconciliationStore(SqliteShareSchedulingStore inner) : IShareReconciliationStore
    {
        private readonly TaskCompletionSource bothLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource acceptCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int loadCalls;

        public async Task<ShareExecutionContext?> LoadPendingAsync(
            Guid operationId,
            CancellationToken cancellationToken = default)
        {
            var context = await inner.LoadPendingAsync(operationId, cancellationToken);
            if (Interlocked.Increment(ref loadCalls) == 2) bothLoaded.TrySetResult();
            await bothLoaded.Task.WaitAsync(cancellationToken);
            return context;
        }

        public Task<StringKeysetPage<ShareDiscoveryBaselineRecord>> GetBaselinePageAsync(
            Guid operationId, string? afterShareId, int pageSize, CancellationToken cancellationToken = default) =>
            inner.GetBaselinePageAsync(operationId, afterShareId, pageSize, cancellationToken);

        public async Task<bool> TryAcceptAsync(
            Guid operationId, int generation, long leaseGeneration, ShareReconciliationWriteSnapshot expected,
            BaiduShareResultRecord result, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            var changed = await inner.TryAcceptAsync(
                operationId, generation, leaseGeneration, expected, result, updatedAt, cancellationToken);
            acceptCompleted.TrySetResult();
            return changed;
        }

        public async Task<bool> TryKeepPendingAsync(
            Guid operationId, int generation, long leaseGeneration, ShareReconciliationWriteSnapshot expected,
            ShareReconciliationIssue issue, DateTimeOffset? observedSessionCapturedAtUtc,
            DateTimeOffset nextReconcileAt, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            await acceptCompleted.Task.WaitAsync(cancellationToken);
            return await inner.TryKeepPendingAsync(
                operationId, generation, leaseGeneration, expected, issue, observedSessionCapturedAtUtc,
                nextReconcileAt, updatedAt, cancellationToken);
        }

        public Task<bool> TryFailDefinitelyNotAcceptedAsync(
            Guid operationId, int generation, long leaseGeneration,
            ShareReconciliationFailureSnapshot expected, DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) =>
            inner.TryFailDefinitelyNotAcceptedAsync(
                operationId, generation, leaseGeneration, expected, updatedAt, cancellationToken);
    }

    private sealed class RestartReconciliationSessionStore(Guid accountId, DateTimeOffset now) : IBaiduSessionStore
    {
        private readonly AccountSession session = new(accountId, "baidu-user", [
            new SessionCookie("BDUSS", "secret", ".baidu.com", "/", null, true, true)], now);

        public Task<AccountSession?> LoadAsync(string accountId, CancellationToken cancellationToken) =>
            Task.FromResult<AccountSession?>(session);

        public Task SaveAsync(AccountSession session, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveAsync(string accountId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SequencedCapturedSessionStore(
        Guid accountId,
        IEnumerable<DateTimeOffset> capturedAtValues) : IBaiduSessionStore
    {
        private readonly Queue<DateTimeOffset> capturedAtValues = new(capturedAtValues);

        public Task<AccountSession?> LoadAsync(string requestedAccountId, CancellationToken cancellationToken) =>
            Task.FromResult<AccountSession?>(new(
                accountId,
                "baidu-user",
                [new SessionCookie("BDUSS", "secret", ".baidu.com", "/", null, true, true)],
                capturedAtValues.Dequeue()));

        public Task SaveAsync(AccountSession session, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveAsync(string accountId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class MissingSessionStore : IBaiduSessionStore
    {
        public Task<AccountSession?> LoadAsync(string accountId, CancellationToken cancellationToken) =>
            Task.FromResult<AccountSession?>(null);

        public Task SaveAsync(AccountSession session, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveAsync(string accountId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoDelay : IAsyncDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static async Task SetShareEnabledAsync(Graph graph,bool enabled)
    {
        await using var connection=await graph.Factory.OpenConnectionAsync();
        await using var command=connection.CreateCommand();
        command.CommandText="UPDATE BatchOutputAccounts SET ShareEnabled=$enabled,PlannedShareCount=$planned,CreatedShareOperationCount=$created WHERE BatchJobId=$batch AND AccountId=$account;";
        command.Parameters.AddWithValue("$enabled",enabled?1:0);
        command.Parameters.AddWithValue("$planned",enabled?3:0);
        command.Parameters.AddWithValue("$created",enabled?1:0);
        command.Parameters.AddWithValue("$batch",graph.BatchId.ToString("D"));
        command.Parameters.AddWithValue("$account",graph.AccountId.ToString("D"));
        Assert.Equal(1,await command.ExecuteNonQueryAsync());
    }

    private static async Task SeedOrphanShareOperationAsync(SqliteConnectionFactory factory, ShareOperationRecord share)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = factory.DatabasePath, Pooling = false, ForeignKeys = false };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ShareOperations(Id,TransferOperationId,ResourceItemId,BatchJobId,AccountId,OutputOrdinal,Status,
              ExactItemSetFingerprint,SettingsFingerprint,Generation,SubmissionAcceptance,AttemptCount,CreatedAt,UpdatedAt,
              ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES($id,$transfer,$resource,$batch,$account,$ordinal,$status,$items,$settings,$generation,'Unknown',0,$created,$updated,
              $engine,$family,$profile,$installation,$session);
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
        command.Parameters.AddWithValue("$engine", share.ExecutionProfile.Engine == BaiduExecutionEngine.WebView2V1
            ? "WebView2-v1" : "ManagedChromium-v1");
        command.Parameters.AddWithValue("$family", share.ExecutionProfile.BrowserFamily.ToString());
        command.Parameters.AddWithValue("$profile", share.ExecutionProfile.ProfileId);
        command.Parameters.AddWithValue("$installation", (object?)share.ExecutionProfile.InstallationId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$session", (object?)share.ExecutionProfile.SessionId?.ToString("D") ?? DBNull.Value);
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
                  ExactItemSetFingerprint,SettingsFingerprint,Generation,SubmissionAcceptance,AttemptCount,CreatedAt,UpdatedAt,
                  ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
                VALUES($id,$transfer,$resource,$batch,$account,1,'WaitingShare','unsealed-items',$settings,1,'Unknown',0,$now,$now,
                  'WebView2-v1','LegacyWebView2',$profile,NULL,NULL);
                """;
            operation.Parameters.AddWithValue("$id", shareId.ToString("D"));
            operation.Parameters.AddWithValue("$transfer", transferId.ToString("D"));
            operation.Parameters.AddWithValue("$resource", graph.ResourceId.ToString("D"));
            operation.Parameters.AddWithValue("$batch", graph.BatchId.ToString("D"));
            operation.Parameters.AddWithValue("$account", graph.AccountId.ToString("D"));
            operation.Parameters.AddWithValue("$settings", graph.SettingsFingerprint);
            operation.Parameters.AddWithValue("$now", graph.Now.ToString("O"));
            operation.Parameters.AddWithValue("$profile", ExecutionProfileSnapshot.LegacyForAccount(graph.AccountId).ProfileId);
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
            "BatchShareDiscoverySnapshots" => "SELECT COUNT(*) FROM BatchShareDiscoverySnapshots;",
            "BatchShareDiscoveryStaging" => "SELECT COUNT(*) FROM BatchShareDiscoveryStaging;",
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

    private static BaiduShareResultRecord AcceptedResult(Graph graph) => new(
        Guid.Parse("40000000-0000-0000-0000-000000000004"),
        graph.ShareOperationId,
        graph.BatchId,
        graph.ResourceId,
        graph.AccountId,
        graph.ItemSetFingerprint,
        graph.SettingsFingerprint,
        1,
        "new-share",
        "https://pan.baidu.com/s/redacted",
        "a1b2",
        true,
        true,
        graph.Now,
        graph.Now);

    private static async Task<T> ScalarAsync<T>(SqliteConnectionFactory factory, string sql, Guid id)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        var value = await command.ExecuteScalarAsync() ?? throw new InvalidDataException("The scalar query returned null.");
        return (T)Convert.ChangeType(value, typeof(T));
    }

    private static async Task<object?> ScalarNullableAsync(SqliteConnectionFactory factory, string sql, Guid id)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
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
        Guid ResourceId, Guid TransferId, Guid ShareOperationId, string ItemSetFingerprint, string SettingsFingerprint,
        ExecutionProfileSnapshot ShareExecutionProfile);
}
