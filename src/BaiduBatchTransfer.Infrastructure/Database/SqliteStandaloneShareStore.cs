using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed class SqliteStandaloneShareStore(SqliteConnectionFactory factory) : IStandaloneShareExecutionStore
{
    private const int MaximumOperationsPerBatch = 10_000;
    private readonly SqliteConnectionFactory factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async Task<StandaloneShareBatchCreationReceipt> CreateOrGetBatchAsync(
        StandaloneSharePendingBatchEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Operations.Length > MaximumOperationsPerBatch)
            throw new ArgumentOutOfRangeException(nameof(envelope), "STANDALONE_SHARE_BATCH_TOO_LARGE");
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var existing = await LoadReceiptAsync(
            connection, transaction, envelope.Batch.Id, envelope.DraftFingerprint, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }
        if (await BatchExistsAsync(connection, transaction, envelope.Batch.Id, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("STANDALONE_SHARE_IDEMPOTENCY_CONFLICT");

        await InsertBatchAsync(connection, transaction, envelope, cancellationToken).ConfigureAwait(false);
        foreach (var operation in envelope.Operations)
            await InsertOperationAsync(connection, transaction, operation, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(envelope.DraftFingerprint, envelope.Batch, envelope.Operations, false);
    }

    public async Task<StandaloneShareBatchCreationReceipt?> GetBatchCreationAsync(
        Guid batchId,
        string draftFingerprint,
        CancellationToken cancellationToken = default)
    {
        ValidateBatchId(batchId);
        ValidateFingerprint(draftFingerprint, nameof(draftFingerprint));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadReceiptAsync(connection, null, batchId, draftFingerprint, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Page<StandaloneShareBatchRecord>> GetBatchesAsync(
        PageQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        long total;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM StandaloneShareBatches;";
            total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }
        var items = new List<StandaloneShareBatchRecord>(query.PageSize);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id,ResultDirectoryReference,Status,OperationCount,Revision,CreatedAt,UpdatedAt
            FROM StandaloneShareBatches
            ORDER BY CreatedAt DESC,Id DESC LIMIT $take OFFSET $skip;
            """;
        command.Parameters.AddWithValue("$take", query.PageSize);
        command.Parameters.AddWithValue("$skip", query.Offset);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) items.Add(ReadBatch(reader));
        return new(items, total, query.PageNumber, query.PageSize);
    }

    public async Task<IReadOnlyList<StandaloneShareOperationRecord>> GetOperationsAsync(
        Guid batchId,
        CancellationToken cancellationToken = default)
    {
        ValidateBatchId(batchId);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadOperationsAsync(connection, null, batchId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StandaloneShareBatchSnapshot?> GetBatchSnapshotAsync(
        Guid batchId,
        CancellationToken cancellationToken = default)
    {
        ValidateBatchId(batchId);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var batch = await LoadBatchAsync(connection, null, batchId, cancellationToken).ConfigureAwait(false);
        if (batch is null) return null;
        return new(batch, await LoadOperationsAsync(connection, null, batchId, cancellationToken).ConfigureAwait(false));
    }

    public Task<StandaloneShareMutationResult<StandaloneShareBatchRecord>> TryPauseBatchAsync(
        Guid batchId,
        long expectedRevision,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        MutateBatchAsync(batchId, expectedRevision,
            [StandaloneShareBatchStatus.Queued, StandaloneShareBatchStatus.Active],
            StandaloneShareBatchStatus.Paused, updatedAt, cancellationToken);

    public Task<StandaloneShareMutationResult<StandaloneShareBatchRecord>> TryResumeBatchAsync(
        Guid batchId,
        long expectedRevision,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        MutateBatchAsync(batchId, expectedRevision,
            [StandaloneShareBatchStatus.Paused], StandaloneShareBatchStatus.Queued,
            updatedAt, cancellationToken);

    public async Task<StandaloneShareMutationResult<StandaloneShareOperationRecord>> TryRetryFailedOperationAsync(
        Guid operationId,
        int expectedGeneration,
        long expectedRevision,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation is required.", nameof(operationId));
        if (expectedGeneration < 1) throw new ArgumentOutOfRangeException(nameof(expectedGeneration));
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await LoadOperationAsync(connection, transaction, operationId, cancellationToken).ConfigureAwait(false);
        if (current is null) return await RollbackResultAsync<StandaloneShareOperationRecord>(
            transaction, StandaloneShareMutationDisposition.NotFound, cancellationToken).ConfigureAwait(false);
        if (current.Generation != expectedGeneration || current.Revision != expectedRevision)
            return await RollbackResultAsync<StandaloneShareOperationRecord>(
                transaction, StandaloneShareMutationDisposition.Conflict, cancellationToken).ConfigureAwait(false);
        if (!current.CanSafelyRetry)
            return await RollbackResultAsync<StandaloneShareOperationRecord>(
                transaction, StandaloneShareMutationDisposition.Unsafe, cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE StandaloneShareOperations
                SET Generation=Generation+1,Status='Queued',SubmissionState='NotStarted',SubmissionAcceptance='Unknown',
                    BaselineFingerprint=NULL,BaselineShareCount=NULL,BaselineCompletedAt=NULL,
                    LeaseExpiresAt=NULL,LastErrorCode=NULL,Revision=Revision+1,UpdatedAt=$updated
                WHERE Id=$id AND Generation=$generation AND Revision=$revision AND Status='Failed'
                  AND SubmissionState IN ('NotStarted','DefinitelyNotAccepted');
                """;
            command.Parameters.AddWithValue("$id", operationId.ToString("D"));
            command.Parameters.AddWithValue("$generation", expectedGeneration);
            command.Parameters.AddWithValue("$revision", expectedRevision);
            command.Parameters.AddWithValue("$updated", updatedAt.ToUniversalTime().ToString("O"));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                return await RollbackResultAsync<StandaloneShareOperationRecord>(
                    transaction, StandaloneShareMutationDisposition.Conflict, cancellationToken).ConfigureAwait(false);
        }
        await using (var batch = connection.CreateCommand())
        {
            batch.Transaction = transaction;
            batch.CommandText = """
                UPDATE StandaloneShareBatches SET Status='Queued',Revision=Revision+1,UpdatedAt=$updated
                WHERE Id=$batch AND Status='CompletedWithFailures';
                """;
            batch.Parameters.AddWithValue("$batch", current.BatchId.ToString("D"));
            batch.Parameters.AddWithValue("$updated", updatedAt.ToUniversalTime().ToString("O"));
            await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var updated = await LoadOperationAsync(connection, transaction, operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("STANDALONE_SHARE_RETRY_MISSING");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(StandaloneShareMutationDisposition.Applied, updated);
    }

    public async Task<StandaloneShareOperationLease?> TryLeaseNextAsync(
        Guid batchId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        ValidateBatchId(batchId);
        if (leaseExpiresAt <= now) throw new ArgumentOutOfRangeException(nameof(leaseExpiresAt));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        Guid operationId;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT operation.Id FROM StandaloneShareOperations operation
                JOIN StandaloneShareBatches batch ON batch.Id=operation.BatchId
                JOIN Accounts account ON account.Id=operation.AccountId
                WHERE operation.BatchId=$batch AND operation.Status='Queued' AND operation.LeaseExpiresAt IS NULL
                  AND batch.Status IN ('Queued','Active')
                  AND account.Status='Active' AND account.IsEnabled=1 AND account.IsDeleted=0
                ORDER BY operation.SequenceNumber,operation.Id LIMIT 1;
                """;
            select.Parameters.AddWithValue("$batch", batchId.ToString("D"));
            var raw = await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (raw is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
            operationId = Guid.Parse((string)raw);
        }
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE StandaloneShareOperations
                SET Status='Preparing',LeaseGeneration=LeaseGeneration+1,LeaseExpiresAt=$expires,
                    Revision=Revision+1,UpdatedAt=$now
                WHERE Id=$id AND Status='Queued' AND LeaseExpiresAt IS NULL;
                """;
            update.Parameters.AddWithValue("$id", operationId.ToString("D"));
            update.Parameters.AddWithValue("$expires", leaseExpiresAt.ToUniversalTime().ToString("O"));
            update.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O"));
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
        }
        await using (var batch = connection.CreateCommand())
        {
            batch.Transaction = transaction;
            batch.CommandText = """
                UPDATE StandaloneShareBatches SET Status='Active',Revision=Revision+1,UpdatedAt=$now
                WHERE Id=$batch AND Status='Queued';
                """;
            batch.Parameters.AddWithValue("$batch", batchId.ToString("D"));
            batch.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O"));
            await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var operation = await LoadOperationAsync(connection, transaction, operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("STANDALONE_SHARE_LEASE_MISSING");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(operation.Id, operation.BatchId, operation.AccountId, operation.Generation,
            operation.LeaseGeneration, operation.Revision, operation.LeaseExpiresAt!.Value);
    }

    public async Task<bool> TryCompleteBaselineAsync(
        Guid operationId,
        Guid accountId,
        int generation,
        long expectedLeaseGeneration,
        long expectedRevision,
        IReadOnlyCollection<string> existingShareIds,
        string baselineFingerprint,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateOperationIdentity(operationId, accountId, generation, expectedLeaseGeneration, expectedRevision);
        ArgumentNullException.ThrowIfNull(existingShareIds);
        ValidateFingerprint(baselineFingerprint, nameof(baselineFingerprint));
        var baseline = existingShareIds.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (baseline.Length > 100_000 || baseline.Distinct(StringComparer.Ordinal).Count() != baseline.Length ||
            baseline.Any(value => !IsSafeRemoteIdentifier(value)))
            throw new ArgumentException("A sanitized exact share baseline is required.", nameof(existingShareIds));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await LoadOperationAsync(connection, transaction, operationId, cancellationToken).ConfigureAwait(false);
        if (!MatchesLease(current, accountId, generation, expectedLeaseGeneration, expectedRevision,
                StandaloneShareOperationStatus.Preparing) || current!.SubmissionState != StandaloneShareSubmissionState.NotStarted)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        foreach (var shareId in baseline)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO StandaloneShareDiscoveryBaselines(OperationId,Generation,ShareId)
                VALUES($id,$generation,$share);
                """;
            insert.Parameters.AddWithValue("$id", operationId.ToString("D"));
            insert.Parameters.AddWithValue("$generation", generation);
            insert.Parameters.AddWithValue("$share", shareId);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE StandaloneShareOperations
            SET SubmissionState='Prepared',BaselineFingerprint=$fingerprint,BaselineShareCount=$count,
                BaselineCompletedAt=$updated,Revision=Revision+1,UpdatedAt=$updated
            WHERE Id=$id AND AccountId=$account AND Generation=$generation AND LeaseGeneration=$lease
              AND Revision=$revision AND Status='Preparing' AND SubmissionState='NotStarted'
              AND LeaseExpiresAt>=$updated;
            """;
        AddOperationCas(update, operationId, accountId, generation, expectedLeaseGeneration, expectedRevision);
        update.Parameters.AddWithValue("$fingerprint", baselineFingerprint);
        update.Parameters.AddWithValue("$count", baseline.Length);
        update.Parameters.AddWithValue("$updated", updatedAt.ToUniversalTime().ToString("O"));
        var changed = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (changed) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        else await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    public async Task<StringKeysetPage<string>> GetBaselinePageAsync(
        Guid operationId,
        int generation,
        string? afterShareId,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation is required.", nameof(operationId));
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
        if (pageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (afterShareId is not null && !IsSafeRemoteIdentifier(afterShareId))
            throw new ArgumentException("A sanitized cursor is required.", nameof(afterShareId));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ShareId FROM StandaloneShareDiscoveryBaselines
            WHERE OperationId=$id AND Generation=$generation AND ($after IS NULL OR ShareId>$after)
            ORDER BY ShareId LIMIT $take;
            """;
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$after", (object?)afterShareId ?? DBNull.Value);
        command.Parameters.AddWithValue("$take", pageSize + 1);
        var values = new List<string>(pageSize + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) values.Add(reader.GetString(0));
        var next = values.Count > pageSize ? values[pageSize - 1] : null;
        return new(values.Take(pageSize).ToArray(), next);
    }

    public Task<bool> TryMarkSubmittingAsync(
        Guid operationId, Guid accountId, int generation, long expectedLeaseGeneration,
        long expectedRevision, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
        TransitionAsync(operationId, accountId, generation, expectedLeaseGeneration, expectedRevision,
            StandaloneShareOperationStatus.Preparing, StandaloneShareSubmissionState.Prepared,
            StandaloneShareOperationStatus.Submitting, StandaloneShareSubmissionState.Accepted,
            SubmissionAcceptance.Unknown, updatedAt, cancellationToken);

    public Task<bool> TryMarkResultPendingAsync(
        Guid operationId, Guid accountId, int generation, long expectedLeaseGeneration,
        long expectedRevision, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
        TransitionAsync(operationId, accountId, generation, expectedLeaseGeneration, expectedRevision,
            StandaloneShareOperationStatus.Submitting, StandaloneShareSubmissionState.Accepted,
            StandaloneShareOperationStatus.ResultPending, StandaloneShareSubmissionState.ResultPending,
            SubmissionAcceptance.Unknown, updatedAt, cancellationToken);

    public async Task<bool> TryFailAsync(
        Guid operationId,
        Guid accountId,
        int generation,
        long expectedLeaseGeneration,
        long expectedRevision,
        SubmissionAcceptance submissionAcceptance,
        string sanitizedErrorCode,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateOperationIdentity(operationId, accountId, generation, expectedLeaseGeneration, expectedRevision);
        if (!Enum.IsDefined(submissionAcceptance)) throw new ArgumentOutOfRangeException(nameof(submissionAcceptance));
        var error = LocalResultExportValidation.SanitizedErrorCode(sanitizedErrorCode, nameof(sanitizedErrorCode));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await LoadOperationAsync(connection, transaction, operationId, cancellationToken).ConfigureAwait(false);
        if (!MatchesLease(current, accountId, generation, expectedLeaseGeneration, expectedRevision,
                current?.Status ?? StandaloneShareOperationStatus.Cancelled))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE StandaloneShareOperations
            SET Status='Failed',SubmissionState=$submission,SubmissionAcceptance=$acceptance,
                LastErrorCode=$error,LeaseExpiresAt=NULL,Revision=Revision+1,UpdatedAt=$updated
            WHERE Id=$id AND AccountId=$account AND Generation=$generation AND LeaseGeneration=$lease
              AND Revision=$revision AND LeaseExpiresAt>=$updated AND UpdatedAt<=$updated
              AND (($acceptance='DefinitelyNotAccepted' AND Status='Preparing' AND SubmissionState IN ('NotStarted','Prepared'))
                OR ($acceptance='Unknown' AND Status IN ('Submitting','ResultPending')));
            """;
        AddOperationCas(command, operationId, accountId, generation, expectedLeaseGeneration, expectedRevision);
        command.Parameters.AddWithValue("$acceptance", submissionAcceptance.ToString());
        command.Parameters.AddWithValue("$submission", submissionAcceptance == SubmissionAcceptance.DefinitelyNotAccepted
            ? StandaloneShareSubmissionState.DefinitelyNotAccepted.ToString()
            : StandaloneShareSubmissionState.Accepted.ToString());
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$updated", updatedAt.ToUniversalTime().ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        await UpdateBatchCompletionAsync(
            connection, transaction, current!.BatchId, updatedAt, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> TryAcceptResultAsync(
        StandaloneShareResultRecord result,
        long expectedLeaseGeneration,
        long expectedRevision,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateResult(result);
        ValidateOperationIdentity(result.OperationId, result.AccountId, result.Generation,
            expectedLeaseGeneration, expectedRevision);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var operation = await LoadOperationAsync(connection, transaction, result.OperationId, cancellationToken).ConfigureAwait(false);
        if (!MatchesLease(operation, result.AccountId, result.Generation, expectedLeaseGeneration,
                expectedRevision, StandaloneShareOperationStatus.ResultPending) ||
            !string.Equals(operation!.ExactRemoteItems.Fingerprint, result.ExactItemSetFingerprint, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO StandaloneShareResults(
                  Id,OperationId,BatchId,AccountId,Generation,ExactItemSetFingerprint,SettingsFingerprint,
                  ShareId,ShareUrl,ExtractionCode,IsPermanent,IsAccessUnlimited,AcceptedAt,CreatedAt)
                VALUES($id,$operation,$batch,$account,$generation,$items,$settings,$share,$url,$code,1,1,$accepted,$created);
                """;
            insert.Parameters.AddWithValue("$id", result.Id.ToString("D"));
            insert.Parameters.AddWithValue("$operation", result.OperationId.ToString("D"));
            insert.Parameters.AddWithValue("$batch", result.BatchId.ToString("D"));
            insert.Parameters.AddWithValue("$account", result.AccountId.ToString("D"));
            insert.Parameters.AddWithValue("$generation", result.Generation);
            insert.Parameters.AddWithValue("$items", result.ExactItemSetFingerprint);
            insert.Parameters.AddWithValue("$settings", result.SettingsFingerprint);
            insert.Parameters.AddWithValue("$share", result.ShareId);
            insert.Parameters.AddWithValue("$url", result.ShareUrl);
            insert.Parameters.AddWithValue("$code", result.ExtractionCode);
            insert.Parameters.AddWithValue("$accepted", result.AcceptedAt.ToUniversalTime().ToString("O"));
            insert.Parameters.AddWithValue("$created", result.CreatedAt.ToUniversalTime().ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE StandaloneShareOperations
                SET Status='Succeeded',SubmissionState='Completed',LeaseExpiresAt=NULL,LastErrorCode=NULL,
                    Revision=Revision+1,UpdatedAt=$updated
                WHERE Id=$id AND AccountId=$account AND Generation=$generation AND LeaseGeneration=$lease
                  AND Revision=$revision AND Status='ResultPending' AND LeaseExpiresAt>=$updated;
                """;
            AddOperationCas(update, result.OperationId, result.AccountId, result.Generation,
                expectedLeaseGeneration, expectedRevision);
            update.Parameters.AddWithValue("$updated", updatedAt.ToUniversalTime().ToString("O"));
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }
        await UpdateBatchCompletionAsync(connection, transaction, result.BatchId, updatedAt, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<StandaloneShareResultRecord?> GetResultAsync(
        Guid operationId,
        int generation,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation is required.", nameof(operationId));
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id,OperationId,BatchId,AccountId,Generation,ExactItemSetFingerprint,SettingsFingerprint,
                   ShareId,ShareUrl,ExtractionCode,IsPermanent,IsAccessUnlimited,AcceptedAt,CreatedAt
            FROM StandaloneShareResults WHERE OperationId=$id AND Generation=$generation;
            """;
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        command.Parameters.AddWithValue("$generation", generation);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadResult(reader) : null;
    }

    private async Task<StandaloneShareMutationResult<StandaloneShareBatchRecord>> MutateBatchAsync(
        Guid batchId,
        long expectedRevision,
        IReadOnlyCollection<StandaloneShareBatchStatus> allowed,
        StandaloneShareBatchStatus next,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        ValidateBatchId(batchId);
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await LoadBatchAsync(connection, transaction, batchId, cancellationToken).ConfigureAwait(false);
        if (current is null) return await RollbackResultAsync<StandaloneShareBatchRecord>(
            transaction, StandaloneShareMutationDisposition.NotFound, cancellationToken).ConfigureAwait(false);
        if (current.Revision != expectedRevision) return await RollbackResultAsync<StandaloneShareBatchRecord>(
            transaction, StandaloneShareMutationDisposition.Conflict, cancellationToken).ConfigureAwait(false);
        if (!allowed.Contains(current.Status)) return await RollbackResultAsync<StandaloneShareBatchRecord>(
            transaction, StandaloneShareMutationDisposition.InvalidState, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE StandaloneShareBatches SET Status=$next,Revision=Revision+1,UpdatedAt=$updated
            WHERE Id=$id AND Revision=$revision AND Status=$current AND UpdatedAt<=$updated;
            """;
        command.Parameters.AddWithValue("$next", next.ToString());
        command.Parameters.AddWithValue("$updated", updatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$id", batchId.ToString("D"));
        command.Parameters.AddWithValue("$revision", expectedRevision);
        command.Parameters.AddWithValue("$current", current.Status.ToString());
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            return await RollbackResultAsync<StandaloneShareBatchRecord>(
                transaction, StandaloneShareMutationDisposition.Conflict, cancellationToken).ConfigureAwait(false);
        var updated = await LoadBatchAsync(connection, transaction, batchId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("STANDALONE_SHARE_BATCH_MISSING");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(StandaloneShareMutationDisposition.Applied, updated);
    }

    private async Task<bool> TransitionAsync(
        Guid operationId, Guid accountId, int generation, long leaseGeneration, long revision,
        StandaloneShareOperationStatus currentStatus, StandaloneShareSubmissionState currentSubmission,
        StandaloneShareOperationStatus nextStatus, StandaloneShareSubmissionState nextSubmission,
        SubmissionAcceptance acceptance, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        ValidateOperationIdentity(operationId, accountId, generation, leaseGeneration, revision);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE StandaloneShareOperations
            SET Status=$nextStatus,SubmissionState=$nextSubmission,SubmissionAcceptance=$acceptance,
                Revision=Revision+1,UpdatedAt=$updated
            WHERE Id=$id AND AccountId=$account AND Generation=$generation AND LeaseGeneration=$lease
              AND Revision=$revision AND Status=$currentStatus AND SubmissionState=$currentSubmission
              AND LeaseExpiresAt>=$updated AND UpdatedAt<=$updated;
            """;
        AddOperationCas(command, operationId, accountId, generation, leaseGeneration, revision);
        command.Parameters.AddWithValue("$nextStatus", nextStatus.ToString());
        command.Parameters.AddWithValue("$nextSubmission", nextSubmission.ToString());
        command.Parameters.AddWithValue("$acceptance", acceptance.ToString());
        command.Parameters.AddWithValue("$currentStatus", currentStatus.ToString());
        command.Parameters.AddWithValue("$currentSubmission", currentSubmission.ToString());
        command.Parameters.AddWithValue("$updated", updatedAt.ToUniversalTime().ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async Task InsertBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StandaloneSharePendingBatchEnvelope envelope,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO StandaloneShareBatches(
              Id,DraftFingerprint,ResultDirectoryReference,Status,OperationCount,Revision,CreatedAt,UpdatedAt)
            VALUES($id,$fingerprint,$directory,$status,$count,$revision,$created,$updated);
            """;
        var batch = envelope.Batch;
        command.Parameters.AddWithValue("$id", batch.Id.ToString("D"));
        command.Parameters.AddWithValue("$fingerprint", envelope.DraftFingerprint);
        command.Parameters.AddWithValue("$directory", batch.ResultDirectoryReference);
        command.Parameters.AddWithValue("$status", batch.Status.ToString());
        command.Parameters.AddWithValue("$count", batch.OperationCount);
        command.Parameters.AddWithValue("$revision", batch.Revision);
        command.Parameters.AddWithValue("$created", batch.CreatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$updated", batch.UpdatedAt.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertOperationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StandaloneShareOperationRecord operation,
        CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO StandaloneShareOperations(
                  Id,BatchId,AccountId,SequenceNumber,DisplayName,ResourceKind,ParentPath,ExactItemSetFingerprint,
                  Generation,Status,SubmissionState,SubmissionAcceptance,BaselineFingerprint,BaselineShareCount,
                  BaselineCompletedAt,LeaseGeneration,LeaseExpiresAt,LastErrorCode,Revision,CreatedAt,UpdatedAt)
                VALUES($id,$batch,$account,$sequence,$display,$kind,$path,$fingerprint,$generation,$status,$submission,
                  $acceptance,NULL,NULL,NULL,0,NULL,NULL,$revision,$created,$updated);
                """;
            command.Parameters.AddWithValue("$id", operation.Id.ToString("D"));
            command.Parameters.AddWithValue("$batch", operation.BatchId.ToString("D"));
            command.Parameters.AddWithValue("$account", operation.AccountId.ToString("D"));
            command.Parameters.AddWithValue("$sequence", operation.SequenceNumber);
            command.Parameters.AddWithValue("$display", operation.DisplayName);
            command.Parameters.AddWithValue("$kind", operation.Kind.ToString());
            command.Parameters.AddWithValue("$path", operation.ParentPath.Value);
            command.Parameters.AddWithValue("$fingerprint", operation.ExactRemoteItems.Fingerprint);
            command.Parameters.AddWithValue("$generation", operation.Generation);
            command.Parameters.AddWithValue("$status", operation.Status.ToString());
            command.Parameters.AddWithValue("$submission", operation.SubmissionState.ToString());
            command.Parameters.AddWithValue("$acceptance", operation.SubmissionAcceptance.ToString());
            command.Parameters.AddWithValue("$revision", operation.Revision);
            command.Parameters.AddWithValue("$created", operation.CreatedAt.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$updated", operation.UpdatedAt.ToUniversalTime().ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var item = operation.ExactRemoteItems.Items.Single();
        await using var remote = connection.CreateCommand();
        remote.Transaction = transaction;
        remote.CommandText = """
            INSERT INTO StandaloneShareRemoteItems(OperationId,ItemOrdinal,RemoteItemId,CanonicalSortKey,DisplayName)
            VALUES($operation,0,$item,$sort,$display);
            """;
        remote.Parameters.AddWithValue("$operation", operation.Id.ToString("D"));
        remote.Parameters.AddWithValue("$item", item.ExactId);
        remote.Parameters.AddWithValue("$sort", RemoteItemIdentitySet.GetCanonicalSortKey(item.ExactId));
        remote.Parameters.AddWithValue("$display", item.DisplayedTopLevelName);
        await remote.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<StandaloneShareBatchCreationReceipt?> LoadReceiptAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid batchId, string fingerprint,
        CancellationToken cancellationToken)
    {
        var batch = await LoadBatchAsync(connection, transaction, batchId, cancellationToken).ConfigureAwait(false);
        if (batch is null) return null;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT DraftFingerprint FROM StandaloneShareBatches WHERE Id=$id;";
            command.Parameters.AddWithValue("$id", batchId.ToString("D"));
            var stored = (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            if (!string.Equals(stored, fingerprint, StringComparison.Ordinal)) return null;
        }
        var operations = await LoadOperationsAsync(connection, transaction, batchId, cancellationToken).ConfigureAwait(false);
        return new(fingerprint, batch, operations, true);
    }

    private static async Task<StandaloneShareBatchRecord?> LoadBatchAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid batchId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT Id,ResultDirectoryReference,Status,OperationCount,Revision,CreatedAt,UpdatedAt
            FROM StandaloneShareBatches WHERE Id=$id;
            """;
        command.Parameters.AddWithValue("$id", batchId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadBatch(reader) : null;
    }

    private static StandaloneShareBatchRecord ReadBatch(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), reader.GetString(1),
        Enum.Parse<StandaloneShareBatchStatus>(reader.GetString(2)), reader.GetInt32(3),
        reader.GetInt64(4), Parse(reader.GetString(5)), Parse(reader.GetString(6)));

    private static async Task<IReadOnlyList<StandaloneShareOperationRecord>> LoadOperationsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid batchId, CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT Id FROM StandaloneShareOperations WHERE BatchId=$batch
                ORDER BY SequenceNumber,Id LIMIT 10001;
                """;
            command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) ids.Add(Guid.Parse(reader.GetString(0)));
        }
        if (ids.Count > MaximumOperationsPerBatch) throw new InvalidDataException("STANDALONE_SHARE_BATCH_TOO_LARGE");
        var operations = new List<StandaloneShareOperationRecord>(ids.Count);
        foreach (var id in ids)
        {
            var operation = await LoadOperationAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
            if (operation is not null) operations.Add(operation);
        }
        return operations;
    }

    private static async Task<StandaloneShareOperationRecord?> LoadOperationAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid operationId, CancellationToken cancellationToken)
    {
        string[] values;
        long revision;
        long leaseGeneration;
        DateTimeOffset created;
        DateTimeOffset updated;
        DateTimeOffset? leaseExpires;
        DateTimeOffset? baselineCompleted;
        int? baselineCount;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT Id,BatchId,AccountId,DisplayName,ResourceKind,ParentPath,Status,SubmissionState,
                       SubmissionAcceptance,ExactItemSetFingerprint,Generation,SequenceNumber,Revision,
                       BaselineFingerprint,BaselineShareCount,BaselineCompletedAt,LeaseGeneration,LeaseExpiresAt,
                       LastErrorCode,CreatedAt,UpdatedAt
                FROM StandaloneShareOperations WHERE Id=$id;
                """;
            command.Parameters.AddWithValue("$id", operationId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            values = Enumerable.Range(0, 21).Select(index => reader.IsDBNull(index)
                ? ""
                : Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture)!).ToArray();
            revision = reader.GetInt64(12);
            baselineCount = reader.IsDBNull(14) ? null : reader.GetInt32(14);
            baselineCompleted = reader.IsDBNull(15) ? null : Parse(reader.GetString(15));
            leaseGeneration = reader.GetInt64(16);
            leaseExpires = reader.IsDBNull(17) ? null : Parse(reader.GetString(17));
            created = Parse(reader.GetString(19));
            updated = Parse(reader.GetString(20));
        }
        RemoteItemIdentity item;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT RemoteItemId,DisplayName FROM StandaloneShareRemoteItems
                WHERE OperationId=$id ORDER BY ItemOrdinal LIMIT 2;
                """;
            command.Parameters.AddWithValue("$id", operationId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("STANDALONE_SHARE_REMOTE_ITEM_MISSING");
            item = new(reader.GetString(0), reader.GetString(1));
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("STANDALONE_SHARE_REMOTE_ITEM_COUNT");
        }
        var exact = new RemoteItemIdentitySet([item]);
        if (!string.Equals(exact.Fingerprint, values[9], StringComparison.Ordinal))
            throw new InvalidDataException("STANDALONE_SHARE_REMOTE_ITEM_FINGERPRINT");
        return new StandaloneShareOperationRecord(
            Guid.Parse(values[0]), Guid.Parse(values[1]), Guid.Parse(values[2]), exact, values[3],
            Enum.Parse<RemoteResourceKind>(values[4]), new RemotePath(values[5]), int.Parse(values[11]),
            int.Parse(values[10]), Enum.Parse<StandaloneShareOperationStatus>(values[6]),
            Enum.Parse<StandaloneShareSubmissionState>(values[7]), revision, created, updated)
        {
            SubmissionAcceptance = Enum.Parse<SubmissionAcceptance>(values[8]),
            BaselineFingerprint = string.IsNullOrEmpty(values[13]) ? null : values[13],
            BaselineShareCount = baselineCount,
            BaselineCompletedAt = baselineCompleted,
            LeaseGeneration = leaseGeneration,
            LeaseExpiresAt = leaseExpires,
            LastErrorCode = string.IsNullOrEmpty(values[18]) ? null : values[18],
        };
    }

    private static StandaloneShareResultRecord ReadResult(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
        Guid.Parse(reader.GetString(3)), reader.GetInt32(4), reader.GetString(5), reader.GetString(6),
        reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetInt64(10) != 0,
        reader.GetInt64(11) != 0, Parse(reader.GetString(12)), Parse(reader.GetString(13)));

    private static async Task UpdateBatchCompletionAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid batchId,
        DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE StandaloneShareBatches
            SET Status=CASE
                  WHEN EXISTS(SELECT 1 FROM StandaloneShareOperations WHERE BatchId=$batch AND Status='Failed')
                    THEN 'CompletedWithFailures' ELSE 'Completed' END,
                Revision=Revision+1,UpdatedAt=$updated
            WHERE Id=$batch
              AND NOT EXISTS(SELECT 1 FROM StandaloneShareOperations
                WHERE BatchId=$batch AND Status NOT IN ('Succeeded','Failed','Cancelled'));
            """;
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        command.Parameters.AddWithValue("$updated", updatedAt.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> BatchExistsAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid batchId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM StandaloneShareBatches WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", batchId.ToString("D"));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }

    private static bool MatchesLease(
        StandaloneShareOperationRecord? operation, Guid accountId, int generation,
        long leaseGeneration, long revision, StandaloneShareOperationStatus status) =>
        operation is not null && operation.AccountId == accountId && operation.Generation == generation &&
        operation.LeaseGeneration == leaseGeneration && operation.Revision == revision &&
        operation.Status == status && operation.LeaseExpiresAt is not null;

    private static void AddOperationCas(
        SqliteCommand command, Guid operationId, Guid accountId, int generation, long leaseGeneration, long revision)
    {
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        command.Parameters.AddWithValue("$account", accountId.ToString("D"));
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$lease", leaseGeneration);
        command.Parameters.AddWithValue("$revision", revision);
    }

    private static void ValidateOperationIdentity(
        Guid operationId, Guid accountId, int generation, long leaseGeneration, long revision)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation is required.", nameof(operationId));
        if (accountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(accountId));
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
        if (leaseGeneration < 1) throw new ArgumentOutOfRangeException(nameof(leaseGeneration));
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
    }

    private static void ValidateResult(StandaloneShareResultRecord result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Id == Guid.Empty || result.BatchId == Guid.Empty)
            throw new ArgumentException("A result identity is required.", nameof(result));
        ValidateFingerprint(result.ExactItemSetFingerprint, nameof(result));
        ValidateFingerprint(result.SettingsFingerprint, nameof(result));
        if (!IsSafeRemoteIdentifier(result.ShareId)) throw new ArgumentException("A stable share id is required.", nameof(result));
        if (!Uri.TryCreate(result.ShareUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("An HTTPS share result is required.", nameof(result));
        if (string.IsNullOrWhiteSpace(result.ExtractionCode) || result.ExtractionCode.Length > 64 ||
            result.ExtractionCode.Any(char.IsControl) || !result.IsPermanent || !result.IsAccessUnlimited)
            throw new ArgumentException("An accepted permanent share result is required.", nameof(result));
    }

    private static bool IsSafeRemoteIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && !value.Any(char.IsControl) &&
        !Uri.TryCreate(value, UriKind.Absolute, out _);

    private static void ValidateFingerprint(string value, string parameterName) =>
        WebAutomationAttemptRecord.ValidateFingerprint(value, parameterName);

    private static void ValidateBatchId(Guid batchId)
    {
        if (batchId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(batchId));
    }

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);

    private static async Task<StandaloneShareMutationResult<T>> RollbackResultAsync<T>(
        SqliteTransaction transaction, StandaloneShareMutationDisposition disposition,
        CancellationToken cancellationToken) where T : class
    {
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return new(disposition, null);
    }
}
