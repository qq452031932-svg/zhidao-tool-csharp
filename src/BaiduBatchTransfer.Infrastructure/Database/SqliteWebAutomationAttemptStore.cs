using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed class SqliteWebAutomationAttemptStore(SqliteConnectionFactory factory) : IWebAutomationAttemptStore
{
    private readonly SqliteConnectionFactory factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async Task<bool> BeginAsync(
        WebAutomationAttemptRecord attempt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.State != WebAutomationAttemptState.Preparing || attempt.Revision != 0 || attempt.Baseline is not null)
            throw new ArgumentException("A new browser attempt must start in Preparing without evidence.", nameof(attempt));

        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO WebAutomationAttempts(
              OperationId,AccountId,Generation,OwnerKind,State,ObservationFingerprint,
              BaselineFingerprint,BaselineItemCount,BaselinePersistedAt,IsWaitingForUser,Revision,CreatedAt,UpdatedAt)
            VALUES($operation,$account,$generation,$owner,$state,NULL,NULL,0,NULL,0,0,$created,$updated);
            """;
        AddIdentity(command, attempt.OperationId, attempt.AccountId, attempt.Generation);
        command.Parameters.AddWithValue("$owner", attempt.OwnerKind.ToString());
        command.Parameters.AddWithValue("$state", attempt.State.ToString());
        command.Parameters.AddWithValue("$created", attempt.CreatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$updated", attempt.UpdatedAt.ToUniversalTime().ToString("O"));
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
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
                  OperationId,AccountId,Generation,ItemOrdinal,RemoteItemId,CanonicalSortKey,DisplayName)
                VALUES($operation,$account,$generation,$ordinal,$item,$sort,$display);
                """;
            AddIdentity(insert, operationId, accountId, generation);
            insert.Parameters.AddWithValue("$ordinal", index);
            insert.Parameters.AddWithValue("$item", item.ExactId);
            insert.Parameters.AddWithValue("$sort", RemoteItemIdentitySet.GetCanonicalSortKey(item.ExactId));
            insert.Parameters.AddWithValue("$display", item.DisplayedTopLevelName);
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
        if (!IsAllowed(expectedState, nextState)) return false;

        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE WebAutomationAttempts
            SET State=$next,ObservationFingerprint=COALESCE($observation,ObservationFingerprint),
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
        WebAutomationAttemptState.UserHandoff => next is WebAutomationAttemptState.ResultPending or
            WebAutomationAttemptState.Succeeded or WebAutomationAttemptState.DefinitelyNotSubmitted or
            WebAutomationAttemptState.NeedsDecision,
        WebAutomationAttemptState.NeedsDecision => next is WebAutomationAttemptState.UserHandoff or
            WebAutomationAttemptState.Succeeded or WebAutomationAttemptState.DefinitelyNotSubmitted,
        _ => false,
    };

    private static bool RequiresBaseline(WebAutomationAttemptState state) => state is
        WebAutomationAttemptState.BaselinePersisted or WebAutomationAttemptState.Submitting or
        WebAutomationAttemptState.ResultPending or WebAutomationAttemptState.Succeeded or
        WebAutomationAttemptState.NeedsDecision;

    private static async Task<WebAutomationAttemptRecord?> LoadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid operationId,
        Guid accountId,
        int generation,
        CancellationToken cancellationToken)
    {
        string owner;
        string state;
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
                SELECT OwnerKind,State,ObservationFingerprint,BaselineFingerprint,BaselineItemCount,
                       IsWaitingForUser,Revision,CreatedAt,UpdatedAt
                FROM WebAutomationAttempts
                WHERE OperationId=$operation AND AccountId=$account AND Generation=$generation;
                """;
            AddIdentity(command, operationId, accountId, generation);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            owner = reader.GetString(0);
            state = reader.GetString(1);
            observation = reader.IsDBNull(2) ? null : reader.GetString(2);
            baselineFingerprint = reader.IsDBNull(3) ? null : reader.GetString(3);
            baselineCount = reader.GetInt32(4);
            waiting = reader.GetInt64(5) != 0;
            revision = reader.GetInt64(6);
            created = DateTimeOffset.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind);
            updated = DateTimeOffset.Parse(reader.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind);
        }

        WebAutomationBaseline? baseline = null;
        if (baselineFingerprint is not null)
        {
            var items = new List<RemoteItemIdentity>(baselineCount);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT RemoteItemId,DisplayName FROM WebAutomationAttemptRemoteItems
                WHERE OperationId=$operation AND AccountId=$account AND Generation=$generation
                ORDER BY ItemOrdinal LIMIT 100000;
                """;
            AddIdentity(command, operationId, accountId, generation);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                items.Add(new RemoteItemIdentity(reader.GetString(0), reader.GetString(1)));
            if (items.Count != baselineCount) throw new InvalidDataException("WEB_BASELINE_COUNT_MISMATCH");
            baseline = WebAutomationBaseline.Create(items);
            if (!string.Equals(baseline.Fingerprint, baselineFingerprint, StringComparison.Ordinal))
                throw new InvalidDataException("WEB_BASELINE_FINGERPRINT_MISMATCH");
        }

        return new(operationId, accountId, generation,
            Enum.Parse<WebAutomationOwnerKind>(owner), Enum.Parse<WebAutomationAttemptState>(state),
            baseline, observation, waiting, revision, created, updated);
    }

    private static void AddIdentity(SqliteCommand command, Guid operationId, Guid accountId, int generation)
    {
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.Parameters.AddWithValue("$account", accountId.ToString("D"));
        command.Parameters.AddWithValue("$generation", generation);
    }

    private static void ValidateIdentity(Guid operationId, Guid accountId, int generation)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation is required.", nameof(operationId));
        if (accountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(accountId));
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
    }
}
