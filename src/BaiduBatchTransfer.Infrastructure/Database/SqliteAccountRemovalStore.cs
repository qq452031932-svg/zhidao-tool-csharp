using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BaiduBatchTransfer.Application.Accounts;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed class SqliteAccountRemovalStore : IAccountRemovalStore
{
    private const string ReadyMessage = "账号可以安全退出。";
    private const string BlockedMessage = "账号仍有未完成工作，请先完成、取消或重新分配。";
    private const string NotFoundMessage = "账号不存在。";
    private const string RemovedMessage = "账号已经退出。";
    private const string StaleMessage = "确认信息已过期，请重新确认。";
    private const string HistoryUnavailableMessage = "当前版本暂不支持清理历史记录。";
    private readonly SqliteConnectionFactory factory;
    private readonly Func<SqliteConnection, SqliteTransaction, CancellationToken, Task>? afterFinalValidation;
    private readonly Func<SqliteConnection, SqliteTransaction, CancellationToken, Task>? afterPreviewSnapshot;

    public SqliteAccountRemovalStore(SqliteConnectionFactory factory) : this(factory, null, null) { }

    internal SqliteAccountRemovalStore(
        SqliteConnectionFactory factory,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task>? afterFinalValidation) :
        this(factory, afterFinalValidation, null) { }

    internal SqliteAccountRemovalStore(
        SqliteConnectionFactory factory,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task>? afterFinalValidation,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task>? afterPreviewSnapshot)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.afterFinalValidation = afterFinalValidation;
        this.afterPreviewSnapshot = afterPreviewSnapshot;
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
        if (await HasActiveWorkAsync(connection, transaction, accountId, token).ConfigureAwait(false))
        {
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return Preview(AccountExitStatus.ActiveWorkBlocked, account.DisplayName, BlockedMessage);
        }
        var manifest = await BuildManifestAsync(connection, transaction, account, mode, token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return new AccountExitPreview(AccountExitStatus.Ready, account.DisplayName, manifest.TerminalOperations,
            manifest.TerminalBatches, manifest.SharedBatches, Convert.ToHexString(manifest.Digest), ReadyMessage);
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
        if (await HasActiveWorkAsync(connection, transaction, request.AccountId, token).ConfigureAwait(false))
            return await RollbackResultAsync(transaction, AccountRemovalPreparationStatus.ActiveWorkBlocked, request.AccountId, BlockedMessage).ConfigureAwait(false);

        var manifest = await BuildManifestAsync(connection, transaction, account, request.HistoryMode, token).ConfigureAwait(false);
        if (!TokenEquals(manifest.Digest, request.ConfirmationToken))
            return await RollbackResultAsync(transaction, AccountRemovalPreparationStatus.StaleConfirmation, request.AccountId, StaleMessage).ConfigureAwait(false);
        if (request.HistoryMode == AccountExitHistoryMode.RemoveTerminalHistory)
            return await RollbackResultAsync(transaction, AccountRemovalPreparationStatus.Failed, request.AccountId, HistoryUnavailableMessage).ConfigureAwait(false);

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

        await EnsureForeignKeysAsync(connection, transaction, token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return new AccountRemovalPreparation(AccountRemovalPreparationStatus.Prepared, request.AccountId,
            account.ProfilePath, true, "账号已安全退出。");
    }

    public async Task FinalizeTombstoneAsync(Guid accountId, DateTimeOffset removedAt, CancellationToken token)
    {
        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = Command(connection, transaction, """
            UPDATE Accounts
            SET Status='Deleted',IsDeleted=1,DeletedAt=$removed,WebView2ProfileDirectory=NULL
            WHERE Id=$id AND Status='Disabled' AND IsEnabled=0 AND EncryptedSession IS NULL AND IsDeleted=0;
            """);
        Add(command, "$removed", Time(removedAt));
        Add(command, "$id", Id(accountId));
        var changed = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        if (changed != 1)
        {
            var alreadyDeleted = await ScalarLongAsync(connection, transaction,
                "SELECT COUNT(*) FROM Accounts WHERE Id=$id AND Status='Deleted' AND IsDeleted=1;",
                token, ("$id", Id(accountId))).ConfigureAwait(false) == 1;
            if (!alreadyDeleted)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException("Account tombstone preconditions were not satisfied.");
            }
        }
        await EnsureForeignKeysAsync(connection, transaction, token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
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
        return new AccountRemovalPreparation(status, accountId, null, false, message);
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

    private static async Task<bool> HasActiveWorkAsync(SqliteConnection connection, SqliteTransaction transaction, Guid accountId, CancellationToken token)
    {
        var count = await ScalarLongAsync(connection, transaction, """
            SELECT
              (SELECT COUNT(*) FROM TransferOperations WHERE AssignedAccountId=$account
                 AND Status IN ('Waiting','Reserved','Preparing','Submitting','ResultPending'))
              +
              (SELECT COUNT(*) FROM AccountAllocations allocation
                 JOIN BatchJobs batch ON batch.Id=allocation.BatchJobId
                WHERE allocation.AccountId=$account
                  AND batch.Status IN ('Queued','Active','Paused','AwaitingCandidates','PausedReady'));
            """, token, ("$account", Id(accountId))).ConfigureAwait(false);
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
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(6), reader.GetInt64(12) != 0, reader.GetInt64(15) != 0);
    }

    private static async Task<Manifest> BuildManifestAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AccountSnapshot account,
        AccountExitHistoryMode mode,
        CancellationToken token)
    {
        await MaterializeManifestClosureAsync(connection, transaction, account.Id, token).ConfigureAwait(false);
        using var writer = new CanonicalWriter();
        writer.Value("manifest-version", "1");
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

        var terminalOperations = await ScalarLongAsync(connection, transaction,
            "SELECT COUNT(*) FROM TransferOperations WHERE AssignedAccountId=$account AND Status IN ('Succeeded','Failed','Cancelled');",
            token, ("$account", Id(account.Id))).ConfigureAwait(false);
        var terminalBatches = await ScalarLongAsync(connection, transaction, """
            SELECT COUNT(*) FROM BatchJobs batch JOIN temp.AccountRemovalManifestTargetBatches target ON target.Id=batch.Id
            WHERE batch.Status IN ('Completed','CompletedWithShortfall','Cancelled');
            """, token).ConfigureAwait(false);
        var sharedBatches = await ScalarLongAsync(connection, transaction, """
            SELECT COUNT(*) FROM BatchJobs batch JOIN temp.AccountRemovalManifestTargetBatches target ON target.Id=batch.Id
            WHERE batch.Status IN ('Completed','CompletedWithShortfall','Cancelled')
              AND (EXISTS(SELECT 1 FROM AccountAllocations a WHERE a.BatchJobId=batch.Id AND a.AccountId<>$account)
                   OR EXISTS(SELECT 1 FROM TransferOperations o WHERE o.BatchJobId=batch.Id AND o.AssignedAccountId<>$account));
            """, token, ("$account", Id(account.Id))).ConfigureAwait(false);
        return new Manifest(writer.Finish(), checked((int)terminalOperations), checked((int)terminalBatches), checked((int)sharedBatches));
    }

    private static async Task MaterializeManifestClosureAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid accountId, CancellationToken token)
    {
        await ExecuteChangeAsync(connection, transaction, """
            CREATE TEMP TABLE AccountRemovalManifestTargetBatches(Id TEXT PRIMARY KEY) WITHOUT ROWID;
            CREATE TEMP TABLE AccountRemovalManifestBatches(Id TEXT PRIMARY KEY) WITHOUT ROWID;
            CREATE TEMP TABLE AccountRemovalManifestImports(Id TEXT PRIMARY KEY) WITHOUT ROWID;
            CREATE TEMP TABLE AccountRemovalManifestResources(Id TEXT PRIMARY KEY) WITHOUT ROWID;
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
        string? ProfilePath, string Status, bool IsEnabled, bool IsDeleted);
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
