using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Application.Tests.Jobs;

public sealed class AppendCandidatesServiceTests
{
    [Fact]
    public async Task ConfirmedAppendContinuesSequenceDeduplicatesAndMakesWaitingBatchPausedReady()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var account = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var importId = Guid.NewGuid();
        var oldImportId = Guid.NewGuid();
        var oldSession = BatchTestData.ReadySession(oldImportId, 0, 0);
        var appendSession = BatchTestData.ReadySession(importId, 2, 2, appendToBatchJobId: batchId) with { Status = "Draft" };
        var oldResource = Resource(batchId, 5, "same", account, "Unused");
        var appendRows = new[]
        {
            BatchTestData.ReadyRow(importId, 1, normalizedId: "same"),
            BatchTestData.ReadyRow(importId, 2, mappedAccountId: account, note: "new note", normalizedId: "new"),
        };
        var job = Job(batchId, oldImportId, "AwaitingCandidates", completedAt: null, resourceCount: 1);
        var allocation = Allocation(batchId, account, target: 10, fulfilled: 2, exclusive: 1, unused: 1);
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")], oldSession, null, job, [oldResource], null, [allocation]);
        await database.SeedAsync([], appendSession, appendRows);
        var service = new AppendCandidatesService(database.UnitOfWorkFactory,
            new ReplicationPreflightEstimator(
                new ReplicationPreflightEstimatorTests.StubDiskSpaceProbe(long.MaxValue), database.DatabasePath));
        var request = new AppendCandidatesDraft(batchId, importId);

        var preview = await service.PreviewAsync(request);
        Assert.Equal(1, preview.ResourceCount);
        Assert.Equal(1, preview.OperationCount);
        Assert.Equal(1, preview.StorageEstimate.OperationCount);
        var accountPreview = Assert.Single(preview.Accounts);
        Assert.Equal(2, accountPreview.FulfilledCount);
        Assert.Equal(0, accountPreview.ReservedSlotCount);
        Assert.Equal(1, accountPreview.UnusedCandidateCount);
        Assert.Equal(1, accountPreview.NewExclusiveCandidateCount);
        Assert.Equal(4, accountPreview.ReachableTargetSuccessCount);
        await service.ConfirmAsync(request, preview.ConfirmationToken);

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        var savedJob = (await read.Jobs.GetAsync(batchId))!;
        var resources = await CreateBatchServiceTests.AllResourcesAsync(read.Jobs, batchId);
        var savedAllocation = Assert.Single((await read.Jobs.GetAllocationsPageAsync(batchId, new PageQuery(1, 10))).Items);
        Assert.Equal("PausedReady", savedJob.Status);
        Assert.Equal(2, savedJob.ResourceCount);
        Assert.Equal(new long[] { 5, 6 }, resources.Select(resource => resource.CandidateSequence));
        Assert.Single(resources, resource => resource.NormalizedShareId == "same");
        Assert.Equal("new note", resources.Single(resource => resource.NormalizedShareId == "new").Note);
        Assert.Equal("Unused", resources.Single(resource => resource.NormalizedShareId == "same").CandidateStatus);
        Assert.Equal(10, savedAllocation.TargetSuccessCount);
        Assert.Equal(2, savedAllocation.FulfilledCount);
        Assert.Equal(2, savedAllocation.ExclusiveCandidateCount);
        Assert.Equal("BatchCreated", (await read.Imports.GetSessionAsync(importId))!.Status);
    }

    [Fact]
    public async Task AppendPreviewExcludesCandidateBlockedByExistingNonCancelledOperationKey()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var account = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var importId = Guid.NewGuid();
        var oldImportId = Guid.NewGuid();
        var blocked = Resource(batchId, 1, "blocked", account, "Unused");
        var failed = new TransferOperationRecord(
            Guid.NewGuid(), blocked.Id, batchId, blocked.CandidateSequence, 1,
            "/account", "/account", "", account, "Exclusive", "Failed", "Failed", 1,
            "failed-key", "{}", "request", null, "ResultReceived", "Released", null,
            BatchTestData.Now, BatchTestData.Now, "InsufficientCapacity",
            "The account has insufficient capacity.", null, BatchTestData.Now, BatchTestData.Now);
        var job = Job(batchId, oldImportId, "AwaitingCandidates", null, 1) with
        {
            OperationCount = 1,
        };
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            BatchTestData.ReadySession(oldImportId, 0, 0),
            null,
            job,
            [blocked],
            [failed],
            [Allocation(batchId, account, 2, 0, 1, 1)]);
        await database.SeedAsync([], BatchTestData.ReadySession(
                importId, 1, 1, appendToBatchJobId: batchId),
            [BatchTestData.ReadyRow(importId, 1, mappedAccountId: account, normalizedId: "new")]);
        var service = Service(database, long.MaxValue);

        var preview = await service.PreviewAsync(new AppendCandidatesDraft(batchId, importId));

        Assert.Equal(1, Assert.Single(preview.Accounts).ReachableTargetSuccessCount);
    }

    [Fact]
    public async Task AppendWithoutMatchingConfirmationDoesNotChangeBatchOrImport()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var account = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var importId = Guid.NewGuid();
        var oldImportId = Guid.NewGuid();
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            BatchTestData.ReadySession(oldImportId, 0, 0),
            null,
            Job(batchId, oldImportId, "AwaitingCandidates", null, 0),
            null,
            null,
            [Allocation(batchId, account, 1, 0, 0, 0)]);
        await database.SeedAsync([], BatchTestData.ReadySession(importId, 1, 1, appendToBatchJobId: batchId),
            [BatchTestData.ReadyRow(importId, 1)]);
        var service = new AppendCandidatesService(database.UnitOfWorkFactory,
            new ReplicationPreflightEstimator(
                new ReplicationPreflightEstimatorTests.StubDiskSpaceProbe(long.MaxValue), database.DatabasePath));

        await Assert.ThrowsAsync<BatchConfirmationException>(() =>
            service.ConfirmAsync(new AppendCandidatesDraft(batchId, importId), "not-confirmed"));

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Equal("AwaitingCandidates", (await read.Jobs.GetAsync(batchId))!.Status);
        Assert.Equal("ReadyToConfirm", (await read.Imports.GetSessionAsync(importId))!.Status);
        Assert.Empty(await CreateBatchServiceTests.AllResourcesAsync(read.Jobs, batchId));
    }

    [Fact]
    public async Task AppendConfirmationIsDurablyIdempotentForConcurrentAndSequentialReplay()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var account = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var importId = Guid.NewGuid();
        var oldImportId = Guid.NewGuid();
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")], BatchTestData.ReadySession(oldImportId, 0, 0), null,
            Job(batchId, oldImportId, "AwaitingCandidates", null, 0), null, null,
            [Allocation(batchId, account, 1, 0, 0, 0)]);
        await database.SeedAsync([], BatchTestData.ReadySession(importId, 1, 1, appendToBatchJobId: batchId),
            [BatchTestData.ReadyRow(importId, 1)]);
        var service = Service(database, long.MaxValue);
        var request = new AppendCandidatesDraft(batchId, importId);
        var preview = await service.PreviewAsync(request);

        var concurrent = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => Service(database, long.MaxValue).ConfirmAsync(request, preview.ConfirmationToken)));
        var replay = await service.ConfirmAsync(request, preview.ConfirmationToken);
        var queried = await service.GetResultAsync(request, preview.ConfirmationToken);

        Assert.All(concurrent, result => Assert.Equal(concurrent[0], result));
        Assert.Equal(concurrent[0], replay);
        Assert.Equal(concurrent[0], queried);
        Assert.Equal(batchId, replay.BatchJobId);
        Assert.Equal(importId, replay.ImportSessionId);
        Assert.Equal("CANDIDATES_APPENDED", replay.EventCode);
        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Single(await CreateBatchServiceTests.AllResourcesAsync(read.Jobs, batchId));
        Assert.Equal(1, (await read.Jobs.GetAsync(batchId))!.ResourceCount);
        Assert.Equal(1L, await CountAuditAsync(database, batchId, "CANDIDATES_APPENDED"));
    }

    [Fact]
    public async Task CompletedAppendTokenCannotBeReusedForDifferentRequest()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var account = Guid.NewGuid();
        var firstBatch = Guid.NewGuid();
        var secondBatch = Guid.NewGuid();
        var oldImportId = Guid.NewGuid();
        var firstImport = Guid.NewGuid();
        var secondImport = Guid.NewGuid();
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")], BatchTestData.ReadySession(oldImportId, 0, 0), null,
            Job(firstBatch, oldImportId, "AwaitingCandidates", null, 0), null, null,
            [Allocation(firstBatch, account, 1, 0, 0, 0)]);
        await database.SeedAsync([], BatchTestData.ReadySession(firstImport, 1, 1, appendToBatchJobId: firstBatch),
            [BatchTestData.ReadyRow(firstImport, 1)]);
        var secondOldImport = Guid.NewGuid();
        await database.SeedAsync([], BatchTestData.ReadySession(secondOldImport, 0, 0), null,
            Job(secondBatch, secondOldImport, "AwaitingCandidates", null, 0), null, null,
            [Allocation(secondBatch, account, 1, 0, 0, 0)]);
        await database.SeedAsync([], BatchTestData.ReadySession(secondImport, 1, 1, appendToBatchJobId: secondBatch),
            [BatchTestData.ReadyRow(secondImport, 1)]);
        var service = Service(database, long.MaxValue);
        var first = new AppendCandidatesDraft(firstBatch, firstImport);
        var preview = await service.PreviewAsync(first);
        await service.ConfirmAsync(first, preview.ConfirmationToken);

        await Assert.ThrowsAsync<BatchConfirmationException>(() =>
            service.ConfirmAsync(new AppendCandidatesDraft(secondBatch, secondImport), preview.ConfirmationToken));

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Empty(await CreateBatchServiceTests.AllResourcesAsync(read.Jobs, secondBatch));
        Assert.Equal("ReadyToConfirm", (await read.Imports.GetSessionAsync(secondImport))!.Status);
    }

    [Fact]
    public async Task AppendConfirmationBecomesStaleWhenInheritedAllocationSnapshotChanges()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var account = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var importId = Guid.NewGuid();
        var oldImportId = Guid.NewGuid();
        var allocation = Allocation(batchId, account, 2, 0, 0, 0);
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")], BatchTestData.ReadySession(oldImportId, 0, 0), null,
            Job(batchId, oldImportId, "AwaitingCandidates", null, 0), null, null, [allocation]);
        await database.SeedAsync([], BatchTestData.ReadySession(importId, 1, 1, appendToBatchJobId: batchId),
            [BatchTestData.ReadyRow(importId, 1)]);
        var service = new AppendCandidatesService(database.UnitOfWorkFactory,
            new ReplicationPreflightEstimator(
                new ReplicationPreflightEstimatorTests.StubDiskSpaceProbe(long.MaxValue), database.DatabasePath));
        var request = new AppendCandidatesDraft(batchId, importId);
        var preview = await service.PreviewAsync(request);
        await using (var update = await database.UnitOfWorkFactory.BeginAsync())
        {
            await update.Jobs.UpdateAllocationsAsync([allocation with { TargetSuccessCount = 3, EffectiveTargetSuccessCount = 3 }]);
            await update.CommitAsync();
        }

        await Assert.ThrowsAsync<BatchConfirmationException>(() =>
            service.ConfirmAsync(request, preview.ConfirmationToken));

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Equal("AwaitingCandidates", (await read.Jobs.GetAsync(batchId))!.Status);
        Assert.Empty(await CreateBatchServiceTests.AllResourcesAsync(read.Jobs, batchId));
        Assert.Equal("ReadyToConfirm", (await read.Imports.GetSessionAsync(importId))!.Status);
    }

    [Fact]
    public async Task AppendDiskShortageUsesFutureOperationsAndWritesNothing()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var account = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var importId = Guid.NewGuid();
        var oldImportId = Guid.NewGuid();
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")], BatchTestData.ReadySession(oldImportId, 0, 0), null,
            Job(batchId, oldImportId, "AwaitingCandidates", null, 0), null, null,
            [Allocation(batchId, account, 1, 0, 0, 0)]);
        await database.SeedAsync([], BatchTestData.ReadySession(importId, 1, 1, appendToBatchJobId: batchId),
            [BatchTestData.ReadyRow(importId, 1)]);
        var service = Service(database, availableBytes: 0);
        var request = new AppendCandidatesDraft(batchId, importId);

        var preview = await service.PreviewAsync(request);
        var error = await Assert.ThrowsAsync<StorageCapacityException>(() =>
            service.ConfirmAsync(request, preview.ConfirmationToken));

        Assert.Equal(1, preview.ResourceCount);
        Assert.Equal(1, preview.OperationCount);
        Assert.Equal(1, preview.StorageEstimate.OperationCount);
        Assert.True(preview.StorageEstimate.DatabaseBytes > 0);
        Assert.True(preview.StorageEstimate.IndexBytes > 0);
        Assert.True(preview.StorageEstimate.WalBytes > 0);
        Assert.True(preview.StorageEstimate.SafetyReserveBytes > 0);
        Assert.True(error.RequiredBytes > error.AvailableBytes);
        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Equal("AwaitingCandidates", (await read.Jobs.GetAsync(batchId))!.Status);
        Assert.Equal("ReadyToConfirm", (await read.Imports.GetSessionAsync(importId))!.Status);
        Assert.Empty(await CreateBatchServiceTests.AllResourcesAsync(read.Jobs, batchId));
    }

    [Fact]
    public async Task BatchPrecheckAppendIncludesOnlySucceededCandidates()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var account = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var importId = Guid.NewGuid();
        var oldImportId = Guid.NewGuid();
        var succeeded = BatchTestData.ReadyRow(importId, 1) with { PrecheckStatus = "Succeeded" };
        var failed = BatchTestData.ReadyRow(importId, 2) with { PrecheckStatus = "Failed", PrecheckErrorCode = "INVALID" };
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")], BatchTestData.ReadySession(oldImportId, 0, 0), null,
            Job(batchId, oldImportId, "AwaitingCandidates", null, 0, validationMode: "BatchPrecheck"),
            null, null, [Allocation(batchId, account, 2, 0, 0, 0)]);
        await database.SeedAsync([], BatchTestData.ReadySession(importId, 2, 2, appendToBatchJobId: batchId),
            [succeeded, failed]);
        var service = Service(database, long.MaxValue);
        var request = new AppendCandidatesDraft(batchId, importId);

        var preview = await service.PreviewAsync(request);
        await service.ConfirmAsync(request, preview.ConfirmationToken);

        Assert.Equal(1, preview.ResourceCount);
        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        var saved = Assert.Single(await CreateBatchServiceTests.AllResourcesAsync(read.Jobs, batchId));
        Assert.Equal(succeeded.Id, saved.StagedImportRowId);
    }

    [Fact]
    public async Task BatchPrecheckAppendRejectsUnprecheckedReadyCandidateWithoutWrites()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var account = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var importId = Guid.NewGuid();
        var oldImportId = Guid.NewGuid();
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")], BatchTestData.ReadySession(oldImportId, 0, 0), null,
            Job(batchId, oldImportId, "AwaitingCandidates", null, 0, validationMode: "BatchPrecheck"),
            null, null, [Allocation(batchId, account, 1, 0, 0, 0)]);
        await database.SeedAsync([], BatchTestData.ReadySession(importId, 1, 1, appendToBatchJobId: batchId),
            [BatchTestData.ReadyRow(importId, 1)]);
        var service = Service(database, long.MaxValue);

        await Assert.ThrowsAsync<BatchDraftValidationException>(() =>
            service.PreviewAsync(new AppendCandidatesDraft(batchId, importId)));

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Equal("AwaitingCandidates", (await read.Jobs.GetAsync(batchId))!.Status);
        Assert.Equal("ReadyToConfirm", (await read.Imports.GetSessionAsync(importId))!.Status);
        Assert.Empty(await CreateBatchServiceTests.AllResourcesAsync(read.Jobs, batchId));
    }

    [Fact]
    public async Task ReopenShortfallPreviewIsReadOnlyAndConfirmedApplyPreservesTargetsAndResults()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var account = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var oldImportId = Guid.NewGuid();
        var completedAt = BatchTestData.Now.AddHours(1);
        var resource = Resource(batchId, 1, "unused", account, "Unused");
        var allocation = Allocation(batchId, account, 10, 7, 1, 1) with { EffectiveTargetSuccessCount = 7 };
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            BatchTestData.ReadySession(oldImportId, 0, 0),
            null,
            Job(batchId, oldImportId, "CompletedWithShortfall", completedAt, 1),
            [resource], null, [allocation]);
        var service = new ReopenShortfallBatchService(database.UnitOfWorkFactory);

        var preview = await service.PreviewAsync(batchId);

        Assert.Equal(batchId, preview.BatchJobId);
        Assert.Equal("CompletedWithShortfall", preview.JobStatus);
        Assert.Equal(10, preview.TargetSuccessCount);
        Assert.Equal(7, preview.EffectiveTargetSuccessCount);
        Assert.Equal(7, preview.FulfilledCount);
        Assert.Equal(0, preview.ReservedSlotCount);
        Assert.Equal(3, preview.ShortfallCount);
        Assert.Equal(64, preview.ConfirmationToken.Length);
        await using (var beforeApply = await database.UnitOfWorkFactory.BeginAsync())
        {
            Assert.Equal("CompletedWithShortfall", (await beforeApply.Jobs.GetAsync(batchId))!.Status);
            Assert.Equal(7, Assert.Single((await beforeApply.Jobs.GetAllocationsPageAsync(
                batchId, new PageQuery(1, 10))).Items).EffectiveTargetSuccessCount);
        }

        await service.ApplyAsync(batchId, preview.ConfirmationToken, BatchTestData.Now.AddHours(2));

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        var saved = (await read.Jobs.GetAsync(batchId))!;
        var savedAllocation = Assert.Single((await read.Jobs.GetAllocationsPageAsync(batchId, new PageQuery(1, 10))).Items);
        var savedResource = Assert.Single(await CreateBatchServiceTests.AllResourcesAsync(read.Jobs, batchId));
        Assert.Equal("PausedReady", saved.Status);
        Assert.Null(saved.CompletedAt);
        Assert.Equal(10, savedAllocation.TargetSuccessCount);
        Assert.Equal(10, savedAllocation.EffectiveTargetSuccessCount);
        Assert.Equal(7, savedAllocation.FulfilledCount);
        Assert.Equal("Unused", savedResource.CandidateStatus);
    }

    [Fact]
    public async Task ReopenShortfallRejectsStaleSnapshotTokenWithoutWrites()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var account = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var oldImportId = Guid.NewGuid();
        var completedAt = BatchTestData.Now.AddHours(1);
        var allocation = Allocation(batchId, account, 10, 7, 1, 1) with { EffectiveTargetSuccessCount = 7 };
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            BatchTestData.ReadySession(oldImportId, 0, 0),
            null,
            Job(batchId, oldImportId, "CompletedWithShortfall", completedAt, 0),
            null, null, [allocation]);
        var service = new ReopenShortfallBatchService(database.UnitOfWorkFactory);
        var preview = await service.PreviewAsync(batchId);
        await using (var update = await database.UnitOfWorkFactory.BeginAsync())
        {
            await update.Jobs.UpdateAllocationsAsync([allocation with { ReservedSlotCount = 1 }]);
            await update.CommitAsync();
        }

        await Assert.ThrowsAsync<BatchConfirmationException>(() =>
            service.ApplyAsync(batchId, preview.ConfirmationToken, BatchTestData.Now.AddHours(2)));

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        var saved = (await read.Jobs.GetAsync(batchId))!;
        var savedAllocation = Assert.Single((await read.Jobs.GetAllocationsPageAsync(
            batchId, new PageQuery(1, 10))).Items);
        Assert.Equal("CompletedWithShortfall", saved.Status);
        Assert.Equal(completedAt, saved.CompletedAt);
        Assert.Equal(7, savedAllocation.EffectiveTargetSuccessCount);
        Assert.Equal(1, savedAllocation.ReservedSlotCount);
    }

    [Fact]
    public async Task ReopenShortfallApplyIsDurablyIdempotentForConcurrentAndSequentialReplay()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var account = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var oldImportId = Guid.NewGuid();
        var completedAt = BatchTestData.Now.AddHours(1);
        var allocation = Allocation(batchId, account, 10, 7, 1, 1) with { EffectiveTargetSuccessCount = 7 };
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")], BatchTestData.ReadySession(oldImportId, 0, 0), null,
            Job(batchId, oldImportId, "CompletedWithShortfall", completedAt, 0), null, null, [allocation]);
        var service = new ReopenShortfallBatchService(database.UnitOfWorkFactory);
        var preview = await service.PreviewAsync(batchId);
        var reopenedAt = BatchTestData.Now.AddHours(2);

        var concurrent = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => new ReopenShortfallBatchService(database.UnitOfWorkFactory)
                .ApplyAsync(batchId, preview.ConfirmationToken, reopenedAt)));
        var replay = await service.ApplyAsync(batchId, preview.ConfirmationToken, reopenedAt);
        var queried = await service.GetResultAsync(batchId, preview.ConfirmationToken);

        Assert.All(concurrent, result => Assert.Equal(concurrent[0], result));
        Assert.Equal(concurrent[0], replay);
        Assert.Equal(concurrent[0], queried);
        Assert.Equal(batchId, replay.BatchJobId);
        Assert.Equal("SHORTFALL_REOPENED", replay.EventCode);
        await Assert.ThrowsAsync<BatchConfirmationException>(() =>
            service.ApplyAsync(batchId, preview.ConfirmationToken, reopenedAt.AddSeconds(1)));
        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Equal("PausedReady", (await read.Jobs.GetAsync(batchId))!.Status);
        Assert.Equal(10, Assert.Single((await read.Jobs.GetAllocationsPageAsync(
            batchId, new PageQuery(1, 10))).Items).EffectiveTargetSuccessCount);
        Assert.Equal(1L, await CountAuditAsync(database, batchId, "SHORTFALL_REOPENED"));
    }

    private static BatchJobRecord Job(
        Guid id,
        Guid importId,
        string status,
        DateTimeOffset? completedAt,
        int resourceCount,
        string validationMode = "Immediate") =>
        new(id, "Existing", "Excel", "Distribution", "SuccessTarget", "WaitForCandidates",
            resourceCount, 0, "/", "None", "Skip", validationMode, status, BatchTestData.Now,
            completedAt is null ? null : BatchTestData.Now.AddMinutes(1), completedAt, importId, 1);

    private static AppendCandidatesService Service(BatchTestDatabase database, long availableBytes) =>
        new(database.UnitOfWorkFactory,
            new ReplicationPreflightEstimator(
                new ReplicationPreflightEstimatorTests.StubDiskSpaceProbe(availableBytes), database.DatabasePath));

    private static async Task<long> CountAuditAsync(
        BatchTestDatabase database,
        Guid batchJobId,
        string eventCode)
    {
        await using var connection = await database.ConnectionFactory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ExecutionLogs WHERE BatchJobId=$batch AND EventCode=$event;";
        command.Parameters.AddWithValue("$batch", batchJobId.ToString("D"));
        command.Parameters.AddWithValue("$event", eventCode);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static ResourceItemRecord Resource(
        Guid batchId,
        long sequence,
        string normalizedId,
        Guid constraint,
        string status) =>
        new(Guid.NewGuid(), batchId, null, sequence, $"https://pan.baidu.com/s/{normalizedId}", normalizedId,
            null, null, null, null, null, "None", "Unknown", constraint, status, null, null,
            BatchTestData.Now, BatchTestData.Now);

    private static AccountAllocationRecord Allocation(
        Guid batchId,
        Guid account,
        int target,
        int fulfilled,
        int exclusive,
        int unused) =>
        new(Guid.NewGuid(), batchId, account, 0, "/account", "/account", 0, target, target,
            0, 0, fulfilled, 0, fulfilled, 0, 0, exclusive, unused, 0, fulfilled);
}
