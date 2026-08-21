using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Tests.Scheduling;

public sealed class ShareOperationExecutorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 4, 0, 0, TimeSpan.Zero);
    private static readonly Guid AccountId = Guid.Parse("10000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task RandomAndFixedModesAlwaysRequestPermanentUnlimitedShares()
    {
        var random = Harness(RandomSettings());
        await random.Executor.ExecuteAsync(random.Operation.Id);
        var fixedCode = Harness(FixedSettings("a1B2"));
        await fixedCode.Executor.ExecuteAsync(fixedCode.Operation.Id);

        Assert.True(Assert.Single(random.Adapter.CreateRequests).IsPermanent);
        Assert.True(Assert.Single(random.Adapter.CreateRequests).IsAccessUnlimited);
        Assert.True(Assert.Single(random.Adapter.CreateRequests).UsesRandomExtractionCode);
        var fixedRequest = Assert.Single(fixedCode.Adapter.CreateRequests);
        Assert.Equal("a1B2", fixedRequest.FixedExtractionCode);
        Assert.DoesNotContain("a1B2", fixedRequest.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("a1B2", fixedCode.Store.LastDescriptor ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("abcde")]
    [InlineData("a-b1")]
    [InlineData("中文ab")]
    public async Task InvalidFixedCodeIsRejectedBeforeExternalCalls(string code)
    {
        var harness = Harness(FixedSettings(code));

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Executor.ExecuteAsync(harness.Operation.Id));

        Assert.Empty(harness.Adapter.ListRequests);
        Assert.Empty(harness.Adapter.CreateRequests);
    }

    [Fact]
    public async Task CompatibleExistingShareIsReusedWithoutCreate()
    {
        var existing = Existing("existing", "https://pan.baidu.com/s/existing", "r4Nd", permanent: true, unlimited: true);
        var harness = Harness(RandomSettings(), ExistingShareDecision.Reuse, [existing]);

        var outcome = await harness.Executor.ExecuteAsync(harness.Operation.Id);

        Assert.Equal(ShareExecutionDisposition.ReusedExisting, outcome.Disposition);
        Assert.Empty(harness.Adapter.CreateRequests);
        Assert.Equal(existing.ShareUrl, harness.Store.AcceptedResult?.ShareUrl);
        Assert.Equal(existing.ExtractionCode, harness.Store.AcceptedResult?.ExtractionCode);
    }

    [Fact]
    public async Task RegenerateCreatesNewShareAndNeverCancelsExistingShare()
    {
        var harness = Harness(RandomSettings(), ExistingShareDecision.Regenerate,
            [Existing("old", "https://pan.baidu.com/s/old", "o1d2", true, true)]);

        var outcome = await harness.Executor.ExecuteAsync(harness.Operation.Id);

        Assert.Equal(ShareExecutionDisposition.Succeeded, outcome.Disposition);
        Assert.Single(harness.Adapter.CreateRequests);
        Assert.Equal("new-share", harness.Store.AcceptedResult?.ShareId);
    }

    [Fact]
    public async Task IncompatibleExistingShareCannotSilentlyReuse()
    {
        var harness = Harness(RandomSettings(), ExistingShareDecision.Reuse,
            [Existing("old", "https://pan.baidu.com/s/old", "o1d2", permanent: false, unlimited: true)]);

        var outcome = await harness.Executor.ExecuteAsync(harness.Operation.Id);

        Assert.Equal(ShareExecutionDisposition.Paused, outcome.Disposition);
        Assert.Equal(SharePauseReason.IncompatibleExistingShare, harness.Store.PauseReason);
        Assert.Empty(harness.Adapter.CreateRequests);
    }

    [Fact]
    public async Task CancelDecisionPausesWithoutCreating()
    {
        var harness = Harness(RandomSettings(), ExistingShareDecision.Cancel,
            [Existing("old", "https://pan.baidu.com/s/old", "o1d2", true, true)]);

        var outcome = await harness.Executor.ExecuteAsync(harness.Operation.Id);

        Assert.Equal(ShareExecutionDisposition.Paused, outcome.Disposition);
        Assert.Equal(SharePauseReason.CancelledByUser, harness.Store.PauseReason);
        Assert.Empty(harness.Adapter.CreateRequests);
    }

    [Fact]
    public async Task DefinitelyNotAcceptedTransientFailureRetriesThreeTimesAndHonorsServerWait()
    {
        var failure = BaiduAdapterResult<ShareCreationReceipt>.Failure(
            BaiduErrorKind.TransientService, "private", TimeSpan.FromSeconds(23),
            SubmissionAcceptance.DefinitelyNotAccepted);
        var harness = Harness(RandomSettings());
        harness.Adapter.CreateResults = new([failure, failure, failure, harness.Adapter.SuccessReceipt()]);

        var outcome = await harness.Executor.ExecuteAsync(harness.Operation.Id);

        Assert.Equal(ShareExecutionDisposition.Succeeded, outcome.Disposition);
        Assert.Equal(4, harness.Adapter.CreateRequests.Count);
        Assert.Equal(3, harness.Store.DefiniteRetryCount);
        Assert.Equal([TimeSpan.FromSeconds(23), TimeSpan.FromSeconds(23), TimeSpan.FromSeconds(23)], harness.Delay.Delays);
    }

    [Fact]
    public async Task UnknownAcceptanceBecomesPendingAfterFirstAttemptWithoutBlindRetry()
    {
        var harness = Harness(RandomSettings());
        harness.Adapter.CreateResults = new([
            BaiduAdapterResult<ShareCreationReceipt>.Failure(
                BaiduErrorKind.UnknownResponse, "private response", submissionAcceptance: SubmissionAcceptance.Unknown)]);

        var outcome = await harness.Executor.ExecuteAsync(harness.Operation.Id);

        Assert.Equal(ShareExecutionDisposition.ResultPending, outcome.Disposition);
        Assert.Single(harness.Adapter.CreateRequests);
        Assert.True(harness.Store.ResultPending);
    }

    [Fact]
    public async Task ShareOptOutHasStableStatusAndZeroExternalCalls()
    {
        var harness = Harness(RandomSettings(), shareEnabled: false);

        var outcome = await harness.Executor.ExecuteAsync(harness.Operation.Id);

        Assert.Equal(ShareExecutionDisposition.NotEnabled, outcome.Disposition);
        Assert.Equal("未启用分享", outcome.StableStatus);
        Assert.Empty(harness.Adapter.ListRequests);
        Assert.Empty(harness.Adapter.CreateRequests);
    }

    [Fact]
    public void PublicModelsRedactUrlsCodesIdsAndTokens()
    {
        var summary = new ExistingShareBatchSummary(
            Guid.Parse("20000000-0000-0000-0000-000000000002"), "private-token",
            [new ExistingShareAccountSummary(AccountId, 3, 2, 1)]);
        var command = new ConfirmExistingShareDecisionCommand(
            summary.BatchJobId, summary.ConfirmationToken, ExistingShareDecision.Reuse,
            new Dictionary<Guid, ExistingShareDecision> { [AccountId] = ExistingShareDecision.Regenerate });

        Assert.DoesNotContain("private-token", summary.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-token", command.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(AccountId.ToString("D"), summary.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DecisionDiscoveryAggregatesOncePerSelectedShareEnabledAccountAndPersistsPauseSnapshot()
    {
        var secondAccount = Guid.Parse("10000000-0000-0000-0000-000000000002");
        var batch = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var settings = RandomSettings() with { BatchJobId = batch };
        var firstSet = ExactItems();
        var secondSet = new RemoteItemIdentitySet([new("remote-2", "Second")]);
        var store = new RecordingDecisionStore([
            new(batch, AccountId, settings),
            new(batch, secondAccount, settings)], new Dictionary<Guid, IReadOnlyList<RemoteItemIdentitySet>>
            {
                [AccountId] = [firstSet, secondSet], [secondAccount] = [firstSet],
            });
        var adapter = new AccountShareAdapter(new Dictionary<Guid, IReadOnlyList<BaiduShareRecord>>
        {
            [AccountId] = [
                Existing("one", "https://pan.baidu.com/s/one", "a1b2", true, true),
                new("two", secondSet, "https://pan.baidu.com/s/two", "c3d4", false, true)],
            [secondAccount] = [],
        });
        var service = new ExistingShareDecisionService(
            adapter, new MultiSessionStore(AccountId, secondAccount), store, new FrozenTimeProvider(Now));

        var summary = await service.DiscoverAsync(batch);

        Assert.Equal(2, summary.Accounts.Count);
        Assert.Equal(new ExistingShareAccountSummary(AccountId, 2, 1, 1), summary.Accounts[0]);
        Assert.Equal(new ExistingShareAccountSummary(secondAccount, 0, 0, 0), summary.Accounts[1]);
        Assert.Equal(2, adapter.ListCalls);
        Assert.Equal(2, store.PersistedSnapshots.Count);
    }

    [Fact]
    public async Task BatchDefaultAndPerAccountOverrideAreDurableAndRestartSafe()
    {
        var secondAccount = Guid.Parse("10000000-0000-0000-0000-000000000002");
        var batch = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var settings = RandomSettings() with { BatchJobId = batch };
        var store = new RecordingDecisionStore([
            new(batch, AccountId, settings),
            new(batch, secondAccount, settings)], new Dictionary<Guid, IReadOnlyList<RemoteItemIdentitySet>>
            {
                [AccountId] = [ExactItems()], [secondAccount] = [ExactItems()],
            });
        var adapter = new AccountShareAdapter(new Dictionary<Guid, IReadOnlyList<BaiduShareRecord>>
        {
            [AccountId] = [Existing("one", "https://pan.baidu.com/s/one", "a1b2", true, true)],
            [secondAccount] = [Existing("two", "https://pan.baidu.com/s/two", "c3d4", true, true)],
        });
        var sessions = new MultiSessionStore(AccountId, secondAccount);
        var summary = await new ExistingShareDecisionService(adapter, sessions, store, new FrozenTimeProvider(Now))
            .DiscoverAsync(batch);

        var restarted = new ExistingShareDecisionService(adapter, sessions, store, new FrozenTimeProvider(Now));
        var result = await restarted.ConfirmAsync(new(
            batch, summary.ConfirmationToken, ExistingShareDecision.Reuse,
            new Dictionary<Guid, ExistingShareDecision> { [secondAccount] = ExistingShareDecision.Regenerate }));

        Assert.True(result.Accepted);
        Assert.Equal(ExistingShareDecision.Reuse.ToString(), store.Decisions.Single(x => x.AccountId == AccountId).Decision);
        Assert.Equal(ExistingShareDecision.Regenerate.ToString(), store.Decisions.Single(x => x.AccountId == secondAccount).Decision);
    }

    [Fact]
    public async Task ChangedDiscoveryMakesConfirmationTokenStaleWithZeroDecisionWrites()
    {
        var batch = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var settings = RandomSettings() with { BatchJobId = batch };
        var store = new RecordingDecisionStore([new(batch, AccountId, settings)],
            new Dictionary<Guid, IReadOnlyList<RemoteItemIdentitySet>> { [AccountId] = [ExactItems()] });
        var adapter = new AccountShareAdapter(new Dictionary<Guid, IReadOnlyList<BaiduShareRecord>>
        {
            [AccountId] = [Existing("one", "https://pan.baidu.com/s/one", "a1b2", true, true)],
        });
        var service = new ExistingShareDecisionService(adapter, new MultiSessionStore(AccountId), store, new FrozenTimeProvider(Now));
        var summary = await service.DiscoverAsync(batch);
        store.ChangeDiscovery(AccountId);

        var result = await service.ConfirmAsync(new(
            batch, summary.ConfirmationToken, ExistingShareDecision.Reuse,
            new Dictionary<Guid, ExistingShareDecision>()));

        Assert.True(result.IsStale);
        Assert.Empty(store.Decisions);
    }

    [Fact]
    public async Task MultiAccountDiscoveryFailureDoesNotPersistOrPauseAPartialBatch()
    {
        var secondAccount = Guid.Parse("10000000-0000-0000-0000-000000000002");
        var batch = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var settings = RandomSettings() with { BatchJobId = batch };
        var store = new RecordingDecisionStore([
            new(batch, AccountId, settings), new(batch, secondAccount, settings)],
            new Dictionary<Guid, IReadOnlyList<RemoteItemIdentitySet>>
            {
                [AccountId] = [ExactItems()], [secondAccount] = [ExactItems()],
            });
        var adapter = new AccountShareAdapter(new Dictionary<Guid, IReadOnlyList<BaiduShareRecord>>
        {
            [AccountId] = [],
            // The deliberately absent second account makes the adapter throw.
        });
        var service = new ExistingShareDecisionService(
            adapter, new MultiSessionStore(AccountId, secondAccount), store, new FrozenTimeProvider(Now));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DiscoverAsync(batch));

        Assert.Empty(store.PersistedSnapshots);
    }

    private static HarnessState Harness(
        BatchShareSettingsRecord settings,
        ExistingShareDecision? decision = ExistingShareDecision.Regenerate,
        IReadOnlyList<BaiduShareRecord>? existing = null,
        bool shareEnabled = true)
    {
        var exact = ExactItems();
        var operation = new ShareOperationRecord(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), AccountId, 1,
            "Reserved", exact.Fingerprint, settings.SettingsFingerprint, 1, null, null, "Unknown", 0,
            Now.AddMinutes(2), null, null, null, null, Now, Now, 7);
        settings = settings with { BatchJobId = operation.BatchJobId };
        var store = new RecordingStore(new ShareExecutionContext(
            operation, settings, Selected: true, ShareEnabled: shareEnabled, exact, decision));
        var adapter = new RecordingShareAdapter(exact, existing ?? []);
        var delay = new RecordingDelay();
        var executor = new ShareOperationExecutor(
            adapter, new MemorySessionStore(), store, RetryPolicy.Default, delay, new FrozenTimeProvider(Now));
        return new(executor, operation, store, adapter, delay);
    }

    private static BatchShareSettingsRecord RandomSettings() =>
        new(Guid.NewGuid(), true, true, "Random", null, "Ask", Now,
            ShareSettingsFingerprint.Compute(true, true, ShareExtractionCodeBehavior.Random, null));

    private static BatchShareSettingsRecord FixedSettings(string code) =>
        new(Guid.NewGuid(), true, true, "Fixed", code, "Ask", Now,
            ShareSettingsFingerprint.Compute(true, true, ShareExtractionCodeBehavior.Fixed, code));

    private static RemoteItemIdentitySet ExactItems() =>
        new([new RemoteItemIdentity("remote-1", "Private title")]);

    private static BaiduShareRecord Existing(
        string id, string url, string code, bool permanent, bool unlimited) =>
        new(id, ExactItems(), url, code, permanent, unlimited);

    private sealed record HarnessState(
        ShareOperationExecutor Executor,
        ShareOperationRecord Operation,
        RecordingStore Store,
        RecordingShareAdapter Adapter,
        RecordingDelay Delay);

    private sealed class RecordingStore(ShareExecutionContext context) : IShareExecutionStore
    {
        public string? LastDescriptor { get; private set; }
        public BaiduShareResultRecord? AcceptedResult { get; private set; }
        public SharePauseReason? PauseReason { get; private set; }
        public bool ResultPending { get; private set; }
        public int DefiniteRetryCount { get; private set; }

        public Task<ShareExecutionContext?> LoadAsync(Guid operationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ShareExecutionContext?>(context);

        public Task<bool> TryMarkPreparingAsync(Guid operationId, int generation, long leaseGeneration, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task AppendDiscoveryBaselinePageAsync(Guid operationId, long leaseGeneration, IEnumerable<ShareDiscoveryBaselineRecord> entries, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> TryCompleteDiscoveryBaselineAsync(Guid operationId, int generation, long leaseGeneration, string fingerprint, int count, DateTimeOffset completedAt, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> TryMarkSubmittingAsync(Guid operationId, int generation, long leaseGeneration, string descriptor, DateTimeOffset submittedAt, CancellationToken cancellationToken = default)
        {
            LastDescriptor = descriptor;
            return Task.FromResult(true);
        }

        public Task<bool> TryRecordAdapterRequestAsync(Guid operationId, int generation, long leaseGeneration, string requestId, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> TryRecordDefinitelyNotAcceptedRetryAsync(Guid operationId, int generation, long leaseGeneration, int expectedAttemptCount, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            DefiniteRetryCount++;
            return Task.FromResult(true);
        }

        public Task<bool> TryMarkResultPendingAsync(Guid operationId, int generation, long leaseGeneration, DateTimeOffset nextReconcileAt, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            ResultPending = true;
            return Task.FromResult(true);
        }

        public Task<bool> TryPauseAsync(Guid operationId, int generation, long leaseGeneration, SharePauseReason reason, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            PauseReason = reason;
            return Task.FromResult(true);
        }

        public Task<bool> TryFailAsync(Guid operationId, int generation, long leaseGeneration, string errorCode, string safeMessage, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> AcceptResultAsync(Guid operationId, ShareOperationStatus expectedStatus, long leaseGeneration, BaiduShareResultRecord result, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            AcceptedResult = result;
            return Task.FromResult(true);
        }

        public Task<bool> ReuseExistingResultAsync(Guid operationId, long leaseGeneration, BaiduShareResultRecord result, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            AcceptedResult = result;
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingShareAdapter(RemoteItemIdentitySet exact, IReadOnlyList<BaiduShareRecord> existing)
        : IBaiduShareAdapter
    {
        public List<PermanentShareRequest> CreateRequests { get; } = [];
        public List<string?> ListRequests { get; } = [];
        public Queue<BaiduAdapterResult<ShareCreationReceipt>> CreateResults { get; set; } = [];

        public BaiduAdapterResult<ShareCreationReceipt> SuccessReceipt()
        {
            var request = CreateRequests.LastOrDefault() ?? new PermanentShareRequest(exact);
            return BaiduAdapterResult<ShareCreationReceipt>.Success(new(
                Now, "adapter-request", request, "new-share", "https://pan.baidu.com/s/new", new(
                    true, true,
                    request.UsesRandomExtractionCode ? ShareExtractionCodeBehavior.Random : ShareExtractionCodeBehavior.Fixed,
                    request.FixedExtractionCode ?? "r4Nd")));
        }

        public Task<BaiduAdapterResult<ShareCreationReceipt>> CreatePermanentShareAsync(AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken)
        {
            CreateRequests.Add(request);
            return Task.FromResult(CreateResults.TryDequeue(out var result) ? result : SuccessReceipt());
        }

        public Task<BaiduAdapterResult<BaiduSharePage>> ListSharesAsync(AccountSession session, string? cursor, CancellationToken cancellationToken)
        {
            ListRequests.Add(cursor);
            return Task.FromResult(BaiduAdapterResult<BaiduSharePage>.Success(new(existing, null)));
        }
    }

    private sealed class MemorySessionStore : IBaiduSessionStore
    {
        private readonly AccountSession session = new(AccountId, "baidu-user", [
            new SessionCookie("BDUSS", "secret", ".baidu.com", "/", null, true, true)], Now);
        public Task<AccountSession?> LoadAsync(string accountId, CancellationToken cancellationToken) => Task.FromResult<AccountSession?>(session);
        public Task SaveAsync(AccountSession session, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveAsync(string accountId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class MultiSessionStore(params Guid[] accounts) : IBaiduSessionStore
    {
        private readonly Dictionary<string, AccountSession> sessions = accounts.ToDictionary(
            id => id.ToString("D"),
            id => new AccountSession(id, $"baidu-{id:N}", [
                new SessionCookie("BDUSS", $"secret-{id:N}", ".baidu.com", "/", null, true, true)], Now),
            StringComparer.OrdinalIgnoreCase);

        public Task<AccountSession?> LoadAsync(string accountId, CancellationToken cancellationToken) =>
            Task.FromResult(sessions.GetValueOrDefault(accountId));
        public Task SaveAsync(AccountSession session, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveAsync(string accountId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class AccountShareAdapter(IReadOnlyDictionary<Guid, IReadOnlyList<BaiduShareRecord>> shares)
        : IBaiduShareAdapter
    {
        public int ListCalls { get; private set; }
        public Task<BaiduAdapterResult<BaiduSharePage>> ListSharesAsync(AccountSession session, string? cursor, CancellationToken cancellationToken)
        {
            ListCalls++;
            return Task.FromResult(BaiduAdapterResult<BaiduSharePage>.Success(
                new(shares[session.LocalAccountId!.Value], null)));
        }

        public Task<BaiduAdapterResult<ShareCreationReceipt>> CreatePermanentShareAsync(AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingDecisionStore(
        IReadOnlyList<ShareDecisionAccountContext> accounts,
        IReadOnlyDictionary<Guid, IReadOnlyList<RemoteItemIdentitySet>> exactSets)
        : IExistingShareDecisionStore
    {
        private readonly Dictionary<Guid, BatchShareDiscoverySnapshotRecord> snapshots = [];
        public List<BatchShareDiscoverySnapshotRecord> PersistedSnapshots { get; } = [];
        public List<BatchShareDecisionSnapshotRecord> Decisions { get; } = [];

        public Task<IReadOnlyList<ShareDecisionAccountContext>> GetDecisionAccountsAsync(Guid batchJobId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ShareDecisionAccountContext>>(accounts.Where(x => x.BatchJobId == batchJobId).ToArray());

        public Task<StringKeysetPage<string>> GetDecisionExactSetFingerprintsKeysetPageAsync(Guid batchJobId, Guid accountId, string? afterFingerprint, int pageSize, CancellationToken cancellationToken = default)
        {
            var ordered = exactSets[accountId].Select(value => value.Fingerprint).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).Where(value => afterFingerprint is null || string.CompareOrdinal(value, afterFingerprint) > 0)
                .Take(pageSize + 1).ToList();
            var more = ordered.Count > pageSize;
            if (more) ordered.RemoveAt(ordered.Count - 1);
            return Task.FromResult(new StringKeysetPage<string>(ordered, more ? ordered[^1] : null));
        }

        public Task<bool> ContainsDecisionExactSetAsync(Guid batchJobId, Guid accountId, string exactItemSetFingerprint, CancellationToken cancellationToken = default) =>
            Task.FromResult(exactSets[accountId].Any(value => value.Fingerprint == exactItemSetFingerprint));

        public Task<bool> TrySaveDiscoveryAndPauseAsync(IReadOnlyList<BatchShareDiscoverySnapshotRecord> values, CancellationToken cancellationToken = default)
        {
            foreach (var snapshot in values)
            {
                snapshots[snapshot.AccountId] = snapshot;
                PersistedSnapshots.Add(snapshot);
            }
            return Task.FromResult(true);
        }

        public Task<BatchShareDiscoverySnapshotRecord?> GetDiscoverySnapshotAsync(Guid batchJobId, Guid accountId, string settingsFingerprint, CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshots.TryGetValue(accountId, out var value) && value.SettingsFingerprint == settingsFingerprint
                ? value
                : null);

        public Task<bool> ApplyDecisionSnapshotsAsync(IReadOnlyList<BatchShareDecisionSnapshotRecord> values, CancellationToken cancellationToken = default)
        {
            if (values.Any(value => !snapshots.TryGetValue(value.AccountId, out var current) ||
                                    current.DiscoveryFingerprint != value.DiscoveryFingerprint ||
                                    current.Version != value.DiscoveryVersion))
                return Task.FromResult(false);
            Decisions.AddRange(values);
            return Task.FromResult(true);
        }

        public void ChangeDiscovery(Guid accountId)
        {
            var current = snapshots[accountId];
            snapshots[accountId] = current with
            {
                DiscoveryFingerprint = "changed", Version = current.Version + 1,
                CompletedAt = current.CompletedAt.AddSeconds(1),
            };
        }
    }

    private sealed class RecordingDelay : IAsyncDelay
    {
        public List<TimeSpan> Delays { get; } = [];
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
