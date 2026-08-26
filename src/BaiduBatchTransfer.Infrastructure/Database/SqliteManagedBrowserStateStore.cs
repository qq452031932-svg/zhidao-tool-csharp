using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed class SqliteManagedBrowserStateStore(SqliteConnectionFactory factory) : IManagedBrowserStateStore
{
    private readonly SqliteConnectionFactory factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async Task<StoredBrowserInstallation> CreateInstallationAsync(
        BrowserInstallationIdentity installation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow.ToString("O");
        try
        {
            await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO BrowserInstallations(
                  InstallationId,BrowserFamily,InstallationDirectory,ExecutablePath,Publisher,
                  ProductName,FileIdentity,State,Revision,CreatedAt,UpdatedAt)
                VALUES($id,$family,$directory,$executable,$publisher,$product,$file,'Retired',0,$now,$now);
                """;
            command.Parameters.AddWithValue("$id", installation.InstallationId.ToString("D"));
            command.Parameters.AddWithValue("$family", FormatFamily(installation.BrowserFamily));
            command.Parameters.AddWithValue("$directory", installation.InstallationDirectory);
            command.Parameters.AddWithValue("$executable", installation.ExecutablePath);
            command.Parameters.AddWithValue("$publisher", installation.Publisher);
            command.Parameters.AddWithValue("$product", installation.ProductName);
            command.Parameters.AddWithValue("$file", installation.FileIdentity);
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new StoredBrowserInstallation(
                installation, BrowserInstallationLifecycleState.Retired, revision: 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            throw new InvalidOperationException("Managed browser installation could not be created.");
        }
    }

    public async Task<StoredBrowserInstallation?> LoadInstallationAsync(
        Guid installationId,
        CancellationToken cancellationToken)
    {
        if (installationId == Guid.Empty)
            throw new ArgumentException("An installation ID is required.", nameof(installationId));
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadInstallationAsync(connection, null, installationId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredBrowserInstallation?> LoadActiveInstallationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadActiveInstallationAsync(connection, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ManagedBrowserActiveSlotTransitionResult<StoredBrowserInstallation>>
        TryReplaceActiveInstallationAsync(
            ManagedBrowserInstallationActivationRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var active = await LoadActiveInstallationAsync(
                connection, transaction, cancellationToken).ConfigureAwait(false);
            var target = await LoadInstallationAsync(
                connection, transaction, request.Target.Identity.InstallationId, cancellationToken)
                .ConfigureAwait(false);
            if (target is null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return ManagedBrowserActiveSlotTransitionResult<StoredBrowserInstallation>.NotFound();
            }

            if (request.ExpectedCurrent is null)
            {
                if (active is not null)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return ManagedBrowserActiveSlotTransitionResult<StoredBrowserInstallation>.Conflict(active);
                }
            }
            else if (active is null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return ManagedBrowserActiveSlotTransitionResult<StoredBrowserInstallation>.NotFound();
            }
            else if (!Matches(active, request.ExpectedCurrent))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return ManagedBrowserActiveSlotTransitionResult<StoredBrowserInstallation>.Conflict(active);
            }

            if (!Matches(target, request.Target) || target.State != BrowserInstallationLifecycleState.Retired)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return active is null
                    ? ManagedBrowserActiveSlotTransitionResult<StoredBrowserInstallation>.NotFound()
                    : ManagedBrowserActiveSlotTransitionResult<StoredBrowserInstallation>.Conflict(active);
            }

            var now = DateTimeOffset.UtcNow.ToString("O");
            if (active is not null)
            {
                var retired = await UpdateInstallationStateAsync(
                    connection, transaction, active.Identity.InstallationId, active.Revision,
                    BrowserInstallationLifecycleState.Active, BrowserInstallationLifecycleState.Retired,
                    now, cancellationToken).ConfigureAwait(false);
                if (retired != 1)
                    return await RollbackInstallationConflictAsync(
                        connection, transaction, cancellationToken).ConfigureAwait(false);
            }

            var activated = await UpdateInstallationStateAsync(
                connection, transaction, target.Identity.InstallationId, target.Revision,
                BrowserInstallationLifecycleState.Retired, BrowserInstallationLifecycleState.Active,
                now, cancellationToken).ConfigureAwait(false);
            if (activated != 1)
                return await RollbackInstallationConflictAsync(
                    connection, transaction, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            var current = await LoadInstallationAsync(
                target.Identity.InstallationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Managed browser installation state is invalid.");
            return ManagedBrowserActiveSlotTransitionResult<StoredBrowserInstallation>.Applied(current);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* Preserve the original safe failure. */ }
            throw;
        }
    }

    public async Task<ManagedBrowserTransitionResult> TryRetireInstallationAsync(
        ManagedBrowserTransitionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadInstallationAsync(
                connection, transaction, request.IdentityId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return ManagedBrowserTransitionResult.NotFound();
            }
            if (current.Revision != request.ExpectedRevision ||
                current.State != BrowserInstallationLifecycleState.Active)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return ManagedBrowserTransitionResult.Conflict(current.Revision);
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE BrowserInstallations
                SET State='Retired',Revision=Revision+1,UpdatedAt=$updated
                WHERE InstallationId=$id AND Revision=$revision AND State='Active'
                  AND NOT EXISTS(
                    SELECT 1 FROM ManagedBrowserSessions session
                    WHERE session.InstallationId=BrowserInstallations.InstallationId AND session.State='Active');
                """;
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", request.IdentityId.ToString("D"));
            command.Parameters.AddWithValue("$revision", request.ExpectedRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                var conflict = await LoadInstallationAsync(request.IdentityId, cancellationToken).ConfigureAwait(false);
                return conflict is null
                    ? ManagedBrowserTransitionResult.NotFound()
                    : ManagedBrowserTransitionResult.Conflict(conflict.Revision);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ManagedBrowserTransitionResult.Applied(checked(current.Revision + 1));
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* Preserve the original safe failure. */ }
            throw;
        }
    }

    public async Task<StoredManagedBrowserSession> CreateSessionAsync(
        ManagedBrowserSessionReference session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow.ToString("O");
        try
        {
            await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ManagedBrowserSessions(
                  SessionId,AccountId,BaiduUserId,InstallationId,BrowserFamily,ProfileId,
                  ProfileOwnershipId,State,Revision,CreatedAt,UpdatedAt)
                SELECT $session,$account,$uid,$installation,$family,$profile,$ownership,'Retired',0,$now,$now
                WHERE EXISTS(
                  SELECT 1 FROM Accounts
                  WHERE Id=$account AND BaiduUserId=$uid AND Status='Active' AND IsEnabled=1 AND IsDeleted=0);
                """;
            command.Parameters.AddWithValue("$session", session.SessionId.ToString("D"));
            command.Parameters.AddWithValue("$account", session.AccountId.ToString("D"));
            command.Parameters.AddWithValue("$uid", session.StableBaiduUserId);
            command.Parameters.AddWithValue("$installation", session.InstallationId.ToString("D"));
            command.Parameters.AddWithValue("$family", FormatFamily(session.BrowserFamily));
            command.Parameters.AddWithValue("$profile", session.ProfileId);
            command.Parameters.AddWithValue("$ownership", session.ProfileOwnershipId);
            command.Parameters.AddWithValue("$now", now);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException();
            return new StoredManagedBrowserSession(
                session, ManagedBrowserSessionLifecycleState.Retired, revision: 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            throw new InvalidOperationException("Managed browser session could not be created.");
        }
    }

    public async Task<StoredManagedBrowserSession?> LoadSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("A session ID is required.", nameof(sessionId));
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadSessionAsync(connection, null, sessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredManagedBrowserSession?> LoadActiveSessionAsync(
        Guid accountId,
        BrowserFamily browserFamily,
        CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("An account ID is required.", nameof(accountId));
        _ = FormatFamily(browserFamily);
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadActiveSessionAsync(
            connection, null, accountId, browserFamily, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ManagedBrowserActiveSlotTransitionResult<StoredManagedBrowserSession>> TryReplaceActiveSessionAsync(
        ManagedBrowserSessionActivationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var scopeAccount = request.Target.Identity.AccountId;
        var scopeFamily = request.Target.Identity.BrowserFamily;
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var active = await LoadActiveSessionAsync(
                connection, transaction, scopeAccount, scopeFamily, cancellationToken).ConfigureAwait(false);
            var target = await LoadSessionAsync(
                connection, transaction, request.Target.Identity.SessionId, cancellationToken).ConfigureAwait(false);
            if (target is null || target.Identity.AccountId != scopeAccount ||
                target.Identity.BrowserFamily != scopeFamily)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return ManagedBrowserActiveSlotTransitionResult<StoredManagedBrowserSession>.NotFound();
            }

            if (request.ExpectedCurrent is null)
            {
                if (active is not null)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return ManagedBrowserActiveSlotTransitionResult<StoredManagedBrowserSession>.Conflict(active);
                }
            }
            else if (active is null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return ManagedBrowserActiveSlotTransitionResult<StoredManagedBrowserSession>.NotFound();
            }
            else if (!Matches(active, request.ExpectedCurrent))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return ManagedBrowserActiveSlotTransitionResult<StoredManagedBrowserSession>.Conflict(active);
            }

            if (!Matches(target, request.Target) || target.State != ManagedBrowserSessionLifecycleState.Retired)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return active is null
                    ? ManagedBrowserActiveSlotTransitionResult<StoredManagedBrowserSession>.NotFound()
                    : ManagedBrowserActiveSlotTransitionResult<StoredManagedBrowserSession>.Conflict(active);
            }

            var now = DateTimeOffset.UtcNow.ToString("O");
            if (active is not null)
            {
                var retired = await UpdateSessionStateAsync(
                    connection, transaction, active.Identity.SessionId, active.Revision,
                    ManagedBrowserSessionLifecycleState.Active, ManagedBrowserSessionLifecycleState.Retired,
                    now, requireActiveAccount: false, cancellationToken).ConfigureAwait(false);
                if (retired != 1)
                    return await RollbackSessionConflictAsync(
                        connection, transaction, scopeAccount, scopeFamily, cancellationToken).ConfigureAwait(false);
            }

            var activated = await UpdateSessionStateAsync(
                connection, transaction, target.Identity.SessionId, target.Revision,
                ManagedBrowserSessionLifecycleState.Retired, ManagedBrowserSessionLifecycleState.Active,
                now, requireActiveAccount: true, cancellationToken).ConfigureAwait(false);
            if (activated != 1)
                return await RollbackSessionConflictAsync(
                    connection, transaction, scopeAccount, scopeFamily, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            var current = await LoadSessionAsync(target.Identity.SessionId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Managed browser session state is invalid.");
            return ManagedBrowserActiveSlotTransitionResult<StoredManagedBrowserSession>.Applied(current);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* Preserve the original safe failure. */ }
            throw;
        }
    }

    public async Task<ManagedBrowserTransitionResult> TryRetireSessionAsync(
        ManagedBrowserTransitionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadSessionAsync(
                connection, transaction, request.IdentityId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return ManagedBrowserTransitionResult.NotFound();
            }
            if (current.Revision != request.ExpectedRevision ||
                current.State != ManagedBrowserSessionLifecycleState.Active)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return ManagedBrowserTransitionResult.Conflict(current.Revision);
            }

            var changed = await UpdateSessionStateAsync(
                connection, transaction, request.IdentityId, request.ExpectedRevision,
                ManagedBrowserSessionLifecycleState.Active, ManagedBrowserSessionLifecycleState.Retired,
                DateTimeOffset.UtcNow.ToString("O"), requireActiveAccount: false, cancellationToken)
                .ConfigureAwait(false);
            if (changed != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                var conflict = await LoadSessionAsync(request.IdentityId, cancellationToken).ConfigureAwait(false);
                return conflict is null
                    ? ManagedBrowserTransitionResult.NotFound()
                    : ManagedBrowserTransitionResult.Conflict(conflict.Revision);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ManagedBrowserTransitionResult.Applied(checked(current.Revision + 1));
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* Preserve the original safe failure. */ }
            throw;
        }
    }

    public async Task<StoredManagedProfileCleanupManifest> CreateCleanupManifestAsync(
        ManagedProfileCleanupManifest manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow.ToString("O");
        try
        {
            await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ManagedProfileCleanupManifests(
                  ManifestId,AccountId,SessionId,State,SourceProfilePath,QuarantinePath,
                  ExpectedOwnershipId,ConfirmationTokenHash,Revision,CreatedAt,UpdatedAt)
                SELECT $manifest,$account,$session,'Prepared',$source,$quarantine,
                       $ownership,$confirmation,0,$now,$now
                FROM ManagedBrowserSessions managed
                WHERE managed.SessionId=$session AND managed.AccountId=$account
                  AND managed.ProfileOwnershipId=$ownership;
                """;
            command.Parameters.AddWithValue("$manifest", manifest.ManifestId.ToString("D"));
            command.Parameters.AddWithValue("$account", manifest.AccountId.ToString("D"));
            command.Parameters.AddWithValue("$session", manifest.SessionId.ToString("D"));
            command.Parameters.AddWithValue("$source", manifest.SourceProfilePath);
            command.Parameters.AddWithValue("$quarantine", manifest.QuarantinePath);
            command.Parameters.AddWithValue("$ownership", manifest.ExpectedOwnershipId);
            command.Parameters.AddWithValue("$confirmation", (object?)manifest.ConfirmationTokenHash ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", now);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException();
            return new StoredManagedProfileCleanupManifest(
                manifest, ManagedProfileCleanupState.Prepared, revision: 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            throw new InvalidOperationException("Managed profile cleanup manifest could not be created.");
        }
    }

    public async Task<StoredManagedProfileCleanupManifest?> LoadCleanupManifestAsync(
        Guid manifestId,
        CancellationToken cancellationToken)
    {
        if (manifestId == Guid.Empty)
            throw new ArgumentException("A manifest ID is required.", nameof(manifestId));
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadCleanupManifestAsync(
            connection, null, manifestId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ManagedBrowserTransitionResult> TryTransitionCleanupManifestAsync(
        ManagedProfileCleanupTransitionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadCleanupManifestAsync(
                connection, transaction, request.ManifestId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return ManagedBrowserTransitionResult.NotFound();
            }
            if (current.Revision != request.ExpectedRevision || current.State != request.ExpectedState)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return ManagedBrowserTransitionResult.Conflict(current.Revision);
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE ManagedProfileCleanupManifests
                SET State=$target,Revision=Revision+1,UpdatedAt=$updated
                WHERE ManifestId=$manifest AND Revision=$revision AND State=$expected;
                """;
            command.Parameters.AddWithValue("$target", FormatState(request.TargetState));
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$manifest", request.ManifestId.ToString("D"));
            command.Parameters.AddWithValue("$revision", request.ExpectedRevision);
            command.Parameters.AddWithValue("$expected", FormatState(request.ExpectedState));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                var conflict = await LoadCleanupManifestAsync(
                    request.ManifestId, cancellationToken).ConfigureAwait(false);
                return conflict is null
                    ? ManagedBrowserTransitionResult.NotFound()
                    : ManagedBrowserTransitionResult.Conflict(conflict.Revision);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ManagedBrowserTransitionResult.Applied(checked(current.Revision + 1));
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* Preserve the original safe failure. */ }
            throw;
        }
    }

    public async Task AddRetentionAsync(
        ManagedBrowserRetentionReference reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ManagedBrowserRetentions(
                  OwnerKind,OwnerId,InstallationId,SessionId,CreatedAt)
                SELECT $kind,$owner,$installation,$session,$created
                WHERE
                  ($kind='Operation' AND (
                    EXISTS(SELECT 1 FROM TransferOperations operation
                      WHERE operation.Id=$owner AND operation.ExecutionEngineVersion='ManagedChromium-v1'
                        AND operation.InstallationId=$installation AND operation.SessionId=$session)
                    OR EXISTS(SELECT 1 FROM ShareOperations operation
                      WHERE operation.Id=$owner AND operation.ExecutionEngineVersion='ManagedChromium-v1'
                        AND operation.InstallationId=$installation AND operation.SessionId=$session)
                    OR EXISTS(SELECT 1 FROM StandaloneShareOperations operation
                      WHERE operation.Id=$owner AND operation.ExecutionEngineVersion='ManagedChromium-v1'
                        AND operation.InstallationId=$installation AND operation.SessionId=$session)))
                  OR ($kind='Attempt' AND EXISTS(
                    SELECT 1 FROM WebAutomationAttempts attempt
                    WHERE attempt.OperationId=$owner AND attempt.ExecutionEngineVersion='ManagedChromium-v1'
                      AND attempt.InstallationId=$installation AND attempt.SessionId=$session))
                  OR ($kind='CleanupManifest' AND EXISTS(
                    SELECT 1 FROM ManagedProfileCleanupManifests manifest
                    JOIN ManagedBrowserSessions managed ON managed.SessionId=manifest.SessionId
                    WHERE manifest.ManifestId=$owner AND manifest.State<>'Deleted'
                      AND managed.InstallationId=$installation AND manifest.SessionId=$session))
                ON CONFLICT(OwnerKind,OwnerId,InstallationId,SessionId)
                DO UPDATE SET CreatedAt=ManagedBrowserRetentions.CreatedAt;
                """;
            AddRetentionParameters(command, reference);
            command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            throw new InvalidOperationException("Managed browser retention could not be added.");
        }
    }

    public async Task ReleaseRetentionAsync(
        ManagedBrowserRetentionReference reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM ManagedBrowserRetentions
            WHERE OwnerKind=$kind AND OwnerId=$owner AND InstallationId=$installation AND SessionId=$session;
            """;
        AddRetentionParameters(command, reference);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsRetirementEligibleAsync(
        Guid installationId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (installationId == Guid.Empty)
            throw new ArgumentException("An installation ID is required.", nameof(installationId));
        if (sessionId == Guid.Empty)
            throw new ArgumentException("A session ID is required.", nameof(sessionId));
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
              SELECT 1
              FROM BrowserInstallations installation
              JOIN ManagedBrowserSessions session
                ON session.InstallationId=installation.InstallationId
              WHERE installation.InstallationId=$installation AND session.SessionId=$session
                AND installation.State='Retired' AND session.State='Retired'
                AND NOT EXISTS(
                  SELECT 1 FROM ManagedBrowserRetentions retention
                  WHERE retention.InstallationId=installation.InstallationId
                    AND retention.SessionId=session.SessionId)
                AND NOT EXISTS(
                  SELECT 1 FROM TransferOperations operation
                  WHERE operation.ExecutionEngineVersion='ManagedChromium-v1'
                    AND operation.InstallationId=installation.InstallationId
                    AND operation.SessionId=session.SessionId)
                AND NOT EXISTS(
                  SELECT 1 FROM ShareOperations operation
                  WHERE operation.ExecutionEngineVersion='ManagedChromium-v1'
                    AND operation.InstallationId=installation.InstallationId
                    AND operation.SessionId=session.SessionId)
                AND NOT EXISTS(
                  SELECT 1 FROM StandaloneShareOperations operation
                  WHERE operation.ExecutionEngineVersion='ManagedChromium-v1'
                    AND operation.InstallationId=installation.InstallationId
                    AND operation.SessionId=session.SessionId)
                AND NOT EXISTS(
                  SELECT 1 FROM WebAutomationAttempts attempt
                  WHERE attempt.ExecutionEngineVersion='ManagedChromium-v1'
                    AND attempt.InstallationId=installation.InstallationId
                    AND attempt.SessionId=session.SessionId)
                AND NOT EXISTS(
                  SELECT 1 FROM ManagedProfileCleanupManifests manifest
                  WHERE manifest.SessionId=session.SessionId AND manifest.State<>'Deleted'));
            """;
        command.Parameters.AddWithValue("$installation", installationId.ToString("D"));
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }

    private static async Task<StoredBrowserInstallation?> LoadInstallationAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid installationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT InstallationId,BrowserFamily,InstallationDirectory,ExecutablePath,Publisher,
                   ProductName,FileIdentity,State,Revision
            FROM BrowserInstallations WHERE InstallationId=$id;
            """;
        command.Parameters.AddWithValue("$id", installationId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var current = ReadInstallation(reader);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Managed browser installation state is invalid.");
        return current;
    }

    private static async Task<StoredBrowserInstallation?> LoadActiveInstallationAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT InstallationId,BrowserFamily,InstallationDirectory,ExecutablePath,Publisher,
                   ProductName,FileIdentity,State,Revision
            FROM BrowserInstallations WHERE State='Active';
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var current = ReadInstallation(reader);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Managed browser installation state is invalid.");
        return current;
    }

    private static StoredBrowserInstallation ReadInstallation(SqliteDataReader reader)
    {
        try
        {
            var family = reader.GetString(1) switch
            {
                "Chrome" => BrowserFamily.Chrome,
                "Edge" => BrowserFamily.Edge,
                _ => throw new InvalidDataException(),
            };
            var state = reader.GetString(7) switch
            {
                "Active" => BrowserInstallationLifecycleState.Active,
                "Retired" => BrowserInstallationLifecycleState.Retired,
                _ => throw new InvalidDataException(),
            };
            var identity = new BrowserInstallationIdentity(
                Guid.ParseExact(reader.GetString(0), "D"), family, reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6));
            return new StoredBrowserInstallation(identity, state, reader.GetInt64(8));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidDataException)
        {
            throw new InvalidDataException("Managed browser installation state is invalid.");
        }
    }

    private static async Task<StoredManagedBrowserSession?> LoadSessionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT SessionId,InstallationId,AccountId,BaiduUserId,BrowserFamily,ProfileId,
                   ProfileOwnershipId,State,Revision
            FROM ManagedBrowserSessions WHERE SessionId=$id;
            """;
        command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var current = ReadSession(reader);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Managed browser session state is invalid.");
        return current;
    }

    private static async Task<StoredManagedBrowserSession?> LoadActiveSessionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid accountId,
        BrowserFamily browserFamily,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT session.SessionId,session.InstallationId,session.AccountId,session.BaiduUserId,
                   session.BrowserFamily,session.ProfileId,session.ProfileOwnershipId,session.State,session.Revision
            FROM ManagedBrowserSessions session
            JOIN Accounts account ON account.Id=session.AccountId
            WHERE session.AccountId=$account AND session.BrowserFamily=$family AND session.State='Active'
              AND account.Status='Active' AND account.IsEnabled=1 AND account.IsDeleted=0;
            """;
        command.Parameters.AddWithValue("$account", accountId.ToString("D"));
        command.Parameters.AddWithValue("$family", FormatFamily(browserFamily));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var current = ReadSession(reader);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Managed browser session state is invalid.");
        return current;
    }

    private static StoredManagedBrowserSession ReadSession(SqliteDataReader reader)
    {
        try
        {
            var family = reader.GetString(4) switch
            {
                "Chrome" => BrowserFamily.Chrome,
                "Edge" => BrowserFamily.Edge,
                _ => throw new InvalidDataException(),
            };
            var state = reader.GetString(7) switch
            {
                "Active" => ManagedBrowserSessionLifecycleState.Active,
                "Retired" => ManagedBrowserSessionLifecycleState.Retired,
                "QuarantinePending" => ManagedBrowserSessionLifecycleState.QuarantinePending,
                "Quarantined" => ManagedBrowserSessionLifecycleState.Quarantined,
                "CleanupPending" => ManagedBrowserSessionLifecycleState.CleanupPending,
                "Deleted" => ManagedBrowserSessionLifecycleState.Deleted,
                _ => throw new InvalidDataException(),
            };
            var identity = new ManagedBrowserSessionReference(
                Guid.ParseExact(reader.GetString(0), "D"), Guid.ParseExact(reader.GetString(1), "D"),
                Guid.ParseExact(reader.GetString(2), "D"), reader.GetString(3), family,
                reader.GetString(5), reader.GetString(6));
            return new StoredManagedBrowserSession(identity, state, reader.GetInt64(8));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidDataException)
        {
            throw new InvalidDataException("Managed browser session state is invalid.");
        }
    }

    private static async Task<StoredManagedProfileCleanupManifest?> LoadCleanupManifestAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid manifestId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ManifestId,AccountId,SessionId,State,SourceProfilePath,QuarantinePath,
                   ExpectedOwnershipId,ConfirmationTokenHash,Revision
            FROM ManagedProfileCleanupManifests WHERE ManifestId=$manifest;
            """;
        command.Parameters.AddWithValue("$manifest", manifestId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var current = ReadCleanupManifest(reader);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Managed profile cleanup manifest state is invalid.");
        return current;
    }

    private static StoredManagedProfileCleanupManifest ReadCleanupManifest(SqliteDataReader reader)
    {
        try
        {
            var state = reader.GetString(3) switch
            {
                "Prepared" => ManagedProfileCleanupState.Prepared,
                "QuarantineMoveIntent" => ManagedProfileCleanupState.QuarantineMoveIntent,
                "Quarantined" => ManagedProfileCleanupState.Quarantined,
                "DeleteIntent" => ManagedProfileCleanupState.DeleteIntent,
                "CleanupPending" => ManagedProfileCleanupState.CleanupPending,
                "Deleted" => ManagedProfileCleanupState.Deleted,
                _ => throw new InvalidDataException(),
            };
            var manifest = new ManagedProfileCleanupManifest(
                Guid.ParseExact(reader.GetString(0), "D"), Guid.ParseExact(reader.GetString(1), "D"),
                Guid.ParseExact(reader.GetString(2), "D"), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7));
            return new StoredManagedProfileCleanupManifest(manifest, state, reader.GetInt64(8));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidDataException or
                                                 InvalidCastException or OverflowException)
        {
            throw new InvalidDataException("Managed profile cleanup manifest state is invalid.");
        }
    }

    private static async Task<int> UpdateInstallationStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid installationId,
        long expectedRevision,
        BrowserInstallationLifecycleState expectedState,
        BrowserInstallationLifecycleState targetState,
        string updatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE BrowserInstallations
            SET State=$target,Revision=Revision+1,UpdatedAt=$updated
            WHERE InstallationId=$id AND Revision=$revision AND State=$expected;
            """;
        command.Parameters.AddWithValue("$target", FormatState(targetState));
        command.Parameters.AddWithValue("$updated", updatedAt);
        command.Parameters.AddWithValue("$id", installationId.ToString("D"));
        command.Parameters.AddWithValue("$revision", expectedRevision);
        command.Parameters.AddWithValue("$expected", FormatState(expectedState));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ManagedBrowserActiveSlotTransitionResult<StoredBrowserInstallation>>
        RollbackInstallationConflictAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        var active = await LoadActiveInstallationAsync(connection, null, cancellationToken).ConfigureAwait(false);
        return active is null
            ? ManagedBrowserActiveSlotTransitionResult<StoredBrowserInstallation>.NotFound()
            : ManagedBrowserActiveSlotTransitionResult<StoredBrowserInstallation>.Conflict(active);
    }

    private static async Task<int> UpdateSessionStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        long expectedRevision,
        ManagedBrowserSessionLifecycleState expectedState,
        ManagedBrowserSessionLifecycleState targetState,
        string updatedAt,
        bool requireActiveAccount,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ManagedBrowserSessions
            SET State=$target,Revision=Revision+1,UpdatedAt=$updated
            WHERE SessionId=$id AND Revision=$revision AND State=$expected
              AND ($requireActive=0 OR EXISTS(
                SELECT 1 FROM Accounts account
                WHERE account.Id=ManagedBrowserSessions.AccountId AND account.BaiduUserId=ManagedBrowserSessions.BaiduUserId
                  AND account.Status='Active' AND account.IsEnabled=1 AND account.IsDeleted=0));
            """;
        command.Parameters.AddWithValue("$target", FormatState(targetState));
        command.Parameters.AddWithValue("$updated", updatedAt);
        command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$revision", expectedRevision);
        command.Parameters.AddWithValue("$expected", FormatState(expectedState));
        command.Parameters.AddWithValue("$requireActive", requireActiveAccount ? 1 : 0);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ManagedBrowserActiveSlotTransitionResult<StoredManagedBrowserSession>>
        RollbackSessionConflictAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            Guid accountId,
            BrowserFamily browserFamily,
            CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        var active = await LoadActiveSessionAsync(
            connection, null, accountId, browserFamily, cancellationToken).ConfigureAwait(false);
        return active is null
            ? ManagedBrowserActiveSlotTransitionResult<StoredManagedBrowserSession>.NotFound()
            : ManagedBrowserActiveSlotTransitionResult<StoredManagedBrowserSession>.Conflict(active);
    }

    private static bool Matches(StoredBrowserInstallation actual, StoredBrowserInstallation expected) =>
        actual.Identity.InstallationId == expected.Identity.InstallationId &&
        actual.Revision == expected.Revision && actual.State == expected.State;

    private static bool Matches(StoredManagedBrowserSession actual, StoredManagedBrowserSession expected) =>
        actual.Identity.SessionId == expected.Identity.SessionId &&
        actual.Identity.AccountId == expected.Identity.AccountId &&
        actual.Identity.BrowserFamily == expected.Identity.BrowserFamily &&
        actual.Revision == expected.Revision && actual.State == expected.State;

    private static string FormatFamily(BrowserFamily family) => family switch
    {
        BrowserFamily.Chrome => "Chrome",
        BrowserFamily.Edge => "Edge",
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, "A managed browser family is required."),
    };

    private static string FormatState(BrowserInstallationLifecycleState state) => state switch
    {
        BrowserInstallationLifecycleState.Active => "Active",
        BrowserInstallationLifecycleState.Retired => "Retired",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "A valid installation state is required."),
    };

    private static string FormatState(ManagedBrowserSessionLifecycleState state) => state switch
    {
        ManagedBrowserSessionLifecycleState.Active => "Active",
        ManagedBrowserSessionLifecycleState.Retired => "Retired",
        ManagedBrowserSessionLifecycleState.QuarantinePending => "QuarantinePending",
        ManagedBrowserSessionLifecycleState.Quarantined => "Quarantined",
        ManagedBrowserSessionLifecycleState.CleanupPending => "CleanupPending",
        ManagedBrowserSessionLifecycleState.Deleted => "Deleted",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "A valid session state is required."),
    };

    private static string FormatState(ManagedProfileCleanupState state) => state switch
    {
        ManagedProfileCleanupState.Prepared => "Prepared",
        ManagedProfileCleanupState.QuarantineMoveIntent => "QuarantineMoveIntent",
        ManagedProfileCleanupState.Quarantined => "Quarantined",
        ManagedProfileCleanupState.DeleteIntent => "DeleteIntent",
        ManagedProfileCleanupState.CleanupPending => "CleanupPending",
        ManagedProfileCleanupState.Deleted => "Deleted",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "A valid cleanup state is required."),
    };

    private static void AddRetentionParameters(
        SqliteCommand command,
        ManagedBrowserRetentionReference reference)
    {
        command.Parameters.AddWithValue("$kind", reference.OwnerKind switch
        {
            ManagedBrowserRetentionOwnerKind.Operation => "Operation",
            ManagedBrowserRetentionOwnerKind.Attempt => "Attempt",
            ManagedBrowserRetentionOwnerKind.CleanupManifest => "CleanupManifest",
            _ => throw new ArgumentOutOfRangeException(nameof(reference), "A retention owner kind is required."),
        });
        command.Parameters.AddWithValue("$owner", reference.OwnerId.ToString("D"));
        command.Parameters.AddWithValue("$installation", reference.InstallationId.ToString("D"));
        command.Parameters.AddWithValue("$session", reference.SessionId.ToString("D"));
    }
}

internal static class SqliteExecutionProfileSnapshotCodec
{
    internal static string FormatEngine(BaiduExecutionEngine engine) => engine switch
    {
        BaiduExecutionEngine.WebView2V1 => "WebView2-v1",
        BaiduExecutionEngine.ManagedChromiumV1 => "ManagedChromium-v1",
        _ => throw new InvalidDataException("The execution profile snapshot is invalid."),
    };

    internal static string FormatFamily(BrowserFamily family) => family switch
    {
        BrowserFamily.LegacyWebView2 => "LegacyWebView2",
        BrowserFamily.Chrome => "Chrome",
        BrowserFamily.Edge => "Edge",
        _ => throw new InvalidDataException("The execution profile snapshot is invalid."),
    };

    internal static ExecutionProfileSnapshot Read(SqliteDataReader reader, int offset)
    {
        try
        {
            var engine = reader.GetString(offset) switch
            {
                "WebView2-v1" => BaiduExecutionEngine.WebView2V1,
                "ManagedChromium-v1" => BaiduExecutionEngine.ManagedChromiumV1,
                _ => throw new InvalidDataException(),
            };
            var family = reader.GetString(offset + 1) switch
            {
                "LegacyWebView2" => BrowserFamily.LegacyWebView2,
                "Chrome" => BrowserFamily.Chrome,
                "Edge" => BrowserFamily.Edge,
                _ => throw new InvalidDataException(),
            };
            return new ExecutionProfileSnapshot(
                engine, family, reader.GetString(offset + 2),
                reader.IsDBNull(offset + 3) ? null : Guid.ParseExact(reader.GetString(offset + 3), "D"),
                reader.IsDBNull(offset + 4) ? null : Guid.ParseExact(reader.GetString(offset + 4), "D"));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidDataException)
        {
            throw new InvalidDataException("The execution profile snapshot is invalid.");
        }
    }
}
