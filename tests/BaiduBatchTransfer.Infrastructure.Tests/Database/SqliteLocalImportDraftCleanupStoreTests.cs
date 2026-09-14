using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Tests.Database;

public sealed class SqliteLocalImportDraftCleanupStoreTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "import-draft-cleanup-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DeletesDraftAndReportsExactStagedRowCount()
    {
        var graph = await CreateAsync();
        await SeedDraftAsync(graph.Factory, graph.SessionId, stagedRowCount: 2);

        var result = await graph.Store.DeleteAsync(graph.SessionId);

        Assert.Equal(LocalImportDraftCleanupDisposition.Deleted, result.Disposition);
        Assert.Equal(2, result.DeletedStagedRowCount);
        await AssertCountsAsync(graph.Factory, graph.SessionId, 0, 0, 0, 0);
        await AssertNoForeignKeyIssuesAsync(graph.Factory);
    }

    [Fact]
    public async Task MissingDraftIsAlreadyAbsent()
    {
        var graph = await CreateAsync();

        var result = await graph.Store.DeleteAsync(graph.SessionId);

        Assert.Equal(new LocalImportDraftCleanupResult(
            LocalImportDraftCleanupDisposition.AlreadyAbsent, 0), result);
    }

    public static IEnumerable<object[]> BatchRelationships()
    {
        yield return ["ImportSessions.CreatedBatchJobId"];
        yield return ["ImportSessions.AppendToBatchJobId"];
        yield return ["BatchJobs.ImportSessionId"];
        yield return ["BatchCreationReceipts.ImportSessionId"];
        yield return ["ResourceItems.StagedImportRowId"];
    }

    [Theory]
    [MemberData(nameof(BatchRelationships))]
    public async Task EachBatchRelationshipBlocksDeletionWithoutChangingDraft(string relationship)
    {
        var graph = await CreateAsync();
        var rowId = await SeedDraftAsync(graph.Factory, graph.SessionId, stagedRowCount: 1);
        await AddBatchRelationshipAsync(graph.Factory, graph.SessionId, rowId, relationship);

        var result = await graph.Store.DeleteAsync(graph.SessionId);

        Assert.Equal(LocalImportDraftCleanupDisposition.BatchConflict, result.Disposition);
        Assert.Equal(0, result.DeletedStagedRowCount);
        await AssertCountsAsync(graph.Factory, graph.SessionId, 1, 1, 1, 1);
        await AssertNoForeignKeyIssuesAsync(graph.Factory);
    }

    [Fact]
    public async Task CrossSessionDuplicateReferenceBlocksDeletionWithoutChangingEitherDraft()
    {
        var graph = await CreateAsync();
        var targetRowId = await SeedDraftAsync(graph.Factory, graph.SessionId, stagedRowCount: 1);
        var otherSessionId = Guid.NewGuid();
        var otherRowId = await SeedDraftAsync(graph.Factory, otherSessionId, stagedRowCount: 1);
        await ExecuteAsync(graph.Factory,
            "UPDATE StagedImportRows SET DuplicateOfStagedRowId=$target WHERE Id=$other;",
            ("$target", targetRowId), ("$other", otherRowId));

        var result = await graph.Store.DeleteAsync(graph.SessionId);

        Assert.Equal(LocalImportDraftCleanupDisposition.LocalDependencyConflict, result.Disposition);
        await AssertCountsAsync(graph.Factory, graph.SessionId, 1, 1, 1, 1);
        await AssertCountsAsync(graph.Factory, otherSessionId, 1, 1, 1, 1);
        await AssertNoForeignKeyIssuesAsync(graph.Factory);
    }

    [Fact]
    public async Task CascadesDeleteColumnsAndPayloadsButPreserveOtherDraft()
    {
        var graph = await CreateAsync();
        var otherSessionId = Guid.NewGuid();
        await SeedDraftAsync(graph.Factory, graph.SessionId, stagedRowCount: 2);
        await SeedDraftAsync(graph.Factory, otherSessionId, stagedRowCount: 1);

        var result = await graph.Store.DeleteAsync(graph.SessionId);

        Assert.Equal(LocalImportDraftCleanupDisposition.Deleted, result.Disposition);
        await AssertCountsAsync(graph.Factory, graph.SessionId, 0, 0, 0, 0);
        await AssertCountsAsync(graph.Factory, otherSessionId, 1, 1, 1, 1);
        await AssertNoForeignKeyIssuesAsync(graph.Factory);
    }

    [Fact]
    public async Task AbortDuringSessionDeleteRollsBackExplicitStagedRowDeleteAndCascades()
    {
        var graph = await CreateAsync();
        await SeedDraftAsync(graph.Factory, graph.SessionId, stagedRowCount: 2);
        await ExecuteAsync(graph.Factory, """
            CREATE TRIGGER TestAbortImportSessionDelete
            BEFORE DELETE ON ImportSessions
            BEGIN
                SELECT RAISE(ABORT, 'test abort after staged delete');
            END;
            """);

        var error = await Assert.ThrowsAsync<SqliteException>(
            () => graph.Store.DeleteAsync(graph.SessionId));

        Assert.Contains("test abort after staged delete", error.Message, StringComparison.Ordinal);
        await AssertCountsAsync(graph.Factory, graph.SessionId, 1, 2, 1, 2);
        await AssertNoForeignKeyIssuesAsync(graph.Factory);
    }

    [Fact]
    public async Task SecondImmediateTransactionStartsBeforeFirstCommitsAndSeesAlreadyAbsent()
    {
        var graph = await CreateAsync();
        await SeedDraftAsync(graph.Factory, graph.SessionId, stagedRowCount: 3);
        var firstTransactionAcquired = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstTransaction = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondTransactionStarting = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var transactionStartCount = 0;
        var transactionAcquireCount = 0;
        var store = new SqliteLocalImportDraftCleanupStore(
            graph.Factory,
            () =>
            {
                if (Interlocked.Increment(ref transactionStartCount) == 2)
                    secondTransactionStarting.TrySetResult();
            },
            async cancellationToken =>
            {
                if (Interlocked.Increment(ref transactionAcquireCount) != 1) return;
                firstTransactionAcquired.TrySetResult();
                await releaseFirstTransaction.Task.WaitAsync(cancellationToken);
            });

        var first = Task.Run(() => store.DeleteAsync(graph.SessionId));
        await firstTransactionAcquired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = Task.Run(() => store.DeleteAsync(graph.SessionId));
        await secondTransactionStarting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref transactionAcquireCount));

        releaseFirstTransaction.TrySetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(2, transactionStartCount);
        Assert.Equal(2, transactionAcquireCount);
        Assert.Single(results, result =>
            result == new LocalImportDraftCleanupResult(LocalImportDraftCleanupDisposition.Deleted, 3));
        Assert.Single(results, result =>
            result == new LocalImportDraftCleanupResult(LocalImportDraftCleanupDisposition.AlreadyAbsent, 0));
        await AssertCountsAsync(graph.Factory, graph.SessionId, 0, 0, 0, 0);
    }

    [Fact]
    public void DeletedStagedRowCountConversionRejectsOverflow()
    {
        Assert.Equal(int.MaxValue,
            SqliteLocalImportDraftCleanupStore.ToDeletedStagedRowCount(int.MaxValue));
        Assert.Throws<OverflowException>(() =>
            SqliteLocalImportDraftCleanupStore.ToDeletedStagedRowCount((long)int.MaxValue + 1));
    }

    private async Task<Graph> CreateAsync()
    {
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, Guid.NewGuid().ToString("N") + ".db");
        var factory = new SqliteConnectionFactory(databasePath);
        await new DatabaseMigrator(
            factory,
            new DatabaseBackupService(factory, Path.Combine(root, "backups")),
            "test").MigrateAsync();
        return new(factory, new SqliteLocalImportDraftCleanupStore(factory), Guid.NewGuid());
    }

    private static async Task<string> SeedDraftAsync(
        SqliteConnectionFactory factory,
        Guid sessionId,
        int stagedRowCount)
    {
        await ExecuteAsync(factory, """
            INSERT INTO ImportSessions(Id,SourceType,SourceName,Status,CreatedAt,UpdatedAt)
            VALUES($session,'Text','fixture','Draft',$at,$at);
            INSERT INTO ImportColumnDefinitions(ImportSessionId,ColumnOrdinal,SourceHeader,MappedRole,IsRequired)
            VALUES($session,0,'链接','ShareUrl',1);
            """, ("$session", sessionId.ToString("D")), ("$at", "2026-09-14T00:00:00Z"));

        var firstRowId = string.Empty;
        for (var index = 1; index <= stagedRowCount; index++)
        {
            var rowId = Guid.NewGuid().ToString("D");
            if (index == 1) firstRowId = rowId;
            await ExecuteAsync(factory, """
                INSERT INTO StagedImportRows(Id,ImportSessionId,SourceRowNumber,ShareUrl,Disposition,UpdatedAt)
                VALUES($row,$session,$number,$url,'Ready',$at);
                INSERT INTO StagedImportRowPayloads(StagedImportRowId,OriginalShareUrl,RawValuesJson)
                VALUES($row,$url,'{}');
                """,
                ("$row", rowId),
                ("$session", sessionId.ToString("D")),
                ("$number", index),
                ("$url", $"https://pan.baidu.com/s/{index}"),
                ("$at", "2026-09-14T00:00:00Z"));
        }
        return firstRowId;
    }

    private static async Task AddBatchRelationshipAsync(
        SqliteConnectionFactory factory,
        Guid sessionId,
        string rowId,
        string relationship)
    {
        var batchId = Guid.NewGuid().ToString("D");
        var session = sessionId.ToString("D");
        var batchImportSession = relationship == "BatchJobs.ImportSessionId" ? session : null;
        await ExecuteAsync(factory, """
            INSERT INTO BatchJobs(Id,Name,SourceType,TransferMode,QuotaBasis,ShortagePolicy,ResourceCount,
              OperationCount,DefaultTargetPath,FolderCreationMode,DuplicatePolicy,ValidationMode,Status,CreatedAt,
              ImportSessionId,QueueOrder)
            VALUES($batch,'fixture','Text','Distribution','TaskCount','ProportionalReduction',0,0,'/','None',
              'Skip','Immediate','Queued',$at,$importSession,1);
            """,
            ("$batch", batchId), ("$at", "2026-09-14T00:00:00Z"),
            ("$importSession", batchImportSession));

        switch (relationship)
        {
            case "ImportSessions.CreatedBatchJobId":
                await ExecuteAsync(factory,
                    "UPDATE ImportSessions SET CreatedBatchJobId=$batch WHERE Id=$session;",
                    ("$batch", batchId), ("$session", session));
                break;
            case "ImportSessions.AppendToBatchJobId":
                await ExecuteAsync(factory,
                    "UPDATE ImportSessions SET AppendToBatchJobId=$batch WHERE Id=$session;",
                    ("$batch", batchId), ("$session", session));
                break;
            case "BatchCreationReceipts.ImportSessionId":
                await ExecuteAsync(factory, """
                    INSERT INTO BatchCreationReceipts(CommandFingerprint,ConfirmationTokenHash,SnapshotFingerprint,
                      ImportSessionId,BatchJobId,CreatedAt,ConfirmationSummaryJson)
                    VALUES($command,$token,$snapshot,$session,$batch,$at,'{}');
                    """,
                    ("$command", new string('a', 64)), ("$token", new string('b', 64)),
                    ("$snapshot", new string('c', 64)), ("$session", session),
                    ("$batch", batchId), ("$at", "2026-09-14T00:00:00Z"));
                break;
            case "ResourceItems.StagedImportRowId":
                await ExecuteAsync(factory, """
                    INSERT INTO ResourceItems(Id,BatchJobId,StagedImportRowId,CandidateSequence,ShareUrl,
                      NormalizedShareId,FolderCreationModeOverride,GlobalValidityStatus,CandidateStatus,CreatedAt,UpdatedAt)
                    VALUES($id,$batch,$row,1,'https://pan.baidu.com/s/1','1','None','Valid','Unallocated',$at,$at);
                    """,
                    ("$id", Guid.NewGuid().ToString("D")), ("$batch", batchId), ("$row", rowId),
                    ("$at", "2026-09-14T00:00:00Z"));
                break;
        }
    }

    private static async Task AssertCountsAsync(
        SqliteConnectionFactory factory,
        Guid sessionId,
        long sessions,
        long rows,
        long columns,
        long payloads)
    {
        var id = sessionId.ToString("D");
        Assert.Equal(sessions, await ScalarAsync(factory,
            "SELECT COUNT(*) FROM ImportSessions WHERE Id=$id;", ("$id", id)));
        Assert.Equal(rows, await ScalarAsync(factory,
            "SELECT COUNT(*) FROM StagedImportRows WHERE ImportSessionId=$id;", ("$id", id)));
        Assert.Equal(columns, await ScalarAsync(factory,
            "SELECT COUNT(*) FROM ImportColumnDefinitions WHERE ImportSessionId=$id;", ("$id", id)));
        Assert.Equal(payloads, await ScalarAsync(factory, """
            SELECT COUNT(*) FROM StagedImportRowPayloads payload
            JOIN StagedImportRows staged ON staged.Id=payload.StagedImportRowId
            WHERE staged.ImportSessionId=$id;
            """, ("$id", id)));
    }

    private static async Task AssertNoForeignKeyIssuesAsync(SqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.False(await reader.ReadAsync());
    }

    private static async Task<long> ScalarAsync(
        SqliteConnectionFactory factory,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(
        SqliteConnectionFactory factory,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed record Graph(
        SqliteConnectionFactory Factory,
        SqliteLocalImportDraftCleanupStore Store,
        Guid SessionId);
}
