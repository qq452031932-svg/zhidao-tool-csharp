using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Database;

namespace BaiduBatchTransfer.Application.Tests.Jobs;

public sealed class BatchRunControlServiceTests
{
    private static readonly DateTimeOffset Now = BatchTestData.Now;

    [Fact]
    public async Task PauseActiveBatchBlocksNewClaimsWithoutRewritingSubmittedWork()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batchId = Guid.NewGuid();
        var submittedAccount = Guid.NewGuid();
        var waitingAccount = Guid.NewGuid();
        var submittedResource = Resource(batchId, 1);
        var waitingResource = Resource(batchId, 2);
        var submitted = Operation(submittedResource, submittedAccount, 1,
            status: "Submitting", submitState: "Submitting", slotState: "Reserved",
            descriptor: "{\"operation\":\"submitted\"}", dailyCountedAt: Now, submitStartedAt: Now);
        var waiting = Operation(waitingResource, waitingAccount, 2);
        await db.SeedAsync(
            [Account(submittedAccount), Account(waitingAccount)],
            job: Job(batchId, "Active", 2, 2),
            resources: [submittedResource, waitingResource],
            operations: [submitted, waiting],
            allocations:
            [
                Allocation(batchId, submittedAccount, 1, reserved: 1),
                Allocation(batchId, waitingAccount, 2),
            ]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        var disposition = await new PauseBatchService(store).PauseAsync(batchId);
        var claimed = await store.TryReserveNextAsync(batchId, Now.AddMinutes(1), Now.AddMinutes(6));

        Assert.Equal(PauseBatchDisposition.Paused, disposition);
        Assert.Null(claimed);
        Assert.Equal("Paused", await ScalarStringAsync(db, "SELECT Status FROM BatchJobs WHERE Id=$id", batchId));
        Assert.Equal("Submitting", await ScalarStringAsync(db, "SELECT Status FROM TransferOperations WHERE Id=$id", submitted.Id));
        Assert.Equal("Submitting", await ScalarStringAsync(db, "SELECT SubmitState FROM TransferOperations WHERE Id=$id", submitted.Id));
        Assert.Null(await ScalarNullableStringAsync(db, "SELECT LastErrorCode FROM TransferOperations WHERE Id=$id", submitted.Id));
    }

    [Fact]
    public async Task ResumeQueuesBatchAndLeavesActivationToNormalPromoter()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var pausedBatchId = Guid.NewGuid();
        var activeBatchId = Guid.NewGuid();
        await db.SeedAsync([], job: Job(pausedBatchId, "Paused", 0, 0));
        await AddJobAsync(db, Job(activeBatchId, "Active", 0, 0));
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        var disposition = await new ResumeBatchService(store).ResumeAsync(pausedBatchId);

        Assert.Equal(ResumeBatchDisposition.Queued, disposition);
        Assert.Equal("Queued", await ScalarStringAsync(db, "SELECT Status FROM BatchJobs WHERE Id=$id", pausedBatchId));
        Assert.Null(await store.TryActivateNextBatchAsync(Now.AddMinutes(1)));
        await ExecuteAsync(db, "UPDATE BatchJobs SET Status='Completed',CompletedAt=$now WHERE Id=$id;",
            ("$now", Now.ToString("O")), ("$id", activeBatchId.ToString("D")));
        Assert.Equal(pausedBatchId, await store.TryActivateNextBatchAsync(Now.AddMinutes(2)));
    }

    [Fact]
    public async Task ConcurrentPauseAndResumeCallsEachTransitionOnlyOnce()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batchId = Guid.NewGuid();
        await db.SeedAsync([], job: Job(batchId, "Active", 0, 0));
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var pause = new PauseBatchService(store);
        var resume = new ResumeBatchService(store);

        var pauseResults = await Task.WhenAll(pause.PauseAsync(batchId), pause.PauseAsync(batchId));
        var resumeResults = await Task.WhenAll(resume.ResumeAsync(batchId), resume.ResumeAsync(batchId));

        Assert.Equal(1, pauseResults.Count(value => value == PauseBatchDisposition.Paused));
        Assert.Equal(1, pauseResults.Count(value => value == PauseBatchDisposition.AlreadyPaused));
        Assert.Equal(1, resumeResults.Count(value => value == ResumeBatchDisposition.Queued));
        Assert.Equal(1, resumeResults.Count(value => value == ResumeBatchDisposition.AlreadyResumed));
        Assert.Equal("Queued", await ScalarStringAsync(db, "SELECT Status FROM BatchJobs WHERE Id=$id", batchId));
    }

    [Fact]
    public async Task SafeFailedTransferRequeuesExactlyOnceAndReopensShortfallBatchAsQueued()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batchId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var resource = Resource(batchId, 1);
        var operation = Operation(resource, accountId, 1,
            status: "Failed", outcomeKind: "Failed", submitState: "PreparingToSubmit",
            slotState: "Released", attemptCount: 3, errorCode: "TransientService",
            errorMessage: "The service is temporarily unavailable.");
        await db.SeedAsync([Account(accountId)],
            job: Job(batchId, "CompletedWithShortfall", 1, 1, quotaBasis: "SuccessTarget"),
            resources: [resource], operations: [operation],
            allocations: [Allocation(batchId, accountId, 1, failed: 1, effectiveTarget: 0)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var service = new RetryFailedTransferService(store, new FixedTimeProvider(Now.AddMinutes(1)));

        var results = await Task.WhenAll(service.RetryAsync(operation.Id), service.RetryAsync(operation.Id));

        Assert.Equal(1, results.Count(value => value == RetryFailedTransferDisposition.Requeued));
        Assert.Equal(1, results.Count(value => value == RetryFailedTransferDisposition.NotRetryable));
        Assert.Equal("Waiting", await ScalarStringAsync(db, "SELECT Status FROM TransferOperations WHERE Id=$id", operation.Id));
        Assert.Equal("PreparingToSubmit", await ScalarStringAsync(db, "SELECT SubmitState FROM TransferOperations WHERE Id=$id", operation.Id));
        Assert.Equal(4, await ScalarLongAsync(db, "SELECT AttemptCount FROM TransferOperations WHERE Id=$id", operation.Id));
        Assert.Equal(0, await ScalarLongAsync(db, "SELECT FailedItemCount FROM AccountAllocations WHERE BatchJobId=$id", batchId));
        Assert.Equal(1, await ScalarLongAsync(db, "SELECT EffectiveTargetSuccessCount FROM AccountAllocations WHERE BatchJobId=$id", batchId));
        Assert.Equal("Queued", await ScalarStringAsync(db, "SELECT Status FROM BatchJobs WHERE Id=$id", batchId));
        Assert.Null(await ScalarNullableStringAsync(db, "SELECT CompletedAt FROM BatchJobs WHERE Id=$id", batchId));
        Assert.Equal("Reserved", await ScalarStringAsync(db, "SELECT CandidateStatus FROM ResourceItems WHERE Id=$id", resource.Id));
        Assert.Equal(accountId.ToString("D"), await ScalarStringAsync(db, "SELECT ReservedAccountId FROM ResourceItems WHERE Id=$id", resource.Id));
        Assert.Equal(operation.OperationFingerprint,
            await ScalarStringAsync(db, "SELECT OperationFingerprint FROM TransferOperations WHERE Id=$id", operation.Id));
        Assert.Equal(batchId, await store.TryActivateNextBatchAsync(Now.AddMinutes(2)));
        Assert.NotNull(await store.TryReserveNextAsync(batchId, Now.AddMinutes(3), Now.AddMinutes(8)));
        await store.MarkPreparingAsync(operation.Id, Now.AddMinutes(4), expectedAttemptCount: 4);
        var boundary = await store.PersistAsync(new SubmissionBoundaryPersistence(
            new SubmissionBoundaryCommand(
                operation.Id, accountId, "{\"operation\":\"retry\"}", Now.AddMinutes(9),
                ExpectedAttemptCount: 4),
            "2026-08-19",
            Now.AddMinutes(5)));
        Assert.True(boundary.Accepted);
        await store.MarkSucceededAsync(operation.Id, Now.AddMinutes(6), expectedAttemptCount: 4);
        Assert.Equal("Expanded", await ScalarStringAsync(db, "SELECT CandidateStatus FROM ResourceItems WHERE Id=$id", resource.Id));
        Assert.Null(await ScalarNullableStringAsync(db, "SELECT ReservedAccountId FROM ResourceItems WHERE Id=$id", resource.Id));
    }

    [Fact]
    public async Task ResultPendingAndUnknownFailedSubmissionRequireReconciliation()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batchId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var pendingResource = Resource(batchId, 1);
        var failedResource = Resource(batchId, 2);
        var pending = Operation(pendingResource, accountId, 1,
            status: "ResultPending", submitState: "PendingConfirmation", slotState: "Reserved",
            descriptor: "{\"operation\":\"pending\"}", dailyCountedAt: Now, submitStartedAt: Now,
            errorCode: "UnknownResponse", errorMessage: "The service returned an unrecognized response.");
        var failedUnknown = Operation(failedResource, accountId, 2,
            status: "Failed", outcomeKind: "Failed", submitState: "PendingConfirmation", slotState: "Released",
            descriptor: "{\"operation\":\"unknown\"}", dailyCountedAt: Now, submitStartedAt: Now,
            errorCode: "UnknownResponse", errorMessage: "The service returned an unrecognized response.");
        await db.SeedAsync([Account(accountId)], job: Job(batchId, "Paused", 2, 2),
            resources: [pendingResource, failedResource], operations: [pending, failedUnknown],
            allocations: [Allocation(batchId, accountId, 1, reserved: 1, failed: 1)]);
        var service = new RetryFailedTransferService(new SqliteTransferSchedulingStore(db.ConnectionFactory));

        Assert.Equal(RetryFailedTransferDisposition.NeedsReconciliation, await service.RetryAsync(pending.Id));
        Assert.Equal(RetryFailedTransferDisposition.NeedsReconciliation, await service.RetryAsync(failedUnknown.Id));
        Assert.Equal("ResultPending", await ScalarStringAsync(db, "SELECT Status FROM TransferOperations WHERE Id=$id", pending.Id));
        Assert.Equal("Failed", await ScalarStringAsync(db, "SELECT Status FROM TransferOperations WHERE Id=$id", failedUnknown.Id));
    }

    [Fact]
    public async Task LateWorkerCannotOverwriteRequeuedAttemptState()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batchId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var resource = Resource(batchId, 1);
        var operation = Operation(resource, accountId, 1,
            status: "Failed", outcomeKind: "Failed", submitState: "PreparingToSubmit",
            slotState: "Released", attemptCount: 2, errorCode: "TransientService",
            errorMessage: "The service is temporarily unavailable.");
        await db.SeedAsync([Account(accountId)], job: Job(batchId, "Active", 1, 1),
            resources: [resource], operations: [operation],
            allocations: [Allocation(batchId, accountId, 1, failed: 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        Assert.Equal(RetryFailedTransferDisposition.Requeued,
            await new RetryFailedTransferService(store, new FixedTimeProvider(Now.AddMinutes(1))).RetryAsync(operation.Id));
        Assert.NotNull(await store.TryReserveNextAsync(batchId, Now.AddMinutes(2), Now.AddMinutes(7)));
        await store.MarkPreparingAsync(
            operation.Id, Now.AddMinutes(3), expectedAttemptCount: 3);
        var currentBoundary = await store.PersistAsync(new SubmissionBoundaryPersistence(
            new SubmissionBoundaryCommand(
                operation.Id, accountId, "{\"operation\":\"current\"}", Now.AddMinutes(8),
                ExpectedAttemptCount: 3),
            "2026-08-19",
            Now.AddMinutes(4)));
        Assert.True(currentBoundary.Accepted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PersistAsync(
            new SubmissionBoundaryPersistence(
                new SubmissionBoundaryCommand(
                    operation.Id, accountId, "{\"operation\":\"stale\"}", Now.AddMinutes(8),
                    ExpectedAttemptCount: 2),
                "2026-08-19",
                Now.AddMinutes(5))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MarkFailedAsync(
            operation.Id, "TransientService", "The service is temporarily unavailable.", false,
            Now.AddMinutes(6), expectedAttemptCount: 2));

        Assert.Equal("Submitting", await ScalarStringAsync(db, "SELECT Status FROM TransferOperations WHERE Id=$id", operation.Id));
        Assert.Equal(3, await ScalarLongAsync(db, "SELECT AttemptCount FROM TransferOperations WHERE Id=$id", operation.Id));
        Assert.Equal(1, await ScalarLongAsync(db, "SELECT DailyExecutedCount FROM Accounts WHERE Id=$id", accountId));
        Assert.Null(await ScalarNullableStringAsync(db, "SELECT LastErrorCode FROM TransferOperations WHERE Id=$id", operation.Id));
    }

    [Fact]
    public async Task CountedSubmittedFailureCannotRetryWithoutPersistedAcceptanceProof()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batchId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var resource = Resource(batchId, 1);
        var operation = Operation(resource, accountId, 1,
            status: "Failed", outcomeKind: "Failed", submitState: "ResultReceived", slotState: "Released",
            attemptCount: 1, descriptor: "{\"operation\":\"definite-failure\"}", adapterRequestId: "request-1",
            dailyCountedAt: Now, submitStartedAt: Now, errorCode: "TransientService",
            errorMessage: "The service is temporarily unavailable.");
        await db.SeedAsync([Account(accountId) with { DailyExecutedCount = 1, DailyCountDateKey = "2026-08-19" }],
            job: Job(batchId, "Active", 1, 1), resources: [resource], operations: [operation],
            allocations: [Allocation(batchId, accountId, 1, failed: 1, attempted: 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        Assert.Equal(RetryFailedTransferDisposition.NeedsReconciliation,
            await new RetryFailedTransferService(store, new FixedTimeProvider(Now.AddMinutes(1))).RetryAsync(operation.Id));

        Assert.Equal(1, await ScalarLongAsync(db, "SELECT DailyExecutedCount FROM Accounts WHERE Id=$id", accountId));
        Assert.Equal(1, await ScalarLongAsync(db, "SELECT AttemptedItemCount FROM AccountAllocations WHERE BatchJobId=$id", batchId));
        Assert.Equal("Failed", await ScalarStringAsync(db, "SELECT Status FROM TransferOperations WHERE Id=$id", operation.Id));
        Assert.Equal(1, await ScalarLongAsync(db, "SELECT AttemptCount FROM TransferOperations WHERE Id=$id", operation.Id));
        Assert.Equal(operation.Id.ToString("D"), await ScalarStringAsync(db, "SELECT Id FROM TransferOperations WHERE Id=$id", operation.Id));
        Assert.Equal(resource.Id.ToString("D"), await ScalarStringAsync(db, "SELECT ResourceItemId FROM TransferOperations WHERE Id=$id", operation.Id));
        Assert.Equal(accountId.ToString("D"), await ScalarStringAsync(db, "SELECT AssignedAccountId FROM TransferOperations WHERE Id=$id", operation.Id));
        Assert.Equal(operation.OperationFingerprint,
            await ScalarStringAsync(db, "SELECT OperationFingerprint FROM TransferOperations WHERE Id=$id", operation.Id));
    }

    [Fact]
    public async Task TwoSequentialShortfallRetriesEachRestoreOneSlotAndCanCompleteNormally()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batchId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var firstResource = Resource(batchId, 1);
        var secondResource = Resource(batchId, 2);
        var first = Operation(firstResource, accountId, 1,
            status: "Failed", outcomeKind: "Failed", submitState: "NotPrepared", slotState: "Released",
            errorCode: "TransientService", errorMessage: "The service is temporarily unavailable.");
        var second = Operation(secondResource, accountId, 2,
            status: "Failed", outcomeKind: "Failed", submitState: "PreparingToSubmit", slotState: "Released",
            errorCode: "TransientService", errorMessage: "The service is temporarily unavailable.");
        await db.SeedAsync([Account(accountId)],
            job: Job(batchId, "CompletedWithShortfall", 2, 2, quotaBasis: "SuccessTarget"),
            resources: [firstResource, secondResource], operations: [first, second],
            allocations: [Allocation(batchId, accountId, 1, failed: 2, effectiveTarget: 0,
                targetItems: 2, targetSuccesses: 2, assigned: 2)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var retry = new RetryFailedTransferService(store, new FixedTimeProvider(Now.AddMinutes(1)));

        Assert.Equal(RetryFailedTransferDisposition.Requeued, await retry.RetryAsync(first.Id));
        Assert.Equal(RetryFailedTransferDisposition.Requeued, await retry.RetryAsync(second.Id));
        Assert.Equal(2, await ScalarLongAsync(db,
            "SELECT EffectiveTargetSuccessCount FROM AccountAllocations WHERE BatchJobId=$id", batchId));
        Assert.Equal(0, await ScalarLongAsync(db,
            "SELECT FailedItemCount FROM AccountAllocations WHERE BatchJobId=$id", batchId));

        Assert.Equal(batchId, await store.TryActivateNextBatchAsync(Now.AddMinutes(2)));
        await CompleteRetryAsync(store, batchId, first, accountId, minute: 3);
        await CompleteRetryAsync(store, batchId, second, accountId, minute: 7);

        Assert.Equal(BatchExhaustionOutcome.Completed,
            await store.TryFinalizeExhaustedBatchAsync(batchId, Now.AddMinutes(11)));
        Assert.Equal("Completed", await ScalarStringAsync(db, "SELECT Status FROM BatchJobs WHERE Id=$id", batchId));
        Assert.Equal(2, await ScalarLongAsync(db,
            "SELECT FulfilledCount FROM AccountAllocations WHERE BatchJobId=$id", batchId));
    }

    [Fact]
    public async Task RetryWhileActiveAtCurrentEffectiveTargetAddsAClaimableSlot()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batchId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var resource = Resource(batchId, 1);
        var operation = Operation(resource, accountId, 1,
            status: "Failed", outcomeKind: "Failed", submitState: "NotPrepared", slotState: "Released",
            errorCode: "TransientService", errorMessage: "The service is temporarily unavailable.");
        await db.SeedAsync([Account(accountId)],
            job: Job(batchId, "Active", 1, 1, quotaBasis: "SuccessTarget"),
            resources: [resource], operations: [operation],
            allocations: [Allocation(batchId, accountId, 1, failed: 1, effectiveTarget: 1,
                targetItems: 2, targetSuccesses: 2, fulfilled: 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        Assert.Equal(RetryFailedTransferDisposition.Requeued,
            await new RetryFailedTransferService(store, new FixedTimeProvider(Now.AddMinutes(1))).RetryAsync(operation.Id));
        Assert.Equal(2, await ScalarLongAsync(db,
            "SELECT EffectiveTargetSuccessCount FROM AccountAllocations WHERE BatchJobId=$id", batchId));
        Assert.Equal(operation.Id,
            (await store.TryReserveNextAsync(batchId, Now.AddMinutes(2), Now.AddMinutes(7)))?.OperationId);
    }

    [Fact]
    public async Task RetryCannotIncreaseSuccessCapacityPastTheOriginalTarget()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batchId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var resource = Resource(batchId, 1);
        var operation = Operation(resource, accountId, 1,
            status: "Failed", outcomeKind: "Failed", submitState: "NotPrepared", slotState: "Released",
            errorCode: "TransientService", errorMessage: "The service is temporarily unavailable.");
        await db.SeedAsync([Account(accountId)],
            job: Job(batchId, "Active", 1, 1, quotaBasis: "SuccessTarget"),
            resources: [resource], operations: [operation],
            allocations: [Allocation(batchId, accountId, 1, failed: 1, effectiveTarget: 1, fulfilled: 1)]);

        Assert.Equal(RetryFailedTransferDisposition.NotRetryable,
            await new RetryFailedTransferService(new SqliteTransferSchedulingStore(db.ConnectionFactory))
                .RetryAsync(operation.Id));
        Assert.Equal("Failed", await ScalarStringAsync(db,
            "SELECT Status FROM TransferOperations WHERE Id=$id", operation.Id));
        Assert.Equal(1, await ScalarLongAsync(db,
            "SELECT EffectiveTargetSuccessCount FROM AccountAllocations WHERE BatchJobId=$id", batchId));
        Assert.Equal(1, await ScalarLongAsync(db,
            "SELECT FailedItemCount FROM AccountAllocations WHERE BatchJobId=$id", batchId));
    }

    [Fact]
    public async Task RetryAtOriginalTargetReusesAnUnoccupiedSlotWithoutIncreasingCapacity()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batchId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var resource = Resource(batchId, 1);
        var operation = Operation(resource, accountId, 1,
            status: "Failed", outcomeKind: "Failed", submitState: "NotPrepared", slotState: "Released",
            errorCode: "TransientService", errorMessage: "The service is temporarily unavailable.");
        await db.SeedAsync([Account(accountId)],
            job: Job(batchId, "Active", 1, 1, quotaBasis: "SuccessTarget"),
            resources: [resource], operations: [operation],
            allocations: [Allocation(batchId, accountId, 1, failed: 1, effectiveTarget: 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        Assert.Equal(RetryFailedTransferDisposition.Requeued,
            await new RetryFailedTransferService(store).RetryAsync(operation.Id));
        Assert.Equal(1, await ScalarLongAsync(db,
            "SELECT EffectiveTargetSuccessCount FROM AccountAllocations WHERE BatchJobId=$id", batchId));
        Assert.Equal(operation.Id,
            (await store.TryReserveNextAsync(batchId, Now.AddMinutes(1), Now.AddMinutes(6)))?.OperationId);
    }

    [Fact]
    public async Task ConcurrentRetryRestoresCapacityOnlyOnce()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batchId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var resource = Resource(batchId, 1);
        var operation = Operation(resource, accountId, 1,
            status: "Failed", outcomeKind: "Failed", submitState: "NotPrepared", slotState: "Released",
            errorCode: "TransientService", errorMessage: "The service is temporarily unavailable.");
        await db.SeedAsync([Account(accountId)],
            job: Job(batchId, "Queued", 1, 1, quotaBasis: "SuccessTarget"),
            resources: [resource], operations: [operation],
            allocations: [Allocation(batchId, accountId, 1, failed: 1, effectiveTarget: 0,
                targetItems: 2, targetSuccesses: 2)]);
        var service = new RetryFailedTransferService(
            new SqliteTransferSchedulingStore(db.ConnectionFactory), new FixedTimeProvider(Now.AddMinutes(1)));

        var results = await Task.WhenAll(service.RetryAsync(operation.Id), service.RetryAsync(operation.Id));

        Assert.Equal(1, results.Count(result => result == RetryFailedTransferDisposition.Requeued));
        Assert.Equal(1, results.Count(result => result == RetryFailedTransferDisposition.NotRetryable));
        Assert.Equal(1, await ScalarLongAsync(db,
            "SELECT EffectiveTargetSuccessCount FROM AccountAllocations WHERE BatchJobId=$id", batchId));
    }

    [Fact]
    public async Task RetryAtMaximumAttemptCountIsRejectedWithoutMutation()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batchId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var resource = Resource(batchId, 1);
        var operation = Operation(resource, accountId, 1,
            status: "Failed", outcomeKind: "Failed", submitState: "NotPrepared", slotState: "Released",
            attemptCount: int.MaxValue, errorCode: "TransientService",
            errorMessage: "The service is temporarily unavailable.");
        await db.SeedAsync([Account(accountId)],
            job: Job(batchId, "Queued", 1, 1, quotaBasis: "SuccessTarget"),
            resources: [resource], operations: [operation],
            allocations: [Allocation(batchId, accountId, 1, failed: 1, effectiveTarget: 0)]);
        var service = new RetryFailedTransferService(new SqliteTransferSchedulingStore(db.ConnectionFactory));

        Assert.Equal(RetryFailedTransferDisposition.NotRetryable, await service.RetryAsync(operation.Id));
        Assert.Equal("Failed", await ScalarStringAsync(db,
            "SELECT Status FROM TransferOperations WHERE Id=$id", operation.Id));
        Assert.Equal(int.MaxValue, await ScalarInt32Async(db,
            "SELECT AttemptCount FROM TransferOperations WHERE Id=$id", operation.Id));
        Assert.Equal(1, await ScalarLongAsync(db,
            "SELECT FailedItemCount FROM AccountAllocations WHERE BatchJobId=$id", batchId));
        Assert.Equal(0, await ScalarLongAsync(db,
            "SELECT EffectiveTargetSuccessCount FROM AccountAllocations WHERE BatchJobId=$id", batchId));
    }

    [Fact]
    public async Task RetryAtMaximumMinusOneAllocatesFinalGenerationWithoutDoubleAdvanceAtBegin()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batchId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var resource = Resource(batchId, 1);
        var operation = Operation(resource, accountId, 1,
            status: "Failed", outcomeKind: "Failed", submitState: "NotPrepared", slotState: "Released",
            attemptCount: int.MaxValue - 1, errorCode: "TransientService",
            errorMessage: "The service is temporarily unavailable.");
        await db.SeedAsync([Account(accountId)], job: Job(batchId, "Active", 1, 1),
            resources: [resource], operations: [operation],
            allocations: [Allocation(batchId, accountId, 1, failed: 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        Assert.Equal(RetryFailedTransferDisposition.Requeued,
            await new RetryFailedTransferService(store).RetryAsync(operation.Id));
        Assert.Equal(int.MaxValue, await ScalarInt32Async(db,
            "SELECT AttemptCount FROM TransferOperations WHERE Id=$id", operation.Id));
        Assert.NotNull(await store.TryReserveNextAsync(batchId, Now.AddMinutes(1), Now.AddMinutes(6)));
        await store.MarkPreparingAsync(operation.Id, Now.AddMinutes(2), expectedAttemptCount: int.MaxValue);
        Assert.Equal(int.MaxValue, await ScalarInt32Async(db,
            "SELECT AttemptCount FROM TransferOperations WHERE Id=$id", operation.Id));
    }

    [Fact]
    public async Task ExecutionCannotBeginAtMaximumAttemptCountAndLeavesReservationIntact()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batchId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var resource = Resource(batchId, 1) with
        {
            CandidateStatus = "Reserved",
            ReservedAccountId = accountId,
        };
        var operation = Operation(resource, accountId, 1, status: "Reserved", slotState: "Reserved",
            attemptCount: int.MaxValue);
        await db.SeedAsync([Account(accountId)], job: Job(batchId, "Active", 1, 1),
            resources: [resource], operations: [operation],
            allocations: [Allocation(batchId, accountId, 1, reserved: 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MarkPreparingAsync(
            operation.Id, Now.AddMinutes(1), expectedAttemptCount: int.MaxValue));

        Assert.Equal("Reserved", await ScalarStringAsync(db,
            "SELECT Status FROM TransferOperations WHERE Id=$id", operation.Id));
        Assert.Equal("NotPrepared", await ScalarStringAsync(db,
            "SELECT SubmitState FROM TransferOperations WHERE Id=$id", operation.Id));
        Assert.Equal(int.MaxValue, await ScalarInt32Async(db,
            "SELECT AttemptCount FROM TransferOperations WHERE Id=$id", operation.Id));
    }

    [Fact]
    public async Task FreshExecutionAtMaximumMinusOneAdvancesAtomicallyToMaximum()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batchId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var resource = Resource(batchId, 1) with
        {
            CandidateStatus = "Reserved",
            ReservedAccountId = accountId,
        };
        var operation = Operation(resource, accountId, 1, status: "Reserved", slotState: "Reserved",
            attemptCount: int.MaxValue - 1);
        await db.SeedAsync([Account(accountId)], job: Job(batchId, "Active", 1, 1),
            resources: [resource], operations: [operation],
            allocations: [Allocation(batchId, accountId, 1, reserved: 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        await store.MarkPreparingAsync(
            operation.Id, Now.AddMinutes(1), expectedAttemptCount: int.MaxValue - 1);

        Assert.Equal("Preparing", await ScalarStringAsync(db,
            "SELECT Status FROM TransferOperations WHERE Id=$id", operation.Id));
        Assert.Equal(int.MaxValue, await ScalarInt32Async(db,
            "SELECT AttemptCount FROM TransferOperations WHERE Id=$id", operation.Id));
    }

    [Fact]
    public async Task CrashedMaximumGenerationBecomesTerminalAndAllowsBatchFinalization()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batchId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var resource = Resource(batchId, 1) with
        {
            CandidateStatus = "Reserved",
            ReservedAccountId = accountId,
            ReservationExpiresAt = Now.AddMinutes(-1),
        };
        var operation = Operation(resource, accountId, 1, status: "Preparing",
            submitState: "PreparingToSubmit", slotState: "Reserved", attemptCount: int.MaxValue) with
        {
            LeaseExpiresAt = Now.AddMinutes(-1),
        };
        await db.SeedAsync([Account(accountId)],
            job: Job(batchId, "Active", 1, 1, quotaBasis: "SuccessTarget"),
            resources: [resource], operations: [operation],
            allocations: [Allocation(batchId, accountId, 1, reserved: 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        var recovered = await store.RecoverExpiredLeasesAsync(Now);

        Assert.Equal(0, recovered.RetryableOperationCount);
        Assert.Equal("Failed", await ScalarStringAsync(db,
            "SELECT Status FROM TransferOperations WHERE Id=$id", operation.Id));
        Assert.Equal("AttemptLimitReached", await ScalarStringAsync(db,
            "SELECT LastErrorCode FROM TransferOperations WHERE Id=$id", operation.Id));
        Assert.Equal(int.MaxValue, await ScalarInt32Async(db,
            "SELECT AttemptCount FROM TransferOperations WHERE Id=$id", operation.Id));
        Assert.Equal("Expanded", await ScalarStringAsync(db,
            "SELECT CandidateStatus FROM ResourceItems WHERE Id=$id", resource.Id));
        Assert.Null(await ScalarNullableStringAsync(db,
            "SELECT ReservedAccountId FROM ResourceItems WHERE Id=$id", resource.Id));
        Assert.Equal(0, await ScalarLongAsync(db,
            "SELECT ReservedSlotCount FROM AccountAllocations WHERE BatchJobId=$id", batchId));
        Assert.Equal(1, await ScalarLongAsync(db,
            "SELECT FailedItemCount FROM AccountAllocations WHERE BatchJobId=$id", batchId));
        Assert.Null(await store.TryReserveNextAsync(batchId, Now.AddMinutes(1), Now.AddMinutes(6)));
        Assert.Equal(BatchExhaustionOutcome.CompletedWithShortfall,
            await store.TryFinalizeExhaustedBatchAsync(batchId, Now.AddMinutes(2)));
    }

    [Fact]
    public async Task RestartObservesPersistedPausedAndRequeuedStates()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var pausedBatchId = Guid.NewGuid();
        var retryBatchId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var resource = Resource(retryBatchId, 1);
        var operation = Operation(resource, accountId, 1,
            status: "Failed", outcomeKind: "Failed", submitState: "NotPrepared", slotState: "Released",
            errorCode: "TransientService", errorMessage: "The service is temporarily unavailable.");
        await db.SeedAsync([Account(accountId)], job: Job(pausedBatchId, "Active", 0, 0));
        await AddJobGraphAsync(db, Job(retryBatchId, "Paused", 1, 1), resource, operation,
            Allocation(retryBatchId, accountId, 1, failed: 1));
        var firstStore = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        Assert.Equal(PauseBatchDisposition.Paused, await new PauseBatchService(firstStore).PauseAsync(pausedBatchId));
        Assert.Equal(RetryFailedTransferDisposition.Requeued,
            await new RetryFailedTransferService(firstStore, new FixedTimeProvider(Now.AddMinutes(1))).RetryAsync(operation.Id));

        var restartedStore = new SqliteTransferSchedulingStore(new SqliteConnectionFactory(db.DatabasePath));

        Assert.Equal(PauseBatchDisposition.AlreadyPaused,
            await new PauseBatchService(restartedStore).PauseAsync(pausedBatchId));
        Assert.Equal("Waiting", await ScalarStringAsync(db, "SELECT Status FROM TransferOperations WHERE Id=$id", operation.Id));
        Assert.Equal("PreparingToSubmit", await ScalarStringAsync(db,
            "SELECT SubmitState FROM TransferOperations WHERE Id=$id", operation.Id));
        Assert.True((await restartedStore.LoadOperationAsync(operation.Id))!.AttemptGenerationPrepared);
        Assert.Null(await restartedStore.TryReserveNextAsync(retryBatchId, Now.AddMinutes(2), Now.AddMinutes(7)));
    }

    private static AccountRecord Account(Guid id) =>
        AccountRecord.Create(id, $"baidu-{id:N}", "Account") with { DailyCountDateKey = "2026-08-19" };

    private static BatchJobRecord Job(
        Guid id,
        string status,
        int resources,
        int operations,
        string quotaBasis = "TaskCount") =>
        new(id, "Batch", "Text", "Distribution", quotaBasis, "ProportionalReduction", resources,
            operations, "/base", "OptionalOuter", "Skip", "Immediate", status, Now,
            status is "Active" or "Paused" or "Completed" or "CompletedWithShortfall" ? Now : null,
            status is "Completed" or "CompletedWithShortfall" ? Now : null, null, 1);

    private static ResourceItemRecord Resource(Guid batchId, long sequence) =>
        new(Guid.NewGuid(), batchId, null, sequence, $"https://example.invalid/{sequence}", $"share-{sequence}",
            null, null, null, null, null, "OptionalOuter", "Unknown", null, "Expanded", null, null, Now, Now);

    private static TransferOperationRecord Operation(
        ResourceItemRecord resource,
        Guid accountId,
        long ordinal,
        string status = "Waiting",
        string? outcomeKind = null,
        string submitState = "NotPrepared",
        string slotState = "Unoccupied",
        int attemptCount = 0,
        string? descriptor = null,
        string? adapterRequestId = null,
        DateTimeOffset? dailyCountedAt = null,
        DateTimeOffset? submitStartedAt = null,
        string? errorCode = null,
        string? errorMessage = null) =>
        new(Guid.NewGuid(), resource.Id, resource.BatchJobId, resource.CandidateSequence, ordinal,
            "/base", "/base", "", accountId, "Weighted", status, outcomeKind, attemptCount, $"fp-{ordinal}",
            descriptor, adapterRequestId, null, submitState, slotState, slotState == "Reserved" ? Now : null,
            dailyCountedAt, submitStartedAt, errorCode, errorMessage, null, Now, Now);

    private static AccountAllocationRecord Allocation(
        Guid batchId,
        Guid accountId,
        int priority,
        int reserved = 0,
        int failed = 0,
        int attempted = 0,
        int effectiveTarget = 1,
        int targetItems = 1,
        int targetSuccesses = 1,
        int assigned = 1,
        int fulfilled = 0) =>
        new(Guid.NewGuid(), batchId, accountId, priority, "/base", "/base", targetItems, targetSuccesses,
            effectiveTarget, assigned, attempted, 0, 0, fulfilled, failed, 0, 0, 0, reserved, fulfilled);

    private static async Task CompleteRetryAsync(
        SqliteTransferSchedulingStore store,
        Guid batchId,
        TransferOperationRecord operation,
        Guid accountId,
        int minute)
    {
        Assert.Equal(operation.Id,
            (await store.TryReserveNextAsync(batchId, Now.AddMinutes(minute), Now.AddMinutes(minute + 5)))?.OperationId);
        var attempt = checked(operation.AttemptCount + 1);
        await store.MarkPreparingAsync(operation.Id, Now.AddMinutes(minute + 1), expectedAttemptCount: attempt);
        Assert.True((await store.PersistAsync(new SubmissionBoundaryPersistence(
            new SubmissionBoundaryCommand(operation.Id, accountId, $"{{\"operation\":\"retry-{minute}\"}}",
                Now.AddMinutes(minute + 6), ExpectedAttemptCount: attempt),
            "2026-08-19", Now.AddMinutes(minute + 2)))).Accepted);
        await store.MarkSucceededAsync(operation.Id, Now.AddMinutes(minute + 3), expectedAttemptCount: attempt);
    }

    private static async Task AddJobAsync(BatchTestDatabase db, BatchJobRecord job)
    {
        await using var unit = await db.UnitOfWorkFactory.BeginAsync();
        await unit.Jobs.AddAsync(job);
        await unit.CommitAsync();
    }

    private static async Task AddJobGraphAsync(
        BatchTestDatabase db,
        BatchJobRecord job,
        ResourceItemRecord resource,
        TransferOperationRecord operation,
        AccountAllocationRecord allocation)
    {
        await using var unit = await db.UnitOfWorkFactory.BeginAsync();
        await unit.Jobs.AddAsync(job);
        await unit.Jobs.AddResourcesAsync([resource]);
        await unit.Jobs.AddOperationsAsync([operation]);
        await unit.Jobs.AddAllocationsAsync([allocation]);
        await unit.CommitAsync();
    }

    private static async Task ExecuteAsync(BatchTestDatabase db, string sql, params (string Name, object Value)[] values)
    {
        await using var connection = await db.ConnectionFactory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarLongAsync(BatchTestDatabase db, string sql, Guid id)
    {
        await using var connection = await db.ConnectionFactory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<int> ScalarInt32Async(BatchTestDatabase db, string sql, Guid id)
    {
        await using var connection = await db.ConnectionFactory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return reader.GetInt32(0);
    }

    private static async Task<string> ScalarStringAsync(BatchTestDatabase db, string sql, Guid id) =>
        (await ScalarNullableStringAsync(db, sql, id))!;

    private static async Task<string?> ScalarNullableStringAsync(BatchTestDatabase db, string sql, Guid id)
    {
        await using var connection = await db.ConnectionFactory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
