using System.Diagnostics;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Database;
using BaiduBatchTransfer.Infrastructure.Database.Repositories;
using Xunit.Abstractions;

namespace BaiduBatchTransfer.Infrastructure.Tests.Database;

public sealed class RepositoryTests(ITestOutputHelper output) : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bbt-repository-{Guid.NewGuid():N}");

    [Fact]
    public async Task UnitOfWorkCommitsAllRepositoriesAtomically()
    {
        var factory = await CreateFactoryAsync();
        var account = AccountRecord.Create(Guid.NewGuid(), "user-1", "Alpha");
        var session = ImportSessionRecord.Create(Guid.NewGuid(), "Text", "paste");
        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await unit.Accounts.UpsertAsync(account);
            await unit.Imports.AddSessionAsync(session);
            await unit.CommitAsync();
        }

        await using var verify = await SqliteUnitOfWork.BeginAsync(factory);
        Assert.Equal(account, await verify.Accounts.GetByIdAsync(account.Id));
        Assert.Equal(session, await verify.Imports.GetSessionAsync(session.Id));
    }

    [Fact]
    public async Task DisposedUncommittedUnitOfWorkRollsBack()
    {
        var factory = await CreateFactoryAsync();
        var account = AccountRecord.Create(Guid.NewGuid(), "user-rollback", "Beta");
        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory)) await unit.Accounts.UpsertAsync(account);

        await using var verify = await SqliteUnitOfWork.BeginAsync(factory);
        Assert.Null(await verify.Accounts.GetByIdAsync(account.Id));
    }

    [Fact]
    public async Task ImportRowsArePagedByStableSourceOrderAndDisposition()
    {
        var factory = await CreateFactoryAsync();
        var session = ImportSessionRecord.Create(Guid.NewGuid(), "Excel", "large.xlsx");
        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await unit.Imports.AddSessionAsync(session);
            await unit.Imports.AddRowsAsync(Enumerable.Range(1, 25).Select(i => StagedImportRowRecord.Create(Guid.NewGuid(), session.Id, i, $"url-{i}", i % 2 == 0 ? "Ready" : "Invalid")));
            await unit.CommitAsync();
        }

        await using var read = await SqliteUnitOfWork.BeginAsync(factory);
        var page = await read.Imports.GetRowsPageAsync(new StagedImportRowQuery(session.Id, "Ready", 2, 5));
        Assert.Equal(12, page.TotalCount);
        Assert.Equal(new[] { 12, 14, 16, 18, 20 }, page.Items.Select(x => x.SourceRowNumber));
    }

    [Fact]
    public async Task NormalizedIdentityQueriesNeverReturnExcludedRows()
    {
        var factory = await CreateFactoryAsync();
        var session = ImportSessionRecord.Create(Guid.NewGuid(), "Text", "paste");
        var rows = new[]
        {
            StagedImportRowRecord.Create(Guid.NewGuid(), session.Id, 1, "url-1", "Excluded") with { NormalizedShareId = "shared", Note = "keep-first" },
            StagedImportRowRecord.Create(Guid.NewGuid(), session.Id, 2, "url-2", "Ready") with { NormalizedShareId = "shared" },
            StagedImportRowRecord.Create(Guid.NewGuid(), session.Id, 3, "url-3", "Excluded") with { NormalizedShareId = "shared", Note = "keep-later" },
            StagedImportRowRecord.Create(Guid.NewGuid(), session.Id, 4, "url-4", "Duplicate") with { NormalizedShareId = "shared" },
            StagedImportRowRecord.Create(Guid.NewGuid(), session.Id, 5, "url-5", "Excluded") with { NormalizedShareId = "excluded-only" },
        };
        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await unit.Imports.AddSessionAsync(session);
            await unit.Imports.AddRowsAsync(rows);
            await unit.CommitAsync();
        }

        await using var read = await SqliteUnitOfWork.BeginAsync(factory);
        Assert.Equal(rows[1].Id, (await read.Imports.GetFirstByNormalizedShareIdAsync(session.Id, "shared"))!.Id);
        Assert.Equal(
            new[] { rows[3].Id },
            (await read.Imports.GetRowsByNormalizedShareIdAfterSourceRowAsync(session.Id, "shared", 2, 10)).Select(row => row.Id));
        Assert.Null(await read.Imports.GetFirstByNormalizedShareIdAsync(session.Id, "excluded-only"));
    }

    [Fact]
    public async Task RepositoryOperationsHonorPreCancelledToken()
    {
        var factory = await CreateFactoryAsync();
        await using var unit = await SqliteUnitOfWork.BeginAsync(factory);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unit.Accounts.GetPageAsync(new PageQuery(1, 10), cancellation.Token));
    }

    [Fact]
    public async Task AccountRulesRoundTripInPriorityOrder()
    {
        var factory = await CreateFactoryAsync();
        var account = AccountRecord.Create(Guid.NewGuid(), "rules-user", "Rules");
        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await unit.Accounts.UpsertAsync(account);
            await unit.Accounts.UpsertRuleAsync(new AccountRuleRecord(Guid.NewGuid(), account.Id, "PathPrefix", "/b", 2, true));
            await unit.Accounts.UpsertRuleAsync(new AccountRuleRecord(Guid.NewGuid(), account.Id, "PathPrefix", "/a", 1, true));
            await unit.CommitAsync();
        }
        await using var read = await SqliteUnitOfWork.BeginAsync(factory);
        var page = await read.Accounts.GetRulesPageAsync(account.Id, new PageQuery(1, 10));
        Assert.Equal(new[] { 1, 2 }, page.Items.Select(x => x.Priority));
    }

    [Fact]
    public async Task JobGraphRepositoriesRoundTripUsingPages()
    {
        var factory = await CreateFactoryAsync(); var now = DateTimeOffset.UtcNow;
        var account = AccountRecord.Create(Guid.NewGuid(), "job-user", "Worker");
        var job = new BatchJobRecord(Guid.NewGuid(), "Job", "Text", "Distribution", "TaskCount", "ProportionalReduction", 1, 1, "/", "None", "Skip", "Immediate", "Queued", now, null, null, null, 1);
        var resource = new ResourceItemRecord(Guid.NewGuid(), job.Id, null, 1, "url", "share", null, null, null, null, null, "None", "Unknown", null, "Unallocated", null, null, now, now);
        var operation = new TransferOperationRecord(Guid.NewGuid(), resource.Id, job.Id, 1, 1, "/", "/", "", account.Id, "Rule", "Waiting", null, 0, "fingerprint", "{}", null, null, "NotPrepared", "Unoccupied", null, null, null, null, null, null, now, now);
        var allocation = new AccountAllocationRecord(Guid.NewGuid(), job.Id, account.Id, 1, "/", "/", 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await unit.Accounts.UpsertAsync(account); await unit.Jobs.AddAsync(job); await unit.Jobs.AddResourcesAsync([resource]);
            await unit.Jobs.AddOperationsAsync([operation]); await unit.Jobs.AddAllocationsAsync([allocation]); await unit.CommitAsync();
        }
        await using var read = await SqliteUnitOfWork.BeginAsync(factory);
        Assert.Equal(job, await read.Jobs.GetAsync(job.Id));
        Assert.Equal(resource, Assert.Single((await read.Jobs.GetResourcesPageAsync(job.Id, new PageQuery(1, 1))).Items));
        Assert.Equal(operation, Assert.Single((await read.Jobs.GetOperationsPageAsync(job.Id, new PageQuery(1, 1))).Items));
        Assert.Equal(allocation, Assert.Single((await read.Jobs.GetAllocationsPageAsync(job.Id, new PageQuery(1, 1))).Items));
    }

    [Fact]
    public async Task JobUpdatesAllocationUpdatesAndSanitizedAuditShareOneTransaction()
    {
        var factory = await CreateFactoryAsync();
        var now = DateTimeOffset.UtcNow;
        var account = AccountRecord.Create(Guid.NewGuid(), "update-user", "Update");
        var job = new BatchJobRecord(Guid.NewGuid(), "Job", "Text", "Distribution", "SuccessTarget",
            "WaitForCandidates", 1, 0, "/", "None", "Skip", "Immediate", "AwaitingCandidates",
            now, null, null, null, 1);
        var allocation = new AccountAllocationRecord(Guid.NewGuid(), job.Id, account.Id, 1, "/", "/",
            0, 10, 10, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0);
        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await unit.Accounts.UpsertAsync(account);
            await unit.Jobs.AddAsync(job);
            await unit.Jobs.AddAllocationsAsync([allocation]);
            await unit.CommitAsync();
        }

        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await unit.Jobs.UpdateAsync(job with { ResourceCount = 2, Status = "PausedReady" });
            await unit.Jobs.UpdateAllocationsAsync([allocation with { ExclusiveCandidateCount = 2 }]);
            await unit.Jobs.WriteAuditAsync(new AuditEventRecord(
                Guid.NewGuid(), job.Id, null, null, null, AuditEventKind.CandidatesAppended, now));
            await unit.CommitAsync();
        }

        await using (var read = await SqliteUnitOfWork.BeginAsync(factory))
        {
            Assert.Equal("PausedReady", (await read.Jobs.GetAsync(job.Id))!.Status);
            Assert.Equal(2, Assert.Single((await read.Jobs.GetAllocationsPageAsync(job.Id, new PageQuery(1, 10))).Items).ExclusiveCandidateCount);
        }
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EventCode,SanitizedMessage FROM ExecutionLogs WHERE BatchJobId=$id;";
        command.Parameters.AddWithValue("$id", job.Id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("CANDIDATES_APPENDED", reader.GetString(0));
        Assert.Equal("Candidate append was confirmed.", reader.GetString(1));
    }

    [Fact]
    public async Task AuditBoundaryRejectsUnknownEventsAndNeverAcceptsUserMessageContent()
    {
        var factory = await CreateFactoryAsync();
        var now = DateTimeOffset.UtcNow;
        var job = new BatchJobRecord(Guid.NewGuid(), "https://private/?pwd=code", "Text", "Distribution",
            "SuccessTarget", "WaitForCandidates", 0, 0, "/private/cookie/profile", "None", "Skip",
            "Immediate", "AwaitingCandidates", now, null, null, null, 1);
        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await unit.Jobs.AddAsync(job);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => unit.Jobs.WriteAuditAsync(
                new AuditEventRecord(Guid.NewGuid(), job.Id, null, null, null, (AuditEventKind)999, now)));
            await unit.Jobs.WriteAuditAsync(new AuditEventRecord(
                Guid.NewGuid(), job.Id, null, null, null, AuditEventKind.CandidatesAppended, now));
            await unit.CommitAsync();
        }

        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EventCode,SanitizedMessage FROM ExecutionLogs WHERE BatchJobId=$id;";
        command.Parameters.AddWithValue("$id", job.Id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var persisted = $"{reader.GetString(0)} {reader.GetString(1)}";
        Assert.Equal("CANDIDATES_APPENDED Candidate append was confirmed.", persisted);
        Assert.DoesNotContain("https://", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pwd=", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("profile", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/private", persisted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreparedStreamingWritesHandleTwoHundredThousandResourcesAndThreeReplicasWithinBoundedMemory()
    {
        const int resourceCount = 200_000;
        const int accountCount = 3;
        var factory = await CreateFactoryAsync();
        var now = DateTimeOffset.UtcNow;
        var accounts = Enumerable.Range(0, accountCount)
            .Select(index => AccountRecord.Create(StableGuid(index + 1), $"scale-{index}", $"Scale {index}"))
            .ToArray();
        var job = new BatchJobRecord(StableGuid(900_001), "Scale", "Text", "Replication", "TaskCount",
            "ProportionalReduction", resourceCount, resourceCount * accountCount, "/", "None", "Skip",
            "Immediate", "Queued", now, null, null, null, 1);
        var beforeBytes = GC.GetTotalMemory(forceFullCollection: true);
        var peakBytes = beforeBytes;
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                var current = GC.GetTotalMemory(forceFullCollection: false);
                UpdateMaximum(ref peakBytes, current);
                await Task.Delay(10);
            }
        });
        var timer = Stopwatch.StartNew();
        try
        {
            await using var unit = await SqliteUnitOfWork.BeginAsync(factory);
            foreach (var account in accounts) await unit.Accounts.UpsertAsync(account);
            await unit.Jobs.AddAsync(job);
            await unit.Jobs.AddResourcesAsync(ScaleResources(job.Id, resourceCount, now));
            await unit.Jobs.AddOperationsAsync(ScaleOperations(job.Id, accounts, resourceCount, now));
            await unit.CommitAsync();
        }
        finally
        {
            timer.Stop();
            sampling.Cancel();
            await sampler;
        }

        await using var connection = await factory.OpenConnectionAsync();
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT (SELECT COUNT(*) FROM ResourceItems), (SELECT COUNT(*) FROM TransferOperations);";
        await using var reader = await count.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(resourceCount, reader.GetInt32(0));
        Assert.Equal(resourceCount * accountCount, reader.GetInt32(1));
        var peakGrowth = Math.Max(0, Volatile.Read(ref peakBytes) - beforeBytes);
        output.WriteLine($"scale resources={resourceCount}, operations={resourceCount * accountCount}, elapsed={timer.Elapsed}, peak-managed-growth={peakGrowth:N0} bytes");
        Assert.True(peakGrowth < 384L * 1024 * 1024, $"Peak managed growth was {peakGrowth:N0} bytes.");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(20), $"Bulk insert took {timer.Elapsed}.");
    }

    [Fact]
    public async Task OperationSubmitLifecyclePersistsDescriptorBeforeResponseIdentifiers()
    {
        var factory = await CreateFactoryAsync(); var now = DateTimeOffset.UtcNow;
        var account = AccountRecord.Create(Guid.NewGuid(), "submit-user", "Submit");
        var job = new BatchJobRecord(Guid.NewGuid(), "Job", "Text", "Distribution", "TaskCount", "ProportionalReduction", 1, 1, "/", "None", "Skip", "Immediate", "Queued", now, null, null, null, 1);
        var resource = new ResourceItemRecord(Guid.NewGuid(), job.Id, null, 1, "url", "share", null, null, null, null, null, "None", "Unknown", null, "Unallocated", null, null, now, now);
        var operation = new TransferOperationRecord(Guid.NewGuid(), resource.Id, job.Id, 1, 1, "/", "/", "", account.Id, "Rule", "Preparing", null, 1, "submit-fingerprint", null, null, null, "PreparingToSubmit", "Reserved", now, null, null, null, null, null, now, now);
        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await unit.Accounts.UpsertAsync(account); await unit.Jobs.AddAsync(job); await unit.Jobs.AddResourcesAsync([resource]); await unit.Jobs.AddOperationsAsync([operation]);
            await unit.Jobs.PrepareSubmissionAsync(new OperationSubmissionPreparation(operation.Id, "{\"share\":\"share\"}", now.AddMinutes(1), now.AddSeconds(1)));
            await Assert.ThrowsAsync<ArgumentException>(() => unit.Jobs.RecordSubmissionResponseAsync(new OperationSubmissionResponse(operation.Id, null, null, "NotPrepared", "Reserved", now.AddSeconds(2))));
            await unit.Jobs.RecordSubmissionResponseAsync(new OperationSubmissionResponse(operation.Id, "request-1", "[\"remote-1\"]", "ResultReceived", "ResultPending", now.AddSeconds(2)));
            await unit.CommitAsync();
        }
        await using var read = await SqliteUnitOfWork.BeginAsync(factory);
        var saved = Assert.Single((await read.Jobs.GetOperationsPageAsync(job.Id, new PageQuery(1, 1))).Items);
        Assert.Equal("{\"share\":\"share\"}", saved.ConfirmationDescriptorJson);
        Assert.Equal("request-1", saved.AdapterRequestId); Assert.Equal("[\"remote-1\"]", saved.RemoteItemIdsJson);
    }

    [Fact]
    public async Task WalReaderGetsStableSnapshotWhileAnotherTransactionIsWriting()
    {
        var factory = await CreateFactoryAsync();
        await using var writer = await SqliteUnitOfWork.BeginAsync(factory);
        await writer.Accounts.UpsertAsync(AccountRecord.Create(Guid.NewGuid(), "uncommitted", "Writer"));
        await using var reader = await SqliteUnitOfWork.BeginAsync(factory).WaitAsync(TimeSpan.FromSeconds(2));
        var page = await reader.Accounts.GetPageAsync(new PageQuery(1, 10));
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task UnitOfWorkCleanupAttemptsEveryStepAndAggregatesFailures()
    {
        var calls = new List<string>();
        static ValueTask Fail(List<string> calls, string step)
        {
            calls.Add(step);
            return ValueTask.FromException(new IOException(step));
        }

        var error = await Assert.ThrowsAsync<AggregateException>(async () =>
            await UnitOfWorkCleanup.DisposeAsync(
                rollbackRequired: true,
                () => Fail(calls, "rollback"),
                () => Fail(calls, "transaction"),
                () => Fail(calls, "connection")));

        Assert.Equal(new[] { "rollback", "transaction", "connection" }, calls);
        Assert.Equal(3, error.InnerExceptions.Count);
    }

    [Fact]
    public async Task FactoryTracksRawCloseAndReopenLifecycle()
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "app.db"), busyTimeoutMilliseconds: 4321);
        await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "1.0").MigrateAsync();
        await using var connection = await factory.OpenConnectionAsync();
        Assert.Equal(1, factory.ActiveConnectionCount);
        connection.Close();
        Assert.Equal(0, factory.ActiveConnectionCount);
        connection.Open();
        Assert.Equal(1, factory.ActiveConnectionCount);
        await using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys;";
            Assert.Equal(1L, await foreignKeys.ExecuteScalarAsync());
        }
        await using (var timeout = connection.CreateCommand())
        {
            timeout.CommandText = "PRAGMA busy_timeout;";
            Assert.Equal(4321L, await timeout.ExecuteScalarAsync());
        }
        await connection.DisposeAsync();
        Assert.Equal(0, factory.ActiveConnectionCount);
    }

    private async Task<SqliteConnectionFactory> CreateFactoryAsync()
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "app.db"));
        await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
        return factory;
    }

    private static IEnumerable<ResourceItemRecord> ScaleResources(
        Guid batchJobId,
        int count,
        DateTimeOffset now)
    {
        for (var index = 1; index <= count; index++)
        {
            yield return new ResourceItemRecord(
                StableGuid(1_000_000 + index), batchJobId, null, index,
                $"https://pan.baidu.com/s/scale-{index}", $"scale-{index}", null, null, null, null,
                null, "None", "Unknown", null, "Expanded", null, null, now, now);
        }
    }

    private static IEnumerable<TransferOperationRecord> ScaleOperations(
        Guid batchJobId,
        IReadOnlyList<AccountRecord> accounts,
        int resourceCount,
        DateTimeOffset now)
    {
        var ordinal = 0;
        for (var resourceIndex = 1; resourceIndex <= resourceCount; resourceIndex++)
        {
            var resourceId = StableGuid(1_000_000 + resourceIndex);
            foreach (var account in accounts)
            {
                ordinal++;
                yield return new TransferOperationRecord(
                    StableGuid(2_000_000 + ordinal), resourceId, batchJobId, resourceIndex, ordinal,
                    "/", "/", string.Empty, account.Id, "Replication", "Waiting", null, 0,
                    $"scale-{resourceIndex}-{account.Id:N}", null, null, null, "NotPrepared", "Unoccupied",
                    null, null, null, null, null, null, now, now);
            }
        }
    }

    private static Guid StableGuid(int value) => new(value, 0, 0, new byte[8]);

    private static void UpdateMaximum(ref long target, long value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) return;
            current = observed;
        }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
