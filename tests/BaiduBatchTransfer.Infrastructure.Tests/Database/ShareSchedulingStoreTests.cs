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

    [Fact]
    public async Task ExpiredPreSubmissionReservationCanBeRecoveredWithoutCreatingDuplicateWork()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var store = new SqliteShareSchedulingStore(graph.Factory);
        Assert.NotNull(await store.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2)));

        Assert.Null(await store.TryReserveNextAsync(graph.Now.AddMinutes(1), TimeSpan.FromMinutes(2)));
        var recovered = await store.TryReserveNextAsync(graph.Now.AddMinutes(3), TimeSpan.FromMinutes(2));

        Assert.NotNull(recovered);
        Assert.Equal(graph.ShareOperationId, recovered.Id);
        Assert.Equal(graph.Now.AddMinutes(5), recovered.LeaseExpiresAt);
    }

    [Fact]
    public async Task ShareCreationRejectsMissingOrMismatchedExactItemsAndWrongOutputOrdinal()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var template = new ShareOperationRecord(Guid.NewGuid(), graph.TransferId, graph.ResourceId, graph.BatchId,
            graph.AccountId, 1, "WaitingShare", "wrong-fingerprint", 1, null, null, "Unknown", 0,
            null, null, null, null, null, graph.Now, graph.Now);

        await using (var unit = await SqliteUnitOfWork.BeginAsync(graph.Factory))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => unit.Jobs.AddShareOperationsAsync([template], []));
            await Assert.ThrowsAsync<ArgumentException>(() => unit.Jobs.AddShareOperationsAsync(
                [template], [new(template.Id, 0, "remote-a", "A")]));
        }

        var secondTransfer = new TransferOperationRecord(Guid.NewGuid(), graph.ResourceId, graph.BatchId, 2, 2,
            "/two", "/two", "", graph.AccountId, "Rule", "Cancelled", "Cancelled", 0, "transfer-two", null,
            null, null, "NotPrepared", "Released", null, null, null, null, null, null, graph.Now, graph.Now);
        var operationId = Guid.NewGuid();
        var fingerprint = new RemoteItemIdentitySet([new("remote-c", "C")]).Fingerprint;
        await using (var unit = await SqliteUnitOfWork.BeginAsync(graph.Factory))
        {
            await unit.Jobs.AddOperationsAsync([secondTransfer]);
            await Assert.ThrowsAsync<SqliteException>(() => unit.Jobs.AddShareOperationsAsync(
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
        Assert.NotNull(await store.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2)));
        await store.AppendDiscoveryBaselinePageAsync(graph.ShareOperationId, [new(graph.ShareOperationId, "old-share-1")]);

        Assert.False(await store.TryCompleteDiscoveryBaselineAsync(
            graph.ShareOperationId, "Reserved", 1, "baseline-fingerprint", 2, graph.Now.AddSeconds(1)));
        Assert.False((await store.GetDiscoveryBaselineStateAsync(graph.ShareOperationId))!.IsComplete);

        await store.AppendDiscoveryBaselinePageAsync(graph.ShareOperationId, [new(graph.ShareOperationId, "old-share-2")]);
        Assert.True(await store.TryCompleteDiscoveryBaselineAsync(
            graph.ShareOperationId, "Reserved", 1, "baseline-fingerprint", 2, graph.Now.AddSeconds(2)));
        await store.AppendDiscoveryBaselinePageAsync(graph.ShareOperationId, [new(graph.ShareOperationId, "old-share-1")]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendDiscoveryBaselinePageAsync(
            graph.ShareOperationId, [new(graph.ShareOperationId, "late-share")]));
        Assert.Equal(
            new ShareDiscoveryBaselineStateRecord(graph.ShareOperationId, 1, "baseline-fingerprint", 2, true, graph.Now.AddSeconds(2)),
            await store.GetDiscoveryBaselineStateAsync(graph.ShareOperationId));
    }

    [Fact]
    public async Task ShareWorkRoundTripsExactItemsBaselinesDecisionAcceptedResultAndKeysetPages()
    {
        var graph = await CreateGraphAsync(includeShareSettings: true);
        var store = new SqliteShareSchedulingStore(graph.Factory);

        var reserved = await store.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2));
        Assert.NotNull(reserved);
        Assert.Equal(graph.ShareOperationId, reserved.Id);
        Assert.Equal("Reserved", reserved.Status);
        Assert.Equal(graph.Now.AddMinutes(2), reserved.LeaseExpiresAt);

        var exact = await store.GetRemoteItemsKeysetPageAsync(graph.ShareOperationId, null, 1);
        Assert.Equal("remote-a", Assert.Single(exact.Items).RemoteItemId);
        Assert.Equal("remote-a", exact.NextCursor);
        var exactTail = await store.GetRemoteItemsKeysetPageAsync(graph.ShareOperationId, exact.NextCursor, 1);
        Assert.Equal("remote-b", Assert.Single(exactTail.Items).RemoteItemId);
        Assert.Null(exactTail.NextCursor);

        await store.AppendDiscoveryBaselinePageAsync(graph.ShareOperationId, [
            new(graph.ShareOperationId, "share-2"),
            new(graph.ShareOperationId, "share-1")]);
        var baseline = await store.GetDiscoveryBaselineKeysetPageAsync(graph.ShareOperationId, null, 1);
        Assert.Equal("share-1", Assert.Single(baseline.Items).ShareId);
        Assert.Equal("share-1", baseline.NextCursor);
        Assert.True(await store.TryCompleteDiscoveryBaselineAsync(
            graph.ShareOperationId, "Reserved", 1, "baseline-complete", 2, graph.Now));

        var decision = new BatchShareDecisionSnapshotRecord(
            graph.BatchId, graph.AccountId, "Regenerate", "discovery-fingerprint", 4, 1, 2,
            "generation-token", graph.Now);
        await store.SaveDecisionSnapshotAsync(decision);
        Assert.Equal(decision, await store.GetDecisionSnapshotAsync(graph.BatchId, graph.AccountId));
        await Assert.ThrowsAsync<SqliteException>(() => store.SaveDecisionSnapshotAsync(
            decision with { DiscoveryFingerprint = "stale", Generation = "stale-generation" }));
        Assert.Equal(decision, await store.GetDecisionSnapshotAsync(graph.BatchId, graph.AccountId));

        var result = new BaiduShareResultRecord(
            Guid.NewGuid(), graph.ShareOperationId, graph.BatchId, graph.ResourceId, graph.AccountId,
            graph.ItemSetFingerprint, "settings", 1, "share-new", "https://pan.baidu.com/s/private", "abcd",
            true, true, graph.Now, graph.Now);
        Assert.False(await store.AcceptResultAsync(graph.ShareOperationId, "Preparing",
            result with { AccountId = Guid.NewGuid() }, graph.Now));
        Assert.False(await store.AcceptResultAsync(graph.ShareOperationId, "Preparing",
            result with { SettingsFingerprint = "wrong-settings" }, graph.Now));
        Assert.False(await store.AcceptResultAsync(graph.ShareOperationId, "Preparing",
            result with { ShareId = "share-1" }, graph.Now));
        Assert.True(await store.TryMarkSubmittingAsync(
            graph.ShareOperationId, 1, "{\"safe\":true}", graph.Now));
        Assert.True(await store.TryRecordAdapterRequestAsync(
            graph.ShareOperationId, 1, "adapter-request", graph.Now));
        Assert.False(await store.TryRecordAdapterRequestAsync(
            graph.ShareOperationId, 1, "different-request", graph.Now));
        Assert.True(await store.TryMarkResultPendingAsync(
            graph.ShareOperationId, 1, graph.Now.AddMinutes(1), graph.Now));
        Assert.True(await store.AcceptResultAsync(graph.ShareOperationId, "ResultPending", result, graph.Now));
        Assert.Equal(result, await store.GetAcceptedResultAsync(graph.ShareOperationId));
        Assert.False(await store.AcceptResultAsync(graph.ShareOperationId, "Preparing", result with { Id = Guid.NewGuid() }, graph.Now));

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
                    "WaitingShare", secondItemSetFingerprint, 2, null, null, "Unknown", 0, null, null, null, null, null,
                    graph.Now, graph.Now)], [new(secondShareId, 0, "remote-c", "C")]);
            await unit.CommitAsync();
        }
        var secondReserved = Assert.IsType<ShareOperationRecord>(await store.TryReserveNextAsync(graph.Now, TimeSpan.FromMinutes(2)));
        await store.AppendDiscoveryBaselinePageAsync(secondShareId, [new(secondShareId, "share-two", 2)]);
        Assert.True(await store.TryCompleteDiscoveryBaselineAsync(
            secondShareId, secondReserved.Status, 2, "second-baseline", 1, graph.Now));
        var secondResult = result with
        {
            Id = Guid.NewGuid(), ShareOperationId = secondShareId, ExactItemSetFingerprint = secondItemSetFingerprint,
            SettingsFingerprint = "settings", Generation = 2, ShareId = "share-two"
        };
        Assert.True(await store.ReuseExistingResultAsync(secondShareId, secondResult, graph.Now));

        var export = await store.GetResultsKeysetPageAsync(new ShareResultKeysetQuery(graph.BatchId, 1));
        Assert.NotNull(export.NextCursor);
        var exportTail = await store.GetResultsKeysetPageAsync(new ShareResultKeysetQuery(graph.BatchId, 1, export.NextCursor));
        Assert.Equal(new[] { result.Id, secondResult.Id }.Order(), export.Items.Concat(exportTail.Items).Select(item => item.Id).Order());
        Assert.Null(exportTail.NextCursor);

        Assert.True(await store.TryStartNextGenerationAsync(
            graph.ShareOperationId, "Succeeded", 1, graph.Now.AddMinutes(2), graph.Now.AddSeconds(1)));
        var regenerated = result with { Id = Guid.NewGuid(), Generation = 2, ShareId = "share-regenerated" };
        Assert.True(await store.TryCompleteDiscoveryBaselineAsync(
            graph.ShareOperationId, "Reserved", 2, "regenerated-baseline", 0, graph.Now.AddSeconds(1)));
        Assert.True(await store.TryMarkSubmittingAsync(
            graph.ShareOperationId, 2, "{}", graph.Now.AddSeconds(1)));
        Assert.True(await store.TryRecordAdapterRequestAsync(
            graph.ShareOperationId, 2, "request-regenerated", graph.Now.AddSeconds(1)));
        Assert.True(await store.AcceptResultAsync(graph.ShareOperationId, "Submitting", regenerated, graph.Now.AddSeconds(1)));
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

    private async Task<Graph> CreateGraphAsync(bool includeShareSettings)
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, $"{Guid.NewGuid():N}.db"));
        await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
        var now = new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);
        var account = AccountRecord.Create(Guid.NewGuid(), $"user-{Guid.NewGuid():N}", "Account");
        var batch = new BatchJobRecord(Guid.NewGuid(), "Batch", "Excel", "Distribution", "TaskCount", "ProportionalReduction",
            1, 1, "/", "None", "Skip", "Immediate", "Queued", now, null, null, null, 1, 1);
        var resource = new ResourceItemRecord(Guid.NewGuid(), batch.Id, null, 1, "source-url", "source-id", null, null,
            null, null, null, "None", "Valid", null, "Expanded", null, null, now, now, "Resolved title");
        var transfer = new TransferOperationRecord(Guid.NewGuid(), resource.Id, batch.Id, 1, 1, "/", "/", "", account.Id,
            "Rule", "Succeeded", "NewTransfer", 1, "transfer", "{}", "request", "[\"remote-a\",\"remote-b\"]",
            "ResultReceived", "Fulfilled", now, now, now, null, null, null, now, now);
        var shareId = Guid.NewGuid();
        var itemSetFingerprint = new RemoteItemIdentitySet([
            new("remote-a", "A"),
            new("remote-b", "B")]).Fingerprint;
        var share = new ShareOperationRecord(shareId, transfer.Id, resource.Id, batch.Id, account.Id, 1,
            "WaitingShare", itemSetFingerprint, 1, null, null, "Unknown", 0, null, null, null, null, null, now, now);

        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await unit.Accounts.UpsertAsync(account);
            await unit.Jobs.AddAsync(batch);
            await unit.Jobs.AddResourcesAsync([resource]);
            await unit.Jobs.AddOperationsAsync([transfer]);
            if (includeShareSettings)
            {
                await unit.Jobs.SetShareSettingsAsync(new(batch.Id, true, true, "Random", null, "Ask", now, "settings"));
            }
            await unit.Jobs.AddOutputAccountsAsync([new(batch.Id, account.Id, 1, "Account", 1, 1, true)]);
            await unit.Jobs.AddShareOperationsAsync([share], [
                new(shareId, 1, "remote-b", "B"),
                new(shareId, 0, "remote-a", "A")]);
            await unit.CommitAsync();
        }
        return new(factory, now, account.Id, batch.Id, resource.Id, transfer.Id, shareId, itemSetFingerprint);
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

    private sealed record Graph(SqliteConnectionFactory Factory, DateTimeOffset Now, Guid AccountId, Guid BatchId,
        Guid ResourceId, Guid TransferId, Guid ShareOperationId, string ItemSetFingerprint);
}
