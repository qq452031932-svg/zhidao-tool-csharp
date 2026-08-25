using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed class SqliteLocalResultExportOwnerRepository(SqliteConnectionFactory factory) :
    ILocalResultExportOwnerRepository
{
    private readonly SqliteConnectionFactory factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async Task<LocalResultExportOwnerSnapshot?> GetAsync(
        LocalResultExportKey key,
        CancellationToken cancellationToken = default)
    {
        key.Validate();
        if (!IsSupported(key)) return null;
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = key.OwnerKind == LocalResultOwnerKind.TransferBatch
            ? """
              SELECT destination.DirectoryReference,batch.Status,batch.ResourceCount
              FROM BatchJobs batch
              JOIN BatchResultDestinations destination ON destination.BatchJobId=batch.Id
              WHERE batch.Id=$id;
              """
            : """
              SELECT batch.ResultDirectoryReference,batch.Status,batch.OperationCount
              FROM StandaloneShareBatches batch
              WHERE batch.Id=$id;
              """;
        command.Parameters.AddWithValue("$id", key.OwnerId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new(
            key,
            reader.GetString(0),
            IsTerminal(key.OwnerKind, reader.GetString(1)),
            reader.GetInt64(2));
    }

    public async Task<Page<LocalResultExportOwnerSnapshot>> GetReadyPageAsync(
        PageQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        long total;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = """
                SELECT
                  (SELECT COUNT(*) FROM BatchJobs batch
                   JOIN BatchResultDestinations destination ON destination.BatchJobId=batch.Id
                   WHERE batch.Status IN ('Completed','CompletedWithShortfall'))
                  +
                  (SELECT COUNT(*) FROM StandaloneShareBatches batch
                   WHERE batch.Status IN ('Completed','CompletedWithFailures'));
                """;
            total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }

        var items = new List<LocalResultExportOwnerSnapshot>(query.PageSize);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT OwnerKind,OwnerId,ExportKind,Generation,DirectoryReference,ExpectedRowCount FROM (
              SELECT 'TransferBatch' OwnerKind,batch.Id OwnerId,'ReplacementWorkbook' ExportKind,
                     1 Generation,destination.DirectoryReference DirectoryReference,
                     batch.ResourceCount ExpectedRowCount,0 SortKind
              FROM BatchJobs batch
              JOIN BatchResultDestinations destination ON destination.BatchJobId=batch.Id
              WHERE batch.Status IN ('Completed','CompletedWithShortfall')
              UNION ALL
              SELECT 'StandaloneShareBatch',batch.Id,'StandaloneShareWorkbook',1,
                     batch.ResultDirectoryReference,batch.OperationCount,1
              FROM StandaloneShareBatches batch
              WHERE batch.Status IN ('Completed','CompletedWithFailures')
            ) ready
            ORDER BY SortKind,OwnerId LIMIT $take OFFSET $skip;
            """;
        command.Parameters.AddWithValue("$take", query.PageSize);
        command.Parameters.AddWithValue("$skip", query.Offset);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = new LocalResultExportKey(
                Enum.Parse<LocalResultOwnerKind>(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                Enum.Parse<LocalResultExportKind>(reader.GetString(2)),
                reader.GetInt32(3));
            items.Add(new(key, reader.GetString(4), true, reader.GetInt64(5)));
        }
        return new(items, total, query.PageNumber, query.PageSize);
    }

    private static bool IsSupported(LocalResultExportKey key) => key switch
    {
        { OwnerKind: LocalResultOwnerKind.TransferBatch,
          ExportKind: LocalResultExportKind.ReplacementWorkbook } => true,
        { OwnerKind: LocalResultOwnerKind.StandaloneShareBatch,
          ExportKind: LocalResultExportKind.StandaloneShareWorkbook } => true,
        _ => false,
    };

    private static bool IsTerminal(LocalResultOwnerKind kind, string status) => kind switch
    {
        LocalResultOwnerKind.TransferBatch => status is "Completed" or "CompletedWithShortfall",
        LocalResultOwnerKind.StandaloneShareBatch => status is "Completed" or "CompletedWithFailures",
        _ => false,
    };
}
