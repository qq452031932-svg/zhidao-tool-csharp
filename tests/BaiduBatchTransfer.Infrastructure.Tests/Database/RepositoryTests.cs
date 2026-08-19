using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Database;
using BaiduBatchTransfer.Infrastructure.Database.Repositories;

namespace BaiduBatchTransfer.Infrastructure.Tests.Database;

public sealed class RepositoryTests : IDisposable
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

    private async Task<SqliteConnectionFactory> CreateFactoryAsync()
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "app.db"));
        await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
        return factory;
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
