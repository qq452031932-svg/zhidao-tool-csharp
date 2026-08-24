using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed class SqliteWebAutomationAttemptStore : IWebAutomationAttemptStore
{
    private readonly SqliteConnectionFactory factory;
    private readonly int maximumBaselineItemCount;
    private readonly int baselineReadPageSize;

    public SqliteWebAutomationAttemptStore(
        SqliteConnectionFactory factory,
        int maximumBaselineItemCount = 100_000,
        int baselineReadPageSize = 1_000)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        if (maximumBaselineItemCount < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumBaselineItemCount));
        if (baselineReadPageSize is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(baselineReadPageSize));
        this.maximumBaselineItemCount = maximumBaselineItemCount;
        this.baselineReadPageSize = baselineReadPageSize;
    }

    public async Task<bool> BeginAsync(
        WebAutomationAttemptRecord attempt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.State != WebAutomationAttemptState.Preparing || attempt.Revision != 0 || attempt.Baseline is not null)
            throw new ArgumentException("A new browser attempt must start in Preparing without evidence.", nameof(attempt));

        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (!await OwnerExistsAsync(connection, transaction, attempt, cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO WebAutomationAttempts(
              OperationId,AccountId,Generation,OwnerKind,State,ResumeStage,ObservationFingerprint,
              BaselineFingerprint,BaselineItemCount,BaselinePersistedAt,IsWaitingForUser,Revision,CreatedAt,UpdatedAt)
            VALUES($operation,$account,$generation,$owner,$state,NULL,NULL,NULL,0,NULL,0,0,$created,$updated);
            """;
        AddIdentity(command, attempt.OperationId, attempt.AccountId, attempt.Generation);
        command.Parameters.AddWithValue("$owner", attempt.OwnerKind.ToString());
        command.Parameters.AddWithValue("$state", attempt.State.ToString());
        command.Parameters.AddWithValue("$created", attempt.CreatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$updated", attempt.UpdatedAt.ToUniversalTime().ToString("O"));
        try
        {
            var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
            if (changed) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            else await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return changed;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    public async Task<WebAutomationAttemptRecord?> GetAsync(
        Guid operationId,
        Guid accountId,
        int generation,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(operationId, accountId, generation);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadAsync(connection, null, operationId, accountId, generation, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Page<WebAutomationAttemptRecord>> GetRecoveryPageAsync(
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
                SELECT COUNT(*) FROM WebAutomationAttempts
                WHERE State NOT IN ('Succeeded','DefinitelyNotSubmitted');
                """;
            total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }

        var identities = new List<(Guid OperationId, Guid AccountId, int Generation)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT OperationId,AccountId,Generation FROM WebAutomationAttempts
                WHERE State NOT IN ('Succeeded','DefinitelyNotSubmitted')
                ORDER BY UpdatedAt,OperationId,AccountId,Generation LIMIT $take OFFSET $skip;
                """;
            command.Parameters.AddWithValue("$take", query.PageSize);
            command.Parameters.AddWithValue("$skip", query.Offset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                identities.Add((Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt32(2)));
        }

        var items = new List<WebAutomationAttemptRecord>(identities.Count);
        foreach (var identity in identities)
        {
            var item = await LoadAsync(connection, null, identity.OperationId, identity.AccountId,
                identity.Generation, cancellationToken).ConfigureAwait(false);
            if (item is not null) items.Add(item);
        }
        return new(items, total, query.PageNumber, query.PageSize);
    }

    public async Task<bool> TryPersistBaselineAsync(
        Guid operationId,
        Guid accountId,
        int generation,
        long expectedRevision,
        WebAutomationBaseline baseline,
        string observationFingerprint,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(operationId, accountId, generation);
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        ArgumentNullException.ThrowIfNull(baseline);
        WebAutomationAttemptRecord.ValidateFingerprint(observationFingerprint, nameof(observationFingerprint));
        if (baseline.Items.Length > maximumBaselineItemCount)
            throw new ArgumentOutOfRangeException(nameof(baseline), "WEB_BASELINE_TOO_LARGE");

        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await LoadAsync(connection, transaction, operationId, accountId, generation, cancellationToken)
            .ConfigureAwait(false);
        if (current is null || current.State != WebAutomationAttemptState.Preparing ||
            current.Revision != expectedRevision || updatedAt < current.UpdatedAt)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        for (var index = 0; index < baseline.Items.Length; index++)
        {
            var item = baseline.Items[index];
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO WebAutomationAttemptRemoteItems(
                  OperationId,AccountId,Generation,ItemOrdinal,RemoteItemId,CanonicalSortKey)
                VALUES($operation,$account,$generation,$ordinal,$item,$sort);
                """;
            AddIdentity(insert, operationId, accountId, generation);
            insert.Parameters.AddWithValue("$ordinal", index);
            insert.Parameters.AddWithValue("$item", item.ExactId);
            insert.Parameters.AddWithValue("$sort", item.CanonicalSortKey.ToArray());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE WebAutomationAttempts
            SET State='BaselinePersisted',BaselineFingerprint=$baseline,BaselineItemCount=$count,
                BaselinePersistedAt=$updated,ObservationFingerprint=$observation,
                IsWaitingForUser=0,Revision=Revision+1,UpdatedAt=$updated
            WHERE OperationId=$operation AND AccountId=$account AND Generation=$generation
              AND State='Preparing' AND Revision=$revision;
            """;
        AddIdentity(update, operationId, accountId, generation);
        update.Parameters.AddWithValue("$baseline", baseline.Fingerprint);
        update.Parameters.AddWithValue("$count", baseline.Items.Length);
        update.Parameters.AddWithValue("$observation", observationFingerprint);
        update.Parameters.AddWithValue("$revision", expectedRevision);
        update.Parameters.AddWithValue("$updated", updatedAt.ToUniversalTime().ToString("O"));
        var changed = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (changed) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        else await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    public async Task<bool> TryAdvanceAsync(
        Guid operationId,
        Guid accountId,
        int generation,
        WebAutomationAttemptState expectedState,
        long expectedRevision,
        WebAutomationAttemptState nextState,
        string? observationFingerprint,
        bool isWaitingForUser,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(operationId, accountId, generation);
        if (!Enum.IsDefined(expectedState)) throw new ArgumentOutOfRangeException(nameof(expectedState));
        if (!Enum.IsDefined(nextState)) throw new ArgumentOutOfRangeException(nameof(nextState));
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        WebAutomationAttemptRecord.ValidateFingerprint(observationFingerprint, nameof(observationFingerprint));
        if (nextState == WebAutomationAttemptState.UserHandoff)
        {
            if (!isWaitingForUser || !WebAutomationAttemptRecord.CanResumeStage(expectedState)) return false;
        }
        else if (isWaitingForUser)
        {
            return false;
        }
        if (!IsAllowed(expectedState, nextState)) return false;

        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE WebAutomationAttempts
            SET State=$next,ObservationFingerprint=COALESCE($observation,ObservationFingerprint),
                ResumeStage=CASE WHEN $next='UserHandoff' THEN $expected ELSE NULL END,
                IsWaitingForUser=$waiting,Revision=Revision+1,UpdatedAt=$updated
            WHERE OperationId=$operation AND AccountId=$account AND Generation=$generation
              AND State=$expected AND Revision=$revision AND UpdatedAt<=$updated
              AND ($requiresBaseline=0 OR BaselinePersistedAt IS NOT NULL);
            """;
        AddIdentity(command, operationId, accountId, generation);
        command.Parameters.AddWithValue("$next", nextState.ToString());
        command.Parameters.AddWithValue("$expected", expectedState.ToString());
        command.Parameters.AddWithValue("$revision", expectedRevision);
        command.Parameters.AddWithValue("$observation", (object?)observationFingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$waiting", isWaitingForUser ? 1 : 0);
        command.Parameters.AddWithValue("$updated", updatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$requiresBaseline", RequiresBaseline(nextState) ? 1 : 0);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> TryResumeFromUserHandoffAsync(
        Guid operationId,
        Guid accountId,
        int generation,
        long expectedRevision,
        WebAutomationAttemptState resumeStage,
        string? observationFingerprint,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(operationId, accountId, generation);
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        if (!Enum.IsDefined(resumeStage)) throw new ArgumentOutOfRangeException(nameof(resumeStage));
        WebAutomationAttemptRecord.ValidateFingerprint(observationFingerprint, nameof(observationFingerprint));
        if (!WebAutomationAttemptRecord.CanResumeStage(resumeStage)) return false;

        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE WebAutomationAttempts
            SET State=$resume,ResumeStage=NULL,
                ObservationFingerprint=COALESCE($observation,ObservationFingerprint),
                IsWaitingForUser=0,Revision=Revision+1,UpdatedAt=$updated
            WHERE OperationId=$operation AND AccountId=$account AND Generation=$generation
              AND State='UserHandoff' AND ResumeStage=$resume AND Revision=$revision
              AND UpdatedAt<=$updated
              AND ($resume='Preparing' OR (BaselineFingerprint IS NOT NULL AND BaselinePersistedAt IS NOT NULL));
            """;
        AddIdentity(command, operationId, accountId, generation);
        command.Parameters.AddWithValue("$resume", resumeStage.ToString());
        command.Parameters.AddWithValue("$revision", expectedRevision);
        command.Parameters.AddWithValue("$observation", (object?)observationFingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", updatedAt.ToUniversalTime().ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> TryResolveUserHandoffAsync(
        Guid operationId,
        Guid accountId,
        int generation,
        long expectedRevision,
        WebAutomationAttemptState resumeStage,
        WebAutomationAttemptState terminalState,
        string? observationFingerprint,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(operationId, accountId, generation);
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        if (!WebAutomationAttemptRecord.CanResumeStage(resumeStage))
            throw new ArgumentOutOfRangeException(nameof(resumeStage));
        if (terminalState is not (WebAutomationAttemptState.DefinitelyNotSubmitted or WebAutomationAttemptState.NeedsDecision))
            throw new ArgumentOutOfRangeException(nameof(terminalState));
        if (resumeStage is WebAutomationAttemptState.Preparing or WebAutomationAttemptState.BaselinePersisted)
        {
            if (terminalState != WebAutomationAttemptState.DefinitelyNotSubmitted) return false;
        }
        else if (terminalState != WebAutomationAttemptState.NeedsDecision) return false;
        WebAutomationAttemptRecord.ValidateFingerprint(observationFingerprint, nameof(observationFingerprint));

        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE WebAutomationAttempts
            SET State=$terminal,ResumeStage=NULL,
                ObservationFingerprint=COALESCE($observation,ObservationFingerprint),
                IsWaitingForUser=0,Revision=Revision+1,UpdatedAt=$updated
            WHERE OperationId=$operation AND AccountId=$account AND Generation=$generation
              AND State='UserHandoff' AND ResumeStage=$resume AND Revision=$revision
              AND UpdatedAt<=$updated
              AND ($terminal='DefinitelyNotSubmitted' OR
                   (BaselineFingerprint IS NOT NULL AND BaselinePersistedAt IS NOT NULL));
            """;
        AddIdentity(command, operationId, accountId, generation);
        command.Parameters.AddWithValue("$resume", resumeStage.ToString());
        command.Parameters.AddWithValue("$terminal", terminalState.ToString());
        command.Parameters.AddWithValue("$revision", expectedRevision);
        command.Parameters.AddWithValue("$observation", (object?)observationFingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", updatedAt.ToUniversalTime().ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static bool IsAllowed(WebAutomationAttemptState current, WebAutomationAttemptState next) => current switch
    {
        WebAutomationAttemptState.Preparing => next is WebAutomationAttemptState.DefinitelyNotSubmitted or
            WebAutomationAttemptState.UserHandoff,
        WebAutomationAttemptState.BaselinePersisted => next is WebAutomationAttemptState.Submitting or
            WebAutomationAttemptState.UserHandoff or WebAutomationAttemptState.NeedsDecision,
        WebAutomationAttemptState.Submitting => next is WebAutomationAttemptState.ResultPending or
            WebAutomationAttemptState.UserHandoff or WebAutomationAttemptState.NeedsDecision,
        WebAutomationAttemptState.ResultPending => next is WebAutomationAttemptState.Succeeded or
            WebAutomationAttemptState.UserHandoff or WebAutomationAttemptState.NeedsDecision,
        WebAutomationAttemptState.UserHandoff => false,
        WebAutomationAttemptState.NeedsDecision => next is WebAutomationAttemptState.Succeeded or
            WebAutomationAttemptState.DefinitelyNotSubmitted,
        _ => false,
    };

    private static bool RequiresBaseline(WebAutomationAttemptState state) => state is
        WebAutomationAttemptState.BaselinePersisted or WebAutomationAttemptState.Submitting or
        WebAutomationAttemptState.ResultPending or WebAutomationAttemptState.Succeeded or
        WebAutomationAttemptState.NeedsDecision;

    private async Task<WebAutomationAttemptRecord?> LoadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid operationId,
        Guid accountId,
        int generation,
        CancellationToken cancellationToken)
    {
        string owner;
        string state;
        string? resumeStage;
        string? observation;
        string? baselineFingerprint;
        int baselineCount;
        bool waiting;
        long revision;
        DateTimeOffset created;
        DateTimeOffset updated;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT OwnerKind,State,ResumeStage,ObservationFingerprint,BaselineFingerprint,BaselineItemCount,
                       IsWaitingForUser,Revision,CreatedAt,UpdatedAt
                FROM WebAutomationAttempts
                WHERE OperationId=$operation AND AccountId=$account AND Generation=$generation;
                """;
            AddIdentity(command, operationId, accountId, generation);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            owner = reader.GetString(0);
            state = reader.GetString(1);
            resumeStage = reader.IsDBNull(2) ? null : reader.GetString(2);
            observation = reader.IsDBNull(3) ? null : reader.GetString(3);
            baselineFingerprint = reader.IsDBNull(4) ? null : reader.GetString(4);
            baselineCount = reader.GetInt32(5);
            waiting = reader.GetInt64(6) != 0;
            revision = reader.GetInt64(7);
            created = DateTimeOffset.Parse(reader.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind);
            updated = DateTimeOffset.Parse(reader.GetString(9), null, System.Globalization.DateTimeStyles.RoundtripKind);
        }

        WebAutomationBaseline? baseline = null;
        if (baselineFingerprint is not null)
        {
            if (baselineCount < 0 || baselineCount > maximumBaselineItemCount)
                throw new InvalidDataException("WEB_BASELINE_COUNT_OUT_OF_RANGE");
            await using (var count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = """
                    SELECT COUNT(*) FROM WebAutomationAttemptRemoteItems
                    WHERE OperationId=$operation AND AccountId=$account AND Generation=$generation;
                    """;
                AddIdentity(count, operationId, accountId, generation);
                if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != baselineCount)
                    throw new InvalidDataException("WEB_BASELINE_COUNT_MISMATCH");
            }
            var items = new List<string>(baselineCount);
            var afterOrdinal = -1;
            while (items.Count < baselineCount)
            {
                var read = 0;
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT ItemOrdinal,RemoteItemId,CanonicalSortKey FROM WebAutomationAttemptRemoteItems
                    WHERE OperationId=$operation AND AccountId=$account AND Generation=$generation
                      AND ItemOrdinal>$after
                    ORDER BY ItemOrdinal LIMIT $take;
                    """;
                AddIdentity(command, operationId, accountId, generation);
                command.Parameters.AddWithValue("$after", afterOrdinal);
                command.Parameters.AddWithValue("$take", Math.Min(baselineReadPageSize, baselineCount - items.Count));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var ordinal = reader.GetInt32(0);
                    if (ordinal != items.Count)
                        throw new InvalidDataException("WEB_BASELINE_ORDINAL_MISMATCH");
                    var exactId = reader.GetString(1);
                    if (!reader.GetFieldValue<byte[]>(2).AsSpan().SequenceEqual(
                            RemoteItemIdentitySet.GetCanonicalSortKey(exactId)))
                        throw new InvalidDataException("WEB_BASELINE_CANONICAL_SORT_KEY_MISMATCH");
                    items.Add(exactId);
                    afterOrdinal = ordinal;
                    read++;
                }
                if (read == 0) break;
            }
            if (items.Count != baselineCount) throw new InvalidDataException("WEB_BASELINE_COUNT_MISMATCH");
            baseline = WebAutomationBaseline.Create(items);
            if (!string.Equals(baseline.Fingerprint, baselineFingerprint, StringComparison.Ordinal))
                throw new InvalidDataException("WEB_BASELINE_FINGERPRINT_MISMATCH");
        }

        return new(operationId, accountId, generation,
            Enum.Parse<WebAutomationOwnerKind>(owner), Enum.Parse<WebAutomationAttemptState>(state),
            resumeStage is null ? null : Enum.Parse<WebAutomationAttemptState>(resumeStage),
            baseline, observation, waiting, revision, created, updated);
    }

    private static void AddIdentity(SqliteCommand command, Guid operationId, Guid accountId, int generation)
    {
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.Parameters.AddWithValue("$account", accountId.ToString("D"));
        command.Parameters.AddWithValue("$generation", generation);
    }

    private static async Task<bool> OwnerExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WebAutomationAttemptRecord attempt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = attempt.OwnerKind switch
        {
            WebAutomationOwnerKind.Transfer => """
                SELECT COUNT(*) FROM TransferOperations
                WHERE Id=$operation AND AssignedAccountId=$account AND AttemptCount=$generation;
                """,
            WebAutomationOwnerKind.TransferShare => """
                SELECT COUNT(*) FROM ShareOperations share
                JOIN TransferOperations transfer ON transfer.Id=share.TransferOperationId
                  AND transfer.AssignedAccountId=share.AccountId
                WHERE share.Id=$operation AND share.AccountId=$account AND share.Generation=$generation;
                """,
            WebAutomationOwnerKind.StandaloneShare => """
                SELECT COUNT(*) FROM StandaloneShareOperations
                WHERE Id=$operation AND AccountId=$account AND Generation=$generation;
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(attempt)),
        };
        AddIdentity(command, attempt.OperationId, attempt.AccountId, attempt.Generation);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }

    private static void ValidateIdentity(Guid operationId, Guid accountId, int generation)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation is required.", nameof(operationId));
        if (accountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(accountId));
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
    }
}
