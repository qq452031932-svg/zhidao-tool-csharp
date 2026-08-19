using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Application.Tests.Jobs;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Application.Tests.Scheduling;

public sealed class SqliteSchedulingStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PausedReadyBatchCannotActivateUntilExplicitUserConfirmedResumeCommand()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        await db.SeedAsync([], job: Job(batch, "SuccessTarget", "PausedReady", 0));
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        Assert.Null(await store.TryActivateNextBatchAsync(Now));
        Assert.Equal("PausedReady", await ScalarStringAsync(db, "SELECT Status FROM BatchJobs"));

        var service = new BatchResumeService(store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmReadyForQueueAsync(batch, userConfirmed: false));
        await service.ConfirmReadyForQueueAsync(batch, userConfirmed: true);
        Assert.Equal(batch, await store.TryActivateNextBatchAsync(Now));
    }

    [Fact]
    public async Task SuccessTargetWaitingOperationCannotBeReacquiredAfterTargetDropsToZero()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var candidate = Resource(batch, 1, null);
        await db.SeedAsync([Account(account, 0)], job: Job(batch, "SuccessTarget", "Active", 1),
            resources: [candidate], allocations: [Allocation(batch, account, 1, 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var first = await store.TryReserveNextAsync(batch, Now, Now.AddMinutes(5));
        Assert.NotNull(first);
        await store.MarkPreparingAsync(first.OperationId, Now.AddSeconds(1));
        await store.ReturnToWaitingForDailyLimitAsync(first.OperationId, Now.AddSeconds(2));
        await ExecuteAsync(db,
            "UPDATE Accounts SET Status='Active' WHERE Id=$account; UPDATE AccountAllocations SET EffectiveTargetSuccessCount=0,TargetSuccessCount=0 WHERE BatchJobId=$batch AND AccountId=$account;",
            ("$account", account.ToString("D")), ("$batch", batch.ToString("D")));

        var reacquired = await store.TryReserveNextAsync(batch, Now.AddMinutes(1), Now.AddMinutes(6));

        Assert.Null(reacquired);
        Assert.Equal("Waiting", await ScalarStringAsync(db, "SELECT Status FROM TransferOperations"));
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT ReservedSlotCount FROM AccountAllocations"));
    }

    [Fact]
    public async Task GlobalInvalidCancelsAllUnsubmittedReplicationSiblingsAndReleasesTheirSlotsAtomically()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Expanded");
        var failing = Operation(resource, a, 1, "Preparing", "PreparingToSubmit", "Reserved");
        var waitingSibling = Operation(resource, b, 2);
        var reservedSibling = Operation(resource, c, 3, "Reserved", "NotPrepared", "Reserved");
        await db.SeedAsync([Account(a, 0), Account(b, 0), Account(c, 0)],
            job: new BatchJobRecord(batch, "Replication", "Text", "Replication", "TaskCount", "ProportionalReduction",
                1, 3, "/base", "None", "Skip", "Immediate", "Active", Now, Now, null, null, 1),
            resources: [resource], operations: [failing, waitingSibling, reservedSibling],
            allocations:
            [
                Allocation(batch, a, 1, 1, 1), Allocation(batch, b, 2, 1), Allocation(batch, c, 3, 1, 1),
            ]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        await store.MarkFailedAsync(failing.Id, "InvalidLink", "The share link is invalid.", globallyInvalid: true, Now.AddSeconds(1));

        Assert.Equal("GloballyInvalid", await ScalarStringAsync(db, "SELECT CandidateStatus FROM ResourceItems"));
        Assert.Equal("Invalid", await ScalarStringAsync(db, "SELECT GlobalValidityStatus FROM ResourceItems"));
        Assert.Equal("Failed", await ScalarStringAsync(db, "SELECT Status FROM TransferOperations WHERE Id='" + failing.Id.ToString("D") + "'"));
        Assert.Equal("Cancelled", await ScalarStringAsync(db, "SELECT Status FROM TransferOperations WHERE Id='" + waitingSibling.Id.ToString("D") + "'"));
        Assert.Equal("Cancelled", await ScalarStringAsync(db, "SELECT Status FROM TransferOperations WHERE Id='" + reservedSibling.Id.ToString("D") + "'"));
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT SUM(ReservedSlotCount) FROM AccountAllocations"));
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT COUNT(*) FROM AccountAllocations WHERE ReservedSlotCount<0"));
        Assert.Null(await store.TryReserveNextAsync(batch, Now.AddMinutes(1), Now.AddMinutes(6)));
    }

    [Fact]
    public async Task SuccessTargetAllocationIsAtomicSerialAndUsesExclusiveBeforePublic()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var exclusive = Resource(batch, 2, a);
        var publicCandidate = Resource(batch, 1, null);
        await db.SeedAsync(
            [Account(a, 0), Account(b, 0)],
            job: Job(batch, "SuccessTarget", "Active", resourceCount: 2),
            resources: [publicCandidate, exclusive],
            allocations: [Allocation(batch, a, 1, 2), Allocation(batch, b, 2, 2)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        var first = await store.TryReserveNextAsync(batch, Now, Now.AddMinutes(5));
        var second = await store.TryReserveNextAsync(batch, Now, Now.AddMinutes(5));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(a, first.AccountId);
        Assert.Equal(exclusive.Id, await ScalarGuidAsync(db, "SELECT ResourceItemId FROM TransferOperations WHERE Id=$id", first.OperationId));
        Assert.Equal(b, second.AccountId);
        Assert.Equal([1L, 2L], new[] { first.AllocationOrdinal, second.AllocationOrdinal });
        Assert.Equal(2L, await ScalarLongAsync(db, "SELECT COUNT(*) FROM TransferOperations WHERE TargetSlotState='Reserved'"));
    }

    [Fact]
    public async Task ResultPendingRetainsSlotAndBlocksOnlyItsOwnAccount()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var pendingResource = Resource(batch, 1, null, "Expanded");
        var waitAResource = Resource(batch, 2, null, "Expanded");
        var waitBResource = Resource(batch, 3, null, "Expanded");
        await db.SeedAsync(
            [Account(a, 0), Account(b, 0)],
            job: Job(batch, "TaskCount", "Active", resourceCount: 3, operationCount: 3),
            resources: [pendingResource, waitAResource, waitBResource],
            operations:
            [
                Operation(pendingResource, a, 1, "ResultPending", "PendingConfirmation", "Reserved", "{}"),
                Operation(waitAResource, a, 2),
                Operation(waitBResource, b, 3),
            ],
            allocations: [Allocation(batch, a, 1, 2, reserved: 1), Allocation(batch, b, 2, 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        var state = await store.GetStateAsync();
        var reserved = await store.TryReserveNextAsync(batch, Now, Now.AddMinutes(5));

        Assert.Equal(1, state.ActiveSlotCount);
        Assert.Contains(a, state.BusyAccountIds);
        Assert.Equal(b, reserved?.AccountId);
    }

    [Theory]
    [InlineData("ExpiredLogin", "The account session has expired.")]
    [InlineData("VerificationRequired", "Account verification is required.")]
    [InlineData("InsufficientCapacity", "The account has insufficient capacity.")]
    [InlineData("InvalidTargetPath", "The target path is invalid.")]
    [InlineData("InvalidLink", "The share link is invalid.")]
    [InlineData("InvalidExtractionCode", "The extraction code is invalid.")]
    public async Task ResultPendingStoreRejectsDefiniteErrorsWithoutChangingDatabase(string code, string message)
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Expanded");
        var operation = Operation(resource, account, 1, "Submitting", "Submitting", "Reserved", "{}") with
        {
            DailyCountedAt = Now,
            SubmitStartedAt = Now,
        };
        await db.SeedAsync([Account(account, 0) with { DailyExecutedCount = 1 }],
            job: Job(batch, "TaskCount", "Active", 1, 1),
            resources: [resource],
            operations: [operation],
            allocations: [Allocation(batch, account, 1, 1, 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.MarkResultPendingAsync(operation.Id, code, message, Now.AddSeconds(1)));

        Assert.Equal("Submitting", await ScalarStringAsync(db, "SELECT Status FROM TransferOperations"));
        Assert.Equal("Submitting", await ScalarStringAsync(db, "SELECT SubmitState FROM TransferOperations"));
        Assert.Equal("Reserved", await ScalarStringAsync(db, "SELECT TargetSlotState FROM TransferOperations"));
        Assert.Equal(1L, await ScalarLongAsync(db, "SELECT ReservedSlotCount FROM AccountAllocations"));
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT FailedItemCount FROM AccountAllocations"));
        Assert.Equal("Active", await ScalarStringAsync(db, "SELECT Status FROM Accounts"));
    }

    [Theory]
    [InlineData("TransientService", "The service is temporarily unavailable.")]
    [InlineData("UnknownResponse", "The service returned an unrecognized response.")]
    public async Task ResultPendingStoreAcceptsOnlyUncertainExecutionResults(string code, string message)
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Expanded");
        var operation = Operation(resource, account, 1, "Submitting", "Submitting", "Reserved", "{}") with
        {
            DailyCountedAt = Now,
            SubmitStartedAt = Now,
        };
        await db.SeedAsync([Account(account, 0) with { DailyExecutedCount = 1 }],
            job: Job(batch, "TaskCount", "Active", 1, 1),
            resources: [resource],
            operations: [operation],
            allocations: [Allocation(batch, account, 1, 1, 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        await store.MarkResultPendingAsync(operation.Id, code, message, Now.AddSeconds(1));

        Assert.Equal("ResultPending", await ScalarStringAsync(db, "SELECT Status FROM TransferOperations"));
        Assert.Equal("PendingConfirmation", await ScalarStringAsync(db, "SELECT SubmitState FROM TransferOperations"));
        Assert.Equal(code, await ScalarStringAsync(db, "SELECT LastErrorCode FROM TransferOperations"));
        Assert.Equal(1L, await ScalarLongAsync(db, "SELECT ReservedSlotCount FROM AccountAllocations"));
    }

    [Fact]
    public async Task SubmitBoundaryPersistsDescriptorStateAndDailyCountExactlyOnce()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Expanded");
        var operation = Operation(resource, account, 1, "Preparing", "PreparingToSubmit", "Reserved");
        await db.SeedAsync(
            [Account(account, 1)],
            job: Job(batch, "TaskCount", "Active", 1, 1),
            resources: [resource], operations: [operation],
            allocations: [Allocation(batch, account, 1, 1, reserved: 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var persistence = new SubmissionBoundaryPersistence(
            new SubmissionBoundaryCommand(operation.Id, account, "{\"descriptor\":true}", Now.AddMinutes(5)),
            "2026-08-19", Now);

        var first = await store.PersistAsync(persistence);
        var repeat = await store.PersistAsync(persistence);

        Assert.True(first.Accepted);
        Assert.True(first.WasNewlyCounted);
        Assert.True(repeat.Accepted);
        Assert.False(repeat.WasNewlyCounted);
        Assert.Equal(1L, await ScalarLongAsync(db, "SELECT DailyExecutedCount FROM Accounts"));
        Assert.Equal(1L, await ScalarLongAsync(db, "SELECT AttemptedItemCount FROM AccountAllocations"));
        Assert.Equal("Submitting", await ScalarStringAsync(db, "SELECT SubmitState FROM TransferOperations"));
        Assert.Equal("{\"descriptor\":true}", await ScalarStringAsync(db, "SELECT ConfirmationDescriptorJson FROM TransferOperations"));
    }

    [Fact]
    public async Task ReassignmentApplyUsesVersionCompareAndSingleTransaction()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Expanded");
        var operation = Operation(resource, failed, 1);
        await db.SeedAsync(
            [Account(failed, 0), Account(replacement, 0)],
            job: Job(batch, "TaskCount", "Paused", 1, 1),
            resources: [resource], operations: [operation],
            allocations: [Allocation(batch, failed, 1, 1), Allocation(batch, replacement, 2, 0)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var state = await store.LoadAsync(batch);
        Assert.NotNull(state);
        var mutation = new ReassignmentMutation(batch,
            [new ReassignmentOperationMove(operation.Id, failed, replacement)], [],
            [new ReassignmentAllocationDelta(failed, 1, 0, -1), new ReassignmentAllocationDelta(replacement, 0, 1, 1)]);

        Assert.True(await store.ApplyAsync(mutation, state.BatchVersion));
        Assert.False(await store.ApplyAsync(mutation, state.BatchVersion));
        Assert.Equal(replacement, await ScalarGuidAsync(db, "SELECT AssignedAccountId FROM TransferOperations", null));
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT AssignedItemCount FROM AccountAllocations WHERE AccountId='" + failed.ToString("D") + "'"));
        Assert.Equal(1L, await ScalarLongAsync(db, "SELECT AssignedItemCount FROM AccountAllocations WHERE AccountId='" + replacement.ToString("D") + "'"));
    }

    [Fact]
    public async Task ConcurrentReservationsStillRespectGlobalAndPerAccountLimits()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var resources = Enumerable.Range(1, 4).Select(i => Resource(batch, i, null, "Expanded")).ToArray();
        await db.SeedAsync(
            [Account(a, 0), Account(b, 0)],
            job: Job(batch, "TaskCount", "Active", 4, 4), resources: resources,
            operations:
            [
                Operation(resources[0], a, 1), Operation(resources[1], a, 2),
                Operation(resources[2], b, 3), Operation(resources[3], b, 4),
            ],
            allocations: [Allocation(batch, a, 1, 2), Allocation(batch, b, 2, 2)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => store.TryReserveNextAsync(batch, Now, Now.AddMinutes(5))));
        var reserved = results.Where(item => item is not null).Select(item => item!).ToArray();

        Assert.Equal(2, reserved.Length);
        Assert.Equal(2, reserved.Select(item => item.AccountId).Distinct().Count());
        Assert.Equal(2L, await ScalarLongAsync(db, "SELECT COUNT(*) FROM TransferOperations WHERE Status='Reserved'"));
    }

    [Fact]
    public async Task GlobalSlotCountIncludesPendingWorkOutsideCurrentActiveBatch()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var activeBatch = Guid.NewGuid();
        var pausedBatch = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var r1 = Resource(pausedBatch, 1, null, "Expanded");
        var r2 = Resource(pausedBatch, 2, null, "Expanded");
        await db.SeedAsync([Account(a, 0), Account(b, 0), Account(c, 0)],
            job: Job(pausedBatch, "TaskCount", "Paused", 2, 2), resources: [r1, r2],
            operations:
            [
                Operation(r1, a, 1, "ResultPending", "PendingConfirmation", "Reserved", "{}"),
                Operation(r2, b, 2, "Submitting", "Submitting", "Reserved", "{}"),
            ], allocations: [Allocation(pausedBatch, a, 1, 1, 1), Allocation(pausedBatch, b, 2, 1, 1)]);
        await db.SeedAsync([Account(a, 0), Account(b, 0), Account(c, 0)],
            job: Job(activeBatch, "SuccessTarget", "Active", 1), resources: [Resource(activeBatch, 1, null)],
            allocations: [Allocation(activeBatch, c, 1, 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        var state = await store.GetStateAsync();
        var result = await store.TryReserveNextAsync(activeBatch, Now, Now.AddMinutes(5));

        Assert.Equal(2, state.ActiveSlotCount);
        Assert.Null(result);
    }

    [Fact]
    public async Task NewLocalDateResetsLimitButRejectedSubmissionDoesNotCrossBoundary()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var r1 = Resource(batch, 1, null, "Expanded");
        var r2 = Resource(batch, 2, null, "Expanded");
        var op1 = Operation(r1, account, 1, "Preparing", "PreparingToSubmit", "Reserved");
        var op2 = Operation(r2, account, 2, "Preparing", "PreparingToSubmit", "Reserved");
        await db.SeedAsync([Account(account, 1) with { DailyExecutedCount = 1 }],
            job: Job(batch, "TaskCount", "Active", 2, 2), resources: [r1, r2], operations: [op1, op2],
            allocations: [Allocation(batch, account, 1, 2, 2)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        var rejected = await store.PersistAsync(new SubmissionBoundaryPersistence(
            new SubmissionBoundaryCommand(op1.Id, account, "{}", Now.AddMinutes(5)), "2026-08-19", Now));
        var acceptedNextDay = await store.PersistAsync(new SubmissionBoundaryPersistence(
            new SubmissionBoundaryCommand(op2.Id, account, "{}", Now.AddDays(1).AddMinutes(5)), "2026-08-20", Now.AddDays(1)));

        Assert.False(rejected.Accepted);
        Assert.Equal("PreparingToSubmit", await ScalarStringAsync(db, "SELECT SubmitState FROM TransferOperations WHERE Id='" + op1.Id.ToString("D") + "'"));
        Assert.True(acceptedNextDay.Accepted);
        Assert.Equal(1L, await ScalarLongAsync(db, "SELECT DailyExecutedCount FROM Accounts"));
    }

    [Fact]
    public async Task UnsafeFreeformFailureMessageIsRejectedBeforePersistence()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        await Assert.ThrowsAsync<ArgumentException>(() => store.MarkFailedAsync(
            Guid.NewGuid(), "InvalidTargetPath", "cookie=secret", false, Now));
    }

    [Fact]
    public async Task BatchStatusChangeInvalidatesReassignmentPreviewVersion()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Expanded");
        var operation = Operation(resource, failed, 1);
        await db.SeedAsync([Account(failed, 0), Account(replacement, 0)],
            job: Job(batch, "TaskCount", "Paused", 1, 1), resources: [resource], operations: [operation],
            allocations: [Allocation(batch, failed, 1, 1), Allocation(batch, replacement, 2, 0)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var previewState = await store.LoadAsync(batch);
        await ExecuteAsync(db, "UPDATE BatchJobs SET Status='Active' WHERE Id=$id;", ("$id", batch.ToString("D")));

        var applied = await store.ApplyAsync(new ReassignmentMutation(batch,
            [new ReassignmentOperationMove(operation.Id, failed, replacement)], [],
            [new ReassignmentAllocationDelta(failed, 1, 0, -1), new ReassignmentAllocationDelta(replacement, 0, 1, 1)]),
            previewState!.BatchVersion);

        Assert.False(applied);
    }

    [Theory]
    [InlineData("ExpiredLogin", "The account session has expired.")]
    [InlineData("VerificationRequired", "Account verification is required.")]
    [InlineData("InsufficientCapacity", "The account has insufficient capacity.")]
    [InlineData("InvalidTargetPath", "The target path is invalid.")]
    public async Task AccountFailurePausesOnlyThatAccountAndAllowsAnotherAccountToContinue(string code, string message)
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var healthy = Guid.NewGuid();
        var failedResource = Resource(batch, 1, null, "Expanded");
        var healthyResource = Resource(batch, 2, null, "Expanded");
        var failedOperation = Operation(failedResource, failed, 1, "Preparing", "PreparingToSubmit", "Reserved");
        var healthyOperation = Operation(healthyResource, healthy, 2);
        await db.SeedAsync([Account(failed, 0), Account(healthy, 0)],
            job: Job(batch, "TaskCount", "Active", 2, 2), resources: [failedResource, healthyResource],
            operations: [failedOperation, healthyOperation],
            allocations: [Allocation(batch, failed, 1, 1, 1), Allocation(batch, healthy, 2, 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        await store.MarkDefiniteAccountFailureAsync(failedOperation.Id, code, message, Now);
        var next = await store.TryReserveNextAsync(batch, Now, Now.AddMinutes(5));

        Assert.Equal("Invalid", await ScalarStringAsync(db, "SELECT Status FROM Accounts WHERE Id='" + failed.ToString("D") + "'"));
        Assert.Equal("Failed", await ScalarStringAsync(db, "SELECT Status FROM TransferOperations WHERE Id='" + failedOperation.Id.ToString("D") + "'"));
        Assert.Equal("Released", await ScalarStringAsync(db, "SELECT TargetSlotState FROM TransferOperations WHERE Id='" + failedOperation.Id.ToString("D") + "'"));
        Assert.Equal(code, await ScalarStringAsync(db, "SELECT LastErrorCode FROM TransferOperations WHERE Id='" + failedOperation.Id.ToString("D") + "'"));
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT ReservedSlotCount FROM AccountAllocations WHERE AccountId='" + failed.ToString("D") + "'"));
        Assert.Equal(1L, await ScalarLongAsync(db, "SELECT FailedItemCount FROM AccountAllocations WHERE AccountId='" + failed.ToString("D") + "'"));
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT DailyExecutedCount FROM Accounts WHERE Id='" + failed.ToString("D") + "'"));
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT COUNT(*) FROM AccountAllocations WHERE ReservedSlotCount<0"));
        Assert.Equal(healthy, next?.AccountId);
    }

    [Theory]
    [InlineData("ExpiredLogin", "The account session has expired.")]
    [InlineData("VerificationRequired", "Account verification is required.")]
    [InlineData("InsufficientCapacity", "The account has insufficient capacity.")]
    [InlineData("InvalidTargetPath", "The target path is invalid.")]
    public async Task DefiniteAccountFailureAfterSubmitIsFinalAndReleasesItsSlot(string code, string message)
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var healthy = Guid.NewGuid();
        var submittedResource = Resource(batch, 1, null, "Expanded");
        var healthyResource = Resource(batch, 2, null, "Expanded");
        var submitted = Operation(
            submittedResource, failed, 1, "Submitting", "Submitting", "Reserved", "{}") with
        {
            DailyCountedAt = Now,
            SubmitStartedAt = Now,
        };
        await db.SeedAsync([Account(failed, 0) with { DailyExecutedCount = 1 }, Account(healthy, 0)],
            job: Job(batch, "TaskCount", "Active", 2, 2),
            resources: [submittedResource, healthyResource],
            operations: [submitted, Operation(healthyResource, healthy, 2)],
            allocations: [Allocation(batch, failed, 1, 1, 1), Allocation(batch, healthy, 2, 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        await store.MarkDefiniteAccountFailureAsync(
            submitted.Id,
            code,
            message,
            Now.AddSeconds(1));
        var next = await store.TryReserveNextAsync(batch, Now.AddMinutes(1), Now.AddMinutes(6));

        Assert.Equal("Invalid", await ScalarStringAsync(db, "SELECT Status FROM Accounts WHERE Id='" + failed.ToString("D") + "'"));
        Assert.Equal("Failed", await ScalarStringAsync(db, "SELECT Status FROM TransferOperations WHERE Id='" + submitted.Id.ToString("D") + "'"));
        Assert.Equal("ResultReceived", await ScalarStringAsync(db, "SELECT SubmitState FROM TransferOperations WHERE Id='" + submitted.Id.ToString("D") + "'"));
        Assert.Equal("Released", await ScalarStringAsync(db, "SELECT TargetSlotState FROM TransferOperations WHERE Id='" + submitted.Id.ToString("D") + "'"));
        Assert.Equal(code, await ScalarStringAsync(db, "SELECT LastErrorCode FROM TransferOperations WHERE Id='" + submitted.Id.ToString("D") + "'"));
        Assert.Equal(1L, await ScalarLongAsync(db, "SELECT DailyExecutedCount FROM Accounts WHERE Id='" + failed.ToString("D") + "'"));
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT ReservedSlotCount FROM AccountAllocations WHERE AccountId='" + failed.ToString("D") + "'"));
        Assert.Equal(1L, await ScalarLongAsync(db, "SELECT FailedItemCount FROM AccountAllocations WHERE AccountId='" + failed.ToString("D") + "'"));
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT COUNT(*) FROM AccountAllocations WHERE ReservedSlotCount<0"));
        Assert.Equal(healthy, next?.AccountId);
    }

    [Fact]
    public async Task DefiniteAccountFailureReleasesSuccessCandidateForAnotherAccount()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var healthy = Guid.NewGuid();
        var resource = Resource(batch, 1, null);
        await db.SeedAsync([Account(failed, 0), Account(healthy, 0)],
            job: Job(batch, "SuccessTarget", "Active", 1),
            resources: [resource],
            allocations: [Allocation(batch, failed, 1, 1), Allocation(batch, healthy, 2, 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var first = await store.TryReserveNextAsync(batch, Now, Now.AddMinutes(5));
        Assert.Equal(failed, first?.AccountId);
        await store.MarkPreparingAsync(first!.OperationId, Now.AddSeconds(1));

        await store.MarkDefiniteAccountFailureAsync(
            first.OperationId,
            "InsufficientCapacity",
            "The account has insufficient capacity.",
            Now.AddSeconds(2));
        var replacement = await store.TryReserveNextAsync(batch, Now.AddMinutes(1), Now.AddMinutes(6));

        Assert.Equal(healthy, replacement?.AccountId);
        Assert.Equal("Reserved", await ScalarStringAsync(db, "SELECT CandidateStatus FROM ResourceItems WHERE Id='" + resource.Id.ToString("D") + "'"));
        Assert.Equal(healthy, await ScalarGuidAsync(db, "SELECT ReservedAccountId FROM ResourceItems WHERE Id=$id", resource.Id));
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT ReservedSlotCount FROM AccountAllocations WHERE AccountId='" + failed.ToString("D") + "'"));
        Assert.Equal(1L, await ScalarLongAsync(db, "SELECT FailedItemCount FROM AccountAllocations WHERE AccountId='" + failed.ToString("D") + "'"));
        Assert.Equal(1L, await ScalarLongAsync(db, "SELECT ReservedSlotCount FROM AccountAllocations WHERE AccountId='" + healthy.ToString("D") + "'"));
    }

    [Fact]
    public async Task DefiniteAccountFailureRollsBackInsteadOfMakingReservedCountNegative()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Expanded");
        var operation = Operation(resource, account, 1, "Preparing", "PreparingToSubmit", "Reserved");
        await db.SeedAsync([Account(account, 0)],
            job: Job(batch, "TaskCount", "Active", 1, 1),
            resources: [resource],
            operations: [operation],
            allocations: [Allocation(batch, account, 1, 1, reserved: 0)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MarkDefiniteAccountFailureAsync(
            operation.Id,
            "ExpiredLogin",
            "The account session has expired.",
            Now));

        Assert.Equal("Active", await ScalarStringAsync(db, "SELECT Status FROM Accounts WHERE Id='" + account.ToString("D") + "'"));
        Assert.Equal("Preparing", await ScalarStringAsync(db, "SELECT Status FROM TransferOperations WHERE Id='" + operation.Id.ToString("D") + "'"));
        Assert.Equal("Reserved", await ScalarStringAsync(db, "SELECT TargetSlotState FROM TransferOperations WHERE Id='" + operation.Id.ToString("D") + "'"));
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT ReservedSlotCount FROM AccountAllocations"));
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT FailedItemCount FROM AccountAllocations"));
    }

    [Fact]
    public async Task CancellationBeforeReservationLeavesPersistentStateUntouched()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Expanded");
        await db.SeedAsync([Account(account, 0)], job: Job(batch, "TaskCount", "Active", 1, 1),
            resources: [resource], operations: [Operation(resource, account, 1)],
            allocations: [Allocation(batch, account, 1, 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.TryReserveNextAsync(batch, Now, Now.AddMinutes(5), cancellation.Token));

        Assert.Equal("Waiting", await ScalarStringAsync(db, "SELECT Status FROM TransferOperations"));
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT ReservedSlotCount FROM AccountAllocations"));
    }

    private static AccountRecord Account(Guid id, int dailyLimit) =>
        AccountRecord.Create(id, $"baidu-{id:N}", "Account") with { DailyLimit = dailyLimit, DailyCountDateKey = "2026-08-19" };

    private static BatchJobRecord Job(Guid id, string quota, string status, int resourceCount, int operationCount = 0) =>
        new(id, "Batch", "Text", "Distribution", quota, "ProportionalReduction", resourceCount,
            operationCount, "/base", "OptionalOuter", "Skip", "Immediate", status, Now, Now, null, null, 1);

    private static ResourceItemRecord Resource(Guid batch, long sequence, Guid? constraint, string status = "Unallocated") =>
        new(Guid.NewGuid(), batch, null, sequence, $"https://pan.baidu.com/s/{sequence}", $"share-{sequence}", "a1b2",
            null, null, null, null, "OptionalOuter", "Unknown", constraint, status, null, null, Now, Now);

    private static TransferOperationRecord Operation(
        ResourceItemRecord resource,
        Guid account,
        long ordinal,
        string status = "Waiting",
        string submitState = "NotPrepared",
        string slotState = "Unoccupied",
        string? descriptor = null) =>
        new(Guid.NewGuid(), resource.Id, resource.BatchJobId, resource.CandidateSequence, ordinal,
            "/base", "/base", "", account, "Weighted", status, null, 0, $"fp-{ordinal}", descriptor,
            null, null, submitState, slotState, slotState == "Reserved" ? Now : null, null, null,
            null, null, null, Now, Now);

    private static AccountAllocationRecord Allocation(Guid batch, Guid account, int priority, int target, int reserved = 0) =>
        new(Guid.NewGuid(), batch, account, priority, "/base", "/base", target, target, target,
            target, 0, 0, 0, 0, 0, 0, 0, 0, reserved, 0);

    private static async Task<long> ScalarLongAsync(BatchTestDatabase db, string sql)
    {
        await using var connection = await db.ConnectionFactory.OpenConnectionAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ScalarStringAsync(BatchTestDatabase db, string sql)
    {
        await using var connection = await db.ConnectionFactory.OpenConnectionAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }

    private static async Task<Guid> ScalarGuidAsync(BatchTestDatabase db, string sql, Guid? id)
    {
        await using var connection = await db.ConnectionFactory.OpenConnectionAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        if (id is not null) command.Parameters.AddWithValue("$id", id.Value.ToString("D"));
        return Guid.Parse(Convert.ToString(await command.ExecuteScalarAsync())!);
    }

    private static async Task ExecuteAsync(BatchTestDatabase db, string sql, params (string Name, object? Value)[] values)
    {
        await using var connection = await db.ConnectionFactory.OpenConnectionAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }
}
