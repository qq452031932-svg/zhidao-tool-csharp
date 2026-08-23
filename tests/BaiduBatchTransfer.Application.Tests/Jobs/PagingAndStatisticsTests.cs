using BaiduBatchTransfer.Application.Imports;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Excel;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using Xunit.Abstractions;

namespace BaiduBatchTransfer.Application.Tests.Jobs;

public sealed class PagingAndStatisticsTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper output = output;

    [Fact]
    public async Task OperationQueryProjectsStableChineseStatusLabels()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var statuses = new[]
        {
            (Raw: "Waiting", Chinese: "等待"),
            (Raw: "Reserved", Chinese: "处理中"),
            (Raw: "Preparing", Chinese: "处理中"),
            (Raw: "Submitting", Chinese: "处理中"),
            (Raw: "ResultPending", Chinese: "结果待确认"),
            (Raw: "Succeeded", Chinese: "成功"),
            (Raw: "Failed", Chinese: "失败"),
            (Raw: "Paused", Chinese: "暂停"),
            (Raw: "Cancelled", Chinese: "已取消"),
        };
        var resources = statuses.Select((_, index) => Resource(batch, index + 1)).ToArray();
        await db.SeedAsync(
            [BatchTestData.Account(account, "projection")],
            job: Job(batch) with { ResourceCount = statuses.Length, OperationCount = statuses.Length },
            resources: resources,
            operations: statuses.Select((status, index) =>
                Operation(batch, resources[index], account, index + 1, status.Raw, null)),
            allocations: [Allocation(batch, account, 1)]);

        var page = await new JobPageQuery(db.UnitOfWorkFactory).GetOperationsAsync(
            new(batch, account, null, null, null, 100));

        Assert.Equal(
            statuses.Select(status => (status.Raw, status.Chinese)),
            page.Items.Select(item => (item.Status, item.StatusText)));
    }

    [Fact]
    public async Task ImportPagingUsesStableCursorAndBoundsPageSize()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var importId = Guid.NewGuid();
        await db.SeedAsync(
            [],
            BatchTestData.ReadySession(importId, 4, 3, invalidRows: 1),
            [
                BatchTestData.ReadyRow(importId, 1),
                BatchTestData.ReadyRow(importId, 2),
                BatchTestData.ReadyRow(importId, 3),
                BatchTestData.ReadyRow(importId, 4) with { Disposition = "Invalid", ValidationMessage = "Safe validation failure." },
            ]);
        var query = new ImportPageQuery(db.UnitOfWorkFactory);

        var first = await query.ExecuteAsync(new ImportPageRequest(importId, "Ready", null, 2));
        var second = await query.ExecuteAsync(new ImportPageRequest(importId, "Ready", null, 2, first.NextSourceRowNumber));

        Assert.Equal([1, 2], first.Items.Select(item => item.SourceRowNumber));
        Assert.Equal([3], second.Items.Select(item => item.SourceRowNumber));
        Assert.Equal(3, first.TotalCount);
        Assert.Null(second.NextSourceRowNumber);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => query.ExecuteAsync(new ImportPageRequest(importId, null, null, 1_001)));
    }

    [Fact]
    public async Task JobPagingAndStatisticsKeepResourceAndOperationCountsSeparate()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var resources = Enumerable.Range(1, 3).Select(i => Resource(batch, i)).ToArray();
        var operations = new[]
        {
            Operation(batch, resources[0], accountA, 1, "Succeeded", "NewTransfer"),
            Operation(batch, resources[0], accountB, 2, "Waiting", null),
            Operation(batch, resources[1], accountA, 3, "Succeeded", "AlreadySatisfied"),
            Operation(batch, resources[1], accountB, 4, "Succeeded", "NewTransfer"),
            Operation(batch, resources[2], accountA, 5, "Failed", "Failed", "SAFE_FAILURE", "A safe failure."),
            Operation(batch, resources[2], accountB, 6, "Paused", null) with
            {
                TargetSlotState = "Reserved",
                TargetSlotReservedAt = BatchTestData.Now,
            },
        };
        await db.SeedAsync(
            [BatchTestData.Account(accountA, "A"), BatchTestData.Account(accountB, "B")],
            job: Job(batch),
            resources: resources,
            operations: operations,
            allocations:
            [
                Allocation(batch, accountA, 1),
                Allocation(batch, accountB, 2),
            ]);
        var pages = new JobPageQuery(db.UnitOfWorkFactory);

        var first = await pages.GetOperationsAsync(new OperationPageRequest(batch, accountA, "Succeeded", null, null, 1));
        var second = await pages.GetOperationsAsync(new OperationPageRequest(batch, accountA, "Succeeded", null, null, 1, first.NextCursor));
        var timeFiltered = await pages.GetOperationsAsync(new OperationPageRequest(
            batch,
            accountA,
            null,
            new DateTimeOffset(2026, 8, 19, 9, 2, 30, TimeSpan.FromHours(8)),
            new DateTimeOffset(2026, 8, 19, 9, 3, 30, TimeSpan.FromHours(8)),
            10));
        var resourcePage = await pages.GetResourcesAsync(new ResourcePageRequest(batch, null, null, 2));
        var statistics = await new BatchStatisticsQuery(db.UnitOfWorkFactory).ExecuteAsync(batch);

        Assert.Single(first.Items);
        Assert.Single(second.Items);
        Assert.NotEqual(first.Items[0].OperationId, second.Items[0].OperationId);
        Assert.Single(timeFiltered.Items);
        Assert.Equal(1, timeFiltered.Items[0].AllocationOrdinal);
        Assert.Equal(2, first.TotalCount);
        Assert.Equal(3, resourcePage.TotalCount);
        Assert.DoesNotContain("pan.baidu.com", resourcePage.Items[0].ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, statistics.ResourceCount);
        Assert.Equal(6, statistics.OperationCount);
        Assert.Collection(
            statistics.Accounts,
            a => { Assert.Equal(accountA, a.AccountId); Assert.Equal((1, 1, 1, 0, 0), (a.NewCount, a.ExistingCount, a.FailedCount, a.PendingCount, a.PausedCount)); },
            a => { Assert.Equal(accountB, a.AccountId); Assert.Equal((1, 0, 0, 1, 1), (a.NewCount, a.ExistingCount, a.FailedCount, a.PendingCount, a.PausedCount)); });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            pages.GetOperationsAsync(new(batch, null, null, null, null, 0)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            pages.GetResourcesAsync(new(batch, null, null, 1_001)));
    }

    [Fact]
    public async Task ResultExportStreamsKeysetPagesAndEscapesFormulaText()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resources = Enumerable.Range(1, 3).Select(i => Resource(batch, i)).ToArray();
        await db.SeedAsync(
            [BatchTestData.Account(account, "export")],
            job: Job(batch) with { OperationCount = 3 },
            resources: resources,
            operations: resources.Select((resource, i) =>
                Operation(batch, resource, account, i + 1, "Failed", "Failed", "SAFE_FAILURE", "=unsafe error")),
            allocations: [Allocation(batch, account, 1)]);
        var service = new ExportBatchResultsService(db.UnitOfWorkFactory);
        await using var output = new MemoryStream();

        var exported = await service.ExportAsync(
            new ExportBatchResultsRequest(batch, null, null, null, 1),
            output,
            new OpenXmlResultWriter());

        Assert.Equal(3, exported.ExportedRows);
        output.Position = 0;
        using var workbook = SpreadsheetDocument.Open(output, false);
        var worksheet = workbook.WorkbookPart!.WorksheetParts.Single().Worksheet
            ?? throw new InvalidDataException("The result worksheet is missing.");
        var rows = worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.Row>().ToArray();
        Assert.Equal(4, rows.Length);
        var headers = rows[0].Descendants<DocumentFormat.OpenXml.Spreadsheet.Text>().Select(text => text.Text).ToArray();
        Assert.Equal("源行号", headers[0]);
        var firstData = rows[1].Descendants<DocumentFormat.OpenXml.Spreadsheet.Text>().Select(text => text.Text).ToArray();
        Assert.Equal("'=unsafe note", firstData[4]);
        Assert.Equal("'=unsafe error", firstData[5]);
    }

    [Fact]
    public async Task ResultExportDoesNotReportARowUntilTheWriterConsumesIt()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1);
        await db.SeedAsync(
            [BatchTestData.Account(account, "progress")],
            job: Job(batch) with { ResourceCount = 1, OperationCount = 1 },
            resources: [resource],
            operations: [Operation(batch, resource, account, 1, "Succeeded", "NewTransfer")],
            allocations: [Allocation(batch, account, 1)]);
        var reports = new List<long>();

        await Assert.ThrowsAsync<IOException>(() => new ExportBatchResultsService(db.UnitOfWorkFactory).ExportAsync(
            new(batch, null, null, null, 10),
            Stream.Null,
            new RejectingResultWriter(),
            new InlineProgress<long>(reports.Add)));

        Assert.Empty(reports);
    }

    [Fact]
    public async Task TimeFilteringComparesLegacyOffsetTimestampsAsUtcInstants()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1);
        var operation = Operation(batch, resource, account, 1, "Waiting", null);
        await db.SeedAsync(
            [BatchTestData.Account(account, "offset")],
            job: Job(batch) with { ResourceCount = 1, OperationCount = 1 },
            resources: [resource],
            operations: [operation],
            allocations: [Allocation(batch, account, 1)]);
        await using (var connection = await db.ConnectionFactory.OpenConnectionAsync())
        {
            await ExecuteAsync(
                connection,
                "UPDATE TransferOperations SET UpdatedAt='2026-08-19T09:00:00.0000000+08:00' WHERE Id=$id;",
                ("$id", operation.Id.ToString("D")));
        }

        var page = await new JobPageQuery(db.UnitOfWorkFactory).GetOperationsAsync(new(
            batch,
            account,
            null,
            new DateTimeOffset(2026, 8, 19, 1, 30, 0, TimeSpan.Zero),
            null,
            10));

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task TwoHundredThousandResultsUseIndexedKeysetsAndBoundedExportMemory()
    {
        const int count = 200_000;
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        await db.SeedAsync(
            [BatchTestData.Account(account, "scale")],
            job: Job(batch) with { ResourceCount = count, OperationCount = count },
            allocations: [Allocation(batch, account, 1)]);
        await using (var connection = await db.ConnectionFactory.OpenConnectionAsync())
        {
            await ExecuteAsync(connection, $"""
                WITH RECURSIVE seq(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM seq WHERE x<{count})
                INSERT INTO ResourceItems(
                    Id,BatchJobId,CandidateSequence,ShareUrl,NormalizedShareId,FolderCreationModeOverride,
                    GlobalValidityStatus,CandidateStatus,CreatedAt,UpdatedAt)
                SELECT printf('10000000-0000-0000-0000-%012d',x),$batch,x,
                    'https://pan.baidu.com/s/'||x,'share-'||x,'Keep','Unknown','Expanded',$time,$time
                FROM seq;
                """, ("$batch", batch.ToString("D")), ("$time", BatchTestData.Now.ToString("O")));
            await ExecuteAsync(connection, $"""
                WITH RECURSIVE seq(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM seq WHERE x<{count})
                INSERT INTO TransferOperations(
                    Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,NormalizedTargetPath,
                    AssignedAccountId,AllocationSource,Status,OutcomeKind,AttemptCount,OperationFingerprint,
                    SubmitState,TargetSlotState,CreatedAt,UpdatedAt)
                SELECT printf('20000000-0000-0000-0000-%012d',x),
                    printf('10000000-0000-0000-0000-%012d',x),$batch,x,x,'/target','/target',$account,'Rule',
                    CASE x%4 WHEN 0 THEN 'Waiting' WHEN 3 THEN 'Failed' ELSE 'Succeeded' END,
                    CASE x%4 WHEN 1 THEN 'NewTransfer' WHEN 2 THEN 'AlreadySatisfied' WHEN 3 THEN 'Failed' ELSE NULL END,
                    0,'fp-'||x,'NotPrepared','Unoccupied',$time,$time
                FROM seq;
                """, ("$batch", batch.ToString("D")), ("$account", account.ToString("D")), ("$time", BatchTestData.Now.ToString("O")));

            var accountStatusPlan = await ExplainAsync(connection, """
                EXPLAIN QUERY PLAN
                SELECT o.Id FROM TransferOperations o INDEXED BY IX_TransferOperations_Result_Account_Keyset
                WHERE o.BatchJobId=$batch AND o.AssignedAccountId=$account AND o.Status=$status
                  AND o.AllocationOrdinal>$after
                ORDER BY o.AllocationOrdinal,o.SequenceNumber,o.Id LIMIT $limit;
                """,
                ("$batch", batch.ToString("D")), ("$account", account.ToString("D")),
                ("$status", "Succeeded"), ("$after", 100_000), ("$limit", 101));
            Assert.Contains(accountStatusPlan, line => line.Contains(
                "IX_TransferOperations_Result_Account_Keyset", StringComparison.Ordinal));

            var instantPlan = await ExplainAsync(connection, """
                EXPLAIN QUERY PLAN
                SELECT o.Id FROM TransferOperations o INDEXED BY IX_TransferOperations_Batch_AllocationOrdinal
                WHERE o.BatchJobId=$batch AND julianday(o.UpdatedAt)>=julianday($from)
                  AND o.AllocationOrdinal>$after
                ORDER BY o.AllocationOrdinal,o.SequenceNumber,o.Id LIMIT $limit;
                """,
                ("$batch", batch.ToString("D")), ("$from", "2026-08-19T00:00:00.0000000Z"),
                ("$after", 100_000), ("$limit", 101));
            Assert.Contains(instantPlan, line => line.Contains(
                "IX_TransferOperations_Batch_AllocationOrdinal", StringComparison.Ordinal));
            Assert.DoesNotContain(instantPlan, line => line.Contains("USE TEMP B-TREE", StringComparison.Ordinal));

            var instantCountPlan = await ExplainAsync(connection, """
                EXPLAIN QUERY PLAN
                SELECT COUNT(*) FROM TransferOperations o
                WHERE o.BatchJobId=$batch AND julianday(o.UpdatedAt)>=julianday($from);
                """,
                ("$batch", batch.ToString("D")), ("$from", "2026-08-19T00:00:00.0000000Z"));
            Assert.Contains(instantCountPlan, line => line.Contains(
                "IX_TransferOperations_Result_UpdatedInstant_Keyset", StringComparison.Ordinal));

            var accountOnlyPlan = await ExplainAsync(connection, """
                EXPLAIN QUERY PLAN
                SELECT o.Id FROM TransferOperations o INDEXED BY IX_TransferOperations_Result_Account_Keyset
                WHERE o.BatchJobId=$batch AND o.AssignedAccountId=$account
                  AND o.AllocationOrdinal>$after
                ORDER BY o.AllocationOrdinal,o.SequenceNumber,o.Id LIMIT $limit;
                """,
                ("$batch", batch.ToString("D")), ("$account", account.ToString("D")),
                ("$after", 100_000), ("$limit", 101));
            Assert.DoesNotContain(accountOnlyPlan, line => line.Contains("USE TEMP B-TREE", StringComparison.Ordinal));
            Assert.Contains(accountOnlyPlan, line => line.Contains(
                "IX_TransferOperations_Result_Account_Keyset", StringComparison.Ordinal));

            var resourcePlan = await ExplainAsync(connection, """
                EXPLAIN QUERY PLAN
                SELECT Id FROM ResourceItems
                WHERE BatchJobId=$batch AND GlobalValidityStatus=$validity AND CandidateStatus=$status
                  AND CandidateSequence>$after
                ORDER BY CandidateSequence,Id LIMIT $limit;
                """,
                ("$batch", batch.ToString("D")), ("$validity", "Unknown"),
                ("$status", "Expanded"), ("$after", 100_000), ("$limit", 101));
            Assert.Contains(resourcePlan, line => line.Contains(
                "IX_ResourceItems_Result_Filter_Keyset", StringComparison.Ordinal));

            var statisticsPlan = await ExplainAsync(connection, """
                EXPLAIN QUERY PLAN
                SELECT AssignedAccountId,Status,OutcomeKind,COUNT(*) FROM TransferOperations
                WHERE BatchJobId=$batch
                GROUP BY AssignedAccountId,Status,OutcomeKind;
                """, ("$batch", batch.ToString("D")));
            Assert.Contains(statisticsPlan, line => line.Contains(
                "SEARCH TransferOperations USING INDEX IX_TransferOperations_", StringComparison.Ordinal));
            Assert.DoesNotContain(statisticsPlan, line => line.Contains(
                "SCAN TransferOperations", StringComparison.Ordinal));
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baseline = GC.GetTotalMemory(true);
        var timer = Stopwatch.StartNew();
        var pages = new JobPageQuery(db.UnitOfWorkFactory);
        var page = await pages.GetOperationsAsync(new(batch, account, "Succeeded", null, null, 100));
        var resources = await pages.GetResourcesAsync(new(batch, null, null, 100));
        var statistics = await new BatchStatisticsQuery(db.UnitOfWorkFactory).ExecuteAsync(batch);
        var accountWriter = new CountingResultWriter();
        var export = await new ExportBatchResultsService(db.UnitOfWorkFactory).ExportAsync(
            new(batch, account, null, null, 1_000),
            Stream.Null,
            accountWriter);
        var timeWriter = new CountingResultWriter();
        var timeExport = await new ExportBatchResultsService(db.UnitOfWorkFactory).ExportAsync(
            new(batch, null, null, BatchTestData.Now.AddMinutes(-1), 1_000),
            Stream.Null,
            timeWriter);
        timer.Stop();
        output.WriteLine(
            "QUERY_EXPORT_200K_FILTERED elapsed_ms={0} peak_managed_delta_bytes={1} account_rows={2} time_rows={3}",
            timer.ElapsedMilliseconds,
            Math.Max(accountWriter.MaximumManagedBytes, timeWriter.MaximumManagedBytes) - baseline,
            accountWriter.RowCount,
            timeWriter.RowCount);

        Assert.Equal(100, page.Items.Count);
        Assert.Equal(count / 2, page.TotalCount);
        Assert.Equal(100, resources.Items.Count);
        Assert.Equal(count, statistics.ResourceCount);
        Assert.Equal(count, statistics.OperationCount);
        Assert.Equal((50_000L, 50_000L, 50_000L, 50_000L),
            (statistics.Accounts[0].NewCount, statistics.Accounts[0].ExistingCount,
             statistics.Accounts[0].FailedCount, statistics.Accounts[0].PendingCount));
        Assert.Equal(0L, statistics.Accounts[0].PausedCount);
        Assert.Equal(count, export.ExportedRows);
        Assert.Equal(count, timeExport.ExportedRows);
        Assert.Equal(count, accountWriter.RowCount);
        Assert.Equal(count, timeWriter.RowCount);
        var peakManagedBytes = Math.Max(accountWriter.MaximumManagedBytes, timeWriter.MaximumManagedBytes);
        Assert.True(peakManagedBytes - baseline < 128L * 1024 * 1024,
            $"Managed memory grew by {peakManagedBytes - baseline:N0} bytes.");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30), $"Scale query/export took {timer.Elapsed}.");
    }

    private static BatchJobRecord Job(Guid id) => new(
        id, "batch", "Excel", "Replication", "TaskCount", "ProportionalReduction", 3, 5,
        "/", "Keep", "Skip", "Immediate", "Queued", BatchTestData.Now, null, null, null, 1);

    private static ResourceItemRecord Resource(Guid batch, int sequence) => new(
        Guid.NewGuid(), batch, null, sequence, $"https://pan.baidu.com/s/{sequence}", $"share-{sequence}",
        null, sequence == 1 ? "=unsafe note" : $"note-{sequence}", null, null, null, "Keep", "Unknown",
        null, "Expanded", null, null, BatchTestData.Now, BatchTestData.Now);

    private static TransferOperationRecord Operation(
        Guid batch,
        ResourceItemRecord resource,
        Guid account,
        long ordinal,
        string status,
        string? outcome,
        string? errorCode = null,
        string? errorMessage = null) => new(
            Guid.NewGuid(), resource.Id, batch, ordinal, ordinal, "/target", "/target", "", account, "Rule",
            status, outcome, 1, $"fingerprint-{ordinal}", null, null, null, "NotPrepared", "Unoccupied",
            null, null, null, errorCode, errorMessage, null, BatchTestData.Now, BatchTestData.Now.AddMinutes(ordinal));

    private static AccountAllocationRecord Allocation(Guid batch, Guid account, int priority) => new(
        Guid.NewGuid(), batch, account, priority, "/", "/", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<string>> ExplainAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        var plan = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) plan.Add(reader.GetString(3));
        return plan;
    }

    private sealed class CountingResultWriter : IBatchResultWriter
    {
        public long RowCount { get; private set; }
        public long MaximumManagedBytes { get; private set; }

        public async Task WriteAsync(Stream stream, IAsyncEnumerable<BatchResultExportRow> rows, CancellationToken cancellationToken = default)
        {
            await foreach (var _ in rows.WithCancellation(cancellationToken))
            {
                RowCount++;
                if ((RowCount & 1023) == 0)
                    MaximumManagedBytes = Math.Max(MaximumManagedBytes, GC.GetTotalMemory(false));
            }
            MaximumManagedBytes = Math.Max(MaximumManagedBytes, GC.GetTotalMemory(false));
        }
    }

    private sealed class RejectingResultWriter : IBatchResultWriter
    {
        public async Task WriteAsync(
            Stream stream,
            IAsyncEnumerable<BatchResultExportRow> rows,
            CancellationToken cancellationToken = default)
        {
            await using var enumerator = rows.GetAsyncEnumerator(cancellationToken);
            Assert.True(await enumerator.MoveNextAsync());
            throw new IOException("The destination rejected the first row.");
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
