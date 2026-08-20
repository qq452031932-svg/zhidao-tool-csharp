using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed class SqliteShareSchedulingStore : IShareSchedulingStore
{
    private readonly SqliteConnectionFactory factory;

    public SqliteShareSchedulingStore(SqliteConnectionFactory factory) =>
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async Task<ShareOperationRecord?> TryReserveNextAsync(
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            string? id = null;
            await using (var select = Command(connection, transaction, """
                SELECT operation.Id
                FROM ShareOperations operation
                JOIN BatchShareSettings settings ON settings.BatchJobId=operation.BatchJobId
                  AND settings.SettingsFingerprint=operation.SettingsFingerprint
                JOIN BatchOutputAccounts output ON output.BatchJobId=operation.BatchJobId
                  AND output.AccountId=operation.AccountId AND output.ShareEnabled=1
                WHERE (operation.Status='WaitingShare' AND
                         (operation.LeaseExpiresAt IS NULL OR julianday(operation.LeaseExpiresAt)<=julianday($now)))
                   OR (operation.Status='Reserved' AND
                         operation.LeaseExpiresAt IS NOT NULL AND julianday(operation.LeaseExpiresAt)<=julianday($now))
                ORDER BY operation.BatchJobId,operation.OutputOrdinal,operation.Id
                LIMIT 1;
                """))
            {
                Add(select,"$now",Time(now));
                id = Convert.ToString(await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            }
            if (string.IsNullOrEmpty(id))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            await using (var update = Command(connection, transaction, """
                UPDATE ShareOperations SET Status='Reserved',LeaseExpiresAt=$lease,UpdatedAt=$now
                WHERE Id=$id AND (
                  (Status='WaitingShare' AND (LeaseExpiresAt IS NULL OR julianday(LeaseExpiresAt)<=julianday($now)))
                  OR (Status='Reserved' AND LeaseExpiresAt IS NOT NULL AND julianday(LeaseExpiresAt)<=julianday($now)))
                  AND EXISTS(SELECT 1 FROM BatchShareSettings settings
                    WHERE settings.BatchJobId=ShareOperations.BatchJobId
                      AND settings.SettingsFingerprint=ShareOperations.SettingsFingerprint);
                """))
            {
                Add(update,"$id",id); Add(update,"$lease",Time(now + leaseDuration)); Add(update,"$now",Time(now));
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return null;
                }
            }
            var record = await GetOperationAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return record;
        }
        catch { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
    }

    public async Task<StringKeysetPage<ShareOperationRemoteItemRecord>> GetRemoteItemsKeysetPageAsync(
        Guid shareOperationId,
        string? afterRemoteItemId,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ValidatePage(shareOperationId, pageSize);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var command = Command(connection, transaction, """
            SELECT ShareOperationId,ItemOrdinal,RemoteItemId,DisplayName
            FROM ShareOperationRemoteItems
            WHERE ShareOperationId=$operation AND ($after IS NULL OR RemoteItemId>$after)
            ORDER BY RemoteItemId LIMIT $limit;
            """);
        Add(command,"$operation",Id(shareOperationId)); Add(command,"$after",afterRemoteItemId); Add(command,"$limit",checked(pageSize+1));
        var values = new List<ShareOperationRemoteItemRecord>(pageSize+1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            values.Add(new(Guid.Parse(reader.GetString(0)),reader.GetInt32(1),reader.GetString(2),reader.GetString(3)));
        var more = values.Count > pageSize;
        if (more) values.RemoveAt(values.Count-1);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(values,more ? values[^1].RemoteItemId : null);
    }

    public async Task AppendDiscoveryBaselinePageAsync(
        Guid shareOperationId,
        IEnumerable<ShareDiscoveryBaselineRecord> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            await using var command = Command(connection, transaction,
                "INSERT OR IGNORE INTO ShareDiscoveryBaselines(ShareOperationId,Generation,ShareId) SELECT $operation,$generation,$share WHERE EXISTS(SELECT 1 FROM ShareOperations operation JOIN BatchShareSettings settings ON settings.BatchJobId=operation.BatchJobId AND settings.SettingsFingerprint=operation.SettingsFingerprint WHERE operation.Id=$operation AND operation.Generation=$generation AND operation.BaselineCompletedAt IS NULL);");
            foreach (var entry in entries)
            {
                if (entry.ShareOperationId != shareOperationId || string.IsNullOrWhiteSpace(entry.ShareId))
                    throw new ArgumentException("The discovery baseline page is invalid.", nameof(entries));
                command.Parameters.Clear(); Add(command,"$operation",Id(shareOperationId)); Add(command,"$generation",entry.Generation); Add(command,"$share",entry.ShareId);
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1) continue;
                await using var existing = Command(connection, transaction, """
                    SELECT EXISTS(
                      SELECT 1 FROM ShareDiscoveryBaselines baseline
                      JOIN ShareOperations operation ON operation.Id=baseline.ShareOperationId
                      JOIN BatchShareSettings settings ON settings.BatchJobId=operation.BatchJobId
                        AND settings.SettingsFingerprint=operation.SettingsFingerprint
                      WHERE baseline.ShareOperationId=$operation AND baseline.Generation=$generation
                        AND baseline.ShareId=$share AND operation.Generation=$generation);
                    """);
                Add(existing,"$operation",Id(shareOperationId)); Add(existing,"$generation",entry.Generation); Add(existing,"$share",entry.ShareId);
                if (Convert.ToInt64(await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 1)
                    throw new InvalidOperationException("The discovery baseline generation is stale or already complete.");
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
    }

    public async Task<StringKeysetPage<ShareDiscoveryBaselineRecord>> GetDiscoveryBaselineKeysetPageAsync(
        Guid shareOperationId,
        string? afterShareId,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ValidatePage(shareOperationId,pageSize);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred:true);
        await using var command = Command(connection,transaction,"""
            SELECT ShareOperationId,ShareId,Generation FROM ShareDiscoveryBaselines
            WHERE ShareOperationId=$operation
              AND Generation=(SELECT Generation FROM ShareOperations WHERE Id=$operation)
              AND ($after IS NULL OR ShareId>$after)
            ORDER BY ShareId LIMIT $limit;
            """);
        Add(command,"$operation",Id(shareOperationId)); Add(command,"$after",afterShareId); Add(command,"$limit",checked(pageSize+1));
        var values = new List<ShareDiscoveryBaselineRecord>(pageSize+1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) values.Add(new(Guid.Parse(reader.GetString(0)),reader.GetString(1),reader.GetInt32(2)));
        var more = values.Count>pageSize; if (more) values.RemoveAt(values.Count-1);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(values,more ? values[^1].ShareId : null);
    }

    public async Task<bool> TryCompleteDiscoveryBaselineAsync(
        Guid shareOperationId, int expectedGeneration, string baselineFingerprint,
        int expectedShareCount, DateTimeOffset completedAt, CancellationToken cancellationToken = default)
    {
        if (shareOperationId == Guid.Empty) throw new ArgumentException("An operation is required.", nameof(shareOperationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineFingerprint);
        if (expectedGeneration < 1) throw new ArgumentOutOfRangeException(nameof(expectedGeneration));
        if (expectedShareCount < 0) throw new ArgumentOutOfRangeException(nameof(expectedShareCount));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred:false);
        try
        {
            await using var command = Command(connection,transaction,"""
                UPDATE ShareOperations
                SET Status='Preparing',BaselineGeneration=$generation,BaselineFingerprint=$fingerprint,
                  BaselineShareCount=$count,BaselineCompletedAt=$completed,UpdatedAt=$completed
                WHERE Id=$id AND Status='Reserved' AND Generation=$generation AND BaselineCompletedAt IS NULL
                  AND EXISTS(SELECT 1 FROM BatchShareSettings settings
                    WHERE settings.BatchJobId=ShareOperations.BatchJobId
                      AND settings.SettingsFingerprint=ShareOperations.SettingsFingerprint)
                  AND (SELECT COUNT(*) FROM ShareDiscoveryBaselines WHERE ShareOperationId=$id AND Generation=$generation)=$count;
                """);
            Add(command,"$generation",expectedGeneration); Add(command,"$fingerprint",baselineFingerprint);
            Add(command,"$count",expectedShareCount); Add(command,"$completed",Time(completedAt));
            Add(command,"$id",Id(shareOperationId));
            var changed=await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false)==1;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return changed;
        }
        catch { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
    }

    public async Task<ShareDiscoveryBaselineStateRecord?> GetDiscoveryBaselineStateAsync(
        Guid shareOperationId, CancellationToken cancellationToken = default)
    {
        await using var connection=await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction=connection.BeginTransaction(deferred:true);
        await using var command=Command(connection,transaction,"SELECT Id,Generation,BaselineFingerprint,BaselineShareCount,BaselineCompletedAt FROM ShareOperations WHERE Id=$id;");
        Add(command,"$id",Id(shareOperationId));
        await using var reader=await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var value=await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ShareDiscoveryBaselineStateRecord(Guid.Parse(reader.GetString(0)),reader.GetInt32(1),reader.IsDBNull(2)?null:reader.GetString(2),reader.GetInt32(3),!reader.IsDBNull(4),ReadNullableTime(reader,4))
            : null;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return value;
    }

    public Task<bool> TryMarkSubmittingAsync(
        Guid shareOperationId, int expectedGeneration, string requestDescriptorJson,
        DateTimeOffset submittedAt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestDescriptorJson);
        return TryTransitionAsync("""
            UPDATE ShareOperations SET Status='Submitting',RequestDescriptorJson=$descriptor,
              AdapterRequestId=NULL,SubmissionAcceptance='Unknown',AttemptCount=AttemptCount+1,
              SubmitStartedAt=$updated,NextReconcileAt=NULL,UpdatedAt=$updated
            WHERE Id=$id AND Status='Preparing' AND Generation=$generation
              AND BaselineGeneration=$generation AND BaselineCompletedAt IS NOT NULL
              AND RequestDescriptorJson IS NULL AND SubmitStartedAt IS NULL
              AND EXISTS(SELECT 1 FROM BatchShareSettings settings
                WHERE settings.BatchJobId=ShareOperations.BatchJobId
                  AND settings.SettingsFingerprint=ShareOperations.SettingsFingerprint);
            """, shareOperationId, expectedGeneration, submittedAt,
            [("$descriptor", requestDescriptorJson)], cancellationToken);
    }

    public Task<bool> TryRecordAdapterRequestAsync(
        Guid shareOperationId, int expectedGeneration, string adapterRequestId, DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterRequestId);
        return TryTransitionAsync("""
            UPDATE ShareOperations SET AdapterRequestId=$request,UpdatedAt=$updated
            WHERE Id=$id AND Status IN ('Submitting','ResultPending') AND Generation=$generation
              AND RequestDescriptorJson IS NOT NULL AND SubmitStartedAt IS NOT NULL
              AND AdapterRequestId IS NULL
              AND EXISTS(SELECT 1 FROM BatchShareSettings settings
                WHERE settings.BatchJobId=ShareOperations.BatchJobId
                  AND settings.SettingsFingerprint=ShareOperations.SettingsFingerprint);
            """, shareOperationId, expectedGeneration, updatedAt,
            [("$request", adapterRequestId)], cancellationToken);
    }

    public Task<bool> TryMarkResultPendingAsync(
        Guid shareOperationId, int expectedGeneration, DateTimeOffset nextReconcileAt, DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        if (nextReconcileAt < updatedAt) throw new ArgumentOutOfRangeException(nameof(nextReconcileAt));
        return TryTransitionAsync("""
            UPDATE ShareOperations SET Status='ResultPending',LeaseExpiresAt=NULL,
              NextReconcileAt=$reconcile,UpdatedAt=$updated
            WHERE Id=$id AND Status='Submitting' AND Generation=$generation
              AND RequestDescriptorJson IS NOT NULL AND SubmitStartedAt IS NOT NULL
              AND EXISTS(SELECT 1 FROM BatchShareSettings settings
                WHERE settings.BatchJobId=ShareOperations.BatchJobId
                  AND settings.SettingsFingerprint=ShareOperations.SettingsFingerprint);
            """, shareOperationId, expectedGeneration, updatedAt,
            [("$reconcile", Time(nextReconcileAt))], cancellationToken);
    }

    public Task<bool> TryStartNextGenerationAsync(
        Guid shareOperationId, ShareOperationStatus expectedStatus, int expectedGeneration, DateTimeOffset leaseExpiresAt,
        DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
    {
        if (expectedStatus is not (ShareOperationStatus.Succeeded or ShareOperationStatus.Failed or ShareOperationStatus.ReusedExisting))
            throw new ArgumentException("Only terminal share work can start another generation.", nameof(expectedStatus));
        if (leaseExpiresAt <= updatedAt) throw new ArgumentOutOfRangeException(nameof(leaseExpiresAt));
        return TryTransitionAsync("""
            UPDATE ShareOperations SET Status='Reserved',Generation=Generation+1,RequestDescriptorJson=NULL,
              AdapterRequestId=NULL,SubmissionAcceptance='Unknown',LeaseExpiresAt=$lease,SubmitStartedAt=NULL,
              NextReconcileAt=NULL,BaselineGeneration=NULL,BaselineFingerprint=NULL,BaselineShareCount=0,
              BaselineCompletedAt=NULL,LastErrorCode=NULL,LastErrorMessage=NULL,UpdatedAt=$updated
            WHERE Id=$id AND Status=$status AND Generation=$generation
              AND EXISTS(SELECT 1 FROM BatchShareSettings settings
                WHERE settings.BatchJobId=ShareOperations.BatchJobId
                  AND settings.SettingsFingerprint=ShareOperations.SettingsFingerprint);
            """, shareOperationId, expectedGeneration, updatedAt,
            [("$status", expectedStatus.ToString()), ("$lease", Time(leaseExpiresAt))], cancellationToken);
    }

    public async Task SaveDecisionSnapshotAsync(BatchShareDecisionSnapshotRecord snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.SettingsFingerprint) ||
            snapshot.Decision is not ("Reuse" or "Regenerate" or "Cancel") || snapshot.DiscoveredCount < 0 ||
            snapshot.CompatibleCount < 0 || snapshot.IncompatibleCount < 0 ||
            snapshot.CompatibleCount + snapshot.IncompatibleCount > snapshot.DiscoveredCount)
            throw new ArgumentException("The share decision snapshot is invalid.",nameof(snapshot));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred:false);
        try
        {
            await using var command = Command(connection,transaction,"""
                INSERT INTO BatchShareDecisionSnapshots(BatchJobId,AccountId,SettingsFingerprint,Decision,DiscoveryFingerprint,DiscoveredCount,
                  CompatibleCount,IncompatibleCount,Generation,ConfirmedAt)
                SELECT $batch,$account,$settings,$decision,$fingerprint,$discovered,$compatible,$incompatible,$generation,$confirmed
                WHERE EXISTS(SELECT 1 FROM BatchShareSettings settings
                    WHERE settings.BatchJobId=$batch AND settings.SettingsFingerprint=$settings)
                  AND EXISTS(SELECT 1 FROM ShareOperations operation
                    WHERE operation.BatchJobId=$batch AND operation.AccountId=$account
                      AND operation.SettingsFingerprint=$settings);
                """);
            Add(command,"$batch",Id(snapshot.BatchJobId)); Add(command,"$account",Id(snapshot.AccountId));
            Add(command,"$settings",snapshot.SettingsFingerprint);
            Add(command,"$decision",snapshot.Decision); Add(command,"$fingerprint",snapshot.DiscoveryFingerprint);
            Add(command,"$discovered",snapshot.DiscoveredCount); Add(command,"$compatible",snapshot.CompatibleCount);
            Add(command,"$incompatible",snapshot.IncompatibleCount); Add(command,"$generation",snapshot.Generation);
            Add(command,"$confirmed",Time(snapshot.ConfirmedAt));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Sharing is not enabled for this confirmed batch.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
    }

    public async Task<BatchShareDecisionSnapshotRecord?> GetDecisionSnapshotAsync(
        Guid batchJobId, Guid accountId, CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred:true);
        await using var command = Command(connection,transaction,"SELECT BatchJobId,AccountId,SettingsFingerprint,Decision,DiscoveryFingerprint,DiscoveredCount,CompatibleCount,IncompatibleCount,Generation,ConfirmedAt FROM BatchShareDecisionSnapshots WHERE BatchJobId=$batch AND AccountId=$account;");
        Add(command,"$batch",Id(batchJobId)); Add(command,"$account",Id(accountId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var value = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new BatchShareDecisionSnapshotRecord(Guid.Parse(reader.GetString(0)),Guid.Parse(reader.GetString(1)),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetInt32(5),reader.GetInt32(6),reader.GetInt32(7),reader.GetString(8),ReadTime(reader,9))
            : null;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return value;
    }

    public Task<bool> AcceptResultAsync(
        Guid shareOperationId, ShareOperationStatus expectedStatus, BaiduShareResultRecord result, DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        if (expectedStatus is not (ShareOperationStatus.Submitting or ShareOperationStatus.ResultPending))
            return Task.FromResult(false);
        return PersistResultAsync(shareOperationId, expectedStatus.ToString(), "Succeeded", result,
            baselineMustContainShare: false, updatedAt, cancellationToken);
    }

    public Task<bool> ReuseExistingResultAsync(
        Guid shareOperationId, BaiduShareResultRecord result, DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        PersistResultAsync(shareOperationId, "Preparing", "ReusedExisting", result,
            baselineMustContainShare: true, updatedAt, cancellationToken);

    private async Task<bool> PersistResultAsync(
        Guid shareOperationId, string expectedStatus, string finalStatus, BaiduShareResultRecord result,
        bool baselineMustContainShare, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (shareOperationId != result.ShareOperationId || !result.IsAccepted || !result.IsCurrent)
            throw new ArgumentException("An accepted result must match the share operation.",nameof(result));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred:false);
        try
        {
            await using (var update = Command(connection,transaction,"""
                UPDATE ShareOperations SET Status=$final,LeaseExpiresAt=NULL,UpdatedAt=$updated
                WHERE Id=$id AND Status=$expected AND BatchJobId=$batch AND ResourceItemId=$resource
                  AND AccountId=$account AND ExactItemSetFingerprint=$items AND SettingsFingerprint=$settings
                  AND Generation=$generation
                  AND BaselineGeneration=$generation AND BaselineCompletedAt IS NOT NULL
                  AND EXISTS(SELECT 1 FROM BatchShareSettings settings
                    WHERE settings.BatchJobId=ShareOperations.BatchJobId
                      AND settings.SettingsFingerprint=ShareOperations.SettingsFingerprint)
                  AND (($baselineMustContain=1 AND EXISTS(
                    SELECT 1 FROM ShareDiscoveryBaselines baseline
                    WHERE baseline.ShareOperationId=ShareOperations.Id AND baseline.Generation=$generation
                      AND baseline.ShareId=$share))
                    OR ($baselineMustContain=0 AND NOT EXISTS(
                    SELECT 1 FROM ShareDiscoveryBaselines baseline
                    WHERE baseline.ShareOperationId=ShareOperations.Id AND baseline.Generation=$generation
                      AND baseline.ShareId=$share)));
                """))
            {
                Add(update,"$final",finalStatus); Add(update,"$updated",Time(updatedAt)); Add(update,"$id",Id(shareOperationId)); Add(update,"$expected",expectedStatus);
                Add(update,"$batch",Id(result.BatchJobId)); Add(update,"$resource",Id(result.ResourceItemId));
                Add(update,"$account",Id(result.AccountId)); Add(update,"$items",result.ExactItemSetFingerprint);
                Add(update,"$settings",result.SettingsFingerprint); Add(update,"$generation",result.Generation);
                Add(update,"$share",result.ShareId); Add(update,"$baselineMustContain",baselineMustContainShare ? 1 : 0);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false)!=1)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return false;
                }
            }
            await using (var demote = Command(connection,transaction,"""
                UPDATE BaiduShareResults SET IsCurrent=0,UpdatedAt=$updated
                WHERE AccountId=$account AND ExactItemSetFingerprint=$items AND SettingsFingerprint=$settings
                  AND IsAccepted=1 AND IsCurrent=1;
                """))
            {
                Add(demote,"$updated",Time(updatedAt)); Add(demote,"$account",Id(result.AccountId));
                Add(demote,"$items",result.ExactItemSetFingerprint); Add(demote,"$settings",result.SettingsFingerprint);
                await demote.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using var insert = Command(connection,transaction,"""
                INSERT INTO BaiduShareResults(Id,ShareOperationId,BatchJobId,ResourceItemId,AccountId,ExactItemSetFingerprint,
                  SettingsFingerprint,Generation,ShareId,ShareUrl,ExtractionCode,IsAccepted,IsCurrent,SharedAt,UpdatedAt)
                VALUES($id,$operation,$batch,$resource,$account,$items,$settings,$generation,$share,$url,$code,$accepted,$current,$shared,$updated);
                """);
            Add(insert,"$id",Id(result.Id)); Add(insert,"$operation",Id(result.ShareOperationId)); Add(insert,"$batch",Id(result.BatchJobId));
            Add(insert,"$resource",Id(result.ResourceItemId)); Add(insert,"$account",Id(result.AccountId));
            Add(insert,"$items",result.ExactItemSetFingerprint); Add(insert,"$settings",result.SettingsFingerprint);
            Add(insert,"$generation",result.Generation); Add(insert,"$share",result.ShareId); Add(insert,"$url",result.ShareUrl);
            Add(insert,"$code",result.ExtractionCode); Add(insert,"$accepted",result.IsAccepted?1:0); Add(insert,"$current",result.IsCurrent?1:0);
            Add(insert,"$shared",Time(result.SharedAt)); Add(insert,"$updated",Time(result.UpdatedAt));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
    }

    public async Task<BaiduShareResultRecord?> GetAcceptedResultAsync(Guid shareOperationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred:true);
        await using var command = Command(connection,transaction,ResultSelect+" WHERE ShareOperationId=$operation AND IsAccepted=1 AND IsCurrent=1 ORDER BY Generation DESC,Id DESC LIMIT 1;");
        Add(command,"$operation",Id(shareOperationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var value = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadResult(reader) : null;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return value;
    }

    public async Task<CursorKeysetPage<BaiduShareResultRecord, ShareResultCursor>> GetResultsKeysetPageAsync(ShareResultKeysetQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query); query.Validate();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred:true);
        await using var command = Command(connection,transaction,ResultColumns+",resource.CandidateSequence FROM BaiduShareResults JOIN ResourceItems resource ON resource.Id=BaiduShareResults.ResourceItemId WHERE BaiduShareResults.BatchJobId=$batch AND BaiduShareResults.IsAccepted=1 AND BaiduShareResults.IsCurrent=1 AND (resource.CandidateSequence>$afterSequence OR (resource.CandidateSequence=$afterSequence AND (BaiduShareResults.AccountId>$afterAccount OR (BaiduShareResults.AccountId=$afterAccount AND BaiduShareResults.Id>$afterResult)))) ORDER BY resource.CandidateSequence,BaiduShareResults.AccountId,BaiduShareResults.Id LIMIT $limit;");
        Add(command,"$batch",Id(query.BatchJobId)); Add(command,"$afterSequence",query.After?.CandidateSequence??0L);
        Add(command,"$afterAccount",query.After is null?string.Empty:Id(query.After.AccountId));
        Add(command,"$afterResult",query.After is null?string.Empty:Id(query.After.ResultId)); Add(command,"$limit",checked(query.PageSize+1));
        var values = new List<(BaiduShareResultRecord Result,long Cursor)>(query.PageSize+1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) values.Add((ReadResult(reader),reader.GetInt64(15)));
        var more=values.Count>query.PageSize; if(more)values.RemoveAt(values.Count-1);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(values.Select(value=>value.Result).ToArray(),more?new(values[^1].Cursor,values[^1].Result.AccountId,values[^1].Result.Id):null);
    }

    private const string ResultColumns = "SELECT BaiduShareResults.Id,BaiduShareResults.ShareOperationId,BaiduShareResults.BatchJobId,BaiduShareResults.ResourceItemId,BaiduShareResults.AccountId,BaiduShareResults.ExactItemSetFingerprint,BaiduShareResults.SettingsFingerprint,BaiduShareResults.Generation,BaiduShareResults.ShareId,BaiduShareResults.ShareUrl,BaiduShareResults.ExtractionCode,BaiduShareResults.IsAccepted,BaiduShareResults.IsCurrent,BaiduShareResults.SharedAt,BaiduShareResults.UpdatedAt";
    private const string ResultSelect = ResultColumns + " FROM BaiduShareResults";

    private static BaiduShareResultRecord ReadResult(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),Guid.Parse(reader.GetString(1)),Guid.Parse(reader.GetString(2)),Guid.Parse(reader.GetString(3)),Guid.Parse(reader.GetString(4)),
        reader.GetString(5),reader.GetString(6),reader.GetInt32(7),reader.GetString(8),reader.GetString(9),reader.GetString(10),reader.GetInt64(11)!=0,reader.GetInt64(12)!=0,ReadTime(reader,13),ReadTime(reader,14));

    private static async Task<ShareOperationRecord> GetOperationAsync(SqliteConnection connection, SqliteTransaction transaction, string id, CancellationToken token)
    {
        await using var command = Command(connection,transaction,"SELECT Id,TransferOperationId,ResourceItemId,BatchJobId,AccountId,OutputOrdinal,Status,ExactItemSetFingerprint,SettingsFingerprint,Generation,RequestDescriptorJson,AdapterRequestId,SubmissionAcceptance,AttemptCount,LeaseExpiresAt,SubmitStartedAt,NextReconcileAt,LastErrorCode,LastErrorMessage,CreatedAt,UpdatedAt FROM ShareOperations WHERE Id=$id;");
        Add(command,"$id",id); await using var reader=await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if(!await reader.ReadAsync(token).ConfigureAwait(false))throw new InvalidDataException("Reserved share work disappeared.");
        return new(Guid.Parse(reader.GetString(0)),Guid.Parse(reader.GetString(1)),Guid.Parse(reader.GetString(2)),Guid.Parse(reader.GetString(3)),Guid.Parse(reader.GetString(4)),reader.GetInt32(5),reader.GetString(6),reader.GetString(7),reader.GetString(8),reader.GetInt32(9),reader.IsDBNull(10)?null:reader.GetString(10),reader.IsDBNull(11)?null:reader.GetString(11),reader.GetString(12),reader.GetInt32(13),ReadNullableTime(reader,14),ReadNullableTime(reader,15),ReadNullableTime(reader,16),reader.IsDBNull(17)?null:reader.GetString(17),reader.IsDBNull(18)?null:reader.GetString(18),ReadTime(reader,19),ReadTime(reader,20));
    }

    private async Task<bool> TryTransitionAsync(
        string sql, Guid shareOperationId, int expectedGeneration, DateTimeOffset updatedAt,
        IReadOnlyList<(string Name, object? Value)> additionalParameters,
        CancellationToken cancellationToken)
    {
        if (shareOperationId == Guid.Empty) throw new ArgumentException("An operation is required.", nameof(shareOperationId));
        if (expectedGeneration < 1) throw new ArgumentOutOfRangeException(nameof(expectedGeneration));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred:false);
        try
        {
            await using var command = Command(connection, transaction, sql);
            Add(command,"$id",Id(shareOperationId)); Add(command,"$generation",expectedGeneration);
            Add(command,"$updated",Time(updatedAt));
            foreach (var parameter in additionalParameters) Add(command,parameter.Name,parameter.Value);
            var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return changed;
        }
        catch { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
    }

    private static void ValidatePage(Guid id,int pageSize){if(id==Guid.Empty)throw new ArgumentException("An operation is required.",nameof(id));if(pageSize is <1 or >1000)throw new ArgumentOutOfRangeException(nameof(pageSize));}
    private static SqliteCommand Command(SqliteConnection connection,SqliteTransaction transaction,string sql){var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText=sql;return command;}
    private static void Add(SqliteCommand command,string name,object? value)=>command.Parameters.AddWithValue(name,value??DBNull.Value);
    private static string Id(Guid value)=>value.ToString("D");
    private static string Time(DateTimeOffset value)=>value.ToUniversalTime().ToString("O");
    private static DateTimeOffset ReadTime(SqliteDataReader reader,int ordinal)=>DateTimeOffset.Parse(reader.GetString(ordinal),System.Globalization.CultureInfo.InvariantCulture);
    private static DateTimeOffset? ReadNullableTime(SqliteDataReader reader,int ordinal)=>reader.IsDBNull(ordinal)?null:ReadTime(reader,ordinal);
}
