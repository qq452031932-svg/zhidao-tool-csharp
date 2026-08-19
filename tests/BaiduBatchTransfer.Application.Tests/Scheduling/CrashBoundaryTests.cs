using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Application.Tests.Jobs;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Application.Tests.Scheduling;

public sealed class CrashBoundaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);

    public static TheoryData<string, string, string, bool, bool, bool> DeathPoints => new()
    {
        { "lease", "Reserved", "NotPrepared", false, false, false },
        { "reservation", "Preparing", "PreparingToSubmit", false, false, false },
        { "descriptor", "Preparing", "PreparingToSubmit", true, false, false },
        { "preparing", "Preparing", "PreparingToSubmit", false, false, false },
        { "submitting", "Submitting", "Submitting", true, true, false },
        { "adapter-response", "Submitting", "Submitting", true, true, false },
        { "response-identifiers", "Submitting", "Submitting", true, true, true },
        { "before-final-outcome", "Submitting", "Submitting", true, true, true },
    };

    [Theory]
    [MemberData(nameof(DeathPoints))]
    public async Task StartupRecoversEveryPersistedCrashBoundaryWithoutBlindResubmission(
        string deathPoint,
        string status,
        string submitState,
        bool hasDescriptor,
        bool dailyCounted,
        bool hasResponseIdentifiers)
    {
        _ = deathPoint;
        await using var db = await BatchTestDatabase.CreateAsync();
        var batchId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var resource = Resource(resourceId, batchId, accountId);
        var operation = Operation(operationId, resource, accountId, status, submitState, hasDescriptor,
            dailyCounted, hasResponseIdentifiers);
        await db.SeedAsync(
            [AccountRecord.Create(accountId, $"baidu-{accountId:N}", "Account")],
            job: Job(batchId),
            resources: [resource],
            operations: [operation],
            allocations: [Allocation(batchId, accountId) with { ReservedSlotCount = 7, FulfilledCount = 3 }]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        await store.RecoverInterruptedOperationsAsync(Now);
        var rebuilt = await new AllocationCounterRebuilder(store).RebuildAsync();

        var unsafeBoundary = hasDescriptor || dailyCounted || submitState == "Submitting" || hasResponseIdentifiers;
        Assert.Equal(unsafeBoundary ? "ResultPending" : "Waiting", await ScalarAsync(db, "Status"));
        Assert.Equal(unsafeBoundary ? "PendingConfirmation" : "NotPrepared", await ScalarAsync(db, "SubmitState"));
        Assert.Equal(unsafeBoundary ? "Reserved" : "Unoccupied", await ScalarAsync(db, "TargetSlotState"));
        Assert.Equal(unsafeBoundary ? 1 : 0, await AllocationCountAsync(db, "ReservedSlotCount"));
        Assert.Equal(0, await AllocationCountAsync(db, "FulfilledCount"));
        Assert.Equal(unsafeBoundary ? 1 : 0, rebuilt.ReservedSlotCount);
        Assert.Equal(0, rebuilt.FulfilledCount);
    }

    [Fact]
    public async Task CounterRebuildUsesOperationRowsAndIncludesPendingAndFulfilledSlots()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batchId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var pendingResource = Resource(Guid.NewGuid(), batchId, accountId);
        var fulfilledResource = Resource(Guid.NewGuid(), batchId, null) with { CandidateStatus = "Expanded" };
        var pending = Operation(Guid.NewGuid(), pendingResource, accountId, "ResultPending",
            "PendingConfirmation", true, true, false) with { LeaseExpiresAt = null };
        var fulfilled = Operation(Guid.NewGuid(), fulfilledResource, accountId, "Succeeded",
            "ResultReceived", true, true, true) with
        {
            TargetSlotState = "Fulfilled",
            TargetSlotReservedAt = Now.AddMinutes(-5),
            LeaseExpiresAt = null,
            OutcomeKind = "NewTransfer",
        };
        await db.SeedAsync(
            [AccountRecord.Create(accountId, $"baidu-{accountId:N}", "Account")],
            job: Job(batchId),
            resources: [pendingResource, fulfilledResource],
            operations: [pending, fulfilled],
            allocations: [Allocation(batchId, accountId)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        var rebuilt = await new AllocationCounterRebuilder(store).RebuildAsync();

        Assert.Equal(1, await AllocationCountAsync(db, "ReservedSlotCount"));
        Assert.Equal(1, await AllocationCountAsync(db, "FulfilledCount"));
        Assert.Equal(new AllocationCounterRebuildResult(1, 1, 1), rebuilt);
    }

    [Fact]
    public async Task StartupAcquiresSingleInstanceBeforeDatabaseAndOnlyThenAllowsCoordinator()
    {
        var calls = new List<string>();
        var guard = new RecordingGuard(calls, acquired: true);
        var maintenance = new RecordingMaintenance(calls);
        var recovery = new RecordingRecoveryStore(calls);
        var counters = new AllocationCounterRebuilder(new RecordingCounterStore(calls));
        var service = new StartupRecoveryService(
            guard,
            maintenance,
            recovery,
            counters,
            new FixedTimeProvider(Now));

        var result = await service.RecoverAsync();

        Assert.Equal(["guard", "database", "leases", "counters"], calls);
        Assert.True(result.CoordinatorMayStart);
        Assert.True(service.CoordinatorMayStart);
    }

    [Fact]
    public async Task StartupStopsBeforeDatabaseWhenAnotherInstanceOwnsTheGuard()
    {
        var calls = new List<string>();
        var service = new StartupRecoveryService(
            new RecordingGuard(calls, acquired: false),
            new RecordingMaintenance(calls),
            new RecordingRecoveryStore(calls),
            new AllocationCounterRebuilder(new RecordingCounterStore(calls)),
            new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<SingleInstanceUnavailableException>(() => service.RecoverAsync());

        Assert.Equal(["guard"], calls);
        Assert.False(service.CoordinatorMayStart);
    }

    [Fact]
    public async Task CoordinatorRemainsBlockedWhenAnyRecoveryStageFails()
    {
        var calls = new List<string>();
        var service = new StartupRecoveryService(
            new RecordingGuard(calls, acquired: true),
            new RecordingMaintenance(calls),
            new RecordingRecoveryStore(calls, failure: new InvalidDataException("recovery failed")),
            new AllocationCounterRebuilder(new RecordingCounterStore(calls)),
            new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.RecoverAsync());

        Assert.Equal(["guard", "database", "leases"], calls);
        Assert.False(service.CoordinatorMayStart);
    }

    [Fact]
    public async Task SqliteStartupMaintenanceMigratesThenLeavesAHealthyDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bbt-startup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var factory = new SqliteConnectionFactory(Path.Combine(root, "startup.db"));
            var backup = new DatabaseBackupService(factory, Path.Combine(root, "backups"));
            var migrator = new DatabaseMigrator(factory, backup, "task13-test");
            var integrity = new DatabaseIntegrityService(factory);
            var maintenance = new SqliteStartupDatabaseMaintenance(migrator, integrity);

            await maintenance.PrepareAsync();

            Assert.True((await integrity.CheckAsync()).IsHealthy);
            await using var connection = await factory.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT MAX(Version) FROM SchemaVersions;";
            Assert.Equal(3, Convert.ToInt32(await command.ExecuteScalarAsync()));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingGuard(List<string> calls, bool acquired) : ISingleInstanceGuard
    {
        public Task<bool> TryAcquireAsync(CancellationToken cancellationToken = default)
        {
            calls.Add("guard");
            return Task.FromResult(acquired);
        }
    }

    private sealed class RecordingMaintenance(List<string> calls) : IStartupDatabaseMaintenance
    {
        public Task PrepareAsync(CancellationToken cancellationToken = default)
        {
            calls.Add("database");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRecoveryStore(List<string> calls, Exception? failure = null)
        : IInterruptedOperationRecoveryStore
    {
        public Task<LeaseRecoveryResult> RecoverInterruptedOperationsAsync(
            DateTimeOffset recoveredAt,
            CancellationToken cancellationToken = default)
        {
            calls.Add("leases");
            return failure is null
                ? Task.FromResult(new LeaseRecoveryResult(0, 0))
                : Task.FromException<LeaseRecoveryResult>(failure);
        }
    }

    private sealed class RecordingCounterStore(List<string> calls) : IAllocationCounterStore
    {
        public Task<AllocationCounterRebuildResult> RebuildAllocationCountersAsync(
            CancellationToken cancellationToken = default)
        {
            calls.Add("counters");
            return Task.FromResult(new AllocationCounterRebuildResult(0, 0, 0));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static BatchJobRecord Job(Guid id) =>
        new(id, "Batch", "Text", "Distribution", "SuccessTarget", "ProportionalReduction", 2, 2,
            "/base", "OptionalOuter", "Skip", "Immediate", "Active", Now, Now, null, null, 1);

    private static ResourceItemRecord Resource(Guid id, Guid batchId, Guid? accountId) =>
        new(id, batchId, null, id.GetHashCode() is var hash ? Math.Abs((long)hash) + 1 : 1,
            "https://pan.baidu.com/s/recovery", $"share-{id:N}", "a1b2", null, null, null, null,
            "OptionalOuter", "Unknown", accountId, "Reserved", accountId, Now.AddHours(1), Now, Now);

    private static TransferOperationRecord Operation(
        Guid id,
        ResourceItemRecord resource,
        Guid accountId,
        string status,
        string submitState,
        bool hasDescriptor,
        bool dailyCounted,
        bool hasResponseIdentifiers) =>
        new(id, resource.Id, resource.BatchJobId, resource.CandidateSequence, resource.CandidateSequence,
            "/base", "/base", "", accountId, "Weighted", status, null, 1, $"fp-{id:N}",
            hasDescriptor ? DescriptorJson(id) : null,
            hasResponseIdentifiers ? $"request-{id:N}" : null,
            hasResponseIdentifiers ? "[\"remote-1\"]" : null,
            submitState, "Reserved", Now.AddMinutes(-5), dailyCounted ? Now.AddMinutes(-4) : null,
            dailyCounted ? Now.AddMinutes(-4) : null, null, null, Now.AddHours(1), Now.AddMinutes(-10), Now);

    private static AccountAllocationRecord Allocation(Guid batchId, Guid accountId) =>
        new(Guid.NewGuid(), batchId, accountId, 1, "/base", "/base", 10, 10, 10,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static string DescriptorJson(Guid operationId) =>
        $$"""{"SourceShareId":"share","TargetPath":"/base","ExpectedOuterNameFingerprint":"outer","OperationFingerprint":"fp-{{operationId:N}}","SubmitWindowStartUtc":"{{Now:O}}","SubmitWindowEndUtc":"{{Now.AddMinutes(10):O}}"}""";

    private static async Task<string> ScalarAsync(BatchTestDatabase db, string column)
    {
        await using var connection = await db.ConnectionFactory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM TransferOperations LIMIT 1;";
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> AllocationCountAsync(BatchTestDatabase db, string column)
    {
        await using var connection = await db.ConnectionFactory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM AccountAllocations LIMIT 1;";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
