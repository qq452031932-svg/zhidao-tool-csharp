using BaiduBatchTransfer.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed class SqliteLocalResultExportStore(SqliteConnectionFactory factory) : ILocalResultExportStore
{
    private readonly SqliteConnectionFactory factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async Task<LocalResultExportRecord> CreateOrGetAsync(
        LocalResultExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (!await OwnerExistsAsync(connection, transaction, request.Key, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("LOCAL_EXPORT_OWNER_NOT_FOUND");

        var existing = await LoadAsync(connection, transaction, request.Key, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(existing.TargetFileName, request.TargetFileName, StringComparison.Ordinal))
                throw new InvalidOperationException("LOCAL_EXPORT_IDEMPOTENCY_CONFLICT");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        var id = Guid.NewGuid();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO LocalResultExports(
              Id,OwnerKind,OwnerId,ExportKind,Generation,Status,TargetFileName,FinalFileName,
              SanitizedErrorCode,LeaseGeneration,LeaseOwnerId,LeaseExpiresAt,Revision,CreatedAt,UpdatedAt)
            VALUES($id,$ownerKind,$ownerId,$exportKind,$generation,'Pending',$target,NULL,NULL,0,NULL,NULL,0,$now,$now);
            """;
        AddKey(command, request.Key);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$target", request.TargetFileName);
        command.Parameters.AddWithValue("$now", request.CreatedAt.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var created = await LoadAsync(connection, transaction, request.Key, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("LOCAL_EXPORT_CREATE_MISSING");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return created;
    }

    public async Task<LocalResultExportRecord?> GetAsync(
        LocalResultExportKey key,
        CancellationToken cancellationToken = default)
    {
        key.Validate();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadAsync(connection, null, key, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Page<LocalResultExportRecord>> GetPageAsync(
        PageQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        long total;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM LocalResultExports;";
            total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }
        var items = new List<LocalResultExportRecord>(query.PageSize);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id,OwnerKind,OwnerId,ExportKind,Generation,Status,TargetFileName,FinalFileName,
                   SanitizedErrorCode,LeaseGeneration,LeaseExpiresAt,Revision,CreatedAt,UpdatedAt
            FROM LocalResultExports ORDER BY CreatedAt DESC,Id DESC LIMIT $take OFFSET $skip;
            """;
        command.Parameters.AddWithValue("$take", query.PageSize);
        command.Parameters.AddWithValue("$skip", query.Offset);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) items.Add(Read(reader));
        return new(items, total, query.PageNumber, query.PageSize);
    }

    public async Task<LocalResultExportLease?> TryAcquireAsync(
        LocalResultExportKey key,
        long expectedRevision,
        Guid leaseOwnerId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        key.Validate();
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        ValidateLeaseOwner(leaseOwnerId);
        if (leaseExpiresAt <= now) throw new ArgumentOutOfRangeException(nameof(leaseExpiresAt));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE LocalResultExports
                SET Status='InProgress',LeaseGeneration=LeaseGeneration+1,LeaseOwnerId=$leaseOwner,LeaseExpiresAt=$expires,
                    SanitizedErrorCode=NULL,Revision=Revision+1,UpdatedAt=$now
                WHERE OwnerKind=$ownerKind AND OwnerId=$ownerId AND ExportKind=$exportKind AND Generation=$generation
                  AND Revision=$revision AND Status IN ('Pending','Failed')
                  AND LeaseOwnerId IS NULL AND LeaseExpiresAt IS NULL AND UpdatedAt<=$now;
                """;
            AddKey(command, key);
            command.Parameters.AddWithValue("$revision", expectedRevision);
            command.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$expires", leaseExpiresAt.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$leaseOwner", leaseOwnerId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
        }
        var record = await LoadAsync(connection, transaction, key, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("LOCAL_EXPORT_LEASE_MISSING");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(record.Id, record.Key, record.LeaseGeneration, leaseOwnerId,
            record.LeaseExpiresAt!.Value, record.Revision);
    }

    public Task<bool> TryCompleteAsync(
        Guid exportId,
        int expectedGeneration,
        long expectedLeaseGeneration,
        Guid expectedLeaseOwnerId,
        long expectedRevision,
        DateTimeOffset expectedLeaseExpiresAt,
        string finalFileName,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        FinishAsync(exportId, expectedGeneration, expectedLeaseGeneration, expectedLeaseOwnerId,
            expectedRevision, expectedLeaseExpiresAt, "Succeeded",
            LocalResultExportValidation.SafeFileName(finalFileName, nameof(finalFileName)), null,
            updatedAt, cancellationToken);

    public Task<bool> TryFailAsync(
        Guid exportId,
        int expectedGeneration,
        long expectedLeaseGeneration,
        Guid expectedLeaseOwnerId,
        long expectedRevision,
        DateTimeOffset expectedLeaseExpiresAt,
        string sanitizedErrorCode,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        FinishAsync(exportId, expectedGeneration, expectedLeaseGeneration, expectedLeaseOwnerId,
            expectedRevision, expectedLeaseExpiresAt, "Failed", null,
            LocalResultExportValidation.SanitizedErrorCode(sanitizedErrorCode, nameof(sanitizedErrorCode)),
            updatedAt, cancellationToken);

    public async Task<int> RecoverExpiredAsync(
        DateTimeOffset now,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var leases = new List<ExpiredExportLease>(maximumCount);
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT Id,Generation,LeaseGeneration,LeaseOwnerId,Revision,LeaseExpiresAt
                FROM LocalResultExports
                WHERE Status='InProgress' AND LeaseOwnerId IS NOT NULL AND LeaseExpiresAt<=$now
                ORDER BY LeaseExpiresAt,Id LIMIT $take;
                """;
            select.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O"));
            select.Parameters.AddWithValue("$take", maximumCount);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                leases.Add(new(
                    Guid.Parse(reader.GetString(0)), reader.GetInt32(1), reader.GetInt64(2),
                    Guid.Parse(reader.GetString(3)), reader.GetInt64(4), Parse(reader.GetString(5))));
        }
        var recovered = 0;
        foreach (var lease in leases)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE LocalResultExports SET Status='Pending',LeaseOwnerId=NULL,LeaseExpiresAt=NULL,
                  SanitizedErrorCode='RECOVERED_EXPIRED_LEASE',Revision=Revision+1,UpdatedAt=$now
                WHERE Id=$id AND Generation=$generation AND LeaseGeneration=$lease
                  AND LeaseOwnerId=$owner AND Revision=$revision AND Status='InProgress'
                  AND LeaseExpiresAt=$expires AND LeaseExpiresAt<=$now;
                """;
            update.Parameters.AddWithValue("$id", lease.ExportId.ToString("D"));
            update.Parameters.AddWithValue("$generation", lease.Generation);
            update.Parameters.AddWithValue("$lease", lease.LeaseGeneration);
            update.Parameters.AddWithValue("$owner", lease.LeaseOwnerId.ToString("D"));
            update.Parameters.AddWithValue("$revision", lease.Revision);
            update.Parameters.AddWithValue("$expires", lease.LeaseExpiresAt.ToUniversalTime().ToString("O"));
            update.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O"));
            recovered += await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return recovered;
    }

    private async Task<bool> FinishAsync(
        Guid exportId,
        int expectedGeneration,
        long expectedLeaseGeneration,
        Guid expectedLeaseOwnerId,
        long expectedRevision,
        DateTimeOffset expectedLeaseExpiresAt,
        string status,
        string? finalFileName,
        string? errorCode,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        if (exportId == Guid.Empty) throw new ArgumentException("An export is required.", nameof(exportId));
        if (expectedGeneration < 1) throw new ArgumentOutOfRangeException(nameof(expectedGeneration));
        if (expectedLeaseGeneration < 1) throw new ArgumentOutOfRangeException(nameof(expectedLeaseGeneration));
        ValidateLeaseOwner(expectedLeaseOwnerId);
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE LocalResultExports
            SET Status=$status,FinalFileName=$final,SanitizedErrorCode=$error,
                LeaseOwnerId=NULL,LeaseExpiresAt=NULL,
                Revision=Revision+1,UpdatedAt=$updated
            WHERE Id=$id AND Generation=$generation AND Status='InProgress' AND LeaseGeneration=$lease
              AND LeaseOwnerId=$owner AND Revision=$revision AND LeaseExpiresAt=$expires
              AND LeaseExpiresAt>=$updated AND UpdatedAt<=$updated;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$final", (object?)finalFileName ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", updatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$id", exportId.ToString("D"));
        command.Parameters.AddWithValue("$generation", expectedGeneration);
        command.Parameters.AddWithValue("$lease", expectedLeaseGeneration);
        command.Parameters.AddWithValue("$owner", expectedLeaseOwnerId.ToString("D"));
        command.Parameters.AddWithValue("$revision", expectedRevision);
        command.Parameters.AddWithValue("$expires", expectedLeaseExpiresAt.ToUniversalTime().ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async Task<bool> OwnerExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalResultExportKey key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = key.OwnerKind == LocalResultOwnerKind.TransferBatch
            ? "SELECT COUNT(*) FROM BatchJobs WHERE Id=$id;"
            : "SELECT COUNT(*) FROM StandaloneShareBatches WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", key.OwnerId.ToString("D"));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }

    private static async Task<LocalResultExportRecord?> LoadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        LocalResultExportKey key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT Id,OwnerKind,OwnerId,ExportKind,Generation,Status,TargetFileName,FinalFileName,
                   SanitizedErrorCode,LeaseGeneration,LeaseExpiresAt,Revision,CreatedAt,UpdatedAt
            FROM LocalResultExports
            WHERE OwnerKind=$ownerKind AND OwnerId=$ownerId AND ExportKind=$exportKind AND Generation=$generation;
            """;
        AddKey(command, key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static LocalResultExportRecord Read(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        new(Enum.Parse<LocalResultOwnerKind>(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
            Enum.Parse<LocalResultExportKind>(reader.GetString(3)), reader.GetInt32(4)),
        Enum.Parse<LocalResultExportStatus>(reader.GetString(5)),
        reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetInt64(9),
        reader.IsDBNull(10) ? null : Parse(reader.GetString(10)), reader.GetInt64(11),
        Parse(reader.GetString(12)), Parse(reader.GetString(13)));

    private static void AddKey(SqliteCommand command, LocalResultExportKey key)
    {
        command.Parameters.AddWithValue("$ownerKind", key.OwnerKind.ToString());
        command.Parameters.AddWithValue("$ownerId", key.OwnerId.ToString("D"));
        command.Parameters.AddWithValue("$exportKind", key.ExportKind.ToString());
        command.Parameters.AddWithValue("$generation", key.Generation);
    }

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);

    private static void ValidateLeaseOwner(Guid leaseOwnerId)
    {
        if (leaseOwnerId == Guid.Empty) throw new ArgumentException("A lease owner is required.", nameof(leaseOwnerId));
    }

    private sealed record ExpiredExportLease(
        Guid ExportId,
        int Generation,
        long LeaseGeneration,
        Guid LeaseOwnerId,
        long Revision,
        DateTimeOffset LeaseExpiresAt);
}
