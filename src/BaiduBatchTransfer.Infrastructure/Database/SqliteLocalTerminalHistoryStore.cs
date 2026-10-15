using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BaiduBatchTransfer.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database;

/// <summary>Deletes only a confirmed, unchanged terminal history snapshot. Register as a singleton.</summary>
public sealed class SqliteLocalTerminalHistoryStore : ILocalTerminalHistoryStore
{
    private readonly SqliteConnectionFactory factory;
    private readonly TimeProvider timeProvider;
    private readonly byte[] snapshotKey = RandomNumberGenerator.GetBytes(32);
    private const string Batches = "SELECT Id FROM temp.HistoryBatches";
    private const string Transfers = "SELECT Id FROM temp.HistoryOperations WHERE OwnerKind='Transfer'";
    private const string Shares = "SELECT Id FROM temp.HistoryOperations WHERE OwnerKind='TransferShare'";
    private const string Standalones = "SELECT Id FROM temp.HistoryOperations WHERE OwnerKind='StandaloneShare'";
    private const string Resources = "SELECT Id FROM temp.HistoryResources";
    private const string DiscoveryRuns = "SELECT RunId,AccountId FROM temp.HistoryDiscoveryRuns";
    // SQLite normalizes explicit offsets to UTC; malformed timestamps fail closed.
    private const string ActiveLease = "(LeaseExpiresAt IS NOT NULL AND (julianday(LeaseExpiresAt) IS NULL OR julianday(LeaseExpiresAt)>julianday($now)))";
    private static readonly string[] DiscoveryExpiryFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'",
    ];

    public SqliteLocalTerminalHistoryStore(SqliteConnectionFactory factory, TimeProvider? timeProvider = null)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<LocalHistoryCleanupPreview> PreviewAsync(LocalHistoryKind kind, CancellationToken cancellationToken = default)
    {
        ValidateKind(kind);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var snapshot = await ReadSnapshotAsync(connection, transaction, kind, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    public async Task<LocalHistoryCleanupResult> DeleteAsync(LocalHistoryCleanupPreview confirmedPreview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmedPreview);
        ValidateKind(confirmedPreview.Kind);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // BEGIN IMMEDIATE prevents another writer from changing the validated closure before commit.
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadSnapshotAsync(connection, transaction, confirmedPreview.Kind, cancellationToken).ConfigureAwait(false);
        if (confirmedPreview.HasConflicts || current.HasConflicts ||
            current.TerminalBatchCount != confirmedPreview.TerminalBatchCount ||
            !TokenEquals(current.SnapshotToken, confirmedPreview.SnapshotToken))
            return new(0, LocalHistoryCleanupDisposition.Conflict);
        if (current.TerminalBatchCount == 0)
            return new(0, LocalHistoryCleanupDisposition.NothingToDelete);

        var oldForeignKeyIssues = await ForeignKeyIssuesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var scopes = Scopes(current.Kind);
        // Delete non-FK automation evidence before the attempt, and attempts before their owners.
        foreach (var scope in scopes.Where(s => s.Table.StartsWith("WebAutomation", StringComparison.Ordinal)))
            await ExecuteAsync(connection, transaction, $"DELETE FROM {scope.Table} WHERE {scope.Predicate};", cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction,
            $"DELETE FROM LocalResultExports WHERE {scopes.Single(s => s.Table == "LocalResultExports").Predicate};", cancellationToken).ConfigureAwait(false);
        if (current.Kind == LocalHistoryKind.Transfer)
        {
            await ExecuteAsync(connection, transaction, $"""
                DELETE FROM BatchShareDiscoveryRuns WHERE BatchJobId IN ({Batches});
                DELETE FROM ExecutionLogs WHERE BatchJobId IN ({Batches})
                  OR TransferOperationId IN ({Transfers}) OR ResourceItemId IN ({Resources});
                DELETE FROM ShareOperations WHERE Id IN ({Shares});
                DELETE FROM TransferOperations WHERE Id IN ({Transfers});
                DELETE FROM AccountAllocations WHERE BatchJobId IN ({Batches});
                DELETE FROM ResourceItems WHERE Id IN ({Resources});
                UPDATE ImportSessions SET CreatedBatchJobId=NULL WHERE CreatedBatchJobId IN ({Batches});
                UPDATE ImportSessions SET AppendToBatchJobId=NULL WHERE AppendToBatchJobId IN ({Batches});
                DELETE FROM BatchJobs WHERE Id IN ({Batches});
                """, cancellationToken).ConfigureAwait(false);
        }
        else
            await ExecuteAsync(connection, transaction, $"""
                DELETE FROM StandaloneShareOperations WHERE Id IN ({Standalones});
                DELETE FROM StandaloneShareBatches WHERE Id IN ({Batches});
                """, cancellationToken).ConfigureAwait(false);

        // Parent deletion performs the real FK cascades. Checking the same persisted target
        // identities afterwards also catches trigger-suppressed deletes and non-FK orphans.
        foreach (var scope in scopes)
            if (await ExistsAsync(connection, transaction, $"SELECT 1 FROM {scope.Table} WHERE {scope.Predicate}", cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("Local history cleanup left target records behind.");
        var newForeignKeyIssues = await ForeignKeyIssuesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (!newForeignKeyIssues.IsSubsetOf(oldForeignKeyIssues))
            throw new InvalidDataException("Local history cleanup introduced a database relationship violation.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(current.TerminalBatchCount, LocalHistoryCleanupDisposition.Deleted);
    }

    private async Task<LocalHistoryCleanupPreview> ReadSnapshotAsync(
        SqliteConnection connection, SqliteTransaction transaction, LocalHistoryKind kind, CancellationToken token)
    {
        var table = kind == LocalHistoryKind.Transfer ? "BatchJobs" : "StandaloneShareBatches";
        var terminal = kind == LocalHistoryKind.Transfer
            ? "'Completed','CompletedWithShortfall','Cancelled'" : "'Completed','CompletedWithFailures','Cancelled'";
        await ExecuteAsync(connection, transaction, $"""
            CREATE TEMP TABLE HistoryBatches(Id TEXT PRIMARY KEY);
            INSERT INTO HistoryBatches SELECT Id FROM {table} WHERE Status IN ({terminal});
            CREATE TEMP TABLE HistoryOperations(Id TEXT,OwnerKind TEXT,AccountId TEXT,PRIMARY KEY(Id,OwnerKind));
            CREATE TEMP TABLE HistoryResources(Id TEXT PRIMARY KEY);
            CREATE TEMP TABLE HistoryDiscoveryRuns(RunId TEXT,AccountId TEXT,PRIMARY KEY(RunId,AccountId));
            """, token).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, kind == LocalHistoryKind.Transfer ? $"""
            INSERT INTO HistoryResources SELECT Id FROM ResourceItems WHERE BatchJobId IN ({Batches});
            INSERT INTO HistoryOperations SELECT Id,'Transfer',AssignedAccountId FROM TransferOperations WHERE BatchJobId IN ({Batches});
            INSERT INTO HistoryOperations SELECT Id,'TransferShare',AccountId FROM ShareOperations WHERE BatchJobId IN ({Batches});
            INSERT INTO HistoryDiscoveryRuns SELECT RunId,AccountId FROM BatchShareDiscoveryRuns WHERE BatchJobId IN ({Batches});
            """ : $"""
            INSERT INTO HistoryOperations SELECT Id,'StandaloneShare',AccountId FROM StandaloneShareOperations WHERE BatchId IN ({Batches});
            """, token).ConfigureAwait(false);
        await using var countCommand = Command(connection, transaction, "SELECT count(*) FROM temp.HistoryBatches;");
        var count = checked(Convert.ToInt32(await countCommand.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture));
        var conflicts = await HasConflictsAsync(connection, transaction, kind, timeProvider.GetUtcNow(), token).ConfigureAwait(false);
        var retryableFailures = await CountRetryableFailuresAsync(connection, transaction, kind, token).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, snapshotKey);
        Append(hash, kind.ToString());
        Append(hash, conflicts ? "conflict" : "clear");
        // 计数进令牌：确认之后如果可重试的失败项发生变化，删除必须失败重来，
        // 免得用户确认的是"3 条"而实际删掉的是另一批。
        Append(hash, retryableFailures.ToString(CultureInfo.InvariantCulture));
        foreach (var scope in Scopes(kind))
        {
            Append(hash, scope.Table);
            await using var command = Command(connection, transaction,
                $"SELECT * FROM {scope.Table} WHERE {scope.Predicate} ORDER BY rowid;");
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                hash.AppendData([1]);
                for (var column = 0; column < reader.FieldCount; column++)
                {
                    Append(hash, reader.GetName(column));
                    var value = reader.GetValue(column);
                    if (value is DBNull) hash.AppendData([0]);
                    else if (value is byte[] bytes) { hash.AppendData([2]); Append(hash, Convert.ToHexString(bytes)); }
                    else { hash.AppendData([3]); Append(hash, Convert.ToString(value, CultureInfo.InvariantCulture)!); }
                }
            }
            hash.AppendData([0]);
        }
        return new(kind, count, conflicts, Convert.ToHexString(hash.GetHashAndReset()), retryableFailures);
    }

    // 仍可安全重试的失败转存。它曾经算作硬冲突，把整批历史永久挡住：软件崩溃一次
    // 就会留下"准备提交"状态的失败项，用户既推进不了状态也清不掉记录。删除的只是
    // 本地记录，所以改为在确认框里如实告知代价，由用户决定。
    private static async Task<int> CountRetryableFailuresAsync(
        SqliteConnection c, SqliteTransaction tx, LocalHistoryKind kind, CancellationToken token)
    {
        if (kind != LocalHistoryKind.Transfer) return 0;
        // 可重试的失败分享与可重试的失败转存一样：不再挡住清空，只在确认框里说明清空后不能再重试。
        // 例如分享结果一直核对不到、按未创建处理的那一条（批次已收尾，只差重试分享）。
        await using var shares = Command(c, tx, $"""
            SELECT count(*) FROM ShareOperations operation
            WHERE operation.Id IN ({Shares}) AND operation.Status='Failed'
              AND ({SqliteShareRequeueSafety.Predicate("operation")})
            """);
        var retryableShares = checked(Convert.ToInt32(
            await shares.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture));
        var safeRetryPredicate = SqliteRetryFailedTransferSafety.Predicate(
            "o", "b", "r", "allocation", "account", "output", "settings");
        await using var command = Command(c, tx, $"""
            SELECT count(*) FROM TransferOperations o
            JOIN BatchJobs b ON b.Id=o.BatchJobId
            JOIN ResourceItems r ON r.Id=o.ResourceItemId
            JOIN AccountAllocations allocation ON allocation.BatchJobId=o.BatchJobId AND allocation.AccountId=o.AssignedAccountId
            JOIN Accounts account ON account.Id=o.AssignedAccountId
            LEFT JOIN BatchOutputAccounts output ON output.BatchJobId=o.BatchJobId AND output.AccountId=o.AssignedAccountId
            LEFT JOIN BatchShareSettings settings ON settings.BatchJobId=o.BatchJobId
            WHERE o.Id IN ({Transfers}) AND b.AcceptanceOnly=0 AND ({safeRetryPredicate})
            """);
        return checked(retryableShares +
            Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture));
    }

    private static async Task<bool> HasConflictsAsync(SqliteConnection c, SqliteTransaction tx, LocalHistoryKind kind, DateTimeOffset now, CancellationToken token)
    {
        var owner = kind == LocalHistoryKind.Transfer ? "TransferBatch" : "StandaloneShareBatch";
        var nowValue = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        // A terminal transfer/share attempt does not resolve a separate directory mutation,
        // including an intent from an older generation or one without an attempt FK.
        if (await ExistsAsync(c, tx, $"""
            SELECT 1 FROM LocalResultExports WHERE OwnerKind='{owner}' AND OwnerId IN ({Batches})
              AND (Status='InProgress' OR {ActiveLease})
            UNION ALL
            SELECT 1 FROM WebAutomationAttempts attempt
            JOIN temp.HistoryOperations target ON target.Id=attempt.OperationId
            WHERE attempt.IsWaitingForUser<>0
              OR attempt.ResumeStage IS NOT NULL OR attempt.OwnerKind<>target.OwnerKind OR attempt.AccountId<>target.AccountId
              -- 'Preparing' 是状态机的第一个阶段，且提交是预写式的：适配器先把
              -- BaselinePersisted->Submitting 落盘，再真正调用提交（见
              -- WebViewBaiduNetdiskAdapter.MarkSubmittingAsync）。进入 Submitting
              -- 又必须先处于 BaselinePersisted。所以停在 Preparing 且没有基线的
              -- 尝试，可证明从未向网盘提交过任何东西，也没有证据需要保留——它只是
              -- 进程被杀/崩溃留下的残渣。把它算作冲突，会让那一批历史永远清不掉，
              -- 而界面上既不说明原因也没有出路。
              OR (attempt.State NOT IN ('Succeeded','DefinitelyNotSubmitted')
                  AND NOT (attempt.State='Preparing' AND attempt.BaselineItemCount=0))
            UNION ALL
            SELECT 1 FROM WebAutomationDirectoryMutations mutation
            JOIN temp.HistoryOperations target ON target.Id=mutation.OperationId
            WHERE mutation.State<>'Confirmed'
            """, token, ("$now", nowValue)).ConfigureAwait(false)) return true;
        if (kind == LocalHistoryKind.Transfer)
        {
            if (await HasActiveDiscoveryLeaseAsync(c, tx, now, token).ConfigureAwait(false)) return true;
            return await ExistsAsync(c, tx, $"""
                SELECT 1 FROM TransferOperations WHERE Id IN ({Transfers}) AND (
                    Status NOT IN ('Succeeded','Failed','Cancelled') OR {ActiveLease}
                    OR SubmitState IN ('Submitting','PendingConfirmation') OR TargetSlotState='Reserved'
                    OR (Status='Failed' AND SubmitState IN ('NotPrepared','PreparingToSubmit')
                        AND (DailyCountedAt IS NOT NULL OR ConfirmationDescriptorJson IS NOT NULL
                            OR SubmitStartedAt IS NOT NULL OR AdapterRequestId IS NOT NULL OR RemoteItemIdsJson IS NOT NULL))
                    OR (Status='Cancelled' AND (SubmitStartedAt IS NOT NULL OR AdapterRequestId IS NOT NULL
                        OR ConfirmationDescriptorJson IS NOT NULL OR DailyCountedAt IS NOT NULL)))
                UNION ALL
                SELECT 1 FROM ShareOperations WHERE Id IN ({Shares}) AND (
                    Status NOT IN ('Succeeded','ReusedExisting','Failed','Cancelled') OR {ActiveLease}
                    OR NextReconcileAt IS NOT NULL
                    OR (Status IN ('Failed','Cancelled') AND SubmissionAcceptance<>'DefinitelyNotAccepted'
                        AND (RequestDescriptorJson IS NOT NULL OR AdapterRequestId IS NOT NULL OR SubmitStartedAt IS NOT NULL
                             OR BaselineCompletedAt IS NOT NULL OR AttemptCount<>0)))
                UNION ALL
                SELECT 1 FROM ResourceItems WHERE Id IN ({Resources}) AND
                    (CandidateStatus='Reserved' OR ReservedAccountId IS NOT NULL OR ReservationExpiresAt IS NOT NULL)
                UNION ALL
                SELECT 1 FROM TransferOperations WHERE ResourceItemId IN ({Resources}) AND BatchJobId NOT IN ({Batches})
                UNION ALL
                SELECT 1 FROM BatchShareDiscoveryStaging WHERE BatchJobId NOT IN ({Batches})
                  AND (RunId,AccountId) IN ({DiscoveryRuns})
                UNION ALL
                SELECT 1 FROM ExecutionLogs WHERE BatchJobId IS NOT NULL AND BatchJobId NOT IN ({Batches})
                  AND (TransferOperationId IN ({Transfers}) OR ResourceItemId IN ({Resources}))
                """, token, ("$now", nowValue)).ConfigureAwait(false);
        }
        // Failed NotStarted/DefinitelyNotAccepted is the exact CanSafelyRetry gate used by
        // StandaloneShareOperationRecord and SqliteStandaloneShareStore.TryRetryFailedOperationAsync.
        return await ExistsAsync(c, tx, $"""
            SELECT 1 FROM StandaloneShareOperations WHERE Id IN ({Standalones}) AND (
                Status NOT IN ('Succeeded','Failed','Cancelled') OR {ActiveLease}
                OR NextReconcileAt IS NOT NULL OR ReconciliationIssue IS NOT NULL
                OR SubmissionState IN ('Prepared','Accepted','ResultPending')
                OR (Status='Succeeded' AND SubmissionState<>'Completed')
                OR (Status='Failed' AND SubmissionState IN ('NotStarted','DefinitelyNotAccepted'))
                OR (Status='Cancelled' AND (RequestDescriptorJson IS NOT NULL OR SubmitStartedAt IS NOT NULL OR AdapterRequestId IS NOT NULL)))
            """, token, ("$now", nowValue)).ConfigureAwait(false);
    }

    private static async Task<bool> HasActiveDiscoveryLeaseAsync(
        SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, CancellationToken token)
    {
        await using var command = Command(c, tx, $"SELECT ExpiresAt FROM BatchShareDiscoveryRuns WHERE BatchJobId IN ({Batches});");
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            // Require an absolute, valid calendar timestamp with an explicit UTC offset.
            // SQLite also accepts relative words and Julian-day numbers, which are not leases.
            if (reader.IsDBNull(0) || !DateTimeOffset.TryParseExact(reader.GetString(0), DiscoveryExpiryFormats,
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expiresAt)
                || expiresAt > now)
                return true;
        }
        return false;
    }

    private static List<Scope> Scopes(LocalHistoryKind kind)
    {
        // Directory mutations intentionally have no FK. Scope by owner identity across
        // all generations, including evidence from older attempts and orphaned generations.
        var result = new List<Scope>();
        foreach (var table in new[] { "WebAutomationDirectoryMutations", "WebAutomationAttemptSourceItems", "WebAutomationAttemptRemoteItems", "WebAutomationTransferMappings", "WebAutomationAttemptAcceptedRemoteItems", "WebAutomationShareAcceptances", "WebAutomationAttempts" })
            result.Add(new(table, $"EXISTS(SELECT 1 FROM temp.HistoryOperations target WHERE target.Id={table}.OperationId AND target.AccountId={table}.AccountId)"));
        result.Add(new("LocalResultExports", $"OwnerKind='{(kind == LocalHistoryKind.Transfer ? "TransferBatch" : "StandaloneShareBatch")}' AND OwnerId IN ({Batches})"));
        if (kind == LocalHistoryKind.Transfer)
        {
            result.Add(new("TransferOperations", $"Id IN ({Transfers})"));
            result.Add(new("ShareOperations", $"Id IN ({Shares})"));
            foreach (var table in new[] { "ShareOperationGenerations", "ShareOperationRemoteItems", "ShareDiscoveryBaselines", "BaiduShareResults" })
                result.Add(new(table, $"ShareOperationId IN ({Shares})"));
            result.Add(new("ResourceItems", $"Id IN ({Resources})"));
            result.Add(new("ExecutionLogs", $"BatchJobId IN ({Batches}) OR TransferOperationId IN ({Transfers}) OR ResourceItemId IN ({Resources})"));
            foreach (var table in new[] { "AccountAllocations", "BatchShareSettings", "BatchCreationReceipts", "BatchSourceColumns", "BatchOutputAccounts", "BatchShareDecisionSnapshots", "BatchShareDiscoverySnapshots", "BatchShareDiscoveryMembers", "BatchShareDiscoveryExactSets", "BatchShareDiscoveryRuns", "BatchResultDestinations" })
                result.Add(new(table, $"BatchJobId IN ({Batches})"));
            result.Add(new("BatchShareDiscoveryRunExactSets", $"(RunId,AccountId) IN ({DiscoveryRuns})"));
            result.Add(new("BatchShareDiscoveryStaging", $"BatchJobId IN ({Batches}) OR (RunId,AccountId) IN ({DiscoveryRuns})"));
            result.Add(new("DirectBackgroundAcceptanceAuthorizations", $"BatchId IN ({Batches})"));
            result.Add(new("ImportSessions", $"CreatedBatchJobId IN ({Batches}) OR AppendToBatchJobId IN ({Batches})"));
            result.Add(new("BatchJobs", $"Id IN ({Batches})"));
        }
        else
        {
            result.Add(new("StandaloneShareOperations", $"Id IN ({Standalones})"));
            foreach (var table in new[] { "StandaloneShareRemoteItems", "StandaloneShareDiscoveryBaselines", "StandaloneShareResults" })
                result.Add(new(table, $"OperationId IN ({Standalones})"));
            result.Add(new("StandaloneShareBatches", $"Id IN ({Batches})"));
        }
        return result;
    }

    private static async Task<HashSet<string>> ForeignKeyIssuesAsync(SqliteConnection c, SqliteTransaction tx, CancellationToken token)
    {
        var issues = new HashSet<string>(StringComparer.Ordinal);
        await using var command = Command(c, tx, "PRAGMA foreign_key_check;");
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            issues.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
        return issues;
    }

    private static bool TokenEquals(string? current, string? supplied) =>
        current is not null && supplied is not null && supplied.Length == current.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(current), Encoding.ASCII.GetBytes(supplied));

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void ValidateKind(LocalHistoryKind kind)
    {
        if (kind is not (LocalHistoryKind.Transfer or LocalHistoryKind.StandaloneShare))
            throw new ArgumentOutOfRangeException(nameof(kind));
    }

    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction tx, string sql)
    {
        var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        return command;
    }

    private static async Task ExecuteAsync(SqliteConnection c, SqliteTransaction tx, string sql, CancellationToken token)
    {
        await using var command = Command(c, tx, sql);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task<bool> ExistsAsync(SqliteConnection c, SqliteTransaction tx, string sql, CancellationToken token,
        params (string Name, object Value)[] parameters)
    {
        await using var command = Command(c, tx, $"SELECT EXISTS({sql});");
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
    }

    private sealed record Scope(string Table, string Predicate);
}
