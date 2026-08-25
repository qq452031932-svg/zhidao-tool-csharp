using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;

namespace BaiduBatchTransfer.App.Features.BrowserAutomation;

public sealed class SqliteShareAutomationAcceptanceEvidenceStore(
    SqliteConnectionFactory factory,
    TimeProvider? timeProvider = null) : IShareAutomationAcceptanceEvidenceStore
{
    private readonly SqliteConnectionFactory factory = factory ?? throw new ArgumentNullException(nameof(factory));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task PersistAsync(
        ShareOperationAutomationContext context,
        ShareOfficialAcceptance acceptance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(acceptance);
        context.Validate();
        if (acceptance is not { IsAccepted: true, ExactRemoteItems: not null, EffectiveSettings: not null } ||
            acceptance.ExactRemoteItems != context.ExactRemoteItems)
            throw new InvalidOperationException("Share acceptance evidence does not match the operation identity.");
        var settings = acceptance.EffectiveSettings;
        if (settings.IsPermanent != true || settings.IsAccessUnlimited != true ||
            !PermanentShareRequest.IsValidFixedExtractionCode(settings.ExtractionCode))
            throw new InvalidOperationException("Share acceptance settings are not permanent, unlimited, and complete.");
        var mode = settings.ExtractionCodeBehavior is ShareExtractionCodeBehavior.Random or ShareExtractionCodeBehavior.Fixed
            ? settings.ExtractionCodeBehavior.ToString()
            : throw new InvalidOperationException("Share acceptance settings are incomplete.");
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO WebAutomationShareAcceptances(
              OperationId,AccountId,Generation,OwnerKind,AdapterRequestId,ShareId,ShareUrl,ExtractionCode,
              ExtractionCodeMode,ExactItemSetFingerprint,ObservationFingerprint,AcceptedAt)
            SELECT $operation,$account,$generation,$owner,$adapter,$share,$url,$code,$mode,$items,$observation,$accepted
            WHERE EXISTS(
              SELECT 1 FROM WebAutomationAttempts attempt
              WHERE attempt.OperationId=$operation AND attempt.AccountId=$account AND attempt.Generation=$generation
                AND attempt.OwnerKind=$owner AND attempt.State='Submitting')
            ON CONFLICT(OperationId,AccountId,Generation) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$operation", context.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$account", context.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$generation", context.Generation);
        command.Parameters.AddWithValue("$owner", context.OwnerKind.ToString());
        command.Parameters.AddWithValue("$adapter", acceptance.AdapterRequestId!);
        command.Parameters.AddWithValue("$share", acceptance.ShareId!);
        command.Parameters.AddWithValue("$url", acceptance.ShareUrl!);
        command.Parameters.AddWithValue("$code", settings.ExtractionCode!);
        command.Parameters.AddWithValue("$mode", mode);
        command.Parameters.AddWithValue("$items", context.ExactRemoteItems.Fingerprint);
        command.Parameters.AddWithValue("$observation", acceptance.ObservationFingerprint);
        command.Parameters.AddWithValue("$accepted", timeProvider.GetUtcNow().ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var stored = await GetAsync(context, cancellationToken).ConfigureAwait(false);
        if (stored is null || !Same(stored, acceptance))
            throw new InvalidOperationException("Share acceptance evidence could not be persisted for the current attempt.");
    }

    public async Task<ShareOfficialAcceptance?> GetAsync(
        ShareOperationAutomationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Validate();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT AdapterRequestId,ShareId,ShareUrl,ExtractionCode,ExtractionCodeMode,
                   ExactItemSetFingerprint,ObservationFingerprint
            FROM WebAutomationShareAcceptances
            WHERE OperationId=$operation AND AccountId=$account AND Generation=$generation AND OwnerKind=$owner;
            """;
        command.Parameters.AddWithValue("$operation", context.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$account", context.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$generation", context.Generation);
        command.Parameters.AddWithValue("$owner", context.OwnerKind.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        if (!string.Equals(reader.GetString(5), context.ExactRemoteItems.Fingerprint, StringComparison.Ordinal))
            return null;
        var behavior = Enum.Parse<ShareExtractionCodeBehavior>(reader.GetString(4));
        return ShareOfficialAcceptance.Accepted(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), context.ExactRemoteItems,
            new EffectiveShareSettings(true, true, behavior, reader.GetString(3)), reader.GetString(6));
    }

    public async Task<bool> TryPersistRecoveredAsync(
        ShareOperationAutomationContext context,
        ShareOfficialAcceptance acceptance,
        CancellationToken cancellationToken = default)
    {
        Validate(context, acceptance);
        var settings = acceptance.EffectiveSettings!;
        var now = timeProvider.GetUtcNow().ToUniversalTime().ToString("O");
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            await using (var evidence = connection.CreateCommand())
            {
                evidence.Transaction = transaction;
                evidence.CommandText = """
                    INSERT INTO WebAutomationShareAcceptances(
                      OperationId,AccountId,Generation,OwnerKind,AdapterRequestId,ShareId,ShareUrl,ExtractionCode,
                      ExtractionCodeMode,ExactItemSetFingerprint,ObservationFingerprint,AcceptedAt)
                    SELECT $operation,$account,$generation,$owner,$adapter,$share,$url,$code,$mode,$items,$observation,$accepted
                    WHERE EXISTS(SELECT 1 FROM WebAutomationAttempts attempt
                      WHERE attempt.OperationId=$operation AND attempt.AccountId=$account
                        AND attempt.Generation=$generation AND attempt.OwnerKind=$owner
                        AND attempt.State='Submitting' AND attempt.BaselinePersistedAt IS NOT NULL)
                      AND NOT EXISTS(SELECT 1 FROM WebAutomationShareAcceptances existing
                        WHERE existing.OperationId=$operation AND existing.AccountId=$account
                          AND existing.Generation=$generation);
                    """;
                AddEvidenceParameters(evidence, context, acceptance, settings, now);
                var inserted = await evidence.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
                if (!inserted && (!await SubmittingAttemptExistsAsync(
                        connection, transaction, context, cancellationToken).ConfigureAwait(false) ||
                    !await ExistingEvidenceMatchesAsync(
                        connection, transaction, context, acceptance, cancellationToken).ConfigureAwait(false)))
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return false;
                }
            }

            for (var index = 0; index < context.ExactRemoteItems.Items.Length; index++)
            {
                var item = context.ExactRemoteItems.Items[index];
                await using var acceptedItem = connection.CreateCommand();
                acceptedItem.Transaction = transaction;
                acceptedItem.CommandText = """
                    INSERT INTO WebAutomationAttemptAcceptedRemoteItems(
                      OperationId,AccountId,Generation,ItemOrdinal,RemoteItemId,CanonicalSortKey)
                    VALUES($operation,$account,$generation,$ordinal,$item,$sort);
                    """;
                acceptedItem.Parameters.AddWithValue("$operation", context.OperationId.ToString("D"));
                acceptedItem.Parameters.AddWithValue("$account", context.AccountId.ToString("D"));
                acceptedItem.Parameters.AddWithValue("$generation", context.Generation);
                acceptedItem.Parameters.AddWithValue("$ordinal", index);
                acceptedItem.Parameters.AddWithValue("$item", item.ExactId);
                acceptedItem.Parameters.AddWithValue("$sort", RemoteItemIdentitySet.GetCanonicalSortKey(item.ExactId));
                await acceptedItem.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE WebAutomationAttempts
                SET State='ResultPending',AcceptanceFingerprint=$items,AcceptedItemCount=$count,
                    AcceptancePersistedAt=$updated,ObservationFingerprint=$observation,
                    IsWaitingForUser=0,ResumeStage=NULL,Revision=Revision+1,UpdatedAt=$updated
                WHERE OperationId=$operation AND AccountId=$account AND Generation=$generation
                  AND OwnerKind=$owner AND State='Submitting' AND BaselinePersistedAt IS NOT NULL;
                """;
            update.Parameters.AddWithValue("$operation", context.OperationId.ToString("D"));
            update.Parameters.AddWithValue("$account", context.AccountId.ToString("D"));
            update.Parameters.AddWithValue("$generation", context.Generation);
            update.Parameters.AddWithValue("$owner", context.OwnerKind.ToString());
            update.Parameters.AddWithValue("$items", context.ExactRemoteItems.Fingerprint);
            update.Parameters.AddWithValue("$count", context.ExactRemoteItems.Count);
            update.Parameters.AddWithValue("$observation", acceptance.ObservationFingerprint);
            update.Parameters.AddWithValue("$updated", now);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static void Validate(
        ShareOperationAutomationContext context,
        ShareOfficialAcceptance acceptance)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(acceptance);
        context.Validate();
        if (acceptance is not { IsAccepted: true, ExactRemoteItems: not null, EffectiveSettings: not null } ||
            acceptance.ExactRemoteItems != context.ExactRemoteItems)
            throw new InvalidOperationException("Share acceptance evidence does not match the operation identity.");
        var settings = acceptance.EffectiveSettings;
        if (settings.IsPermanent != true || settings.IsAccessUnlimited != true ||
            !PermanentShareRequest.IsValidFixedExtractionCode(settings.ExtractionCode))
            throw new InvalidOperationException("Share acceptance settings are not permanent, unlimited, and complete.");
    }

    private static void AddEvidenceParameters(
        Microsoft.Data.Sqlite.SqliteCommand command,
        ShareOperationAutomationContext context,
        ShareOfficialAcceptance acceptance,
        EffectiveShareSettings settings,
        string acceptedAt)
    {
        command.Parameters.AddWithValue("$operation", context.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$account", context.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$generation", context.Generation);
        command.Parameters.AddWithValue("$owner", context.OwnerKind.ToString());
        command.Parameters.AddWithValue("$adapter", acceptance.AdapterRequestId!);
        command.Parameters.AddWithValue("$share", acceptance.ShareId!);
        command.Parameters.AddWithValue("$url", acceptance.ShareUrl!);
        command.Parameters.AddWithValue("$code", settings.ExtractionCode!);
        command.Parameters.AddWithValue("$mode", settings.ExtractionCodeBehavior.ToString());
        command.Parameters.AddWithValue("$items", context.ExactRemoteItems.Fingerprint);
        command.Parameters.AddWithValue("$observation", acceptance.ObservationFingerprint);
        command.Parameters.AddWithValue("$accepted", acceptedAt);
    }

    private static async Task<bool> ExistingEvidenceMatchesAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        ShareOperationAutomationContext context,
        ShareOfficialAcceptance acceptance,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT AdapterRequestId,ShareId,ShareUrl,ExtractionCode,ExtractionCodeMode,
                   ExactItemSetFingerprint,ObservationFingerprint
            FROM WebAutomationShareAcceptances
            WHERE OperationId=$operation AND AccountId=$account AND Generation=$generation AND OwnerKind=$owner;
            """;
        command.Parameters.AddWithValue("$operation", context.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$account", context.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$generation", context.Generation);
        command.Parameters.AddWithValue("$owner", context.OwnerKind.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return false;
        var settings = acceptance.EffectiveSettings!;
        // Adapter request ids and observation fingerprints belong to the original acceptance event.
        // A crash recovery reconstructs only the stable official-list identity and must preserve those
        // stronger, already-persisted fields rather than requiring them to equal reconstructed values.
        return string.Equals(reader.GetString(1), acceptance.ShareId, StringComparison.Ordinal) &&
               string.Equals(reader.GetString(2), acceptance.ShareUrl, StringComparison.Ordinal) &&
               string.Equals(reader.GetString(3), settings.ExtractionCode, StringComparison.Ordinal) &&
               string.Equals(reader.GetString(4), settings.ExtractionCodeBehavior.ToString(), StringComparison.Ordinal) &&
               string.Equals(reader.GetString(5), context.ExactRemoteItems.Fingerprint, StringComparison.Ordinal);
    }

    private static async Task<bool> SubmittingAttemptExistsAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        ShareOperationAutomationContext context,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM WebAutomationAttempts
            WHERE OperationId=$operation AND AccountId=$account AND Generation=$generation
              AND OwnerKind=$owner AND State='Submitting' AND BaselinePersistedAt IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$operation", context.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$account", context.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$generation", context.Generation);
        command.Parameters.AddWithValue("$owner", context.OwnerKind.ToString());
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }

    private static bool Same(ShareOfficialAcceptance left, ShareOfficialAcceptance right) =>
        left.Status == right.Status &&
        string.Equals(left.AdapterRequestId, right.AdapterRequestId, StringComparison.Ordinal) &&
        string.Equals(left.ShareId, right.ShareId, StringComparison.Ordinal) &&
        string.Equals(left.ShareUrl, right.ShareUrl, StringComparison.Ordinal) &&
        left.ExactRemoteItems == right.ExactRemoteItems &&
        left.EffectiveSettings == right.EffectiveSettings &&
        string.Equals(left.ObservationFingerprint, right.ObservationFingerprint, StringComparison.Ordinal);
}
