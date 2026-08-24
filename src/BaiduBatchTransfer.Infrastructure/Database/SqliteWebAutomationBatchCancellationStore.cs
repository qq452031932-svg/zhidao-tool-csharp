using BaiduBatchTransfer.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed class SqliteWebAutomationBatchCancellationStore(
    SqliteConnectionFactory factory) : IWebAutomationBatchCancellationStore
{
    public async Task<WebAutomationBatchCancellationDisposition> CancelAsync(
        Guid operationId,
        Guid batchId,
        Guid accountId,
        int generation,
        WebAutomationOwnerKind ownerKind,
        long expectedAttemptRevision,
        WebAutomationAttemptState expectedResumeStage,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        Validate(operationId, batchId, accountId, generation, ownerKind);
        if (expectedAttemptRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedAttemptRevision));
        if (!WebAutomationAttemptRecord.CanResumeStage(expectedResumeStage))
            throw new ArgumentOutOfRangeException(nameof(expectedResumeStage));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        if (!await IsExactOwnerAsync(
                connection, transaction, operationId, batchId, accountId, generation, ownerKind,
                cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return WebAutomationBatchCancellationDisposition.OwnershipMismatch;
        }

        var batchStatus = await GetBatchStatusAsync(
            connection, transaction, batchId, ownerKind, cancellationToken).ConfigureAwait(false);
        if (string.Equals(batchStatus, "Cancelled", StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return WebAutomationBatchCancellationDisposition.AlreadyCancelled;
        }
        if (IsTerminal(batchStatus, ownerKind))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return WebAutomationBatchCancellationDisposition.Terminal;
        }
        if (!await IsExactHandoffAsync(
                connection, transaction, operationId, accountId, generation, ownerKind,
                expectedAttemptRevision, expectedResumeStage, cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return WebAutomationBatchCancellationDisposition.StaleHandoff;
        }

        var updated = updatedAt.ToUniversalTime().ToString("O");
        var changed = await CancelBatchAsync(
            connection, transaction, batchId, ownerKind, updated, cancellationToken).ConfigureAwait(false);
        if (changed != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return WebAutomationBatchCancellationDisposition.Terminal;
        }

        if (ownerKind == WebAutomationOwnerKind.StandaloneShare)
            await CancelStandaloneWorkAsync(connection, transaction, batchId, updated, cancellationToken)
                .ConfigureAwait(false);
        else
            await CancelTransferWorkAsync(connection, transaction, batchId, updated, cancellationToken)
                .ConfigureAwait(false);
        await ResolveAttemptsAsync(connection, transaction, batchId, ownerKind, updated, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return WebAutomationBatchCancellationDisposition.Cancelled;
    }

    public async Task<bool> IsCancelledAsync(
        Guid batchId,
        WebAutomationOwnerKind ownerKind,
        CancellationToken cancellationToken = default)
    {
        if (batchId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(batchId));
        if (!Enum.IsDefined(ownerKind)) throw new ArgumentOutOfRangeException(nameof(ownerKind));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = ownerKind == WebAutomationOwnerKind.StandaloneShare
            ? "SELECT COUNT(*) FROM StandaloneShareBatches WHERE Id=$batch AND Status='Cancelled';"
            : "SELECT COUNT(*) FROM BatchJobs WHERE Id=$batch AND Status='Cancelled';";
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }

    private static async Task<bool> IsExactOwnerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid operationId,
        Guid batchId,
        Guid accountId,
        int generation,
        WebAutomationOwnerKind ownerKind,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ownerKind switch
        {
            WebAutomationOwnerKind.Transfer => """
                SELECT COUNT(*) FROM TransferOperations
                WHERE Id=$operation AND BatchJobId=$batch AND AssignedAccountId=$account
                  AND AttemptCount=$generation;
                """,
            WebAutomationOwnerKind.TransferShare => """
                SELECT COUNT(*) FROM ShareOperations
                WHERE Id=$operation AND BatchJobId=$batch AND AccountId=$account
                  AND Generation=$generation;
                """,
            WebAutomationOwnerKind.StandaloneShare => """
                SELECT COUNT(*) FROM StandaloneShareOperations
                WHERE Id=$operation AND BatchId=$batch AND AccountId=$account
                  AND Generation=$generation;
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(ownerKind)),
        };
        AddIdentity(command, operationId, batchId, accountId, generation);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }

    private static async Task<string?> GetBatchStatusAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid batchId,
        WebAutomationOwnerKind ownerKind,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ownerKind == WebAutomationOwnerKind.StandaloneShare
            ? "SELECT Status FROM StandaloneShareBatches WHERE Id=$batch;"
            : "SELECT Status FROM BatchJobs WHERE Id=$batch;";
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task<bool> IsExactHandoffAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid operationId,
        Guid accountId,
        int generation,
        WebAutomationOwnerKind ownerKind,
        long expectedAttemptRevision,
        WebAutomationAttemptState expectedResumeStage,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM WebAutomationAttempts
            WHERE OperationId=$operation AND AccountId=$account AND Generation=$generation
              AND OwnerKind=$owner AND State='UserHandoff' AND IsWaitingForUser=1
              AND Revision=$revision AND ResumeStage=$resume;
            """;
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.Parameters.AddWithValue("$account", accountId.ToString("D"));
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$owner", ownerKind.ToString());
        command.Parameters.AddWithValue("$revision", expectedAttemptRevision);
        command.Parameters.AddWithValue("$resume", expectedResumeStage.ToString());
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }

    private static async Task<int> CancelBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid batchId,
        WebAutomationOwnerKind ownerKind,
        string updated,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ownerKind == WebAutomationOwnerKind.StandaloneShare
            ? """
                UPDATE StandaloneShareBatches
                SET Status='Cancelled',Revision=Revision+1,UpdatedAt=$updated
                WHERE Id=$batch AND Status IN ('Queued','Active','Paused');
                """
            : """
                UPDATE BatchJobs SET Status='Cancelled',CompletedAt=$updated
                WHERE Id=$batch AND Status IN ('Queued','Active','Paused','AwaitingCandidates','PausedReady');
                """;
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        command.Parameters.AddWithValue("$updated", updated);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task CancelTransferWorkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid batchId,
        string updated,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, """
            UPDATE TransferOperations
            SET Status='Cancelled',OutcomeKind='Cancelled',
                TargetSlotState=CASE WHEN TargetSlotState='Reserved' THEN 'Released' ELSE TargetSlotState END,
                TargetSlotReservedAt=NULL,LeaseExpiresAt=NULL,UpdatedAt=$updated
            WHERE BatchJobId=$batch AND Status IN ('Waiting','Reserved','Preparing')
              AND SubmitState IN ('NotPrepared','PreparingToSubmit')
              AND AdapterRequestId IS NULL AND RemoteItemIdsJson IS NULL AND SubmitStartedAt IS NULL;
            """, batchId, updated, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, """
            UPDATE ShareOperations
            SET Status='Cancelled',LeaseExpiresAt=NULL,NextReconcileAt=NULL,UpdatedAt=$updated
            WHERE BatchJobId=$batch AND Status IN ('WaitingShare','Reserved','Preparing')
              AND AdapterRequestId IS NULL AND SubmitStartedAt IS NULL;
            """, batchId, updated, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, """
            UPDATE ShareOperations
            SET LastErrorCode='BATCH_CANCELLED',LastErrorMessage='批次已取消。',UpdatedAt=$updated
            WHERE BatchJobId=$batch AND Status='WaitingTransfer';
            """, batchId, updated, cancellationToken).ConfigureAwait(false);
    }

    private static Task CancelStandaloneWorkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid batchId,
        string updated,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, """
            UPDATE StandaloneShareOperations
            SET Status='Cancelled',LeaseOwnerId=NULL,LeaseExpiresAt=NULL,
                LastErrorCode='BATCH_CANCELLED',Revision=Revision+1,UpdatedAt=$updated
            WHERE BatchId=$batch AND Status IN ('Queued','Reserved','Preparing')
              AND SubmissionState IN ('NotStarted','Prepared','DefinitelyNotAccepted');
            """, batchId, updated, cancellationToken);

    private static Task ResolveAttemptsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid batchId,
        WebAutomationOwnerKind ownerKind,
        string updated,
        CancellationToken cancellationToken)
    {
        var ownership = ownerKind switch
        {
            WebAutomationOwnerKind.Transfer or WebAutomationOwnerKind.TransferShare => """
                (attempt.OwnerKind='Transfer' AND EXISTS(
                    SELECT 1 FROM TransferOperations operation
                    WHERE operation.Id=attempt.OperationId AND operation.BatchJobId=$batch))
                OR (attempt.OwnerKind='TransferShare' AND EXISTS(
                    SELECT 1 FROM ShareOperations operation
                    WHERE operation.Id=attempt.OperationId AND operation.BatchJobId=$batch))
                """,
            WebAutomationOwnerKind.StandaloneShare => "attempt.OwnerKind='StandaloneShare' AND EXISTS(SELECT 1 FROM StandaloneShareOperations operation WHERE operation.Id=attempt.OperationId AND operation.BatchId=$batch)",
            _ => throw new ArgumentOutOfRangeException(nameof(ownerKind)),
        };
        return ExecuteAsync(connection, transaction, $"""
            UPDATE WebAutomationAttempts AS attempt
            SET State=CASE
                  WHEN State IN ('Submitting','ResultPending')
                    OR (State='UserHandoff' AND ResumeStage IN ('Submitting','ResultPending'))
                    THEN 'NeedsDecision'
                  ELSE 'DefinitelyNotSubmitted' END,
                ResumeStage=NULL,IsWaitingForUser=0,Revision=Revision+1,UpdatedAt=$updated
            WHERE ({ownership})
              AND State IN ('Preparing','BaselinePersisted','Submitting','ResultPending','UserHandoff');
            """, batchId, updated, cancellationToken);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        Guid batchId,
        string updated,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        command.Parameters.AddWithValue("$updated", updated);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsTerminal(string? status, WebAutomationOwnerKind ownerKind)
    {
        if (status is null) return true;
        return ownerKind == WebAutomationOwnerKind.StandaloneShare
            ? status is "Completed" or "CompletedWithFailures"
            : status is "Completed" or "CompletedWithShortfall";
    }

    private static void AddIdentity(
        SqliteCommand command, Guid operationId, Guid batchId, Guid accountId, int generation)
    {
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        command.Parameters.AddWithValue("$account", accountId.ToString("D"));
        command.Parameters.AddWithValue("$generation", generation);
    }

    private static void Validate(
        Guid operationId, Guid batchId, Guid accountId, int generation, WebAutomationOwnerKind ownerKind)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation is required.", nameof(operationId));
        if (batchId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(batchId));
        if (accountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(accountId));
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
        if (!Enum.IsDefined(ownerKind)) throw new ArgumentOutOfRangeException(nameof(ownerKind));
    }
}
