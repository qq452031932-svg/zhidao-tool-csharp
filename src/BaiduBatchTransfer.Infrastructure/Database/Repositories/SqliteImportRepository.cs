using BaiduBatchTransfer.Core.Abstractions;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace BaiduBatchTransfer.Infrastructure.Database.Repositories;

public sealed class SqliteImportRepository : SqliteRepositoryBase, IImportRepository
{
    internal SqliteImportRepository(SqliteConnection connection, SqliteTransaction transaction) : base(connection, transaction) { }

    public async Task AddSessionAsync(ImportSessionRecord s, CancellationToken cancellationToken = default)
    {
        await using var c = Command("INSERT INTO ImportSessions(Id,SourceType,SourceName,Status,TotalRows,ReadyRows,InvalidRows,DuplicateRows,ExcludedRows,PrecheckAccountId,CreatedBatchJobId,AppendToBatchJobId,CreatedAt,UpdatedAt,ColumnMappingFingerprint,SourceContentFingerprint,HeaderRowNumber) VALUES($id,$type,$name,$status,$total,$ready,$invalid,$duplicate,$excluded,$precheck,$createdJob,$appendJob,$created,$updated,$mapping,$content,$headerRow);");
        Add(c,"$id",Id(s.Id)); Add(c,"$type",s.SourceType); Add(c,"$name",s.SourceName); Add(c,"$status",s.Status); Add(c,"$total",s.TotalRows);
        Add(c,"$ready",s.ReadyRows); Add(c,"$invalid",s.InvalidRows); Add(c,"$duplicate",s.DuplicateRows); Add(c,"$excluded",s.ExcludedRows);
        Add(c,"$precheck",NullableId(s.PrecheckAccountId)); Add(c,"$createdJob",NullableId(s.CreatedBatchJobId)); Add(c,"$appendJob",NullableId(s.AppendToBatchJobId));
        Add(c,"$created",Time(s.CreatedAt)); Add(c,"$updated",Time(s.UpdatedAt)); Add(c,"$mapping",s.ColumnMappingFingerprint);
        Add(c,"$content",s.SourceContentFingerprint); Add(c,"$headerRow",s.HeaderRowNumber); await c.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImportSessionRecord?> GetSessionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var c = Command("SELECT Id,SourceType,SourceName,Status,TotalRows,ReadyRows,InvalidRows,DuplicateRows,ExcludedRows,PrecheckAccountId,CreatedBatchJobId,AppendToBatchJobId,CreatedAt,UpdatedAt,ColumnMappingFingerprint,SourceContentFingerprint,HeaderRowNumber FROM ImportSessions WHERE Id=$id;"); Add(c,"$id",Id(id));
        await using var r = await c.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await r.ReadAsync(cancellationToken).ConfigureAwait(false) ? new(Guid.Parse(r.GetString(0)),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt32(4),r.GetInt32(5),r.GetInt32(6),r.GetInt32(7),r.GetInt32(8),ReadNullableId(r,9),ReadNullableId(r,10),ReadNullableId(r,11),ReadTime(r,12),ReadTime(r,13),r.IsDBNull(14)?null:r.GetString(14),r.IsDBNull(15)?null:r.GetString(15),r.IsDBNull(16)?null:r.GetInt32(16)) : null;
    }

    public async Task<ImportSessionRecord?> GetLatestResumableExcelSessionAsync(
        CancellationToken cancellationToken = default)
    {
        await using var c = Command("""
            SELECT Id,SourceType,SourceName,Status,TotalRows,ReadyRows,InvalidRows,DuplicateRows,ExcludedRows,
                   PrecheckAccountId,CreatedBatchJobId,AppendToBatchJobId,CreatedAt,UpdatedAt,
                   ColumnMappingFingerprint,SourceContentFingerprint,HeaderRowNumber
            FROM ImportSessions s
            WHERE s.SourceType='Excel' AND s.Status='Parsing'
              AND length(s.ColumnMappingFingerprint)=64
              AND s.ColumnMappingFingerprint NOT GLOB '*[^0-9a-f]*'
              AND length(s.SourceContentFingerprint)=64
              AND s.SourceContentFingerprint NOT GLOB '*[^0-9a-f]*'
              AND (SELECT COUNT(*) FROM ImportColumnDefinitions d WHERE d.ImportSessionId=s.Id) > 0
              AND (SELECT MIN(d.ColumnOrdinal) FROM ImportColumnDefinitions d WHERE d.ImportSessionId=s.Id)=0
              AND (SELECT MAX(d.ColumnOrdinal)+1 FROM ImportColumnDefinitions d WHERE d.ImportSessionId=s.Id)=
                  (SELECT COUNT(*) FROM ImportColumnDefinitions d WHERE d.ImportSessionId=s.Id)
              AND (SELECT COUNT(*) FROM ImportColumnDefinitions d
                   WHERE d.ImportSessionId=s.Id AND d.MappedRole='ShareUrl' AND d.IsRequired=1)=1
            ORDER BY s.UpdatedAt DESC,s.CreatedAt DESC,s.Id DESC
            LIMIT 1;
            """);
        await using var r = await c.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await r.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(Guid.Parse(r.GetString(0)),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt32(4),r.GetInt32(5),r.GetInt32(6),r.GetInt32(7),r.GetInt32(8),ReadNullableId(r,9),ReadNullableId(r,10),ReadNullableId(r,11),ReadTime(r,12),ReadTime(r,13),r.IsDBNull(14)?null:r.GetString(14),r.IsDBNull(15)?null:r.GetString(15),r.IsDBNull(16)?null:r.GetInt32(16))
            : null;
    }

    public async Task UpdateSessionAsync(ImportSessionRecord s, CancellationToken cancellationToken = default)
    {
        await using var c = Command("UPDATE ImportSessions SET Status=$status,TotalRows=$total,ReadyRows=$ready,InvalidRows=$invalid,DuplicateRows=$duplicate,ExcludedRows=$excluded,PrecheckAccountId=$precheck,CreatedBatchJobId=$createdJob,ColumnMappingFingerprint=$mapping,SourceContentFingerprint=$content,HeaderRowNumber=$headerRow,UpdatedAt=$updated WHERE Id=$id;");
        Add(c,"$id",Id(s.Id)); Add(c,"$status",s.Status); Add(c,"$total",s.TotalRows); Add(c,"$ready",s.ReadyRows);
        Add(c,"$invalid",s.InvalidRows); Add(c,"$duplicate",s.DuplicateRows); Add(c,"$excluded",s.ExcludedRows);
        Add(c,"$precheck",NullableId(s.PrecheckAccountId)); Add(c,"$createdJob",NullableId(s.CreatedBatchJobId)); Add(c,"$mapping",s.ColumnMappingFingerprint);
        Add(c,"$content",s.SourceContentFingerprint); Add(c,"$headerRow",s.HeaderRowNumber); Add(c,"$updated",Time(s.UpdatedAt));
        if (await c.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) throw new InvalidOperationException("The import session does not exist.");
    }

    public async Task AddRowsAsync(IEnumerable<StagedImportRowRecord> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var r in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var c = Command("INSERT INTO StagedImportRows(Id,ImportSessionId,SourceRowNumber,ShareUrl,NormalizedShareId,ExtractionCode,TargetPath,CreateFolderName,RequestedAccountName,MappedAccountId,Note,Disposition,DuplicateOfStagedRowId,ValidationMessage,PrecheckStatus,PrecheckErrorCode,PrecheckedAt,UpdatedAt) VALUES($id,$session,$row,$url,$normalized,$code,$target,$folder,$requested,$mapped,$note,$disposition,$duplicate,$validation,$precheck,$error,$prechecked,$updated);");
            Add(c,"$id",Id(r.Id)); Add(c,"$session",Id(r.ImportSessionId)); Add(c,"$row",r.SourceRowNumber); Add(c,"$url",r.ShareUrl); Add(c,"$normalized",r.NormalizedShareId); Add(c,"$code",r.ExtractionCode);
            Add(c,"$target",r.TargetPath); Add(c,"$folder",r.CreateFolderName); Add(c,"$requested",r.RequestedAccountName); Add(c,"$mapped",NullableId(r.MappedAccountId)); Add(c,"$note",r.Note);
            Add(c,"$disposition",r.Disposition); Add(c,"$duplicate",NullableId(r.DuplicateOfStagedRowId)); Add(c,"$validation",r.ValidationMessage); Add(c,"$precheck",r.PrecheckStatus); Add(c,"$error",r.PrecheckErrorCode);
            Add(c,"$prechecked",NullableTime(r.PrecheckedAt)); Add(c,"$updated",Time(r.UpdatedAt)); await c.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UpdateRowsAsync(IEnumerable<StagedImportRowRecord> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var r in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var c = Command("UPDATE StagedImportRows SET ShareUrl=$url,NormalizedShareId=$normalized,ExtractionCode=$code,TargetPath=$target,CreateFolderName=$folder,RequestedAccountName=$requested,MappedAccountId=$mapped,Note=$note,Disposition=$disposition,DuplicateOfStagedRowId=$duplicate,ValidationMessage=$validation,PrecheckStatus=$precheck,PrecheckErrorCode=$error,PrecheckedAt=$prechecked,UpdatedAt=$updated WHERE Id=$id;");
            Add(c,"$id",Id(r.Id)); Add(c,"$url",r.ShareUrl); Add(c,"$normalized",r.NormalizedShareId); Add(c,"$code",r.ExtractionCode); Add(c,"$target",r.TargetPath); Add(c,"$folder",r.CreateFolderName);
            Add(c,"$requested",r.RequestedAccountName); Add(c,"$mapped",NullableId(r.MappedAccountId)); Add(c,"$note",r.Note); Add(c,"$disposition",r.Disposition);
            Add(c,"$duplicate",NullableId(r.DuplicateOfStagedRowId)); Add(c,"$validation",r.ValidationMessage); Add(c,"$precheck",r.PrecheckStatus); Add(c,"$error",r.PrecheckErrorCode);
            Add(c,"$prechecked",NullableTime(r.PrecheckedAt)); Add(c,"$updated",Time(r.UpdatedAt));
            if (await c.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) throw new InvalidOperationException("The staging row does not exist.");
        }
    }

    public async Task<StagedImportRowRecord?> GetFirstByNormalizedShareIdAsync(Guid sessionId, string normalizedShareId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedShareId);
        await using var c = Command("SELECT Id,ImportSessionId,SourceRowNumber,ShareUrl,NormalizedShareId,ExtractionCode,TargetPath,CreateFolderName,RequestedAccountName,MappedAccountId,Note,Disposition,DuplicateOfStagedRowId,ValidationMessage,PrecheckStatus,PrecheckErrorCode,PrecheckedAt,UpdatedAt FROM StagedImportRows WHERE ImportSessionId=$session AND NormalizedShareId=$normalized AND Disposition<>'Excluded' ORDER BY SourceRowNumber,Id LIMIT 1;");
        Add(c,"$session",Id(sessionId)); Add(c,"$normalized",normalizedShareId);
        await using var r = await c.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await r.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRow(r) : null;
    }

    public async Task<StagedImportRowRecord?> GetRowByIdAsync(Guid rowId, CancellationToken cancellationToken = default)
    {
        await using var c = Command("SELECT Id,ImportSessionId,SourceRowNumber,ShareUrl,NormalizedShareId,ExtractionCode,TargetPath,CreateFolderName,RequestedAccountName,MappedAccountId,Note,Disposition,DuplicateOfStagedRowId,ValidationMessage,PrecheckStatus,PrecheckErrorCode,PrecheckedAt,UpdatedAt FROM StagedImportRows WHERE Id=$id;");
        Add(c,"$id",Id(rowId));
        await using var r = await c.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await r.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRow(r) : null;
    }

    public async Task<int> GetLastSourceRowNumberAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var c = Command("SELECT COALESCE(MAX(r.SourceRowNumber),0) FROM StagedImportRows r JOIN StagedImportRowPayloads p ON p.StagedImportRowId=r.Id WHERE r.ImportSessionId=$session;");
        Add(c,"$session",Id(sessionId));
        return Convert.ToInt32(await c.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<StagedImportRowRecord>> GetRowsAfterSourceRowAsync(Guid sessionId, string disposition, int afterSourceRowNumber, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(disposition);
        if (afterSourceRowNumber < 0) throw new ArgumentOutOfRangeException(nameof(afterSourceRowNumber));
        if (pageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        await using var c = Command("SELECT Id,ImportSessionId,SourceRowNumber,ShareUrl,NormalizedShareId,ExtractionCode,TargetPath,CreateFolderName,RequestedAccountName,MappedAccountId,Note,Disposition,DuplicateOfStagedRowId,ValidationMessage,PrecheckStatus,PrecheckErrorCode,PrecheckedAt,UpdatedAt FROM StagedImportRows WHERE ImportSessionId=$session AND Disposition=$disposition AND SourceRowNumber>$after ORDER BY SourceRowNumber,Id LIMIT $limit;");
        Add(c,"$session",Id(sessionId)); Add(c,"$disposition",disposition); Add(c,"$after",afterSourceRowNumber); Add(c,"$limit",pageSize);
        var items = new List<StagedImportRowRecord>();
        await using var r = await c.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false)) items.Add(ReadRow(r));
        return items;
    }

    public async Task<IReadOnlyList<StagedImportRowRecord>> GetRowsByNormalizedShareIdAfterSourceRowAsync(Guid sessionId, string normalizedShareId, int afterSourceRowNumber, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedShareId);
        if (afterSourceRowNumber < 0) throw new ArgumentOutOfRangeException(nameof(afterSourceRowNumber));
        if (pageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        await using var c = Command("SELECT Id,ImportSessionId,SourceRowNumber,ShareUrl,NormalizedShareId,ExtractionCode,TargetPath,CreateFolderName,RequestedAccountName,MappedAccountId,Note,Disposition,DuplicateOfStagedRowId,ValidationMessage,PrecheckStatus,PrecheckErrorCode,PrecheckedAt,UpdatedAt FROM StagedImportRows WHERE ImportSessionId=$session AND NormalizedShareId=$normalized AND Disposition<>'Excluded' AND SourceRowNumber>$after ORDER BY SourceRowNumber,Id LIMIT $limit;");
        Add(c,"$session",Id(sessionId)); Add(c,"$normalized",normalizedShareId); Add(c,"$after",afterSourceRowNumber); Add(c,"$limit",pageSize);
        var items = new List<StagedImportRowRecord>();
        await using var r = await c.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false)) items.Add(ReadRow(r));
        return items;
    }

    public async Task<Page<StagedImportRowRecord>> GetRowsPageAsync(StagedImportRowQuery query, CancellationToken cancellationToken = default)
    {
        query.Validate(); cancellationToken.ThrowIfCancellationRequested();
        var filter = query.Disposition is null ? string.Empty : " AND Disposition=$disposition";
        await using var count = Command("SELECT COUNT(*) FROM StagedImportRows WHERE ImportSessionId=$session" + filter + ";"); Add(count,"$session",Id(query.ImportSessionId)); if (query.Disposition is not null) Add(count,"$disposition",query.Disposition);
        var total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        await using var c = Command("SELECT Id,ImportSessionId,SourceRowNumber,ShareUrl,NormalizedShareId,ExtractionCode,TargetPath,CreateFolderName,RequestedAccountName,MappedAccountId,Note,Disposition,DuplicateOfStagedRowId,ValidationMessage,PrecheckStatus,PrecheckErrorCode,PrecheckedAt,UpdatedAt FROM StagedImportRows WHERE ImportSessionId=$session" + filter + " ORDER BY SourceRowNumber,Id LIMIT $limit OFFSET $offset;");
        Add(c,"$session",Id(query.ImportSessionId)); if (query.Disposition is not null) Add(c,"$disposition",query.Disposition); Add(c,"$limit",query.PageSize); Add(c,"$offset",query.Offset);
        var items = new List<StagedImportRowRecord>(); await using var r = await c.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false)) items.Add(ReadRow(r));
        return new(items,total,query.PageNumber,query.PageSize);
    }

    public async Task<KeysetPage<StagedImportRowRecord>> GetRowsKeysetPageAsync(
        StagedImportKeysetQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var dispositionFilter = query.Disposition is null ? string.Empty : " AND Disposition=$disposition";
        var precheckFilter = query.PrecheckStatus switch
        {
            null => string.Empty,
            "Pending" => " AND PrecheckStatus IS NULL",
            _ => " AND PrecheckStatus=$precheck",
        };
        var filter = dispositionFilter + precheckFilter;
        long? total = null;
        if (query.IncludeTotalCount)
        {
            await using var count = Command(
                "SELECT COUNT(*) FROM StagedImportRows WHERE ImportSessionId=$session" + filter + ";");
            Add(count, "$session", Id(query.ImportSessionId));
            if (query.Disposition is not null) Add(count, "$disposition", query.Disposition);
            if (query.PrecheckStatus is not null and not "Pending") Add(count, "$precheck", query.PrecheckStatus);
            total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }

        await using var command = Command(
            "SELECT Id,ImportSessionId,SourceRowNumber,ShareUrl,NormalizedShareId,ExtractionCode,TargetPath,CreateFolderName,RequestedAccountName,MappedAccountId,Note,Disposition,DuplicateOfStagedRowId,ValidationMessage,PrecheckStatus,PrecheckErrorCode,PrecheckedAt,UpdatedAt " +
            "FROM StagedImportRows WHERE ImportSessionId=$session" + filter +
            " AND SourceRowNumber>$after ORDER BY SourceRowNumber,Id LIMIT $limit;");
        Add(command, "$session", Id(query.ImportSessionId));
        if (query.Disposition is not null) Add(command, "$disposition", query.Disposition);
        if (query.PrecheckStatus is not null and not "Pending") Add(command, "$precheck", query.PrecheckStatus);
        Add(command, "$after", query.AfterSourceRowNumber);
        Add(command, "$limit", checked(query.PageSize + 1));
        var items = new List<StagedImportRowRecord>(query.PageSize + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) items.Add(ReadRow(reader));
        var hasMore = items.Count > query.PageSize;
        if (hasMore) items.RemoveAt(items.Count - 1);
        return new(items, total, hasMore ? items[^1].SourceRowNumber : null);
    }

    public async Task<IReadOnlyList<string>> GetDuplicateNormalizedShareIdsPageAsync(
        Guid sessionId,
        string? afterNormalizedShareId,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("An import session is required.", nameof(sessionId));
        if (pageSize is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        await using var command = Command("""
            SELECT NormalizedShareId
            FROM StagedImportRows
            WHERE ImportSessionId=$session AND NormalizedShareId IS NOT NULL
              AND Disposition IN ('Ready','Duplicate','Conflict')
              AND ($after IS NULL OR NormalizedShareId>$after)
            GROUP BY NormalizedShareId
            HAVING COUNT(*)>1
            ORDER BY NormalizedShareId
            LIMIT $limit;
            """);
        Add(command, "$session", Id(sessionId));
        Add(command, "$after", afterNormalizedShareId);
        Add(command, "$limit", pageSize);
        var values = new List<string>(pageSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) values.Add(reader.GetString(0));
        return values;
    }

    public async Task<KeysetPage<StagedImportRowRecord>> GetNonDuplicateClassifiableRowsKeysetPageAsync(
        Guid sessionId,
        long afterSourceRowNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("An import session is required.", nameof(sessionId));
        if (afterSourceRowNumber < 0) throw new ArgumentOutOfRangeException(nameof(afterSourceRowNumber));
        if (pageSize is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        await using var command = Command("""
            SELECT Id,ImportSessionId,SourceRowNumber,ShareUrl,NormalizedShareId,ExtractionCode,TargetPath,
              CreateFolderName,RequestedAccountName,MappedAccountId,Note,Disposition,DuplicateOfStagedRowId,
              ValidationMessage,PrecheckStatus,PrecheckErrorCode,PrecheckedAt,UpdatedAt
            FROM StagedImportRows AS row
            WHERE row.ImportSessionId=$session
              AND row.SourceRowNumber>$after
              AND row.NormalizedShareId IS NOT NULL
              AND row.Disposition IN ('Ready','Duplicate','Conflict')
              AND NOT EXISTS (
                SELECT 1 FROM StagedImportRows AS other
                WHERE other.ImportSessionId=row.ImportSessionId
                  AND other.NormalizedShareId=row.NormalizedShareId
                  AND other.Disposition IN ('Ready','Duplicate','Conflict')
                  AND other.Id<>row.Id
              )
            ORDER BY row.SourceRowNumber,row.Id
            LIMIT $limit;
            """);
        Add(command, "$session", Id(sessionId)); Add(command, "$after", afterSourceRowNumber);
        Add(command, "$limit", checked(pageSize + 1));
        var items = new List<StagedImportRowRecord>(pageSize + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) items.Add(ReadRow(reader));
        var hasMore = items.Count > pageSize;
        if (hasMore) items.RemoveAt(items.Count - 1);
        return new(items, null, hasMore ? items[^1].SourceRowNumber : null);
    }

    public async Task<Page<RequestedAccountNameGroupRecord>> GetRequestedAccountNameGroupsPageAsync(
        Guid sessionId,
        PageQuery query,
        CancellationToken cancellationToken = default)
    {
        query.Validate();
        await using var count = Command("""
            SELECT COUNT(*) FROM (
              SELECT RequestedAccountName FROM StagedImportRows
              WHERE ImportSessionId=$session AND RequestedAccountName IS NOT NULL
              GROUP BY RequestedAccountName
            );
            """);
        Add(count, "$session", Id(sessionId));
        var total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        await using var command = Command("""
            SELECT RequestedAccountName,COUNT(*) FROM StagedImportRows
            WHERE ImportSessionId=$session AND RequestedAccountName IS NOT NULL
            GROUP BY RequestedAccountName
            ORDER BY RequestedAccountName
            LIMIT $limit OFFSET $offset;
            """);
        Add(command, "$session", Id(sessionId)); Add(command, "$limit", query.PageSize); Add(command, "$offset", query.Offset);
        var items = new List<RequestedAccountNameGroupRecord>(query.PageSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            items.Add(new(reader.GetString(0), reader.GetInt64(1)));
        return new(items, total, query.PageNumber, query.PageSize);
    }

    public async Task<ImportDispositionCounts> GetDispositionCountsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var command = Command("""
            SELECT COUNT(*),
              SUM(CASE WHEN Disposition='Ready' THEN 1 ELSE 0 END),
              SUM(CASE WHEN Disposition='Invalid' THEN 1 ELSE 0 END),
              SUM(CASE WHEN Disposition='Duplicate' THEN 1 ELSE 0 END),
              SUM(CASE WHEN Disposition='Conflict' THEN 1 ELSE 0 END),
              SUM(CASE WHEN Disposition='NeedsResolution' THEN 1 ELSE 0 END),
              SUM(CASE WHEN Disposition='Excluded' THEN 1 ELSE 0 END)
            FROM StagedImportRows WHERE ImportSessionId=$session;
            """);
        Add(command, "$session", Id(sessionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        static int Value(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? 0 : reader.GetInt32(index);
        return new(Value(reader, 0), Value(reader, 1), Value(reader, 2), Value(reader, 3),
            Value(reader, 4), Value(reader, 5), Value(reader, 6));
    }

    public async Task<bool> TryFinalizeBatchCreationAsync(
        ImportSessionRecord expectedSession,
        Guid batchJobId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedSession);
        if (batchJobId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(batchJobId));
        await using var command = Command("""
            UPDATE ImportSessions
            SET Status='BatchCreated',CreatedBatchJobId=$batch,UpdatedAt=$updated
            WHERE Id=$id AND Status=$status AND CreatedBatchJobId IS NULL AND AppendToBatchJobId IS NULL
              AND UpdatedAt=$expectedUpdated AND TotalRows=$total AND ReadyRows=$ready
              AND InvalidRows=$invalid AND DuplicateRows=$duplicate AND ExcludedRows=$excluded
              AND ColumnMappingFingerprint IS $mapping;
            """);
        Add(command,"$batch",Id(batchJobId)); Add(command,"$updated",Time(updatedAt));
        Add(command,"$id",Id(expectedSession.Id)); Add(command,"$status",expectedSession.Status);
        Add(command,"$expectedUpdated",Time(expectedSession.UpdatedAt)); Add(command,"$total",expectedSession.TotalRows);
        Add(command,"$ready",expectedSession.ReadyRows); Add(command,"$invalid",expectedSession.InvalidRows);
        Add(command,"$duplicate",expectedSession.DuplicateRows); Add(command,"$excluded",expectedSession.ExcludedRows);
        Add(command,"$mapping",expectedSession.ColumnMappingFingerprint);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task SetColumnDefinitionsAsync(
        Guid sessionId,
        IEnumerable<ImportColumnDefinitionRecord> definitions,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("An import session is required.", nameof(sessionId));
        ArgumentNullException.ThrowIfNull(definitions);
        var values = definitions.ToArray();
        if (values.Length == 0 || values.Count(value => value.MappedRole == "ShareUrl") != 1)
            throw new ArgumentException("Exactly one ShareUrl column is required.", nameof(definitions));
        if (values.Any(value => value.ImportSessionId != sessionId || value.ColumnOrdinal < 0))
            throw new ArgumentException("Every column definition must belong to the import session and use a nonnegative ordinal.", nameof(definitions));
        var allowed = new HashSet<string>(
            ["ShareUrl", "Title", "ExtractionCode", "TargetPath", "OuterFolderName", "RequestedAccountName", "Note", "Preserve"],
            StringComparer.Ordinal);
        if (values.Any(value => !allowed.Contains(value.MappedRole) || value.IsRequired != (value.MappedRole == "ShareUrl")) ||
            values.Select(value => value.ColumnOrdinal).Distinct().Count() != values.Length ||
            values.Where(value => value.MappedRole != "Preserve").GroupBy(value => value.MappedRole).Any(group => group.Count() > 1))
            throw new ArgumentException("The import column definition set is invalid.", nameof(definitions));

        await using (var delete = Command("DELETE FROM ImportColumnDefinitions WHERE ImportSessionId=$session;"))
        {
            Add(delete, "$session", Id(sessionId));
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var insert = Command("INSERT INTO ImportColumnDefinitions(ImportSessionId,ColumnOrdinal,SourceHeader,MappedRole,IsRequired) VALUES($session,$ordinal,$header,$role,$required);");
        foreach (var value in values.OrderBy(value => value.ColumnOrdinal))
        {
            insert.Parameters.Clear();
            Add(insert, "$session", Id(sessionId)); Add(insert, "$ordinal", value.ColumnOrdinal);
            Add(insert, "$header", value.SourceHeader); Add(insert, "$role", value.MappedRole);
            Add(insert, "$required", value.IsRequired ? 1 : 0);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<ImportColumnDefinitionRecord>> GetColumnDefinitionsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var command = Command("SELECT ImportSessionId,ColumnOrdinal,SourceHeader,MappedRole,IsRequired FROM ImportColumnDefinitions WHERE ImportSessionId=$session ORDER BY ColumnOrdinal;");
        Add(command, "$session", Id(sessionId));
        var values = new List<ImportColumnDefinitionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            values.Add(new(Guid.Parse(reader.GetString(0)), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetInt64(4) != 0));
        return values;
    }

    public async Task AddRowPayloadsAsync(
        IEnumerable<StagedImportRowPayloadRecord> payloads,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        await using var command = Command("INSERT INTO StagedImportRowPayloads(StagedImportRowId,SourceTitle,ResolvedOutputTitle,OriginalShareUrl,RawValuesJson) VALUES($row,$source,$resolved,$url,$raw);");
        foreach (var payload in payloads)
        {
            using var document = JsonDocument.Parse(payload.RawValuesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.EnumerateArray().Any(value => value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
                throw new ArgumentException("Raw import values must be a positional JSON array.", nameof(payloads));
            command.Parameters.Clear();
            Add(command, "$row", Id(payload.StagedImportRowId)); Add(command, "$source", payload.SourceTitle);
            Add(command, "$resolved", payload.ResolvedOutputTitle); Add(command, "$url", payload.OriginalShareUrl);
            Add(command, "$raw", payload.RawValuesJson);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<StagedImportRowPayloadRecord?> GetRowPayloadAsync(
        Guid stagedImportRowId,
        CancellationToken cancellationToken = default)
    {
        await using var command = Command("SELECT StagedImportRowId,SourceTitle,ResolvedOutputTitle,OriginalShareUrl,RawValuesJson FROM StagedImportRowPayloads WHERE StagedImportRowId=$row;");
        Add(command, "$row", Id(stagedImportRowId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(Guid.Parse(reader.GetString(0)), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4))
            : null;
    }

    public async Task<IReadOnlyList<StagedImportRowPayloadRecord>> GetRowPayloadsAsync(
        IEnumerable<Guid> stagedImportRowIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stagedImportRowIds);
        var result = new List<StagedImportRowPayloadRecord>();
        await using var command = Command("SELECT StagedImportRowId,SourceTitle,ResolvedOutputTitle,OriginalShareUrl,RawValuesJson FROM StagedImportRowPayloads WHERE StagedImportRowId=$row;");
        var row = command.Parameters.Add("$row", SqliteType.Text);
        command.Prepare();
        foreach (var id in stagedImportRowIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            row.Value = Id(id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                result.Add(new(Guid.Parse(reader.GetString(0)), reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }
        return result;
    }

    public async Task UpdateRowPayloadAsync(
        StagedImportRowPayloadRecord payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var document = JsonDocument.Parse(payload.RawValuesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array ||
            document.RootElement.EnumerateArray().Any(value => value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
            throw new ArgumentException("Raw import values must be a positional JSON array.", nameof(payload));
        await using var command = Command("UPDATE StagedImportRowPayloads SET SourceTitle=$source,ResolvedOutputTitle=$resolved,OriginalShareUrl=$url,RawValuesJson=$raw WHERE StagedImportRowId=$row;");
        Add(command, "$source", payload.SourceTitle); Add(command, "$resolved", payload.ResolvedOutputTitle);
        Add(command, "$url", payload.OriginalShareUrl); Add(command, "$raw", payload.RawValuesJson);
        Add(command, "$row", Id(payload.StagedImportRowId));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("The staged import row payload no longer exists.");
    }

    private static StagedImportRowRecord ReadRow(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)),Guid.Parse(r.GetString(1)),r.GetInt32(2),r.GetString(3),r.IsDBNull(4)?null:r.GetString(4),r.IsDBNull(5)?null:r.GetString(5),r.IsDBNull(6)?null:r.GetString(6),r.IsDBNull(7)?null:r.GetString(7),r.IsDBNull(8)?null:r.GetString(8),ReadNullableId(r,9),r.IsDBNull(10)?null:r.GetString(10),r.GetString(11),ReadNullableId(r,12),r.IsDBNull(13)?null:r.GetString(13),r.IsDBNull(14)?null:r.GetString(14),r.IsDBNull(15)?null:r.GetString(15),ReadNullableTime(r,16),ReadTime(r,17));
}
