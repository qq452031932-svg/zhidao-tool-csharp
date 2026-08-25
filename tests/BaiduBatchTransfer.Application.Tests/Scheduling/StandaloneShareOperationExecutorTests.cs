using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;

namespace BaiduBatchTransfer.Application.Tests.Scheduling;

public sealed class StandaloneShareOperationExecutorTests
{
    [Fact]
    public async Task BoundAdapterPersistsFullBaselineBeforeSubmittingAndAcceptsExactResult()
    {
        await using var fixture = await Fixture.CreateAsync(AdapterBehavior.Success);

        var result = await fixture.Executor.ExecuteAsync(fixture.Lease);

        Assert.Equal(StandaloneShareExecutionDisposition.Succeeded, result.Disposition);
        var operation = Assert.Single((await fixture.Store.GetBatchSnapshotAsync(fixture.BatchId))!.Operations);
        Assert.Equal(StandaloneShareOperationStatus.Succeeded, operation.Status);
        Assert.NotNull(operation.BaselineFingerprint);
        Assert.Equal(["list", "prepare", "submit"], fixture.Adapter.Calls);
        Assert.NotNull(await fixture.Store.GetResultAsync(operation.Id, operation.Generation));
    }

    [Fact]
    public async Task WindowCloseAfterSubmissionRemainsResultPendingAndNeverBlindRetries()
    {
        await using var fixture = await Fixture.CreateAsync(AdapterBehavior.CloseAfterSubmit);

        var result = await fixture.Executor.ExecuteAsync(fixture.Lease);

        Assert.Equal(StandaloneShareExecutionDisposition.ResultPending, result.Disposition);
        var operation = Assert.Single((await fixture.Store.GetBatchSnapshotAsync(fixture.BatchId))!.Operations);
        Assert.Equal(StandaloneShareOperationStatus.ResultPending, operation.Status);
        Assert.Equal(StandaloneShareSubmissionState.ResultPending, operation.SubmissionState);
        Assert.Null(operation.LeaseOwnerId);
        Assert.Null(operation.LeaseExpiresAt);
        Assert.NotNull(await fixture.Store.TryClaimReconciliationAsync(
            fixture.BatchId, Guid.NewGuid(), fixture.Time.GetUtcNow(), fixture.Time.GetUtcNow().AddMinutes(30)));
        Assert.Equal(1, fixture.Adapter.Calls.Count(call => call == "submit"));
    }

    [Fact]
    public async Task RestartClaimsPendingOperationAndExactConfirmationCompletesWithoutResubmission()
    {
        await using var fixture = await Fixture.CreateAsync(AdapterBehavior.CloseAfterSubmit);
        await fixture.Executor.ExecuteAsync(fixture.Lease);
        var reconciliationLease = await fixture.Store.TryClaimReconciliationAsync(
            fixture.BatchId, Guid.NewGuid(), fixture.Time.GetUtcNow(), fixture.Time.GetUtcNow().AddMinutes(30));

        var result = await fixture.Executor.ReconcileAsync(reconciliationLease!);

        Assert.Equal(StandaloneShareExecutionDisposition.Succeeded, result.Disposition);
        Assert.Equal(1, fixture.Adapter.Calls.Count(call => call == "submit"));
        Assert.Equal(1, fixture.Adapter.Calls.Count(call => call == "confirm"));
    }

    [Theory]
    [InlineData(AdapterBehavior.PrepareUnknown)]
    [InlineData(AdapterBehavior.PrepareThrows)]
    public async Task PrepareFailureBeforeBusinessBoundaryReleasesLeaseAsDefinitelyNotAccepted(AdapterBehavior behavior)
    {
        await using var fixture = await Fixture.CreateAsync(behavior);

        var result = await fixture.Executor.ExecuteAsync(fixture.Lease);

        Assert.Equal(StandaloneShareExecutionDisposition.DefinitelyNotAccepted, result.Disposition);
        var operation = Assert.Single((await fixture.Store.GetBatchSnapshotAsync(fixture.BatchId))!.Operations);
        Assert.Equal(StandaloneShareOperationStatus.Failed, operation.Status);
        Assert.Equal(StandaloneShareSubmissionState.DefinitelyNotAccepted, operation.SubmissionState);
        Assert.Null(operation.LeaseOwnerId);
        Assert.DoesNotContain("submit", fixture.Adapter.Calls);
    }

    [Fact]
    public async Task MissingSessionBeforeBusinessBoundaryReleasesLeaseAsDefinitelyNotAccepted()
    {
        await using var fixture = await Fixture.CreateAsync(AdapterBehavior.Success, hasSession: false);

        var result = await fixture.Executor.ExecuteAsync(fixture.Lease);

        Assert.Equal(StandaloneShareExecutionDisposition.DefinitelyNotAccepted, result.Disposition);
        var operation = Assert.Single((await fixture.Store.GetBatchSnapshotAsync(fixture.BatchId))!.Operations);
        Assert.Equal(StandaloneShareOperationStatus.Failed, operation.Status);
        Assert.Null(operation.LeaseOwnerId);
        Assert.Empty(fixture.Adapter.Calls);
    }

    [Fact]
    public async Task ReconciliationWithAuthoritativeDefinitelyNotAcceptedBecomesSafelyRetryable()
    {
        await using var fixture = await Fixture.CreateAsync(AdapterBehavior.ConfirmDefinitelyNotAccepted);
        Assert.Equal(StandaloneShareExecutionDisposition.ResultPending,
            (await fixture.Executor.ExecuteAsync(fixture.Lease)).Disposition);
        var reconciliationLease = await fixture.Store.TryClaimReconciliationAsync(
            fixture.BatchId, Guid.NewGuid(), fixture.Time.GetUtcNow(), fixture.Time.GetUtcNow().AddMinutes(30));

        var result = await fixture.Executor.ReconcileAsync(reconciliationLease!);

        Assert.Equal(StandaloneShareExecutionDisposition.DefinitelyNotAccepted, result.Disposition);
        var operation = Assert.Single((await fixture.Store.GetBatchSnapshotAsync(fixture.BatchId))!.Operations);
        Assert.True(operation.CanSafelyRetry);
        Assert.Equal(StandaloneShareSubmissionState.DefinitelyNotAccepted, operation.SubmissionState);
        Assert.Null(operation.LeaseOwnerId);
    }

    [Fact]
    public async Task ExplicitOfficialRejectionDuringSubmitBecomesSafelyRetryable()
    {
        await using var fixture = await Fixture.CreateAsync(AdapterBehavior.SubmitDefinitelyNotAccepted);

        var result = await fixture.Executor.ExecuteAsync(fixture.Lease);

        Assert.Equal(StandaloneShareExecutionDisposition.DefinitelyNotAccepted, result.Disposition);
        var operation = Assert.Single((await fixture.Store.GetBatchSnapshotAsync(fixture.BatchId))!.Operations);
        Assert.True(operation.CanSafelyRetry);
        Assert.Equal(StandaloneShareSubmissionState.DefinitelyNotAccepted, operation.SubmissionState);
        Assert.Null(operation.LeaseOwnerId);
    }

    [Fact]
    public async Task TransientDefinitelyNotAcceptedUsesSharedBoundedRetryThenSucceeds()
    {
        await using var fixture = await Fixture.CreateAsync(AdapterBehavior.TransientThenSuccess);

        var result = await fixture.Executor.ExecuteAsync(fixture.Lease);

        Assert.Equal(StandaloneShareExecutionDisposition.Succeeded, result.Disposition);
        Assert.Equal(2, fixture.Adapter.Calls.Count(call => call == "submit"));
        Assert.Equal(1, Assert.Single((await fixture.Store.GetBatchSnapshotAsync(fixture.BatchId))!.Operations).AttemptCount);
    }

    [Fact]
    public async Task TransientDiscoveryUsesTheSameSharedBoundedRetryBeforeSubmission()
    {
        await using var fixture = await Fixture.CreateAsync(AdapterBehavior.DiscoveryTransientThenSuccess);

        var result = await fixture.Executor.ExecuteAsync(fixture.Lease);

        Assert.Equal(StandaloneShareExecutionDisposition.Succeeded, result.Disposition);
        Assert.Equal(2, fixture.Adapter.Calls.Count(call => call == "list"));
        Assert.Equal(1, fixture.Adapter.Calls.Count(call => call == "submit"));
    }

    [Fact]
    public async Task TransientDefinitelyNotAcceptedExhaustsSharedBoundedRetryBeforeSafeFailure()
    {
        await using var fixture = await Fixture.CreateAsync(AdapterBehavior.AlwaysTransient);

        var result = await fixture.Executor.ExecuteAsync(fixture.Lease);

        Assert.Equal(StandaloneShareExecutionDisposition.DefinitelyNotAccepted, result.Disposition);
        Assert.Equal(2, fixture.Adapter.Calls.Count(call => call == "submit"));
        Assert.Equal(2, Assert.Single((await fixture.Store.GetBatchSnapshotAsync(fixture.BatchId))!.Operations).AttemptCount);
    }

    [Fact]
    public async Task ReuseDecisionAcceptsOneExactCompatibleExistingShareWithoutSubmitting()
    {
        await using var fixture = await Fixture.CreateAsync(AdapterBehavior.ExistingExact);

        var result = await fixture.Executor.ExecuteAsync(fixture.Lease);

        Assert.Equal(StandaloneShareExecutionDisposition.Succeeded, result.Disposition);
        Assert.Equal(["list"], fixture.Adapter.Calls);
        Assert.NotNull(await fixture.Store.GetResultAsync(fixture.Lease.OperationId, fixture.Lease.Generation));
    }

    [Fact]
    public async Task CancelExistingPolicyTerminatesSafelyBeforeAnySubmission()
    {
        await using var fixture = await Fixture.CreateAsync(
            AdapterBehavior.Success, existingDecision: ExistingShareDecision.Cancel);

        var result = await fixture.Executor.ExecuteAsync(fixture.Lease);

        Assert.Equal(StandaloneShareExecutionDisposition.DefinitelyNotAccepted, result.Disposition);
        Assert.Equal(["list"], fixture.Adapter.Calls);
    }

    public enum AdapterBehavior
    {
        Success,
        CloseAfterSubmit,
        PrepareUnknown,
        PrepareThrows,
        ConfirmDefinitelyNotAccepted,
        SubmitDefinitelyNotAccepted,
        TransientThenSuccess,
        AlwaysTransient,
        DiscoveryTransientThenSuccess,
        ExistingExact,
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root;
        private Fixture(string root, SqliteStandaloneShareStore store, Guid batchId,
            StandaloneShareOperationLease lease, RecordingAdapter adapter, StandaloneShareOperationExecutor executor,
            MutableTimeProvider time)
        {
            this.root = root; Store = store; BatchId = batchId; Lease = lease; Adapter = adapter; Executor = executor; Time = time;
        }
        public SqliteStandaloneShareStore Store { get; }
        public Guid BatchId { get; }
        public StandaloneShareOperationLease Lease { get; }
        public RecordingAdapter Adapter { get; }
        public StandaloneShareOperationExecutor Executor { get; }
        public MutableTimeProvider Time { get; }

        public static async Task<Fixture> CreateAsync(
            AdapterBehavior behavior,
            bool hasSession = true,
            ExistingShareDecision existingDecision = ExistingShareDecision.Reuse)
        {
            var root = Path.Combine(Path.GetTempPath(), $"bbt-standalone-executor-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var factory = new SqliteConnectionFactory(Path.Combine(root, "app.db"));
            await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
            var accountId = Guid.NewGuid();
            await using (var connection = await factory.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO Accounts(Id,BaiduUserId,DisplayName,LoginMethod,Status,DailyLimit,DailyExecutedCount,
                      SortOrder,IsEnabled,DailyCountDateKey,IsDeleted)
                    VALUES($id,'user','account','WebView2','Active',0,0,0,1,'2026-08-25',0);
                    """;
                command.Parameters.AddWithValue("$id", accountId.ToString("D"));
                await command.ExecuteNonQueryAsync();
            }
            var now = new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);
            var batchId = Guid.NewGuid();
            var selection = new OwnedRemoteSelection(accountId,
                new RemoteItemIdentitySet([new RemoteItemIdentity("remote-target", "target")]),
                "target", RemoteResourceKind.File, new RemotePath("/apps"));
            var batch = StandaloneShareBatchRecord.Create(batchId, Path.Combine(root, "results"), 1, now);
            var operation = StandaloneShareOperationRecord.Create(
                Guid.NewGuid(), batchId, selection, 1, now,
                existingShareDecision: existingDecision);
            var store = new SqliteStandaloneShareStore(factory);
            await store.CreateOrGetBatchAsync(StandaloneSharePendingBatchEnvelope.Create(batch, [operation]));
            var lease = (await store.TryLeaseNextAsync(batchId, Guid.NewGuid(), now.AddSeconds(1), now.AddMinutes(30)))!;
            var session = new AccountSession(accountId, "user",
                [new SessionCookie("BDUSS", "secret", ".baidu.com", "/", null, true, true)], now);
            var adapter = new RecordingAdapter(behavior);
            var time = new MutableTimeProvider(now.AddSeconds(2));
            var executor = new StandaloneShareOperationExecutor(
                adapter, new SessionStore(hasSession ? session : null), store, new ShareSubmissionEngine(),
                time, new RetryPolicy([TimeSpan.Zero]), new ImmediateDelay());
            return new Fixture(root, store, batchId, lease, adapter, executor, time);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingAdapter(AdapterBehavior behavior) : IOperationBoundBaiduShareAdapter
    {
        private int submissionCount;
        private int discoveryCount;
        public List<string> Calls { get; } = [];
        public Task<BaiduAdapterResult<BaiduSharePage>> ListSharesAsync(AccountSession session, string? cursor, CancellationToken cancellationToken)
        {
            Calls.Add("list");
            discoveryCount++;
            if (behavior == AdapterBehavior.DiscoveryTransientThenSuccess && discoveryCount == 1)
                return Task.FromResult(BaiduAdapterResult<BaiduSharePage>.Failure(
                    BaiduErrorKind.TransientService, adapterDiagnostic: null,
                    submissionAcceptance: SubmissionAcceptance.DefinitelyNotAccepted));
            var shares = behavior == AdapterBehavior.ExistingExact
                ? new[] { new BaiduShareRecord(
                    "share-existing", new([new("remote-target", "target")]),
                    "https://pan.baidu.com/s/share-existing", "A1b2", true, true) }
                : [];
            return Task.FromResult(BaiduAdapterResult<BaiduSharePage>.Success(new(shares, null)));
        }
        public Task<BaiduAdapterResult<ShareCreationReceipt>> CreatePermanentShareAsync(AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<SharePreparationReceipt>> PreparePermanentShareForOperationAsync(ShareOperationAutomationContext context, AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("prepare");
            if (behavior == AdapterBehavior.PrepareThrows) throw new InvalidOperationException("prepare failed");
            return Task.FromResult(behavior == AdapterBehavior.PrepareUnknown
                ? BaiduAdapterResult<SharePreparationReceipt>.Failure(
                    BaiduErrorKind.UnknownResponse, adapterDiagnostic: null, submissionAcceptance: SubmissionAcceptance.Unknown)
                : BaiduAdapterResult<SharePreparationReceipt>.Success(
                    SharePreparationReceipt.Ready, SubmissionAcceptance.DefinitelyNotAccepted));
        }
        public Task<BaiduAdapterResult<ShareCreationReceipt>> SubmitPermanentShareForOperationAsync(ShareOperationAutomationContext context, AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("submit");
            submissionCount++;
            if (behavior == AdapterBehavior.AlwaysTransient ||
                behavior == AdapterBehavior.TransientThenSuccess && submissionCount == 1)
                return Task.FromResult(BaiduAdapterResult<ShareCreationReceipt>.Failure(
                    BaiduErrorKind.TransientService, adapterDiagnostic: null,
                    submissionAcceptance: SubmissionAcceptance.DefinitelyNotAccepted));
            if (behavior == AdapterBehavior.SubmitDefinitelyNotAccepted)
                return Task.FromResult(BaiduAdapterResult<ShareCreationReceipt>.Failure(
                    BaiduErrorKind.UnknownResponse, adapterDiagnostic: null,
                    submissionAcceptance: SubmissionAcceptance.DefinitelyNotAccepted));
            if (behavior is AdapterBehavior.CloseAfterSubmit or AdapterBehavior.ConfirmDefinitelyNotAccepted)
                throw new InvalidOperationException("window closed");
            return Task.FromResult(BaiduAdapterResult<ShareCreationReceipt>.Success(new(
                DateTimeOffset.UtcNow, "adapter", request, "share-new", "https://pan.baidu.com/s/share-new",
                new EffectiveShareSettings(true, true, ShareExtractionCodeBehavior.Random, "A1b2"))));
        }
        public Task<BaiduAdapterResult<ShareCreationReceipt>> ConfirmExactShareForOperationAsync(ShareOperationAutomationContext context, AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("confirm");
            if (behavior == AdapterBehavior.ConfirmDefinitelyNotAccepted)
                return Task.FromResult(BaiduAdapterResult<ShareCreationReceipt>.Failure(
                    BaiduErrorKind.UnknownResponse, adapterDiagnostic: null,
                    submissionAcceptance: SubmissionAcceptance.DefinitelyNotAccepted));
            return Task.FromResult(BaiduAdapterResult<ShareCreationReceipt>.Success(new(
                DateTimeOffset.UtcNow, "adapter", request, "share-new", "https://pan.baidu.com/s/share-new",
                new EffectiveShareSettings(true, true, ShareExtractionCodeBehavior.Random, "A1b2"))));
        }
    }

    private sealed class ImmediateDelay : IAsyncDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SessionStore(AccountSession? session) : IBaiduSessionStore
    {
        public Task<AccountSession?> LoadAsync(string accountId, CancellationToken cancellationToken) => Task.FromResult<AccountSession?>(session);
        public Task SaveAsync(AccountSession value, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveAsync(string accountId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now = now.Add(value);
    }
}
