using BaiduBatchTransfer.Application.Scheduling;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database;

/// <summary>
/// Exact-id store used only by the single-item AcceptanceOnly runner. None of
/// its queries select the next row from a queue.
/// </summary>
public sealed class SqliteManagedTransferAcceptanceStore(SqliteConnectionFactory factory)
    : IManagedTransferAcceptanceStore
{
    private readonly SqliteConnectionFactory factory =
        factory ?? throw new ArgumentNullException(nameof(factory));

    public async Task<ManagedTransferAcceptanceReservation?> TryReserveTransferAsync(
        Guid batchId,
        DateTimeOffset reservedAt,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        Required(batchId, nameof(batchId));
        if (leaseExpiresAt <= reservedAt) throw new ArgumentOutOfRangeException(nameof(leaseExpiresAt));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            string? transferId = null;
            string? shareId = null;
            string? accountId = null;
            var attemptCount = 0;
            await using (var read = Command(connection, transaction, """
                SELECT transfer.Id,share.Id,transfer.AssignedAccountId,transfer.AttemptCount
                FROM BatchJobs batch
                JOIN TransferOperations transfer ON transfer.BatchJobId=batch.Id
                JOIN ShareOperations share ON share.TransferOperationId=transfer.Id
                JOIN ResourceItems resource ON resource.Id=transfer.ResourceItemId
                JOIN AccountAllocations allocation ON allocation.BatchJobId=batch.Id
                  AND allocation.AccountId=transfer.AssignedAccountId
                JOIN BatchOutputAccounts output ON output.BatchJobId=batch.Id
                  AND output.AccountId=transfer.AssignedAccountId
                  AND output.OutputOrdinal=share.OutputOrdinal
                WHERE batch.Id=$batch AND batch.AcceptanceOnly=1 AND batch.Status='Queued'
                  AND batch.ResourceCount=1 AND batch.OperationCount=1 AND batch.ShareOperationCount=1
                  AND transfer.AcceptanceOnly=1 AND transfer.Status='Waiting'
                  AND transfer.TargetSlotState IN ('Unoccupied','Released')
                  AND transfer.ExecutionEngineVersion='ManagedChromium-v1'
                  AND transfer.InstallationId IS NOT NULL AND transfer.SessionId IS NOT NULL
                  AND share.Status='WaitingTransfer' AND output.ShareEnabled=1
                  AND share.AccountId=transfer.AssignedAccountId
                  AND share.ResourceItemId=transfer.ResourceItemId
                  AND share.ExecutionEngineVersion=transfer.ExecutionEngineVersion
                  AND share.BrowserFamily=transfer.BrowserFamily
                  AND share.ProfileId=transfer.ProfileId
                  AND share.InstallationId=transfer.InstallationId
                  AND share.SessionId=transfer.SessionId
                  AND resource.BatchJobId=batch.Id AND resource.CandidateStatus='Expanded'
                  AND allocation.ReservedSlotCount=0
                  AND (SELECT COUNT(*) FROM TransferOperations t WHERE t.BatchJobId=batch.Id)=1
                  AND (SELECT COUNT(*) FROM ShareOperations s WHERE s.BatchJobId=batch.Id)=1
                  AND NOT EXISTS(SELECT 1 FROM BatchJobs active WHERE active.Status='Active');
                """))
            {
                Add(read, "$batch", batchId);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    transferId = reader.GetString(0);
                    shareId = reader.GetString(1);
                    accountId = reader.GetString(2);
                    attemptCount = reader.GetInt32(3);
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        throw new InvalidDataException("AcceptanceOnly graph is not unique.");
                }
            }
            if (transferId is null || shareId is null || accountId is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            await using (var batch = Command(connection, transaction, """
                UPDATE BatchJobs SET Status='Active',StartedAt=COALESCE(StartedAt,$now)
                WHERE Id=$batch AND AcceptanceOnly=1 AND Status='Queued'
                  AND NOT EXISTS(SELECT 1 FROM BatchJobs active WHERE active.Status='Active');
                """))
            {
                Add(batch, "$batch", batchId); Add(batch, "$now", reservedAt);
                RequireOne(await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
            }
            await using (var resource = Command(connection, transaction, """
                UPDATE ResourceItems SET CandidateStatus='Reserved',ReservedAccountId=$account,
                  ReservationExpiresAt=$lease,UpdatedAt=$now
                WHERE BatchJobId=$batch AND CandidateStatus='Expanded'
                  AND Id=(SELECT ResourceItemId FROM TransferOperations WHERE Id=$transfer);
                """))
            {
                Add(resource, "$batch", batchId); Add(resource, "$transfer", transferId);
                Add(resource, "$account", accountId); Add(resource, "$lease", leaseExpiresAt);
                Add(resource, "$now", reservedAt);
                RequireOne(await resource.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
            }
            await using (var transfer = Command(connection, transaction, """
                UPDATE TransferOperations SET Status='Reserved',TargetSlotState='Reserved',
                  TargetSlotReservedAt=$now,LeaseExpiresAt=$lease,UpdatedAt=$now
                WHERE Id=$transfer AND BatchJobId=$batch AND AcceptanceOnly=1
                  AND Status='Waiting' AND TargetSlotState IN ('Unoccupied','Released')
                  AND AttemptCount=$attempt;
                """))
            {
                Add(transfer, "$transfer", transferId); Add(transfer, "$batch", batchId);
                Add(transfer, "$now", reservedAt); Add(transfer, "$lease", leaseExpiresAt);
                Add(transfer, "$attempt", attemptCount);
                RequireOne(await transfer.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
            }
            await using (var allocation = Command(connection, transaction, """
                UPDATE AccountAllocations SET ReservedSlotCount=ReservedSlotCount+1
                WHERE BatchJobId=$batch AND AccountId=$account AND ReservedSlotCount=0;
                """))
            {
                Add(allocation, "$batch", batchId); Add(allocation, "$account", accountId);
                RequireOne(await allocation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(
                batchId,
                Guid.Parse(transferId),
                Guid.Parse(shareId),
                Guid.Parse(accountId),
                checked(attemptCount + 1));
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ShareExecutionLease?> TryReserveLinkedShareAsync(
        ManagedTransferAcceptanceReservation reservation,
        DateTimeOffset reservedAt,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        Validate(reservation);
        if (leaseExpiresAt <= reservedAt) throw new ArgumentOutOfRangeException(nameof(leaseExpiresAt));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            await using (var update = Command(connection, transaction, """
                UPDATE ShareOperations SET Status='Reserved',LeaseGeneration=LeaseGeneration+1,
                  LeaseExpiresAt=$lease,UpdatedAt=$now
                WHERE Id=$share AND TransferOperationId=$transfer AND BatchJobId=$batch
                  AND AccountId=$account AND Status='WaitingShare' AND LeaseExpiresAt IS NULL
                  AND ExactItemSetFingerprint IS NOT NULL
                  AND EXISTS(SELECT 1 FROM BatchJobs batch
                    WHERE batch.Id=ShareOperations.BatchJobId AND batch.AcceptanceOnly=1
                      AND batch.Status='Active' AND batch.ResourceCount=1
                      AND batch.OperationCount=1 AND batch.ShareOperationCount=1)
                  AND EXISTS(SELECT 1 FROM TransferOperations transfer
                    WHERE transfer.Id=ShareOperations.TransferOperationId
                      AND transfer.AcceptanceOnly=1 AND transfer.Status='Succeeded'
                      AND transfer.AssignedAccountId=ShareOperations.AccountId)
                  AND (SELECT COUNT(*) FROM ShareOperationRemoteItems item
                    WHERE item.ShareOperationId=ShareOperations.Id)=1
                  AND NOT EXISTS(SELECT 1 FROM TransferOperations busy
                    WHERE busy.AssignedAccountId=ShareOperations.AccountId
                      AND busy.Status IN ('Reserved','Preparing','Submitting','ResultPending','Paused'))
                  AND NOT EXISTS(SELECT 1 FROM ShareOperations busy
                    WHERE busy.AccountId=ShareOperations.AccountId AND busy.Id<>ShareOperations.Id
                      AND busy.Status IN ('Reserved','Preparing','Submitting','ResultPending','Paused'));
                """))
            {
                Add(update, "$share", reservation.ShareOperationId);
                Add(update, "$transfer", reservation.TransferOperationId);
                Add(update, "$batch", reservation.BatchId);
                Add(update, "$account", reservation.AccountId);
                Add(update, "$lease", leaseExpiresAt); Add(update, "$now", reservedAt);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return null;
                }
            }
            await using var read = Command(connection, transaction,
                "SELECT Generation,LeaseGeneration FROM ShareOperations WHERE Id=$share;");
            Add(read, "$share", reservation.ShareOperationId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("Reserved acceptance share disappeared.");
            var lease = new ShareExecutionLease(
                reservation.ShareOperationId, reader.GetInt32(0), reader.GetInt64(1));
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return lease;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> TryFinalizeAsync(
        ManagedTransferAcceptanceReservation reservation,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        Validate(reservation);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var update = Command(connection, transaction, """
            UPDATE BatchJobs SET Status='Completed',CompletedAt=COALESCE(CompletedAt,$now)
            WHERE Id=$batch AND AcceptanceOnly=1 AND Status IN ('Active','Completed')
              AND ResourceCount=1 AND OperationCount=1 AND ShareOperationCount=1
              AND EXISTS(SELECT 1 FROM TransferOperations transfer
                WHERE transfer.Id=$transfer AND transfer.BatchJobId=BatchJobs.Id
                  AND transfer.AcceptanceOnly=1 AND transfer.AssignedAccountId=$account
                  AND transfer.Status='Succeeded')
              AND EXISTS(SELECT 1 FROM ShareOperations share
                WHERE share.Id=$share AND share.TransferOperationId=$transfer
                  AND share.BatchJobId=BatchJobs.Id AND share.AccountId=$account
                  AND share.Status IN ('Succeeded','ReusedExisting'))
              AND EXISTS(SELECT 1 FROM BaiduShareResults result
                WHERE result.ShareOperationId=$share AND result.BatchJobId=BatchJobs.Id
                  AND result.AccountId=$account AND result.IsAccepted=1 AND result.IsCurrent=1);
            """);
        Add(update, "$batch", reservation.BatchId); Add(update, "$transfer", reservation.TransferOperationId);
        Add(update, "$share", reservation.ShareOperationId); Add(update, "$account", reservation.AccountId);
        Add(update, "$now", updatedAt);
        var changed = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    private static SqliteCommand Command(
        SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value switch
        {
            Guid guid => guid.ToString("D"),
            DateTimeOffset time => time.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            _ => value,
        });

    private static void RequireOne(int changed)
    {
        if (changed != 1) throw new InvalidOperationException("The exact AcceptanceOnly reservation changed.");
    }

    private static void Required(Guid value, string name)
    {
        if (value == Guid.Empty) throw new ArgumentException("An exact identity is required.", name);
    }

    private static void Validate(ManagedTransferAcceptanceReservation value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Required(value.BatchId, nameof(value.BatchId));
        Required(value.TransferOperationId, nameof(value.TransferOperationId));
        Required(value.ShareOperationId, nameof(value.ShareOperationId));
        Required(value.AccountId, nameof(value.AccountId));
        if (value.TransferGeneration < 1) throw new ArgumentOutOfRangeException(nameof(value.TransferGeneration));
    }
}
