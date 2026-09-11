using System.Globalization;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database.Repositories;

public sealed class SqliteAccountShareAnalyticsRepository(SqliteConnectionFactory connectionFactory)
    : IAccountShareAnalyticsRepository
{
    private readonly SqliteConnectionFactory connectionFactory =
        connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    public async Task<long> ReserveGenerationAsync(Guid accountId, CancellationToken cancellationToken)
    {
        ValidateAccountId(accountId);
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            await using (var insert = Command(connection, transaction, """
                INSERT INTO AccountShareSyncState(AccountId,IssuedGeneration,CommittedGeneration,LastSuccessfulSyncAtUtc)
                VALUES($account,0,0,NULL) ON CONFLICT(AccountId) DO NOTHING;
                """))
            {
                insert.Parameters.AddWithValue("$account", Id(accountId));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            long generation;
            await using (var update = Command(connection, transaction, """
                UPDATE AccountShareSyncState SET IssuedGeneration=IssuedGeneration+1
                WHERE AccountId=$account RETURNING IssuedGeneration;
                """))
            {
                update.Parameters.AddWithValue("$account", Id(accountId));
                generation = Convert.ToInt64(await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return generation;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<AccountShareCachedSnapshot> LoadCachedAsync(Guid accountId, CancellationToken cancellationToken)
    {
        ValidateAccountId(accountId);
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        DateTimeOffset? lastSuccessfulSync = null;
        await using (var state = Command(connection, transaction,
            "SELECT LastSuccessfulSyncAtUtc FROM AccountShareSyncState WHERE AccountId=$account;"))
        {
            state.Parameters.AddWithValue("$account", Id(accountId));
            var raw = await state.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (raw is string value) lastSuccessfulSync = ParseTime(value);
        }

        var records = new Dictionary<string, RecordBuilder>(StringComparer.Ordinal);
        await using (var command = Command(connection, transaction, """
            SELECT ShareId,Title,ShareUrl,SharedAtUtc,Status
            FROM AccountShareRecords
            WHERE AccountId=$account AND IsCurrent=1
            ORDER BY SharedAtUtc DESC,ShareId;
            """))
        {
            command.Parameters.AddWithValue("$account", Id(accountId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var shareId = reader.GetString(0);
                records.Add(shareId, new(
                    shareId,
                    reader.GetString(1),
                    new Uri(reader.GetString(2), UriKind.Absolute),
                    ParseTime(reader.GetString(3)),
                    ParseStatus(reader.GetString(4))));
            }
        }

        if (records.Count != 0)
        {
            await using var command = Command(connection, transaction, """
                WITH ranked AS (
                  SELECT s.ShareId,s.Metric,s.DateKey,s.HistoricalTotal,s.DailyValue,
                         ROW_NUMBER() OVER (
                           PARTITION BY s.ShareId,s.Metric ORDER BY s.DateKey DESC,s.CapturedAtUtc DESC) AS rn
                  FROM AccountShareMetricSnapshots s
                  JOIN AccountShareRecords r
                    ON r.AccountId=s.AccountId AND r.ShareId=s.ShareId AND r.IsCurrent=1
                  WHERE s.AccountId=$account
                )
                SELECT ShareId,Metric,DateKey,HistoricalTotal,DailyValue
                FROM ranked WHERE rn<=6
                ORDER BY ShareId,Metric,DateKey DESC;
                """);
            command.Parameters.AddWithValue("$account", Id(accountId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var builder = records[reader.GetString(0)];
                var metric = ParseMetric(reader.GetString(1));
                var date = DateOnly.ParseExact(reader.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                builder.Add(metric, date, reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(accountId, lastSuccessfulSync, records.Values.Select(builder => builder.Build()).ToArray());
    }

    public async Task<bool> CommitCompleteSyncAsync(AccountShareSyncCommit commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            long issued;
            long committed;
            await using (var state = Command(connection, transaction, """
                SELECT IssuedGeneration,CommittedGeneration FROM AccountShareSyncState WHERE AccountId=$account;
                """))
            {
                state.Parameters.AddWithValue("$account", Id(commit.AccountId));
                await using var reader = await state.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("The sync generation was not reserved.");
                issued = reader.GetInt64(0);
                committed = reader.GetInt64(1);
            }
            if (commit.Generation <= committed)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
            if (commit.Generation > issued)
                throw new InvalidOperationException("The sync generation was not reserved.");

            await using (var archive = Command(connection, transaction,
                "UPDATE AccountShareRecords SET IsCurrent=0 WHERE AccountId=$account AND IsCurrent=1;"))
            {
                archive.Parameters.AddWithValue("$account", Id(commit.AccountId));
                await archive.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var record = CreateRecordUpsert(connection, transaction);
            await using var snapshot = CreateSnapshotUpsert(connection, transaction);
            var syncDate = BeijingDate(commit.SyncedAtUtc);
            foreach (var item in commit.Items)
            {
                SetRecord(record, commit, item);
                await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                foreach (var metric in item.Metrics)
                {
                    long syncDaily = 0;
                    var hasSyncDaily = metric.DirectDailyValues?.TryGetValue(syncDate, out syncDaily) == true;
                    SetSnapshot(snapshot, commit, item.ShareId, metric.Metric, syncDate,
                        metric.HistoricalTotal, hasSyncDaily ? syncDaily : null,
                        hasSyncDaily ? "Direct" : null,
                        preserveHistoricalTotal: false);
                    await snapshot.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    if (metric.DirectDailyValues is null) continue;
                    foreach (var (date, dailyValue) in metric.DirectDailyValues)
                    {
                        if (date == syncDate) continue;
                        SetSnapshot(snapshot, commit, item.ShareId, metric.Metric, date, null, dailyValue, "Direct",
                            preserveHistoricalTotal: true);
                        await snapshot.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            await using (var finish = Command(connection, transaction, """
                UPDATE AccountShareSyncState
                SET CommittedGeneration=$generation,LastSuccessfulSyncAtUtc=$synced
                WHERE AccountId=$account;
                """))
            {
                finish.Parameters.AddWithValue("$generation", commit.Generation);
                finish.Parameters.AddWithValue("$synced", Time(commit.SyncedAtUtc));
                finish.Parameters.AddWithValue("$account", Id(commit.AccountId));
                await finish.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private static SqliteCommand CreateRecordUpsert(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = Command(connection, transaction, """
            INSERT INTO AccountShareRecords(AccountId,ShareId,Title,ShareUrl,SharedAtUtc,Status,IsCurrent,LastSeenAtUtc)
            VALUES($account,$share,$title,$url,$shared,$status,1,$seen)
            ON CONFLICT(AccountId,ShareId) DO UPDATE SET
              Title=excluded.Title,ShareUrl=excluded.ShareUrl,SharedAtUtc=excluded.SharedAtUtc,
              Status=excluded.Status,IsCurrent=1,LastSeenAtUtc=excluded.LastSeenAtUtc;
            """);
        foreach (var name in new[] { "$account", "$share", "$title", "$url", "$shared", "$status", "$seen" })
            command.Parameters.Add(new SqliteParameter(name, null));
        return command;
    }

    private static SqliteCommand CreateSnapshotUpsert(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = Command(connection, transaction, """
            INSERT INTO AccountShareMetricSnapshots(
              AccountId,ShareId,Metric,DateKey,CapturedAtUtc,HistoricalTotal,DailyValue,DailySource)
            VALUES($account,$share,$metric,$date,$captured,$total,$daily,$source)
            ON CONFLICT(AccountId,ShareId,Metric,DateKey) DO UPDATE SET
              CapturedAtUtc=excluded.CapturedAtUtc,
              HistoricalTotal=CASE WHEN $preserve=1 THEN COALESCE(excluded.HistoricalTotal,HistoricalTotal) ELSE excluded.HistoricalTotal END,
              DailyValue=excluded.DailyValue,DailySource=excluded.DailySource;
            """);
        foreach (var name in new[] { "$account", "$share", "$metric", "$date", "$captured", "$total", "$daily", "$source", "$preserve" })
            command.Parameters.Add(new SqliteParameter(name, null));
        return command;
    }

    private static void SetRecord(SqliteCommand command, AccountShareSyncCommit commit, AccountShareObservation item)
    {
        command.Parameters["$account"].Value = Id(commit.AccountId);
        command.Parameters["$share"].Value = item.ShareId;
        command.Parameters["$title"].Value = item.Title;
        command.Parameters["$url"].Value = item.ShareUri.AbsoluteUri;
        command.Parameters["$shared"].Value = Time(item.SharedAtUtc);
        command.Parameters["$status"].Value = item.Status.ToString();
        command.Parameters["$seen"].Value = Time(commit.SyncedAtUtc);
    }

    private static void SetSnapshot(SqliteCommand command, AccountShareSyncCommit commit, string shareId,
        AccountShareMetric metric, DateOnly date, long? total, long? daily, string? source, bool preserveHistoricalTotal)
    {
        command.Parameters["$account"].Value = Id(commit.AccountId);
        command.Parameters["$share"].Value = shareId;
        command.Parameters["$metric"].Value = metric.ToString();
        command.Parameters["$date"].Value = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        command.Parameters["$captured"].Value = Time(commit.SyncedAtUtc);
        command.Parameters["$total"].Value = total is null ? DBNull.Value : total.Value;
        command.Parameters["$daily"].Value = daily is null ? DBNull.Value : daily.Value;
        command.Parameters["$source"].Value = source is null ? DBNull.Value : source;
        command.Parameters["$preserve"].Value = preserveHistoricalTotal ? 1 : 0;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static string Id(Guid value) => value.ToString("D");
    private static string Time(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).ToUniversalTime();
    private static DateOnly BeijingDate(DateTimeOffset value) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, ResolveBeijingTimeZone()).DateTime);
    private static TimeZoneInfo ResolveBeijingTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); }
    }
    private static void ValidateAccountId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("Account ID is required.", nameof(value));
    }
    private static AccountShareRemoteStatus ParseStatus(string value) => value switch
    {
        "Valid" => AccountShareRemoteStatus.Valid,
        "Invalid" => AccountShareRemoteStatus.Invalid,
        _ => throw new InvalidDataException("Cached share status is invalid."),
    };
    private static AccountShareMetric ParseMetric(string value) => value switch
    {
        "Transfer" => AccountShareMetric.Transfer,
        "View" => AccountShareMetric.View,
        "Download" => AccountShareMetric.Download,
        _ => throw new InvalidDataException("Cached share metric is invalid."),
    };

    private sealed class RecordBuilder(string shareId, string title, Uri shareUri, DateTimeOffset sharedAtUtc,
        AccountShareRemoteStatus status)
    {
        private readonly Dictionary<AccountShareMetric, SortedDictionary<DateOnly, long?>> cumulative = [];
        private readonly Dictionary<AccountShareMetric, SortedDictionary<DateOnly, long?>> daily = [];

        public void Add(AccountShareMetric metric, DateOnly date, long? historicalTotal, long? dailyValue)
        {
            if (!cumulative.TryGetValue(metric, out var cumulativeValues)) cumulative[metric] = cumulativeValues = [];
            if (!daily.TryGetValue(metric, out var dailyValues)) daily[metric] = dailyValues = [];
            if (historicalTotal is not null) cumulativeValues[date] = historicalTotal;
            if (dailyValue is not null) dailyValues[date] = dailyValue;
        }

        public AccountShareCachedRecord Build() => new(
            shareId, title, shareUri, sharedAtUtc, status,
            Enum.GetValues<AccountShareMetric>().Select(metric =>
            {
                cumulative.TryGetValue(metric, out var cumulativeValues);
                daily.TryGetValue(metric, out var dailyValues);
                var total = cumulativeValues?.OrderByDescending(pair => pair.Key).Select(pair => pair.Value).FirstOrDefault();
                return new AccountShareMetricHistory(metric, total, cumulativeValues, dailyValues);
            }).ToArray());
    }
}
