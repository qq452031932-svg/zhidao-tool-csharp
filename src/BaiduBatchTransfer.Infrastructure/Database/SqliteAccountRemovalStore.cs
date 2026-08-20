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

    public SqliteAccountRemovalStore(SqliteConnectionFactory factory) : this(factory, null) { }

    internal SqliteAccountRemovalStore(
        SqliteConnectionFactory factory,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task>? afterFinalValidation)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.afterFinalValidation = afterFinalValidation;
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
        await using var transaction = connection.BeginTransaction();
        var account = await ReadAccountAsync(connection, transaction, accountId, token).ConfigureAwait(false);
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
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
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
        if (!string.IsNullOrEmpty(suppliedToken))
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
        return new AccountSnapshot(accountId, reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(6), reader.GetInt64(12) != 0, reader.GetInt64(15) != 0);
    }

    private static async Task<Manifest> BuildManifestAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AccountSnapshot account,
        AccountExitHistoryMode mode,
        CancellationToken token)
    {
        var batchIds = await ReadRelevantBatchIdsAsync(connection, transaction, account.Id, token).ConfigureAwait(false);
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

        var inClause = InClause(batchIds.Count);
        if (batchIds.Count != 0)
        {
            await AppendQueryAsync(connection, transaction, writer, "BatchJobs", $"""
                SELECT Id,Name,SourceType,TransferMode,QuotaBasis,ShortagePolicy,ResourceCount,OperationCount,DefaultTargetPath,
                  FolderCreationMode,DuplicatePolicy,ValidationMode,Status,CreatedAt,StartedAt,CompletedAt,ImportSessionId,QueueOrder,NextAllocationOrdinal
                FROM BatchJobs WHERE Id IN ({inClause}) ORDER BY Id;
                """, token, ["CreatedAt", "StartedAt", "CompletedAt"], [], BatchParameters(batchIds)).ConfigureAwait(false);
            await AppendQueryAsync(connection, transaction, writer, "TransferOperations", $"""
                SELECT Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,NormalizedTargetPath,CreateFolderName,
                  AssignedAccountId,AllocationSource,Status,OutcomeKind,AttemptCount,OperationFingerprint,ConfirmationDescriptorJson,
                  AdapterRequestId,RemoteItemIdsJson,SubmitState,TargetSlotState,TargetSlotReservedAt,DailyCountedAt,SubmitStartedAt,
                  LastErrorCode,LastErrorMessage,LeaseExpiresAt,CreatedAt,UpdatedAt
                FROM TransferOperations WHERE BatchJobId IN ({inClause}) ORDER BY BatchJobId,AllocationOrdinal,SequenceNumber,Id;
                """, token, ["TargetSlotReservedAt", "DailyCountedAt", "SubmitStartedAt", "LeaseExpiresAt", "CreatedAt", "UpdatedAt"], [], BatchParameters(batchIds)).ConfigureAwait(false);
            await AppendQueryAsync(connection, transaction, writer, "ResourceItems", $"""
                SELECT Id,BatchJobId,StagedImportRowId,CandidateSequence,ShareUrl,NormalizedShareId,ExtractionCode,Note,TargetPathOverride,
                  NormalizedTargetPathOverride,CreateFolderName,FolderCreationModeOverride,GlobalValidityStatus,CandidateConstraintAccountId,
                  CandidateStatus,ReservedAccountId,ReservationExpiresAt,CreatedAt,UpdatedAt
                FROM ResourceItems WHERE BatchJobId IN ({inClause}) ORDER BY BatchJobId,CandidateSequence,Id;
                """, token, ["ReservationExpiresAt", "CreatedAt", "UpdatedAt"], [], BatchParameters(batchIds)).ConfigureAwait(false);
            await AppendQueryAsync(connection, transaction, writer, "AccountAllocations", $"""
                SELECT Id,BatchJobId,AccountId,PriorityOrder,DefaultTargetPath,NormalizedDefaultTargetPath,TargetItemCount,TargetSuccessCount,
                  EffectiveTargetSuccessCount,AssignedItemCount,AttemptedItemCount,NewSuccessItemCount,AlreadySatisfiedItemCount,SuccessItemCount,
                  FailedItemCount,LockedItemCount,ExclusiveCandidateCount,UnusedCandidateCount,ReservedSlotCount,FulfilledCount
                FROM AccountAllocations WHERE BatchJobId IN ({inClause}) ORDER BY BatchJobId,PriorityOrder,AccountId,Id;
                """, token, [], [], BatchParameters(batchIds)).ConfigureAwait(false);
        }
        else
        {
            writer.Value("BatchJobs", "empty");
            writer.Value("TransferOperations", "empty");
            writer.Value("ResourceItems", "empty");
            writer.Value("AccountAllocations", "empty");
        }

        var importIds = await ReadRelevantImportIdsAsync(connection, transaction, account.Id, batchIds, token).ConfigureAwait(false);
        if (importIds.Count != 0)
        {
            var importIn = InClause(importIds.Count);
            await AppendQueryAsync(connection, transaction, writer, "ImportSessions", $"""
                SELECT Id,SourceType,SourceName,Status,TotalRows,ReadyRows,InvalidRows,DuplicateRows,ExcludedRows,PrecheckAccountId,
                  CreatedBatchJobId,AppendToBatchJobId,CreatedAt,UpdatedAt
                FROM ImportSessions WHERE Id IN ({importIn}) ORDER BY Id;
                """, token, ["CreatedAt", "UpdatedAt"], [], BatchParameters(importIds)).ConfigureAwait(false);
        }
        else writer.Value("ImportSessions", "empty");

        var terminalOperations = await ScalarLongAsync(connection, transaction,
            "SELECT COUNT(*) FROM TransferOperations WHERE AssignedAccountId=$account AND Status IN ('Succeeded','Failed','Cancelled');",
            token, ("$account", Id(account.Id))).ConfigureAwait(false);
        var terminalBatches = batchIds.Count == 0 ? 0 : await ScalarLongAsync(connection, transaction,
            $"SELECT COUNT(*) FROM BatchJobs WHERE Id IN ({inClause}) AND Status IN ('Completed','CompletedWithShortfall','Cancelled');",
            token, BatchParameters(batchIds)).ConfigureAwait(false);
        var sharedBatches = batchIds.Count == 0 ? 0 : await ScalarLongAsync(connection, transaction, $"""
            SELECT COUNT(*) FROM BatchJobs batch
            WHERE batch.Id IN ({inClause}) AND batch.Status IN ('Completed','CompletedWithShortfall','Cancelled')
              AND (EXISTS(SELECT 1 FROM AccountAllocations a WHERE a.BatchJobId=batch.Id AND a.AccountId<>$account)
                   OR EXISTS(SELECT 1 FROM TransferOperations o WHERE o.BatchJobId=batch.Id AND o.AssignedAccountId<>$account));
            """, token, [.. BatchParameters(batchIds), ("$account", Id(account.Id))]).ConfigureAwait(false);
        return new Manifest(writer.Finish(), checked((int)terminalOperations), checked((int)terminalBatches), checked((int)sharedBatches));
    }

    private static async Task<List<Guid>> ReadRelevantBatchIdsAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid accountId, CancellationToken token)
    {
        await using var command = Command(connection, transaction, """
            SELECT BatchJobId FROM TransferOperations WHERE AssignedAccountId=$account AND Status IN ('Succeeded','Failed','Cancelled')
            UNION SELECT BatchJobId FROM AccountAllocations WHERE AccountId=$account
            UNION SELECT BatchJobId FROM ResourceItems WHERE CandidateConstraintAccountId=$account OR ReservedAccountId=$account
            UNION SELECT CreatedBatchJobId FROM ImportSessions WHERE PrecheckAccountId=$account AND CreatedBatchJobId IS NOT NULL
            UNION SELECT AppendToBatchJobId FROM ImportSessions WHERE PrecheckAccountId=$account AND AppendToBatchJobId IS NOT NULL
            UNION SELECT batch.Id FROM BatchJobs batch JOIN ImportSessions import ON import.Id=batch.ImportSessionId WHERE import.PrecheckAccountId=$account
            ORDER BY 1;
            """);
        Add(command, "$account", Id(accountId));
        var result = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(Guid.Parse(reader.GetString(0)));
        return result;
    }

    private static async Task<List<Guid>> ReadRelevantImportIdsAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid accountId, IReadOnlyList<Guid> batchIds, CancellationToken token)
    {
        var parameters = BatchParameters(batchIds);
        var predicates = batchIds.Count == 0
            ? "PrecheckAccountId=$account"
            : $"PrecheckAccountId=$account OR CreatedBatchJobId IN ({InClause(batchIds.Count)}) OR AppendToBatchJobId IN ({InClause(batchIds.Count)}) OR Id IN (SELECT ImportSessionId FROM BatchJobs WHERE Id IN ({InClause(batchIds.Count)}) AND ImportSessionId IS NOT NULL)";
        await using var command = Command(connection, transaction, $"SELECT Id FROM ImportSessions WHERE {predicates} ORDER BY Id;");
        foreach (var value in parameters) Add(command, value.Name, value.Value);
        Add(command, "$account", Id(accountId));
        var result = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(Guid.Parse(reader.GetString(0)));
        return result;
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
                string? value;
                if (reader.IsDBNull(i)) value = null;
                else value = Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
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
    private static string InClause(int count) => string.Join(',', Enumerable.Range(0, count).Select(i => "$b" + i.ToString(CultureInfo.InvariantCulture)));
    private static (string Name, object? Value)[] BatchParameters(IReadOnlyList<Guid> ids) =>
        ids.Select((id, index) => ("$b" + index.ToString(CultureInfo.InvariantCulture), (object?)Id(id))).ToArray();

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

    private static string Id(Guid id) => id.ToString("N");
    private static string Time(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private sealed record AccountSnapshot(Guid Id, string BaiduUserId, string DisplayName, string? EncryptedSession,
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
