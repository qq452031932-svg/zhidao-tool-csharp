using BaiduBatchTransfer.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed class SqliteLocalResultDestinationStore(SqliteConnectionFactory factory) : ILocalResultDestinationStore
{
    private readonly SqliteConnectionFactory factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async Task SetAsync(
        LocalResultDestinationRecord destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var owner = connection.CreateCommand())
        {
            owner.Transaction = transaction;
            owner.CommandText = "SELECT COUNT(*) FROM BatchJobs WHERE Id=$id;";
            owner.Parameters.AddWithValue("$id", destination.BatchJobId.ToString("D"));
            if (Convert.ToInt64(await owner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 1)
                throw new InvalidOperationException("RESULT_DESTINATION_OWNER_NOT_FOUND");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO BatchResultDestinations(BatchJobId,DirectoryReference,Revision,CreatedAt,UpdatedAt)
            VALUES($id,$directory,0,$created,$updated)
            ON CONFLICT(BatchJobId) DO UPDATE SET
              DirectoryReference=excluded.DirectoryReference,
              Revision=BatchResultDestinations.Revision+1,
              UpdatedAt=excluded.UpdatedAt
            WHERE BatchResultDestinations.UpdatedAt<=excluded.UpdatedAt;
            """;
        command.Parameters.AddWithValue("$id", destination.BatchJobId.ToString("D"));
        command.Parameters.AddWithValue("$directory", destination.DirectoryReference);
        command.Parameters.AddWithValue("$created", destination.CreatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$updated", destination.UpdatedAt.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<LocalResultDestinationRecord?> GetAsync(
        Guid batchJobId,
        CancellationToken cancellationToken = default)
    {
        if (batchJobId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(batchJobId));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DirectoryReference,Revision,CreatedAt,UpdatedAt
            FROM BatchResultDestinations WHERE BatchJobId=$id;
            """;
        command.Parameters.AddWithValue("$id", batchJobId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new(batchJobId, reader.GetString(0), reader.GetInt64(1), Parse(reader.GetString(2)), Parse(reader.GetString(3)));
    }

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);
}
