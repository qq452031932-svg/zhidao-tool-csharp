using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using Microsoft.Data.Sqlite;
using System.Text.Json;

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

    public async Task<IReadOnlyList<LocalResultOperationEvidence>> GetProvenOperationEvidenceAsync(
        LocalResultExportKey key,
        IReadOnlyList<LocalResultOperationIdentity> candidates,
        CancellationToken cancellationToken = default)
    {
        key.Validate();
        ArgumentNullException.ThrowIfNull(candidates);
        if (!IsSupported(key) || candidates.Count == 0) return [];
        var evidence = new List<LocalResultOperationEvidence>(candidates.Count);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var candidate in candidates.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.AccountId == Guid.Empty || candidate.OperationId == Guid.Empty ||
                candidate.Generation < 1) continue;
            var lineage = await LoadLineageAsync(connection, key, candidate, cancellationToken)
                .ConfigureAwait(false);
            if (lineage is null) continue;
            var proven = lineage.Value;
            if (!LocalResultWorkbookManifest.IsFingerprint(proven.Fingerprint) || proven.Count < 1) continue;
            var exactIds = new List<string>(proven.Count);
            var canonicalOrderIsValid = true;
            await using (var items = connection.CreateCommand())
            {
                items.CommandText = """
                    SELECT RemoteItemId,CanonicalSortKey FROM WebAutomationAttemptAcceptedRemoteItems
                    WHERE OperationId=$operation AND AccountId=$account AND Generation=$generation
                    ORDER BY CanonicalSortKey;
                    """;
                AddIdentity(items, candidate);
                await using var reader = await items.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var remoteItemId = reader.GetString(0);
                    if (!reader.GetFieldValue<byte[]>(1).AsSpan().SequenceEqual(
                            RemoteItemIdentitySet.GetCanonicalSortKey(remoteItemId)))
                        canonicalOrderIsValid = false;
                    exactIds.Add(remoteItemId);
                }
            }
            if (!canonicalOrderIsValid || exactIds.Count != proven.Count) continue;
            if (proven.RemoteItemIdsJson is not null &&
                !MatchesPersistedTransferResult(proven.RemoteItemIdsJson, exactIds)) continue;
            string computed;
            try
            {
                computed = RemoteItemIdentitySet.ComputeFingerprintFromCanonicalOrder(exactIds);
            }
            catch (ArgumentException)
            {
                continue;
            }
            if (!string.Equals(computed, proven.Fingerprint, StringComparison.Ordinal)) continue;
            evidence.Add(new(
                candidate.AdapterKind, candidate.AccountId, candidate.OperationId,
                candidate.Generation, computed));
        }
        return evidence
            .OrderBy(item => item.AdapterKind, StringComparer.Ordinal)
            .ThenBy(item => item.AccountId)
            .ThenBy(item => item.OperationId)
            .ThenBy(item => item.Generation)
            .ToArray();
    }

    private static async Task<(string Fingerprint, int Count, string? RemoteItemIdsJson)?> LoadLineageAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        LocalResultExportKey key,
        LocalResultOperationIdentity candidate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = (key.OwnerKind, candidate.AdapterKind) switch
        {
            (LocalResultOwnerKind.TransferBatch, "BaiduTransfer") => """
                SELECT attempt.AcceptanceFingerprint,attempt.AcceptedItemCount,operation.RemoteItemIdsJson
                FROM TransferOperations operation
                JOIN WebAutomationAttempts attempt
                  ON attempt.OperationId=operation.Id
                 AND attempt.AccountId=operation.AssignedAccountId
                 AND attempt.Generation=operation.AttemptCount
                 AND attempt.OwnerKind='Transfer'
                WHERE operation.Id=$operation AND operation.BatchJobId=$owner
                  AND operation.AssignedAccountId=$account AND operation.AttemptCount=$generation
                  AND operation.Status='Succeeded' AND operation.SubmitState='ResultReceived'
                  AND operation.TargetSlotState='Fulfilled' AND operation.OutcomeKind='NewTransfer'
                  AND operation.RemoteItemIdsJson IS NOT NULL
                  AND attempt.State='Succeeded' AND attempt.AcceptanceFingerprint IS NOT NULL
                  AND attempt.AcceptancePersistedAt IS NOT NULL AND attempt.AcceptedItemCount>0;
                """,
            (LocalResultOwnerKind.TransferBatch, "BaiduShare") => """
                SELECT attempt.AcceptanceFingerprint,attempt.AcceptedItemCount,NULL
                FROM ShareOperations operation
                JOIN BaiduShareResults result
                  ON result.ShareOperationId=operation.Id AND result.Generation=operation.Generation
                 AND result.BatchJobId=operation.BatchJobId AND result.AccountId=operation.AccountId
                 AND result.ExactItemSetFingerprint=operation.ExactItemSetFingerprint
                 AND result.SettingsFingerprint=operation.SettingsFingerprint
                 AND result.IsAccepted=1 AND result.IsCurrent=1
                JOIN WebAutomationAttempts attempt
                  ON attempt.OperationId=operation.Id AND attempt.AccountId=operation.AccountId
                 AND attempt.Generation=operation.Generation AND attempt.OwnerKind='TransferShare'
                 AND attempt.AcceptanceFingerprint=result.ExactItemSetFingerprint
                WHERE operation.Id=$operation AND operation.BatchJobId=$owner
                  AND operation.AccountId=$account AND operation.Generation=$generation
                  AND operation.Status IN ('Succeeded','ReusedExisting')
                  AND attempt.State='Succeeded' AND attempt.AcceptancePersistedAt IS NOT NULL
                  AND attempt.AcceptedItemCount>0;
                """,
            (LocalResultOwnerKind.StandaloneShareBatch, "BaiduShare") => """
                SELECT attempt.AcceptanceFingerprint,attempt.AcceptedItemCount,NULL
                FROM StandaloneShareOperations operation
                JOIN StandaloneShareResults result
                  ON result.OperationId=operation.Id AND result.BatchId=operation.BatchId
                 AND result.AccountId=operation.AccountId AND result.Generation=operation.Generation
                 AND result.ExactItemSetFingerprint=operation.ExactItemSetFingerprint
                 AND result.SettingsFingerprint=operation.SettingsFingerprint
                 AND result.IsPermanent=1 AND result.IsAccessUnlimited=1
                JOIN WebAutomationAttempts attempt
                  ON attempt.OperationId=operation.Id AND attempt.AccountId=operation.AccountId
                 AND attempt.Generation=operation.Generation AND attempt.OwnerKind='StandaloneShare'
                 AND attempt.AcceptanceFingerprint=result.ExactItemSetFingerprint
                WHERE operation.Id=$operation AND operation.BatchId=$owner
                  AND operation.AccountId=$account AND operation.Generation=$generation
                  AND operation.Status='Succeeded' AND operation.SubmissionState='Completed'
                  AND attempt.State='Succeeded' AND attempt.AcceptancePersistedAt IS NOT NULL
                  AND attempt.AcceptedItemCount>0;
                """,
            _ => string.Empty,
        };
        if (command.CommandText.Length == 0) return null;
        AddIdentity(command, candidate);
        command.Parameters.AddWithValue("$owner", key.OwnerId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return (reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static bool MatchesPersistedTransferResult(
        string remoteItemIdsJson,
        IReadOnlyList<string> acceptedIds)
    {
        if (remoteItemIdsJson.Length > 1_000_000) return false;
        try
        {
            var persistedIds = JsonSerializer.Deserialize<string[]>(remoteItemIdsJson);
            return persistedIds is not null &&
                   persistedIds.SequenceEqual(acceptedIds, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void AddIdentity(
        Microsoft.Data.Sqlite.SqliteCommand command,
        LocalResultOperationIdentity candidate)
    {
        command.Parameters.AddWithValue("$operation", candidate.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$account", candidate.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$generation", candidate.Generation);
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
