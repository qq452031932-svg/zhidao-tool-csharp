using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using Microsoft.Data.Sqlite;
using System.Text;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed class SqliteShareSchedulingStore : IShareSchedulingStore, IShareExecutionStore, IExistingShareDecisionStore
{
    internal const string ResultKeysetSelectSql = ResultColumns +
        ",BaiduShareResults.CandidateSequence,BaiduShareResults.OutputOrdinal FROM BaiduShareResults " +
        "WHERE BaiduShareResults.BatchJobId=$batch AND BaiduShareResults.IsAccepted=1 AND BaiduShareResults.IsCurrent=1 " +
        "AND (BaiduShareResults.CandidateSequence>$afterSequence OR " +
        "(BaiduShareResults.CandidateSequence=$afterSequence AND (BaiduShareResults.OutputOrdinal>$afterOutput OR " +
        "(BaiduShareResults.OutputOrdinal=$afterOutput AND BaiduShareResults.Id>$afterResult)))) " +
        "ORDER BY BaiduShareResults.CandidateSequence,BaiduShareResults.OutputOrdinal,BaiduShareResults.Id LIMIT $limit;";

    internal const string ExpiredBatchSelectSql = """
        SELECT batch.Id FROM BatchJobs batch
        WHERE batch.Status='Active' AND EXISTS(
          SELECT 1 FROM ShareOperations candidate
          WHERE candidate.BatchJobId=batch.Id AND candidate.Status='Reserved'
            AND candidate.LeaseExpiresAt IS NOT NULL AND candidate.LeaseExpiresAt<=$now
            AND EXISTS(SELECT 1 FROM BatchShareSettings settings
              WHERE settings.BatchJobId=candidate.BatchJobId
                AND settings.SettingsFingerprint=candidate.SettingsFingerprint)
            AND EXISTS(SELECT 1 FROM BatchOutputAccounts output
              WHERE output.BatchJobId=candidate.BatchJobId AND output.AccountId=candidate.AccountId
                AND output.OutputOrdinal=candidate.OutputOrdinal AND output.ShareEnabled=1)
            AND EXISTS(SELECT 1 FROM ShareOperationRemoteItems remote
              WHERE remote.ShareOperationId=candidate.Id)
            AND EXISTS(SELECT 1 FROM ShareOperationGenerations lineage
              WHERE lineage.ShareOperationId=candidate.Id AND lineage.Generation=candidate.Generation
                AND lineage.BatchJobId=candidate.BatchJobId AND lineage.ResourceItemId=candidate.ResourceItemId
                AND lineage.AccountId=candidate.AccountId
                AND lineage.ExactItemSetFingerprint=candidate.ExactItemSetFingerprint
                AND lineage.SettingsFingerprint=candidate.SettingsFingerprint
                AND lineage.ExactItemCount=(SELECT COUNT(*) FROM ShareOperationRemoteItems exactItem
                  WHERE exactItem.ShareOperationId=candidate.Id)))
        ORDER BY batch.QueueOrder,batch.CreatedAt,batch.Id LIMIT 1;
        """;

    internal const string ExpiredOperationSelectSql = """
        SELECT operation.Id FROM ShareOperations operation
        WHERE operation.BatchJobId=$batch AND operation.Status='Reserved'
          AND operation.LeaseExpiresAt IS NOT NULL AND operation.LeaseExpiresAt<=$now
          AND EXISTS(SELECT 1 FROM BatchShareSettings settings
            WHERE settings.BatchJobId=operation.BatchJobId
              AND settings.SettingsFingerprint=operation.SettingsFingerprint)
          AND EXISTS(SELECT 1 FROM BatchOutputAccounts output
            WHERE output.BatchJobId=operation.BatchJobId AND output.AccountId=operation.AccountId
              AND output.OutputOrdinal=operation.OutputOrdinal AND output.ShareEnabled=1)
          AND EXISTS(SELECT 1 FROM ShareOperationRemoteItems remote
            WHERE remote.ShareOperationId=operation.Id)
          AND EXISTS(SELECT 1 FROM ShareOperationGenerations lineage
            WHERE lineage.ShareOperationId=operation.Id AND lineage.Generation=operation.Generation
              AND lineage.BatchJobId=operation.BatchJobId AND lineage.ResourceItemId=operation.ResourceItemId
              AND lineage.AccountId=operation.AccountId
              AND lineage.ExactItemSetFingerprint=operation.ExactItemSetFingerprint
              AND lineage.SettingsFingerprint=operation.SettingsFingerprint
              AND lineage.ExactItemCount=(SELECT COUNT(*) FROM ShareOperationRemoteItems exactItem
                WHERE exactItem.ShareOperationId=operation.Id))
        ORDER BY operation.LeaseExpiresAt,operation.OutputOrdinal,operation.Id LIMIT 1;
        """;

    private readonly SqliteConnectionFactory factory;

    public SqliteShareSchedulingStore(SqliteConnectionFactory factory) =>
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async Task<ShareExecutionContext?> LoadAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation is required.", nameof(operationId));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var command = Command(connection, transaction, """
            SELECT operation.Id,operation.TransferOperationId,operation.ResourceItemId,operation.BatchJobId,
              operation.AccountId,operation.OutputOrdinal,operation.Status,operation.ExactItemSetFingerprint,
              operation.SettingsFingerprint,operation.Generation,operation.RequestDescriptorJson,
              operation.AdapterRequestId,operation.SubmissionAcceptance,operation.AttemptCount,
              operation.LeaseExpiresAt,operation.SubmitStartedAt,operation.NextReconcileAt,
              operation.LastErrorCode,operation.LastErrorMessage,operation.CreatedAt,operation.UpdatedAt,
              operation.LeaseGeneration,
              settings.IsPermanent,settings.IsAccessUnlimited,settings.ExtractionCodeMode,
              settings.FixedExtractionCode,settings.ExistingSharePolicy,settings.CreatedAt,
              output.ShareEnabled,
              decision.Decision,
              (SELECT member.ShareId FROM BatchShareDiscoveryMembers member
                WHERE member.BatchJobId=operation.BatchJobId AND member.AccountId=operation.AccountId
                  AND member.SettingsFingerprint=operation.SettingsFingerprint
                  AND member.DiscoveryFingerprint=decision.DiscoveryFingerprint
                  AND member.ExactItemSetFingerprint=operation.ExactItemSetFingerprint
                ORDER BY member.IsCompatible DESC,member.ShareId LIMIT 1),
              (SELECT member.ShareUrl FROM BatchShareDiscoveryMembers member
                WHERE member.BatchJobId=operation.BatchJobId AND member.AccountId=operation.AccountId
                  AND member.SettingsFingerprint=operation.SettingsFingerprint
                  AND member.DiscoveryFingerprint=decision.DiscoveryFingerprint
                  AND member.ExactItemSetFingerprint=operation.ExactItemSetFingerprint
                ORDER BY member.IsCompatible DESC,member.ShareId LIMIT 1),
              (SELECT member.ExtractionCode FROM BatchShareDiscoveryMembers member
                WHERE member.BatchJobId=operation.BatchJobId AND member.AccountId=operation.AccountId
                  AND member.SettingsFingerprint=operation.SettingsFingerprint
                  AND member.DiscoveryFingerprint=decision.DiscoveryFingerprint
                  AND member.ExactItemSetFingerprint=operation.ExactItemSetFingerprint
                ORDER BY member.IsCompatible DESC,member.ShareId LIMIT 1),
              (SELECT member.IsPermanent FROM BatchShareDiscoveryMembers member
                WHERE member.BatchJobId=operation.BatchJobId AND member.AccountId=operation.AccountId
                  AND member.SettingsFingerprint=operation.SettingsFingerprint
                  AND member.DiscoveryFingerprint=decision.DiscoveryFingerprint
                  AND member.ExactItemSetFingerprint=operation.ExactItemSetFingerprint
                ORDER BY member.IsCompatible DESC,member.ShareId LIMIT 1),
              (SELECT member.IsAccessUnlimited FROM BatchShareDiscoveryMembers member
                WHERE member.BatchJobId=operation.BatchJobId AND member.AccountId=operation.AccountId
                  AND member.SettingsFingerprint=operation.SettingsFingerprint
                  AND member.DiscoveryFingerprint=decision.DiscoveryFingerprint
                  AND member.ExactItemSetFingerprint=operation.ExactItemSetFingerprint
                ORDER BY member.IsCompatible DESC,member.ShareId LIMIT 1)
            FROM ShareOperations operation
            JOIN BatchShareSettings settings ON settings.BatchJobId=operation.BatchJobId
              AND settings.SettingsFingerprint=operation.SettingsFingerprint
            JOIN BatchOutputAccounts output ON output.BatchJobId=operation.BatchJobId
              AND output.AccountId=operation.AccountId AND output.OutputOrdinal=operation.OutputOrdinal
            LEFT JOIN BatchShareDecisionSnapshots decision ON decision.BatchJobId=operation.BatchJobId
              AND decision.AccountId=operation.AccountId AND decision.SettingsFingerprint=operation.SettingsFingerprint
              AND EXISTS(SELECT 1 FROM BatchShareDiscoverySnapshots discovery
                WHERE discovery.BatchJobId=decision.BatchJobId AND discovery.AccountId=decision.AccountId
                  AND discovery.SettingsFingerprint=decision.SettingsFingerprint
                  AND discovery.DiscoveryFingerprint=decision.DiscoveryFingerprint
                  AND discovery.DiscoveredCount=decision.DiscoveredCount
                  AND discovery.CompatibleCount=decision.CompatibleCount
                  AND discovery.IncompatibleCount=decision.IncompatibleCount
                  AND discovery.Generation=decision.Generation
                  AND discovery.Version=decision.DiscoveryVersion)
              AND EXISTS(SELECT 1 FROM BatchShareDiscoveryMembers member
                WHERE member.BatchJobId=operation.BatchJobId AND member.AccountId=operation.AccountId
                  AND member.SettingsFingerprint=operation.SettingsFingerprint
                  AND member.DiscoveryFingerprint=decision.DiscoveryFingerprint
                  AND member.ExactItemSetFingerprint=operation.ExactItemSetFingerprint)
            WHERE operation.Id=$id;
            """);
        Add(command, "$id", Id(operationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        var operation = ReadOperation(reader);
        var settings = new BatchShareSettingsRecord(
            operation.BatchJobId, reader.GetInt64(22) != 0, reader.GetInt64(23) != 0,
            reader.GetString(24), reader.IsDBNull(25) ? null : reader.GetString(25), reader.GetString(26),
            ReadTime(reader, 27), operation.SettingsFingerprint);
        var shareEnabled = reader.GetInt64(28) != 0;
        var decision = reader.IsDBNull(29)
            ? (ExistingShareDecision?)null
            : Enum.Parse<ExistingShareDecision>(reader.GetString(29), ignoreCase: false);
        var existingShareId = reader.IsDBNull(30) ? null : reader.GetString(30);
        var existingShareUrl = reader.IsDBNull(31) ? null : reader.GetString(31);
        var existingCode = reader.IsDBNull(32) ? null : reader.GetString(32);
        var existingPermanent = !reader.IsDBNull(33) && reader.GetInt64(33) != 0;
        var existingUnlimited = !reader.IsDBNull(34) && reader.GetInt64(34) != 0;
        await reader.DisposeAsync().ConfigureAwait(false);

        await using var itemsCommand = Command(connection, transaction, """
            SELECT RemoteItemId,DisplayName FROM ShareOperationRemoteItems
            WHERE ShareOperationId=$id ORDER BY CanonicalSortKey;
            """);
        Add(itemsCommand, "$id", Id(operationId));
        var items = new List<RemoteItemIdentity>();
        await using (var itemsReader = await itemsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await itemsReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                items.Add(new(itemsReader.GetString(0), itemsReader.GetString(1)));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (items.Count == 0) return null;
        var exact = new RemoteItemIdentitySet(items);
        var existing = existingShareId is null || existingShareUrl is null || existingCode is null
            ? null
            : new BaiduShareRecord(existingShareId, exact, existingShareUrl, existingCode, existingPermanent, existingUnlimited);
        return new(operation, settings, Selected: true, shareEnabled, exact, decision, existing);
    }

    public Task<bool> TryMarkPreparingAsync(
        Guid operationId,
        int generation,
        long leaseGeneration,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        TryTransitionAsync("""
            UPDATE ShareOperations SET Status='Preparing',UpdatedAt=$updated
            WHERE Id=$id AND Status='Reserved' AND Generation=$generation
              AND LeaseGeneration=$leaseGeneration AND LeaseExpiresAt IS NOT NULL
              AND RequestDescriptorJson IS NULL AND SubmitStartedAt IS NULL
              AND EXISTS(SELECT 1 FROM BatchJobs batch
                WHERE batch.Id=ShareOperations.BatchJobId AND batch.Status='Active')
              AND EXISTS(SELECT 1 FROM BatchOutputAccounts output
                WHERE output.BatchJobId=ShareOperations.BatchJobId
                  AND output.AccountId=ShareOperations.AccountId
                  AND output.OutputOrdinal=ShareOperations.OutputOrdinal AND output.ShareEnabled=1);
            """, operationId, generation, leaseGeneration, updatedAt, [], cancellationToken);

    public Task<bool> TryRecordDefinitelyNotAcceptedRetryAsync(
        Guid operationId,
        int generation,
        long leaseGeneration,
        int expectedAttemptCount,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        if (expectedAttemptCount < 1) throw new ArgumentOutOfRangeException(nameof(expectedAttemptCount));
        return TryTransitionAsync("""
            UPDATE ShareOperations SET SubmissionAcceptance='DefinitelyNotAccepted',UpdatedAt=$updated
            WHERE Id=$id AND Status='Submitting' AND Generation=$generation
              AND LeaseGeneration=$leaseGeneration AND AttemptCount=$attempt
              AND RequestDescriptorJson IS NOT NULL AND SubmitStartedAt IS NOT NULL;
            """, operationId, generation, leaseGeneration, updatedAt,
            [("$attempt", expectedAttemptCount)], cancellationToken);
    }

    public Task<bool> TryBeginSubmissionAttemptAsync(
        Guid operationId,
        int generation,
        long leaseGeneration,
        int expectedAttemptCount,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        if (expectedAttemptCount < 1) throw new ArgumentOutOfRangeException(nameof(expectedAttemptCount));
        return TryTransitionAsync("""
            UPDATE ShareOperations SET AttemptCount=AttemptCount+1,
              SubmissionAcceptance='Unknown',AdapterRequestId=NULL,UpdatedAt=$updated
            WHERE Id=$id AND Status='Submitting' AND Generation=$generation
              AND LeaseGeneration=$leaseGeneration AND AttemptCount=$attempt
              AND SubmissionAcceptance='DefinitelyNotAccepted'
              AND RequestDescriptorJson IS NOT NULL AND SubmitStartedAt IS NOT NULL
              AND EXISTS(SELECT 1 FROM BatchJobs batch
                WHERE batch.Id=ShareOperations.BatchJobId AND batch.Status='Active')
              AND EXISTS(SELECT 1 FROM BatchOutputAccounts output
                WHERE output.BatchJobId=ShareOperations.BatchJobId
                  AND output.AccountId=ShareOperations.AccountId
                  AND output.OutputOrdinal=ShareOperations.OutputOrdinal AND output.ShareEnabled=1);
            """, operationId, generation, leaseGeneration, updatedAt,
            [("$attempt", expectedAttemptCount)], cancellationToken);
    }

    public Task<bool> TryPauseAsync(
        Guid operationId,
        int generation,
        long leaseGeneration,
        SharePauseReason reason,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return TryTransitionAsync("""
            UPDATE ShareOperations SET Status='Paused',LeaseExpiresAt=NULL,
              LastErrorCode=$code,LastErrorMessage=$message,UpdatedAt=$updated
            WHERE Id=$id AND Status IN ('Reserved','Preparing','Submitting')
              AND Generation=$generation AND LeaseGeneration=$leaseGeneration
              AND (Status<>'Submitting' OR SubmissionAcceptance='DefinitelyNotAccepted');
            """, operationId, generation, leaseGeneration, updatedAt,
            [("$code", reason.ToString()), ("$message", SafePauseMessage(reason))], cancellationToken);
    }

    public Task<bool> TryFailAsync(
        Guid operationId,
        int generation,
        long leaseGeneration,
        string errorCode,
        string safeMessage,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(safeMessage);
        if (errorCode.Length > 128 || safeMessage.Length > 512)
            throw new ArgumentException("The stable share failure is too large.");
        return TryTransitionAsync("""
            UPDATE ShareOperations SET Status='Failed',LeaseExpiresAt=NULL,
              LastErrorCode=$code,LastErrorMessage=$message,UpdatedAt=$updated
            WHERE Id=$id AND Status IN ('Preparing','Submitting') AND Generation=$generation
              AND LeaseGeneration=$leaseGeneration
              AND (Status='Preparing' OR SubmissionAcceptance='DefinitelyNotAccepted');
            """, operationId, generation, leaseGeneration, updatedAt,
            [("$code", errorCode), ("$message", safeMessage)], cancellationToken);
    }

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
            async Task<string?> SelectAsync(string status, bool expired)
            {
                if (expired)
                {
                    string? expiredBatchId;
                    await using (var selectBatch = Command(connection, transaction, ExpiredBatchSelectSql))
                    {
                        Add(selectBatch,"$now",Time(now));
                        expiredBatchId = Convert.ToString(await selectBatch.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
                    }
                    if (string.IsNullOrEmpty(expiredBatchId)) return null;
                    await using var selectOperation = Command(connection, transaction, ExpiredOperationSelectSql);
                    Add(selectOperation,"$batch",expiredBatchId); Add(selectOperation,"$now",Time(now));
                    var expiredId = Convert.ToString(await selectOperation.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
                    return string.IsNullOrEmpty(expiredId) ? null : expiredId;
                }

                string? batchId;
                await using (var selectBatch = Command(connection, transaction, $"""
                    SELECT batch.Id FROM BatchJobs batch
                    WHERE batch.Status='Active' AND EXISTS(
                      SELECT 1 FROM ShareOperations candidate
                      WHERE candidate.BatchJobId=batch.Id AND candidate.Status=$status
                        AND candidate.LeaseExpiresAt IS NULL
                        AND EXISTS(SELECT 1 FROM BatchShareSettings settings
                          WHERE settings.BatchJobId=candidate.BatchJobId
                            AND settings.SettingsFingerprint=candidate.SettingsFingerprint)
                        AND EXISTS(SELECT 1 FROM BatchOutputAccounts output
                          WHERE output.BatchJobId=candidate.BatchJobId AND output.AccountId=candidate.AccountId
                            AND output.OutputOrdinal=candidate.OutputOrdinal AND output.ShareEnabled=1)
                        AND EXISTS(SELECT 1 FROM ShareOperationRemoteItems remote
                          WHERE remote.ShareOperationId=candidate.Id)
                        AND EXISTS(SELECT 1 FROM ShareOperationGenerations lineage
                          WHERE lineage.ShareOperationId=candidate.Id AND lineage.Generation=candidate.Generation
                            AND lineage.BatchJobId=candidate.BatchJobId AND lineage.ResourceItemId=candidate.ResourceItemId
                            AND lineage.AccountId=candidate.AccountId
                            AND lineage.ExactItemSetFingerprint=candidate.ExactItemSetFingerprint
                            AND lineage.SettingsFingerprint=candidate.SettingsFingerprint
                            AND lineage.ExactItemCount=(SELECT COUNT(*) FROM ShareOperationRemoteItems exactItem
                              WHERE exactItem.ShareOperationId=candidate.Id)))
                    ORDER BY batch.QueueOrder,batch.CreatedAt,batch.Id LIMIT 1;
                    """))
                {
                    Add(selectBatch,"$status",status);
                    batchId = Convert.ToString(await selectBatch.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
                }
                if (string.IsNullOrEmpty(batchId)) return null;

                await using var select = Command(connection, transaction, $"""
                    SELECT operation.Id FROM ShareOperations operation
                    WHERE operation.BatchJobId=$batch AND operation.Status=$status AND operation.LeaseExpiresAt IS NULL
                      AND EXISTS(SELECT 1 FROM BatchShareSettings settings
                        WHERE settings.BatchJobId=operation.BatchJobId
                          AND settings.SettingsFingerprint=operation.SettingsFingerprint)
                      AND EXISTS(SELECT 1 FROM BatchOutputAccounts output
                        WHERE output.BatchJobId=operation.BatchJobId AND output.AccountId=operation.AccountId
                          AND output.OutputOrdinal=operation.OutputOrdinal AND output.ShareEnabled=1)
                      AND EXISTS(SELECT 1 FROM ShareOperationRemoteItems remote
                        WHERE remote.ShareOperationId=operation.Id)
                      AND EXISTS(SELECT 1 FROM ShareOperationGenerations lineage
                        WHERE lineage.ShareOperationId=operation.Id AND lineage.Generation=operation.Generation
                          AND lineage.BatchJobId=operation.BatchJobId AND lineage.ResourceItemId=operation.ResourceItemId
                          AND lineage.AccountId=operation.AccountId
                          AND lineage.ExactItemSetFingerprint=operation.ExactItemSetFingerprint
                          AND lineage.SettingsFingerprint=operation.SettingsFingerprint
                          AND lineage.ExactItemCount=(SELECT COUNT(*) FROM ShareOperationRemoteItems exactItem
                            WHERE exactItem.ShareOperationId=operation.Id))
                    ORDER BY operation.OutputOrdinal,operation.Id LIMIT 1;
                    """);
                Add(select,"$batch",batchId); Add(select,"$status",status);
                var selected = Convert.ToString(await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
                return string.IsNullOrEmpty(selected) ? null : selected;
            }
            var id = await SelectAsync("Reserved", expired: true).ConfigureAwait(false)
                ?? await SelectAsync("WaitingShare", expired: false).ConfigureAwait(false);
            if (string.IsNullOrEmpty(id))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            await using (var update = Command(connection, transaction, """
                UPDATE ShareOperations SET Status='Reserved',LeaseGeneration=LeaseGeneration+1,
                  LeaseExpiresAt=$lease,UpdatedAt=$now
                WHERE Id=$id AND (
                  (Status='WaitingShare' AND LeaseExpiresAt IS NULL)
                  OR (Status='Reserved' AND LeaseExpiresAt IS NOT NULL AND LeaseExpiresAt<=$now))
                  AND EXISTS(SELECT 1 FROM BatchShareSettings settings
                    WHERE settings.BatchJobId=ShareOperations.BatchJobId
                      AND settings.SettingsFingerprint=ShareOperations.SettingsFingerprint)
                  AND EXISTS(SELECT 1 FROM BatchJobs batch
                    WHERE batch.Id=ShareOperations.BatchJobId AND batch.Status='Active')
                  AND EXISTS(SELECT 1 FROM BatchOutputAccounts output
                    WHERE output.BatchJobId=ShareOperations.BatchJobId
                      AND output.AccountId=ShareOperations.AccountId
                      AND output.OutputOrdinal=ShareOperations.OutputOrdinal AND output.ShareEnabled=1)
                  AND EXISTS(SELECT 1 FROM ShareOperationRemoteItems remote
                    WHERE remote.ShareOperationId=ShareOperations.Id)
                  AND EXISTS(SELECT 1 FROM ShareOperationGenerations lineage
                    WHERE lineage.ShareOperationId=ShareOperations.Id
                      AND lineage.Generation=ShareOperations.Generation
                      AND lineage.BatchJobId=ShareOperations.BatchJobId
                      AND lineage.ResourceItemId=ShareOperations.ResourceItemId
                      AND lineage.AccountId=ShareOperations.AccountId
                      AND lineage.ExactItemSetFingerprint=ShareOperations.ExactItemSetFingerprint
                      AND lineage.SettingsFingerprint=ShareOperations.SettingsFingerprint
                      AND lineage.ExactItemCount=(SELECT COUNT(*) FROM ShareOperationRemoteItems exactItem
                        WHERE exactItem.ShareOperationId=ShareOperations.Id));
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
            WHERE ShareOperationId=$operation AND ($afterKey IS NULL OR CanonicalSortKey>$afterKey)
            ORDER BY CanonicalSortKey LIMIT $limit;
            """);
        Add(command,"$operation",Id(shareOperationId));
        Add(command,"$afterKey",afterRemoteItemId is null ? null : RemoteItemIdentitySet.GetCanonicalSortKey(afterRemoteItemId));
        Add(command,"$limit",checked(pageSize+1));
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
        long expectedLeaseGeneration,
        IEnumerable<ShareDiscoveryBaselineRecord> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            await using var command = Command(connection, transaction,
                "INSERT OR IGNORE INTO ShareDiscoveryBaselines(ShareOperationId,Generation,ShareId,ShareUrl,ExtractionCode,IsPermanent,IsAccessUnlimited) SELECT $operation,$generation,$share,$url,$code,$permanent,$unlimited WHERE EXISTS(SELECT 1 FROM ShareOperations operation JOIN BatchShareSettings settings ON settings.BatchJobId=operation.BatchJobId AND settings.SettingsFingerprint=operation.SettingsFingerprint WHERE operation.Id=$operation AND operation.Generation=$generation AND operation.LeaseGeneration=$leaseGeneration AND operation.BaselineCompletedAt IS NULL);");
            foreach (var entry in entries)
            {
                if (entry.ShareOperationId != shareOperationId || string.IsNullOrWhiteSpace(entry.ShareId))
                    throw new ArgumentException("The discovery baseline page is invalid.", nameof(entries));
                command.Parameters.Clear(); Add(command,"$operation",Id(shareOperationId)); Add(command,"$generation",entry.Generation); Add(command,"$leaseGeneration",expectedLeaseGeneration); Add(command,"$share",entry.ShareId);
                Add(command,"$url",entry.ShareUrl); Add(command,"$code",entry.ExtractionCode);
                Add(command,"$permanent",entry.IsPermanent is null?null:entry.IsPermanent.Value?1:0);
                Add(command,"$unlimited",entry.IsAccessUnlimited is null?null:entry.IsAccessUnlimited.Value?1:0);
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1) continue;
                await using var existing = Command(connection, transaction, """
                    SELECT EXISTS(
                      SELECT 1 FROM ShareDiscoveryBaselines baseline
                      JOIN ShareOperations operation ON operation.Id=baseline.ShareOperationId
                      JOIN BatchShareSettings settings ON settings.BatchJobId=operation.BatchJobId
                        AND settings.SettingsFingerprint=operation.SettingsFingerprint
                      WHERE baseline.ShareOperationId=$operation AND baseline.Generation=$generation
                        AND baseline.ShareId=$share AND baseline.ShareUrl IS $url
                        AND baseline.ExtractionCode IS $code AND baseline.IsPermanent IS $permanent
                        AND baseline.IsAccessUnlimited IS $unlimited AND operation.Generation=$generation
                        AND operation.LeaseGeneration=$leaseGeneration);
                    """);
                Add(existing,"$operation",Id(shareOperationId)); Add(existing,"$generation",entry.Generation); Add(existing,"$leaseGeneration",expectedLeaseGeneration); Add(existing,"$share",entry.ShareId);
                Add(existing,"$url",entry.ShareUrl); Add(existing,"$code",entry.ExtractionCode);
                Add(existing,"$permanent",entry.IsPermanent is null?null:entry.IsPermanent.Value?1:0);
                Add(existing,"$unlimited",entry.IsAccessUnlimited is null?null:entry.IsAccessUnlimited.Value?1:0);
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
            SELECT ShareOperationId,ShareId,Generation,ShareUrl,ExtractionCode,IsPermanent,IsAccessUnlimited FROM ShareDiscoveryBaselines
            WHERE ShareOperationId=$operation
              AND Generation=(SELECT Generation FROM ShareOperations WHERE Id=$operation)
              AND ($after IS NULL OR ShareId>$after)
            ORDER BY ShareId LIMIT $limit;
            """);
        Add(command,"$operation",Id(shareOperationId)); Add(command,"$after",afterShareId); Add(command,"$limit",checked(pageSize+1));
        var values = new List<ShareDiscoveryBaselineRecord>(pageSize+1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) values.Add(new(
            Guid.Parse(reader.GetString(0)),reader.GetString(1),reader.GetInt32(2),
            reader.IsDBNull(3)?null:reader.GetString(3),reader.IsDBNull(4)?null:reader.GetString(4),
            reader.IsDBNull(5)?null:reader.GetInt64(5)!=0,reader.IsDBNull(6)?null:reader.GetInt64(6)!=0));
        var more = values.Count>pageSize; if (more) values.RemoveAt(values.Count-1);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(values,more ? values[^1].ShareId : null);
    }

    public async Task<bool> TryCompleteDiscoveryBaselineAsync(
        Guid shareOperationId, int expectedGeneration, long expectedLeaseGeneration, string baselineFingerprint,
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
                WHERE Id=$id AND Status IN ('Reserved','Preparing') AND Generation=$generation
                  AND LeaseGeneration=$leaseGeneration AND BaselineCompletedAt IS NULL
                  AND EXISTS(SELECT 1 FROM BatchShareSettings settings
                    WHERE settings.BatchJobId=ShareOperations.BatchJobId
                      AND settings.SettingsFingerprint=ShareOperations.SettingsFingerprint)
                  AND (SELECT COUNT(*) FROM ShareDiscoveryBaselines WHERE ShareOperationId=$id AND Generation=$generation)=$count;
                """);
            Add(command,"$generation",expectedGeneration); Add(command,"$fingerprint",baselineFingerprint);
            Add(command,"$leaseGeneration",expectedLeaseGeneration);
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
        Guid shareOperationId, int expectedGeneration, long expectedLeaseGeneration, string requestDescriptorJson,
        DateTimeOffset submittedAt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestDescriptorJson);
        return TryTransitionAsync("""
            UPDATE ShareOperations SET Status='Submitting',RequestDescriptorJson=$descriptor,
              AdapterRequestId=NULL,SubmissionAcceptance='Unknown',AttemptCount=AttemptCount+1,
              SubmitStartedAt=$updated,NextReconcileAt=NULL,UpdatedAt=$updated
            WHERE Id=$id AND Status='Preparing' AND Generation=$generation AND LeaseGeneration=$leaseGeneration
              AND BaselineGeneration=$generation AND BaselineCompletedAt IS NOT NULL
              AND RequestDescriptorJson IS NULL AND SubmitStartedAt IS NULL
              AND EXISTS(SELECT 1 FROM BatchShareSettings settings
                WHERE settings.BatchJobId=ShareOperations.BatchJobId
                  AND settings.SettingsFingerprint=ShareOperations.SettingsFingerprint)
              AND EXISTS(SELECT 1 FROM BatchJobs batch
                WHERE batch.Id=ShareOperations.BatchJobId AND batch.Status='Active')
              AND EXISTS(SELECT 1 FROM BatchOutputAccounts output
                WHERE output.BatchJobId=ShareOperations.BatchJobId
                  AND output.AccountId=ShareOperations.AccountId
                  AND output.OutputOrdinal=ShareOperations.OutputOrdinal AND output.ShareEnabled=1);
            """, shareOperationId, expectedGeneration, expectedLeaseGeneration, submittedAt,
            [("$descriptor", requestDescriptorJson)], cancellationToken);
    }

    public Task<bool> TryRecordAdapterRequestAsync(
        Guid shareOperationId, int expectedGeneration, long expectedLeaseGeneration, string adapterRequestId, DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterRequestId);
        return TryTransitionAsync("""
            UPDATE ShareOperations SET AdapterRequestId=$request,UpdatedAt=$updated
            WHERE Id=$id AND Status IN ('Submitting','ResultPending') AND Generation=$generation
              AND LeaseGeneration=$leaseGeneration
              AND RequestDescriptorJson IS NOT NULL AND SubmitStartedAt IS NOT NULL
              AND AdapterRequestId IS NULL
              AND EXISTS(SELECT 1 FROM BatchShareSettings settings
                WHERE settings.BatchJobId=ShareOperations.BatchJobId
                  AND settings.SettingsFingerprint=ShareOperations.SettingsFingerprint);
            """, shareOperationId, expectedGeneration, expectedLeaseGeneration, updatedAt,
            [("$request", adapterRequestId)], cancellationToken);
    }

    public Task<bool> TryMarkResultPendingAsync(
        Guid shareOperationId, int expectedGeneration, long expectedLeaseGeneration, DateTimeOffset nextReconcileAt, DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        if (nextReconcileAt < updatedAt) throw new ArgumentOutOfRangeException(nameof(nextReconcileAt));
        return TryTransitionAsync("""
            UPDATE ShareOperations SET Status='ResultPending',SubmissionAcceptance='Unknown',LeaseExpiresAt=NULL,
              NextReconcileAt=$reconcile,UpdatedAt=$updated
            WHERE Id=$id AND Status='Submitting' AND Generation=$generation AND LeaseGeneration=$leaseGeneration
              AND RequestDescriptorJson IS NOT NULL AND SubmitStartedAt IS NOT NULL
              AND EXISTS(SELECT 1 FROM BatchShareSettings settings
                WHERE settings.BatchJobId=ShareOperations.BatchJobId
                  AND settings.SettingsFingerprint=ShareOperations.SettingsFingerprint);
            """, shareOperationId, expectedGeneration, expectedLeaseGeneration, updatedAt,
            [("$reconcile", Time(nextReconcileAt))], cancellationToken);
    }

    public Task<bool> TryStartNextGenerationAsync(
        Guid shareOperationId, ShareOperationStatus expectedStatus, int expectedGeneration, long expectedLeaseGeneration, DateTimeOffset leaseExpiresAt,
        DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
    {
        if (expectedStatus is not (ShareOperationStatus.Succeeded or ShareOperationStatus.Failed or ShareOperationStatus.ReusedExisting))
            throw new ArgumentException("Only terminal share work can start another generation.", nameof(expectedStatus));
        if (leaseExpiresAt <= updatedAt) throw new ArgumentOutOfRangeException(nameof(leaseExpiresAt));
        return TryTransitionAsync("""
            UPDATE ShareOperations SET Status='Reserved',Generation=Generation+1,LeaseGeneration=LeaseGeneration+1,RequestDescriptorJson=NULL,
              AdapterRequestId=NULL,SubmissionAcceptance='Unknown',LeaseExpiresAt=$lease,SubmitStartedAt=NULL,
              NextReconcileAt=NULL,BaselineGeneration=NULL,BaselineFingerprint=NULL,BaselineShareCount=0,
              BaselineCompletedAt=NULL,LastErrorCode=NULL,LastErrorMessage=NULL,UpdatedAt=$updated
            WHERE Id=$id AND Status=$status AND Generation=$generation AND LeaseGeneration=$leaseGeneration
              AND EXISTS(SELECT 1 FROM BatchShareSettings settings
                WHERE settings.BatchJobId=ShareOperations.BatchJobId
                  AND settings.SettingsFingerprint=ShareOperations.SettingsFingerprint);
            """, shareOperationId, expectedGeneration, expectedLeaseGeneration, updatedAt,
            [("$status", expectedStatus.ToString()), ("$lease", Time(leaseExpiresAt))], cancellationToken);
    }

    public async Task<IReadOnlyList<ShareDecisionAccountContext>> GetDecisionAccountsAsync(
        Guid batchJobId,
        CancellationToken cancellationToken = default)
    {
        if (batchJobId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(batchJobId));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var command = Command(connection, transaction, """
            SELECT output.BatchJobId,output.AccountId,
              settings.IsPermanent,settings.IsAccessUnlimited,settings.ExtractionCodeMode,
              settings.FixedExtractionCode,settings.ExistingSharePolicy,settings.CreatedAt,settings.SettingsFingerprint
            FROM BatchOutputAccounts output
            JOIN BatchShareSettings settings ON settings.BatchJobId=output.BatchJobId
            WHERE output.BatchJobId=$batch AND output.ShareEnabled=1
              AND EXISTS(SELECT 1 FROM ShareOperations operation
                WHERE operation.BatchJobId=output.BatchJobId AND operation.AccountId=output.AccountId
                  AND operation.SettingsFingerprint=settings.SettingsFingerprint
                  AND operation.Status IN ('WaitingShare','Reserved','Preparing','Paused')
                  AND operation.ExactItemSetFingerprint IS NOT NULL
                  AND EXISTS(SELECT 1 FROM ShareOperationGenerations lineage
                    WHERE lineage.ShareOperationId=operation.Id AND lineage.Generation=operation.Generation
                      AND lineage.ExactItemCount=(SELECT COUNT(*) FROM ShareOperationRemoteItems item
                        WHERE item.ShareOperationId=operation.Id)))
            ORDER BY output.OutputOrdinal,output.AccountId;
            """);
        Add(command, "$batch", Id(batchJobId));
        var values = new List<ShareDecisionAccountContext>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var batch = Guid.Parse(reader.GetString(0));
            var account = Guid.Parse(reader.GetString(1));
            values.Add(new(batch, account, new(
                batch, reader.GetInt64(2) != 0, reader.GetInt64(3) != 0, reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6),
                ReadTime(reader, 7), reader.GetString(8))));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return values;
    }

    public async Task<StringKeysetPage<string>> GetDecisionExactSetFingerprintsKeysetPageAsync(
        Guid batchJobId,
        Guid accountId,
        string? afterFingerprint,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (batchJobId == Guid.Empty || accountId == Guid.Empty)
            throw new ArgumentException("A batch and account are required.");
        if (pageSize is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var command = Command(connection, transaction, """
            SELECT DISTINCT operation.ExactItemSetFingerprint
            FROM ShareOperations operation
            JOIN BatchOutputAccounts output ON output.BatchJobId=operation.BatchJobId
              AND output.AccountId=operation.AccountId AND output.ShareEnabled=1
            JOIN ShareOperationGenerations lineage ON lineage.ShareOperationId=operation.Id
              AND lineage.Generation=operation.Generation
            WHERE operation.BatchJobId=$batch AND operation.AccountId=$account
              AND operation.Status IN ('WaitingShare','Reserved','Preparing','Paused')
              AND operation.ExactItemSetFingerprint IS NOT NULL
              AND lineage.ExactItemCount=(SELECT COUNT(*) FROM ShareOperationRemoteItems item
                WHERE item.ShareOperationId=operation.Id)
              AND ($after IS NULL OR operation.ExactItemSetFingerprint>$after)
            ORDER BY operation.ExactItemSetFingerprint LIMIT $limit;
            """);
        Add(command, "$batch", Id(batchJobId)); Add(command, "$account", Id(accountId));
        Add(command, "$after", afterFingerprint); Add(command, "$limit", checked(pageSize + 1));
        var values = new List<string>(pageSize + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) values.Add(reader.GetString(0));
        var more = values.Count > pageSize;
        if (more) values.RemoveAt(values.Count - 1);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(values, more ? values[^1] : null);
    }

    public async Task<bool> ContainsDecisionExactSetAsync(
        Guid batchJobId,
        Guid accountId,
        string exactItemSetFingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exactItemSetFingerprint);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM ShareOperations operation
              JOIN BatchOutputAccounts output ON output.BatchJobId=operation.BatchJobId
                AND output.AccountId=operation.AccountId AND output.ShareEnabled=1
              JOIN ShareOperationGenerations lineage ON lineage.ShareOperationId=operation.Id
                AND lineage.Generation=operation.Generation
              WHERE operation.BatchJobId=$batch AND operation.AccountId=$account
                AND operation.ExactItemSetFingerprint=$fingerprint
                AND operation.Status IN ('WaitingShare','Reserved','Preparing','Paused')
                AND lineage.ExactItemCount=(SELECT COUNT(*) FROM ShareOperationRemoteItems item
                  WHERE item.ShareOperationId=operation.Id));
            """;
        Add(command, "$batch", Id(batchJobId)); Add(command, "$account", Id(accountId));
        Add(command, "$fingerprint", exactItemSetFingerprint);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }

    public async Task<long> GetDecisionWorkVersionAsync(
        Guid batchJobId, Guid accountId, string settingsFingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsFingerprint);
        await using var connection=await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command=connection.CreateCommand();
        command.CommandText="""
            SELECT Version FROM BatchShareWorkVersions
            WHERE BatchJobId=$batch AND AccountId=$account AND SettingsFingerprint=$settings;
            """;
        Add(command,"$batch",Id(batchJobId));Add(command,"$account",Id(accountId));Add(command,"$settings",settingsFingerprint);
        var value=await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if(value is null||value is DBNull)throw new InvalidOperationException("The selected account has no exact share work.");
        return Convert.ToInt64(value,System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<long> BeginDiscoveryAsync(
        Guid runId,ShareDecisionAccountContext account,CancellationToken cancellationToken=default)
    {
        if(runId==Guid.Empty)throw new ArgumentException("A discovery run is required.",nameof(runId));
        ArgumentNullException.ThrowIfNull(account);
        await using var connection=await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction=connection.BeginTransaction(deferred:false);
        try
        {
            var now=DateTimeOffset.UtcNow;
            await using(var cleanup=Command(connection,transaction,"""
                DELETE FROM BatchShareDiscoveryRuns
                WHERE (RunId,AccountId) IN (
                  SELECT RunId,AccountId FROM BatchShareDiscoveryRuns
                  WHERE ExpiresAt<=$now ORDER BY ExpiresAt,RunId,AccountId LIMIT 100);
                """))
            {
                Add(cleanup,"$now",Time(now));await cleanup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using(var acquire=Command(connection,transaction,"""
                INSERT OR IGNORE INTO BatchShareDiscoveryRuns(
                  RunId,BatchJobId,AccountId,SettingsFingerprint,ExpectedWorkVersion,CreatedAt,ExpiresAt)
                SELECT $run,$batch,$account,$settings,version.Version,$now,$expires
                FROM BatchShareWorkVersions version
                WHERE version.BatchJobId=$batch AND version.AccountId=$account
                  AND version.SettingsFingerprint=$settings;
                """))
            {
                Add(acquire,"$run",Id(runId));Add(acquire,"$batch",Id(account.BatchJobId));
                Add(acquire,"$account",Id(account.AccountId));Add(acquire,"$settings",account.Settings.SettingsFingerprint);
                Add(acquire,"$now",Time(now));Add(acquire,"$expires",Time(now.AddHours(1)));
                await acquire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using var verify=Command(connection,transaction,"""
                SELECT ExpectedWorkVersion FROM BatchShareDiscoveryRuns
                WHERE RunId=$run AND BatchJobId=$batch AND AccountId=$account AND SettingsFingerprint=$settings;
                """);
            Add(verify,"$run",Id(runId));Add(verify,"$batch",Id(account.BatchJobId));
            Add(verify,"$account",Id(account.AccountId));Add(verify,"$settings",account.Settings.SettingsFingerprint);
            var value=await verify.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if(value is null||value is DBNull)
                throw new InvalidOperationException("Another share discovery is already active for this account.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt64(value,System.Globalization.CultureInfo.InvariantCulture);
        }
        catch{await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);throw;}
    }

    public async Task StageDiscoveryPageAsync(
        Guid runId,
        ShareDecisionAccountContext account,
        IReadOnlyList<BaiduShareRecord> candidates,
        CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty) throw new ArgumentException("A discovery run is required.", nameof(runId));
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(candidates);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var now=DateTimeOffset.UtcNow;
            await using (var verify = Command(connection,transaction,"""
                UPDATE BatchShareDiscoveryRuns SET ExpiresAt=$expires
                WHERE RunId=$run AND BatchJobId=$batch AND AccountId=$account AND SettingsFingerprint=$settings;
                """))
            {
                Add(verify,"$run",Id(runId));Add(verify,"$batch",Id(account.BatchJobId));
                Add(verify,"$account",Id(account.AccountId));Add(verify,"$settings",account.Settings.SettingsFingerprint);
                Add(verify,"$expires",Time(now.AddHours(1)));
                if(await verify.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false)!=1)
                    throw new InvalidOperationException("Another share discovery is already active for this account.");
            }
            foreach (var chunk in candidates.Chunk(40))
            {
                if (chunk.Length == 0) continue;
                var rows = new List<string>(chunk.Length);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                Add(command, "$run", Id(runId)); Add(command, "$batch", Id(account.BatchJobId));
                Add(command, "$account", Id(account.AccountId)); Add(command, "$settings", account.Settings.SettingsFingerprint);
                for (var index = 0; index < chunk.Length; index++)
                {
                    var share = chunk[index];
                    rows.Add($"($f{index},$id{index},$url{index},$code{index},$permanent{index},$unlimited{index},$compatible{index})");
                    Add(command, $"$f{index}", share.RemoteItems.Fingerprint); Add(command, $"$id{index}", share.ShareId);
                    Add(command, $"$url{index}", share.ShareUrl); Add(command, $"$code{index}", share.ExtractionCode);
                    Add(command, $"$permanent{index}", share.IsPermanent ? 1 : 0);
                    Add(command, $"$unlimited{index}", share.IsAccessUnlimited ? 1 : 0);
                    var compatible = share.IsPermanent && share.IsAccessUnlimited &&
                        (string.Equals(account.Settings.ExtractionCodeMode, "Random", StringComparison.Ordinal) ||
                         string.Equals(share.ExtractionCode, account.Settings.FixedExtractionCode, StringComparison.Ordinal));
                    Add(command, $"$compatible{index}", compatible ? 1 : 0);
                }
                command.CommandText = $"""
                    WITH candidate(Fingerprint,ShareId,ShareUrl,ExtractionCode,IsPermanent,IsUnlimited,IsCompatible) AS
                      (VALUES {string.Join(',', rows)})
                    INSERT OR IGNORE INTO BatchShareDiscoveryStaging(
                      RunId,BatchJobId,AccountId,SettingsFingerprint,ExactItemSetFingerprint,ShareId,
                      ShareUrl,ExtractionCode,IsPermanent,IsAccessUnlimited,IsCompatible)
                    SELECT $run,$batch,$account,$settings,candidate.Fingerprint,candidate.ShareId,
                      candidate.ShareUrl,candidate.ExtractionCode,candidate.IsPermanent,candidate.IsUnlimited,candidate.IsCompatible
                    FROM candidate
                    WHERE EXISTS(SELECT 1 FROM ShareOperations operation
                      JOIN BatchOutputAccounts output ON output.BatchJobId=operation.BatchJobId
                        AND output.AccountId=operation.AccountId AND output.ShareEnabled=1
                      WHERE operation.BatchJobId=$batch AND operation.AccountId=$account
                        AND operation.SettingsFingerprint=$settings
                        AND operation.ExactItemSetFingerprint=candidate.Fingerprint
                        AND operation.Status IN ('WaitingShare','Reserved','Preparing','Paused'));
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
    }

    public async Task<ExistingShareDiscoveryPage> GetStagedDiscoveryPageAsync(
        Guid runId, Guid accountId, string? afterCursor, int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty || accountId == Guid.Empty) throw new ArgumentException("A run and account are required.");
        if (pageSize is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var after = DecodeMemberCursor(afterCursor);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT BatchJobId,AccountId,SettingsFingerprint,ExactItemSetFingerprint,ShareId,ShareUrl,
              ExtractionCode,IsPermanent,IsAccessUnlimited,IsCompatible
            FROM BatchShareDiscoveryStaging
            WHERE RunId=$run AND AccountId=$account
              AND ($afterFingerprint IS NULL OR ExactItemSetFingerprint>$afterFingerprint OR
                (ExactItemSetFingerprint=$afterFingerprint AND ShareId>$afterShare))
            ORDER BY ExactItemSetFingerprint,ShareId LIMIT $limit;
            """;
        Add(command,"$run",Id(runId)); Add(command,"$account",Id(accountId));
        Add(command,"$afterFingerprint",after?.Fingerprint); Add(command,"$afterShare",after?.ShareId);
        Add(command,"$limit",checked(pageSize + 1));
        var values = new List<ExistingShareDiscoveryMember>(pageSize + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            values.Add(new(Guid.Parse(reader.GetString(0)),Guid.Parse(reader.GetString(1)),reader.GetString(2),string.Empty,
                reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetString(6),
                reader.GetInt64(7)!=0,reader.GetInt64(8)!=0,reader.GetInt64(9)!=0));
        var more = values.Count > pageSize;
        if (more) values.RemoveAt(values.Count - 1);
        var next = more ? EncodeMemberCursor(values[^1].ExactItemSetFingerprint, values[^1].ShareId) : null;
        return new(values, next);
    }

    public async Task AbortDiscoveryAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty) return;
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM BatchShareDiscoveryRuns WHERE RunId=$run;";
        Add(command,"$run",Id(runId));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TrySaveDiscoveryAndPauseAsync(
        Guid runId,
        IReadOnlyList<BatchShareDiscoverySnapshotRecord> snapshots,
        CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty) throw new ArgumentException("A discovery run is required.", nameof(runId));
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0 || snapshots.Select(value => (value.BatchJobId, value.AccountId)).Distinct().Count() != snapshots.Count)
            throw new ArgumentException("Discovery snapshot accounts must be non-empty and unique.", nameof(snapshots));
        foreach (var snapshot in snapshots) ValidateDiscoverySnapshot(snapshot);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            foreach (var snapshot in snapshots)
            {
                if (!await HasDiscoveryOwnershipAsync(connection,transaction,runId,snapshot,cancellationToken).ConfigureAwait(false) ||
                    !await HasExpectedWorkVersionAsync(connection,transaction,snapshot,cancellationToken).ConfigureAwait(false))
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return false;
                }
                if (await HasUnsafeDecisionWorkAsync(connection, transaction, snapshot, cancellationToken).ConfigureAwait(false))
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return false;
                }

                if (snapshot.Version > 1)
                {
                    await using var deleteDecision = Command(connection, transaction, """
                        DELETE FROM BatchShareDecisionSnapshots
                        WHERE BatchJobId=$batch AND AccountId=$account AND SettingsFingerprint=$settings
                          AND DiscoveryVersion=$previous;
                        """);
                    Add(deleteDecision, "$batch", Id(snapshot.BatchJobId)); Add(deleteDecision, "$account", Id(snapshot.AccountId));
                    Add(deleteDecision, "$settings", snapshot.SettingsFingerprint); Add(deleteDecision, "$previous", snapshot.Version - 1);
                    await deleteDecision.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    await using var deleteMembers = Command(connection, transaction, """
                        DELETE FROM BatchShareDiscoveryMembers
                        WHERE BatchJobId=$batch AND AccountId=$account AND SettingsFingerprint=$settings;
                        """);
                    Add(deleteMembers, "$batch", Id(snapshot.BatchJobId)); Add(deleteMembers, "$account", Id(snapshot.AccountId));
                    Add(deleteMembers, "$settings", snapshot.SettingsFingerprint);
                    await deleteMembers.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                var sql = snapshot.Version == 1
                    ? """
                      INSERT OR IGNORE INTO BatchShareDiscoverySnapshots(
                        BatchJobId,AccountId,SettingsFingerprint,DiscoveryFingerprint,DiscoveredCount,
                        CompatibleCount,IncompatibleCount,Generation,CompletedAt,Version)
                      SELECT $batch,$account,$settings,$fingerprint,$discovered,$compatible,$incompatible,$generation,$completed,$version
                      WHERE EXISTS(SELECT 1 FROM BatchOutputAccounts output
                        WHERE output.BatchJobId=$batch AND output.AccountId=$account AND output.ShareEnabled=1);
                      """
                    : """
                      UPDATE BatchShareDiscoverySnapshots
                      SET DiscoveryFingerprint=$fingerprint,DiscoveredCount=$discovered,CompatibleCount=$compatible,
                        IncompatibleCount=$incompatible,Generation=$generation,CompletedAt=$completed,Version=$version
                      WHERE BatchJobId=$batch AND AccountId=$account AND SettingsFingerprint=$settings
                        AND Version=$previous;
                      """;
                await using var save = Command(connection, transaction, sql);
                BindDiscovery(save, snapshot); Add(save, "$previous", snapshot.Version - 1);
                if (await save.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return false;
                }

                await using var copyMembers = Command(connection, transaction, """
                    INSERT INTO BatchShareDiscoveryMembers(
                      BatchJobId,AccountId,SettingsFingerprint,DiscoveryFingerprint,ExactItemSetFingerprint,
                      ShareId,ShareUrl,ExtractionCode,IsPermanent,IsAccessUnlimited,IsCompatible)
                    SELECT BatchJobId,AccountId,SettingsFingerprint,$fingerprint,ExactItemSetFingerprint,
                      ShareId,ShareUrl,ExtractionCode,IsPermanent,IsAccessUnlimited,IsCompatible
                    FROM BatchShareDiscoveryStaging
                    WHERE RunId=$run AND BatchJobId=$batch AND AccountId=$account AND SettingsFingerprint=$settings;
                    """);
                Add(copyMembers,"$run",Id(runId)); Add(copyMembers,"$batch",Id(snapshot.BatchJobId));
                Add(copyMembers,"$account",Id(snapshot.AccountId)); Add(copyMembers,"$settings",snapshot.SettingsFingerprint);
                Add(copyMembers,"$fingerprint",snapshot.DiscoveryFingerprint);
                if (await copyMembers.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != snapshot.DiscoveredCount)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return false;
                }
            }

            foreach (var snapshot in snapshots)
            {
                await using (var clearBaseline = Command(connection, transaction, """
                    DELETE FROM ShareDiscoveryBaselines
                    WHERE ShareOperationId IN (
                      SELECT Id FROM ShareOperations
                      WHERE BatchJobId=$batch AND AccountId=$account AND SettingsFingerprint=$settings
                        AND Status IN ('WaitingShare','Reserved','Preparing')
                        AND EXISTS(SELECT 1 FROM BatchShareDiscoveryMembers member
                          WHERE member.BatchJobId=ShareOperations.BatchJobId
                            AND member.AccountId=ShareOperations.AccountId
                            AND member.SettingsFingerprint=ShareOperations.SettingsFingerprint
                            AND member.ExactItemSetFingerprint=ShareOperations.ExactItemSetFingerprint));
                    """))
                {
                    Add(clearBaseline, "$batch", Id(snapshot.BatchJobId)); Add(clearBaseline, "$account", Id(snapshot.AccountId));
                    Add(clearBaseline, "$settings", snapshot.SettingsFingerprint);
                    await clearBaseline.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                await using var pause = Command(connection, transaction, """
                    UPDATE ShareOperations SET Status='Paused',LeaseGeneration=LeaseGeneration+1,LeaseExpiresAt=NULL,
                      BaselineGeneration=NULL,BaselineFingerprint=NULL,BaselineShareCount=0,BaselineCompletedAt=NULL,
                      LastErrorCode='AwaitingExistingShareDecision',
                      LastErrorMessage='等待确认已有分享',UpdatedAt=$updated
                    WHERE BatchJobId=$batch AND AccountId=$account AND SettingsFingerprint=$settings
                      AND Status IN ('WaitingShare','Reserved','Preparing')
                      AND EXISTS(SELECT 1 FROM BatchShareDiscoveryMembers member
                        WHERE member.BatchJobId=ShareOperations.BatchJobId
                          AND member.AccountId=ShareOperations.AccountId
                          AND member.SettingsFingerprint=ShareOperations.SettingsFingerprint
                          AND member.ExactItemSetFingerprint=ShareOperations.ExactItemSetFingerprint);
                    """);
                Add(pause, "$batch", Id(snapshot.BatchJobId)); Add(pause, "$account", Id(snapshot.AccountId));
                Add(pause, "$settings", snapshot.SettingsFingerprint); Add(pause, "$updated", Time(snapshot.CompletedAt));
                await pause.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var cleanup = Command(connection, transaction,
                "DELETE FROM BatchShareDiscoveryRuns WHERE RunId=$run;"))
            {
                Add(cleanup,"$run",Id(runId));
                await cleanup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
    }

    public async Task<bool> ApplyDecisionSnapshotsAsync(
        IReadOnlyList<BatchShareDecisionSnapshotRecord> snapshots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0) throw new ArgumentException("At least one decision is required.", nameof(snapshots));
        if (snapshots.Select(value => (value.BatchJobId, value.AccountId)).Distinct().Count() != snapshots.Count)
            throw new ArgumentException("Decision accounts must be unique.", nameof(snapshots));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            foreach (var snapshot in snapshots)
            {
                if(!await HasExpectedWorkVersionAsync(connection,transaction,snapshot,cancellationToken).ConfigureAwait(false))
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return false;
                }
                if (snapshot.Decision is not ("Reuse" or "Regenerate" or "Cancel") ||
                    snapshot.CompatibleCount + snapshot.IncompatibleCount > snapshot.DiscoveredCount ||
                    (snapshot.Decision == "Reuse" && snapshot.DiscoveredCount > 0 && snapshot.CompatibleCount == 0))
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return false;
                }
                await using var insert = Command(connection, transaction, """
                    INSERT INTO BatchShareDecisionSnapshots(BatchJobId,AccountId,SettingsFingerprint,Decision,
                      DiscoveryFingerprint,DiscoveredCount,CompatibleCount,IncompatibleCount,Generation,ConfirmedAt,DiscoveryVersion)
                    SELECT discovery.BatchJobId,discovery.AccountId,discovery.SettingsFingerprint,$decision,
                      discovery.DiscoveryFingerprint,discovery.DiscoveredCount,discovery.CompatibleCount,
                      discovery.IncompatibleCount,discovery.Generation,$confirmed,discovery.Version
                    FROM BatchShareDiscoverySnapshots discovery
                    WHERE discovery.BatchJobId=$batch AND discovery.AccountId=$account
                      AND discovery.SettingsFingerprint=$settings AND discovery.DiscoveryFingerprint=$fingerprint
                      AND discovery.DiscoveredCount=$discovered AND discovery.CompatibleCount=$compatible
                      AND discovery.IncompatibleCount=$incompatible AND discovery.Generation=$generation
                      AND discovery.Version=$version
                    ON CONFLICT(BatchJobId,AccountId) DO NOTHING;
                    """);
                BindDecision(insert, snapshot);
                if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    var persisted = await GetDecisionSnapshotAsync(
                        connection, transaction, snapshot.BatchJobId, snapshot.AccountId, cancellationToken).ConfigureAwait(false);
                    if (persisted != snapshot with { ConfirmedAt = snapshot.ConfirmedAt.ToUniversalTime() })
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                        return false;
                    }
                }
            }

            foreach (var snapshot in snapshots.Where(value => value.Decision != "Cancel"))
            {
                await using var resume = Command(connection, transaction, """
                    UPDATE ShareOperations SET Status='WaitingShare',LeaseExpiresAt=NULL,
                      LastErrorCode=NULL,LastErrorMessage=NULL,UpdatedAt=$updated
                    WHERE BatchJobId=$batch AND AccountId=$account AND SettingsFingerprint=$settings
                      AND Status='Paused' AND LastErrorCode='AwaitingExistingShareDecision'
                      AND EXISTS(SELECT 1 FROM BatchShareDiscoveryMembers member
                        WHERE member.BatchJobId=ShareOperations.BatchJobId
                          AND member.AccountId=ShareOperations.AccountId
                          AND member.SettingsFingerprint=ShareOperations.SettingsFingerprint
                          AND member.ExactItemSetFingerprint=ShareOperations.ExactItemSetFingerprint);
                    """);
                Add(resume, "$batch", Id(snapshot.BatchJobId)); Add(resume, "$account", Id(snapshot.AccountId));
                Add(resume, "$settings", snapshot.SettingsFingerprint); Add(resume, "$updated", Time(snapshot.ConfirmedAt));
                await resume.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SqliteException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return false;
        }
        catch { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
    }

    public async Task<bool> TrySaveDiscoverySnapshotAsync(
        BatchShareDiscoverySnapshotRecord snapshot,
        CancellationToken cancellationToken = default)
    {
        ValidateDiscoverySnapshot(snapshot);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var sql = snapshot.Version == 1
                ? """
                  INSERT OR IGNORE INTO BatchShareDiscoverySnapshots(
                    BatchJobId,AccountId,SettingsFingerprint,DiscoveryFingerprint,DiscoveredCount,
                    CompatibleCount,IncompatibleCount,Generation,CompletedAt,Version)
                  SELECT $batch,$account,$settings,$fingerprint,$discovered,$compatible,$incompatible,$generation,$completed,$version
                  WHERE EXISTS(SELECT 1 FROM ShareOperations operation
                    WHERE operation.BatchJobId=$batch AND operation.AccountId=$account
                      AND operation.SettingsFingerprint=$settings);
                  """
                : """
                  UPDATE BatchShareDiscoverySnapshots
                  SET DiscoveryFingerprint=$fingerprint,DiscoveredCount=$discovered,CompatibleCount=$compatible,
                    IncompatibleCount=$incompatible,Generation=$generation,CompletedAt=$completed,Version=$version
                  WHERE BatchJobId=$batch AND AccountId=$account AND SettingsFingerprint=$settings
                    AND Version=$previous;
                  """;
            await using var command = Command(connection, transaction, sql);
            BindDiscovery(command, snapshot);
            Add(command, "$previous", snapshot.Version - 1);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }

            var existing = await GetDiscoverySnapshotAsync(
                connection, transaction, snapshot.BatchJobId, snapshot.AccountId,
                snapshot.SettingsFingerprint, cancellationToken).ConfigureAwait(false);
            var replay = existing == snapshot with { CompletedAt = snapshot.CompletedAt.ToUniversalTime() };
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return replay;
        }
        catch { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
    }

    public async Task<BatchShareDiscoverySnapshotRecord?> GetDiscoverySnapshotAsync(
        Guid batchJobId,
        Guid accountId,
        string settingsFingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsFingerprint);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var value = await GetDiscoverySnapshotAsync(
            connection, transaction, batchJobId, accountId, settingsFingerprint, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return value;
    }

    public async Task<bool> SaveDecisionSnapshotAsync(BatchShareDecisionSnapshotRecord snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.SettingsFingerprint) ||
            snapshot.Decision is not ("Reuse" or "Regenerate" or "Cancel") || snapshot.DiscoveredCount < 0 ||
            snapshot.CompatibleCount < 0 || snapshot.IncompatibleCount < 0 ||
            snapshot.DiscoveryVersion < 1 ||
            snapshot.CompatibleCount + snapshot.IncompatibleCount > snapshot.DiscoveredCount)
            throw new ArgumentException("The share decision snapshot is invalid.",nameof(snapshot));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred:false);
        try
        {
            await using var command = Command(connection,transaction,"""
                INSERT OR IGNORE INTO BatchShareDecisionSnapshots(BatchJobId,AccountId,SettingsFingerprint,Decision,DiscoveryFingerprint,DiscoveredCount,
                  CompatibleCount,IncompatibleCount,Generation,ConfirmedAt,DiscoveryVersion)
                SELECT discovery.BatchJobId,discovery.AccountId,discovery.SettingsFingerprint,$decision,
                  discovery.DiscoveryFingerprint,discovery.DiscoveredCount,discovery.CompatibleCount,
                  discovery.IncompatibleCount,discovery.Generation,$confirmed,discovery.Version
                FROM BatchShareDiscoverySnapshots discovery
                WHERE discovery.BatchJobId=$batch AND discovery.AccountId=$account
                  AND discovery.SettingsFingerprint=$settings AND discovery.DiscoveryFingerprint=$fingerprint
                  AND discovery.DiscoveredCount=$discovered AND discovery.CompatibleCount=$compatible
                  AND discovery.IncompatibleCount=$incompatible AND discovery.Generation=$generation
                  AND discovery.Version=$version;
                """);
            Add(command,"$batch",Id(snapshot.BatchJobId)); Add(command,"$account",Id(snapshot.AccountId));
            Add(command,"$settings",snapshot.SettingsFingerprint);
            Add(command,"$decision",snapshot.Decision); Add(command,"$fingerprint",snapshot.DiscoveryFingerprint);
            Add(command,"$discovered",snapshot.DiscoveredCount); Add(command,"$compatible",snapshot.CompatibleCount);
            Add(command,"$incompatible",snapshot.IncompatibleCount); Add(command,"$generation",snapshot.Generation);
            Add(command,"$confirmed",Time(snapshot.ConfirmedAt));
            Add(command,"$version",snapshot.DiscoveryVersion);
            var inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
            var persisted = inserted
                ? snapshot with { ConfirmedAt = snapshot.ConfirmedAt.ToUniversalTime() }
                : await GetDecisionSnapshotAsync(
                    connection, transaction, snapshot.BatchJobId, snapshot.AccountId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return persisted == snapshot with { ConfirmedAt = snapshot.ConfirmedAt.ToUniversalTime() };
        }
        catch { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
    }

    public async Task<BatchShareDecisionSnapshotRecord?> GetDecisionSnapshotAsync(
        Guid batchJobId, Guid accountId, CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred:true);
        var value = await GetDecisionSnapshotAsync(
            connection, transaction, batchJobId, accountId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return value;
    }

    public Task<bool> AcceptResultAsync(
        Guid shareOperationId, ShareOperationStatus expectedStatus, long expectedLeaseGeneration, BaiduShareResultRecord result, DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        if (expectedStatus is not (ShareOperationStatus.Submitting or ShareOperationStatus.ResultPending))
            return Task.FromResult(false);
        return PersistResultAsync(shareOperationId, expectedStatus.ToString(), expectedLeaseGeneration, "Succeeded", result,
            baselineMustContainShare: false, updatedAt, cancellationToken);
    }

    public Task<bool> ReuseExistingResultAsync(
        Guid shareOperationId, long expectedLeaseGeneration, BaiduShareResultRecord result, DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        PersistResultAsync(shareOperationId, "Preparing", expectedLeaseGeneration, "ReusedExisting", result,
            baselineMustContainShare: true, updatedAt, cancellationToken);

    private async Task<bool> PersistResultAsync(
        Guid shareOperationId, string expectedStatus, long expectedLeaseGeneration, string finalStatus, BaiduShareResultRecord result,
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
                  AND Generation=$generation AND LeaseGeneration=$leaseGeneration
                  AND BaselineGeneration=$generation AND BaselineCompletedAt IS NOT NULL
                  AND ($requestRequired=0 OR AdapterRequestId IS NOT NULL)
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
                Add(update,"$leaseGeneration",expectedLeaseGeneration);
                Add(update,"$requestRequired",string.Equals(expectedStatus, "Submitting", StringComparison.Ordinal) ? 1 : 0);
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
                INSERT INTO BaiduShareResults(Id,ShareOperationId,BatchJobId,ResourceItemId,AccountId,CandidateSequence,OutputOrdinal,
                  ExactItemSetFingerprint,SettingsFingerprint,Generation,ShareId,ShareUrl,ExtractionCode,IsAccepted,IsCurrent,SharedAt,UpdatedAt)
                SELECT $id,$operation,$batch,$resource,$account,resource.CandidateSequence,output.OutputOrdinal,
                  $items,$settings,$generation,$share,$url,$code,$accepted,$current,$shared,$updated
                FROM ResourceItems resource
                JOIN BatchOutputAccounts output ON output.BatchJobId=$batch AND output.AccountId=$account
                WHERE resource.Id=$resource AND resource.BatchJobId=$batch;
                """);
            Add(insert,"$id",Id(result.Id)); Add(insert,"$operation",Id(result.ShareOperationId)); Add(insert,"$batch",Id(result.BatchJobId));
            Add(insert,"$resource",Id(result.ResourceItemId)); Add(insert,"$account",Id(result.AccountId));
            Add(insert,"$items",result.ExactItemSetFingerprint); Add(insert,"$settings",result.SettingsFingerprint);
            Add(insert,"$generation",result.Generation); Add(insert,"$share",result.ShareId); Add(insert,"$url",result.ShareUrl);
            Add(insert,"$code",result.ExtractionCode); Add(insert,"$accepted",result.IsAccepted?1:0); Add(insert,"$current",result.IsCurrent?1:0);
            Add(insert,"$shared",Time(result.SharedAt)); Add(insert,"$updated",Time(result.UpdatedAt));
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return false;
            }
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
        await using var command = Command(connection,transaction,ResultKeysetSelectSql);
        Add(command,"$batch",Id(query.BatchJobId)); Add(command,"$afterSequence",query.After?.CandidateSequence??0L);
        Add(command,"$afterOutput",query.After?.OutputOrdinal??0);
        Add(command,"$afterResult",query.After is null?string.Empty:Id(query.After.ResultId)); Add(command,"$limit",checked(query.PageSize+1));
        var values = new List<(BaiduShareResultRecord Result,long Sequence,int OutputOrdinal)>(query.PageSize+1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) values.Add((ReadResult(reader),reader.GetInt64(15),reader.GetInt32(16)));
        var more=values.Count>query.PageSize; if(more)values.RemoveAt(values.Count-1);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(values.Select(value=>value.Result).ToArray(),more?new(values[^1].Sequence,values[^1].OutputOrdinal,values[^1].Result.Id):null);
    }

    private const string ResultColumns = "SELECT BaiduShareResults.Id,BaiduShareResults.ShareOperationId,BaiduShareResults.BatchJobId,BaiduShareResults.ResourceItemId,BaiduShareResults.AccountId,BaiduShareResults.ExactItemSetFingerprint,BaiduShareResults.SettingsFingerprint,BaiduShareResults.Generation,BaiduShareResults.ShareId,BaiduShareResults.ShareUrl,BaiduShareResults.ExtractionCode,BaiduShareResults.IsAccepted,BaiduShareResults.IsCurrent,BaiduShareResults.SharedAt,BaiduShareResults.UpdatedAt";
    private const string ResultSelect = ResultColumns + " FROM BaiduShareResults";

    private static BaiduShareResultRecord ReadResult(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),Guid.Parse(reader.GetString(1)),Guid.Parse(reader.GetString(2)),Guid.Parse(reader.GetString(3)),Guid.Parse(reader.GetString(4)),
        reader.GetString(5),reader.GetString(6),reader.GetInt32(7),reader.GetString(8),reader.GetString(9),reader.GetString(10),reader.GetInt64(11)!=0,reader.GetInt64(12)!=0,ReadTime(reader,13),ReadTime(reader,14));

    private static ShareOperationRecord ReadOperation(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),Guid.Parse(reader.GetString(1)),Guid.Parse(reader.GetString(2)),Guid.Parse(reader.GetString(3)),Guid.Parse(reader.GetString(4)),
        reader.GetInt32(5),reader.GetString(6),reader.GetString(7),reader.GetString(8),reader.GetInt32(9),
        reader.IsDBNull(10)?null:reader.GetString(10),reader.IsDBNull(11)?null:reader.GetString(11),reader.GetString(12),reader.GetInt32(13),
        ReadNullableTime(reader,14),ReadNullableTime(reader,15),ReadNullableTime(reader,16),reader.IsDBNull(17)?null:reader.GetString(17),
        reader.IsDBNull(18)?null:reader.GetString(18),ReadTime(reader,19),ReadTime(reader,20),reader.GetInt64(21));

    private static async Task<ShareOperationRecord> GetOperationAsync(SqliteConnection connection, SqliteTransaction transaction, string id, CancellationToken token)
    {
        await using var command = Command(connection,transaction,"SELECT Id,TransferOperationId,ResourceItemId,BatchJobId,AccountId,OutputOrdinal,Status,ExactItemSetFingerprint,SettingsFingerprint,Generation,RequestDescriptorJson,AdapterRequestId,SubmissionAcceptance,AttemptCount,LeaseExpiresAt,SubmitStartedAt,NextReconcileAt,LastErrorCode,LastErrorMessage,CreatedAt,UpdatedAt,LeaseGeneration FROM ShareOperations WHERE Id=$id;");
        Add(command,"$id",id); await using var reader=await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if(!await reader.ReadAsync(token).ConfigureAwait(false))throw new InvalidDataException("Reserved share work disappeared.");
        return ReadOperation(reader);
    }

    private static async Task<bool> HasUnsafeDecisionWorkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BatchShareDiscoverySnapshotRecord snapshot,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM ShareOperations
              WHERE BatchJobId=$batch AND AccountId=$account AND SettingsFingerprint=$settings
                AND Status IN ('Submitting','ResultPending'));
            """);
        Add(command, "$batch", Id(snapshot.BatchJobId)); Add(command, "$account", Id(snapshot.AccountId));
        Add(command, "$settings", snapshot.SettingsFingerprint);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }

    private static async Task<bool> HasDiscoveryOwnershipAsync(
        SqliteConnection connection,SqliteTransaction transaction,Guid runId,
        BatchShareDiscoverySnapshotRecord snapshot,CancellationToken cancellationToken)
    {
        await using var command=Command(connection,transaction,"""
            SELECT EXISTS(SELECT 1 FROM BatchShareDiscoveryRuns
              WHERE RunId=$run AND BatchJobId=$batch AND AccountId=$account
                AND SettingsFingerprint=$settings AND ExpectedWorkVersion=$version AND ExpiresAt>$now);
            """);
        Add(command,"$run",Id(runId));Add(command,"$batch",Id(snapshot.BatchJobId));Add(command,"$account",Id(snapshot.AccountId));
        Add(command,"$settings",snapshot.SettingsFingerprint);Add(command,"$version",ParseWorkVersion(snapshot.Generation));
        Add(command,"$now",Time(DateTimeOffset.UtcNow));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))==1;
    }

    private static async Task<bool> HasExpectedWorkVersionAsync(
        SqliteConnection connection,SqliteTransaction transaction,
        BatchShareDiscoverySnapshotRecord snapshot,CancellationToken cancellationToken)
        =>await HasExpectedWorkVersionAsync(connection,transaction,snapshot.BatchJobId,snapshot.AccountId,
            snapshot.SettingsFingerprint,snapshot.Generation,cancellationToken).ConfigureAwait(false);

    private static async Task<bool> HasExpectedWorkVersionAsync(
        SqliteConnection connection,SqliteTransaction transaction,
        BatchShareDecisionSnapshotRecord snapshot,CancellationToken cancellationToken)
        =>await HasExpectedWorkVersionAsync(connection,transaction,snapshot.BatchJobId,snapshot.AccountId,
            snapshot.SettingsFingerprint,snapshot.Generation,cancellationToken).ConfigureAwait(false);

    private static async Task<bool> HasExpectedWorkVersionAsync(
        SqliteConnection connection,SqliteTransaction transaction,Guid batchJobId,Guid accountId,
        string settingsFingerprint,string generation,CancellationToken cancellationToken)
    {
        var expected=ParseWorkVersion(generation);
        await using var command=Command(connection,transaction,"""
            SELECT EXISTS(SELECT 1 FROM BatchShareWorkVersions
              WHERE BatchJobId=$batch AND AccountId=$account AND SettingsFingerprint=$settings AND Version=$version);
            """);
        Add(command,"$batch",Id(batchJobId));Add(command,"$account",Id(accountId));
        Add(command,"$settings",settingsFingerprint);Add(command,"$version",expected);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))==1;
    }

    private static long ParseWorkVersion(string generation)
    {
        var separator=generation.IndexOf(':');
        if(separator<=0||!long.TryParse(generation.AsSpan(0,separator),System.Globalization.NumberStyles.None,
               System.Globalization.CultureInfo.InvariantCulture,out var version)||version<1)
            throw new ArgumentException("The discovery work generation is invalid.",nameof(generation));
        return version;
    }

    private static void BindDecision(SqliteCommand command, BatchShareDecisionSnapshotRecord snapshot)
    {
        Add(command, "$batch", Id(snapshot.BatchJobId)); Add(command, "$account", Id(snapshot.AccountId));
        Add(command, "$settings", snapshot.SettingsFingerprint); Add(command, "$decision", snapshot.Decision);
        Add(command, "$fingerprint", snapshot.DiscoveryFingerprint); Add(command, "$discovered", snapshot.DiscoveredCount);
        Add(command, "$compatible", snapshot.CompatibleCount); Add(command, "$incompatible", snapshot.IncompatibleCount);
        Add(command, "$generation", snapshot.Generation); Add(command, "$confirmed", Time(snapshot.ConfirmedAt));
        Add(command, "$version", snapshot.DiscoveryVersion);
    }

    private static string SafePauseMessage(SharePauseReason reason) => reason switch
    {
        SharePauseReason.AwaitingExistingShareDecision => "等待确认已有分享",
        SharePauseReason.IncompatibleExistingShare => "已有分享设置不兼容",
        SharePauseReason.CancelledByUser => "用户取消分享",
        SharePauseReason.AccountSessionUnavailable => "账号登录已失效",
        SharePauseReason.AccountVerificationRequired => "账号需要安全验证",
        SharePauseReason.ShareSettingsRejected => "分享设置不可用",
        _ => "分享已暂停",
    };

    private async Task<bool> TryTransitionAsync(
        string sql, Guid shareOperationId, int expectedGeneration, long expectedLeaseGeneration, DateTimeOffset updatedAt,
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
            Add(command,"$leaseGeneration",expectedLeaseGeneration);
            Add(command,"$updated",Time(updatedAt));
            foreach (var parameter in additionalParameters) Add(command,parameter.Name,parameter.Value);
            var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return changed;
        }
        catch { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
    }

    private static void ValidateDiscoverySnapshot(BatchShareDiscoverySnapshotRecord snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.BatchJobId == Guid.Empty || snapshot.AccountId == Guid.Empty ||
            string.IsNullOrWhiteSpace(snapshot.SettingsFingerprint) ||
            string.IsNullOrWhiteSpace(snapshot.DiscoveryFingerprint) ||
            string.IsNullOrWhiteSpace(snapshot.Generation) || snapshot.Version < 1 ||
            snapshot.DiscoveredCount < 0 || snapshot.CompatibleCount < 0 || snapshot.IncompatibleCount < 0 ||
            snapshot.CompatibleCount + snapshot.IncompatibleCount > snapshot.DiscoveredCount)
            throw new ArgumentException("The durable discovery snapshot is invalid.", nameof(snapshot));
    }

    private static void BindDiscovery(SqliteCommand command, BatchShareDiscoverySnapshotRecord snapshot)
    {
        Add(command, "$batch", Id(snapshot.BatchJobId));
        Add(command, "$account", Id(snapshot.AccountId));
        Add(command, "$settings", snapshot.SettingsFingerprint);
        Add(command, "$fingerprint", snapshot.DiscoveryFingerprint);
        Add(command, "$discovered", snapshot.DiscoveredCount);
        Add(command, "$compatible", snapshot.CompatibleCount);
        Add(command, "$incompatible", snapshot.IncompatibleCount);
        Add(command, "$generation", snapshot.Generation);
        Add(command, "$completed", Time(snapshot.CompletedAt));
        Add(command, "$version", snapshot.Version);
    }

    private static async Task<BatchShareDiscoverySnapshotRecord?> GetDiscoverySnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid batchJobId,
        Guid accountId,
        string settingsFingerprint,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT BatchJobId,AccountId,SettingsFingerprint,DiscoveryFingerprint,DiscoveredCount,
              CompatibleCount,IncompatibleCount,Generation,CompletedAt,Version
            FROM BatchShareDiscoverySnapshots
            WHERE BatchJobId=$batch AND AccountId=$account AND SettingsFingerprint=$settings;
            """);
        Add(command, "$batch", Id(batchJobId));
        Add(command, "$account", Id(accountId));
        Add(command, "$settings", settingsFingerprint);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2),
                reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6),
                reader.GetString(7), ReadTime(reader, 8), reader.GetInt64(9))
            : null;
    }

    private static async Task<BatchShareDecisionSnapshotRecord?> GetDecisionSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid batchJobId,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection,transaction,"SELECT BatchJobId,AccountId,SettingsFingerprint,Decision,DiscoveryFingerprint,DiscoveredCount,CompatibleCount,IncompatibleCount,Generation,ConfirmedAt,DiscoveryVersion FROM BatchShareDecisionSnapshots WHERE BatchJobId=$batch AND AccountId=$account;");
        Add(command,"$batch",Id(batchJobId)); Add(command,"$account",Id(accountId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new BatchShareDecisionSnapshotRecord(Guid.Parse(reader.GetString(0)),Guid.Parse(reader.GetString(1)),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetInt32(5),reader.GetInt32(6),reader.GetInt32(7),reader.GetString(8),ReadTime(reader,9),reader.GetInt64(10))
            : null;
    }

    private static void ValidatePage(Guid id,int pageSize){if(id==Guid.Empty)throw new ArgumentException("An operation is required.",nameof(id));if(pageSize is <1 or >1000)throw new ArgumentOutOfRangeException(nameof(pageSize));}
    private static SqliteCommand Command(SqliteConnection connection,SqliteTransaction transaction,string sql){var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText=sql;return command;}
    private static void Add(SqliteCommand command,string name,object? value)=>command.Parameters.AddWithValue(name,value??DBNull.Value);
    private static string Id(Guid value)=>value.ToString("D");
    private static string Time(DateTimeOffset value)=>value.ToUniversalTime().ToString("O");
    private static DateTimeOffset ReadTime(SqliteDataReader reader,int ordinal)=>DateTimeOffset.Parse(reader.GetString(ordinal),System.Globalization.CultureInfo.InvariantCulture);
    private static DateTimeOffset? ReadNullableTime(SqliteDataReader reader,int ordinal)=>reader.IsDBNull(ordinal)?null:ReadTime(reader,ordinal);
    private static string EncodeMemberCursor(string fingerprint,string shareId)=>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(fingerprint+'\n'+shareId));
    private static (string Fingerprint,string ShareId)? DecodeMemberCursor(string? cursor)
    {
        if (cursor is null) return null;
        try
        {
            var value=Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var separator=value.IndexOf('\n');
            if(separator<=0||separator==value.Length-1)throw new FormatException();
            return(value[..separator],value[(separator+1)..]);
        }
        catch(FormatException){throw new ArgumentException("The discovery cursor is invalid.",nameof(cursor));}
    }
}
