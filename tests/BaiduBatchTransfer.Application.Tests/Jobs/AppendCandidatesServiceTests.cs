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
    public async Task ReopenShortfallRequiresExplicitConfirmationAndPreservesTargetsAndResults()
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

        await Assert.ThrowsAsync<BatchConfirmationException>(() =>
            service.ReopenAsync(batchId, userConfirmed: false, BatchTestData.Now.AddHours(2)));
        await service.ReopenAsync(batchId, userConfirmed: true, BatchTestData.Now.AddHours(2));

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

    private static BatchJobRecord Job(
        Guid id,
        Guid importId,
        string status,
        DateTimeOffset? completedAt,
        int resourceCount) =>
        new(id, "Existing", "Excel", "Distribution", "SuccessTarget", "WaitForCandidates",
            resourceCount, 0, "/", "None", "Skip", "Immediate", status, BatchTestData.Now,
            completedAt is null ? null : BatchTestData.Now.AddMinutes(1), completedAt, importId, 1);

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
