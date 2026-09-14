using BaiduBatchTransfer.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed class SqliteLocalImportDraftCleanupStore : ILocalImportDraftCleanupStore
{
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly Action transactionStarting;
    private readonly Func<CancellationToken, Task> transactionAcquired;

    public SqliteLocalImportDraftCleanupStore(SqliteConnectionFactory connectionFactory)
        : this(connectionFactory, static () => { }, static _ => Task.CompletedTask)
    {
    }

    internal SqliteLocalImportDraftCleanupStore(
        SqliteConnectionFactory connectionFactory,
        Action transactionStarting,
        Func<CancellationToken, Task> transactionAcquired)
    {
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        this.transactionStarting = transactionStarting ?? throw new ArgumentNullException(nameof(transactionStarting));
        this.transactionAcquired = transactionAcquired ?? throw new ArgumentNullException(nameof(transactionAcquired));
    }

    public async Task<LocalImportDraftCleanupResult> DeleteAsync(
        Guid importSessionId,
        CancellationToken cancellationToken = default)
    {
        if (importSessionId == Guid.Empty)
            throw new ArgumentException("An import session is required.", nameof(importSessionId));

        await using var connection = await connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        transactionStarting();
        await using var transaction = connection.BeginTransaction(deferred: false);

        try
        {
            await transactionAcquired(cancellationToken).ConfigureAwait(false);
            var sessionId = importSessionId.ToString("D");
            if (!await ExistsAsync(connection, transaction, """
                SELECT EXISTS(SELECT 1 FROM ImportSessions WHERE Id=$session);
                """, sessionId, cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new(LocalImportDraftCleanupDisposition.AlreadyAbsent, 0);
            }

            if (await ExistsAsync(connection, transaction, """
                SELECT EXISTS(
                    SELECT 1 FROM ImportSessions WHERE Id=$session
                      AND (CreatedBatchJobId IS NOT NULL OR AppendToBatchJobId IS NOT NULL)
                    UNION ALL
                    SELECT 1 FROM BatchJobs WHERE ImportSessionId=$session
                    UNION ALL
                    SELECT 1 FROM BatchCreationReceipts WHERE ImportSessionId=$session
                    UNION ALL
                    SELECT 1
                    FROM ResourceItems resource
                    JOIN StagedImportRows staged ON staged.Id=resource.StagedImportRowId
                    WHERE staged.ImportSessionId=$session
                );
                """, sessionId, cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new(LocalImportDraftCleanupDisposition.BatchConflict, 0);
            }

            if (await ExistsAsync(connection, transaction, """
                SELECT EXISTS(
                    SELECT 1
                    FROM StagedImportRows dependent
                    JOIN StagedImportRows target ON target.Id=dependent.DuplicateOfStagedRowId
                    WHERE target.ImportSessionId=$session
                      AND dependent.ImportSessionId<>$session
                );
                """, sessionId, cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new(LocalImportDraftCleanupDisposition.LocalDependencyConflict, 0);
            }

            await ExecuteAsync(connection, transaction, """
                CREATE TEMP TABLE TargetImportDraftStagedRows(
                    Id TEXT PRIMARY KEY
                ) WITHOUT ROWID;
                INSERT INTO TargetImportDraftStagedRows(Id)
                SELECT Id FROM StagedImportRows WHERE ImportSessionId=$session;
                """, sessionId, cancellationToken).ConfigureAwait(false);

            var stagedRowCount = await CountAsync(connection, transaction,
                "SELECT COUNT(*) FROM TargetImportDraftStagedRows;",
                sessionId, cancellationToken).ConfigureAwait(false);
            var columnCount = await CountAsync(connection, transaction,
                "SELECT COUNT(*) FROM ImportColumnDefinitions WHERE ImportSessionId=$session;",
                sessionId, cancellationToken).ConfigureAwait(false);
            var payloadCount = await CountAsync(connection, transaction, """
                SELECT COUNT(*)
                FROM StagedImportRowPayloads payload
                WHERE payload.StagedImportRowId IN (
                    SELECT Id FROM TargetImportDraftStagedRows);
                """, sessionId, cancellationToken).ConfigureAwait(false);
            var deletedStagedRowCount = ToDeletedStagedRowCount(stagedRowCount);

            var deletedStagedRows = await ExecuteAsync(connection, transaction,
                "DELETE FROM StagedImportRows WHERE ImportSessionId=$session;",
                sessionId, cancellationToken).ConfigureAwait(false);
            if (deletedStagedRows != stagedRowCount)
                throw new InvalidOperationException(
                    $"Expected to delete {stagedRowCount} staged import rows but deleted {deletedStagedRows}.");

            var deletedSessions = await ExecuteAsync(connection, transaction,
                "DELETE FROM ImportSessions WHERE Id=$session;",
                sessionId, cancellationToken).ConfigureAwait(false);
            if (deletedSessions != 1)
                throw new InvalidOperationException(
                    $"Expected to delete exactly one import session but deleted {deletedSessions}.");

            var residueCount = await CountAsync(connection, transaction, """
                SELECT
                    (SELECT COUNT(*) FROM ImportSessions WHERE Id=$session) +
                    (SELECT COUNT(*) FROM StagedImportRows WHERE ImportSessionId=$session) +
                    (SELECT COUNT(*) FROM ImportColumnDefinitions WHERE ImportSessionId=$session) +
                    (SELECT COUNT(*) FROM StagedImportRowPayloads
                     WHERE StagedImportRowId IN (
                         SELECT Id FROM TargetImportDraftStagedRows));
                """, sessionId, cancellationToken).ConfigureAwait(false);
            if (residueCount != 0)
                throw new InvalidOperationException(
                    $"Import draft deletion left {residueCount} target rows behind " +
                    $"(pre-delete columns: {columnCount}, payloads: {payloadCount}).");

            await EnsureForeignKeysAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(
                LocalImportDraftCleanupDisposition.Deleted,
                deletedStagedRowCount);
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Preserve the deletion failure; disposal still closes the transaction.
            }
            throw;
        }
    }

    internal static int ToDeletedStagedRowCount(long stagedRowCount) =>
        checked((int)stagedRowCount);

    private static async Task<bool> ExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string sessionId,
        CancellationToken cancellationToken) =>
        await CountAsync(connection, transaction, sql, sessionId, cancellationToken)
            .ConfigureAwait(false) != 0;

    private static async Task<long> CountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$session", sessionId);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$session", sessionId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureForeignKeysAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Import draft deletion failed the foreign-key integrity check.");
    }
}
