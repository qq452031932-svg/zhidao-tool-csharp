using System.Text.Json;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database.Repositories;

public sealed class SqliteBatchResultProjectionRepository(SqliteConnectionFactory connectionFactory)
    : IBatchResultProjectionRepository
{
    private readonly SqliteConnectionFactory connectionFactory =
        connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    public async Task<BatchResultProjectionDefinition?> GetDefinitionAsync(
        Guid batchJobId,
        CancellationToken cancellationToken = default)
    {
        ValidateBatchId(batchJobId);
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);

        string sourceType;
        string sourceName;
        await using (var command = Command(connection, transaction,
            "SELECT i.SourceType,i.SourceName FROM BatchJobs b LEFT JOIN ImportSessions i ON i.Id=b.ImportSessionId WHERE b.Id=$batch;"))
        {
            Add(command, "$batch", batchJobId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            if (reader.IsDBNull(0) || string.IsNullOrWhiteSpace(reader.GetString(0)) ||
                reader.IsDBNull(1) || string.IsNullOrWhiteSpace(reader.GetString(1)))
                throw InvalidProjection("The batch source name is unavailable.");
            sourceType = reader.GetString(0);
            sourceName = reader.GetString(1);
        }

        var columns = new List<BatchResultOriginalColumn>();
        await using (var command = Command(connection, transaction,
            "SELECT ColumnOrdinal,SourceHeader FROM BatchSourceColumns WHERE BatchJobId=$batch ORDER BY ColumnOrdinal;"))
        {
            Add(command, "$batch", batchJobId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                columns.Add(new(reader.GetInt32(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1)));
        }
        ValidateColumnOrdinals(columns.Select(value => value.ColumnOrdinal).ToArray(), sourceType);

        var accounts = new List<BatchResultProjectionAccount>();
        await using (var command = Command(connection, transaction,
            "SELECT AccountId,OutputOrdinal,OutputLabel,ShareEnabled FROM BatchOutputAccounts WHERE BatchJobId=$batch ORDER BY OutputOrdinal,AccountId;"))
        {
            Add(command, "$batch", batchJobId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                accounts.Add(new(Guid.Parse(reader.GetString(0)), reader.GetInt32(1), reader.GetString(2), reader.GetInt64(3) != 0));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(sourceName, columns, accounts);
    }

    public async Task<BatchResultProjectionPage> GetProjectionPageAsync(
        Guid batchJobId,
        long afterCandidateSequence,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ValidateBatchId(batchJobId);
        if (afterCandidateSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterCandidateSequence));
        if (pageSize is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(pageSize));

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var columnOrdinals = await ReadColumnOrdinalsAsync(
            connection, transaction, batchJobId, cancellationToken).ConfigureAwait(false);
        var sourceType = await ReadSourceTypeAsync(
            connection, transaction, batchJobId, cancellationToken).ConfigureAwait(false);
        ValidateColumnOrdinals(columnOrdinals, sourceType);

        var items = new List<BatchResultResourceProjection>(pageSize + 1);
        await using (var command = Command(connection, transaction, """
            SELECT r.Id,r.CandidateSequence,r.ResolvedOutputTitle,
                   p.OriginalShareUrl,p.RawValuesJson,p.StagedImportRowId
            FROM ResourceItems r
            LEFT JOIN StagedImportRowPayloads p ON p.StagedImportRowId=r.StagedImportRowId
            WHERE r.BatchJobId=$batch AND r.CandidateSequence>$after
            ORDER BY r.CandidateSequence,r.Id
            LIMIT $limit;
            """))
        {
            Add(command, "$batch", batchJobId);
            command.Parameters.AddWithValue("$after", afterCandidateSequence);
            command.Parameters.AddWithValue("$limit", checked(pageSize + 1));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                items.Add(ReadResource(reader, columnOrdinals));
        }

        var hasMore = items.Count > pageSize;
        if (hasMore) items.RemoveAt(items.Count - 1);
        if (items.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(items, [], null);
        }

        var accountResults = await ReadAccountResultsAsync(
            connection,
            transaction,
            batchJobId,
            afterCandidateSequence,
            items[^1].CandidateSequence,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(items, accountResults, hasMore ? items[^1].CandidateSequence : null);
    }

    private static async Task<IReadOnlyList<BatchResultAccountProjection>> ReadAccountResultsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid batchJobId,
        long afterCandidateSequence,
        long throughCandidateSequence,
        CancellationToken cancellationToken)
    {
        var results = new List<BatchResultAccountProjection>();
        await using var command = Command(connection, transaction, """
            WITH ranged_resources AS (
                SELECT Id,CandidateSequence
                FROM ResourceItems
                WHERE BatchJobId=$batch
                  AND CandidateSequence>$after
                  AND CandidateSequence<=$through
            ), ranked_transfers AS (
                SELECT t.Id,t.ResourceItemId,t.AssignedAccountId,t.Status,t.LastErrorCode,
                       t.AllocationOrdinal,t.SequenceNumber,r.CandidateSequence,
                       ROW_NUMBER() OVER (
                         PARTITION BY t.ResourceItemId,t.AssignedAccountId
                         ORDER BY CASE WHEN t.Status='Cancelled' THEN 1 ELSE 0 END,
                                  t.AllocationOrdinal DESC,t.SequenceNumber DESC,t.Id DESC) AS CanonicalRank,
                       SUM(CASE WHEN t.Status<>'Cancelled' THEN 1 ELSE 0 END) OVER (
                         PARTITION BY t.ResourceItemId,t.AssignedAccountId) AS ActiveCount
                FROM TransferOperations t
                JOIN ranged_resources r ON r.Id=t.ResourceItemId
                WHERE t.BatchJobId=$batch
            )
            SELECT t.ResourceItemId,t.AssignedAccountId,a.OutputOrdinal,t.Status,
                   s.Status,
                   current_result.ShareUrl,current_result.ExtractionCode,
                   COALESCE(s.LastErrorCode,t.LastErrorCode),
                   t.ActiveCount,
                   (SELECT COUNT(*) FROM BaiduShareResults duplicate_result
                    WHERE duplicate_result.AccountId=s.AccountId
                      AND duplicate_result.ExactItemSetFingerprint=s.ExactItemSetFingerprint
                      AND duplicate_result.SettingsFingerprint=s.SettingsFingerprint
                      AND duplicate_result.IsAccepted=1 AND duplicate_result.IsCurrent=1) AS CurrentResultCount
            FROM ranked_transfers t
            JOIN BatchOutputAccounts a
              ON a.BatchJobId=$batch AND a.AccountId=t.AssignedAccountId
            LEFT JOIN ShareOperations s ON s.TransferOperationId=t.Id
            LEFT JOIN BaiduShareResults current_result ON current_result.Id=(
                SELECT candidate.Id
                FROM BaiduShareResults candidate
                WHERE candidate.AccountId=s.AccountId
                  AND candidate.ExactItemSetFingerprint=s.ExactItemSetFingerprint
                  AND candidate.SettingsFingerprint=s.SettingsFingerprint
                  AND candidate.IsAccepted=1 AND candidate.IsCurrent=1
                ORDER BY candidate.Generation DESC,candidate.UpdatedAt DESC,candidate.Id DESC
                LIMIT 1)
            WHERE t.CanonicalRank=1
            ORDER BY t.CandidateSequence,a.OutputOrdinal,t.ResourceItemId,t.AssignedAccountId;
            """);
        Add(command, "$batch", batchJobId);
        command.Parameters.AddWithValue("$after", afterCandidateSequence);
        command.Parameters.AddWithValue("$through", throughCandidateSequence);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetInt64(8) > 1)
                throw InvalidProjection("A resource/account pair has more than one active transfer lineage.");
            if (reader.GetInt64(9) > 1)
                throw InvalidProjection("A share operation has more than one accepted current result.");
            ShareOperationStatus? shareStatus = reader.IsDBNull(4) ? null : ParseShareStatus(reader.GetString(4));
            if (shareStatus is ShareOperationStatus.Succeeded or ShareOperationStatus.ReusedExisting)
            {
                if (reader.GetInt64(9) != 1 || reader.IsDBNull(5) ||
                    string.IsNullOrWhiteSpace(reader.GetString(5)) || reader.IsDBNull(6) ||
                    string.IsNullOrWhiteSpace(reader.GetString(6)))
                    throw InvalidProjection("A successful share operation has no complete accepted current share result.");
            }
            results.Add(new(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetInt32(2),
                reader.GetString(3),
                shareStatus,
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return results;
    }

    private static BatchResultResourceProjection ReadResource(
        SqliteDataReader reader,
        IReadOnlyList<int> columnOrdinals)
    {
        if (reader.IsDBNull(2) || string.IsNullOrWhiteSpace(reader.GetString(2)))
            throw InvalidProjection("The persisted resolved output title is unavailable.");
        if (reader.IsDBNull(3) || reader.IsDBNull(4) || reader.IsDBNull(5))
            throw InvalidProjection("The persisted source-row payload is unavailable.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(reader.GetString(4));
        }
        catch (JsonException exception)
        {
            throw InvalidProjection("The persisted raw source values are invalid JSON.", exception);
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw InvalidProjection("The persisted raw source values are not an array.");
            var cells = document.RootElement.EnumerateArray().ToArray();
            var values = new List<BatchResultOriginalValue>(columnOrdinals.Count);
            foreach (var ordinal in columnOrdinals)
            {
                if (ordinal < 0 || ordinal >= cells.Length)
                    throw InvalidProjection("A source column ordinal is outside the persisted raw values.");
                var cell = cells[ordinal];
                if (cell.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    throw InvalidProjection("A persisted raw source value is not text.");
                values.Add(new(ordinal, cell.ValueKind == JsonValueKind.Null ? null : cell.GetString()));
            }
            return new(
                Guid.Parse(reader.GetString(0)),
                reader.GetInt64(1),
                values,
                reader.GetString(2),
                reader.GetString(3));
        }
    }

    private static async Task<IReadOnlyList<int>> ReadColumnOrdinalsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid batchJobId,
        CancellationToken cancellationToken)
    {
        var values = new List<int>();
        await using var command = Command(connection, transaction,
            "SELECT ColumnOrdinal FROM BatchSourceColumns WHERE BatchJobId=$batch ORDER BY ColumnOrdinal;");
        Add(command, "$batch", batchJobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) values.Add(reader.GetInt32(0));
        return values;
    }

    private static async Task<string> ReadSourceTypeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid batchJobId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT i.SourceType
            FROM BatchJobs b
            JOIN ImportSessions i ON i.Id=b.ImportSessionId
            WHERE b.Id=$batch;
            """);
        Add(command, "$batch", batchJobId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is string sourceType && !string.IsNullOrWhiteSpace(sourceType)
            ? sourceType
            : throw InvalidProjection("The batch source type is unavailable.");
    }

    private static void ValidateColumnOrdinals(IReadOnlyList<int> ordinals, string sourceType)
    {
        if (ordinals.Count == 0)
        {
            if (string.Equals(sourceType, "Text", StringComparison.Ordinal)) return;
            throw InvalidProjection("The confirmed batch has no source-column snapshot.");
        }
        var previous = -1;
        foreach (var ordinal in ordinals)
        {
            if (ordinal < 0 || ordinal <= previous)
                throw InvalidProjection("The source-column snapshot is not a strictly increasing non-negative sequence.");
            previous = ordinal;
        }
    }

    private static ShareOperationStatus ParseShareStatus(string value) =>
        Enum.TryParse<ShareOperationStatus>(value, ignoreCase: false, out var status) &&
        Enum.IsDefined(status)
            ? status
            : throw InvalidProjection("The persisted share-operation status is invalid.");

    private static void ValidateBatchId(Guid batchJobId)
    {
        if (batchJobId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(batchJobId));
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Add(SqliteCommand command, string name, Guid value) =>
        command.Parameters.AddWithValue(name, value.ToString("D"));

    private static InvalidDataException InvalidProjection(string message, Exception? inner = null) =>
        new(message, inner);
}
