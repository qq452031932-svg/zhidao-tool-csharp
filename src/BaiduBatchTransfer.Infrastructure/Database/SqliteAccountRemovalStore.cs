using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BaiduBatchTransfer.Application.Accounts;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed class SqliteAccountRemovalStore : IAccountRemovalStore
{
    private const string SafeTerminalOperationPredicate = """
        ((operation.Status IN ('Succeeded','Failed')
          AND operation.SubmitState NOT IN ('Submitting','PendingConfirmation')
          AND operation.TargetSlotState<>'Reserved'
          AND operation.LeaseExpiresAt IS NULL)
         OR
         (operation.Status='Cancelled'
          AND operation.DailyCountedAt IS NULL
          AND operation.ConfirmationDescriptorJson IS NULL
          AND operation.SubmitStartedAt IS NULL
          AND operation.AdapterRequestId IS NULL
          AND operation.RemoteItemIdsJson IS NULL
          AND operation.SubmitState IN ('NotPrepared','PreparingToSubmit')
          AND operation.TargetSlotState IN ('Unoccupied','Released')
          AND operation.TargetSlotReservedAt IS NULL
          AND operation.LeaseExpiresAt IS NULL))
        """;
    private const string ReadyMessage = "账号可以安全退出。";
    private const string CleanupReadyMessage = "账号可以安全退出；共享批次的公共审计信息和其他账号仍在使用的数据将保留。";
    private const string BlockedMessage = "账号仍有未完成工作，请先完成、取消或重新分配。";
    private const string NotFoundMessage = "账号不存在。";
    private const string RemovedMessage = "账号已经退出。";
    private const string StaleMessage = "确认信息已过期，请重新确认。";
    private readonly SqliteConnectionFactory factory;
    private readonly Func<SqliteConnection, SqliteTransaction, CancellationToken, Task>? afterFinalValidation;
    private readonly Func<SqliteConnection, SqliteTransaction, CancellationToken, Task>? afterPreviewSnapshot;
    private readonly Func<SqliteConnection, SqliteTransaction, CancellationToken, Task>? afterCredentialClearBeforeCommit;

    public SqliteAccountRemovalStore(SqliteConnectionFactory factory) : this(factory, null, null, null) { }

    internal SqliteAccountRemovalStore(
        SqliteConnectionFactory factory,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task>? afterFinalValidation) :
        this(factory, afterFinalValidation, null, null) { }

    internal SqliteAccountRemovalStore(
        SqliteConnectionFactory factory,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task>? afterFinalValidation,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task>? afterPreviewSnapshot) :
        this(factory, afterFinalValidation, afterPreviewSnapshot, null) { }

    internal SqliteAccountRemovalStore(
        SqliteConnectionFactory factory,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task>? afterFinalValidation,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task>? afterPreviewSnapshot,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task>? afterCredentialClearBeforeCommit)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.afterFinalValidation = afterFinalValidation;
        this.afterPreviewSnapshot = afterPreviewSnapshot;
        this.afterCredentialClearBeforeCommit = afterCredentialClearBeforeCommit;
    }

    public async Task<AccountPage> GetAccountsPageAsync(int pageNumber, int pageSize, CancellationToken token)
    {
        if (pageNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        if (pageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var offset = checked(((long)pageNumber - 1L) * pageSize);
        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        var total = await ScalarLongAsync(connection, null,
            "SELECT COUNT(*) FROM Accounts WHERE IsDeleted=0 AND Status<>'Deleted';", token).ConfigureAwait(false);
        await using var command = Command(connection, null,
            "SELECT Id,DisplayName,Status,IsEnabled FROM Accounts WHERE IsDeleted=0 AND Status<>'Deleted' ORDER BY SortOrder,DisplayName,Id LIMIT $limit OFFSET $offset;");
        Add(command, "$limit", pageSize);
        Add(command, "$offset", offset);
        var items = new List<AccountListItem>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            items.Add(new AccountListItem(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0));
        return new AccountPage(items, pageNumber, pageSize, total);
    }

    public async Task<AccountExitPreview> PreviewAsync(Guid accountId, AccountExitHistoryMode mode, CancellationToken token)
    {
        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var account = await ReadAccountAsync(connection, transaction, accountId, token).ConfigureAwait(false);
        if (afterPreviewSnapshot is not null)
            await afterPreviewSnapshot(connection, transaction, token).ConfigureAwait(false);
        if (account is null)
        {
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return Preview(AccountExitStatus.NotFound, string.Empty, NotFoundMessage);
        }
        if (account.IsDeleted || account.Status == "Deleted")
        {
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return Preview(AccountExitStatus.AlreadyRemoved, account.DisplayName, RemovedMessage);
        }
        var manifest = await BuildManifestAsync(connection, transaction, account, mode, token).ConfigureAwait(false);
        if (await HasActiveWorkAsync(connection, transaction, accountId, mode, token).ConfigureAwait(false))
        {
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return Preview(AccountExitStatus.ActiveWorkBlocked, account.DisplayName, BlockedMessage);
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return new AccountExitPreview(AccountExitStatus.Ready, account.DisplayName, manifest.TerminalOperations,
            manifest.TerminalBatches, manifest.SharedBatches, Convert.ToHexString(manifest.Digest),
            mode == AccountExitHistoryMode.RemoveTerminalHistory ? CleanupReadyMessage : ReadyMessage);
    }

    public async Task<AccountRemovalPreparation> PrepareRemovalAsync(AccountExitRequest request, CancellationToken token)
    {
        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var account = await ReadAccountAsync(connection, transaction, request.AccountId, token).ConfigureAwait(false);
        if (account is null)
            return await RollbackResultAsync(transaction, AccountRemovalPreparationStatus.NotFound, request.AccountId, NotFoundMessage).ConfigureAwait(false);
        if (account.IsDeleted || account.Status == "Deleted")
            return await RollbackResultAsync(transaction, AccountRemovalPreparationStatus.AlreadyRemoved, request.AccountId, RemovedMessage).ConfigureAwait(false);
        var manifest = await BuildManifestAsync(connection, transaction, account, request.HistoryMode, token).ConfigureAwait(false);
        if (await HasActiveWorkAsync(connection, transaction, request.AccountId, request.HistoryMode, token).ConfigureAwait(false))
            return await RollbackResultAsync(transaction, AccountRemovalPreparationStatus.ActiveWorkBlocked, request.AccountId, BlockedMessage).ConfigureAwait(false);
        if (!TokenEquals(manifest.Digest, request.ConfirmationToken))
            return await RollbackResultAsync(transaction, AccountRemovalPreparationStatus.StaleConfirmation, request.AccountId, StaleMessage).ConfigureAwait(false);
        if (request.HistoryMode == AccountExitHistoryMode.RemoveTerminalHistory)
            await DeleteTerminalHistoryAsync(connection, transaction, request.AccountId, token).ConfigureAwait(false);

        if (afterFinalValidation is not null)
            await afterFinalValidation(connection, transaction, token).ConfigureAwait(false);

        await using (var update = Command(connection, transaction, """
            UPDATE Accounts
            SET Status='Disabled',IsEnabled=0,EncryptedSession=NULL,LastValidatedAt=NULL
            WHERE Id=$id AND IsDeleted=0 AND Status<>'Deleted';
            """))
        {
            Add(update, "$id", Id(request.AccountId));
            if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                return await RollbackResultAsync(transaction, AccountRemovalPreparationStatus.StaleConfirmation, request.AccountId, StaleMessage).ConfigureAwait(false);
        }

        if (afterCredentialClearBeforeCommit is not null)
            await afterCredentialClearBeforeCommit(connection, transaction, token).ConfigureAwait(false);

        await EnsureForeignKeysAsync(connection, transaction, token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return new AccountRemovalPreparation(
            AccountRemovalPreparationStatus.Prepared,
            request.AccountId,
            true,
            "账号已安全退出。");
    }

    public async Task CompletePreparedRemovalAsync(
        Guid accountId,
        DateTimeOffset removedAt,
        Func<Guid, string?, CancellationToken, Task> externalCleanup,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(externalCleanup);
        if (accountId == Guid.Empty)
            throw new InvalidOperationException("Account completion preconditions were not satisfied.");

        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var account = await ReadAccountAsync(connection, transaction, accountId, token).ConfigureAwait(false);
            if (account is null)
                throw new InvalidOperationException("Account completion preconditions were not satisfied.");

            if (account.IsDeleted)
            {
                var safeTombstone = account.Status == "Deleted" &&
                    !account.IsEnabled &&
                    account.EncryptedSession is null &&
                    account.ProfilePath is null &&
                    account.LastValidatedAt is null &&
                    await ScalarLongAsync(
                        connection,
                        transaction,
                        "SELECT COUNT(*) FROM Accounts WHERE Id=$id AND DeletedAt IS NOT NULL;",
                        token,
                        ("$id", Id(accountId))).ConfigureAwait(false) == 1;
                if (!safeTombstone)
                    throw new InvalidOperationException("Account completion preconditions were not satisfied.");

                await EnsureForeignKeysAsync(connection, transaction, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return;
            }

            if (account.Status != "Disabled" ||
                account.IsEnabled ||
                account.EncryptedSession is not null ||
                account.LastValidatedAt is not null)
            {
                throw new InvalidOperationException("Account completion preconditions were not satisfied.");
            }

            await externalCleanup(accountId, account.ProfilePath, token).ConfigureAwait(false);

            await using var update = Command(connection, transaction, """
                UPDATE Accounts
                SET Status='Deleted',IsDeleted=1,DeletedAt=$removed,WebView2ProfileDirectory=NULL
                WHERE Id=$id AND Status='Disabled' AND IsEnabled=0 AND EncryptedSession IS NULL
                  AND LastValidatedAt IS NULL AND IsDeleted=0;
                """);
            Add(update, "$removed", Time(removedAt));
            Add(update, "$id", Id(accountId));
            if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Account completion preconditions were not satisfied.");

            await EnsureForeignKeysAsync(connection, transaction, token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static AccountExitPreview Preview(AccountExitStatus status, string displayName, string message) =>
        new(status, displayName, 0, 0, 0, string.Empty, message);

    private static async Task<AccountRemovalPreparation> RollbackResultAsync(
        SqliteTransaction transaction,
        AccountRemovalPreparationStatus status,
        Guid accountId,
        string message)
    {
        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        return new AccountRemovalPreparation(status, accountId, false, message);
    }

    private static bool TokenEquals(byte[] expected, string? suppliedToken)
    {
        var supplied = new byte[expected.Length];
        var valid = false;
        if (suppliedToken?.Length == expected.Length * 2)
        {
            try
            {
                var decoded = Convert.FromHexString(suppliedToken);
                if (decoded.Length == supplied.Length)
                {
                    decoded.CopyTo(supplied, 0);
                    valid = true;
                }
                CryptographicOperations.ZeroMemory(decoded);
            }
            catch (FormatException) { }
        }
        var equal = CryptographicOperations.FixedTimeEquals(expected, supplied);
        CryptographicOperations.ZeroMemory(supplied);
        return valid && equal;
    }

    private static async Task<bool> HasActiveWorkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid accountId,
        AccountExitHistoryMode mode,
        CancellationToken token)
    {
        var count = await ScalarLongAsync(connection, transaction, $"""
            SELECT
              (SELECT COUNT(*) FROM TransferOperations operation
                WHERE operation.AssignedAccountId=$account AND NOT {SafeTerminalOperationPredicate})
              +
              (SELECT COUNT(*) FROM AccountAllocations allocation
                 JOIN BatchJobs batch ON batch.Id=allocation.BatchJobId
                WHERE allocation.AccountId=$account
                  AND batch.Status IN ('Queued','Active','Paused','AwaitingCandidates','PausedReady'))
              +
              (SELECT COUNT(*) FROM BatchJobs batch
                 JOIN temp.AccountRemovalManifestTargetBatches relevant ON relevant.Id=batch.Id
                WHERE batch.Status IN ('Queued','Active','Paused','AwaitingCandidates','PausedReady'))
              +
              (SELECT COUNT(*) FROM ImportSessions import
                WHERE import.Status IN ('Parsing','Draft','Prechecking','ReadyToConfirm')
                  AND (import.PrecheckAccountId=$account
                    OR import.CreatedBatchJobId IN (SELECT Id FROM temp.AccountRemovalManifestTargetBatches)
                    OR import.AppendToBatchJobId IN (SELECT Id FROM temp.AccountRemovalManifestTargetBatches)
                    OR EXISTS(SELECT 1 FROM BatchJobs batch
                               JOIN temp.AccountRemovalManifestTargetBatches relevant ON relevant.Id=batch.Id
                               WHERE batch.ImportSessionId=import.Id)))
              ;
            """, token, ("$account", Id(accountId))).ConfigureAwait(false);
        if (count != 0 || mode != AccountExitHistoryMode.RemoveTerminalHistory)
            return count != 0;

        count = await ScalarLongAsync(connection, transaction, $"""
            SELECT
              (SELECT COUNT(*) FROM TransferOperations operation
                 JOIN temp.AccountRemovalManifestTerminalBatches terminal ON terminal.Id=operation.BatchJobId
                WHERE NOT {SafeTerminalOperationPredicate})
              +
              (SELECT COUNT(*) FROM ResourceItems resource
                 JOIN temp.AccountRemovalManifestTerminalBatches terminal ON terminal.Id=resource.BatchJobId
                WHERE resource.CandidateStatus='Reserved'
                   OR resource.ReservedAccountId IS NOT NULL
                   OR resource.ReservationExpiresAt IS NOT NULL)
              +
              (SELECT COUNT(*) FROM ImportSessions import
                 JOIN temp.AccountRemovalManifestImports relevant ON relevant.Id=import.Id
                WHERE import.Status IN ('Parsing','Draft','Prechecking','ReadyToConfirm')
              );
            """, token).ConfigureAwait(false);
        return count != 0;
    }

    private static async Task<AccountSnapshot?> ReadAccountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid accountId,
        CancellationToken token)
    {
        await using var command = Command(connection, transaction, """
            SELECT Id,BaiduUserId,DisplayName,EncryptedSession,WebView2ProfileDirectory,LoginMethod,Status,CapacityTotal,CapacityUsed,
              DailyLimit,DailyExecutedCount,SortOrder,IsEnabled,LastValidatedAt,DailyCountDateKey,IsDeleted,DeletedAt
            FROM Accounts WHERE Id=$id;
            """);
        Add(command, "$id", Id(accountId));
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        return new AccountSnapshot(accountId, reader.GetString(1), reader.GetString(2), ReadBlob(reader, 3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(6), reader.GetInt64(12) != 0,
            reader.IsDBNull(13) ? null : reader.GetString(13), reader.GetInt64(15) != 0);
    }

    private static async Task<Manifest> BuildManifestAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AccountSnapshot account,
        AccountExitHistoryMode mode,
        CancellationToken token)
    {
        var includeCleanupClosure = mode == AccountExitHistoryMode.RemoveTerminalHistory;
        await MaterializeManifestClosureAsync(connection, transaction, account.Id, includeCleanupClosure, token).ConfigureAwait(false);
        using var writer = new CanonicalWriter();
        writer.Value("manifest-version", "2");
        writer.Value("history-mode", mode.ToString());
        writer.Value("account-id", Id(account.Id));
        writer.Value("identity-fingerprint", Fingerprint(account.BaiduUserId));

        await AppendQueryAsync(connection, transaction, writer, "Accounts", """
            SELECT Id,DisplayName,EncryptedSession,WebView2ProfileDirectory,LoginMethod,Status,CapacityTotal,CapacityUsed,DailyLimit,
              DailyExecutedCount,SortOrder,IsEnabled,LastValidatedAt,DailyCountDateKey,IsDeleted,DeletedAt
            FROM Accounts WHERE Id=$account ORDER BY Id;
            """, token, ["LastValidatedAt", "DeletedAt"], ["EncryptedSession", "WebView2ProfileDirectory"], ("$account", (object?)Id(account.Id))).ConfigureAwait(false);

        await AppendQueryAsync(connection, transaction, writer, "BatchJobs", """
            SELECT batch.Id,batch.Name,batch.SourceType,batch.TransferMode,batch.QuotaBasis,batch.ShortagePolicy,batch.ResourceCount,
              batch.OperationCount,batch.DefaultTargetPath,batch.FolderCreationMode,batch.DuplicatePolicy,batch.ValidationMode,batch.Status,
              batch.CreatedAt,batch.StartedAt,batch.CompletedAt,batch.ImportSessionId,batch.QueueOrder,batch.NextAllocationOrdinal
            FROM BatchJobs batch JOIN temp.AccountRemovalManifestBatches closure ON closure.Id=batch.Id
            ORDER BY batch.Id;
            """, token, ["CreatedAt", "StartedAt", "CompletedAt"], []).ConfigureAwait(false);
        await AppendQueryAsync(connection, transaction, writer, "TransferOperations", """
            SELECT operation.Id,operation.ResourceItemId,operation.BatchJobId,operation.SequenceNumber,operation.AllocationOrdinal,
              operation.TargetPath,operation.NormalizedTargetPath,operation.CreateFolderName,operation.AssignedAccountId,
              operation.AllocationSource,operation.Status,operation.OutcomeKind,operation.AttemptCount,operation.OperationFingerprint,
              operation.ConfirmationDescriptorJson,operation.AdapterRequestId,operation.RemoteItemIdsJson,operation.SubmitState,
              operation.TargetSlotState,operation.TargetSlotReservedAt,operation.DailyCountedAt,operation.SubmitStartedAt,
              operation.LastErrorCode,operation.LastErrorMessage,operation.LeaseExpiresAt,operation.CreatedAt,operation.UpdatedAt
            FROM TransferOperations operation
            JOIN temp.AccountRemovalManifestBatches closure ON closure.Id=operation.BatchJobId
            ORDER BY operation.BatchJobId,operation.AllocationOrdinal,operation.SequenceNumber,operation.Id;
            """, token, ["TargetSlotReservedAt", "DailyCountedAt", "SubmitStartedAt", "LeaseExpiresAt", "CreatedAt", "UpdatedAt"], []).ConfigureAwait(false);
        await AppendQueryAsync(connection, transaction, writer, "ResourceItems", """
            SELECT resource.Id,resource.BatchJobId,resource.StagedImportRowId,resource.CandidateSequence,resource.ShareUrl,
              resource.NormalizedShareId,resource.ExtractionCode,resource.Note,resource.TargetPathOverride,
              resource.NormalizedTargetPathOverride,resource.CreateFolderName,resource.FolderCreationModeOverride,
              resource.GlobalValidityStatus,resource.CandidateConstraintAccountId,resource.CandidateStatus,resource.ReservedAccountId,
              resource.ReservationExpiresAt,resource.CreatedAt,resource.UpdatedAt
            FROM ResourceItems resource JOIN temp.AccountRemovalManifestResources closure ON closure.Id=resource.Id
            ORDER BY resource.BatchJobId,resource.CandidateSequence,resource.Id;
            """, token, ["ReservationExpiresAt", "CreatedAt", "UpdatedAt"], []).ConfigureAwait(false);
        await AppendQueryAsync(connection, transaction, writer, "AccountAllocations", """
            SELECT allocation.Id,allocation.BatchJobId,allocation.AccountId,allocation.PriorityOrder,allocation.DefaultTargetPath,
              allocation.NormalizedDefaultTargetPath,allocation.TargetItemCount,allocation.TargetSuccessCount,
              allocation.EffectiveTargetSuccessCount,allocation.AssignedItemCount,allocation.AttemptedItemCount,
              allocation.NewSuccessItemCount,allocation.AlreadySatisfiedItemCount,allocation.SuccessItemCount,
              allocation.FailedItemCount,allocation.LockedItemCount,allocation.ExclusiveCandidateCount,
              allocation.UnusedCandidateCount,allocation.ReservedSlotCount,allocation.FulfilledCount
            FROM AccountAllocations allocation JOIN temp.AccountRemovalManifestBatches closure ON closure.Id=allocation.BatchJobId
            ORDER BY allocation.BatchJobId,allocation.PriorityOrder,allocation.AccountId,allocation.Id;
            """, token, [], []).ConfigureAwait(false);
        await AppendQueryAsync(connection, transaction, writer, "ImportSessions", """
            SELECT import.Id,import.SourceType,import.SourceName,import.Status,import.TotalRows,import.ReadyRows,import.InvalidRows,
              import.DuplicateRows,import.ExcludedRows,import.PrecheckAccountId,import.CreatedBatchJobId,import.AppendToBatchJobId,
              import.CreatedAt,import.UpdatedAt
            FROM ImportSessions import JOIN temp.AccountRemovalManifestImports closure ON closure.Id=import.Id
            ORDER BY import.Id;
            """, token, ["CreatedAt", "UpdatedAt"], []).ConfigureAwait(false);
        if (includeCleanupClosure)
            await AppendQueryAsync(connection, transaction, writer, "StagedImportRows", """
                SELECT staged.Id,staged.ImportSessionId,staged.SourceRowNumber,staged.ShareUrl,staged.NormalizedShareId,
                  staged.ExtractionCode,staged.TargetPath,staged.CreateFolderName,staged.RequestedAccountName,staged.MappedAccountId,
                  staged.Note,staged.Disposition,staged.DuplicateOfStagedRowId,staged.ValidationMessage,staged.PrecheckStatus,
                  staged.PrecheckErrorCode,staged.PrecheckedAt,staged.UpdatedAt
                FROM StagedImportRows staged JOIN temp.AccountRemovalManifestStagedRows closure ON closure.Id=staged.Id
                ORDER BY staged.ImportSessionId,staged.SourceRowNumber,staged.Id;
                """, token, ["PrecheckedAt", "UpdatedAt"], []).ConfigureAwait(false);
        await AppendQueryAsync(connection, transaction, writer, "ExecutionLogs", """
            SELECT log.Id,log.BatchJobId,log.ResourceItemId,log.TransferOperationId,log.AccountId,
              log.Stage,log.Level,log.EventCode,log.SanitizedMessage,log.CreatedAt
            FROM ExecutionLogs log JOIN temp.AccountRemovalManifestLogs closure ON closure.Id=log.Id
            ORDER BY log.CreatedAt,log.Id;
            """, token, ["CreatedAt"], []).ConfigureAwait(false);

        var terminalOperations = await ScalarLongAsync(connection, transaction,
            "SELECT COUNT(*) FROM temp.AccountRemovalManifestTargetOperations;", token).ConfigureAwait(false);
        var terminalBatches = await ScalarLongAsync(connection, transaction, """
            SELECT COUNT(*) FROM temp.AccountRemovalManifestTerminalBatches;
            """, token).ConfigureAwait(false);
        var sharedBatches = await ScalarLongAsync(connection, transaction, """
            SELECT COUNT(*) FROM temp.AccountRemovalManifestTerminalBatches terminal
            WHERE NOT EXISTS(SELECT 1 FROM temp.AccountRemovalManifestBOnlyBatches onlyB WHERE onlyB.Id=terminal.Id);
            """, token).ConfigureAwait(false);
        return new Manifest(writer.Finish(), checked((int)terminalOperations), checked((int)terminalBatches), checked((int)sharedBatches));
    }

    private static async Task MaterializeManifestClosureAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid accountId,
        bool includeStagedRows,
        CancellationToken token)
    {
        await ExecuteChangeAsync(connection, transaction, $"""
            CREATE TEMP TABLE AccountRemovalManifestTargetBatches(Id TEXT PRIMARY KEY) WITHOUT ROWID;
            CREATE TEMP TABLE AccountRemovalManifestBatches(Id TEXT PRIMARY KEY) WITHOUT ROWID;
            CREATE TEMP TABLE AccountRemovalManifestImports(Id TEXT PRIMARY KEY) WITHOUT ROWID;
            CREATE TEMP TABLE AccountRemovalManifestResources(Id TEXT PRIMARY KEY) WITHOUT ROWID;
            CREATE TEMP TABLE AccountRemovalManifestStagedRows(Id TEXT PRIMARY KEY) WITHOUT ROWID;
            CREATE TEMP TABLE AccountRemovalManifestTargetOperations(Id TEXT PRIMARY KEY) WITHOUT ROWID;
            CREATE TEMP TABLE AccountRemovalManifestTerminalBatches(Id TEXT PRIMARY KEY) WITHOUT ROWID;
            CREATE TEMP TABLE AccountRemovalManifestBOnlyBatches(Id TEXT PRIMARY KEY) WITHOUT ROWID;
            CREATE TEMP TABLE AccountRemovalManifestTargetResources(Id TEXT PRIMARY KEY) WITHOUT ROWID;
            CREATE TEMP TABLE AccountRemovalManifestLogs(Id TEXT PRIMARY KEY) WITHOUT ROWID;
            CREATE TEMP TABLE AccountRemovalManifestTargetLogs(Id TEXT PRIMARY KEY) WITHOUT ROWID;
            CREATE TEMP TABLE AccountRemovalManifestCleanupImports(Id TEXT PRIMARY KEY) WITHOUT ROWID;
            CREATE TEMP TABLE AccountRemovalAllocationCounters(
              BatchJobId TEXT NOT NULL,
              AccountId TEXT NOT NULL,
              AssignedItemCount INTEGER NOT NULL,
              AttemptedItemCount INTEGER NOT NULL,
              NewSuccessItemCount INTEGER NOT NULL,
              AlreadySatisfiedItemCount INTEGER NOT NULL,
              SuccessItemCount INTEGER NOT NULL,
              FailedItemCount INTEGER NOT NULL,
              LockedItemCount INTEGER NOT NULL,
              ExclusiveCandidateCount INTEGER NOT NULL,
              UnusedCandidateCount INTEGER NOT NULL,
              ReservedSlotCount INTEGER NOT NULL,
              FulfilledCount INTEGER NOT NULL,
              PRIMARY KEY(BatchJobId,AccountId)) WITHOUT ROWID;
            """, token).ConfigureAwait(false);
        await ExecuteChangeAsync(connection, transaction, """
            INSERT OR IGNORE INTO temp.AccountRemovalManifestTargetBatches(Id)
            SELECT BatchJobId FROM TransferOperations
             WHERE AssignedAccountId=$account AND Status IN ('Succeeded','Failed','Cancelled')
            UNION SELECT BatchJobId FROM AccountAllocations WHERE AccountId=$account
            UNION SELECT BatchJobId FROM ResourceItems
             WHERE CandidateConstraintAccountId=$account OR ReservedAccountId=$account;
            """, token, ("$account", Id(accountId))).ConfigureAwait(false);
        await ExecuteChangeAsync(connection, transaction, """
            INSERT OR IGNORE INTO temp.AccountRemovalManifestBatches(Id)
            SELECT Id FROM temp.AccountRemovalManifestTargetBatches;
            """, token).ConfigureAwait(false);
        await ExecuteChangeAsync(connection, transaction, """
            INSERT OR IGNORE INTO temp.AccountRemovalManifestImports(Id)
            SELECT Id FROM ImportSessions WHERE PrecheckAccountId=$account;
            """, token, ("$account", Id(accountId))).ConfigureAwait(false);

        while (true)
        {
            var changed = 0;
            changed += await ExecuteChangeAsync(connection, transaction, """
                INSERT OR IGNORE INTO temp.AccountRemovalManifestResources(Id)
                SELECT resource.Id FROM ResourceItems resource
                JOIN temp.AccountRemovalManifestBatches batch ON batch.Id=resource.BatchJobId;
                """, token).ConfigureAwait(false);
            if (includeStagedRows)
            {
                changed += await ExecuteChangeAsync(connection, transaction, """
                    INSERT OR IGNORE INTO temp.AccountRemovalManifestStagedRows(Id)
                    SELECT staged.Id FROM StagedImportRows staged
                    JOIN temp.AccountRemovalManifestImports import ON import.Id=staged.ImportSessionId;
                    """, token).ConfigureAwait(false);
                changed += await ExecuteChangeAsync(connection, transaction, """
                    INSERT OR IGNORE INTO temp.AccountRemovalManifestStagedRows(Id)
                    SELECT resource.StagedImportRowId FROM ResourceItems resource
                    JOIN temp.AccountRemovalManifestResources closure ON closure.Id=resource.Id
                    WHERE resource.StagedImportRowId IS NOT NULL;
                    """, token).ConfigureAwait(false);
                changed += await ExecuteChangeAsync(connection, transaction, """
                    INSERT OR IGNORE INTO temp.AccountRemovalManifestStagedRows(Id)
                    SELECT staged.DuplicateOfStagedRowId FROM StagedImportRows staged
                    JOIN temp.AccountRemovalManifestStagedRows closure ON closure.Id=staged.Id
                    WHERE staged.DuplicateOfStagedRowId IS NOT NULL;
                    """, token).ConfigureAwait(false);
                changed += await ExecuteChangeAsync(connection, transaction, """
                    INSERT OR IGNORE INTO temp.AccountRemovalManifestStagedRows(Id)
                    SELECT staged.Id FROM StagedImportRows staged
                    JOIN temp.AccountRemovalManifestStagedRows closure ON closure.Id=staged.DuplicateOfStagedRowId;
                    """, token).ConfigureAwait(false);
                changed += await ExecuteChangeAsync(connection, transaction, """
                    INSERT OR IGNORE INTO temp.AccountRemovalManifestImports(Id)
                    SELECT staged.ImportSessionId FROM StagedImportRows staged
                    JOIN temp.AccountRemovalManifestStagedRows closure ON closure.Id=staged.Id;
                    """, token).ConfigureAwait(false);
                changed += await ExecuteChangeAsync(connection, transaction, """
                    INSERT OR IGNORE INTO temp.AccountRemovalManifestResources(Id)
                    SELECT resource.Id FROM ResourceItems resource
                    JOIN temp.AccountRemovalManifestStagedRows closure ON closure.Id=resource.StagedImportRowId;
                    """, token).ConfigureAwait(false);
            }
            changed += await ExecuteChangeAsync(connection, transaction, """
                INSERT OR IGNORE INTO temp.AccountRemovalManifestResources(Id)
                SELECT operation.ResourceItemId FROM TransferOperations operation
                JOIN temp.AccountRemovalManifestBatches batch ON batch.Id=operation.BatchJobId;
                """, token).ConfigureAwait(false);
            changed += await ExecuteChangeAsync(connection, transaction, """
                INSERT OR IGNORE INTO temp.AccountRemovalManifestBatches(Id)
                SELECT operation.BatchJobId FROM TransferOperations operation
                JOIN temp.AccountRemovalManifestResources resource ON resource.Id=operation.ResourceItemId;
                """, token).ConfigureAwait(false);
            changed += await ExecuteChangeAsync(connection, transaction, """
                INSERT OR IGNORE INTO temp.AccountRemovalManifestBatches(Id)
                SELECT resource.BatchJobId FROM ResourceItems resource
                JOIN temp.AccountRemovalManifestResources closure ON closure.Id=resource.Id;
                """, token).ConfigureAwait(false);
            changed += await ExecuteChangeAsync(connection, transaction, """
                INSERT OR IGNORE INTO temp.AccountRemovalManifestImports(Id)
                SELECT batch.ImportSessionId FROM BatchJobs batch
                JOIN temp.AccountRemovalManifestBatches closure ON closure.Id=batch.Id
                WHERE batch.ImportSessionId IS NOT NULL;
                """, token).ConfigureAwait(false);
            changed += await ExecuteChangeAsync(connection, transaction, """
                INSERT OR IGNORE INTO temp.AccountRemovalManifestImports(Id)
                SELECT import.Id FROM temp.AccountRemovalManifestBatches batch
                JOIN ImportSessions import ON import.CreatedBatchJobId=batch.Id;
                """, token).ConfigureAwait(false);
            changed += await ExecuteChangeAsync(connection, transaction, """
                INSERT OR IGNORE INTO temp.AccountRemovalManifestImports(Id)
                SELECT import.Id FROM temp.AccountRemovalManifestBatches batch
                JOIN ImportSessions import ON import.AppendToBatchJobId=batch.Id;
                """, token).ConfigureAwait(false);
            changed += await ExecuteChangeAsync(connection, transaction, """
                INSERT OR IGNORE INTO temp.AccountRemovalManifestBatches(Id)
                SELECT batch.Id FROM BatchJobs batch
                JOIN temp.AccountRemovalManifestImports import ON import.Id=batch.ImportSessionId;
                """, token).ConfigureAwait(false);
            changed += await ExecuteChangeAsync(connection, transaction, """
                INSERT OR IGNORE INTO temp.AccountRemovalManifestBatches(Id)
                SELECT import.CreatedBatchJobId FROM ImportSessions import
                JOIN temp.AccountRemovalManifestImports closure ON closure.Id=import.Id
                WHERE import.CreatedBatchJobId IS NOT NULL;
                """, token).ConfigureAwait(false);
            changed += await ExecuteChangeAsync(connection, transaction, """
                INSERT OR IGNORE INTO temp.AccountRemovalManifestBatches(Id)
                SELECT import.AppendToBatchJobId FROM ImportSessions import
                JOIN temp.AccountRemovalManifestImports closure ON closure.Id=import.Id
                WHERE import.AppendToBatchJobId IS NOT NULL;
                """, token).ConfigureAwait(false);
            if (changed == 0) break;
        }

        await ExecuteChangeAsync(connection, transaction, $"""
            INSERT OR IGNORE INTO temp.AccountRemovalManifestTerminalBatches(Id)
            SELECT batch.Id FROM BatchJobs batch
            JOIN temp.AccountRemovalManifestTargetBatches target ON target.Id=batch.Id
            WHERE batch.Status IN ('Completed','CompletedWithShortfall','Cancelled');

            INSERT OR IGNORE INTO temp.AccountRemovalManifestTargetOperations(Id)
            SELECT operation.Id FROM TransferOperations operation
            JOIN temp.AccountRemovalManifestTerminalBatches terminal ON terminal.Id=operation.BatchJobId
            WHERE operation.AssignedAccountId=$account
              AND {SafeTerminalOperationPredicate};

            INSERT OR IGNORE INTO temp.AccountRemovalManifestTargetResources(Id)
            SELECT resource.Id FROM ResourceItems resource
            JOIN temp.AccountRemovalManifestTerminalBatches terminal ON terminal.Id=resource.BatchJobId
            WHERE (resource.CandidateConstraintAccountId=$account
                   OR EXISTS(SELECT 1 FROM temp.AccountRemovalManifestTargetOperations target
                             JOIN TransferOperations operation ON operation.Id=target.Id
                             WHERE operation.ResourceItemId=resource.Id))
              AND NOT EXISTS(
                    SELECT 1 FROM TransferOperations survivor
                    WHERE survivor.ResourceItemId=resource.Id
                      AND NOT EXISTS(SELECT 1 FROM temp.AccountRemovalManifestTargetOperations target
                                     WHERE target.Id=survivor.Id));

            INSERT OR IGNORE INTO temp.AccountRemovalManifestLogs(Id)
            SELECT log.Id FROM ExecutionLogs log WHERE log.AccountId=$account
            UNION SELECT log.Id FROM ExecutionLogs log
              JOIN temp.AccountRemovalManifestBatches batch ON batch.Id=log.BatchJobId
            UNION SELECT log.Id FROM ExecutionLogs log
              JOIN temp.AccountRemovalManifestResources resource ON resource.Id=log.ResourceItemId
            UNION SELECT log.Id FROM ExecutionLogs log
              JOIN TransferOperations operation ON operation.Id=log.TransferOperationId
              JOIN temp.AccountRemovalManifestBatches batch ON batch.Id=operation.BatchJobId;

            INSERT OR IGNORE INTO temp.AccountRemovalManifestBOnlyBatches(Id)
            SELECT terminal.Id FROM temp.AccountRemovalManifestTerminalBatches terminal
            WHERE NOT EXISTS(
                    SELECT 1 FROM TransferOperations operation
                    WHERE operation.BatchJobId=terminal.Id AND operation.AssignedAccountId<>$account)
              AND NOT EXISTS(
                    SELECT 1 FROM AccountAllocations allocation
                    WHERE allocation.BatchJobId=terminal.Id AND allocation.AccountId<>$account)
              AND NOT EXISTS(
                    SELECT 1 FROM ResourceItems resource
                    WHERE resource.BatchJobId=terminal.Id
                      AND NOT EXISTS(SELECT 1 FROM temp.AccountRemovalManifestTargetResources target
                                     WHERE target.Id=resource.Id))
              AND NOT EXISTS(
                    SELECT 1 FROM ExecutionLogs log
                    JOIN temp.AccountRemovalManifestLogs manifested ON manifested.Id=log.Id
                    LEFT JOIN TransferOperations operation ON operation.Id=log.TransferOperationId
                    LEFT JOIN ResourceItems resource ON resource.Id=log.ResourceItemId
                    WHERE log.AccountId IS NOT NULL AND log.AccountId<>$account
                      AND (log.BatchJobId=terminal.Id OR operation.BatchJobId=terminal.Id OR resource.BatchJobId=terminal.Id));

            INSERT OR IGNORE INTO temp.AccountRemovalManifestCleanupImports(Id)
            SELECT batch.ImportSessionId FROM BatchJobs batch
            JOIN temp.AccountRemovalManifestBOnlyBatches onlyB ON onlyB.Id=batch.Id
            WHERE batch.ImportSessionId IS NOT NULL
            UNION SELECT import.Id FROM ImportSessions import
            WHERE import.CreatedBatchJobId IN (SELECT Id FROM temp.AccountRemovalManifestBOnlyBatches)
               OR import.AppendToBatchJobId IN (SELECT Id FROM temp.AccountRemovalManifestBOnlyBatches);

            INSERT OR IGNORE INTO temp.AccountRemovalManifestTargetLogs(Id)
            SELECT log.Id FROM ExecutionLogs log
              JOIN temp.AccountRemovalManifestLogs manifested ON manifested.Id=log.Id
              WHERE log.AccountId=$account
            UNION SELECT log.Id FROM ExecutionLogs log
              JOIN temp.AccountRemovalManifestLogs manifested ON manifested.Id=log.Id
              JOIN temp.AccountRemovalManifestTargetOperations target ON target.Id=log.TransferOperationId
              WHERE log.AccountId IS NULL
            UNION SELECT log.Id FROM ExecutionLogs log
              JOIN temp.AccountRemovalManifestLogs manifested ON manifested.Id=log.Id
              JOIN temp.AccountRemovalManifestTargetResources target ON target.Id=log.ResourceItemId
              WHERE log.AccountId IS NULL
            UNION SELECT log.Id FROM ExecutionLogs log
              JOIN temp.AccountRemovalManifestLogs manifested ON manifested.Id=log.Id
              JOIN temp.AccountRemovalManifestBOnlyBatches onlyB ON onlyB.Id=log.BatchJobId
              WHERE log.AccountId IS NULL;
            """, token, ("$account", Id(accountId))).ConfigureAwait(false);
    }

    private static async Task DeleteTerminalHistoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid accountId,
        CancellationToken token)
    {
        var account = Id(accountId);

        await ExecuteChangeAsync(connection, transaction, """
            DELETE FROM ExecutionLogs
            WHERE Id IN (SELECT Id FROM temp.AccountRemovalManifestTargetLogs);

            UPDATE ExecutionLogs
            SET TransferOperationId=CASE
                    WHEN TransferOperationId IN (SELECT Id FROM temp.AccountRemovalManifestTargetOperations) THEN NULL
                    ELSE TransferOperationId END,
                ResourceItemId=CASE
                    WHEN ResourceItemId IN (SELECT Id FROM temp.AccountRemovalManifestTargetResources) THEN NULL
                    ELSE ResourceItemId END
            WHERE Id IN (SELECT Id FROM temp.AccountRemovalManifestLogs)
              AND Id NOT IN (SELECT Id FROM temp.AccountRemovalManifestTargetLogs)
              AND (TransferOperationId IN (SELECT Id FROM temp.AccountRemovalManifestTargetOperations)
                OR ResourceItemId IN (SELECT Id FROM temp.AccountRemovalManifestTargetResources));
            """, token).ConfigureAwait(false);

        await ExecuteChangeAsync(connection, transaction, """
            DELETE FROM TransferOperations
            WHERE Id IN (SELECT Id FROM temp.AccountRemovalManifestTargetOperations);
            """, token).ConfigureAwait(false);

        await ExecuteChangeAsync(connection, transaction, """
            DELETE FROM AccountAllocations
            WHERE AccountId=$account
              AND BatchJobId IN (SELECT Id FROM temp.AccountRemovalManifestTerminalBatches);
            """, token, ("$account", account)).ConfigureAwait(false);

        await ExecuteChangeAsync(connection, transaction, """
            DELETE FROM ResourceItems
            WHERE Id IN (SELECT Id FROM temp.AccountRemovalManifestTargetResources)
              AND NOT EXISTS(SELECT 1 FROM TransferOperations operation
                             WHERE operation.ResourceItemId=ResourceItems.Id);
            """, token).ConfigureAwait(false);

        await ExecuteChangeAsync(connection, transaction, """
            UPDATE ImportSessions
            SET CreatedBatchJobId=NULL
            WHERE Id IN (SELECT Id FROM temp.AccountRemovalManifestCleanupImports)
              AND CreatedBatchJobId IN (SELECT Id FROM temp.AccountRemovalManifestBOnlyBatches);

            UPDATE ImportSessions
            SET AppendToBatchJobId=NULL
            WHERE Id IN (SELECT Id FROM temp.AccountRemovalManifestCleanupImports)
              AND AppendToBatchJobId IN (SELECT Id FROM temp.AccountRemovalManifestBOnlyBatches);

            UPDATE BatchJobs
            SET ImportSessionId=NULL
            WHERE Id IN (SELECT Id FROM temp.AccountRemovalManifestBOnlyBatches);
            """, token).ConfigureAwait(false);

        await ExecuteChangeAsync(connection, transaction, """
            DELETE FROM BatchJobs
            WHERE Id IN (SELECT Id FROM temp.AccountRemovalManifestBOnlyBatches);
            """, token).ConfigureAwait(false);

        await ExecuteChangeAsync(connection, transaction, """
            DELETE FROM ImportSessions
            WHERE Id IN (SELECT Id FROM temp.AccountRemovalManifestCleanupImports)
              AND Status IN ('BatchCreated','Cancelled')
              AND CreatedBatchJobId IS NULL
              AND AppendToBatchJobId IS NULL
              AND NOT EXISTS(SELECT 1 FROM BatchJobs batch WHERE batch.ImportSessionId=ImportSessions.Id)
              AND NOT EXISTS(
                    SELECT 1 FROM ResourceItems resource
                    JOIN StagedImportRows staged ON staged.Id=resource.StagedImportRowId
                    WHERE staged.ImportSessionId=ImportSessions.Id)
              AND NOT EXISTS(
                    SELECT 1 FROM StagedImportRows staged
                    WHERE staged.ImportSessionId=ImportSessions.Id
                      AND staged.Id NOT IN (SELECT Id FROM temp.AccountRemovalManifestStagedRows))
              AND NOT EXISTS(
                    SELECT 1 FROM StagedImportRows child
                    JOIN StagedImportRows parent ON parent.Id=child.DuplicateOfStagedRowId
                    WHERE parent.ImportSessionId=ImportSessions.Id
                      AND child.ImportSessionId<>ImportSessions.Id);
            """, token).ConfigureAwait(false);

        await ExecuteChangeAsync(connection, transaction, """
            UPDATE BatchJobs
            SET ResourceCount=(SELECT COUNT(*) FROM ResourceItems resource WHERE resource.BatchJobId=BatchJobs.Id),
                OperationCount=(SELECT COUNT(*) FROM TransferOperations operation WHERE operation.BatchJobId=BatchJobs.Id)
            WHERE Id IN (SELECT terminal.Id FROM temp.AccountRemovalManifestTerminalBatches terminal
                         WHERE NOT EXISTS(SELECT 1 FROM temp.AccountRemovalManifestBOnlyBatches onlyB
                                          WHERE onlyB.Id=terminal.Id));
            """, token).ConfigureAwait(false);

        await ExecuteChangeAsync(connection, transaction, """
            INSERT INTO temp.AccountRemovalAllocationCounters(
              BatchJobId,AccountId,AssignedItemCount,AttemptedItemCount,NewSuccessItemCount,
              AlreadySatisfiedItemCount,SuccessItemCount,FailedItemCount,LockedItemCount,
              ExclusiveCandidateCount,UnusedCandidateCount,ReservedSlotCount,FulfilledCount)
            SELECT allocation.BatchJobId,allocation.AccountId,
              COALESCE(operation.AssignedItemCount,0),COALESCE(operation.AttemptedItemCount,0),
              COALESCE(operation.NewSuccessItemCount,0),COALESCE(operation.AlreadySatisfiedItemCount,0),
              COALESCE(operation.SuccessItemCount,0),COALESCE(operation.FailedItemCount,0),
              COALESCE(operation.LockedItemCount,0),COALESCE(resource.ExclusiveCandidateCount,0),
              COALESCE(resource.UnusedCandidateCount,0),COALESCE(operation.ReservedSlotCount,0),
              COALESCE(operation.FulfilledCount,0)
            FROM AccountAllocations allocation
            LEFT JOIN (
              SELECT operation.BatchJobId,operation.AssignedAccountId AS AccountId,
                COUNT(*) AS AssignedItemCount,
                SUM(CASE WHEN operation.DailyCountedAt IS NOT NULL THEN 1 ELSE 0 END) AS AttemptedItemCount,
                SUM(CASE WHEN operation.Status='Succeeded' AND operation.OutcomeKind='NewTransfer' THEN 1 ELSE 0 END) AS NewSuccessItemCount,
                SUM(CASE WHEN operation.Status='Succeeded' AND operation.OutcomeKind='AlreadySatisfied' THEN 1 ELSE 0 END) AS AlreadySatisfiedItemCount,
                SUM(CASE WHEN operation.Status='Succeeded' THEN 1 ELSE 0 END) AS SuccessItemCount,
                SUM(CASE WHEN operation.Status='Failed' THEN 1 ELSE 0 END) AS FailedItemCount,
                SUM(CASE WHEN operation.AllocationSource='Locked' THEN 1 ELSE 0 END) AS LockedItemCount,
                SUM(CASE WHEN operation.TargetSlotState='Reserved' THEN 1 ELSE 0 END) AS ReservedSlotCount,
                SUM(CASE WHEN operation.TargetSlotState='Fulfilled' THEN 1 ELSE 0 END) AS FulfilledCount
              FROM TransferOperations operation
              JOIN temp.AccountRemovalManifestTerminalBatches terminal ON terminal.Id=operation.BatchJobId
              WHERE NOT EXISTS(SELECT 1 FROM temp.AccountRemovalManifestBOnlyBatches onlyB
                               WHERE onlyB.Id=terminal.Id)
              GROUP BY operation.BatchJobId,operation.AssignedAccountId
            ) operation ON operation.BatchJobId=allocation.BatchJobId AND operation.AccountId=allocation.AccountId
            LEFT JOIN (
              SELECT resource.BatchJobId,resource.CandidateConstraintAccountId AS AccountId,
                COUNT(*) AS ExclusiveCandidateCount,
                SUM(CASE WHEN resource.CandidateStatus='Unused' THEN 1 ELSE 0 END) AS UnusedCandidateCount
              FROM ResourceItems resource
              JOIN temp.AccountRemovalManifestTerminalBatches terminal ON terminal.Id=resource.BatchJobId
              WHERE resource.CandidateConstraintAccountId IS NOT NULL
                AND NOT EXISTS(SELECT 1 FROM temp.AccountRemovalManifestBOnlyBatches onlyB
                               WHERE onlyB.Id=terminal.Id)
              GROUP BY resource.BatchJobId,resource.CandidateConstraintAccountId
            ) resource ON resource.BatchJobId=allocation.BatchJobId AND resource.AccountId=allocation.AccountId
            JOIN temp.AccountRemovalManifestTerminalBatches terminal ON terminal.Id=allocation.BatchJobId
            WHERE NOT EXISTS(SELECT 1 FROM temp.AccountRemovalManifestBOnlyBatches onlyB
                             WHERE onlyB.Id=terminal.Id);

            UPDATE AccountAllocations
            SET (AssignedItemCount,AttemptedItemCount,NewSuccessItemCount,AlreadySatisfiedItemCount,
                 SuccessItemCount,FailedItemCount,LockedItemCount,ExclusiveCandidateCount,
                 UnusedCandidateCount,ReservedSlotCount,FulfilledCount) =
                (SELECT counters.AssignedItemCount,counters.AttemptedItemCount,counters.NewSuccessItemCount,
                        counters.AlreadySatisfiedItemCount,counters.SuccessItemCount,counters.FailedItemCount,
                        counters.LockedItemCount,counters.ExclusiveCandidateCount,counters.UnusedCandidateCount,
                        counters.ReservedSlotCount,counters.FulfilledCount
                 FROM temp.AccountRemovalAllocationCounters counters
                 WHERE counters.BatchJobId=AccountAllocations.BatchJobId
                   AND counters.AccountId=AccountAllocations.AccountId)
            WHERE EXISTS(
              SELECT 1 FROM temp.AccountRemovalAllocationCounters counters
              WHERE counters.BatchJobId=AccountAllocations.BatchJobId
                AND counters.AccountId=AccountAllocations.AccountId);
            """, token).ConfigureAwait(false);
    }

    private static async Task AppendQueryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CanonicalWriter writer,
        string table,
        string sql,
        CancellationToken token,
        IReadOnlyCollection<string> timestamps,
        IReadOnlyCollection<string> secrets,
        params (string Name, object? Value)[] parameters)
    {
        writer.Value("table", table);
        await using var command = Command(connection, transaction, sql);
        foreach (var parameter in parameters) Add(command, parameter.Name, parameter.Value);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var row = 0;
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            writer.Value("row", (++row).ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                if (!reader.IsDBNull(i) && reader.GetValue(i) is byte[] bytes)
                {
                    writer.Bytes(name, bytes);
                    continue;
                }
                string? value = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                if (value is not null && timestamps.Contains(name)) value = CanonicalTime(value);
                if (value is not null && secrets.Contains(name)) value = Fingerprint(value);
                writer.Value(name, value);
            }
        }
        writer.Value("rows", row.ToString(CultureInfo.InvariantCulture));
    }

    private static string CanonicalTime(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : "invalid:" + value;

    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static async Task<int> ExecuteChangeAsync(
        SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken token,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql);
        foreach (var parameter in parameters) Add(command, parameter.Name, parameter.Value);
        return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task EnsureForeignKeysAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = Command(connection, transaction, "PRAGMA foreign_key_check;");
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidDataException("Database relationship validation failed.");
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken token,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql);
        foreach (var parameter in parameters) Add(command, parameter.Name, parameter.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static byte[]? ReadBlob(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        return reader.GetValue(ordinal) is byte[] bytes
            ? (byte[])bytes.Clone()
            : throw new InvalidDataException("Encrypted account session has an invalid storage type.");
    }

    private static string Id(Guid id) => id.ToString("D");
    private static string Time(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private sealed record AccountSnapshot(Guid Id, string BaiduUserId, string DisplayName, byte[]? EncryptedSession,
        string? ProfilePath, string Status, bool IsEnabled, string? LastValidatedAt, bool IsDeleted);
    private sealed record Manifest(byte[] Digest, int TerminalOperations, int TerminalBatches, int SharedBatches);

    private sealed class CanonicalWriter : IDisposable
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private bool finished;

        public void Value(string name, string? value)
        {
            Append(name);
            if (value is null) hash.AppendData([0]);
            else
            {
                hash.AppendData([1]);
                Append(value);
            }
        }

        public void Bytes(string name, ReadOnlySpan<byte> value)
        {
            Append(name);
            hash.AppendData([2]);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
            hash.AppendData(length);
            hash.AppendData(value);
        }

        private void Append(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        public byte[] Finish()
        {
            if (finished) throw new InvalidOperationException("Canonical manifest was already finalized.");
            finished = true;
            return hash.GetHashAndReset();
        }

        public void Dispose() => hash.Dispose();
    }
}
