using System.Collections.Immutable;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Application.Tests.Jobs;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;

namespace BaiduBatchTransfer.Application.Tests.Scheduling;

public sealed class ReconciliationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 2, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(TransferConfirmation.ConfirmedPresent)]
    [InlineData(TransferConfirmation.ConfirmedAbsent)]
    [InlineData(TransferConfirmation.Ambiguous)]
    public async Task LegacyDescriptorOnlyConfirmationNeverFinalizesWithoutExactTypedEvidence(
        TransferConfirmation confirmation)
    {
        var operation = PendingOperation() with { RequiresExactShareEvidence = true };
        var store = new RecordingReconciliationStore(operation);
        var adapter = new ConfirmationAdapter
        {
            Result = BaiduAdapterResult<TransferConfirmation>.Success(confirmation),
        };
        var service = new OperationReconciliationService(
            adapter,
            new SessionStore(operation.AccountId),
            store,
            new FixedTimeProvider(Now));

        var outcome = await service.ReconcileAsync(operation.OperationId);

        Assert.Equal(TransferConfirmation.Ambiguous, outcome);
        Assert.NotNull(adapter.Query);
        Assert.Null(adapter.Query!.AdapterRequestId);
        Assert.Empty(adapter.Query.RemoteItemIds);
        Assert.False(store.Present);
        Assert.False(store.Absent);
        Assert.False(store.ResetForRetry);
    }

    [Fact]
    public async Task OptionalResponseIdentifiersEnrichButDoNotReplaceTheDescriptorQuery()
    {
        var operation = PendingOperation() with
        {
            AdapterRequestId = " request-1 ",
            RemoteItemIdsJson = "[\"remote-2\",\"remote-1\",\"remote-1\"]",
        };
        var adapter = new ConfirmationAdapter();
        var service = new OperationReconciliationService(
            adapter,
            new SessionStore(operation.AccountId),
            new RecordingReconciliationStore(operation),
            new FixedTimeProvider(Now));

        await service.ReconcileAsync(operation.OperationId);

        Assert.Equal("share", adapter.Query!.SourceShareId);
        Assert.Equal("request-1", adapter.Query.AdapterRequestId);
        Assert.Equal(["remote-1", "remote-2"], adapter.Query.RemoteItemIds);
    }

    [Fact]
    public async Task AdapterFailureOrTimeoutNeverInfersAbsenceAndLeavesOperationPending()
    {
        var operation = PendingOperation();
        var store = new RecordingReconciliationStore(operation);
        var adapter = new ConfirmationAdapter
        {
            Result = BaiduAdapterResult<TransferConfirmation>.Failure(
                BaiduErrorKind.TransientService,
                "private timeout"),
        };
        var service = new OperationReconciliationService(
            adapter,
            new SessionStore(operation.AccountId),
            store,
            new FixedTimeProvider(Now));

        var outcome = await service.ReconcileAsync(operation.OperationId);

        Assert.Equal(TransferConfirmation.Ambiguous, outcome);
        Assert.False(store.Present);
        Assert.False(store.Absent);
        Assert.False(store.ResetForRetry);
    }

    [Fact]
    public async Task UserCannotBlindRetryAnUnknownSubmissionEvenAfterAcceptingDuplicateRisk()
    {
        var operation = PendingOperation();
        var store = new RecordingReconciliationStore(operation);
        var service = new OperationReconciliationService(
            new ConfirmationAdapter(),
            new SessionStore(operation.AccountId),
            store,
            new FixedTimeProvider(Now));

        var warning = await service.RetryAsync(operation.OperationId, duplicateRiskAccepted: false);

        Assert.True(warning.RequiresConfirmation);
        Assert.False(warning.ResetForRetry);
        Assert.Contains("重复", warning.Warning, StringComparison.Ordinal);
        Assert.False(store.ResetForRetry);

        var accepted = await service.RetryAsync(operation.OperationId, duplicateRiskAccepted: true);

        Assert.True(accepted.RequiresConfirmation);
        Assert.False(accepted.ResetForRetry);
        Assert.Null(store.ResetOperationId);
    }

    [Fact]
    public async Task ExactPresentConfirmationPassesCanonicalEvidenceToAtomicCompletion()
    {
        var operation = PendingOperation() with { RequiresExactShareEvidence = true };
        var exact = new RemoteItemIdentitySet([
            new RemoteItemIdentity("remote-2", "第二项"),
            new RemoteItemIdentity("remote-1", "第一项"),
        ]);
        var store = new RecordingReconciliationStore(operation);
        var adapter = new ConfirmationAdapter
        {
            Result = BaiduAdapterResult<TransferConfirmation>.Success(TransferConfirmation.ConfirmedPresent),
            ExactResult = BaiduAdapterResult<TransferConfirmationResult>.Success(
                TransferConfirmationResult.ExactPresent(exact)),
        };
        var service = new OperationReconciliationService(
            adapter,
            new SessionStore(operation.AccountId),
            store,
            new FixedTimeProvider(Now));

        var outcome = await service.ReconcileAsync(operation.OperationId);

        Assert.Equal(TransferConfirmation.ConfirmedPresent, outcome);
        Assert.True(store.Present);
        Assert.Same(exact, store.ExactRemoteItems);
    }

    [Fact]
    public async Task MultipleApproximateMatchesRemainAmbiguousAndNeverUseLegacyPresentSignal()
    {
        var operation = PendingOperation() with { RequiresExactShareEvidence = true };
        var store = new RecordingReconciliationStore(operation);
        var adapter = new ConfirmationAdapter
        {
            Result = BaiduAdapterResult<TransferConfirmation>.Success(TransferConfirmation.ConfirmedPresent),
            ExactResult = BaiduAdapterResult<TransferConfirmationResult>.Success(
                TransferConfirmationResult.Ambiguous),
        };
        var service = new OperationReconciliationService(
            adapter,
            new SessionStore(operation.AccountId),
            store,
            new FixedTimeProvider(Now));

        var outcome = await service.ReconcileAsync(operation.OperationId);

        Assert.Equal(TransferConfirmation.Ambiguous, outcome);
        Assert.False(store.Present);
        Assert.False(store.Absent);
        Assert.False(store.ResetForRetry);
    }

    [Fact]
    public async Task LegacyTransferOnlyConfirmedPresentRemainsBackwardCompatibleWithoutExactIds()
    {
        var operation = PendingOperation() with { RequiresExactShareEvidence = false };
        var store = new RecordingReconciliationStore(operation);
        var adapter = new ConfirmationAdapter
        {
            Result = BaiduAdapterResult<TransferConfirmation>.Success(TransferConfirmation.ConfirmedPresent),
            ExactResult = BaiduAdapterResult<TransferConfirmationResult>.Success(TransferConfirmationResult.Ambiguous),
        };
        var service = new OperationReconciliationService(
            adapter,
            new SessionStore(operation.AccountId),
            store,
            new FixedTimeProvider(Now));

        var result = await service.ReconcileAsync(operation.OperationId);

        Assert.Equal(TransferConfirmation.ConfirmedPresent, result);
        Assert.True(store.Present);
        Assert.Null(store.ExactRemoteItems);
    }

    [Fact]
    public async Task DefaultExactConfirmationWrapperPreservesDefinitelyNotAcceptedEvidence()
    {
        var operation = PendingOperation() with { RequiresExactShareEvidence = true };
        var store = new RecordingReconciliationStore(operation);
        var service = new OperationReconciliationService(
            new LegacyOnlyConfirmationAdapter(
                BaiduAdapterResult<TransferConfirmation>.Success(
                    TransferConfirmation.ConfirmedAbsent,
                    SubmissionAcceptance.DefinitelyNotAccepted)),
            new SessionStore(operation.AccountId),
            store,
            new FixedTimeProvider(Now));

        var result = await service.ReconcileAsync(operation.OperationId);

        Assert.Equal(TransferConfirmation.ConfirmedAbsent, result);
        Assert.True(store.Absent);
    }

    [Theory]
    [InlineData(SubmissionAcceptance.Unknown, TransferConfirmation.Ambiguous, false)]
    [InlineData(SubmissionAcceptance.DefinitelyNotAccepted, TransferConfirmation.ConfirmedAbsent, true)]
    public async Task AbsentOnlyReleasesPendingWorkWithExplicitDefinitelyNotAcceptedEvidence(
        SubmissionAcceptance acceptance,
        TransferConfirmation expectedOutcome,
        bool expectedAbsentWrite)
    {
        var operation = PendingOperation() with { RequiresExactShareEvidence = true };
        var store = new RecordingReconciliationStore(operation);
        var adapter = new ConfirmationAdapter
        {
            ExactResult = BaiduAdapterResult<TransferConfirmationResult>.Success(
                TransferConfirmationResult.Absent,
                acceptance),
        };
        var service = new OperationReconciliationService(
            adapter,
            new SessionStore(operation.AccountId),
            store,
            new FixedTimeProvider(Now));

        var outcome = await service.ReconcileAsync(operation.OperationId);

        Assert.Equal(expectedOutcome, outcome);
        Assert.Equal(expectedAbsentWrite, store.Absent);
        Assert.False(store.Present);
    }

    [Theory]
    [InlineData("用户标题", "用户标题")]
    [InlineData("预检回退标题", "预检回退标题")]
    public async Task SqliteExactCompletionAtomicallySealsShareEvidenceCountersAndTitle(
        string? persistedTitle,
        string expectedTitle)
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var data = await SeedPendingShareAsync(db, persistedTitle);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var pending = await store.LoadPendingAsync(data.OperationId);
        var exact = new RemoteItemIdentitySet([
            new RemoteItemIdentity("remote-2", "第二项"),
            new RemoteItemIdentity("remote-1", "第一项"),
        ]);

        await store.MarkConfirmedPresentWithEvidenceAsync(pending!, exact, Now);

        Assert.Equal("Succeeded", await ScalarAsync(db, "TransferOperations", "Status"));
        Assert.Equal("WaitingShare", await ScalarAsync(db, "ShareOperations", "Status"));
        Assert.Equal(exact.Fingerprint, await ScalarAsync(db, "ShareOperations", "ExactItemSetFingerprint"));
        Assert.Equal(2, await ScalarIntAsync(db, "ShareOperationGenerations", "ExactItemCount"));
        Assert.Equal(2, await ScalarIntAsync(db, "(SELECT COUNT(*) AS ItemCount FROM ShareOperationRemoteItems)", "ItemCount"));
        Assert.Equal(0, await ScalarIntAsync(db, "AccountAllocations", "ReservedSlotCount"));
        Assert.Equal(1, await ScalarIntAsync(db, "AccountAllocations", "FulfilledCount"));
        Assert.Equal(expectedTitle, await ScalarAsync(db, "ResourceItems", "ResolvedOutputTitle"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task ShareEnabledExactCompletionRejectsBlankPersistedTitleAndRollsBackEntireHandoff(
        string? persistedTitle)
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var data = await SeedPendingShareAsync(db, persistedTitle);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var pending = await store.LoadPendingAsync(data.OperationId);
        var exact = new RemoteItemIdentitySet([
            new RemoteItemIdentity("remote-2", "第二项"),
            new RemoteItemIdentity("remote-1", "第一项"),
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.MarkConfirmedPresentWithEvidenceAsync(pending!, exact, Now));

        Assert.Equal("ResultPending", await ScalarAsync(db, "TransferOperations", "Status"));
        Assert.Equal("Reserved", await ScalarAsync(db, "TransferOperations", "TargetSlotState"));
        Assert.Null(await ScalarNullableAsync(db, "TransferOperations", "OutcomeKind"));
        Assert.Null(await ScalarNullableAsync(db, "TransferOperations", "RemoteItemIdsJson"));
        Assert.Equal("WaitingTransfer", await ScalarAsync(db, "ShareOperations", "Status"));
        Assert.Null(await ScalarNullableAsync(db, "ShareOperations", "ExactItemSetFingerprint"));
        Assert.Equal(0, await ScalarIntAsync(db, "(SELECT COUNT(*) AS ItemCount FROM ShareOperationRemoteItems)", "ItemCount"));
        Assert.Equal(0, await ScalarIntAsync(db, "(SELECT COUNT(*) AS GenerationCount FROM ShareOperationGenerations)", "GenerationCount"));
        Assert.Equal(1, await ScalarIntAsync(db, "AccountAllocations", "ReservedSlotCount"));
        Assert.Equal(0, await ScalarIntAsync(db, "AccountAllocations", "FulfilledCount"));
        Assert.Equal(persistedTitle, await ScalarNullableAsync(db, "ResourceItems", "ResolvedOutputTitle"));
    }

    [Fact]
    public async Task LegacyTransferOnlyExactCompletionMayBuildFallbackForBlankPersistedTitle()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var data = await SeedPendingAsync(db);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var exact = new RemoteItemIdentitySet([
            new RemoteItemIdentity("remote-2", "第二项"),
            new RemoteItemIdentity("remote-1", "第一项"),
        ]);

        await store.CompleteSucceededWithEvidenceAsync(data.OperationId, null, exact, Now);

        Assert.Equal("Succeeded", await ScalarAsync(db, "TransferOperations", "Status"));
        Assert.Equal("第一项 等 2 项", await ScalarAsync(db, "ResourceItems", "ResolvedOutputTitle"));
        Assert.Equal(0, await ScalarIntAsync(db, "(SELECT COUNT(*) AS ShareCount FROM ShareOperations)", "ShareCount"));
        Assert.Equal(0, await ScalarIntAsync(db, "AccountAllocations", "ReservedSlotCount"));
        Assert.Equal(1, await ScalarIntAsync(db, "AccountAllocations", "FulfilledCount"));
    }

    [Fact]
    public async Task FailureWhilePersistingExactItemsRollsBackTheEntireTransferShareHandoff()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var data = await SeedPendingShareAsync(db, "原有回退");
        await ExecuteAsync(db, """
            CREATE TRIGGER fail_exact_item BEFORE INSERT ON ShareOperationRemoteItems
            BEGIN SELECT RAISE(ABORT,'injected exact item failure'); END;
            """);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var pending = await store.LoadPendingAsync(data.OperationId);
        var exact = new RemoteItemIdentitySet([new RemoteItemIdentity("remote-1", "远端名称")]);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            store.MarkConfirmedPresentWithEvidenceAsync(pending!, exact, Now));

        Assert.Equal("ResultPending", await ScalarAsync(db, "TransferOperations", "Status"));
        Assert.Equal("Reserved", await ScalarAsync(db, "TransferOperations", "TargetSlotState"));
        Assert.Equal("WaitingTransfer", await ScalarAsync(db, "ShareOperations", "Status"));
        Assert.Null(await ScalarNullableAsync(db, "ShareOperations", "ExactItemSetFingerprint"));
        Assert.Equal(0, await ScalarIntAsync(db, "(SELECT COUNT(*) AS ItemCount FROM ShareOperationRemoteItems)", "ItemCount"));
        Assert.Equal(0, await ScalarIntAsync(db, "(SELECT COUNT(*) AS GenerationCount FROM ShareOperationGenerations)", "GenerationCount"));
        Assert.Equal(1, await ScalarIntAsync(db, "AccountAllocations", "ReservedSlotCount"));
        Assert.Equal(0, await ScalarIntAsync(db, "AccountAllocations", "FulfilledCount"));
        Assert.Equal("原有回退", await ScalarAsync(db, "ResourceItems", "ResolvedOutputTitle"));
    }

    [Fact]
    public async Task DefinitelyAbsentResetsTheSameTransferSafelyWithoutReleasingShareWork()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var data = await SeedPendingShareAsync(db, "预检标题");
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var service = new OperationReconciliationService(
            new ConfirmationAdapter
            {
                ExactResult = BaiduAdapterResult<TransferConfirmationResult>.Success(
                    TransferConfirmationResult.Absent,
                    SubmissionAcceptance.DefinitelyNotAccepted),
            },
            new SessionStore(data.AccountId),
            store,
            new FixedTimeProvider(Now));

        var result = await service.ReconcileAsync(data.OperationId);

        Assert.Equal(TransferConfirmation.ConfirmedAbsent, result);
        Assert.Equal("Waiting", await ScalarAsync(db, "TransferOperations", "Status"));
        Assert.Equal("Released", await ScalarAsync(db, "TransferOperations", "TargetSlotState"));
        Assert.Equal("WaitingTransfer", await ScalarAsync(db, "ShareOperations", "Status"));
        Assert.Equal(0, await ScalarIntAsync(db, "AccountAllocations", "ReservedSlotCount"));
        Assert.Equal(0, await ScalarIntAsync(db, "AccountAllocations", "FulfilledCount"));
        Assert.Equal("预检标题", await ScalarAsync(db, "ResourceItems", "ResolvedOutputTitle"));
    }

    [Fact]
    public async Task LegacySuccessStoreApisCannotBypassExactEvidenceForShareEnabledWork()
    {
        await using var firstDb = await BatchTestDatabase.CreateAsync();
        var first = await SeedPendingShareAsync(firstDb, "标题");
        var firstStore = new SqliteTransferSchedulingStore(firstDb.ConnectionFactory);
        Assert.True((await firstStore.LoadOperationAsync(first.OperationId))!.RequiresExactShareEvidence);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            firstStore.MarkSucceededAsync(first.OperationId, Now));
        Assert.Equal("ResultPending", await ScalarAsync(firstDb, "TransferOperations", "Status"));
        Assert.Equal("WaitingTransfer", await ScalarAsync(firstDb, "ShareOperations", "Status"));

        await using var secondDb = await BatchTestDatabase.CreateAsync();
        var second = await SeedPendingShareAsync(secondDb, "标题");
        var secondStore = new SqliteTransferSchedulingStore(secondDb.ConnectionFactory);
        var pending = await secondStore.LoadPendingAsync(second.OperationId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            secondStore.MarkConfirmedPresentAsync(pending!, Now));
        Assert.Equal("ResultPending", await ScalarAsync(secondDb, "TransferOperations", "Status"));
        Assert.Equal("WaitingTransfer", await ScalarAsync(secondDb, "ShareOperations", "Status"));
    }

    [Fact]
    public async Task TransferFailurePreservesThePreResolvedTitle()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var data = await SeedPendingShareAsync(db, "预转存解析标题");
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        await store.MarkFailedAsync(
            data.OperationId,
            "TransientService",
            "The service is temporarily unavailable.",
            globallyInvalid: false,
            Now);

        Assert.Equal("Failed", await ScalarAsync(db, "TransferOperations", "Status"));
        Assert.Equal("预转存解析标题", await ScalarAsync(db, "ResourceItems", "ResolvedOutputTitle"));
        Assert.Equal(0, await ScalarIntAsync(db, "(SELECT COUNT(*) AS ShareCount FROM ShareOperations)", "ShareCount"));
    }

    [Theory]
    [InlineData(TransferConfirmation.ConfirmedPresent, "Succeeded", "Fulfilled", 0, 1)]
    [InlineData(TransferConfirmation.ConfirmedAbsent, "Waiting", "Released", 0, 0)]
    [InlineData(TransferConfirmation.Ambiguous, "ResultPending", "Reserved", 1, 0)]
    public async Task SqliteReconciliationChangesOperationAndSlotCountersAtomically(
        TransferConfirmation confirmation,
        string expectedStatus,
        string expectedSlot,
        int expectedReserved,
        int expectedFulfilled)
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var data = await SeedPendingAsync(db);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var adapter = new ConfirmationAdapter
        {
            Result = BaiduAdapterResult<TransferConfirmation>.Success(
                confirmation,
                confirmation == TransferConfirmation.ConfirmedAbsent
                    ? SubmissionAcceptance.DefinitelyNotAccepted
                    : SubmissionAcceptance.Unknown),
            ExactResult = confirmation switch
            {
                TransferConfirmation.ConfirmedPresent => BaiduAdapterResult<TransferConfirmationResult>.Success(
                    TransferConfirmationResult.ExactPresent(new RemoteItemIdentitySet([
                        new RemoteItemIdentity("remote-1", "资源")
                    ]))),
                TransferConfirmation.ConfirmedAbsent => BaiduAdapterResult<TransferConfirmationResult>.Success(
                    TransferConfirmationResult.Absent,
                    SubmissionAcceptance.DefinitelyNotAccepted),
                _ => BaiduAdapterResult<TransferConfirmationResult>.Success(TransferConfirmationResult.Ambiguous),
            },
        };
        var service = new OperationReconciliationService(
            adapter,
            new SessionStore(data.AccountId),
            store,
            new FixedTimeProvider(Now));

        await service.ReconcileAsync(data.OperationId);

        Assert.Equal(expectedStatus, await ScalarAsync(db, "TransferOperations", "Status"));
        Assert.Equal(expectedSlot, await ScalarAsync(db, "TransferOperations", "TargetSlotState"));
        Assert.Equal(expectedReserved, await ScalarIntAsync(db, "AccountAllocations", "ReservedSlotCount"));
        Assert.Equal(expectedFulfilled, await ScalarIntAsync(db, "AccountAllocations", "FulfilledCount"));
    }

    [Fact]
    public async Task AcceptedDuplicateRiskStillCannotBlindRetryUnknownPendingWork()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var data = await SeedPendingAsync(db, dailyLimit: 1);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var service = new OperationReconciliationService(
            new ConfirmationAdapter(),
            new SessionStore(data.AccountId),
            store,
            new FixedTimeProvider(Now));

        var retry = await service.RetryAsync(data.OperationId, duplicateRiskAccepted: true);
        var reserved = await store.TryReserveNextAsync(data.BatchId, Now, Now.AddMinutes(5));

        Assert.True(retry.RequiresConfirmation);
        Assert.False(retry.ResetForRetry);
        Assert.Null(reserved);
        Assert.Equal(1, await ScalarIntAsync(db, "Accounts", "DailyExecutedCount"));
        Assert.Equal("ResultPending", await ScalarAsync(db, "TransferOperations", "Status"));
    }

    [Fact]
    public async Task ManualRetryCannotMoveToAnotherAccountAfterOriginalAccountBecomesInvalid()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var data = await SeedTaskQuotaPendingWithReplacementAsync(db);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var reconciliation = new OperationReconciliationService(
            new ConfirmationAdapter(),
            new SessionStore(data.FailedAccountId),
            store,
            new FixedTimeProvider(Now));

        await reconciliation.RetryAsync(data.OperationId, duplicateRiskAccepted: true);
        await ExecuteAsync(db,
            "UPDATE Accounts SET Status='Invalid' WHERE Id=$id;",
            ("$id", data.FailedAccountId.ToString("D")));

        var service = new ReassignRemainingWorkService(store);
        await Assert.ThrowsAsync<ReassignmentValidationException>(() => service.PreviewAsync(
            new ReassignmentRequest(data.BatchId, data.FailedAccountId,
                new Dictionary<Guid, int>
                {
                    [data.FailedAccountId] = 0,
                    [data.ReplacementAccountId] = 1,
                })));

        var state = await store.LoadAsync(data.BatchId);
        Assert.NotNull(state);
        Assert.False(await store.ApplyAsync(new ReassignmentMutation(
            data.BatchId,
            [new ReassignmentOperationMove(
                data.OperationId, data.FailedAccountId, data.ReplacementAccountId)],
            [],
            [
                new ReassignmentAllocationDelta(data.FailedAccountId, 1, 0, -1),
                new ReassignmentAllocationDelta(data.ReplacementAccountId, 0, 1, 1),
            ]), state!.BatchVersion));
        Assert.Equal(data.FailedAccountId.ToString("D"),
            await ScalarAsync(db, "TransferOperations", "AssignedAccountId"));
        Assert.NotNull(await ScalarNullableAsync(db, "TransferOperations", "DailyCountedAt"));
        Assert.Equal(1, await ScalarIntForIdAsync(db, "Accounts", "DailyExecutedCount", data.FailedAccountId));
    }

    [Fact]
    public async Task CancelReleasesReservedSlotBeforeAtomicallyCancellingPreSubmissionOperation()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var data = await SeedPendingAsync(db, status: "Preparing", submitState: "PreparingToSubmit",
            hasDescriptor: false, dailyCounted: false);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var service = new CancelOperationService(store, new FixedTimeProvider(Now));

        await service.CancelAsync(data.OperationId);

        Assert.Equal("Cancelled", await ScalarAsync(db, "TransferOperations", "Status"));
        Assert.Equal("Released", await ScalarAsync(db, "TransferOperations", "TargetSlotState"));
        Assert.Equal(0, await ScalarIntAsync(db, "AccountAllocations", "ReservedSlotCount"));
        Assert.Equal("Expanded", await ScalarAsync(db, "ResourceItems", "CandidateStatus"));
        Assert.Null(await ScalarNullableAsync(db, "ResourceItems", "ReservedAccountId"));
    }

    [Fact]
    public async Task CancelIsRejectedOnceDescriptorShowsSubmissionMayHaveOccurred()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var data = await SeedPendingAsync(db, status: "Preparing", submitState: "PreparingToSubmit",
            hasDescriptor: true, dailyCounted: false);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var service = new CancelOperationService(store, new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelAsync(data.OperationId));

        Assert.Equal("Preparing", await ScalarAsync(db, "TransferOperations", "Status"));
        Assert.Equal(1, await ScalarIntAsync(db, "AccountAllocations", "ReservedSlotCount"));
    }

    [Fact]
    public async Task CancelRemainsRejectedAfterAnUncertainSubmissionEvenWhenManualRetryIsQueued()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var data = await SeedPendingAsync(db);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        await new OperationReconciliationService(
                new ConfirmationAdapter(),
                new SessionStore(data.AccountId),
                store,
                new FixedTimeProvider(Now))
            .RetryAsync(data.OperationId, duplicateRiskAccepted: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CancelOperationService(store, new FixedTimeProvider(Now)).CancelAsync(data.OperationId));

        Assert.Equal("ResultPending", await ScalarAsync(db, "TransferOperations", "Status"));
        Assert.Equal(1, await ScalarIntAsync(db, "AccountAllocations", "ReservedSlotCount"));
    }

    [Fact]
    public async Task StaleExactConfirmationCannotFinalizeChangedPendingEvidence()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var data = await SeedPendingShareAsync(db, "标题");
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var adapter = new ConfirmationAdapter
        {
            BlockConfirmation = true,
            ExactResult = BaiduAdapterResult<TransferConfirmationResult>.Success(
                TransferConfirmationResult.ExactPresent(new RemoteItemIdentitySet([
                    new RemoteItemIdentity("remote-1", "资源")
                ]))),
        };
        var service = new OperationReconciliationService(
            adapter,
            new SessionStore(data.AccountId),
            store,
            new FixedTimeProvider(Now));
        var reconciliation = service.ReconcileAsync(data.OperationId);
        await adapter.QueryObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await ExecuteAsync(db,
            "UPDATE TransferOperations SET AttemptCount=AttemptCount+1,AdapterRequestId='new-request' WHERE Id=$id;",
            ("$id", data.OperationId.ToString("D")));

        adapter.ReleaseConfirmation.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => reconciliation);
        Assert.Equal("ResultPending", await ScalarAsync(db, "TransferOperations", "Status"));
        Assert.Equal("Reserved", await ScalarAsync(db, "TransferOperations", "TargetSlotState"));
    }

    [Fact]
    public async Task RuntimeLeaseRecoveryBlocksUnsafeRowsThatLackAConfirmationDescriptor()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var data = await SeedPendingAsync(db, status: "Preparing", submitState: "PreparingToSubmit",
            hasDescriptor: false, dailyCounted: false);
        await ExecuteAsync(db,
            "UPDATE TransferOperations SET Status='Submitting',SubmitStartedAt=$started,LeaseExpiresAt=$expired WHERE Id=$id;",
            ("$started", Now.AddMinutes(-10).ToString("O")),
            ("$expired", Now.AddMinutes(-1).ToString("O")),
            ("$id", data.OperationId.ToString("D")));
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.RecoverExpiredLeasesAsync(Now));

        Assert.Equal("Submitting", await ScalarAsync(db, "TransferOperations", "Status"));
        Assert.Null(await ScalarNullableAsync(db, "TransferOperations", "ConfirmationDescriptorJson"));
    }

    [Fact]
    public async Task RuntimeLeaseRecoveryAlsoBlocksAlreadyPendingRowsWithoutADescriptor()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var data = await SeedPendingAsync(db, status: "ResultPending", submitState: "PreparingToSubmit",
            hasDescriptor: false, dailyCounted: false);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.RecoverExpiredLeasesAsync(Now));

        Assert.Equal("ResultPending", await ScalarAsync(db, "TransferOperations", "Status"));
        Assert.Null(await ScalarNullableAsync(db, "TransferOperations", "ConfirmationDescriptorJson"));
    }

    private static PendingReconciliationOperation PendingOperation() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            """{"SourceShareId":"share","TargetPath":"/base","ExpectedOuterNameFingerprint":"outer","OperationFingerprint":"operation","SubmitWindowStartUtc":"2026-08-20T01:50:00+00:00","SubmitWindowEndUtc":"2026-08-20T02:10:00+00:00"}""",
            null,
            null);

    private static async Task<(Guid BatchId, Guid AccountId, Guid OperationId)> SeedPendingAsync(
        BatchTestDatabase db,
        int dailyLimit = 0,
        string status = "ResultPending",
        string submitState = "PendingConfirmation",
        bool hasDescriptor = true,
        bool dailyCounted = true)
    {
        var batchId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var account = AccountRecord.Create(accountId, $"baidu-{accountId:N}", "Account") with
        {
            DailyLimit = dailyLimit,
            DailyExecutedCount = dailyCounted ? 1 : 0,
            DailyCountDateKey = "2026-08-20",
        };
        var job = new BatchJobRecord(batchId, "Batch", "Text", "Distribution", "SuccessTarget",
            "ProportionalReduction", 1, 1, "/base", "OptionalOuter", "Skip", "Immediate", "Active",
            Now.AddHours(-1), Now.AddHours(-1), null, null, 1);
        var resource = new ResourceItemRecord(resourceId, batchId, null, 1,
            "https://pan.baidu.com/s/reconcile", "share", "a1b2", null, null, null, null,
            "OptionalOuter", "Unknown", null, "Reserved", accountId, Now.AddMinutes(5),
            Now.AddHours(-1), Now);
        var operation = new TransferOperationRecord(operationId, resourceId, batchId, 1, 1,
            "/base", "/base", "", accountId, "Weighted", status, null, 1, "operation",
            hasDescriptor ? DescriptorJson("operation") : null, null, null, submitState, "Reserved",
            Now.AddMinutes(-10), dailyCounted ? Now.AddMinutes(-9) : null,
            dailyCounted ? Now.AddMinutes(-9) : null, null, null, Now.AddMinutes(5),
            Now.AddHours(-1), Now);
        var allocation = new AccountAllocationRecord(Guid.NewGuid(), batchId, accountId, 1,
            "/base", "/base", 1, 1, 1, 1, dailyCounted ? 1 : 0, 0, 0, 0, 0, 0, 0, 0, 1, 0);
        await db.SeedAsync([account], job: job, resources: [resource], operations: [operation], allocations: [allocation]);
        return (batchId, accountId, operationId);
    }

    private static async Task<(Guid BatchId, Guid AccountId, Guid OperationId)> SeedPendingShareAsync(
        BatchTestDatabase db,
        string? resolvedOutputTitle)
    {
        var data = await SeedPendingAsync(db);
        await ExecuteAsync(db,
            "UPDATE ResourceItems SET ResolvedOutputTitle=$title; UPDATE BatchJobs SET ShareOperationCount=1 WHERE Id=$batch;",
            ("$title", resolvedOutputTitle),
            ("$batch", data.BatchId.ToString("D")));
        await ExecuteAsync(db, """
            INSERT INTO BatchShareSettings(BatchJobId,IsPermanent,IsAccessUnlimited,ExtractionCodeMode,FixedExtractionCode,ExistingSharePolicy,SettingsFingerprint,CreatedAt)
            VALUES($batch,1,1,'Random',NULL,'Ask','settings-fingerprint',$now);
            INSERT INTO BatchOutputAccounts(BatchJobId,AccountId,OutputOrdinal,OutputLabel,PlannedTransferCount,PlannedShareCount,ShareEnabled)
            VALUES($batch,$account,1,'账号',1,1,1);
            INSERT INTO ShareOperations(Id,TransferOperationId,ResourceItemId,BatchJobId,AccountId,OutputOrdinal,Status,ExactItemSetFingerprint,SettingsFingerprint,Generation,RequestDescriptorJson,AdapterRequestId,SubmissionAcceptance,AttemptCount,LeaseGeneration,LeaseExpiresAt,SubmitStartedAt,NextReconcileAt,LastErrorCode,LastErrorMessage,CreatedAt,UpdatedAt)
            SELECT $share,o.Id,o.ResourceItemId,o.BatchJobId,o.AssignedAccountId,1,'WaitingTransfer',NULL,'settings-fingerprint',1,NULL,NULL,'Unknown',0,0,NULL,NULL,NULL,NULL,NULL,$now,$now
            FROM TransferOperations o WHERE o.Id=$operation;
            """,
            ("$batch", data.BatchId.ToString("D")),
            ("$account", data.AccountId.ToString("D")),
            ("$share", Guid.NewGuid().ToString("D")),
            ("$operation", data.OperationId.ToString("D")),
            ("$now", Now.ToString("O")));
        return data;
    }

    private static async Task<(Guid BatchId, Guid FailedAccountId, Guid ReplacementAccountId, Guid OperationId)>
        SeedTaskQuotaPendingWithReplacementAsync(BatchTestDatabase db)
    {
        var batchId = Guid.NewGuid();
        var failedAccountId = Guid.NewGuid();
        var replacementAccountId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var failedAccount = AccountRecord.Create(failedAccountId, $"baidu-{failedAccountId:N}", "Failed") with
        {
            DailyExecutedCount = 1,
            DailyCountDateKey = "2026-08-20",
        };
        var replacementAccount = AccountRecord.Create(
            replacementAccountId, $"baidu-{replacementAccountId:N}", "Replacement");
        var job = new BatchJobRecord(batchId, "Batch", "Text", "Distribution", "TaskCount",
            "ProportionalReduction", 1, 1, "/base", "OptionalOuter", "Skip", "Immediate", "Active",
            Now.AddHours(-1), Now.AddHours(-1), null, null, 1);
        var resource = new ResourceItemRecord(resourceId, batchId, null, 1,
            "https://pan.baidu.com/s/reassign-retry", "share", "a1b2", null, null, null, null,
            "OptionalOuter", "Unknown", null, "Reserved", failedAccountId, Now.AddMinutes(5),
            Now.AddHours(-1), Now);
        var operation = new TransferOperationRecord(operationId, resourceId, batchId, 1, 1,
            "/base", "/base", "", failedAccountId, "Weighted", "ResultPending", null, 1, "operation",
            DescriptorJson("operation"), null, null, "PendingConfirmation", "Reserved",
            Now.AddMinutes(-10), Now.AddMinutes(-9), Now.AddMinutes(-9), null, null, Now.AddMinutes(5),
            Now.AddHours(-1), Now);
        var failedAllocation = new AccountAllocationRecord(Guid.NewGuid(), batchId, failedAccountId, 1,
            "/base", "/base", 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0);
        var replacementAllocation = new AccountAllocationRecord(Guid.NewGuid(), batchId, replacementAccountId, 2,
            "/base", "/base", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        await db.SeedAsync(
            [failedAccount, replacementAccount],
            job: job,
            resources: [resource],
            operations: [operation],
            allocations: [failedAllocation, replacementAllocation]);
        return (batchId, failedAccountId, replacementAccountId, operationId);
    }

    private static string DescriptorJson(string operationFingerprint) =>
        $$"""{"SourceShareId":"share","TargetPath":"/base","ExpectedOuterNameFingerprint":"outer","OperationFingerprint":"{{operationFingerprint}}","SubmitWindowStartUtc":"2026-08-20T01:50:00+00:00","SubmitWindowEndUtc":"2026-08-20T02:10:00+00:00"}""";

    private static async Task<string> ScalarAsync(BatchTestDatabase db, string table, string column)
    {
        await using var connection = await db.ConnectionFactory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM {table} LIMIT 1;";
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> ScalarIntAsync(BatchTestDatabase db, string table, string column) =>
        Convert.ToInt32(await ScalarAsync(db, table, column));

    private static async Task<int> ScalarIntForIdAsync(
        BatchTestDatabase db,
        string table,
        string column,
        Guid id)
    {
        await using var connection = await db.ConnectionFactory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM {table} WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<string?> ScalarNullableAsync(BatchTestDatabase db, string table, string column)
    {
        await using var connection = await db.ConnectionFactory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM {table} LIMIT 1;";
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private static async Task ExecuteAsync(
        BatchTestDatabase db,
        string sql,
        params (string Name, object? Value)[] values)
    {
        await using var connection = await db.ConnectionFactory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class RecordingReconciliationStore(PendingReconciliationOperation operation)
        : IOperationReconciliationStore
    {
        public bool Present { get; private set; }
        public bool Absent { get; private set; }
        public bool ResetForRetry { get; private set; }
        public Guid? ResetOperationId { get; private set; }
        public RemoteItemIdentitySet? ExactRemoteItems { get; private set; }

        public Task<PendingReconciliationOperation?> LoadPendingAsync(
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<PendingReconciliationOperation?>(operationId == operation.OperationId ? operation : null);

        public Task MarkConfirmedPresentAsync(
            PendingReconciliationOperation expected,
            DateTimeOffset confirmedAt,
            CancellationToken cancellationToken = default)
        {
            Present = true;
            return Task.CompletedTask;
        }

        public Task MarkConfirmedPresentWithEvidenceAsync(
            PendingReconciliationOperation expected,
            RemoteItemIdentitySet exactRemoteItems,
            DateTimeOffset confirmedAt,
            CancellationToken cancellationToken = default)
        {
            Present = true;
            ExactRemoteItems = exactRemoteItems;
            return Task.CompletedTask;
        }

        public Task MarkConfirmedAbsentAsync(
            PendingReconciliationOperation expected,
            SubmissionAcceptance submissionAcceptance,
            DateTimeOffset confirmedAt,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(SubmissionAcceptance.DefinitelyNotAccepted, submissionAcceptance);
            Absent = true;
            return Task.CompletedTask;
        }

        public Task ResetPendingForUserRetryAsync(
            Guid operationId,
            DateTimeOffset resetAt,
            CancellationToken cancellationToken = default)
        {
            ResetForRetry = true;
            ResetOperationId = operationId;
            return Task.CompletedTask;
        }
    }

    private sealed class SessionStore(Guid accountId) : IBaiduSessionStore
    {
        private readonly AccountSession session = new(
            accountId,
            $"baidu-{accountId:N}",
            [new SessionCookie("BDUSS", "secret", ".baidu.com", "/", null, true, true)],
            Now);

        public Task SaveAsync(AccountSession value, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<AccountSession?> LoadAsync(string accountIdValue, CancellationToken cancellationToken) =>
            Task.FromResult<AccountSession?>(accountIdValue == accountId.ToString("D") ? session : null);

        public Task RemoveAsync(string accountIdValue, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ConfirmationAdapter : IBaiduNetdiskAdapter
    {
        public BaiduAdapterResult<TransferConfirmation> Result { get; set; } =
            BaiduAdapterResult<TransferConfirmation>.Success(TransferConfirmation.Ambiguous);
        public BaiduAdapterResult<TransferConfirmationResult>? ExactResult { get; init; }

        public TransferConfirmationQuery? Query { get; private set; }
        public bool BlockConfirmation { get; init; }
        public TaskCompletionSource QueryObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseConfirmation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<BaiduAdapterResult<TransferConfirmation>> ConfirmTransferAsync(
            AccountSession session,
            TransferConfirmationQuery query,
            CancellationToken cancellationToken)
        {
            Query = query;
            QueryObserved.TrySetResult();
            if (BlockConfirmation) await ReleaseConfirmation.Task.WaitAsync(cancellationToken);
            return Result;
        }

        public async Task<BaiduAdapterResult<TransferConfirmationResult>> ConfirmExactTransferAsync(
            AccountSession session,
            TransferConfirmationQuery query,
            CancellationToken cancellationToken)
        {
            Query = query;
            QueryObserved.TrySetResult();
            if (BlockConfirmation) await ReleaseConfirmation.Task.WaitAsync(cancellationToken);
            if (ExactResult is not null) return ExactResult;
            if (!Result.IsSuccess)
                return BaiduAdapterResult<TransferConfirmationResult>.Failure(
                    Result.ErrorKind!.Value,
                    null,
                    Result.RetryAfter,
                    Result.SubmissionAcceptance);
            return BaiduAdapterResult<TransferConfirmationResult>.Success(Result.Value.ToExactResult());
        }

        public Task<BaiduAdapterResult<BaiduAccountIdentity>> ValidateSessionAsync(AccountSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<AccountCapacity>> GetCapacityAsync(AccountSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<ImmutableArray<RemoteDirectory>>> ListDirectoriesAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<ShareMetadata>> InspectShareAsync(AccountSession session, ShareReference share, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareAsync(AccountSession session, ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class LegacyOnlyConfirmationAdapter(BaiduAdapterResult<TransferConfirmation> result)
        : IBaiduNetdiskAdapter
    {
        public Task<BaiduAdapterResult<TransferConfirmation>> ConfirmTransferAsync(
            AccountSession session,
            TransferConfirmationQuery query,
            CancellationToken cancellationToken) => Task.FromResult(result);

        public Task<BaiduAdapterResult<BaiduAccountIdentity>> ValidateSessionAsync(AccountSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<AccountCapacity>> GetCapacityAsync(AccountSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<ImmutableArray<RemoteDirectory>>> ListDirectoriesAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<ShareMetadata>> InspectShareAsync(AccountSession session, ShareReference share, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareAsync(AccountSession session, ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
