using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Infrastructure.Database;

namespace BaiduBatchTransfer.Infrastructure.Tests.Database;

public sealed class StandaloneShareStoreTests : IDisposable
{
    // Ownership note: these rows are share-only facts. Every user selection creates exactly one
    // operation and one exact remote-item row; no TransferOperation is fabricated and the existing
    // transfer-triggered ShareOperations foreign-key boundary remains untouched.
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bbt-standalone-share-{Guid.NewGuid():N}");

    [Fact]
    public async Task ResultProjectionIsAStableKeysetAndUsesOnlyExactCurrentGenerationEvidence()
    {
        var graph = await CreateAsync();
        var receipt = await graph.Store.CreateOrGetBatchAsync(Envelope(graph, 3, graph.Now));
        var operations = receipt.Operations.OrderBy(item => item.SequenceNumber).ToArray();
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var transaction = connection.BeginTransaction())
        {
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE StandaloneShareOperations SET Status=CASE SequenceNumber WHEN 1 THEN 'Succeeded' WHEN 2 THEN 'ResultPending' ELSE 'Failed' END, SubmissionState=CASE SequenceNumber WHEN 1 THEN 'Completed' WHEN 2 THEN 'ResultPending' ELSE 'DefinitelyNotAccepted' END, LastErrorCode=CASE SequenceNumber WHEN 3 THEN 'SHARE_FAILED' END WHERE BatchId=$batch;";
                update.Parameters.AddWithValue("$batch", receipt.Batch.Id.ToString("D"));
                await update.ExecuteNonQueryAsync();
            }
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO StandaloneShareResults(Id,OperationId,BatchId,AccountId,Generation,ExactItemSetFingerprint,SettingsFingerprint,ShareId,ShareUrl,ExtractionCode,IsPermanent,IsAccessUnlimited,AcceptedAt,CreatedAt) VALUES($id,$operation,$batch,$account,$generation,$items,$settings,'share-1','https://pan.baidu.com/s/result','a1b2',1,1,$now,$now);";
                insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                insert.Parameters.AddWithValue("$operation", operations[0].Id.ToString("D"));
                insert.Parameters.AddWithValue("$batch", receipt.Batch.Id.ToString("D"));
                insert.Parameters.AddWithValue("$account", graph.AccountId.ToString("D"));
                insert.Parameters.AddWithValue("$generation", operations[0].Generation);
                insert.Parameters.AddWithValue("$items", operations[0].ExactRemoteItems.Fingerprint);
                insert.Parameters.AddWithValue("$settings", operations[0].SettingsFingerprint);
                insert.Parameters.AddWithValue("$now", graph.Now.ToString("O"));
                await insert.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }

        IStandaloneShareResultProjectionRepository projection = new SqliteStandaloneShareStore(graph.Factory);
        var first = await projection.GetPageAsync(receipt.Batch.Id, 0, 2);
        var second = await projection.GetPageAsync(receipt.Batch.Id, first.NextSequenceNumber!.Value, 2);

        Assert.Equal([1, 2], first.Items.Select(item => item.SequenceNumber));
        Assert.Equal(2, first.NextSequenceNumber);
        Assert.Equal("account", first.Items[0].AccountDisplayName);
        Assert.Equal("https://pan.baidu.com/s/result", first.Items[0].ShareUrl);
        Assert.Equal("a1b2", first.Items[0].ExtractionCode);
        Assert.Null(first.Items[1].ShareUrl);
        var failed = Assert.Single(second.Items);
        Assert.Equal(3, failed.SequenceNumber);
        Assert.Equal("SHARE_FAILED", failed.StableErrorCode);
        Assert.Null(second.NextSequenceNumber);
    }

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
    public async Task ResultDirectoryIsNormalizedAndRoundTripsAfterDatabaseReopen()
    {
        var graph = await CreateAsync();
        var batchId = Guid.NewGuid();
        var unnormalized = Path.Combine(graph.Root, "private", "..", $"results-{batchId:N}");
        var batch = StandaloneShareBatchRecord.Create(batchId, unnormalized, 1, graph.Now);
        var operation = StandaloneShareOperationRecord.Create(
            Guid.NewGuid(), batchId,
            new OwnedRemoteSelection(
                graph.AccountId,
                new RemoteItemIdentitySet([new RemoteItemIdentity("remote-path", "item")]),
                "item", RemoteResourceKind.File, new RemotePath("/apps")),
            1, graph.Now);

        await graph.Store.CreateOrGetBatchAsync(StandaloneSharePendingBatchEnvelope.Create(batch, [operation]));
        var restored = await new SqliteStandaloneShareStore(graph.Factory)
            .GetBatchesAsync(new PageQuery(1, 10));

        Assert.Equal(Path.GetFullPath(unnormalized), Assert.Single(restored.Items).ResultDirectoryReference);
    }

    [Fact]
    public async Task FixedExtractionSettingsRoundTripAndRemainTheAuthoritativeResultFingerprint()
    {
        var graph = await CreateAsync();
        var batchId = Guid.NewGuid();
        var batch = StandaloneShareBatchRecord.Create(
            batchId, Path.Combine(graph.Root, $"results-{batchId:N}"), 1, graph.Now);
        var operation = StandaloneShareOperationRecord.Create(
            Guid.NewGuid(), batchId,
            new OwnedRemoteSelection(
                graph.AccountId,
                new RemoteItemIdentitySet([new RemoteItemIdentity("remote-fixed", "item")]),
                "item", RemoteResourceKind.File, new RemotePath("/apps")),
            1, graph.Now, ShareExtractionCodeBehavior.Fixed, "A1b2");

        await graph.Store.CreateOrGetBatchAsync(
            StandaloneSharePendingBatchEnvelope.Create(batch, [operation]));
        var restored = Assert.Single(await new SqliteStandaloneShareStore(graph.Factory)
            .GetOperationsAsync(batchId));

        Assert.Equal(ShareExtractionCodeBehavior.Fixed, restored.ExtractionCodeBehavior);
        Assert.Equal("A1b2", restored.FixedExtractionCode);
        Assert.Equal(operation.SettingsFingerprint, restored.SettingsFingerprint);
    }

    [Theory]
    [InlineData(ExistingShareDecision.Reuse)]
    [InlineData(ExistingShareDecision.Regenerate)]
    [InlineData(ExistingShareDecision.Cancel)]
    public async Task ExistingShareDecisionRoundTripsAfterDatabaseReopen(
        ExistingShareDecision decision)
    {
        var graph = await CreateAsync();
        var batchId = Guid.NewGuid();
        var batch = StandaloneShareBatchRecord.Create(
            batchId, Path.Combine(graph.Root, $"results-{batchId:N}"), 1, graph.Now);
        var operation = StandaloneShareOperationRecord.Create(
            Guid.NewGuid(), batchId,
            new OwnedRemoteSelection(
                graph.AccountId,
                new RemoteItemIdentitySet([new RemoteItemIdentity("remote-policy", "item")]),
                "item", RemoteResourceKind.File, new RemotePath("/apps")),
            1, graph.Now, ShareExtractionCodeBehavior.Random, null, decision);

        await graph.Store.CreateOrGetBatchAsync(
            StandaloneSharePendingBatchEnvelope.Create(batch, [operation]));
        var restored = Assert.Single(await new SqliteStandaloneShareStore(graph.Factory)
            .GetOperationsAsync(batchId));

        Assert.Equal(decision, restored.ExistingShareDecision);
    }

    [Fact]
    public async Task StoreRejectsAnUnsafePersistedResultDirectoryWithoutEchoingIt()
    {
        var graph = await CreateAsync();
        var receipt = await graph.Store.CreateOrGetBatchAsync(Envelope(graph, 1, graph.Now));
        const string unsafeValue = "https://example.test/private-results";
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE StandaloneShareBatches SET ResultDirectoryReference=$path WHERE Id=$id;";
            command.Parameters.AddWithValue("$path", unsafeValue);
            command.Parameters.AddWithValue("$id", receipt.Batch.Id.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            new SqliteStandaloneShareStore(graph.Factory).GetBatchesAsync(new PageQuery(1, 10)));

        Assert.DoesNotContain(unsafeValue, error.Message, StringComparison.OrdinalIgnoreCase);
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
            receipt.Batch.Id, Guid.NewGuid(), graph.Now.AddSeconds(4), graph.Now.AddMinutes(1));
        Assert.NotNull(lease);
        Assert.True(await graph.Store.TryFailAsync(
            lease.OperationId, lease.BatchId, lease.AccountId, lease.Generation, lease.LeaseGeneration,
            lease.LeaseOwnerId,
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
            receipt.Batch.Id, Guid.NewGuid(), graph.Now.AddSeconds(1), graph.Now.AddMinutes(1)))!;

        Assert.False(await graph.Store.TryCompleteBaselineAsync(
            lease.OperationId, lease.BatchId, Guid.NewGuid(), lease.Generation, lease.LeaseGeneration,
            lease.LeaseOwnerId, lease.Revision,
            ["existing-1"], Fingerprint('a'), graph.Now.AddSeconds(2)));
        Assert.False(await graph.Store.TryCompleteBaselineAsync(
            lease.OperationId, lease.BatchId, lease.AccountId, lease.Generation + 1, lease.LeaseGeneration,
            lease.LeaseOwnerId, lease.Revision,
            ["existing-1"], Fingerprint('a'), graph.Now.AddSeconds(2)));
        Assert.False(await graph.Store.TryCompleteBaselineAsync(
            lease.OperationId, Guid.NewGuid(), lease.AccountId, lease.Generation, lease.LeaseGeneration,
            lease.LeaseOwnerId, lease.Revision,
            ["existing-1"], Fingerprint('a'), graph.Now.AddSeconds(2)));
        Assert.False(await graph.Store.TryCompleteBaselineAsync(
            lease.OperationId, lease.BatchId, lease.AccountId, lease.Generation, lease.LeaseGeneration,
            Guid.NewGuid(), lease.Revision,
            ["existing-1"], Fingerprint('a'), graph.Now.AddSeconds(2)));
        Assert.False(await graph.Store.TryMarkResultPendingAsync(
            lease.OperationId, lease.BatchId, lease.AccountId, lease.Generation, lease.LeaseGeneration,
            lease.LeaseOwnerId, lease.Revision,
            graph.Now.AddSeconds(2)));
        Assert.True(await graph.Store.TryCompleteBaselineAsync(
            lease.OperationId, lease.BatchId, lease.AccountId, lease.Generation, lease.LeaseGeneration,
            lease.LeaseOwnerId, lease.Revision,
            ["existing-2", "existing-1"], Fingerprint('a'), graph.Now.AddSeconds(2)));
        var prepared = (await graph.Store.GetOperationsAsync(receipt.Batch.Id)).Single();
        Assert.False(await graph.Store.TryMarkSubmittingAsync(
            prepared.Id, prepared.BatchId, prepared.AccountId, prepared.Generation, lease.LeaseGeneration,
            lease.LeaseOwnerId,
            prepared.Revision - 1, graph.Now.AddSeconds(3)));
        Assert.True(await graph.Store.TryMarkSubmittingAsync(
            prepared.Id, prepared.BatchId, prepared.AccountId, prepared.Generation, lease.LeaseGeneration,
            lease.LeaseOwnerId,
            prepared.Revision, graph.Now.AddSeconds(3)));
        var submitting = Assert.Single(await graph.Store.GetOperationsAsync(receipt.Batch.Id));
        Assert.Equal(StandaloneShareOperationStatus.Submitting, submitting.Status);
        Assert.Equal(StandaloneShareSubmissionState.Prepared, submitting.SubmissionState);
        Assert.Equal(SubmissionAcceptance.Unknown, submitting.SubmissionAcceptance);
    }

    [Fact]
    public async Task DefinitelyNotAcceptedAttemptIsDurableAndExpiredLeaseRecoversToSafeNewGeneration()
    {
        var graph = await CreateAsync();
        var receipt = await graph.Store.CreateOrGetBatchAsync(Envelope(graph, 1, graph.Now));
        var owner = Guid.NewGuid();
        var lease = (await graph.Store.TryLeaseNextAsync(
            receipt.Batch.Id, owner, graph.Now.AddSeconds(1), graph.Now.AddSeconds(10)))!;
        Assert.True(await graph.Store.TryCompleteBaselineAsync(
            lease.OperationId, lease.BatchId, lease.AccountId, lease.Generation,
            lease.LeaseGeneration, lease.LeaseOwnerId, lease.Revision,
            ["existing-share"], WebAutomationBaseline.Create(["existing-share"]).Fingerprint,
            graph.Now.AddSeconds(2)));
        var prepared = Assert.Single((await graph.Store.GetBatchSnapshotAsync(receipt.Batch.Id))!.Operations);
        Assert.True(await graph.Store.TryMarkSubmittingAsync(
            prepared.Id, prepared.BatchId, prepared.AccountId, prepared.Generation,
            prepared.LeaseGeneration, prepared.LeaseOwnerId!.Value, prepared.Revision,
            graph.Now.AddSeconds(3)));
        var submitting = Assert.Single((await graph.Store.GetBatchSnapshotAsync(receipt.Batch.Id))!.Operations);

        Assert.True(await graph.Store.TryRecordDefinitelyNotAcceptedAttemptAsync(
            submitting.Id, submitting.BatchId, submitting.AccountId, submitting.Generation,
            submitting.LeaseGeneration, submitting.LeaseOwnerId!.Value, submitting.Revision,
            1, graph.Now.AddSeconds(4)));
        var durable = Assert.Single((await graph.Store.GetBatchSnapshotAsync(receipt.Batch.Id))!.Operations);
        Assert.Equal(SubmissionAcceptance.DefinitelyNotAccepted, durable.SubmissionAcceptance);
        Assert.Equal(1, durable.AttemptCount);
        Assert.False(await graph.Store.TryRecordDefinitelyNotAcceptedAttemptAsync(
            durable.Id, durable.BatchId, durable.AccountId, durable.Generation,
            durable.LeaseGeneration, durable.LeaseOwnerId!.Value, durable.Revision,
            2, graph.Now.AddSeconds(5)));

        Assert.Equal(1, await graph.Store.RecoverExpiredLeasesAsync(graph.Now.AddSeconds(11), 10));
        var recovered = Assert.Single((await graph.Store.GetBatchSnapshotAsync(receipt.Batch.Id))!.Operations);
        Assert.Equal(StandaloneShareOperationStatus.Queued, recovered.Status);
        Assert.Equal(StandaloneShareSubmissionState.NotStarted, recovered.SubmissionState);
        Assert.Equal(SubmissionAcceptance.Unknown, recovered.SubmissionAcceptance);
        Assert.Equal(submitting.Generation + 1, recovered.Generation);
        Assert.Equal(0, recovered.AttemptCount);
        Assert.Null(recovered.BaselineFingerprint);
        Assert.Null(recovered.LeaseOwnerId);
    }

    [Fact]
    public async Task ExpiredLeaseRejectsLateBrowserStageEvent()
    {
        var graph = await CreateAsync();
        var receipt = await graph.Store.CreateOrGetBatchAsync(Envelope(graph, 1, graph.Now));
        var lease = (await graph.Store.TryLeaseNextAsync(
            receipt.Batch.Id, Guid.NewGuid(), graph.Now.AddSeconds(1), graph.Now.AddSeconds(2)))!;

        Assert.False(await graph.Store.TryCompleteBaselineAsync(
            lease.OperationId, lease.BatchId, lease.AccountId, lease.Generation, lease.LeaseGeneration,
            lease.LeaseOwnerId, lease.Revision,
            [], Fingerprint('e'), graph.Now.AddSeconds(3)));
    }

    [Fact]
    public async Task SubmittingAcceptsOnlyCurrentIdentityAndSettingsFingerprintAndStoresOneRedactedResult()
    {
        var graph = await CreateAsync();
        var receipt = await graph.Store.CreateOrGetBatchAsync(Envelope(graph, 1, graph.Now));
        var lease = (await graph.Store.TryLeaseNextAsync(
            receipt.Batch.Id, Guid.NewGuid(), graph.Now.AddSeconds(1), graph.Now.AddMinutes(1)))!;
        Assert.True(await graph.Store.TryCompleteBaselineAsync(
            lease.OperationId, lease.BatchId, lease.AccountId, lease.Generation, lease.LeaseGeneration,
            lease.LeaseOwnerId, lease.Revision,
            [], Fingerprint('b'), graph.Now.AddSeconds(2)));
        var prepared = (await graph.Store.GetOperationsAsync(receipt.Batch.Id)).Single();
        Assert.True(await graph.Store.TryMarkSubmittingAsync(
            prepared.Id, prepared.BatchId, prepared.AccountId, prepared.Generation, lease.LeaseGeneration,
            lease.LeaseOwnerId,
            prepared.Revision, graph.Now.AddSeconds(3)));
        var submitting = (await graph.Store.GetOperationsAsync(receipt.Batch.Id)).Single();
        var result = new StandaloneShareResultRecord(
            Guid.NewGuid(), submitting.Id, submitting.BatchId, submitting.AccountId, submitting.Generation,
            submitting.ExactRemoteItems.Fingerprint, submitting.SettingsFingerprint, "share-id",
            "https://pan.baidu.com/s/private", "a1b2", true, true,
            graph.Now.AddSeconds(5), graph.Now.AddSeconds(5));

        Assert.False(await graph.Store.TryAcceptResultAsync(
            result with { AccountId = Guid.NewGuid() }, lease.LeaseGeneration, lease.LeaseOwnerId, submitting.Revision,
            graph.Now.AddSeconds(5)));
        Assert.False(await graph.Store.TryAcceptResultAsync(
            result with { SettingsFingerprint = Fingerprint('c') }, lease.LeaseGeneration, lease.LeaseOwnerId,
            submitting.Revision, graph.Now.AddSeconds(5)));
        Assert.True(await graph.Store.TryAcceptResultAsync(
            result, lease.LeaseGeneration, lease.LeaseOwnerId, submitting.Revision, graph.Now.AddSeconds(5)));
        Assert.False(await graph.Store.TryAcceptResultAsync(
            result, lease.LeaseGeneration, lease.LeaseOwnerId, submitting.Revision, graph.Now.AddSeconds(6)));
        var restored = await graph.Store.GetResultAsync(submitting.Id, submitting.Generation);
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
            receipt.Batch.Id, Guid.NewGuid(), graph.Now.AddSeconds(1), graph.Now.AddMinutes(1)))!;
        Assert.True(await graph.Store.TryCompleteBaselineAsync(
            lease.OperationId, lease.BatchId, lease.AccountId, lease.Generation, lease.LeaseGeneration,
            lease.LeaseOwnerId, lease.Revision,
            [], Fingerprint('d'), graph.Now.AddSeconds(2)));
        var prepared = (await graph.Store.GetOperationsAsync(receipt.Batch.Id)).Single();
        Assert.True(await graph.Store.TryMarkSubmittingAsync(
            prepared.Id, prepared.BatchId, prepared.AccountId, prepared.Generation, lease.LeaseGeneration,
            lease.LeaseOwnerId,
            prepared.Revision, graph.Now.AddSeconds(3)));
        var submitting = (await graph.Store.GetOperationsAsync(receipt.Batch.Id)).Single();

        Assert.True(await graph.Store.TryFailAsync(
            submitting.Id, submitting.BatchId, submitting.AccountId, submitting.Generation, lease.LeaseGeneration,
            lease.LeaseOwnerId,
            submitting.Revision, SubmissionAcceptance.Unknown, "SUBMIT_RESULT_UNKNOWN",
            graph.Now.AddSeconds(4)));

        var snapshot = (await graph.Store.GetBatchSnapshotAsync(receipt.Batch.Id))!;
        Assert.Equal(StandaloneShareBatchStatus.CompletedWithFailures, snapshot.Batch.Status);
        var failed = Assert.Single(snapshot.Operations);
        Assert.Equal(StandaloneShareMutationDisposition.Unsafe,
            (await graph.Store.TryRetryFailedOperationAsync(
                failed.Id, failed.Generation, failed.Revision, graph.Now.AddSeconds(5))).Disposition);
    }

    [Fact]
    public async Task ExpiredPreparingLeaseAfterReopenSafelyQueuesANewGenerationAndRejectsOldOwner()
    {
        var graph = await CreateAsync();
        var receipt = await graph.Store.CreateOrGetBatchAsync(Envelope(graph, 1, graph.Now));
        var oldOwner = Guid.NewGuid();
        var lease = (await graph.Store.TryLeaseNextAsync(
            receipt.Batch.Id, oldOwner, graph.Now.AddSeconds(1), graph.Now.AddSeconds(2)))!;
        var reopened = new SqliteStandaloneShareStore(graph.Factory);

        Assert.Equal(1, await reopened.RecoverExpiredLeasesAsync(graph.Now.AddSeconds(2), 10));
        var recovered = Assert.Single(await reopened.GetOperationsAsync(receipt.Batch.Id));
        Assert.Equal(StandaloneShareOperationStatus.Queued, recovered.Status);
        Assert.Equal(lease.Generation + 1, recovered.Generation);
        Assert.Null(recovered.LeaseOwnerId);
        Assert.False(await reopened.TryCompleteBaselineAsync(
            lease.OperationId, lease.BatchId, lease.AccountId, lease.Generation,
            lease.LeaseGeneration, oldOwner, lease.Revision, [], Fingerprint('f'), graph.Now.AddSeconds(4)));

        var newOwner = Guid.NewGuid();
        var newLease = await reopened.TryLeaseNextAsync(
            receipt.Batch.Id, newOwner, graph.Now.AddSeconds(4), graph.Now.AddSeconds(10));
        Assert.Equal(recovered.Generation, newLease!.Generation);
        Assert.Equal(newOwner, newLease.LeaseOwnerId);
    }

    [Fact]
    public async Task ExpiredSubmittingLeaseAfterReopenBecomesReconciliationOnly()
    {
        var graph = await CreateAsync();
        var receipt = await graph.Store.CreateOrGetBatchAsync(Envelope(graph, 1, graph.Now));
        var oldOwner = Guid.NewGuid();
        var lease = (await graph.Store.TryLeaseNextAsync(
            receipt.Batch.Id, oldOwner, graph.Now.AddSeconds(1), graph.Now.AddSeconds(5)))!;
        Assert.True(await graph.Store.TryCompleteBaselineAsync(
            lease.OperationId, lease.BatchId, lease.AccountId, lease.Generation,
            lease.LeaseGeneration, oldOwner, lease.Revision, [], Fingerprint('1'), graph.Now.AddSeconds(2)));
        var prepared = Assert.Single(await graph.Store.GetOperationsAsync(receipt.Batch.Id));
        Assert.True(await graph.Store.TryMarkSubmittingAsync(
            prepared.Id, prepared.BatchId, prepared.AccountId, prepared.Generation,
            prepared.LeaseGeneration, oldOwner, prepared.Revision, graph.Now.AddSeconds(3)));

        var reopened = new SqliteStandaloneShareStore(graph.Factory);
        Assert.Equal(1, await reopened.RecoverExpiredLeasesAsync(graph.Now.AddSeconds(6), 10));
        var pending = Assert.Single(await reopened.GetOperationsAsync(receipt.Batch.Id));
        Assert.Equal(StandaloneShareOperationStatus.ResultPending, pending.Status);
        Assert.Null(pending.LeaseOwnerId);
        Assert.Null(await reopened.TryLeaseNextAsync(
            receipt.Batch.Id, Guid.NewGuid(), graph.Now.AddSeconds(7), graph.Now.AddSeconds(12)));
        Assert.False(await reopened.TryMarkResultPendingAsync(
            prepared.Id, prepared.BatchId, prepared.AccountId, prepared.Generation,
            prepared.LeaseGeneration, oldOwner, prepared.Revision, graph.Now.AddSeconds(7)));

        var reconcileOwner = Guid.NewGuid();
        var reconcileLease = await reopened.TryClaimReconciliationAsync(
            receipt.Batch.Id, reconcileOwner, graph.Now.AddSeconds(7), graph.Now.AddSeconds(12));
        Assert.NotNull(reconcileLease);
        Assert.Equal(reconcileOwner, reconcileLease.LeaseOwnerId);
    }

    [Fact]
    public async Task ExplicitOfficialRejectionAfterClickIsTheOnlySubmittingStateThatCanBeSafelyRetried()
    {
        var graph = await CreateAsync();
        var receipt = await graph.Store.CreateOrGetBatchAsync(Envelope(graph, 1, graph.Now));
        var owner = Guid.NewGuid();
        var lease = (await graph.Store.TryLeaseNextAsync(
            receipt.Batch.Id, owner, graph.Now.AddSeconds(1), graph.Now.AddMinutes(1)))!;
        Assert.True(await graph.Store.TryCompleteBaselineAsync(
            lease.OperationId, lease.BatchId, lease.AccountId, lease.Generation,
            lease.LeaseGeneration, owner, lease.Revision, [], Fingerprint('4'), graph.Now.AddSeconds(2)));
        var prepared = Assert.Single(await graph.Store.GetOperationsAsync(receipt.Batch.Id));
        Assert.True(await graph.Store.TryMarkSubmittingAsync(
            prepared.Id, prepared.BatchId, prepared.AccountId, prepared.Generation,
            prepared.LeaseGeneration, owner, prepared.Revision, graph.Now.AddSeconds(3)));
        var submitting = Assert.Single(await graph.Store.GetOperationsAsync(receipt.Batch.Id));

        Assert.True(await graph.Store.TryFailAsync(
            submitting.Id, submitting.BatchId, submitting.AccountId, submitting.Generation,
            submitting.LeaseGeneration, owner, submitting.Revision,
            SubmissionAcceptance.DefinitelyNotAccepted, "OFFICIAL_REJECTION", graph.Now.AddSeconds(4)));

        var rejected = Assert.Single(await graph.Store.GetOperationsAsync(receipt.Batch.Id));
        Assert.True(rejected.CanSafelyRetry);
        Assert.Equal(StandaloneShareSubmissionState.DefinitelyNotAccepted, rejected.SubmissionState);
    }

    [Fact]
    public async Task ResultPendingReleasesExecutionLeaseAndCanBeClaimedAfterReopen()
    {
        var graph = await CreateAsync();
        var receipt = await graph.Store.CreateOrGetBatchAsync(Envelope(graph, 1, graph.Now));
        var owner = Guid.NewGuid();
        var lease = (await graph.Store.TryLeaseNextAsync(
            receipt.Batch.Id, owner, graph.Now.AddSeconds(1), graph.Now.AddSeconds(5)))!;
        Assert.True(await graph.Store.TryCompleteBaselineAsync(
            lease.OperationId, lease.BatchId, lease.AccountId, lease.Generation,
            lease.LeaseGeneration, owner, lease.Revision, [], Fingerprint('2'), graph.Now.AddSeconds(2)));
        var prepared = Assert.Single(await graph.Store.GetOperationsAsync(receipt.Batch.Id));
        Assert.True(await graph.Store.TryMarkSubmittingAsync(
            prepared.Id, prepared.BatchId, prepared.AccountId, prepared.Generation,
            prepared.LeaseGeneration, owner, prepared.Revision, graph.Now.AddSeconds(3)));
        var submitting = Assert.Single(await graph.Store.GetOperationsAsync(receipt.Batch.Id));
        Assert.True(await graph.Store.TryMarkResultPendingAsync(
            submitting.Id, submitting.BatchId, submitting.AccountId, submitting.Generation,
            submitting.LeaseGeneration, owner, submitting.Revision, graph.Now.AddSeconds(4)));

        var reopened = new SqliteStandaloneShareStore(graph.Factory);
        Assert.Equal(0, await reopened.RecoverExpiredLeasesAsync(graph.Now.AddSeconds(6), 10));
        var recovered = Assert.Single(await reopened.GetOperationsAsync(receipt.Batch.Id));
        Assert.Equal(StandaloneShareOperationStatus.ResultPending, recovered.Status);
        var reconciliationLease = await reopened.TryClaimReconciliationAsync(
            receipt.Batch.Id, Guid.NewGuid(), graph.Now.AddSeconds(7), graph.Now.AddSeconds(12));
        Assert.NotNull(reconciliationLease);
        Assert.True(await reopened.TryReleaseReconciliationAsync(
            reconciliationLease!.OperationId, reconciliationLease.BatchId, reconciliationLease.AccountId,
            reconciliationLease.Generation, reconciliationLease.LeaseGeneration,
            reconciliationLease.LeaseOwnerId, reconciliationLease.Revision, graph.Now.AddSeconds(8)));
        Assert.NotNull(await reopened.TryClaimReconciliationAsync(
            receipt.Batch.Id, Guid.NewGuid(), graph.Now.AddSeconds(9), graph.Now.AddSeconds(14)));
    }

    [Fact]
    public async Task AmbiguousExpiredSubmissionFailsClosedAsNeedsDecision()
    {
        var graph = await CreateAsync();
        var receipt = await graph.Store.CreateOrGetBatchAsync(Envelope(graph, 1, graph.Now));
        var owner = Guid.NewGuid();
        var lease = (await graph.Store.TryLeaseNextAsync(
            receipt.Batch.Id, owner, graph.Now.AddSeconds(1), graph.Now.AddSeconds(2)))!;
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE StandaloneShareOperations SET Status='Submitting',SubmissionState='Accepted'
                WHERE Id=$id;
                """;
            command.Parameters.AddWithValue("$id", lease.OperationId.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        Assert.Equal(1, await new SqliteStandaloneShareStore(graph.Factory)
            .RecoverExpiredLeasesAsync(graph.Now.AddSeconds(3), 10));
        var recovered = Assert.Single(await graph.Store.GetOperationsAsync(receipt.Batch.Id));
        Assert.Equal(StandaloneShareOperationStatus.NeedsDecision, recovered.Status);
        Assert.Null(recovered.LeaseOwnerId);
    }

    [Fact]
    public async Task PausedBatchCannotLeaseRecoveredWorkOrReconciliation()
    {
        var graph = await CreateAsync();
        var receipt = await graph.Store.CreateOrGetBatchAsync(Envelope(graph, 1, graph.Now));
        var owner = Guid.NewGuid();
        var lease = (await graph.Store.TryLeaseNextAsync(
            receipt.Batch.Id, owner, graph.Now.AddSeconds(1), graph.Now.AddSeconds(5)))!;
        Assert.True(await graph.Store.TryCompleteBaselineAsync(
            lease.OperationId, lease.BatchId, lease.AccountId, lease.Generation,
            lease.LeaseGeneration, owner, lease.Revision, [], Fingerprint('3'), graph.Now.AddSeconds(2)));
        var prepared = Assert.Single(await graph.Store.GetOperationsAsync(receipt.Batch.Id));
        Assert.True(await graph.Store.TryMarkSubmittingAsync(
            prepared.Id, prepared.BatchId, prepared.AccountId, prepared.Generation,
            prepared.LeaseGeneration, owner, prepared.Revision, graph.Now.AddSeconds(3)));
        var activeBatch = (await graph.Store.GetBatchSnapshotAsync(receipt.Batch.Id))!.Batch;
        Assert.Equal(StandaloneShareMutationDisposition.Applied,
            (await graph.Store.TryPauseBatchAsync(activeBatch.Id, activeBatch.Revision, graph.Now.AddSeconds(4))).Disposition);

        var reopened = new SqliteStandaloneShareStore(graph.Factory);
        Assert.Equal(1, await reopened.RecoverExpiredLeasesAsync(graph.Now.AddSeconds(6), 10));
        Assert.Null(await reopened.TryLeaseNextAsync(
            receipt.Batch.Id, Guid.NewGuid(), graph.Now.AddSeconds(7), graph.Now.AddSeconds(12)));
        Assert.Null(await reopened.TryClaimReconciliationAsync(
            receipt.Batch.Id, Guid.NewGuid(), graph.Now.AddSeconds(7), graph.Now.AddSeconds(12)));
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
