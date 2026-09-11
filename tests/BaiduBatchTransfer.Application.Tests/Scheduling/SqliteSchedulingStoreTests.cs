using System.Collections.Immutable;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Application.Tests.Jobs;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Core.Services;
using BaiduBatchTransfer.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Application.Tests.Scheduling;

public sealed class SqliteSchedulingStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DirectBatchPolicyUsesPersistedConcurrencyAndSharePriority()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        await db.SeedAsync([], job: Job(batch, "TaskCount", "Active", 1, 1) with
        {
            DirectBackground = true,
            MaxConcurrency = 3,
            DirectActivationState = "Active",
        });
        var source = Assert.IsAssignableFrom<IBatchSchedulingPolicySource>(
            new SqliteTransferSchedulingStore(db.ConnectionFactory));

        var policy = await source.GetPolicyAsync(batch);

        Assert.Equal(3, policy.MaxConcurrency);
        Assert.True(policy.PreferReadyShares);
    }

    [Fact]
    public async Task InertDirectBatchCannotActivateUntilItsWriteBoundaryIsActivated()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        await db.SeedAsync([], job: Job(batch, "TaskCount", "Queued", 1, 1) with
        {
            DirectBackground = true,
            MaxConcurrency = 2,
            DirectActivationState = "Inert",
        });
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        Assert.Null(await store.TryActivateNextBatchAsync(Now));
        await ExecuteAsync(db,
            "UPDATE BatchJobs SET DirectActivationState='Active' WHERE Id=$id;",
            ("$id", batch.ToString("D")));

        Assert.Equal(batch, await store.TryActivateNextBatchAsync(Now));
    }

    [Fact]
    public async Task ExecutionLoadReturnsThePersistedManagedSnapshot()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1, null);
        await db.SeedAsync([Account(account, 0)]);
        var state = new SqliteManagedBrowserStateStore(db.ConnectionFactory);
        var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"managed-scheduling-{Guid.NewGuid():N}"));
        var installation = new BrowserInstallationIdentity(
            Guid.NewGuid(), BrowserFamily.Edge, directory, Path.Combine(directory, "msedge.exe"),
            "Microsoft Corporation", "Microsoft Edge", $"managed-scheduling-file-{Guid.NewGuid():N}");
        var storedInstallation = await state.CreateInstallationAsync(installation, default);
        Assert.Equal(ManagedBrowserTransitionStatus.Applied,
            (await state.TryReplaceActiveInstallationAsync(
                new ManagedBrowserInstallationActivationRequest(null, storedInstallation), default)).Status);
        var session = new ManagedBrowserSessionReference(
            Guid.NewGuid(), installation.InstallationId, account, $"baidu-{account:N}",
            BrowserFamily.Edge, $"managed-scheduling-{account:N}", $"owner-{account:N}");
        var storedSession = await state.CreateSessionAsync(session, default);
        Assert.Equal(ManagedBrowserTransitionStatus.Applied,
            (await state.TryReplaceActiveSessionAsync(
                new ManagedBrowserSessionActivationRequest(null, storedSession), default)).Status);
        var operation = Operation(resource, account, 1) with
        {
            PersistedExecutionProfile = ExecutionProfileSnapshot.Managed(
                BrowserFamily.Edge, session.ProfileId, installation.InstallationId, session.SessionId),
        };
        await db.SeedAsync([], job: Job(batch, "TaskCount", "Active", 1, 1),
            resources: [resource], operations: [operation], allocations: [Allocation(batch, account, 1, 1)]);

        var loaded = await new SqliteTransferSchedulingStore(db.ConnectionFactory)
            .LoadOperationAsync(operation.Id);

        Assert.NotNull(loaded);
        Assert.Equal(BaiduExecutionEngine.ManagedChromiumV1, loaded.ExecutionProfile.Engine);
        Assert.Equal(BrowserFamily.Edge, loaded.ExecutionProfile.BrowserFamily);
        Assert.Equal(session.ProfileId, loaded.ExecutionProfile.ProfileId);
        Assert.Equal(installation.InstallationId, loaded.ExecutionProfile.InstallationId);
        Assert.Equal(session.SessionId, loaded.ExecutionProfile.SessionId);
    }

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
    public async Task DynamicReservationUsesPersistedOrdinalCursorInsteadOfScanningHistory()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var historical = Resource(batch, 1, null, "Expanded");
        var candidate = Resource(batch, 2, null);
        await db.SeedAsync([Account(account, 0)],
            job: Job(batch, "SuccessTarget", "Active", 2, 1),
            resources: [historical, candidate],
            operations: [Operation(historical, account, 999, "Succeeded", "ResultReceived", "Fulfilled", "{}")],
            allocations: [Allocation(batch, account, 1, 2)]);
        await ExecuteAsync(db,
            "UPDATE BatchJobs SET NextAllocationOrdinal=101 WHERE Id=$id;",
            ("$id", batch.ToString("D")));
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        var reserved = await store.TryReserveNextAsync(batch, Now, Now.AddMinutes(5));

        Assert.Equal(101, reserved?.AllocationOrdinal);
        Assert.Equal(102L, await ScalarLongAsync(db, "SELECT NextAllocationOrdinal FROM BatchJobs"));
    }

    [Fact]
    public async Task DynamicReservationCursorOverflowRollsBackCandidateAndAllocation()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        await db.SeedAsync([Account(account, 0)],
            job: Job(batch, "SuccessTarget", "Active", 1),
            resources: [Resource(batch, 1, null)],
            allocations: [Allocation(batch, account, 1, 1)]);
        await ExecuteAsync(db,
            "UPDATE BatchJobs SET NextAllocationOrdinal=$ordinal WHERE Id=$id;",
            ("$ordinal", long.MaxValue), ("$id", batch.ToString("D")));
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        await Assert.ThrowsAsync<OverflowException>(() =>
            store.TryReserveNextAsync(batch, Now, Now.AddMinutes(5)));

        Assert.Equal(long.MaxValue, await ScalarLongAsync(db, "SELECT NextAllocationOrdinal FROM BatchJobs"));
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT COUNT(*) FROM TransferOperations"));
        Assert.Equal("Unallocated", await ScalarStringAsync(db, "SELECT CandidateStatus FROM ResourceItems"));
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT ReservedSlotCount FROM AccountAllocations"));
    }

    [Fact]
    public async Task AccountPriorityShortagePolicyFillsHealthyAccountsInExplicitPriorityOrder()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var firstPriority = Guid.NewGuid();
        var secondPriority = Guid.NewGuid();
        await db.SeedAsync([Account(firstPriority, 0), Account(secondPriority, 0)],
            job: Job(batch, "SuccessTarget", "Active", 2) with { ShortagePolicy = "AccountPriority" },
            resources: [Resource(batch, 1, null), Resource(batch, 2, null)],
            allocations:
            [
                Allocation(batch, firstPriority, 1, 2) with { FulfilledCount = 1 },
                Allocation(batch, secondPriority, 2, 2),
            ]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        var reserved = await store.TryReserveNextAsync(batch, Now, Now.AddMinutes(5));

        Assert.Equal(firstPriority, reserved?.AccountId);
    }

    [Fact]
    public async Task AccountPrioritySelectsAccountBeforePreferringItsExclusiveCandidate()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var publicPriority = Guid.NewGuid();
        var exclusivePriority = Guid.NewGuid();
        var publicCandidate = Resource(batch, 1, null);
        var exclusiveCandidate = Resource(batch, 2, exclusivePriority);
        await db.SeedAsync([Account(publicPriority, 0), Account(exclusivePriority, 0)],
            job: Job(batch, "SuccessTarget", "Active", 2) with { ShortagePolicy = "AccountPriority" },
            resources: [publicCandidate, exclusiveCandidate],
            allocations:
            [
                Allocation(batch, publicPriority, 1, 1),
                Allocation(batch, exclusivePriority, 2, 1),
            ]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        var reserved = await store.TryReserveNextAsync(batch, Now, Now.AddMinutes(5));

        Assert.Equal(publicPriority, reserved?.AccountId);
        Assert.Equal(publicCandidate.Id,
            await ScalarGuidAsync(db, "SELECT ResourceItemId FROM TransferOperations WHERE Id=$id", reserved!.OperationId));
    }

    [Fact]
    public async Task ProportionalSelectionUsesOriginalTargetsWithExactIntegerComparison()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var largeOriginal = Guid.NewGuid();
        var smallOriginal = Guid.NewGuid();
        var large = Allocation(batch, largeOriginal, 2, 10) with
        {
            EffectiveTargetSuccessCount = 2,
            FulfilledCount = 1,
        };
        var small = Allocation(batch, smallOriginal, 1, 2) with { FulfilledCount = 1 };
        await db.SeedAsync([Account(largeOriginal, 0), Account(smallOriginal, 0)],
            job: Job(batch, "SuccessTarget", "Active", 2),
            resources: [Resource(batch, 1, null), Resource(batch, 2, null)],
            allocations: [large, small]);
        var expected = new SuccessTargetAllocator().SelectAccount(
        [
            new SuccessTargetAccount(largeOriginal, 10, 2, 1, 0, 2),
            new SuccessTargetAccount(smallOriginal, 2, 2, 1, 0, 1),
        ]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        var reserved = await store.TryReserveNextAsync(batch, Now, Now.AddMinutes(5));

        Assert.Equal(largeOriginal, expected?.AccountId);
        Assert.Equal(expected?.AccountId, reserved?.AccountId);
    }

    [Fact]
    public async Task ProportionalSelectionDoesNotLetExclusiveCandidateOverrideAccountRatio()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var lowProgress = Guid.NewGuid();
        var higherProgress = Guid.NewGuid();
        var publicCandidate = Resource(batch, 1, null);
        var exclusiveCandidate = Resource(batch, 2, higherProgress);
        await db.SeedAsync([Account(lowProgress, 0), Account(higherProgress, 0)],
            job: Job(batch, "SuccessTarget", "Active", 2),
            resources: [publicCandidate, exclusiveCandidate],
            allocations:
            [
                Allocation(batch, lowProgress, 1, 1),
                Allocation(batch, higherProgress, 2, 2) with { FulfilledCount = 1 },
            ]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        var reserved = await store.TryReserveNextAsync(batch, Now, Now.AddMinutes(5));

        Assert.Equal(lowProgress, reserved?.AccountId);
        Assert.Equal(publicCandidate.Id,
            await ScalarGuidAsync(db, "SELECT ResourceItemId FROM TransferOperations WHERE Id=$id", reserved!.OperationId));
    }

    [Fact]
    public async Task ProportionalSelectionPreservesExactOrderingAtIntegerLimits()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var priorityTieTrap = Guid.NewGuid();
        var exactWinner = Guid.NewGuid();
        await db.SeedAsync([Account(priorityTieTrap, 0), Account(exactWinner, 0)],
            job: Job(batch, "SuccessTarget", "Active", 2),
            resources: [Resource(batch, 1, null), Resource(batch, 2, null)],
            allocations:
            [
                Allocation(batch, priorityTieTrap, 1, int.MaxValue) with
                {
                    FulfilledCount = int.MaxValue - 1,
                },
                Allocation(batch, exactWinner, 2, int.MaxValue - 1) with
                {
                    FulfilledCount = int.MaxValue - 2,
                },
            ]);
        var expected = new SuccessTargetAllocator().SelectAccount(
        [
            new SuccessTargetAccount(priorityTieTrap, int.MaxValue, int.MaxValue, int.MaxValue - 1, 0, 1),
            new SuccessTargetAccount(exactWinner, int.MaxValue - 1, int.MaxValue - 1, int.MaxValue - 2, 0, 2),
        ]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        var reserved = await store.TryReserveNextAsync(batch, Now, Now.AddMinutes(5));

        Assert.Equal(exactWinner, expected?.AccountId);
        Assert.Equal(expected?.AccountId, reserved?.AccountId);
    }

    [Fact]
    public async Task RuntimeProportionalSequenceMatchesConfirmationSimulator()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var resources = new[]
        {
            Resource(batch, 1, null),
            Resource(batch, 2, null, "Unused"),
            Resource(batch, 3, first),
            Resource(batch, 4, second),
        };
        await db.SeedAsync([Account(first, 0), Account(second, 0)],
            job: Job(batch, "SuccessTarget", "Active", 4), resources: resources,
            allocations: [Allocation(batch, first, 1, 2), Allocation(batch, second, 2, 3)]);
        var expected = new ShortageSimulator().Simulate(
            ShortagePolicy.ProportionalReduction,
            [
                new SuccessTargetAccount(first, 2, 2, 0, 0, 1),
                new SuccessTargetAccount(second, 3, 3, 0, 0, 2),
            ],
            resources.Select(resource => new AllocationCandidate(
                resource.Id,
                checked((int)resource.CandidateSequence),
                resource.CandidateConstraintAccountId,
                Enum.Parse<CandidateStatus>(resource.CandidateStatus))).ToArray());
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var actual = new List<Guid>();
        for (var index = 0; index < 4; index++)
        {
            var reserved = await store.TryReserveNextAsync(batch, Now.AddSeconds(index), Now.AddMinutes(5));
            Assert.NotNull(reserved);
            actual.Add(reserved.AccountId);
            await store.MarkPreparingAsync(reserved.OperationId, Now.AddSeconds(index));
            var boundary = await store.PersistAsync(new SubmissionBoundaryPersistence(
                new SubmissionBoundaryCommand(reserved.OperationId, reserved.AccountId, "{}", Now.AddMinutes(5)),
                "2026-08-19", Now.AddSeconds(index)));
            Assert.True(boundary.Accepted);
            await store.MarkSucceededAsync(reserved.OperationId, Now.AddSeconds(index));
        }

        Assert.Equal(expected.Decisions.Select(item => item.AccountId), actual);
    }

    [Fact]
    public async Task TwoHundredThousandCandidatesUseStatusFirstIndexDuringContinuousReservations()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        await db.SeedAsync([Account(account, 0)],
            job: Job(batch, "SuccessTarget", "Active", 200_000),
            allocations: [Allocation(batch, account, 1, 200_000)]);
        await ExecuteAsync(db,
            """
            WITH digits(n) AS (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)),
            numbers(n) AS (
                SELECT h.n*100000 + a.n*10000 + b.n*1000 + c.n*100 + d.n*10 + e.n
                FROM (SELECT 0 n UNION ALL SELECT 1) h
                CROSS JOIN digits a CROSS JOIN digits b CROSS JOIN digits c CROSS JOIN digits d CROSS JOIN digits e
            )
            INSERT INTO ResourceItems(
                Id,BatchJobId,CandidateSequence,ShareUrl,NormalizedShareId,ExtractionCode,
                FolderCreationModeOverride,GlobalValidityStatus,CandidateStatus,CreatedAt,UpdatedAt)
            SELECT printf('00000000-0000-0000-0000-%012d',n+1),$batch,n+1,
                'https://pan.baidu.com/s/'||(n+1),'share-'||(n+1),'a1b2',
                'OptionalOuter','Unknown','Unallocated',$now,$now
            FROM numbers;
            """,
            ("$batch", batch.ToString("D")), ("$now", Now.ToString("O")));

        await using (var connection = await db.ConnectionFactory.OpenConnectionAsync())
        await using (var explain = connection.CreateCommand())
        {
            explain.CommandText =
                "EXPLAIN QUERY PLAN SELECT Id,CandidateSequence FROM ResourceItems WHERE BatchJobId=$batch AND CandidateStatus=$status AND CandidateConstraintAccountId IS NULL AND GlobalValidityStatus<>'Invalid' ORDER BY CandidateSequence LIMIT 1;";
            explain.Parameters.AddWithValue("$batch", batch.ToString("D"));
            explain.Parameters.AddWithValue("$status", "Unallocated");
            var details = new List<string>();
            await using var reader = await explain.ExecuteReaderAsync();
            while (await reader.ReadAsync()) details.Add(reader.GetString(3));
            var plan = string.Join(Environment.NewLine, details);
            Assert.Contains("IX_ResourceItems_Batch_Status_Constraint_Sequence", plan, StringComparison.Ordinal);
            Assert.DoesNotContain("SCAN ResourceItems", plan, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("USE TEMP B-TREE", plan, StringComparison.OrdinalIgnoreCase);
        }

        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var operationIds = new HashSet<Guid>();
        for (var index = 0; index < 256; index++)
        {
            var reserved = await store.TryReserveNextAsync(batch, Now.AddSeconds(index), Now.AddMinutes(5).AddSeconds(index));
            Assert.NotNull(reserved);
            Assert.True(operationIds.Add(reserved.OperationId));
            Assert.Equal(index + 1, reserved.AllocationOrdinal);
            await store.MarkFailedAsync(reserved.OperationId, "InvalidLink", "The share link is invalid.", globallyInvalid: true, Now.AddSeconds(index + 1));
        }

        Assert.Equal(256L, await ScalarLongAsync(db, "SELECT COUNT(*) FROM TransferOperations"));
        Assert.Equal(257L, await ScalarLongAsync(db, "SELECT NextAllocationOrdinal FROM BatchJobs"));
    }

    [Fact]
    public async Task TwoHundredThousandTaskOperationsUseWaitingAndGlobalStatusIndexes()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        await db.SeedAsync([Account(account, 0)],
            job: Job(batch, "TaskCount", "Active", 200_000, 200_000),
            allocations: [Allocation(batch, account, 1, 200_000)]);
        await ExecuteAsync(db,
            """
            WITH digits(n) AS (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)),
            numbers(n) AS (
                SELECT h.n*100000 + a.n*10000 + b.n*1000 + c.n*100 + d.n*10 + e.n
                FROM (SELECT 0 n UNION ALL SELECT 1) h
                CROSS JOIN digits a CROSS JOIN digits b CROSS JOIN digits c CROSS JOIN digits d CROSS JOIN digits e
            )
            INSERT INTO ResourceItems(
                Id,BatchJobId,CandidateSequence,ShareUrl,NormalizedShareId,ExtractionCode,
                FolderCreationModeOverride,GlobalValidityStatus,CandidateStatus,CreatedAt,UpdatedAt)
            SELECT printf('20000000-0000-0000-0000-%012d',n+1),$batch,n+1,
                'https://pan.baidu.com/s/task-'||(n+1),'task-share-'||(n+1),'a1b2',
                'OptionalOuter','Unknown','Expanded',$now,$now
            FROM numbers;

            WITH digits(n) AS (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)),
            numbers(n) AS (
                SELECT h.n*100000 + a.n*10000 + b.n*1000 + c.n*100 + d.n*10 + e.n
                FROM (SELECT 0 n UNION ALL SELECT 1) h
                CROSS JOIN digits a CROSS JOIN digits b CROSS JOIN digits c CROSS JOIN digits d CROSS JOIN digits e
            )
            INSERT INTO TransferOperations(
                Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,NormalizedTargetPath,
                CreateFolderName,AssignedAccountId,AllocationSource,Status,OutcomeKind,AttemptCount,
                OperationFingerprint,ConfirmationDescriptorJson,SubmitState,TargetSlotState,CreatedAt,UpdatedAt,
                ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            SELECT printf('30000000-0000-0000-0000-%012d',n+1),
                printf('20000000-0000-0000-0000-%012d',n+1),$batch,n+1,n+1,'/base','/base','',$account,'Weighted',
                CASE WHEN n<199744 THEN 'Succeeded' ELSE 'Waiting' END,
                CASE WHEN n<199744 THEN 'NewTransfer' ELSE NULL END,0,'task-fp-'||(n+1),
                CASE WHEN n<199744 THEN '{}' ELSE NULL END,
                CASE WHEN n<199744 THEN 'ResultReceived' ELSE 'NotPrepared' END,
                CASE WHEN n<199744 THEN 'Fulfilled' ELSE 'Unoccupied' END,$now,$now,
                'WebView2-v1','LegacyWebView2','legacy-webview2-account-' || lower(replace($account,'-','')),NULL,NULL
            FROM numbers;
            """,
            ("$batch", batch.ToString("D")), ("$account", account.ToString("D")), ("$now", Now.ToString("O")));

        await using (var connection = await db.ConnectionFactory.OpenConnectionAsync())
        {
            await using var waiting = connection.CreateCommand();
            waiting.CommandText =
                "EXPLAIN QUERY PLAN SELECT o.Id,o.AssignedAccountId,o.AllocationOrdinal,o.AllocationSource FROM TransferOperations o JOIN Accounts a ON a.Id=o.AssignedAccountId JOIN BatchJobs b ON b.Id=o.BatchJobId JOIN AccountAllocations al ON al.BatchJobId=o.BatchJobId AND al.AccountId=o.AssignedAccountId JOIN ResourceItems r ON r.Id=o.ResourceItemId WHERE o.BatchJobId=$batch AND o.Status='Waiting' AND o.TargetSlotState IN ('Unoccupied','Released') AND r.CandidateStatus<>'GloballyInvalid' AND r.GlobalValidityStatus<>'Invalid' AND (b.QuotaBasis<>'SuccessTarget' OR al.EffectiveTargetSuccessCount-al.FulfilledCount-al.ReservedSlotCount>0) AND a.Status='Active' AND a.IsEnabled=1 AND a.IsDeleted=0 AND (a.DailyLimit=0 OR a.DailyCountDateKey<>$date OR a.DailyExecutedCount<a.DailyLimit) AND NOT EXISTS(SELECT 1 FROM TransferOperations busy WHERE busy.AssignedAccountId=o.AssignedAccountId AND busy.Status IN ('Reserved','Preparing','Submitting','ResultPending')) ORDER BY o.AllocationOrdinal,o.SequenceNumber,o.Id LIMIT 1;";
            waiting.Parameters.AddWithValue("$batch", batch.ToString("D"));
            waiting.Parameters.AddWithValue("$date", "2026-08-19");
            var waitingPlan = await ExplainDetailsAsync(waiting);
            Assert.Contains("IX_TransferOperations_Batch_Status_AllocationOrdinal", waitingPlan, StringComparison.Ordinal);
            Assert.DoesNotContain("USE TEMP B-TREE", waitingPlan, StringComparison.OrdinalIgnoreCase);

            await using var active = connection.CreateCommand();
            active.CommandText = "EXPLAIN QUERY PLAN SELECT COUNT(*) FROM TransferOperations WHERE Status IN ('Reserved','Preparing','Submitting');";
            var activePlan = await ExplainDetailsAsync(active);
            Assert.Contains("IX_TransferOperations_Status_Lease", activePlan, StringComparison.Ordinal);
            Assert.DoesNotContain("SCAN TransferOperations", activePlan, StringComparison.OrdinalIgnoreCase);
        }

        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        for (var index = 0; index < 256; index++)
        {
            var reserved = await store.TryReserveNextAsync(batch, Now.AddSeconds(index), Now.AddMinutes(5).AddSeconds(index));
            Assert.NotNull(reserved);
            Assert.Equal(199_745 + index, reserved.AllocationOrdinal);
            await store.MarkFailedAsync(reserved.OperationId,
                "InvalidLink", "The share link is invalid.", globallyInvalid: true, Now.AddSeconds(index + 1));
        }
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT COUNT(*) FROM TransferOperations WHERE Status='Waiting'"));
    }

    [Fact]
    public async Task WaitForCandidatesMovesTrulyExhaustedBatchToAwaitingCandidates()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        await db.SeedAsync([Account(account, 0)],
            job: Job(batch, "SuccessTarget", "Active", 0) with { ShortagePolicy = "WaitForCandidates" },
            allocations: [Allocation(batch, account, 1, 5)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        var outcome = await store.TryFinalizeExhaustedBatchAsync(batch, Now);

        Assert.Equal(BatchExhaustionOutcome.AwaitingCandidates, outcome);
        Assert.Equal("AwaitingCandidates", await ScalarStringAsync(db, "SELECT Status FROM BatchJobs"));
        Assert.Equal(5L, await ScalarLongAsync(db, "SELECT EffectiveTargetSuccessCount FROM AccountAllocations"));
    }

    [Theory]
    [InlineData("ProportionalReduction")]
    [InlineData("AccountPriority")]
    public async Task FiniteShortagePoliciesSettleEffectiveTargetsWhenNoReachableWorkRemains(string shortagePolicy)
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        await db.SeedAsync([Account(account, 0)],
            job: Job(batch, "SuccessTarget", "Active", 0) with { ShortagePolicy = shortagePolicy },
            allocations: [Allocation(batch, account, 1, 5) with { FulfilledCount = 2 }]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        var outcome = await store.TryFinalizeExhaustedBatchAsync(batch, Now);

        Assert.Equal(BatchExhaustionOutcome.CompletedWithShortfall, outcome);
        Assert.Equal("CompletedWithShortfall", await ScalarStringAsync(db, "SELECT Status FROM BatchJobs"));
        Assert.Equal(2L, await ScalarLongAsync(db, "SELECT EffectiveTargetSuccessCount FROM AccountAllocations"));
    }

    [Fact]
    public async Task TaskCountCompletesOnlyAfterAllOriginalTasksSucceedAndNextBatchCanActivate()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var nextBatch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Expanded");
        var succeeded = Operation(resource, account, 1, "Succeeded", "ResultReceived", "Fulfilled", "{}") with
        {
            OutcomeKind = "NewTransfer",
        };
        await db.SeedAsync([Account(account, 0)],
            job: Job(batch, "TaskCount", "Active", 1, 1), resources: [resource], operations: [succeeded],
            allocations: [Allocation(batch, account, 1, 1) with { FulfilledCount = 1 }]);
        await db.SeedAsync([Account(account, 0)],
            job: Job(nextBatch, "TaskCount", "Queued", 0),
            allocations: [Allocation(nextBatch, account, 1, 0)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        Assert.Equal(BatchExhaustionOutcome.Completed,
            await store.TryFinalizeExhaustedBatchAsync(batch, Now));
        Assert.Equal(nextBatch, await store.TryActivateNextBatchAsync(Now.AddSeconds(1)));
        Assert.Equal("Active", await ScalarStringAsync(db,
            "SELECT Status FROM BatchJobs WHERE Id='" + nextBatch.ToString("D") + "'"));
    }

    [Fact]
    public async Task ReplicationCompletesAfterAllOperationsReachTerminalStateEvenWhenOneFailed()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Expanded");
        var succeeded = Operation(resource, first, 1, "Succeeded", "ResultReceived", "Fulfilled", "{}") with
        {
            OutcomeKind = "NewTransfer",
        };
        var failed = Operation(resource, second, 2, "Failed", "ResultReceived", "Released", "{}") with
        {
            OutcomeKind = "Failed",
        };
        await db.SeedAsync([Account(first, 0), Account(second, 0)],
            job: new BatchJobRecord(batch, "Replication", "Text", "Replication", "TaskCount", "ProportionalReduction",
                1, 2, "/base", "None", "Skip", "Immediate", "Active", Now, Now, null, null, 1),
            resources: [resource], operations: [succeeded, failed],
            allocations:
            [
                Allocation(batch, first, 1, 1) with { FulfilledCount = 1 },
                Allocation(batch, second, 2, 1) with { FailedItemCount = 1 },
            ]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        Assert.Equal(BatchExhaustionOutcome.Completed,
            await store.TryFinalizeExhaustedBatchAsync(batch, Now));
        Assert.Equal("Completed", await ScalarStringAsync(db, "SELECT Status FROM BatchJobs"));
    }

    [Fact]
    public async Task TaskCountWaitingOnPausedAccountRemainsActive()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Expanded");
        await db.SeedAsync([Account(account, 0) with { Status = "Invalid" }],
            job: Job(batch, "TaskCount", "Active", 1, 1), resources: [resource],
            operations: [Operation(resource, account, 1)], allocations: [Allocation(batch, account, 1, 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        Assert.Equal(BatchExhaustionOutcome.TemporarilyBlocked,
            await store.TryFinalizeExhaustedBatchAsync(batch, Now));
        Assert.Equal("Active", await ScalarStringAsync(db, "SELECT Status FROM BatchJobs"));
    }

    [Fact]
    public async Task SuccessTargetUsesOriginalTargetForTerminalShortfallAndPreservesEffectiveTarget()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        await db.SeedAsync([Account(account, 0)],
            job: Job(batch, "SuccessTarget", "Active", 0),
            allocations:
            [
                Allocation(batch, account, 1, 5) with
                {
                    EffectiveTargetSuccessCount = 3,
                    FulfilledCount = 3,
                },
            ]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        Assert.Equal(BatchExhaustionOutcome.CompletedWithShortfall,
            await store.TryFinalizeExhaustedBatchAsync(batch, Now));
        Assert.Equal("CompletedWithShortfall", await ScalarStringAsync(db, "SELECT Status FROM BatchJobs"));
        Assert.Equal(3L, await ScalarLongAsync(db, "SELECT EffectiveTargetSuccessCount FROM AccountAllocations"));
    }

    [Fact]
    public async Task TerminalSuccessMarksRemainingCandidatesUnusedAndRefreshesExclusiveCounts()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var publicCandidate = Resource(batch, 1, null);
        var exclusiveCandidate = Resource(batch, 2, account);
        var alreadyUnusedExclusive = Resource(batch, 3, account, "Unused");
        await db.SeedAsync([Account(account, 0)],
            job: Job(batch, "SuccessTarget", "Active", 3),
            resources: [publicCandidate, exclusiveCandidate, alreadyUnusedExclusive],
            allocations: [Allocation(batch, account, 1, 1) with { FulfilledCount = 1 }]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        Assert.Equal(BatchExhaustionOutcome.Completed,
            await store.TryFinalizeExhaustedBatchAsync(batch, Now));
        Assert.Equal(3L, await ScalarLongAsync(db,
            "SELECT COUNT(*) FROM ResourceItems WHERE CandidateStatus='Unused'"));
        Assert.Equal(2L, await ScalarLongAsync(db, "SELECT UnusedCandidateCount FROM AccountAllocations"));
        Assert.Equal(0L, await ScalarLongAsync(db,
            "SELECT COUNT(*) FROM ResourceItems WHERE ReservedAccountId IS NOT NULL OR ReservationExpiresAt IS NOT NULL"));
    }

    [Fact]
    public async Task TerminalShortfallArchivesExclusiveSurplusFromAlreadySatisfiedAccount()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var satisfied = Guid.NewGuid();
        var shortAccount = Guid.NewGuid();
        var surplus = Resource(batch, 1, satisfied);
        await db.SeedAsync([Account(satisfied, 0), Account(shortAccount, 0)],
            job: Job(batch, "SuccessTarget", "Active", 1), resources: [surplus],
            allocations:
            [
                Allocation(batch, satisfied, 1, 1) with { FulfilledCount = 1 },
                Allocation(batch, shortAccount, 2, 1),
            ]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        Assert.Equal(BatchExhaustionOutcome.CompletedWithShortfall,
            await store.TryFinalizeExhaustedBatchAsync(batch, Now));
        Assert.Equal("Unused", await ScalarStringAsync(db, "SELECT CandidateStatus FROM ResourceItems"));
        Assert.Equal(1L, await ScalarLongAsync(db,
            "SELECT UnusedCandidateCount FROM AccountAllocations WHERE AccountId='" + satisfied.ToString("D") + "'"));
        Assert.Equal(0L, await ScalarLongAsync(db,
            "SELECT UnusedCandidateCount FROM AccountAllocations WHERE AccountId='" + shortAccount.ToString("D") + "'"));
    }

    [Theory]
    [InlineData("WaitForCandidates", false, BatchExhaustionOutcome.AwaitingCandidates)]
    [InlineData("WaitForCandidates", true, BatchExhaustionOutcome.AwaitingCandidates)]
    [InlineData("ProportionalReduction", false, BatchExhaustionOutcome.CompletedWithShortfall)]
    [InlineData("AccountPriority", true, BatchExhaustionOutcome.CompletedWithShortfall)]
    public async Task ExistingFailedOperationKeyDoesNotKeepExhaustedBatchActive(
        string shortagePolicy,
        bool exclusive,
        BatchExhaustionOutcome expected)
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1, exclusive ? account : null, "Unused");
        var failed = Operation(resource, account, 1, "Failed", "ResultReceived", "Released", "{}") with
        {
            AllocationSource = exclusive ? "Exclusive" : "Weighted",
            OutcomeKind = "Failed",
        };
        await db.SeedAsync([Account(account, 0)],
            job: Job(batch, "SuccessTarget", "Active", 1, 1) with { ShortagePolicy = shortagePolicy },
            resources: [resource], operations: [failed],
            allocations: [Allocation(batch, account, 1, 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        Assert.Null(await store.TryReserveNextAsync(batch, Now, Now.AddMinutes(5)));
        Assert.Equal(expected, await store.TryFinalizeExhaustedBatchAsync(batch, Now));
        Assert.Equal(expected == BatchExhaustionOutcome.AwaitingCandidates ? "AwaitingCandidates" : "CompletedWithShortfall",
            await ScalarStringAsync(db, "SELECT Status FROM BatchJobs"));
    }

    [Fact]
    public async Task ExhaustionDoesNotSettleWhileAValidCandidateIsBlockedByUnavailableAccount()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        await db.SeedAsync([Account(account, 0) with { Status = "Invalid" }],
            job: Job(batch, "SuccessTarget", "Active", 1),
            resources: [Resource(batch, 1, account)],
            allocations: [Allocation(batch, account, 1, 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        var outcome = await store.TryFinalizeExhaustedBatchAsync(batch, Now);

        Assert.Equal(BatchExhaustionOutcome.TemporarilyBlocked, outcome);
        Assert.Equal("Active", await ScalarStringAsync(db, "SELECT Status FROM BatchJobs"));
    }

    [Fact]
    public async Task ExhaustionDoesNotSettleWhileCandidateIsBlockedByCurrentDailyLimit()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        await db.SeedAsync([Account(account, 1) with { DailyExecutedCount = 1 }],
            job: Job(batch, "SuccessTarget", "Active", 1),
            resources: [Resource(batch, 1, null)],
            allocations: [Allocation(batch, account, 1, 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        var outcome = await store.TryFinalizeExhaustedBatchAsync(batch, Now);

        Assert.Equal(BatchExhaustionOutcome.TemporarilyBlocked, outcome);
        Assert.Equal("Active", await ScalarStringAsync(db, "SELECT Status FROM BatchJobs"));
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

        Assert.Equal(0, state.ActiveNetworkOperationCount);
        Assert.Contains(a, state.BusyAccountIds);
        Assert.Equal(b, reserved?.AccountId);
    }

    [Fact]
    public async Task DirectPendingReconciliationSourceReturnsOnlyActiveDirectResultPendingOperation()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var directBatch = Guid.NewGuid();
        var ordinaryBatch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var directResource = Resource(directBatch, 1, null, "Reserved") with
        {
            ReservedAccountId = account,
        };
        var ordinaryResource = Resource(ordinaryBatch, 2, null, "Reserved") with
        {
            ReservedAccountId = account,
        };
        var directOperation = Operation(
            directResource, account, 1, "ResultPending", "PendingConfirmation", "Reserved", "{}");
        var ordinaryOperation = Operation(
            ordinaryResource, account, 2, "ResultPending", "PendingConfirmation", "Reserved", "{}");
        await db.SeedAsync([Account(account, 0)],
            job: Job(directBatch, "TaskCount", "Active", 1, 1) with
            {
                DirectBackground = true,
                DirectActivationState = "Active",
            },
            resources: [directResource], operations: [directOperation],
            allocations: [Allocation(directBatch, account, 1, 1, reserved: 1)]);
        await db.SeedAsync([], job: Job(ordinaryBatch, "TaskCount", "Active", 1, 1),
            resources: [ordinaryResource], operations: [ordinaryOperation],
            allocations: [Allocation(ordinaryBatch, account, 1, 1, reserved: 1)]);
        var source = Assert.IsAssignableFrom<IPendingTransferReconciliationSource>(
            new SqliteTransferSchedulingStore(db.ConnectionFactory));

        var selected = await source.TryGetPendingDirectOperationAsync();

        Assert.Equal(directOperation.Id, selected);
    }

    [Fact]
    public async Task ExpiredPreSubmissionLeaseReturnsSameOperationToWaitingWithoutLosingCandidate()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Reserved") with
        {
            ReservedAccountId = account,
            ReservationExpiresAt = Now.AddMinutes(-1),
        };
        var operation = Operation(resource, account, 1, "Preparing", "PreparingToSubmit", "Reserved") with
        {
            LeaseExpiresAt = Now.AddMinutes(-1),
        };
        await db.SeedAsync([Account(account, 0)],
            job: Job(batch, "SuccessTarget", "Active", 1, 1),
            resources: [resource], operations: [operation],
            allocations: [Allocation(batch, account, 1, 1, reserved: 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        var recovered = await store.RecoverExpiredLeasesAsync(Now);

        Assert.Equal(1, recovered.RetryableOperationCount);
        Assert.Equal(0, recovered.ResultPendingOperationCount);
        Assert.Equal("Waiting", await ScalarStringAsync(db, "SELECT Status FROM TransferOperations"));
        Assert.Equal("Unoccupied", await ScalarStringAsync(db, "SELECT TargetSlotState FROM TransferOperations"));
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT ReservedSlotCount FROM AccountAllocations"));
        Assert.Equal("Reserved", await ScalarStringAsync(db, "SELECT CandidateStatus FROM ResourceItems"));
        Assert.Equal(account, await ScalarGuidAsync(db, "SELECT ReservedAccountId FROM ResourceItems", null));
        var reacquired = await store.TryReserveNextAsync(batch, Now.AddSeconds(1), Now.AddMinutes(5));
        Assert.Equal(operation.Id, reacquired?.OperationId);
        Assert.Equal(1L, await ScalarLongAsync(db, "SELECT COUNT(*) FROM TransferOperations"));
    }

    [Fact]
    public async Task ExpiredSubmittingLeasesBecomeResultPendingBeforeAndAfterResponseIdentifiers()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var firstAccount = Guid.NewGuid();
        var secondAccount = Guid.NewGuid();
        var firstResource = Resource(batch, 1, null, "Expanded");
        var secondResource = Resource(batch, 2, null, "Expanded");
        var first = Operation(firstResource, firstAccount, 1, "Submitting", "Submitting", "Reserved", "{}") with
        {
            DailyCountedAt = Now.AddMinutes(-2),
            SubmitStartedAt = Now.AddMinutes(-2),
            LeaseExpiresAt = Now.AddMinutes(-1),
        };
        var second = Operation(secondResource, secondAccount, 2, "Submitting", "Submitting", "Reserved", "{}") with
        {
            AdapterRequestId = "request-returned-before-crash",
            DailyCountedAt = Now.AddMinutes(-2),
            SubmitStartedAt = Now.AddMinutes(-2),
            LeaseExpiresAt = Now.AddMinutes(-1),
        };
        await db.SeedAsync([Account(firstAccount, 0), Account(secondAccount, 0)],
            job: Job(batch, "TaskCount", "Active", 2, 2),
            resources: [firstResource, secondResource], operations: [first, second],
            allocations:
            [
                Allocation(batch, firstAccount, 1, 1, reserved: 1),
                Allocation(batch, secondAccount, 2, 1, reserved: 1),
            ]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        var recovered = await store.RecoverExpiredLeasesAsync(Now);
        var state = await store.GetStateAsync();

        Assert.Equal(0, recovered.RetryableOperationCount);
        Assert.Equal(2, recovered.ResultPendingOperationCount);
        Assert.Equal(2L, await ScalarLongAsync(db, "SELECT COUNT(*) FROM TransferOperations WHERE Status='ResultPending' AND SubmitState='PendingConfirmation'"));
        Assert.Equal(2L, await ScalarLongAsync(db, "SELECT SUM(ReservedSlotCount) FROM AccountAllocations"));
        Assert.Equal(0, state.ActiveNetworkOperationCount);
        Assert.Equal(2, state.BusyAccountIds.Count);
    }

    [Theory]
    [InlineData("InsufficientCapacity", "The account has insufficient capacity.")]
    [InlineData("InvalidTargetPath", "The target path is invalid.")]
    [InlineData("InvalidLink", "The share link is invalid.")]
    [InlineData("InvalidExtractionCode", "The extraction code is invalid.")]
    public async Task ResultPendingStoreRejectsDefinitelyNotAcceptedWithoutChangingDatabase(string code, string message)
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
            store.MarkResultPendingAsync(
                operation.Id,
                code,
                message,
                SubmissionAcceptance.DefinitelyNotAccepted,
                Now.AddSeconds(1)));

        Assert.Equal("Submitting", await ScalarStringAsync(db, "SELECT Status FROM TransferOperations"));
        Assert.Equal("Submitting", await ScalarStringAsync(db, "SELECT SubmitState FROM TransferOperations"));
        Assert.Equal("Reserved", await ScalarStringAsync(db, "SELECT TargetSlotState FROM TransferOperations"));
        Assert.Equal(1L, await ScalarLongAsync(db, "SELECT ReservedSlotCount FROM AccountAllocations"));
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT FailedItemCount FROM AccountAllocations"));
        Assert.Equal("Active", await ScalarStringAsync(db, "SELECT Status FROM Accounts"));
    }

    [Fact]
    public async Task ManagedPreparationOutcomeUnknownRetainsSlotAndCannotReturnToWaiting()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Expanded");
        var operation = Operation(resource, account, 1, "Preparing", "PreparingToSubmit", "Reserved") with
        {
            AttemptCount = 1,
            LeaseExpiresAt = Now.AddMinutes(5),
        };
        await db.SeedAsync([Account(account, 0)],
            job: Job(batch, "TaskCount", "Active", 1, 1),
            resources: [resource], operations: [operation],
            allocations: [Allocation(batch, account, 1, 1, reserved: 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        await store.MarkPreparationOutcomeUnknownAsync(operation.Id, "PreparationOutcomeUnknown",
            "The managed directory preparation outcome is unknown.", SubmissionAcceptance.Unknown,
            Now.AddSeconds(1), expectedAttemptCount: 1);

        Assert.Equal("ResultPending", await ScalarStringAsync(db,
            "SELECT Status FROM TransferOperations"));
        Assert.Equal("PreparingToSubmit", await ScalarStringAsync(db,
            "SELECT SubmitState FROM TransferOperations"));
        Assert.Equal("PreparationOutcomeUnknown", await ScalarStringAsync(db,
            "SELECT LastErrorCode FROM TransferOperations"));
        Assert.Equal("Reserved", await ScalarStringAsync(db,
            "SELECT TargetSlotState FROM TransferOperations"));
        Assert.Equal(1L, await ScalarLongAsync(db,
            "SELECT ReservedSlotCount FROM AccountAllocations"));
        Assert.Equal(0, (await store.RecoverInterruptedOperationsAsync(Now.AddHours(1))).RetryableOperationCount);
        Assert.Equal("ResultPending", await ScalarStringAsync(db,
            "SELECT Status FROM TransferOperations"));
    }

    [Theory]
    [InlineData("TransientService", "The service is temporarily unavailable.")]
    [InlineData("UnknownResponse", "The service returned an unrecognized response.")]
    [InlineData("ExpiredLogin", "The account session has expired.")]
    public async Task ResultPendingStoreAcceptsUnknownSubmissionAcceptance(string code, string message)
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

        await store.MarkResultPendingAsync(
            operation.Id,
            code,
            message,
            SubmissionAcceptance.Unknown,
            Now.AddSeconds(1));

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
        var replacementAllocation = Allocation(batch, replacement, 2, 0) with
        {
            DefaultTargetPath = "/replacement",
            NormalizedDefaultTargetPath = "/replacement",
        };
        await db.SeedAsync(
            [Account(failed, 0), Account(replacement, 0)],
            job: Job(batch, "TaskCount", "Paused", 1, 1),
            resources: [resource], operations: [operation],
            allocations: [Allocation(batch, failed, 1, 1), replacementAllocation]);
        var reassignedAt = Now.AddHours(4);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory, new FixedTimeProvider(reassignedAt));
        var state = await store.LoadAsync(batch);
        Assert.NotNull(state);
        var mutation = new ReassignmentMutation(batch,
            [new ReassignmentOperationMove(operation.Id, failed, replacement)], [],
            [new ReassignmentAllocationDelta(failed, 1, 0, -1), new ReassignmentAllocationDelta(replacement, 0, 1, 1)]);

        Assert.True(await store.ApplyAsync(mutation, state.BatchVersion));
        Assert.False(await store.ApplyAsync(mutation, state.BatchVersion));
        Assert.Equal(replacement, await ScalarGuidAsync(db, "SELECT AssignedAccountId FROM TransferOperations", null));
        Assert.Equal("/replacement", await ScalarStringAsync(db, "SELECT TargetPath FROM TransferOperations"));
        Assert.Equal("/replacement", await ScalarStringAsync(db, "SELECT NormalizedTargetPath FROM TransferOperations"));
        Assert.Equal(reassignedAt.ToString("O"), await ScalarStringAsync(db, "SELECT UpdatedAt FROM TransferOperations"));
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT AssignedItemCount FROM AccountAllocations WHERE AccountId='" + failed.ToString("D") + "'"));
        Assert.Equal(1L, await ScalarLongAsync(db, "SELECT AssignedItemCount FROM AccountAllocations WHERE AccountId='" + replacement.ToString("D") + "'"));
    }

    [Fact]
    public async Task ReassignmentPreservesResourceTargetOverrideInsteadOfReplacementDefault()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Expanded") with
        {
            TargetPathOverride = "/override",
            NormalizedTargetPathOverride = "/override",
        };
        var operation = Operation(resource, failed, 1);
        var replacementAllocation = Allocation(batch, replacement, 2, 0) with
        {
            DefaultTargetPath = "/replacement",
            NormalizedDefaultTargetPath = "/replacement",
        };
        await db.SeedAsync([Account(failed, 0), Account(replacement, 0)],
            job: Job(batch, "TaskCount", "Paused", 1, 1), resources: [resource], operations: [operation],
            allocations: [Allocation(batch, failed, 1, 1), replacementAllocation]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var state = await store.LoadAsync(batch);

        Assert.True(await store.ApplyAsync(new ReassignmentMutation(batch,
            [new ReassignmentOperationMove(operation.Id, failed, replacement)], [],
            [new ReassignmentAllocationDelta(failed, 1, 0, -1), new ReassignmentAllocationDelta(replacement, 0, 1, 1)]),
            state!.BatchVersion));

        Assert.Equal("/override", await ScalarStringAsync(db, "SELECT TargetPath FROM TransferOperations"));
        Assert.Equal("/override", await ScalarStringAsync(db, "SELECT NormalizedTargetPath FROM TransferOperations"));
    }

    [Theory]
    [InlineData("attempt")]
    [InlineData("daily")]
    [InlineData("confirmation")]
    [InlineData("submit-started")]
    [InlineData("adapter-request")]
    [InlineData("remote-items")]
    [InlineData("directory-mutation-old")]
    [InlineData("directory-mutation-target")]
    [InlineData("directory-mutation-third")]
    public async Task ReassignmentRejectsAnyStartedOrRemoteSideEffectEvidence(string evidence)
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var oldAccount = Guid.NewGuid();
        var newAccount = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Expanded");
        var operation = Operation(resource, oldAccount, 1);
        await db.SeedAsync([Account(oldAccount, 0), Account(newAccount, 0)],
            job: Job(batch, "TaskCount", "Paused", 1, 1), resources: [resource], operations: [operation],
            allocations: [Allocation(batch, oldAccount, 1, 1), Allocation(batch, newAccount, 2, 0)]);

        var mutationAccount = evidence switch
        {
            "directory-mutation-target" => newAccount,
            "directory-mutation-third" => Guid.NewGuid(),
            _ => oldAccount,
        };
        var mutation = evidence switch
        {
            "attempt" => "UPDATE TransferOperations SET AttemptCount=1 WHERE Id=$operation; INSERT INTO WebAutomationAttempts(OperationId,AccountId,Generation,OwnerKind,State,Revision,CreatedAt,UpdatedAt,ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId) SELECT Id,AssignedAccountId,1,'Transfer','Preparing',0,$now,$now,ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId FROM TransferOperations WHERE Id=$operation;",
            "daily" => "UPDATE TransferOperations SET DailyCountedAt=$now WHERE Id=$operation;",
            "confirmation" => "UPDATE TransferOperations SET ConfirmationDescriptorJson='{}' WHERE Id=$operation;",
            "submit-started" => "UPDATE TransferOperations SET SubmitStartedAt=$now WHERE Id=$operation;",
            "adapter-request" => "UPDATE TransferOperations SET AdapterRequestId='request' WHERE Id=$operation;",
            "remote-items" => "UPDATE TransferOperations SET RemoteItemIdsJson='[\"remote\"]' WHERE Id=$operation;",
            "directory-mutation-old" or "directory-mutation-target" or "directory-mutation-third" => """
                INSERT INTO WebAutomationDirectoryMutations(
                  OperationId,AccountId,OriginGeneration,SegmentOrdinal,ParentExactId,CanonicalSegment,
                  ChildExactId,CanonicalChildPath,ListingGenerationToken,BaiduUserId,
                  CapabilityAdapterKind,CapabilityProtocolVersion,CapabilitySelectorVersion,
                  CapabilityBuildVersion,State,OwningAttemptRevision,CreatedAt,UpdatedAt)
                VALUES($operation,$account,1,0,'root','folder',NULL,'/folder','listing','uid',
                  'WebView2','1','1','1','Intent',0,$now,$now);
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(evidence)),
        };
        await ExecuteAsync(db, mutation, ("$operation", operation.Id.ToString("D")),
            ("$account", mutationAccount.ToString("D")), ("$now", Now.ToString("O")));
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var state = await store.LoadAsync(batch);

        var applied = await store.ApplyAsync(new ReassignmentMutation(batch,
            [new ReassignmentOperationMove(operation.Id, oldAccount, newAccount)], [],
            [new ReassignmentAllocationDelta(oldAccount, 1, 0, -1),
             new ReassignmentAllocationDelta(newAccount, 0, 1, 1)]), state!.BatchVersion);

        Assert.False(applied);
        Assert.Equal(oldAccount, await ScalarGuidAsync(db,
            "SELECT AssignedAccountId FROM TransferOperations WHERE Id=$id", operation.Id));
    }

    [Fact]
    public async Task ManagedOperationCannotBeReassignedWithoutAnExplicitValidatedTargetSnapshot()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var oldAccount = Guid.NewGuid();
        var newAccount = Guid.NewGuid();
        await db.SeedAsync([Account(oldAccount, 0), Account(newAccount, 0)]);
        var browserStore = new SqliteManagedBrowserStateStore(db.ConnectionFactory);
        var directory = Path.Combine(Path.GetTempPath(), $"managed-reassignment-{Guid.NewGuid():N}");
        var installation = new BrowserInstallationIdentity(
            Guid.NewGuid(), BrowserFamily.Chrome, directory, Path.Combine(directory, "chrome.exe"),
            "Google LLC", "Google Chrome", $"managed-reassignment-file-{Guid.NewGuid():N}");
        var storedInstallation = await browserStore.CreateInstallationAsync(installation, default);
        Assert.Equal(ManagedBrowserTransitionStatus.Applied,
            (await browserStore.TryReplaceActiveInstallationAsync(
                new ManagedBrowserInstallationActivationRequest(null, storedInstallation), default)).Status);
        var session = new ManagedBrowserSessionReference(
            Guid.NewGuid(), installation.InstallationId, oldAccount, $"baidu-{oldAccount:N}",
            BrowserFamily.Chrome, $"managed-reassignment-profile-{Guid.NewGuid():N}",
            $"managed-reassignment-owner-{Guid.NewGuid():N}");
        var storedSession = await browserStore.CreateSessionAsync(session, default);
        Assert.Equal(ManagedBrowserTransitionStatus.Applied,
            (await browserStore.TryReplaceActiveSessionAsync(
                new ManagedBrowserSessionActivationRequest(null, storedSession), default)).Status);
        var resource = Resource(batch, 1, null, "Expanded");
        var operation = Operation(resource, oldAccount, 1) with
        {
            PersistedExecutionProfile = ExecutionProfileSnapshot.Managed(
                BrowserFamily.Chrome, session.ProfileId, installation.InstallationId, session.SessionId),
        };
        await db.SeedAsync([], job: Job(batch, "TaskCount", "Paused", 1, 1),
            resources: [resource], operations: [operation],
            allocations: [Allocation(batch, oldAccount, 1, 1), Allocation(batch, newAccount, 2, 0)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var state = await store.LoadAsync(batch);

        var applied = await store.ApplyAsync(new ReassignmentMutation(batch,
            [new ReassignmentOperationMove(operation.Id, oldAccount, newAccount)], [],
            [new ReassignmentAllocationDelta(oldAccount, 1, 0, -1),
             new ReassignmentAllocationDelta(newAccount, 0, 1, 1)]), state!.BatchVersion);

        Assert.False(applied);
        Assert.Equal(oldAccount, await ScalarGuidAsync(db,
            "SELECT AssignedAccountId FROM TransferOperations WHERE Id=$id", operation.Id));
    }

    [Fact]
    public async Task TaskCountPreviewRejectsReplacementWithExistingReplicationOperationKey()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Expanded");
        var sourceOperation = Operation(resource, failed, 1);
        var existingReplacement = Operation(resource, replacement, 2, "Succeeded", "ResultReceived", "Fulfilled", "{}") with
        {
            OutcomeKind = "NewTransfer",
        };
        await db.SeedAsync([Account(failed, 0) with { Status = "Invalid" }, Account(replacement, 0)],
            job: Job(batch, "TaskCount", "Paused", 1, 2), resources: [resource],
            operations: [sourceOperation, existingReplacement],
            allocations: [Allocation(batch, failed, 1, 1), Allocation(batch, replacement, 2, 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var service = new ReassignRemainingWorkService(store);

        await Assert.ThrowsAsync<ReassignmentValidationException>(() => service.PreviewAsync(
            new ReassignmentRequest(batch, failed,
                new Dictionary<Guid, int> { [failed] = 0, [replacement] = 2 })));
        var state = await store.LoadAsync(batch);
        Assert.False(await store.ApplyAsync(new ReassignmentMutation(batch,
            [new ReassignmentOperationMove(sourceOperation.Id, failed, replacement)], [],
            [new ReassignmentAllocationDelta(failed, 1, 0, -1), new ReassignmentAllocationDelta(replacement, 1, 2, 1)]),
            state!.BatchVersion));
        Assert.Equal(failed, await ScalarGuidAsync(db,
            "SELECT AssignedAccountId FROM TransferOperations WHERE Id=$id", sourceOperation.Id));
    }

    [Fact]
    public async Task SuccessTargetPreviewRejectsReplacementWithExistingFailedOperationKey()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Reserved") with { ReservedAccountId = failed };
        var sourceOperation = Operation(resource, failed, 1);
        var existingReplacement = Operation(resource, replacement, 2, "Failed", "ResultReceived", "Released", "{}") with
        {
            OutcomeKind = "Failed",
        };
        await db.SeedAsync([Account(failed, 0) with { Status = "Invalid" }, Account(replacement, 0)],
            job: Job(batch, "SuccessTarget", "Paused", 1, 2), resources: [resource],
            operations: [sourceOperation, existingReplacement],
            allocations: [Allocation(batch, failed, 1, 1), Allocation(batch, replacement, 2, 0)]);
        var service = new ReassignRemainingWorkService(new SqliteTransferSchedulingStore(db.ConnectionFactory));

        await Assert.ThrowsAsync<ReassignmentValidationException>(() => service.PreviewAsync(
            new ReassignmentRequest(batch, failed,
                new Dictionary<Guid, int> { [failed] = 0, [replacement] = 1 })));
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
    public async Task GlobalNetworkCountExcludesResultPendingButIncludesSubmittingAcrossBatches()
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

        Assert.Equal(1, state.ActiveNetworkOperationCount);
        Assert.Equal(c, result?.AccountId);
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
    public async Task NotValidatedCandidateDiagnosticIsPersistedByMarkFailed()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        await db.SeedAsync(
            [Account(account, 0)],
            job: Job(batch, "SuccessTarget", "Active", 1),
            resources: [Resource(batch, 1, null)],
            allocations: [Allocation(batch, account, 1, 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var operation = await store.TryReserveNextAsync(batch, Now, Now.AddMinutes(5));
        Assert.NotNull(operation);
        var safeMessage = BaiduAdapterResult.Failure(
            BaiduErrorKind.NotValidated,
            adapterDiagnostic: null,
            safeDiagnosticCode: BaiduCandidateDiagnosticCode.TrustedRootMissing).SafeMessage!;

        await store.MarkFailedAsync(
            operation.OperationId,
            BaiduErrorKind.NotValidated.ToString(),
            safeMessage,
            globallyInvalid: false,
            Now.AddSeconds(1));

        Assert.Equal("Failed", await ScalarStringAsync(db, "SELECT Status FROM TransferOperations"));
        Assert.Equal("NotValidated", await ScalarStringAsync(db, "SELECT LastErrorCode FROM TransferOperations"));
        Assert.Equal(safeMessage, await ScalarStringAsync(db, "SELECT LastErrorMessage FROM TransferOperations"));
    }

    [Fact]
    public async Task NotValidatedCandidateDiagnosticRejectsArbitraryAppendedText()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var safeMessage = BaiduAdapterResult.Failure(
            BaiduErrorKind.NotValidated,
            adapterDiagnostic: null,
            safeDiagnosticCode: BaiduCandidateDiagnosticCode.TrustedRootMissing).SafeMessage!;

        await Assert.ThrowsAsync<ArgumentException>(() => store.MarkFailedAsync(
            Guid.NewGuid(), "NotValidated", $"{safeMessage} extra", false, Now));
    }

    [Fact]
    public async Task NotValidatedCandidateDiagnosticRejectsUndefinedCode()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var safeMessage = BaiduAdapterResult.Failure(
            BaiduErrorKind.NotValidated,
            adapterDiagnostic: null).SafeMessage!;

        await Assert.ThrowsAsync<ArgumentException>(() => store.MarkFailedAsync(
            Guid.NewGuid(), "NotValidated", $"{safeMessage} [DefinitelyNotDefined]", false, Now));
    }

    [Fact]
    public async Task NonNotValidatedFailureRejectsCandidateDiagnostic()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var safeMessage = BaiduAdapterResult.Failure(
            BaiduErrorKind.InvalidTargetPath,
            adapterDiagnostic: null).SafeMessage!;

        await Assert.ThrowsAsync<ArgumentException>(() => store.MarkFailedAsync(
            Guid.NewGuid(), "InvalidTargetPath", $"{safeMessage} [TrustedRootMissing]", false, Now));
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

    [Fact]
    public async Task AccountHealthChangeInvalidatesReassignmentPreviewVersion()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Expanded");
        var operation = Operation(resource, failed, 1);
        await db.SeedAsync([Account(failed, 0) with { Status = "Invalid" }, Account(replacement, 0)],
            job: Job(batch, "TaskCount", "Paused", 1, 1), resources: [resource], operations: [operation],
            allocations: [Allocation(batch, failed, 1, 1), Allocation(batch, replacement, 2, 0)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var previewState = await store.LoadAsync(batch);
        await ExecuteAsync(db, "UPDATE Accounts SET IsEnabled=0 WHERE Id=$id;", ("$id", replacement.ToString("D")));

        var applied = await store.ApplyAsync(new ReassignmentMutation(batch,
            [new ReassignmentOperationMove(operation.Id, failed, replacement)], [],
            [new ReassignmentAllocationDelta(failed, 1, 0, -1), new ReassignmentAllocationDelta(replacement, 0, 1, 1)]),
            previewState!.BatchVersion);

        Assert.False(applied);
        Assert.Equal(failed, await ScalarGuidAsync(db, "SELECT AssignedAccountId FROM TransferOperations", null));
    }

    [Fact]
    public async Task OperationBecomingLockedInvalidatesReassignmentPreviewAndCannotMove()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Expanded");
        var operation = Operation(resource, failed, 1);
        await db.SeedAsync([Account(failed, 0) with { Status = "Invalid" }, Account(replacement, 0)],
            job: Job(batch, "TaskCount", "Paused", 1, 1), resources: [resource], operations: [operation],
            allocations: [Allocation(batch, failed, 1, 1), Allocation(batch, replacement, 2, 0)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var previewState = await store.LoadAsync(batch);
        await ExecuteAsync(db, "UPDATE TransferOperations SET AllocationSource='Locked' WHERE Id=$id;",
            ("$id", operation.Id.ToString("D")));

        var applied = await store.ApplyAsync(new ReassignmentMutation(batch,
            [new ReassignmentOperationMove(operation.Id, failed, replacement)], [],
            [new ReassignmentAllocationDelta(failed, 1, 0, -1), new ReassignmentAllocationDelta(replacement, 0, 1, 1)]),
            previewState!.BatchVersion);

        Assert.False(applied);
        Assert.Equal(failed, await ScalarGuidAsync(db, "SELECT AssignedAccountId FROM TransferOperations", null));
    }

    [Fact]
    public async Task SuccessTargetReassignmentRejectsPausedPublicReservationAndStoreBypass()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Reserved") with { ReservedAccountId = failed };
        var operation = Operation(resource, failed, 1);
        var replacementAllocation = Allocation(batch, replacement, 2, 0) with
        {
            DefaultTargetPath = "/replacement",
            NormalizedDefaultTargetPath = "/replacement",
        };
        await db.SeedAsync([Account(failed, 0) with { Status = "Invalid" }, Account(replacement, 0)],
            job: Job(batch, "SuccessTarget", "Paused", 1, 1),
            resources: [resource], operations: [operation],
            allocations: [Allocation(batch, failed, 1, 1), replacementAllocation]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var service = new ReassignRemainingWorkService(store);
        await Assert.ThrowsAsync<ReassignmentValidationException>(() => service.PreviewAsync(
            new ReassignmentRequest(batch, failed,
                new Dictionary<Guid, int> { [failed] = 0, [replacement] = 1 })));

        var state = await store.LoadAsync(batch);
        Assert.False(await store.ApplyAsync(new ReassignmentMutation(batch,
            [new ReassignmentOperationMove(operation.Id, failed, replacement)],
            [new ReassignmentCandidateMove(resource.Id, failed, replacement)],
            [new ReassignmentAllocationDelta(failed, 1, 0, -1), new ReassignmentAllocationDelta(replacement, 0, 1, 1)]),
            state!.BatchVersion));
        Assert.Equal(failed, await ScalarGuidAsync(db, "SELECT AssignedAccountId FROM TransferOperations", null));
        Assert.Equal(failed, await ScalarGuidAsync(db, "SELECT ReservedAccountId FROM ResourceItems", null));
    }

    [Fact]
    public async Task SuccessTargetReassignmentChangesOnlyUnreservedTargetsAndRetainsPausedWork()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Reserved") with { ReservedAccountId = failed };
        var operation = Operation(resource, failed, 1);
        await db.SeedAsync([Account(failed, 0) with { Status = "Invalid" }, Account(replacement, 0)],
            job: Job(batch, "SuccessTarget", "Paused", 1, 1),
            resources: [resource], operations: [operation],
            allocations: [Allocation(batch, failed, 1, 2), Allocation(batch, replacement, 2, 0)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var service = new ReassignRemainingWorkService(store);
        var preview = await service.PreviewAsync(new ReassignmentRequest(batch, failed,
            new Dictionary<Guid, int> { [failed] = 1, [replacement] = 1 }));

        Assert.Empty(preview.OperationMoves);
        Assert.Empty(preview.CandidateMoves);
        await service.ApplyAsync(preview, preview.ConfirmationToken);

        Assert.Equal(failed, await ScalarGuidAsync(db, "SELECT AssignedAccountId FROM TransferOperations", null));
        Assert.Equal(failed, await ScalarGuidAsync(db, "SELECT ReservedAccountId FROM ResourceItems", null));
        Assert.Equal(1L, await ScalarLongAsync(db,
            "SELECT TargetSuccessCount FROM AccountAllocations WHERE AccountId='" + failed.ToString("D") + "'"));
        var current = await store.LoadAsync(batch);
        Assert.False(await store.ApplyAsync(new ReassignmentMutation(batch, [], [],
        [
            new ReassignmentAllocationDelta(failed, 1, 0, -1),
            new ReassignmentAllocationDelta(replacement, 1, 2, 1),
        ]), current!.BatchVersion));
    }

    [Fact]
    public async Task ReassignmentRetainedCountDoesNotDoubleCountWaitingReservedSlot()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Reserved") with { ReservedAccountId = account };
        var operation = Operation(resource, account, 1, "Waiting", "NotPrepared", "Reserved");
        await db.SeedAsync([Account(account, 0) with { Status = "Invalid" }],
            job: Job(batch, "SuccessTarget", "Paused", 1, 1), resources: [resource], operations: [operation],
            allocations: [Allocation(batch, account, 1, 1, reserved: 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        var state = await store.LoadAsync(batch);

        Assert.Equal(1, Assert.Single(state!.Allocations).MinimumRetainedTargetCount);
    }

    [Fact]
    public async Task SuccessTargetReassignmentCannotDropPausedExclusiveReservationBelowRetainedTarget()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        var resource = Resource(batch, 1, failed, "Reserved") with { ReservedAccountId = failed };
        var operation = Operation(resource, failed, 1) with { AllocationSource = "Exclusive" };
        await db.SeedAsync([Account(failed, 0) with { Status = "Invalid" }, Account(replacement, 0)],
            job: Job(batch, "SuccessTarget", "Paused", 1, 1),
            resources: [resource], operations: [operation],
            allocations: [Allocation(batch, failed, 1, 1), Allocation(batch, replacement, 2, 0)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var service = new ReassignRemainingWorkService(store);

        await Assert.ThrowsAsync<ReassignmentValidationException>(() => service.PreviewAsync(
            new ReassignmentRequest(batch, failed,
                new Dictionary<Guid, int> { [failed] = 0, [replacement] = 1 })));
    }

    [Theory]
    [InlineData("InsufficientCapacity", "The account has insufficient capacity.")]
    [InlineData("InvalidTargetPath", "The target path is invalid.")]
    public async Task DefinitePreSubmissionAccountFailureStopsOnlyThatAccountAndAllowsAnotherToContinue(string code, string message)
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
    [InlineData("ExpiredLogin")]
    [InlineData("VerificationRequired")]
    public async Task PreSubmissionSessionPauseResumesSameOperationWithoutDuplicate(string code)
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1, null);
        await db.SeedAsync([Account(account, 0)],
            job: Job(batch, "SuccessTarget", "Active", 1),
            resources: [resource],
            allocations: [Allocation(batch, account, 1, 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var first = await store.TryReserveNextAsync(batch, Now, Now.AddMinutes(5));
        Assert.NotNull(first);
        await store.MarkPreparingAsync(first.OperationId, Now.AddSeconds(1));

        await store.PauseForAccountInterventionAsync(
            first.OperationId,
            code == "ExpiredLogin"
                ? TransferPauseReason.AccountSessionUnavailable
                : TransferPauseReason.AccountVerificationRequired,
            SubmissionAcceptance.DefinitelyNotAccepted,
            Now,
            Now.AddSeconds(2));

        Assert.Equal("Invalid", await ScalarStringAsync(db, "SELECT Status FROM Accounts"));
        Assert.Equal("Paused", await ScalarStringAsync(db, "SELECT Status FROM TransferOperations"));
        Assert.Equal("Reserved", await ScalarStringAsync(db, "SELECT TargetSlotState FROM TransferOperations"));
        Assert.Equal("Reserved", await ScalarStringAsync(db, "SELECT CandidateStatus FROM ResourceItems"));
        Assert.Equal(account, await ScalarGuidAsync(db, "SELECT ReservedAccountId FROM ResourceItems WHERE Id=$id", resource.Id));
        Assert.Equal(1L, await ScalarLongAsync(db, "SELECT ReservedSlotCount FROM AccountAllocations"));
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT FailedItemCount FROM AccountAllocations"));
        Assert.Equal(1L, await ScalarLongAsync(db, "SELECT COUNT(*) FROM TransferOperations"));

        await ExecuteAsync(db, "UPDATE Accounts SET Status='Active' WHERE Id=$id;", ("$id", account.ToString("D")));
        Assert.True(await store.ResumePausedBeforeSubmissionAsync(first.OperationId, Now.AddSeconds(3)));
        var resumed = await store.TryReserveNextAsync(batch, Now.AddMinutes(1), Now.AddMinutes(6));

        Assert.Equal(first.OperationId, resumed?.OperationId);
        Assert.Equal(1L, await ScalarLongAsync(db, "SELECT COUNT(*) FROM TransferOperations"));
        Assert.Equal(1L, await ScalarLongAsync(db, "SELECT ReservedSlotCount FROM AccountAllocations"));
    }

    [Fact]
    public async Task PausedTransferPreventsTaskCountBatchFinalizationUntilExplicitResume()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Reserved") with { ReservedAccountId = account };
        var operation = Operation(resource, account, 1, "Preparing", "PreparingToSubmit", "Reserved");
        await db.SeedAsync([Account(account, 0)],
            job: Job(batch, "TaskCount", "Active", 1, 1),
            resources: [resource],
            operations: [operation],
            allocations: [Allocation(batch, account, 1, 1, 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        await store.PauseAccountBeforeSubmissionAsync(
            operation.Id,
            "ExpiredLogin",
            "The account session has expired.",
            Now.AddSeconds(2));

        var outcome = await store.TryFinalizeExhaustedBatchAsync(batch, Now.AddSeconds(3));

        Assert.Equal(BatchExhaustionOutcome.TemporarilyBlocked, outcome);
        Assert.Equal("Active", await ScalarStringAsync(db, "SELECT Status FROM BatchJobs"));
        Assert.Equal("Paused", await ScalarStringAsync(db, "SELECT Status FROM TransferOperations"));
    }

    [Theory]
    [InlineData(TransferPauseReason.AccountSessionUnavailable, SubmissionAcceptance.DefinitelyNotAccepted, "ExpiredLogin", "ResultReceived")]
    [InlineData(TransferPauseReason.AccountSessionUnavailable, SubmissionAcceptance.Unknown, "ExpiredLogin", "PendingConfirmation")]
    [InlineData(TransferPauseReason.AccountVerificationRequired, SubmissionAcceptance.DefinitelyNotAccepted, "VerificationRequired", "ResultReceived")]
    [InlineData(TransferPauseReason.AccountVerificationRequired, SubmissionAcceptance.Unknown, "VerificationRequired", "PendingConfirmation")]
    [InlineData(TransferPauseReason.UserInterventionRequired, SubmissionAcceptance.DefinitelyNotAccepted, "UserInterventionRequired", "ResultReceived")]
    [InlineData(TransferPauseReason.UserInterventionRequired, SubmissionAcceptance.Unknown, "UserInterventionRequired", "PendingConfirmation")]
    public async Task PostSubmissionInterventionPauseReleasesLeaseButRetainsBindingSlotAndEvidence(
        TransferPauseReason reason,
        SubmissionAcceptance acceptance,
        string expectedCode,
        string expectedSubmitState)
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Reserved") with { ReservedAccountId = account };
        var operation = Operation(resource, account, 1, "Submitting", "Submitting", "Reserved", "{\"safe\":true}") with
        {
            AdapterRequestId = "request-1",
            RemoteItemIdsJson = "[\"remote-1\"]",
            DailyCountedAt = Now,
            SubmitStartedAt = Now,
            LeaseExpiresAt = Now.AddMinutes(5),
        };
        await db.SeedAsync([Account(account, 0) with { DailyExecutedCount = 1 }],
            job: Job(batch, "TaskCount", "Active", 1, 1),
            resources: [resource],
            operations: [operation],
            allocations: [Allocation(batch, account, 1, 1, 1)]);
        IOperationExecutionStore store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        await store.PauseForAccountInterventionAsync(operation.Id, reason, acceptance, Now, Now.AddSeconds(1));

        Assert.Equal("Invalid", await ScalarStringAsync(db, "SELECT Status FROM Accounts"));
        Assert.Equal("Paused", await ScalarStringAsync(db, "SELECT Status FROM TransferOperations"));
        Assert.Equal(expectedSubmitState, await ScalarStringAsync(db, "SELECT SubmitState FROM TransferOperations"));
        Assert.Equal("Reserved", await ScalarStringAsync(db, "SELECT TargetSlotState FROM TransferOperations"));
        Assert.Equal("Reserved", await ScalarStringAsync(db, "SELECT CandidateStatus FROM ResourceItems"));
        Assert.Equal(account, await ScalarGuidAsync(db, "SELECT ReservedAccountId FROM ResourceItems WHERE Id=$id", resource.Id));
        Assert.Equal(1L, await ScalarLongAsync(db, "SELECT ReservedSlotCount FROM AccountAllocations"));
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT FailedItemCount FROM AccountAllocations"));
        Assert.Equal(1L, await ScalarLongAsync(db, "SELECT DailyExecutedCount FROM Accounts"));
        Assert.Equal("{\"safe\":true}", await ScalarStringAsync(db, "SELECT ConfirmationDescriptorJson FROM TransferOperations"));
        Assert.Equal("request-1", await ScalarStringAsync(db, "SELECT AdapterRequestId FROM TransferOperations"));
        Assert.Equal("[\"remote-1\"]", await ScalarStringAsync(db, "SELECT RemoteItemIdsJson FROM TransferOperations"));
        Assert.Equal(expectedCode, await ScalarStringAsync(db, "SELECT LastErrorCode FROM TransferOperations"));
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT COUNT(*) FROM TransferOperations WHERE LeaseExpiresAt IS NOT NULL"));
    }

    [Theory]
    [InlineData(SubmissionAcceptance.DefinitelyNotAccepted, "Waiting", "Released", 0, 0)]
    [InlineData(SubmissionAcceptance.Unknown, "ResultPending", "Reserved", 1, 1)]
    public async Task ExplicitResumeRoutesPausedSubmissionWithoutBlindRetry(
        SubmissionAcceptance acceptance,
        string expectedStatus,
        string expectedSlotState,
        long expectedReservedCount,
        long expectedEvidenceCount)
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Reserved") with { ReservedAccountId = account };
        var operation = Operation(resource, account, 1, "Submitting", "Submitting", "Reserved", "{\"safe\":true}") with
        {
            AdapterRequestId = "request-1",
            RemoteItemIdsJson = "[\"remote-1\"]",
            DailyCountedAt = Now,
            SubmitStartedAt = Now,
            LeaseExpiresAt = Now.AddMinutes(5),
        };
        await db.SeedAsync([Account(account, 0) with { DailyExecutedCount = 1 }],
            job: Job(batch, "TaskCount", "Active", 1, 1),
            resources: [resource],
            operations: [operation],
            allocations: [Allocation(batch, account, 1, 1, 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        await store.PauseForAccountInterventionAsync(
            operation.Id,
            TransferPauseReason.AccountSessionUnavailable,
            acceptance,
            Now,
            Now.AddSeconds(1));
        await ExecuteAsync(db, "UPDATE Accounts SET Status='Active' WHERE Id=$id;", ("$id", account.ToString("D")));

        Assert.True(await store.ResumePausedAsync(operation.Id, operation.AttemptCount, Now.AddSeconds(1), Now.AddSeconds(2)));

        Assert.Equal(expectedStatus, await ScalarStringAsync(db, "SELECT Status FROM TransferOperations"));
        Assert.Equal(expectedSlotState, await ScalarStringAsync(db, "SELECT TargetSlotState FROM TransferOperations"));
        Assert.Equal(expectedReservedCount, await ScalarLongAsync(db, "SELECT ReservedSlotCount FROM AccountAllocations"));
        Assert.Equal(expectedEvidenceCount, await ScalarLongAsync(db,
            "SELECT COUNT(*) FROM TransferOperations WHERE ConfirmationDescriptorJson IS NOT NULL AND AdapterRequestId IS NOT NULL AND RemoteItemIdsJson IS NOT NULL AND SubmitStartedAt IS NOT NULL"));
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT FailedItemCount FROM AccountAllocations"));
        Assert.Equal(1L, await ScalarLongAsync(db, "SELECT DailyExecutedCount FROM Accounts"));
    }

    [Fact]
    public async Task DefinitelyNotAcceptedPauseCannotDowngradeExistingResultPendingEvidence()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Reserved") with { ReservedAccountId = account };
        var operation = Operation(resource, account, 1, "ResultPending", "PendingConfirmation", "Reserved", "{\"safe\":true}") with
        {
            AdapterRequestId = "request-1",
            RemoteItemIdsJson = "[\"remote-1\"]",
            DailyCountedAt = Now,
            SubmitStartedAt = Now,
        };
        await db.SeedAsync([Account(account, 0) with { DailyExecutedCount = 1 }],
            job: Job(batch, "TaskCount", "Active", 1, 1),
            resources: [resource],
            operations: [operation],
            allocations: [Allocation(batch, account, 1, 1, 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PauseForAccountInterventionAsync(
            operation.Id,
            TransferPauseReason.AccountSessionUnavailable,
            SubmissionAcceptance.DefinitelyNotAccepted,
            Now,
            Now.AddSeconds(1)));

        Assert.Equal("Active", await ScalarStringAsync(db, "SELECT Status FROM Accounts"));
        Assert.Equal("ResultPending", await ScalarStringAsync(db, "SELECT Status FROM TransferOperations"));
        Assert.Equal("PendingConfirmation", await ScalarStringAsync(db, "SELECT SubmitState FROM TransferOperations"));
        Assert.Equal("Reserved", await ScalarStringAsync(db, "SELECT TargetSlotState FROM TransferOperations"));
        Assert.Equal("{\"safe\":true}", await ScalarStringAsync(db, "SELECT ConfirmationDescriptorJson FROM TransferOperations"));
        Assert.Equal("request-1", await ScalarStringAsync(db, "SELECT AdapterRequestId FROM TransferOperations"));
        Assert.Equal("[\"remote-1\"]", await ScalarStringAsync(db, "SELECT RemoteItemIdsJson FROM TransferOperations"));
        Assert.Equal("Reserved", await ScalarStringAsync(db, "SELECT CandidateStatus FROM ResourceItems"));
        Assert.Equal(account, await ScalarGuidAsync(db, "SELECT ReservedAccountId FROM ResourceItems WHERE Id=$id", resource.Id));
        Assert.Equal(1L, await ScalarLongAsync(db, "SELECT ReservedSlotCount FROM AccountAllocations"));
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT FailedItemCount FROM AccountAllocations"));
        Assert.Equal(1L, await ScalarLongAsync(db, "SELECT DailyExecutedCount FROM Accounts"));
        Assert.Equal(1L, await ScalarLongAsync(db, "SELECT COUNT(*) FROM Accounts WHERE LastValidatedAt IS NULL"));
        Assert.Equal(1L, await ScalarLongAsync(db,
            "SELECT COUNT(*) FROM TransferOperations WHERE LastErrorCode IS NULL AND LastErrorMessage IS NULL"));
        Assert.False(await store.ResumePausedAsync(operation.Id, operation.AttemptCount, operation.UpdatedAt, Now.AddSeconds(2)));
        Assert.NotNull(await ((IOperationReconciliationStore)store).LoadPendingAsync(operation.Id));
    }

    [Fact]
    public async Task SaveFailureObservedOnOldSessionDoesNotInvalidateNewActiveLoginButStillPausesOperation()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1, null);
        var observedSession = Now;
        var refreshedLogin = Now.AddMinutes(1);
        await db.SeedAsync([Account(account, 0) with { LastValidatedAt = observedSession }],
            job: Job(batch, "SuccessTarget", "Active", 1),
            resources: [resource],
            allocations: [Allocation(batch, account, 1, 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var reserved = Assert.IsType<ScheduledOperation>(await store.TryReserveNextAsync(batch, Now, Now.AddMinutes(5)));
        var adapter = new ReloginDuringSaveAdapter(async () =>
            await ExecuteAsync(db,
                "UPDATE Accounts SET Status='Active',LastValidatedAt=$validated WHERE Id=$id;",
                ("$validated", refreshedLogin.ToString("O")), ("$id", account.ToString("D"))));
        var session = new AccountSession(account, "baidu-user",
            [new SessionCookie("BDUSS", "secret", ".baidu.com", "/", null, true, true)], observedSession);
        var executor = new OperationExecutor(
            adapter,
            new FixedSessionStore(session),
            store,
            new AccountDailyLimitService(store, new FixedTimeProvider(Now)),
            RetryPolicy.Default,
            new NoDelay(),
            new FixedTimeProvider(Now));

        await executor.ExecuteAsync(reserved.OperationId);

        Assert.Equal("Paused", await ScalarStringAsync(db, "SELECT Status FROM TransferOperations"));
        Assert.Equal("Active", await ScalarStringAsync(db, "SELECT Status FROM Accounts"));
        Assert.Equal(refreshedLogin, DateTimeOffset.Parse(await ScalarStringAsync(db, "SELECT LastValidatedAt FROM Accounts")));
    }

    [Fact]
    public async Task PauseWithoutObservedSessionDoesNotInvalidateActiveAccountButStillPausesOperation()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Reserved") with { ReservedAccountId = account };
        var operation = Operation(resource, account, 1, "Preparing", "PreparingToSubmit", "Reserved");
        await db.SeedAsync([Account(account, 0) with { LastValidatedAt = Now }],
            job: Job(batch, "TaskCount", "Active", 1, 1),
            resources: [resource], operations: [operation],
            allocations: [Allocation(batch, account, 1, 1, 1)]);
        IOperationExecutionStore store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        await store.PauseForAccountInterventionAsync(
            operation.Id,
            TransferPauseReason.AccountSessionUnavailable,
            SubmissionAcceptance.DefinitelyNotAccepted,
            observedSessionCapturedAtUtc: null,
            Now.AddSeconds(1));

        Assert.Equal("Paused", await ScalarStringAsync(db, "SELECT Status FROM TransferOperations"));
        Assert.Equal("Active", await ScalarStringAsync(db, "SELECT Status FROM Accounts"));
        Assert.Equal(Now, DateTimeOffset.Parse(await ScalarStringAsync(db, "SELECT LastValidatedAt FROM Accounts")));
    }

    [Fact]
    public async Task PauseObservedOnCurrentSessionInvalidatesAccountWithoutChangingValidationVersion()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Reserved") with { ReservedAccountId = account };
        var operation = Operation(resource, account, 1, "Preparing", "PreparingToSubmit", "Reserved");
        var lastValidated = Now.AddMinutes(-1);
        await db.SeedAsync([Account(account, 0) with { LastValidatedAt = lastValidated }],
            job: Job(batch, "TaskCount", "Active", 1, 1),
            resources: [resource], operations: [operation],
            allocations: [Allocation(batch, account, 1, 1, 1)]);
        IOperationExecutionStore store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        await store.PauseForAccountInterventionAsync(
            operation.Id,
            TransferPauseReason.AccountSessionUnavailable,
            SubmissionAcceptance.DefinitelyNotAccepted,
            observedSessionCapturedAtUtc: Now,
            Now.AddSeconds(1));

        Assert.Equal("Paused", await ScalarStringAsync(db, "SELECT Status FROM TransferOperations"));
        Assert.Equal("Invalid", await ScalarStringAsync(db, "SELECT Status FROM Accounts"));
        Assert.Equal(lastValidated, DateTimeOffset.Parse(await ScalarStringAsync(db, "SELECT LastValidatedAt FROM Accounts")));
    }

    [Theory]
    [InlineData("ExpiredLogin", "The account session has expired.")]
    [InlineData("VerificationRequired", "Account verification is required.")]
    public async Task SessionInterventionCannotUseDefiniteFailureReleasePath(string code, string message)
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Reserved") with { ReservedAccountId = account };
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
            store.MarkDefiniteAccountFailureAsync(operation.Id, code, message, Now.AddSeconds(1)));

        Assert.Equal("Active", await ScalarStringAsync(db, "SELECT Status FROM Accounts"));
        Assert.Equal("Submitting", await ScalarStringAsync(db, "SELECT Status FROM TransferOperations"));
        Assert.Equal("Reserved", await ScalarStringAsync(db, "SELECT TargetSlotState FROM TransferOperations"));
        Assert.Equal(1L, await ScalarLongAsync(db, "SELECT ReservedSlotCount FROM AccountAllocations"));
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT FailedItemCount FROM AccountAllocations"));
    }

    [Theory]
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
    public async Task ExclusiveUnusedCandidateCountTracksReservationAndDefiniteFailureReturn()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1, account, "Unused");
        await db.SeedAsync([Account(account, 0)],
            job: Job(batch, "SuccessTarget", "Active", 1),
            resources: [resource],
            allocations: [Allocation(batch, account, 1, 1) with { UnusedCandidateCount = 1 }]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        var reserved = await store.TryReserveNextAsync(batch, Now, Now.AddMinutes(5));

        Assert.NotNull(reserved);
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT UnusedCandidateCount FROM AccountAllocations"));
        await store.MarkPreparingAsync(reserved.OperationId, Now.AddSeconds(1));

        await store.MarkDefiniteAccountFailureAsync(
            reserved.OperationId,
            "InsufficientCapacity",
            "The account has insufficient capacity.",
            Now.AddSeconds(2));

        Assert.Equal("Unused", await ScalarStringAsync(db, "SELECT CandidateStatus FROM ResourceItems"));
        Assert.Equal(1L, await ScalarLongAsync(db, "SELECT UnusedCandidateCount FROM AccountAllocations"));
    }

    [Fact]
    public async Task ReservationReconcilesLegacyMissingExclusiveUnusedCandidateCount()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        await db.SeedAsync([Account(account, 0)],
            job: Job(batch, "SuccessTarget", "Active", 1),
            resources: [Resource(batch, 1, account, "Unused")],
            allocations: [Allocation(batch, account, 1, 1) with { UnusedCandidateCount = 0 }]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        var reserved = await store.TryReserveNextAsync(batch, Now, Now.AddMinutes(5));

        Assert.NotNull(reserved);
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT UnusedCandidateCount FROM AccountAllocations"));
        Assert.Equal("Reserved", await ScalarStringAsync(db, "SELECT CandidateStatus FROM ResourceItems"));
    }

    [Fact]
    public async Task DynamicAllocationSkipsExistingFailedKeyForRestoredAccountAndUsesAnotherAccount()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var restored = Guid.NewGuid();
        var healthy = Guid.NewGuid();
        var resource = Resource(batch, 1, null, "Unused");
        var failed = Operation(resource, restored, 1, "Failed", "ResultReceived", "Released", "{}") with
        {
            OutcomeKind = "Failed",
        };
        await db.SeedAsync([Account(restored, 0), Account(healthy, 0)],
            job: Job(batch, "SuccessTarget", "Active", 1, 1) with { ShortagePolicy = "AccountPriority" },
            resources: [resource], operations: [failed],
            allocations: [Allocation(batch, restored, 1, 1), Allocation(batch, healthy, 2, 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        var reserved = await store.TryReserveNextAsync(batch, Now, Now.AddMinutes(5));

        Assert.Equal(healthy, reserved?.AccountId);
        Assert.Equal(2L, await ScalarLongAsync(db, "SELECT COUNT(*) FROM TransferOperations"));
    }

    [Fact]
    public async Task DefiniteNonGlobalOperationFailureDoesNotLeaveSuccessCandidateReservedForever()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1, null);
        await db.SeedAsync([Account(account, 0)],
            job: Job(batch, "SuccessTarget", "Active", 1),
            resources: [resource],
            allocations: [Allocation(batch, account, 1, 1)]);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var operation = await store.TryReserveNextAsync(batch, Now, Now.AddMinutes(5));
        Assert.NotNull(operation);

        await store.MarkFailedAsync(operation.OperationId,
            "TransientService", "The service is temporarily unavailable.", globallyInvalid: false, Now.AddSeconds(1));

        Assert.Equal("Expanded", await ScalarStringAsync(db, "SELECT CandidateStatus FROM ResourceItems"));
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT COUNT(*) FROM ResourceItems WHERE ReservedAccountId IS NOT NULL"));
        Assert.Equal(0L, await ScalarLongAsync(db, "SELECT ReservedSlotCount FROM AccountAllocations"));
        Assert.Equal(BatchExhaustionOutcome.CompletedWithShortfall,
            await store.TryFinalizeExhaustedBatchAsync(batch, Now.AddSeconds(2)));
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
            "InsufficientCapacity",
            "The account has insufficient capacity.",
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FixedSessionStore(AccountSession? session) : IBaiduSessionStore
    {
        public Task<AccountSession?> LoadAsync(string accountId, CancellationToken cancellationToken) =>
            Task.FromResult(session);

        public Task SaveAsync(AccountSession value, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveAsync(string accountId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoDelay : IAsyncDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ReloginDuringSaveAdapter(Func<Task> beforeFailure) : IBaiduNetdiskAdapter
    {
        public Task<BaiduAdapterResult<BaiduAccountIdentity>> ValidateSessionAsync(
            AccountSession session, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BaiduAdapterResult<AccountCapacity>> GetCapacityAsync(
            AccountSession session, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BaiduAdapterResult<ImmutableArray<RemoteDirectory>>> ListDirectoriesAsync(
            AccountSession session, RemotePath path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryAsync(
            AccountSession session, RemotePath path, CancellationToken cancellationToken) =>
            Task.FromResult(BaiduAdapterResult<RemoteDirectory>.Success(new RemoteDirectory("directory-id", path)));

        public Task<BaiduAdapterResult<ShareMetadata>> InspectShareAsync(
            AccountSession session, ShareReference share, CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareAsync(
            AccountSession session,
            ShareReference share,
            RemoteDirectory destination,
            DuplicatePolicy duplicatePolicy,
            CancellationToken cancellationToken)
        {
            await beforeFailure();
            return BaiduAdapterResult<TransferReceipt>.Failure(
                BaiduErrorKind.ExpiredLogin,
                "private response",
                submissionAcceptance: SubmissionAcceptance.DefinitelyNotAccepted);
        }

        public Task<BaiduAdapterResult<TransferConfirmation>> ConfirmTransferAsync(
            AccountSession session, TransferConfirmationQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static async Task<string> ExplainDetailsAsync(SqliteCommand command)
    {
        var details = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) details.Add(reader.GetString(3));
        return string.Join(Environment.NewLine, details);
    }
}
