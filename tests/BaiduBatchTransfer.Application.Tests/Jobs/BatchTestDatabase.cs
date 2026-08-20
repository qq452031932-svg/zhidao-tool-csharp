using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Database;

namespace BaiduBatchTransfer.Application.Tests.Jobs;

internal sealed class BatchTestDatabase : IAsyncDisposable
{
    private readonly string root;

    private BatchTestDatabase(string root, SqliteConnectionFactory connectionFactory)
    {
        this.root = root;
        ConnectionFactory = connectionFactory;
        UnitOfWorkFactory = new SqliteUnitOfWorkFactory(connectionFactory);
    }

    public SqliteConnectionFactory ConnectionFactory { get; }

    public IUnitOfWorkFactory UnitOfWorkFactory { get; }

    public string DatabasePath => ConnectionFactory.DatabasePath;

    public static async Task<BatchTestDatabase> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bbt-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "jobs.db"));
        await new DatabaseMigrator(
            factory,
            new DatabaseBackupService(factory, Path.Combine(root, "backups")),
            "task11-test").MigrateAsync();
        return new BatchTestDatabase(root, factory);
    }

    public async Task SeedAsync(
        IEnumerable<AccountRecord> accounts,
        ImportSessionRecord? session = null,
        IEnumerable<StagedImportRowRecord>? rows = null,
        BatchJobRecord? job = null,
        IEnumerable<ResourceItemRecord>? resources = null,
        IEnumerable<TransferOperationRecord>? operations = null,
        IEnumerable<AccountAllocationRecord>? allocations = null,
        IEnumerable<StagedImportRowPayloadRecord>? payloads = null,
        bool seedDefaultPayloads = true)
    {
        var rowSnapshot = rows?.ToArray();
        await using var unit = await UnitOfWorkFactory.BeginAsync();
        foreach (var account in accounts) await unit.Accounts.UpsertAsync(account);
        if (session is not null) await unit.Imports.AddSessionAsync(session);
        if (rowSnapshot is not null) await unit.Imports.AddRowsAsync(rowSnapshot);
        if (payloads is not null)
            await unit.Imports.AddRowPayloadsAsync(payloads);
        else if (rowSnapshot is not null && seedDefaultPayloads)
            await unit.Imports.AddRowPayloadsAsync(rowSnapshot.Select(row =>
                new StagedImportRowPayloadRecord(row.Id, "source", $"resolved-{row.SourceRowNumber}", row.ShareUrl, "[]")));
        if (job is not null) await unit.Jobs.AddAsync(job);
        if (resources is not null) await unit.Jobs.AddResourcesAsync(resources);
        if (operations is not null) await unit.Jobs.AddOperationsAsync(operations);
        if (allocations is not null) await unit.Jobs.AddAllocationsAsync(allocations);
        await unit.CommitAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

internal static class BatchTestData
{
    public static readonly DateTimeOffset Now = new(2026, 8, 19, 1, 2, 3, TimeSpan.Zero);

    public static AccountRecord Account(Guid id, string suffix) =>
        AccountRecord.Create(id, $"baidu-{suffix}", $"Account {suffix}");

    public static ImportSessionRecord ReadySession(
        Guid id,
        int totalRows,
        int readyRows,
        int invalidRows = 0,
        int duplicateRows = 0,
        int excludedRows = 0,
        Guid? appendToBatchJobId = null) =>
        new(id, "Excel", "input.xlsx", "ReadyToConfirm", totalRows, readyRows, invalidRows,
            duplicateRows, excludedRows, null, null, appendToBatchJobId, Now, Now);

    public static StagedImportRowRecord ReadyRow(
        Guid sessionId,
        int sourceRow,
        Guid? mappedAccountId = null,
        string? targetPath = null,
        string? folder = null,
        string? note = null,
        string? normalizedId = null) =>
        new(Guid.NewGuid(), sessionId, sourceRow, $"https://pan.baidu.com/s/{sourceRow}",
            normalizedId ?? $"share-{sourceRow}", "a1b2", targetPath, folder, null, mappedAccountId,
            note, "Ready", null, null, null, null, null, Now);
}
