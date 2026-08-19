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
    public async Task PersistedDescriptorAloneCanDriveAllThreeConfirmationOutcomes(
        TransferConfirmation confirmation)
    {
        var operation = PendingOperation();
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

        Assert.Equal(confirmation, outcome);
        Assert.NotNull(adapter.Query);
        Assert.Null(adapter.Query!.AdapterRequestId);
        Assert.Empty(adapter.Query.RemoteItemIds);
        Assert.Equal(confirmation == TransferConfirmation.ConfirmedPresent, store.Present);
        Assert.Equal(confirmation == TransferConfirmation.ConfirmedAbsent, store.Absent);
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
    public async Task UserRetryRequiresDuplicateWarningAndReusesTheSameOperation()
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

        Assert.False(accepted.RequiresConfirmation);
        Assert.True(accepted.ResetForRetry);
        Assert.Equal(operation.OperationId, store.ResetOperationId);
    }

    [Theory]
    [InlineData(TransferConfirmation.ConfirmedPresent, "Succeeded", "Fulfilled", 0, 1)]
    [InlineData(TransferConfirmation.ConfirmedAbsent, "Failed", "Released", 0, 0)]
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
            Result = BaiduAdapterResult<TransferConfirmation>.Success(confirmation),
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
    public async Task AcceptedPendingRetryKeepsDailyCountAndCanCrossBoundaryWithoutCountingAgain()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var data = await SeedPendingAsync(db, dailyLimit: 1);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var service = new OperationReconciliationService(
            new ConfirmationAdapter(),
            new SessionStore(data.AccountId),
            store,
            new FixedTimeProvider(Now));

        await service.RetryAsync(data.OperationId, duplicateRiskAccepted: true);
        var reserved = await store.TryReserveNextAsync(data.BatchId, Now, Now.AddMinutes(5));
        await store.MarkPreparingAsync(data.OperationId, Now.AddSeconds(1));
        var boundary = await new AccountDailyLimitService(store, new FixedTimeProvider(Now.AddSeconds(2)))
            .PersistFirstSubmissionAsync(new SubmissionBoundaryCommand(
                data.OperationId,
                data.AccountId,
                DescriptorJson("retry-operation"),
                Now.AddMinutes(5)));

        Assert.Equal(data.OperationId, reserved?.OperationId);
        Assert.True(boundary.Accepted);
        Assert.False(boundary.WasNewlyCounted);
        Assert.Equal(1, await ScalarIntAsync(db, "Accounts", "DailyExecutedCount"));
        Assert.Equal("Submitting", await ScalarAsync(db, "TransferOperations", "Status"));
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

        Assert.Equal("Waiting", await ScalarAsync(db, "TransferOperations", "Status"));
        Assert.Equal(0, await ScalarIntAsync(db, "AccountAllocations", "ReservedSlotCount"));
    }

    [Fact]
    public async Task StaleConfirmationCannotFinalizeANewerManualRetryAttempt()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var data = await SeedPendingAsync(db);
        var store = new SqliteTransferSchedulingStore(db.ConnectionFactory);
        var adapter = new ConfirmationAdapter
        {
            BlockConfirmation = true,
            Result = BaiduAdapterResult<TransferConfirmation>.Success(TransferConfirmation.ConfirmedPresent),
        };
        var service = new OperationReconciliationService(
            adapter,
            new SessionStore(data.AccountId),
            store,
            new FixedTimeProvider(Now));
        var reconciliation = service.ReconcileAsync(data.OperationId);
        await adapter.QueryObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await service.RetryAsync(data.OperationId, duplicateRiskAccepted: true);
        Assert.Equal(data.OperationId,
            (await store.TryReserveNextAsync(data.BatchId, Now, Now.AddMinutes(5)))?.OperationId);
        await store.MarkPreparingAsync(data.OperationId, Now.AddSeconds(1));
        await new AccountDailyLimitService(store, new FixedTimeProvider(Now.AddSeconds(2)))
            .PersistFirstSubmissionAsync(new SubmissionBoundaryCommand(
                data.OperationId,
                data.AccountId,
                DescriptorJson("operation"),
                Now.AddMinutes(5)));
        await store.MarkResultPendingAsync(
            data.OperationId,
            "UnknownResponse",
            "The service returned an unrecognized response.",
            SubmissionAcceptance.Unknown,
            Now.AddSeconds(3));

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

        public Task MarkConfirmedAbsentAsync(
            PendingReconciliationOperation expected,
            DateTimeOffset confirmedAt,
            CancellationToken cancellationToken = default)
        {
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
