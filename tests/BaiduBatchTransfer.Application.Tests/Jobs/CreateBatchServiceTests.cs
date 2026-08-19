using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Application.Tests.Jobs;

public sealed class CreateBatchServiceTests
{
    [Fact]
    public async Task ImmediateValidationCanFinalizeDraftWhenOptionalPrecheckWasSkipped()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            BatchTestData.ReadySession(sessionId, 1, 1) with { Status = "Draft" },
            [BatchTestData.ReadyRow(sessionId, 1)]);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            [new BatchAccountDraft(account, 0, "/target", 1, 0)]);
        var service = Service(database, long.MaxValue);

        var preview = await service.PreviewAsync(draft);
        await service.ConfirmAsync(draft, preview.ConfirmationToken);

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.NotNull(await read.Jobs.GetAsync(draft.BatchJobId));
        Assert.Equal("BatchCreated", (await read.Imports.GetSessionAsync(sessionId))!.Status);
    }

    [Fact]
    public async Task MidTransactionOperationFailureRollsBackBatchResourcesAllocationsAuditAndImportCommit()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            BatchTestData.ReadySession(sessionId, 1, 1),
            [BatchTestData.ReadyRow(sessionId, 1)]);
        var draft = Draft(sessionId, TransferMode.Replication, QuotaBasis.TaskCount,
            [new BatchAccountDraft(account, 0, "/target", 1, 1)]);
        var service = Service(database, long.MaxValue);
        var preview = await service.PreviewAsync(draft);
        await using (var connection = await database.ConnectionFactory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER FailTask11Operation BEFORE INSERT ON TransferOperations BEGIN SELECT RAISE(ABORT, 'forced task11 rollback'); END;";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => service.ConfirmAsync(draft, preview.ConfirmationToken));

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Null(await read.Jobs.GetAsync(draft.BatchJobId));
        Assert.Empty(await AllResourcesAsync(read.Jobs, draft.BatchJobId));
        Assert.Empty((await read.Jobs.GetAllocationsPageAsync(draft.BatchJobId, new PageQuery(1, 10))).Items);
        Assert.Equal("ReadyToConfirm", (await read.Imports.GetSessionAsync(sessionId))!.Status);
        await using var verify = await database.ConnectionFactory.OpenConnectionAsync();
        await using var count = verify.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM ExecutionLogs WHERE BatchJobId=$id;";
        count.Parameters.AddWithValue("$id", draft.BatchJobId.ToString("D"));
        Assert.Equal(0L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ConfirmationTokenBecomesStaleWhenImportChangesAndWritesNothing()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            BatchTestData.ReadySession(sessionId, 1, 1),
            [BatchTestData.ReadyRow(sessionId, 1)]);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            [new BatchAccountDraft(account, 0, "/target", 1, 0)]);
        var service = Service(database, long.MaxValue);
        var preview = await service.PreviewAsync(draft);
        await using (var update = await database.UnitOfWorkFactory.BeginAsync())
        {
            var session = (await update.Imports.GetSessionAsync(sessionId))!;
            await update.Imports.UpdateSessionAsync(session with { UpdatedAt = session.UpdatedAt.AddSeconds(1) });
            await update.CommitAsync();
        }

        await Assert.ThrowsAsync<BatchConfirmationException>(() =>
            service.ConfirmAsync(draft, preview.ConfirmationToken));

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Null(await read.Jobs.GetAsync(draft.BatchJobId));
    }

    [Fact]
    public async Task ConfirmationTokenBecomesStaleWhenRowChangesEvenIfSessionTimestampDoesNot()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        var row = BatchTestData.ReadyRow(sessionId, 1, note: "before");
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            BatchTestData.ReadySession(sessionId, 1, 1),
            [row]);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            [new BatchAccountDraft(account, 0, "/target", 1, 0)]);
        var service = Service(database, long.MaxValue);
        var preview = await service.PreviewAsync(draft);
        await using (var update = await database.UnitOfWorkFactory.BeginAsync())
        {
            await update.Imports.UpdateRowsAsync([row with { Note = "after", UpdatedAt = row.UpdatedAt.AddSeconds(1) }]);
            await update.CommitAsync();
        }

        await Assert.ThrowsAsync<BatchConfirmationException>(() =>
            service.ConfirmAsync(draft, preview.ConfirmationToken));

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Null(await read.Jobs.GetAsync(draft.BatchJobId));
    }

    [Fact]
    public async Task ConfirmationTokenCannotBeReusedWithDifferentImmutableSettings()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            BatchTestData.ReadySession(sessionId, 1, 1),
            [BatchTestData.ReadyRow(sessionId, 1)]);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            [new BatchAccountDraft(account, 0, "/target", 1, 0)]);
        var service = Service(database, long.MaxValue);
        var preview = await service.PreviewAsync(draft);
        var changed = new BatchDraft(
            draft.BatchJobId, draft.ImportSessionId, draft.Name, draft.TransferMode, draft.QuotaBasis,
            draft.ShortagePolicy, draft.Accounts, draft.DefaultTargetPath, draft.FolderCreationMode,
            DuplicatePolicy.Rename, draft.ValidationMode, draft.QueueOrder, draft.IgnoreMappedAccountsForReplication);

        await Assert.ThrowsAsync<BatchConfirmationException>(() =>
            service.ConfirmAsync(changed, preview.ConfirmationToken));

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Null(await read.Jobs.GetAsync(draft.BatchJobId));
    }

    [Fact]
    public async Task AuditUsesFixedSanitizedTextOnly()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        var sensitiveUrl = "https://pan.baidu.com/s/private-resource";
        var sensitiveCode = "p4ss";
        var sensitiveNote = "customer-secret-note";
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            BatchTestData.ReadySession(sessionId, 1, 1),
            [BatchTestData.ReadyRow(sessionId, 1, note: sensitiveNote) with { ShareUrl = sensitiveUrl, ExtractionCode = sensitiveCode }]);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            [new BatchAccountDraft(account, 0, "/target", 1, 0)]);
        var service = Service(database, long.MaxValue);
        var preview = await service.PreviewAsync(draft);
        await service.ConfirmAsync(draft, preview.ConfirmationToken);

        await using var connection = await database.ConnectionFactory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EventCode,SanitizedMessage FROM ExecutionLogs WHERE BatchJobId=$id;";
        command.Parameters.AddWithValue("$id", draft.BatchJobId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var text = $"{reader.GetString(0)} {reader.GetString(1)}";
        Assert.Equal("BATCH_CREATED Batch creation was confirmed.", text);
        Assert.DoesNotContain(sensitiveUrl, text, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveCode, text, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveNote, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CandidateConstrainedToRemovedZeroTargetAccountIsRejected()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var positive = Guid.NewGuid();
        var zero = Guid.NewGuid();
        await database.SeedAsync(
            [BatchTestData.Account(positive, "positive"), BatchTestData.Account(zero, "zero")],
            BatchTestData.ReadySession(sessionId, 1, 1),
            [BatchTestData.ReadyRow(sessionId, 1, mappedAccountId: zero)]);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.SuccessTarget,
            [new BatchAccountDraft(positive, 0, "/positive", 0, 1), new BatchAccountDraft(zero, 1, "/zero", 0, 0)]);

        await Assert.ThrowsAsync<BatchDraftValidationException>(() => Service(database, long.MaxValue).PreviewAsync(draft));
    }

    [Fact]
    public async Task ConfirmedReplicationCreatesOneThousandResourcesAndThreeThousandUniqueOperations()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var accountIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        await database.SeedAsync(
            accountIds.Select((id, index) => BatchTestData.Account(id, index.ToString())),
            BatchTestData.ReadySession(sessionId, 1_000, 1_000),
            Enumerable.Range(1, 1_000).Select(row => BatchTestData.ReadyRow(
                sessionId, row, targetPath: row == 1 ? "/row override" : null,
                folder: row == 1 ? "outer" : null, note: row == 1 ? "private note" : null)));
        var draft = Draft(
            sessionId,
            TransferMode.Replication,
            QuotaBasis.TaskCount,
            accountIds.Select((id, index) => new BatchAccountDraft(
                id, index, $"/account-{index}", index == 0 ? 0 : index * 17,
                index == 0 ? 0 : index * 123)).ToArray());
        var service = Service(database, long.MaxValue);

        var preview = await service.PreviewAsync(draft);
        Assert.Equal(1_000, preview.ResourceCount);
        Assert.Equal(3_000, preview.OperationCount);
        Assert.All(preview.Accounts, account =>
        {
            Assert.Equal(1_000, account.TargetItemCount);
            Assert.Equal(1_000, account.TargetSuccessCount);
            Assert.Equal(1_000, account.EffectiveTargetSuccessCount);
            Assert.Equal(1_000, account.ReachableTargetSuccessCount);
        });

        await service.ConfirmAsync(draft, preview.ConfirmationToken);

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        var job = Assert.IsType<BatchJobRecord>(await read.Jobs.GetAsync(draft.BatchJobId));
        var resources = await AllResourcesAsync(read.Jobs, draft.BatchJobId);
        var operations = await AllOperationsAsync(read.Jobs, draft.BatchJobId);
        var allocations = (await read.Jobs.GetAllocationsPageAsync(draft.BatchJobId, new PageQuery(1, 10))).Items;
        Assert.Equal(1_000, job.ResourceCount);
        Assert.Equal(3_000, job.OperationCount);
        Assert.Equal(1_000, resources.Count);
        Assert.Equal(3_000, operations.Count);
        Assert.Equal(3_000, operations.Select(operation => operation.OperationFingerprint).Distinct().Count());
        Assert.Equal(accountIds, allocations.Select(allocation => allocation.AccountId));
        Assert.Equal(new[] { "/account-0", "/account-1", "/account-2" }, allocations.Select(allocation => allocation.DefaultTargetPath));
        Assert.All(allocations, allocation =>
        {
            Assert.Equal(1_000, allocation.TargetItemCount);
            Assert.Equal(1_000, allocation.TargetSuccessCount);
            Assert.Equal(1_000, allocation.EffectiveTargetSuccessCount);
        });
        var overridden = resources.Single(resource => resource.CandidateSequence == 1);
        Assert.Equal("private note", overridden.Note);
        Assert.Equal("/row override", overridden.TargetPathOverride);
        Assert.All(operations.Where(operation => operation.ResourceItemId == overridden.Id), operation =>
        {
            Assert.Equal("/row override", operation.TargetPath);
            Assert.Equal("outer", operation.CreateFolderName);
        });
        var committed = await read.Imports.GetSessionAsync(sessionId);
        Assert.Equal("BatchCreated", committed!.Status);
        Assert.Equal(draft.BatchJobId, committed.CreatedBatchJobId);
    }

    [Fact]
    public async Task InsufficientDiskRejectsBeforeAnyBatchWrite()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            BatchTestData.ReadySession(sessionId, 1, 1),
            [BatchTestData.ReadyRow(sessionId, 1)]);
        var draft = Draft(sessionId, TransferMode.Replication, QuotaBasis.TaskCount,
            [new BatchAccountDraft(account, 0, "/target", 1, 1)]);
        var service = Service(database, 0);

        var preview = await service.PreviewAsync(draft);
        Assert.False(preview.StorageEstimate.HasSufficientSpace);
        await Assert.ThrowsAsync<StorageCapacityException>(() =>
            service.ConfirmAsync(draft, preview.ConfirmationToken));

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Null(await read.Jobs.GetAsync(draft.BatchJobId));
        Assert.Equal("ReadyToConfirm", (await read.Imports.GetSessionAsync(sessionId))!.Status);
    }

    [Fact]
    public async Task TaskQuotaDistributionHonorsLockedAssignmentsSnapshotsAndRemovesZeroTargets()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var accounts = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var rows = Enumerable.Range(1, 6)
            .Select(row => BatchTestData.ReadyRow(
                sessionId, row, mappedAccountId: row == 1 ? accounts[1] : null,
                targetPath: row == 1 ? "/locked-path" : null,
                folder: row == 1 ? "locked-folder" : null,
                note: row == 1 ? "retain me" : null))
            .ToArray();
        await database.SeedAsync(
            accounts.Select((id, index) => BatchTestData.Account(id, index.ToString())),
            BatchTestData.ReadySession(sessionId, 6, 6), rows);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            [
                new BatchAccountDraft(accounts[0], 0, "/a", 3, 0),
                new BatchAccountDraft(accounts[1], 1, "/b", 3, 0),
                new BatchAccountDraft(accounts[2], 2, "/removed", 0, 0),
            ]);
        var service = Service(database, long.MaxValue);
        var preview = await service.PreviewAsync(draft);

        await service.ConfirmAsync(draft, preview.ConfirmationToken);

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        var allocations = (await read.Jobs.GetAllocationsPageAsync(draft.BatchJobId, new PageQuery(1, 10))).Items;
        var resources = await AllResourcesAsync(read.Jobs, draft.BatchJobId);
        var operations = await AllOperationsAsync(read.Jobs, draft.BatchJobId);
        Assert.Equal(6, resources.Count);
        Assert.Equal(6, operations.Count);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6 }, resources.Select(resource => resource.CandidateSequence));
        Assert.Equal(2, allocations.Count);
        Assert.Equal(6, allocations.Sum(allocation => allocation.TargetItemCount));
        Assert.Equal(1, allocations.Single(allocation => allocation.AccountId == accounts[1]).LockedItemCount);
        var lockedResource = resources.Single(resource => resource.StagedImportRowId == rows[0].Id);
        var lockedOperation = operations.Single(operation => operation.ResourceItemId == lockedResource.Id);
        Assert.Equal(accounts[1], lockedOperation.AssignedAccountId);
        Assert.Equal("/locked-path", lockedOperation.TargetPath);
        Assert.Equal("locked-folder", lockedOperation.CreateFolderName);
        Assert.Equal("retain me", lockedResource.Note);
    }

    [Fact]
    public async Task SuccessTargetDistributionPersistsExclusiveCandidatesAndDefersOperations()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var accounts = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        await database.SeedAsync(
            accounts.Select((id, index) => BatchTestData.Account(id, index.ToString())),
            BatchTestData.ReadySession(sessionId, 5, 5),
            Enumerable.Range(1, 5).Select(row => BatchTestData.ReadyRow(
                sessionId, row, mappedAccountId: row <= 2 ? accounts[0] : null)));
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.SuccessTarget,
            [
                new BatchAccountDraft(accounts[0], 0, "/a", 0, 2),
                new BatchAccountDraft(accounts[1], 1, "/b", 0, 1),
                new BatchAccountDraft(accounts[2], 2, "/removed", 0, 0),
            ]);
        var service = Service(database, long.MaxValue);
        var preview = await service.PreviewAsync(draft);

        await service.ConfirmAsync(draft, preview.ConfirmationToken);

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        var resources = await AllResourcesAsync(read.Jobs, draft.BatchJobId);
        var operations = await AllOperationsAsync(read.Jobs, draft.BatchJobId);
        var allocations = (await read.Jobs.GetAllocationsPageAsync(draft.BatchJobId, new PageQuery(1, 10))).Items;
        Assert.Equal(5, preview.OperationCount);
        Assert.Equal(5, preview.StorageEstimate.OperationCount);
        Assert.Empty(operations);
        Assert.Equal(2, resources.Count(resource => resource.CandidateConstraintAccountId == accounts[0]));
        Assert.Equal(2, allocations.Single(allocation => allocation.AccountId == accounts[0]).ExclusiveCandidateCount);
        Assert.DoesNotContain(allocations, allocation => allocation.AccountId == accounts[2]);
    }

    [Fact]
    public async Task SuccessTargetPreviewShowsDeterministicEffectiveTargetsUnderExclusiveShortage()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var accounts = new[] { Guid.NewGuid(), Guid.NewGuid() };
        await database.SeedAsync(
            accounts.Select((id, index) => BatchTestData.Account(id, index.ToString())),
            BatchTestData.ReadySession(sessionId, 4, 4),
            Enumerable.Range(1, 4).Select(row => BatchTestData.ReadyRow(sessionId, row, mappedAccountId: accounts[0])));
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.SuccessTarget,
            [new BatchAccountDraft(accounts[0], 0, "/a", 0, 2), new BatchAccountDraft(accounts[1], 1, "/b", 0, 2)]);

        var preview = await Service(database, long.MaxValue).PreviewAsync(draft);

        Assert.Equal(2, preview.Accounts.Single(account => account.AccountId == accounts[0]).EffectiveTargetSuccessCount);
        Assert.Equal(0, preview.Accounts.Single(account => account.AccountId == accounts[1]).EffectiveTargetSuccessCount);
        Assert.Equal(2, preview.Accounts.Single(account => account.AccountId == accounts[0]).ReachableTargetSuccessCount);
        Assert.Equal(0, preview.Accounts.Single(account => account.AccountId == accounts[1]).ReachableTargetSuccessCount);
    }

    [Theory]
    [InlineData("Conflict")]
    [InlineData("NeedsResolution")]
    public async Task ConflictOrNeedsResolutionRowsBlockFinalizationWithoutLeakingContent(string disposition)
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        var invalidCount = disposition is "Invalid" or "Conflict" ? 1 : 0;
        var session = BatchTestData.ReadySession(sessionId, 1, 0, invalidRows: invalidCount);
        var sensitiveUrl = "https://pan.baidu.com/s/do-not-leak?pwd=secret";
        var row = BatchTestData.ReadyRow(sessionId, 1) with
        {
            ShareUrl = sensitiveUrl,
            ExtractionCode = "secret",
            NormalizedShareId = disposition == "NeedsResolution" ? null : "sensitive-id",
            Disposition = disposition,
            ValidationMessage = "sensitive validation text",
        };
        await database.SeedAsync([BatchTestData.Account(account, "a")], session, [row]);
        var service = Service(database, long.MaxValue);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            [new BatchAccountDraft(account, 0, "/target", 0, 0)]);

        var error = await Assert.ThrowsAsync<BatchDraftValidationException>(() => service.PreviewAsync(draft));

        Assert.DoesNotContain(sensitiveUrl, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Null(await read.Jobs.GetAsync(draft.BatchJobId));
    }

    [Fact]
    public async Task ClassifiedInvalidRowsAreExcludedWithoutBlockingValidResources()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        var invalid = BatchTestData.ReadyRow(sessionId, 1) with
        {
            Disposition = "Invalid",
            NormalizedShareId = null,
            ValidationMessage = "malformed input",
        };
        var valid = BatchTestData.ReadyRow(sessionId, 2);
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            BatchTestData.ReadySession(sessionId, 2, 1, invalidRows: 1),
            [invalid, valid]);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            [new BatchAccountDraft(account, 0, "/target", 1, 0)]);
        var service = Service(database, long.MaxValue);

        var preview = await service.PreviewAsync(draft);
        await service.ConfirmAsync(draft, preview.ConfirmationToken);

        Assert.Equal(1, preview.ResourceCount);
        Assert.Equal(1, preview.OperationCount);
        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        var resource = Assert.Single(await AllResourcesAsync(read.Jobs, draft.BatchJobId));
        Assert.Equal(valid.Id, resource.StagedImportRowId);
    }

    [Fact]
    public async Task ExcludedDuplicateAndFailedPrecheckRowsDoNotBecomeResources()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        var ready = BatchTestData.ReadyRow(sessionId, 1);
        var failedPrecheck = BatchTestData.ReadyRow(sessionId, 2) with { PrecheckStatus = "Failed", PrecheckErrorCode = "INVALID", PrecheckedAt = BatchTestData.Now };
        var duplicate = BatchTestData.ReadyRow(sessionId, 3) with { Disposition = "Duplicate", DuplicateOfStagedRowId = ready.Id };
        var excluded = BatchTestData.ReadyRow(sessionId, 4) with { Disposition = "Excluded" };
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            BatchTestData.ReadySession(sessionId, 4, 2, duplicateRows: 1, excludedRows: 1),
            [ready, failedPrecheck, duplicate, excluded]);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            [new BatchAccountDraft(account, 0, "/target", 1, 0)]);
        var service = Service(database, long.MaxValue);

        var preview = await service.PreviewAsync(draft);
        await service.ConfirmAsync(draft, preview.ConfirmationToken);

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Single(await AllResourcesAsync(read.Jobs, draft.BatchJobId));
    }

    private static CreateBatchService Service(BatchTestDatabase database, long availableBytes) =>
        new(database.UnitOfWorkFactory, new BatchDraftValidator(),
            new ReplicationPreflightEstimator(
                new ReplicationPreflightEstimatorTests.StubDiskSpaceProbe(availableBytes),
                database.DatabasePath));

    private static BatchDraft Draft(
        Guid importSessionId,
        TransferMode mode,
        QuotaBasis quota,
        IReadOnlyList<BatchAccountDraft> accounts) =>
        new(Guid.NewGuid(), importSessionId, "Batch", mode, quota, ShortagePolicy.ProportionalReduction,
            accounts, "/", "None", DuplicatePolicy.Skip, "Immediate", 1, true);

    internal static async Task<IReadOnlyList<ResourceItemRecord>> AllResourcesAsync(IJobRepository jobs, Guid batchJobId)
    {
        var result = new List<ResourceItemRecord>();
        for (var page = 1; ; page++)
        {
            var current = await jobs.GetResourcesPageAsync(batchJobId, new PageQuery(page, 1_000));
            result.AddRange(current.Items);
            if (result.Count >= current.TotalCount) return result;
        }
    }

    internal static async Task<IReadOnlyList<TransferOperationRecord>> AllOperationsAsync(IJobRepository jobs, Guid batchJobId)
    {
        var result = new List<TransferOperationRecord>();
        for (var page = 1; ; page++)
        {
            var current = await jobs.GetOperationsPageAsync(batchJobId, new PageQuery(page, 1_000));
            result.AddRange(current.Items);
            if (result.Count >= current.TotalCount) return result;
        }
    }
}
