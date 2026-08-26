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
              BaselineFingerprint,BaselineItemCount,BaselinePersistedAt,IsWaitingForUser,Revision,CreatedAt,UpdatedAt,
              ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES($operation,$account,$generation,$owner,$state,NULL,NULL,NULL,0,NULL,0,0,$created,$updated,
              $engine,$browserFamily,$profile,$installation,$session);
            """;
        AddIdentity(command, attempt.OperationId, attempt.AccountId, attempt.Generation);
        command.Parameters.AddWithValue("$owner", attempt.OwnerKind.ToString());
        command.Parameters.AddWithValue("$state", attempt.State.ToString());
        command.Parameters.AddWithValue("$created", attempt.CreatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$updated", attempt.UpdatedAt.ToUniversalTime().ToString("O"));
        AddExecutionProfile(command, attempt.ExecutionProfile);
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

    public async Task<bool> TryPersistTransferSourceEvidenceAsync(
        Guid operationId,
        Guid accountId,
        int generation,
        long expectedRevision,
        TransferSourceEvidence evidence,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(operationId, accountId, generation);
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Items.Count > maximumBaselineItemCount)
            throw new ArgumentOutOfRangeException(nameof(evidence), "WEB_SOURCE_TOO_LARGE");

        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await LoadAsync(connection, transaction, operationId, accountId, generation, cancellationToken)
            .ConfigureAwait(false);
        if (current is null || current.OwnerKind != WebAutomationOwnerKind.Transfer ||
            current.State != WebAutomationAttemptState.Preparing || current.Revision != expectedRevision ||
            current.SourceEvidence is not null || updatedAt < current.UpdatedAt)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        for (var index = 0; index < evidence.Items.Count; index++)
        {
            var item = evidence.Items.Items[index];
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO WebAutomationAttemptSourceItems(
                  OperationId,AccountId,Generation,ItemOrdinal,RemoteItemId,CanonicalSortKey)
                VALUES($operation,$account,$generation,$ordinal,$item,$sort);
                """;
            AddIdentity(insert, operationId, accountId, generation);
            insert.Parameters.AddWithValue("$ordinal", index);
            insert.Parameters.AddWithValue("$item", item.ExactId);
            insert.Parameters.AddWithValue("$sort", RemoteItemIdentitySet.GetCanonicalSortKey(item.ExactId));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE WebAutomationAttempts SET
              SourceTotalCount=$count,SourceReachedEnd=1,SourceIsStable=1,
              SourceGenerationToken=$sourceGeneration,SourceFingerprint=$sourceFingerprint,
              SourceObservationFingerprint=$observation,BaiduUserId=$uid,
              CapabilityAdapterKind=$adapter,CapabilityProtocolVersion=$protocol,
              CapabilitySelectorVersion=$selector,CapabilityBuildVersion=$build,
              ObservationFingerprint=$observation,Revision=Revision+1,UpdatedAt=$updated
            WHERE OperationId=$operation AND AccountId=$account AND Generation=$generation
              AND OwnerKind='Transfer' AND State='Preparing' AND Revision=$revision
              AND SourceFingerprint IS NULL;
            """;
        AddIdentity(update, operationId, accountId, generation);
        update.Parameters.AddWithValue("$count", evidence.TotalCount);
        update.Parameters.AddWithValue("$sourceGeneration", evidence.GenerationToken);
        update.Parameters.AddWithValue("$sourceFingerprint", evidence.Items.Fingerprint);
        update.Parameters.AddWithValue("$observation", evidence.ObservationFingerprint);
        AddCapability(update, evidence.BaiduUserId, evidence.Capability);
        update.Parameters.AddWithValue("$revision", expectedRevision);
        update.Parameters.AddWithValue("$updated", updatedAt.ToUniversalTime().ToString("O"));
        var changed = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (changed) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        else await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    public async Task<bool> TryPersistTransferTargetPreparationAsync(
        Guid operationId,
        Guid accountId,
        int generation,
        long expectedRevision,
        TransferTargetPreparationEvidence evidence,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(operationId, accountId, generation);
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Baseline.Items.Length > maximumBaselineItemCount)
            throw new ArgumentOutOfRangeException(nameof(evidence), "WEB_BASELINE_TOO_LARGE");

        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await LoadAsync(connection, transaction, operationId, accountId, generation, cancellationToken)
            .ConfigureAwait(false);
        if (current?.SourceEvidence is null || current.OwnerKind != WebAutomationOwnerKind.Transfer ||
            current.State != WebAutomationAttemptState.Preparing || current.Revision != expectedRevision ||
            current.TargetPreparationEvidence is not null || updatedAt < current.UpdatedAt ||
            !string.Equals(current.SourceEvidence.BaiduUserId, evidence.BaiduUserId, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        for (var index = 0; index < evidence.Baseline.Items.Length; index++)
        {
            var item = evidence.Baseline.Items[index];
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
            UPDATE WebAutomationAttempts SET
              State='BaselinePersisted',BaselineFingerprint=$baseline,BaselineItemCount=$count,
              BaselinePersistedAt=$updated,TargetExactId=$target,TargetPath=$path,
              TargetTotalCount=$count,TargetReachedEnd=1,TargetIsStable=1,
              TargetGenerationToken=$targetGeneration,TargetObservationFingerprint=$observation,
              ObservationFingerprint=$observation,Revision=Revision+1,UpdatedAt=$updated
            WHERE OperationId=$operation AND AccountId=$account AND Generation=$generation
              AND OwnerKind='Transfer' AND State='Preparing' AND Revision=$revision
              AND SourceFingerprint IS NOT NULL AND BaiduUserId=$uid AND TargetExactId IS NULL;
            """;
        AddIdentity(update, operationId, accountId, generation);
        update.Parameters.AddWithValue("$baseline", evidence.Baseline.Fingerprint);
        update.Parameters.AddWithValue("$count", evidence.TotalCount);
        update.Parameters.AddWithValue("$target", evidence.TargetExactId);
        update.Parameters.AddWithValue("$path", evidence.TargetPath.Value);
        update.Parameters.AddWithValue("$targetGeneration", evidence.TargetGenerationToken);
        update.Parameters.AddWithValue("$observation", evidence.ObservationFingerprint);
        update.Parameters.AddWithValue("$uid", evidence.BaiduUserId);
        update.Parameters.AddWithValue("$revision", expectedRevision);
        update.Parameters.AddWithValue("$updated", updatedAt.ToUniversalTime().ToString("O"));
        var changed = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (changed) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        else await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    public async Task<bool> TryPersistTransferSubmissionAsync(
        Guid operationId,
        Guid accountId,
        int generation,
        long expectedRevision,
        TransferSubmissionEvidence evidence,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(operationId, accountId, generation);
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        ArgumentNullException.ThrowIfNull(evidence);
        var markedAt = evidence.MarkedSubmittingAtUtc.ToUniversalTime();
        if (updatedAt.ToUniversalTime() != markedAt) return false;

        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE WebAutomationAttempts SET
              State='Submitting',SubmissionSourceFingerprint=$source,
              SubmissionTargetExactId=$target,SubmissionTargetBaselineFingerprint=$baseline,
              SubmissionMarkedAt=$marked,ObservationFingerprint=TargetObservationFingerprint,
              Revision=Revision+1,UpdatedAt=$updated
            WHERE OperationId=$operation AND AccountId=$account AND Generation=$generation
              AND OwnerKind='Transfer' AND State='BaselinePersisted' AND Revision=$revision
              AND SourceFingerprint=$source AND TargetExactId=$target AND BaselineFingerprint=$baseline
              AND BaiduUserId=$uid AND CapabilityAdapterKind=$adapter
              AND CapabilityProtocolVersion=$protocol AND CapabilitySelectorVersion=$selector
              AND CapabilityBuildVersion=$build AND UpdatedAt<=$updated;
            """;
        AddIdentity(command, operationId, accountId, generation);
        command.Parameters.AddWithValue("$source", evidence.SourceFingerprint);
        command.Parameters.AddWithValue("$target", evidence.TargetExactId);
        command.Parameters.AddWithValue("$baseline", evidence.TargetBaselineFingerprint);
        command.Parameters.AddWithValue("$marked", markedAt.ToString("O"));
        AddCapability(command, evidence.BaiduUserId, evidence.Capability);
        command.Parameters.AddWithValue("$revision", expectedRevision);
        command.Parameters.AddWithValue("$updated", updatedAt.ToUniversalTime().ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> TryPersistTransferAcceptanceAsync(
        Guid operationId,
        Guid accountId,
        int generation,
        long expectedRevision,
        string officialRequestId,
        TransferIdentityMapping mapping,
        string observationFingerprint,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(operationId, accountId, generation);
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        ValidateBoundedText(officialRequestId, nameof(officialRequestId));
        ArgumentNullException.ThrowIfNull(mapping);
        WebAutomationAttemptRecord.ValidateFingerprint(observationFingerprint, nameof(observationFingerprint));

        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await LoadAsync(connection, transaction, operationId, accountId, generation, cancellationToken)
            .ConfigureAwait(false);
        if (current?.SourceEvidence is null || current.SubmissionEvidence is null ||
            current.OwnerKind != WebAutomationOwnerKind.Transfer ||
            current.State != WebAutomationAttemptState.Submitting || current.Revision != expectedRevision ||
            !mapping.Covers(current.SourceEvidence.Items) || updatedAt < current.UpdatedAt)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var destinations = new RemoteItemIdentitySet(mapping.Entries
            .Select(entry => new RemoteItemIdentity(entry.DestinationExactId, null)));
        for (var index = 0; index < mapping.Entries.Length; index++)
        {
            var entry = mapping.Entries[index];
            await using var insertMapping = connection.CreateCommand();
            insertMapping.Transaction = transaction;
            insertMapping.CommandText = """
                INSERT INTO WebAutomationTransferMappings(
                  OperationId,AccountId,Generation,ItemOrdinal,SourceRemoteItemId,SourceCanonicalSortKey,
                  DestinationRemoteItemId,DestinationCanonicalSortKey)
                VALUES($operation,$account,$generation,$ordinal,$source,$sourceSort,$destination,$destinationSort);
                """;
            AddIdentity(insertMapping, operationId, accountId, generation);
            insertMapping.Parameters.AddWithValue("$ordinal", index);
            insertMapping.Parameters.AddWithValue("$source", entry.SourceExactId);
            insertMapping.Parameters.AddWithValue("$sourceSort", RemoteItemIdentitySet.GetCanonicalSortKey(entry.SourceExactId));
            insertMapping.Parameters.AddWithValue("$destination", entry.DestinationExactId);
            insertMapping.Parameters.AddWithValue("$destinationSort", RemoteItemIdentitySet.GetCanonicalSortKey(entry.DestinationExactId));
            await insertMapping.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var insertAccepted = connection.CreateCommand();
            insertAccepted.Transaction = transaction;
            insertAccepted.CommandText = """
                INSERT INTO WebAutomationAttemptAcceptedRemoteItems(
                  OperationId,AccountId,Generation,ItemOrdinal,RemoteItemId,CanonicalSortKey)
                VALUES($operation,$account,$generation,$ordinal,$item,$sort);
                """;
            AddIdentity(insertAccepted, operationId, accountId, generation);
            insertAccepted.Parameters.AddWithValue("$ordinal", index);
            insertAccepted.Parameters.AddWithValue("$item", entry.DestinationExactId);
            insertAccepted.Parameters.AddWithValue("$sort", RemoteItemIdentitySet.GetCanonicalSortKey(entry.DestinationExactId));
            await insertAccepted.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE WebAutomationAttempts SET
              State='ResultPending',OfficialRequestId=$request,
              TransferMappingFingerprint=$mapping,TransferMappingCount=$count,
              AcceptanceFingerprint=$acceptance,AcceptedItemCount=$count,
              AcceptancePersistedAt=$updated,ObservationFingerprint=$observation,
              Revision=Revision+1,UpdatedAt=$updated
            WHERE OperationId=$operation AND AccountId=$account AND Generation=$generation
              AND OwnerKind='Transfer' AND State='Submitting' AND Revision=$revision
              AND SubmissionSourceFingerprint=SourceFingerprint;
            """;
        AddIdentity(update, operationId, accountId, generation);
        update.Parameters.AddWithValue("$request", officialRequestId.Trim());
        update.Parameters.AddWithValue("$mapping", mapping.Fingerprint);
        update.Parameters.AddWithValue("$acceptance", destinations.Fingerprint);
        update.Parameters.AddWithValue("$count", mapping.Entries.Length);
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
              AND ($requiresBaseline=0 OR BaselinePersistedAt IS NOT NULL)
              AND ($requiresAcceptance=0 OR OwnerKind NOT IN ('Transfer','TransferShare','StandaloneShare') OR
                   (AcceptanceFingerprint IS NOT NULL AND AcceptancePersistedAt IS NOT NULL AND AcceptedItemCount>0));
            """;
        AddIdentity(command, operationId, accountId, generation);
        command.Parameters.AddWithValue("$next", nextState.ToString());
        command.Parameters.AddWithValue("$expected", expectedState.ToString());
        command.Parameters.AddWithValue("$revision", expectedRevision);
        command.Parameters.AddWithValue("$observation", (object?)observationFingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$waiting", isWaitingForUser ? 1 : 0);
        command.Parameters.AddWithValue("$updated", updatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$requiresBaseline", RequiresBaseline(nextState) ? 1 : 0);
        command.Parameters.AddWithValue("$requiresAcceptance",
            expectedState == WebAutomationAttemptState.ResultPending &&
            nextState == WebAutomationAttemptState.Succeeded ? 1 : 0);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> TryPersistOfficialAcceptanceAsync(
        Guid operationId,
        Guid accountId,
        int generation,
        long expectedRevision,
        RemoteItemIdentitySet acceptedRemoteItems,
        string observationFingerprint,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(operationId, accountId, generation);
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        ArgumentNullException.ThrowIfNull(acceptedRemoteItems);
        WebAutomationAttemptRecord.ValidateFingerprint(observationFingerprint, nameof(observationFingerprint));
        if (acceptedRemoteItems.Count > maximumBaselineItemCount)
            throw new ArgumentOutOfRangeException(nameof(acceptedRemoteItems), "WEB_ACCEPTANCE_TOO_LARGE");

        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await LoadAsync(connection, transaction, operationId, accountId, generation, cancellationToken)
            .ConfigureAwait(false);
        if (current is null || current.OwnerKind is not (
                WebAutomationOwnerKind.Transfer or
                WebAutomationOwnerKind.TransferShare or
                WebAutomationOwnerKind.StandaloneShare) ||
            current.State != WebAutomationAttemptState.Submitting ||
            current.Revision != expectedRevision || updatedAt < current.UpdatedAt)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        for (var index = 0; index < acceptedRemoteItems.Items.Length; index++)
        {
            var item = acceptedRemoteItems.Items[index];
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO WebAutomationAttemptAcceptedRemoteItems(
                  OperationId,AccountId,Generation,ItemOrdinal,RemoteItemId,CanonicalSortKey)
                VALUES($operation,$account,$generation,$ordinal,$item,$sort);
                """;
            AddIdentity(insert, operationId, accountId, generation);
            insert.Parameters.AddWithValue("$ordinal", index);
            insert.Parameters.AddWithValue("$item", item.ExactId);
            insert.Parameters.AddWithValue("$sort", RemoteItemIdentitySet.GetCanonicalSortKey(item.ExactId));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE WebAutomationAttempts
            SET State='ResultPending',AcceptanceFingerprint=$fingerprint,AcceptedItemCount=$count,
                AcceptancePersistedAt=$updated,ObservationFingerprint=$observation,
                IsWaitingForUser=0,Revision=Revision+1,UpdatedAt=$updated
            WHERE OperationId=$operation AND AccountId=$account AND Generation=$generation
              AND State='Submitting' AND Revision=$revision
              AND OwnerKind IN ('Transfer','TransferShare','StandaloneShare');
            """;
        AddIdentity(update, operationId, accountId, generation);
        update.Parameters.AddWithValue("$fingerprint", acceptedRemoteItems.Fingerprint);
        update.Parameters.AddWithValue("$count", acceptedRemoteItems.Count);
        update.Parameters.AddWithValue("$observation", observationFingerprint);
        update.Parameters.AddWithValue("$revision", expectedRevision);
        update.Parameters.AddWithValue("$updated", updatedAt.ToUniversalTime().ToString("O"));
        var changed = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (changed) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        else await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    public async Task<bool> TryBeginDirectoryMutationAsync(
        TransferDirectoryMutationRecord mutation,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (mutation.State != TransferDirectoryMutationState.Intent || mutation.ChildExactId is not null)
            throw new ArgumentException("A new directory mutation must be an unresolved intent.", nameof(mutation));
        var segment = GetCanonicalSegment(mutation.CanonicalChildPath);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await LoadAsync(connection, transaction, mutation.OperationId, mutation.AccountId,
            mutation.Generation, cancellationToken).ConfigureAwait(false);
        if (current?.SourceEvidence is null || current.OwnerKind != WebAutomationOwnerKind.Transfer ||
            current.State != WebAutomationAttemptState.Preparing ||
            current.Revision != mutation.OwningAttemptRevision || updatedAt < current.UpdatedAt ||
            !string.Equals(current.SourceEvidence.BaiduUserId, mutation.BaiduUserId, StringComparison.Ordinal) ||
            current.SourceEvidence.Capability != mutation.Capability)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO WebAutomationDirectoryMutations(
              OperationId,AccountId,OriginGeneration,SegmentOrdinal,ParentExactId,CanonicalSegment,
              ChildExactId,CanonicalChildPath,ListingGenerationToken,BaiduUserId,
              CapabilityAdapterKind,CapabilityProtocolVersion,CapabilitySelectorVersion,
              CapabilityBuildVersion,State,OwningAttemptRevision,CreatedAt,UpdatedAt)
            VALUES($operation,$account,$generation,$ordinal,$parent,$segment,NULL,$path,$listing,$uid,
              $adapter,$protocol,$selector,$build,'Intent',$revision,$updated,$updated);
            """;
        command.Parameters.AddWithValue("$operation", mutation.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$account", mutation.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$generation", mutation.Generation);
        command.Parameters.AddWithValue("$ordinal", mutation.SegmentOrdinal);
        command.Parameters.AddWithValue("$parent", mutation.ParentExactId);
        command.Parameters.AddWithValue("$segment", segment);
        command.Parameters.AddWithValue("$path", mutation.CanonicalChildPath.Value);
        command.Parameters.AddWithValue("$listing", mutation.ListingGenerationToken);
        AddCapability(command, mutation.BaiduUserId, mutation.Capability);
        command.Parameters.AddWithValue("$revision", mutation.OwningAttemptRevision);
        command.Parameters.AddWithValue("$updated", updatedAt.ToUniversalTime().ToString("O"));
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

    public async Task<bool> TryResolveDirectoryMutationAsync(
        TransferDirectoryMutationRecord expected,
        TransferDirectoryMutationState nextState,
        string? childExactId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.State == TransferDirectoryMutationState.Confirmed ||
            nextState == TransferDirectoryMutationState.Intent || !Enum.IsDefined(nextState))
            return false;
        if (nextState == TransferDirectoryMutationState.Confirmed)
        {
            _ = new RemoteItemIdentitySet([new RemoteItemIdentity(
                string.IsNullOrWhiteSpace(childExactId)
                    ? throw new ArgumentException("A confirmed mutation requires its exact child ID.", nameof(childExactId))
                    : childExactId, null)]);
        }
        else if (childExactId is not null)
        {
            throw new ArgumentException("An unknown outcome cannot claim a child ID.", nameof(childExactId));
        }

        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE WebAutomationDirectoryMutations SET
              State=$next,ChildExactId=$child,UpdatedAt=$updated
            WHERE OperationId=$operation AND AccountId=$account AND OriginGeneration=$generation
              AND SegmentOrdinal=$ordinal AND State=$expectedState AND ParentExactId=$parent
              AND CanonicalChildPath=$path AND ListingGenerationToken=$listing
              AND BaiduUserId=$uid AND CapabilityAdapterKind=$adapter
              AND CapabilityProtocolVersion=$protocol AND CapabilitySelectorVersion=$selector
              AND CapabilityBuildVersion=$build AND OwningAttemptRevision=$revision
              AND UpdatedAt<=$updated;
            """;
        command.Parameters.AddWithValue("$operation", expected.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$account", expected.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$generation", expected.Generation);
        command.Parameters.AddWithValue("$ordinal", expected.SegmentOrdinal);
        command.Parameters.AddWithValue("$expectedState", expected.State.ToString());
        command.Parameters.AddWithValue("$next", nextState.ToString());
        command.Parameters.AddWithValue("$child", (object?)childExactId ?? DBNull.Value);
        command.Parameters.AddWithValue("$parent", expected.ParentExactId);
        command.Parameters.AddWithValue("$path", expected.CanonicalChildPath.Value);
        command.Parameters.AddWithValue("$listing", expected.ListingGenerationToken);
        AddCapability(command, expected.BaiduUserId, expected.Capability);
        command.Parameters.AddWithValue("$revision", expected.OwningAttemptRevision);
        command.Parameters.AddWithValue("$updated", updatedAt.ToUniversalTime().ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<IReadOnlyList<TransferDirectoryMutationRecord>> GetDirectoryMutationsAsync(
        Guid operationId,
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation is required.", nameof(operationId));
        if (accountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(accountId));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT OriginGeneration,SegmentOrdinal,ParentExactId,CanonicalSegment,ChildExactId,CanonicalChildPath,
              ListingGenerationToken,BaiduUserId,CapabilityAdapterKind,CapabilityProtocolVersion,
              CapabilitySelectorVersion,CapabilityBuildVersion,State,OwningAttemptRevision
            FROM WebAutomationDirectoryMutations
            WHERE OperationId=$operation AND AccountId=$account
            ORDER BY OriginGeneration,SegmentOrdinal;
            """;
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.Parameters.AddWithValue("$account", accountId.ToString("D"));
        return await ReadDirectoryMutationsAsync(command, operationId, accountId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TransferDirectoryMutationRecord?> GetUnresolvedDirectoryMutationAsync(
        Guid operationId,
        Guid accountId,
        string parentExactId,
        string canonicalSegment,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation is required.", nameof(operationId));
        if (accountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(accountId));
        ValidateBoundedText(parentExactId, nameof(parentExactId));
        ValidateBoundedText(canonicalSegment, nameof(canonicalSegment));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT OriginGeneration,SegmentOrdinal,ParentExactId,CanonicalSegment,ChildExactId,CanonicalChildPath,
              ListingGenerationToken,BaiduUserId,CapabilityAdapterKind,CapabilityProtocolVersion,
              CapabilitySelectorVersion,CapabilityBuildVersion,State,OwningAttemptRevision
            FROM WebAutomationDirectoryMutations
            WHERE OperationId=$operation AND AccountId=$account AND ParentExactId=$parent
              AND CanonicalSegment=$segment AND State IN ('Intent','OutcomeUnknown')
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.Parameters.AddWithValue("$account", accountId.ToString("D"));
        command.Parameters.AddWithValue("$parent", parentExactId.Trim());
        command.Parameters.AddWithValue("$segment", canonicalSegment.Trim());
        return (await ReadDirectoryMutationsAsync(command, operationId, accountId, cancellationToken)
            .ConfigureAwait(false)).SingleOrDefault();
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
        string? acceptanceFingerprint;
        int acceptedCount;
        int? sourceTotalCount;
        bool? sourceReachedEnd;
        bool? sourceIsStable;
        string? sourceGenerationToken;
        string? sourceFingerprint;
        string? sourceObservationFingerprint;
        string? baiduUserId;
        string? capabilityAdapterKind;
        string? capabilityProtocolVersion;
        string? capabilitySelectorVersion;
        string? capabilityBuildVersion;
        string? targetExactId;
        string? targetPath;
        int? targetTotalCount;
        bool? targetReachedEnd;
        bool? targetIsStable;
        string? targetGenerationToken;
        string? targetObservationFingerprint;
        string? submissionSourceFingerprint;
        string? submissionTargetExactId;
        string? submissionTargetBaselineFingerprint;
        DateTimeOffset? submissionMarkedAt;
        string? officialRequestId;
        string? transferMappingFingerprint;
        int transferMappingCount;
        ExecutionProfileSnapshot executionProfile;
        bool waiting;
        long revision;
        DateTimeOffset created;
        DateTimeOffset updated;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT OwnerKind,State,ResumeStage,ObservationFingerprint,BaselineFingerprint,BaselineItemCount,
                       IsWaitingForUser,Revision,CreatedAt,UpdatedAt,AcceptanceFingerprint,AcceptedItemCount,
                       SourceTotalCount,SourceReachedEnd,SourceIsStable,SourceGenerationToken,
                       SourceFingerprint,SourceObservationFingerprint,BaiduUserId,
                       CapabilityAdapterKind,CapabilityProtocolVersion,CapabilitySelectorVersion,CapabilityBuildVersion,
                       TargetExactId,TargetPath,TargetTotalCount,TargetReachedEnd,TargetIsStable,
                       TargetGenerationToken,TargetObservationFingerprint,
                       SubmissionSourceFingerprint,SubmissionTargetExactId,SubmissionTargetBaselineFingerprint,
                       SubmissionMarkedAt,OfficialRequestId,TransferMappingFingerprint,TransferMappingCount,
                       ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId
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
            acceptanceFingerprint = reader.IsDBNull(10) ? null : reader.GetString(10);
            acceptedCount = reader.GetInt32(11);
            sourceTotalCount = reader.IsDBNull(12) ? null : reader.GetInt32(12);
            sourceReachedEnd = reader.IsDBNull(13) ? null : reader.GetInt64(13) != 0;
            sourceIsStable = reader.IsDBNull(14) ? null : reader.GetInt64(14) != 0;
            sourceGenerationToken = reader.IsDBNull(15) ? null : reader.GetString(15);
            sourceFingerprint = reader.IsDBNull(16) ? null : reader.GetString(16);
            sourceObservationFingerprint = reader.IsDBNull(17) ? null : reader.GetString(17);
            baiduUserId = reader.IsDBNull(18) ? null : reader.GetString(18);
            capabilityAdapterKind = reader.IsDBNull(19) ? null : reader.GetString(19);
            capabilityProtocolVersion = reader.IsDBNull(20) ? null : reader.GetString(20);
            capabilitySelectorVersion = reader.IsDBNull(21) ? null : reader.GetString(21);
            capabilityBuildVersion = reader.IsDBNull(22) ? null : reader.GetString(22);
            targetExactId = reader.IsDBNull(23) ? null : reader.GetString(23);
            targetPath = reader.IsDBNull(24) ? null : reader.GetString(24);
            targetTotalCount = reader.IsDBNull(25) ? null : reader.GetInt32(25);
            targetReachedEnd = reader.IsDBNull(26) ? null : reader.GetInt64(26) != 0;
            targetIsStable = reader.IsDBNull(27) ? null : reader.GetInt64(27) != 0;
            targetGenerationToken = reader.IsDBNull(28) ? null : reader.GetString(28);
            targetObservationFingerprint = reader.IsDBNull(29) ? null : reader.GetString(29);
            submissionSourceFingerprint = reader.IsDBNull(30) ? null : reader.GetString(30);
            submissionTargetExactId = reader.IsDBNull(31) ? null : reader.GetString(31);
            submissionTargetBaselineFingerprint = reader.IsDBNull(32) ? null : reader.GetString(32);
            submissionMarkedAt = reader.IsDBNull(33) ? null : DateTimeOffset.Parse(
                reader.GetString(33), null, System.Globalization.DateTimeStyles.RoundtripKind);
            officialRequestId = reader.IsDBNull(34) ? null : reader.GetString(34);
            transferMappingFingerprint = reader.IsDBNull(35) ? null : reader.GetString(35);
            transferMappingCount = reader.GetInt32(36);
            executionProfile = SqliteExecutionProfileSnapshotCodec.Read(reader, 37);
        }

        TransferSourceEvidence? sourceEvidence = null;
        var hasAnySourceMetadata = sourceTotalCount is not null || sourceReachedEnd is not null ||
            sourceIsStable is not null || sourceGenerationToken is not null || sourceFingerprint is not null ||
            sourceObservationFingerprint is not null || baiduUserId is not null || capabilityAdapterKind is not null ||
            capabilityProtocolVersion is not null || capabilitySelectorVersion is not null || capabilityBuildVersion is not null;
        if (hasAnySourceMetadata)
        {
            if (sourceTotalCount is null || sourceReachedEnd is null || sourceIsStable is null ||
                sourceGenerationToken is null || sourceFingerprint is null || sourceObservationFingerprint is null ||
                baiduUserId is null || capabilityAdapterKind is null || capabilityProtocolVersion is null ||
                capabilitySelectorVersion is null || capabilityBuildVersion is null)
                throw new InvalidDataException("WEB_SOURCE_METADATA_MISMATCH");
            var sourceIds = await LoadExactIdsAsync(connection, transaction,
                "WebAutomationAttemptSourceItems", operationId, accountId, generation,
                sourceTotalCount.Value, "WEB_SOURCE", cancellationToken).ConfigureAwait(false);
            var sourceItems = new RemoteItemIdentitySet(sourceIds.Select(id => new RemoteItemIdentity(id, null)));
            if (!string.Equals(sourceItems.Fingerprint, sourceFingerprint, StringComparison.Ordinal))
                throw new InvalidDataException("WEB_SOURCE_FINGERPRINT_MISMATCH");
            sourceEvidence = new TransferSourceEvidence(
                sourceItems, sourceTotalCount.Value, sourceReachedEnd.Value, sourceIsStable.Value,
                sourceGenerationToken, baiduUserId,
                new AdapterCapabilityVersion(capabilityAdapterKind, capabilityProtocolVersion,
                    capabilitySelectorVersion, capabilityBuildVersion),
                sourceObservationFingerprint);
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

        RemoteItemIdentitySet? acceptedRemoteItems = null;
        if (acceptanceFingerprint is not null)
        {
            if (acceptedCount < 1 || acceptedCount > maximumBaselineItemCount)
                throw new InvalidDataException("WEB_ACCEPTANCE_COUNT_OUT_OF_RANGE");
            var acceptedIds = new List<string>(acceptedCount);
            await using (var count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = """
                    SELECT COUNT(*) FROM WebAutomationAttemptAcceptedRemoteItems
                    WHERE OperationId=$operation AND AccountId=$account AND Generation=$generation;
                    """;
                AddIdentity(count, operationId, accountId, generation);
                if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != acceptedCount)
                    throw new InvalidDataException("WEB_ACCEPTANCE_COUNT_MISMATCH");
            }
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT ItemOrdinal,RemoteItemId,CanonicalSortKey
                    FROM WebAutomationAttemptAcceptedRemoteItems
                    WHERE OperationId=$operation AND AccountId=$account AND Generation=$generation
                    ORDER BY ItemOrdinal;
                    """;
                AddIdentity(command, operationId, accountId, generation);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (reader.GetInt32(0) != acceptedIds.Count)
                        throw new InvalidDataException("WEB_ACCEPTANCE_ORDINAL_MISMATCH");
                    var exactId = reader.GetString(1);
                    if (!reader.GetFieldValue<byte[]>(2).AsSpan().SequenceEqual(
                            RemoteItemIdentitySet.GetCanonicalSortKey(exactId)))
                        throw new InvalidDataException("WEB_ACCEPTANCE_CANONICAL_SORT_KEY_MISMATCH");
                    acceptedIds.Add(exactId);
                }
            }
            if (acceptedIds.Count != acceptedCount)
                throw new InvalidDataException("WEB_ACCEPTANCE_COUNT_MISMATCH");
            acceptedRemoteItems = new RemoteItemIdentitySet(
                acceptedIds.Select(id => new RemoteItemIdentity(id, null)));
            if (!string.Equals(acceptedRemoteItems.Fingerprint, acceptanceFingerprint, StringComparison.Ordinal))
                throw new InvalidDataException("WEB_ACCEPTANCE_FINGERPRINT_MISMATCH");
        }
        else if (acceptedCount != 0)
        {
            throw new InvalidDataException("WEB_ACCEPTANCE_METADATA_MISMATCH");
        }

        TransferTargetPreparationEvidence? targetPreparationEvidence = null;
        var hasAnyTargetMetadata = targetExactId is not null || targetPath is not null || targetTotalCount is not null ||
            targetReachedEnd is not null || targetIsStable is not null || targetGenerationToken is not null ||
            targetObservationFingerprint is not null;
        if (hasAnyTargetMetadata)
        {
            if (sourceEvidence is null || baseline is null || targetExactId is null || targetPath is null ||
                targetTotalCount is null || targetReachedEnd is null || targetIsStable is null ||
                targetGenerationToken is null || targetObservationFingerprint is null || baiduUserId is null)
                throw new InvalidDataException("WEB_TARGET_METADATA_MISMATCH");
            targetPreparationEvidence = new TransferTargetPreparationEvidence(
                targetExactId, new RemotePath(targetPath), targetTotalCount.Value,
                targetReachedEnd.Value, targetIsStable.Value, targetGenerationToken, baiduUserId,
                baseline, targetObservationFingerprint);
        }

        TransferSubmissionEvidence? submissionEvidence = null;
        var hasAnySubmissionMetadata = submissionSourceFingerprint is not null ||
            submissionTargetExactId is not null || submissionTargetBaselineFingerprint is not null ||
            submissionMarkedAt is not null;
        if (hasAnySubmissionMetadata)
        {
            if (sourceEvidence is null || submissionSourceFingerprint is null || submissionTargetExactId is null ||
                submissionTargetBaselineFingerprint is null || submissionMarkedAt is null || baiduUserId is null)
                throw new InvalidDataException("WEB_SUBMISSION_METADATA_MISMATCH");
            submissionEvidence = new TransferSubmissionEvidence(
                submissionSourceFingerprint, submissionTargetExactId, submissionTargetBaselineFingerprint,
                baiduUserId, sourceEvidence.Capability, submissionMarkedAt.Value);
        }

        TransferIdentityMapping? transferIdentityMapping = null;
        if (transferMappingFingerprint is not null)
        {
            if (sourceEvidence is null || officialRequestId is null || transferMappingCount < 1)
                throw new InvalidDataException("WEB_TRANSFER_MAPPING_METADATA_MISMATCH");
            var entries = new List<TransferIdentityMappingEntry>(transferMappingCount);
            await using var mappingCommand = connection.CreateCommand();
            mappingCommand.Transaction = transaction;
            mappingCommand.CommandText = """
                SELECT ItemOrdinal,SourceRemoteItemId,SourceCanonicalSortKey,
                       DestinationRemoteItemId,DestinationCanonicalSortKey
                FROM WebAutomationTransferMappings
                WHERE OperationId=$operation AND AccountId=$account AND Generation=$generation
                ORDER BY ItemOrdinal;
                """;
            AddIdentity(mappingCommand, operationId, accountId, generation);
            await using var mappingReader = await mappingCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await mappingReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (mappingReader.GetInt32(0) != entries.Count)
                    throw new InvalidDataException("WEB_TRANSFER_MAPPING_ORDINAL_MISMATCH");
                var sourceId = mappingReader.GetString(1);
                var destinationId = mappingReader.GetString(3);
                if (!mappingReader.GetFieldValue<byte[]>(2).AsSpan().SequenceEqual(
                        RemoteItemIdentitySet.GetCanonicalSortKey(sourceId)) ||
                    !mappingReader.GetFieldValue<byte[]>(4).AsSpan().SequenceEqual(
                        RemoteItemIdentitySet.GetCanonicalSortKey(destinationId)))
                    throw new InvalidDataException("WEB_TRANSFER_MAPPING_CANONICAL_SORT_KEY_MISMATCH");
                entries.Add(new TransferIdentityMappingEntry(sourceId, destinationId));
            }
            if (entries.Count != transferMappingCount)
                throw new InvalidDataException("WEB_TRANSFER_MAPPING_COUNT_MISMATCH");
            transferIdentityMapping = new TransferIdentityMapping(entries);
            if (!string.Equals(transferIdentityMapping.Fingerprint, transferMappingFingerprint, StringComparison.Ordinal))
                throw new InvalidDataException("WEB_TRANSFER_MAPPING_FINGERPRINT_MISMATCH");
        }
        else if (transferMappingCount != 0 || officialRequestId is not null)
        {
            throw new InvalidDataException("WEB_TRANSFER_MAPPING_METADATA_MISMATCH");
        }

        var attempt = new WebAutomationAttemptRecord(operationId, accountId, generation,
            Enum.Parse<WebAutomationOwnerKind>(owner), Enum.Parse<WebAutomationAttemptState>(state),
            resumeStage is null ? null : Enum.Parse<WebAutomationAttemptState>(resumeStage),
            baseline, observation, waiting, revision, created, updated, acceptedRemoteItems,
            sourceEvidence, targetPreparationEvidence, submissionEvidence, officialRequestId,
            transferIdentityMapping, executionProfile);
        if (!await OwnerSnapshotMatchesAsync(
                connection, transaction, attempt, cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("WEB_ATTEMPT_OWNER_SNAPSHOT_MISMATCH");
        return attempt;
    }

    private static async Task<bool> OwnerSnapshotMatchesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        WebAutomationAttemptRecord attempt,
        CancellationToken cancellationToken)
    {
        if (attempt.OwnerKind == WebAutomationOwnerKind.RemoteSelection &&
            attempt.ExecutionProfile.Engine == BaiduExecutionEngine.WebView2V1)
            return attempt.ExecutionProfile.Equals(
                ExecutionProfileSnapshot.LegacyForAccount(attempt.AccountId));

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = attempt.OwnerKind switch
        {
            WebAutomationOwnerKind.Transfer => """
                SELECT EXISTS(SELECT 1 FROM TransferOperations
                WHERE Id=$operation AND AssignedAccountId=$account
                  AND ExecutionEngineVersion=$engine AND BrowserFamily=$browserFamily AND ProfileId=$profile
                  AND InstallationId IS $installation AND SessionId IS $session);
                """,
            WebAutomationOwnerKind.TransferShare => """
                SELECT EXISTS(SELECT 1 FROM ShareOperations
                WHERE Id=$operation AND AccountId=$account
                  AND ExecutionEngineVersion=$engine AND BrowserFamily=$browserFamily AND ProfileId=$profile
                  AND InstallationId IS $installation AND SessionId IS $session);
                """,
            WebAutomationOwnerKind.StandaloneShare => """
                SELECT EXISTS(SELECT 1 FROM StandaloneShareOperations
                WHERE Id=$operation AND AccountId=$account
                  AND ExecutionEngineVersion=$engine AND BrowserFamily=$browserFamily AND ProfileId=$profile
                  AND InstallationId IS $installation AND SessionId IS $session);
                """,
            WebAutomationOwnerKind.RemoteSelection => """
                SELECT EXISTS(SELECT 1 FROM ManagedBrowserSessions
                WHERE SessionId=$session AND AccountId=$account AND InstallationId=$installation
                  AND BrowserFamily=$browserFamily AND ProfileId=$profile);
                """,
            _ => throw new InvalidDataException("WEB_ATTEMPT_OWNER_SNAPSHOT_MISMATCH"),
        };
        command.Parameters.AddWithValue("$operation", attempt.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$account", attempt.AccountId.ToString("D"));
        AddExecutionProfile(command, attempt.ExecutionProfile);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }

    private static void AddIdentity(SqliteCommand command, Guid operationId, Guid accountId, int generation)
    {
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.Parameters.AddWithValue("$account", accountId.ToString("D"));
        command.Parameters.AddWithValue("$generation", generation);
    }

    private static void AddCapability(
        SqliteCommand command,
        string baiduUserId,
        AdapterCapabilityVersion capability)
    {
        command.Parameters.AddWithValue("$uid", baiduUserId);
        command.Parameters.AddWithValue("$adapter", capability.AdapterKind);
        command.Parameters.AddWithValue("$protocol", capability.ProtocolVersion);
        command.Parameters.AddWithValue("$selector", capability.SelectorVersion);
        command.Parameters.AddWithValue("$build", capability.BuildVersion);
    }

    private static async Task<IReadOnlyList<string>> LoadExactIdsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        Guid operationId,
        Guid accountId,
        int generation,
        int expectedCount,
        string errorPrefix,
        CancellationToken cancellationToken)
    {
        if (table is not ("WebAutomationAttemptSourceItems" or "WebAutomationAttemptRemoteItems"))
            throw new ArgumentOutOfRangeException(nameof(table));
        var ids = new List<string>(expectedCount);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT ItemOrdinal,RemoteItemId,CanonicalSortKey FROM {table}
            WHERE OperationId=$operation AND AccountId=$account AND Generation=$generation
            ORDER BY ItemOrdinal;
            """;
        AddIdentity(command, operationId, accountId, generation);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetInt32(0) != ids.Count)
                throw new InvalidDataException($"{errorPrefix}_ORDINAL_MISMATCH");
            var exactId = reader.GetString(1);
            if (!reader.GetFieldValue<byte[]>(2).AsSpan().SequenceEqual(
                    RemoteItemIdentitySet.GetCanonicalSortKey(exactId)))
                throw new InvalidDataException($"{errorPrefix}_CANONICAL_SORT_KEY_MISMATCH");
            ids.Add(exactId);
        }
        if (ids.Count != expectedCount)
            throw new InvalidDataException($"{errorPrefix}_COUNT_MISMATCH");
        return ids;
    }

    private static async Task<IReadOnlyList<TransferDirectoryMutationRecord>> ReadDirectoryMutationsAsync(
        SqliteCommand command,
        Guid operationId,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var mutations = new List<TransferDirectoryMutationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var path = new RemotePath(reader.GetString(5));
            if (!string.Equals(reader.GetString(3), GetCanonicalSegment(path), StringComparison.Ordinal))
                throw new InvalidDataException("WEB_DIRECTORY_MUTATION_SEGMENT_MISMATCH");
            mutations.Add(new TransferDirectoryMutationRecord(
                operationId,
                accountId,
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                path,
                reader.GetString(6),
                reader.GetString(7),
                new AdapterCapabilityVersion(
                    reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11)),
                Enum.Parse<TransferDirectoryMutationState>(reader.GetString(12)),
                reader.GetInt64(13)));
        }
        return mutations;
    }

    private static string GetCanonicalSegment(RemotePath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Value == "/") throw new ArgumentException("A child directory path is required.", nameof(path));
        return path.Value[(path.Value.LastIndexOf('/') + 1)..];
    }

    private static void ValidateBoundedText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > 512 || normalized.Any(char.IsControl))
            throw new ArgumentException("The value is invalid.", parameterName);
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
                WHERE Id=$operation AND AssignedAccountId=$account AND AttemptCount=$generation
                  AND ExecutionEngineVersion=$engine AND BrowserFamily=$browserFamily AND ProfileId=$profile
                  AND InstallationId IS $installation AND SessionId IS $session;
                """,
            WebAutomationOwnerKind.TransferShare => """
                SELECT COUNT(*) FROM ShareOperations share
                JOIN TransferOperations transfer ON transfer.Id=share.TransferOperationId
                  AND transfer.AssignedAccountId=share.AccountId
                WHERE share.Id=$operation AND share.AccountId=$account AND share.Generation=$generation
                  AND share.ExecutionEngineVersion=$engine AND share.BrowserFamily=$browserFamily AND share.ProfileId=$profile
                  AND share.InstallationId IS $installation AND share.SessionId IS $session;
                """,
            WebAutomationOwnerKind.StandaloneShare => """
                SELECT COUNT(*) FROM StandaloneShareOperations
                WHERE Id=$operation AND AccountId=$account AND Generation=$generation
                  AND ExecutionEngineVersion=$engine AND BrowserFamily=$browserFamily AND ProfileId=$profile
                  AND InstallationId IS $installation AND SessionId IS $session;
                """,
            WebAutomationOwnerKind.RemoteSelection => """
                SELECT COUNT(*) FROM RemoteSelectionSessions selection
                JOIN Accounts account ON account.Id=selection.AccountId
                WHERE selection.SessionId=$operation AND selection.AccountId=$account
                  AND selection.Generation=$generation AND selection.State='Active'
                  AND account.Status='Active' AND account.IsDeleted=0 AND account.IsEnabled=1;
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(attempt)),
        };
        AddIdentity(command, attempt.OperationId, attempt.AccountId, attempt.Generation);
        AddExecutionProfile(command, attempt.ExecutionProfile);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }

    private static void AddExecutionProfile(SqliteCommand command, ExecutionProfileSnapshot snapshot)
    {
        command.Parameters.AddWithValue("$engine", SqliteExecutionProfileSnapshotCodec.FormatEngine(snapshot.Engine));
        command.Parameters.AddWithValue("$browserFamily", SqliteExecutionProfileSnapshotCodec.FormatFamily(snapshot.BrowserFamily));
        command.Parameters.AddWithValue("$profile", snapshot.ProfileId);
        command.Parameters.AddWithValue("$installation", snapshot.InstallationId is { } installationId
            ? installationId.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$session", snapshot.SessionId is { } sessionId
            ? sessionId.ToString("D") : DBNull.Value);
    }

    private static void ValidateIdentity(Guid operationId, Guid accountId, int generation)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation is required.", nameof(operationId));
        if (accountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(accountId));
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
    }
}
