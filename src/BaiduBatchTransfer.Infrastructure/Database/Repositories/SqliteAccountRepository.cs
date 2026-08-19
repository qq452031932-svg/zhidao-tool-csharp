using BaiduBatchTransfer.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database.Repositories;

public sealed class SqliteAccountRepository : SqliteRepositoryBase, IAccountRepository
{
    internal SqliteAccountRepository(SqliteConnection connection, SqliteTransaction transaction) : base(connection, transaction) { }

    public async Task UpsertAsync(AccountRecord account, CancellationToken cancellationToken = default)
    {
        await using var command = Command("""
            INSERT INTO Accounts(Id,BaiduUserId,DisplayName,EncryptedSession,WebView2ProfileDirectory,LoginMethod,Status,CapacityTotal,CapacityUsed,DailyLimit,DailyExecutedCount,SortOrder,IsEnabled,LastValidatedAt,DailyCountDateKey,IsDeleted,DeletedAt)
            VALUES($id,$user,$name,$session,$profile,$method,$status,$total,$used,$limit,$executed,$sort,$enabled,$validated,$date,$deleted,$deletedAt)
            ON CONFLICT(Id) DO UPDATE SET BaiduUserId=excluded.BaiduUserId,DisplayName=excluded.DisplayName,EncryptedSession=excluded.EncryptedSession,WebView2ProfileDirectory=excluded.WebView2ProfileDirectory,LoginMethod=excluded.LoginMethod,Status=excluded.Status,CapacityTotal=excluded.CapacityTotal,CapacityUsed=excluded.CapacityUsed,DailyLimit=excluded.DailyLimit,DailyExecutedCount=excluded.DailyExecutedCount,SortOrder=excluded.SortOrder,IsEnabled=excluded.IsEnabled,LastValidatedAt=excluded.LastValidatedAt,DailyCountDateKey=excluded.DailyCountDateKey,IsDeleted=excluded.IsDeleted,DeletedAt=excluded.DeletedAt;
            """);
        AddAccount(command, account);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AccountRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var command = Command($"SELECT {Columns} FROM Accounts WHERE Id=$id;"); Add(command, "$id", Id(id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<AccountRecord?> GetByBaiduUserIdAsync(string baiduUserId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baiduUserId);
        await using var command = Command($"SELECT {Columns} FROM Accounts WHERE BaiduUserId=$user LIMIT 1;");
        Add(command, "$user", baiduUserId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task SetEncryptedSessionAsync(Guid id, byte[]? encryptedSession, CancellationToken cancellationToken = default)
    {
        await using var command = Command("UPDATE Accounts SET EncryptedSession=$session WHERE Id=$id;");
        Add(command, "$session", encryptedSession is null ? null : (byte[])encryptedSession.Clone());
        Add(command, "$id", Id(id));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Page<AccountRecord>> GetPageAsync(PageQuery query, CancellationToken cancellationToken = default)
    {
        query.Validate(); cancellationToken.ThrowIfCancellationRequested();
        await using var count = Command("SELECT COUNT(*) FROM Accounts;");
        var total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        await using var command = Command($"SELECT {Columns} FROM Accounts ORDER BY SortOrder, Id LIMIT $limit OFFSET $offset;");
        Add(command, "$limit", query.PageSize); Add(command, "$offset", query.Offset);
        var items = new List<AccountRecord>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) items.Add(Read(reader));
        return new(items, total, query.PageNumber, query.PageSize);
    }

    public async Task UpsertRuleAsync(AccountRuleRecord rule, CancellationToken cancellationToken = default)
    {
        await using var command = Command("INSERT INTO AccountRules(Id,AccountId,RuleType,RuleValue,Priority,IsEnabled) VALUES($id,$account,$type,$value,$priority,$enabled) ON CONFLICT(Id) DO UPDATE SET AccountId=excluded.AccountId,RuleType=excluded.RuleType,RuleValue=excluded.RuleValue,Priority=excluded.Priority,IsEnabled=excluded.IsEnabled;");
        Add(command,"$id",Id(rule.Id)); Add(command,"$account",Id(rule.AccountId)); Add(command,"$type",rule.RuleType); Add(command,"$value",rule.RuleValue); Add(command,"$priority",rule.Priority); Add(command,"$enabled",rule.IsEnabled ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Page<AccountRuleRecord>> GetRulesPageAsync(Guid accountId, PageQuery query, CancellationToken cancellationToken = default)
    {
        query.Validate(); cancellationToken.ThrowIfCancellationRequested();
        await using var count = Command("SELECT COUNT(*) FROM AccountRules WHERE AccountId=$account;"); Add(count,"$account",Id(accountId));
        var total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        await using var command = Command("SELECT Id,AccountId,RuleType,RuleValue,Priority,IsEnabled FROM AccountRules WHERE AccountId=$account ORDER BY Priority,Id LIMIT $limit OFFSET $offset;");
        Add(command,"$account",Id(accountId)); Add(command,"$limit",query.PageSize); Add(command,"$offset",query.Offset);
        var items = new List<AccountRuleRecord>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) items.Add(new(Guid.Parse(reader.GetString(0)),Guid.Parse(reader.GetString(1)),reader.GetString(2),reader.GetString(3),reader.GetInt32(4),reader.GetInt32(5) != 0));
        return new(items,total,query.PageNumber,query.PageSize);
    }

    private const string Columns = "Id,BaiduUserId,DisplayName,EncryptedSession,WebView2ProfileDirectory,LoginMethod,Status,CapacityTotal,CapacityUsed,DailyLimit,DailyExecutedCount,SortOrder,IsEnabled,LastValidatedAt,DailyCountDateKey,IsDeleted,DeletedAt";

    private static void AddAccount(SqliteCommand c, AccountRecord a)
    {
        Add(c,"$id",Id(a.Id)); Add(c,"$user",a.BaiduUserId); Add(c,"$name",a.DisplayName); Add(c,"$session",a.EncryptedSession);
        Add(c,"$profile",a.WebView2ProfileDirectory); Add(c,"$method",a.LoginMethod); Add(c,"$status",a.Status); Add(c,"$total",a.CapacityTotal);
        Add(c,"$used",a.CapacityUsed); Add(c,"$limit",a.DailyLimit); Add(c,"$executed",a.DailyExecutedCount); Add(c,"$sort",a.SortOrder);
        Add(c,"$enabled",a.IsEnabled ? 1 : 0); Add(c,"$validated",NullableTime(a.LastValidatedAt)); Add(c,"$date",a.DailyCountDateKey);
        Add(c,"$deleted",a.IsDeleted ? 1 : 0); Add(c,"$deletedAt",NullableTime(a.DeletedAt));
    }

    private static AccountRecord Read(SqliteDataReader r) => new(
        Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), ReadEncryptedSession(r, 3),
        r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetInt64(7),
        r.IsDBNull(8) ? null : r.GetInt64(8), r.GetInt32(9), r.GetInt32(10), r.GetInt32(11), r.GetInt32(12) != 0,
        ReadNullableTime(r,13), r.GetString(14), r.GetInt32(15) != 0, ReadNullableTime(r,16));

    private static byte[]? ReadEncryptedSession(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) || reader.GetValue(ordinal) is not byte[] bytes ? null : (byte[])bytes.Clone();
}
