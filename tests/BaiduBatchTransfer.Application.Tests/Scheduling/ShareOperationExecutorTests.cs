using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Tests.Scheduling;

public sealed partial class ShareOperationExecutorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 4, 0, 0, TimeSpan.Zero);
    private static readonly Guid AccountId = Guid.Parse("10000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task OperationBoundTransferSharePreparesBeforeBusinessBoundaryAndUsesOneGroupedSubmit()
    {
        var exact = new RemoteItemIdentitySet([
            new RemoteItemIdentity("remote-1", "folder"),
            new RemoteItemIdentity("remote-2", "app.apk")]);
        var settings = RandomSettings();
        var operation = new ShareOperationRecord(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), AccountId, 1,
            "Reserved", exact.Fingerprint, settings.SettingsFingerprint, 1, null, null, "Unknown", 0,
            Now.AddMinutes(2), null, null, null, null, Now, Now, 7);
        settings = settings with { BatchJobId = operation.BatchJobId };
        var store = new RecordingStore(new ShareExecutionContext(
            operation, settings, true, true, exact, ExistingShareDecision.Regenerate));
        var adapter = new OperationBoundRecordingAdapter(exact);
        var executor = new ShareOperationExecutor(
            adapter, new MemorySessionStore(), store, RetryPolicy.Default,
            new RecordingDelay(), new FrozenTimeProvider(Now));

        var result = await executor.ExecuteAsync(Lease(operation));

        Assert.Equal(ShareExecutionDisposition.Succeeded, result.Disposition);
        Assert.Equal(["prepare", "submit"], adapter.Calls);
        Assert.Equal(2, Assert.Single(adapter.Requests).RemoteItems.Count);
    }

    [Fact]
    public async Task OperationBoundPrepareFailureBeforeBusinessBoundaryFailsSafelyWithoutSubmit()
    {
        var exact = ExactItems();
        var settings = RandomSettings();
        var operation = new ShareOperationRecord(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), AccountId, 1,
            "Reserved", exact.Fingerprint, settings.SettingsFingerprint, 1, null, null, "Unknown", 0,
            Now.AddMinutes(2), null, null, null, null, Now, Now, 7);
        settings = settings with { BatchJobId = operation.BatchJobId };
        var store = new RecordingStore(new ShareExecutionContext(
            operation, settings, true, true, exact, ExistingShareDecision.Regenerate));
        var adapter = new OperationBoundRecordingAdapter(exact, prepareFails: true);
        var executor = new ShareOperationExecutor(
            adapter, new MemorySessionStore(), store, RetryPolicy.Default,
            new RecordingDelay(), new FrozenTimeProvider(Now));

        var result = await executor.ExecuteAsync(Lease(operation));

        Assert.Equal(ShareExecutionDisposition.Failed, result.Disposition);
        Assert.Equal(["prepare"], adapter.Calls);
        Assert.False(store.ResultPending);
    }

    [Fact]
    public async Task RandomAndFixedModesAlwaysRequestPermanentUnlimitedShares()
    {
        var random = Harness(RandomSettings());
        await random.Executor.ExecuteAsync(Lease(random.Operation));
        var fixedCode = Harness(FixedSettings("a1B2"));
        await fixedCode.Executor.ExecuteAsync(Lease(fixedCode.Operation));

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

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Executor.ExecuteAsync(Lease(harness.Operation)));

        Assert.Empty(harness.Adapter.ListRequests);
        Assert.Empty(harness.Adapter.CreateRequests);
    }

    [Fact]
    public async Task CompatibleExistingShareIsReusedWithoutCreate()
    {
        var existing = Existing("existing", "https://pan.baidu.com/s/existing", "r4Nd", permanent: true, unlimited: true);
        var harness = Harness(RandomSettings(), ExistingShareDecision.Reuse, [existing]);

        var outcome = await harness.Executor.ExecuteAsync(Lease(harness.Operation));

        Assert.Equal(ShareExecutionDisposition.ReusedExisting, outcome.Disposition);
        Assert.Empty(harness.Adapter.CreateRequests);
        Assert.Equal("https://pan.baidu.com/s/existing?pwd=r4Nd", harness.Store.AcceptedResult?.ShareUrl);
        Assert.Equal(existing.ExtractionCode, harness.Store.AcceptedResult?.ExtractionCode);
    }

    [Fact]
    public async Task RegenerateCreatesNewShareAndNeverCancelsExistingShare()
    {
        var harness = Harness(RandomSettings(), ExistingShareDecision.Regenerate,
            [Existing("old", "https://pan.baidu.com/s/old", "o1d2", true, true)]);

        var outcome = await harness.Executor.ExecuteAsync(Lease(harness.Operation));

        Assert.Equal(ShareExecutionDisposition.Succeeded, outcome.Disposition);
        Assert.Single(harness.Adapter.CreateRequests);
        Assert.Equal("new-share", harness.Store.AcceptedResult?.ShareId);
        Assert.Equal("https://pan.baidu.com/s/new?pwd=r4Nd", harness.Store.AcceptedResult?.ShareUrl);
        Assert.Equal("r4Nd", harness.Store.AcceptedResult?.ExtractionCode);
    }

    [Fact]
    public async Task AcceptedReceiptWithConflictingUrlCodeBecomesPendingAfterSubmission()
    {
        var harness = Harness(RandomSettings());
        harness.Adapter.SuccessShareUrl = "https://pan.baidu.com/s/new?pwd=x9Y8";

        var outcome = await harness.Executor.ExecuteAsync(Lease(harness.Operation));

        Assert.Equal(ShareExecutionDisposition.ResultPending, outcome.Disposition);
        Assert.Equal("ResultPending", harness.Store.Status);
        Assert.NotEqual("Submitting", harness.Store.Status);
        Assert.True(harness.Store.ResultPending);
        Assert.Null(harness.Store.AcceptedResult);
        Assert.Single(harness.Adapter.CreateRequests);
        Assert.DoesNotContain("x9Y8", outcome.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("r4Nd", outcome.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReusableShareWithConflictingUrlCodeFailsWithoutSubmissionOrSecretDiagnostic()
    {
        var harness = Harness(RandomSettings(), ExistingShareDecision.Reuse,
            [Existing("existing", "https://pan.baidu.com/s/existing?pwd=x9Y8", "r4Nd", true, true)]);

        var outcome = await harness.Executor.ExecuteAsync(Lease(harness.Operation));

        Assert.Equal(ShareExecutionDisposition.Failed, outcome.Disposition);
        Assert.Equal("Failed", harness.Store.Status);
        Assert.NotEqual("Submitting", harness.Store.Status);
        Assert.Equal("InvalidShareLink", harness.Store.ErrorCode);
        Assert.Null(harness.Store.AcceptedResult);
        Assert.Empty(harness.Adapter.CreateRequests);
        var diagnostic = harness.Store.ErrorCode + harness.Store.SafeMessage + outcome;
        Assert.DoesNotContain("x9Y8", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("r4Nd", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncompatibleExistingShareCannotSilentlyReuse()
    {
        var harness = Harness(RandomSettings(), ExistingShareDecision.Reuse,
            [Existing("old", "https://pan.baidu.com/s/old", "o1d2", permanent: false, unlimited: true)]);

        var outcome = await harness.Executor.ExecuteAsync(Lease(harness.Operation));

        Assert.Equal(ShareExecutionDisposition.Paused, outcome.Disposition);
        Assert.Equal(SharePauseReason.IncompatibleExistingShare, harness.Store.PauseReason);
        Assert.Empty(harness.Adapter.CreateRequests);
    }

    [Fact]
    public async Task CancelDecisionPausesWithoutCreating()
    {
        var harness = Harness(RandomSettings(), ExistingShareDecision.Cancel,
            [Existing("old", "https://pan.baidu.com/s/old", "o1d2", true, true)]);

        var outcome = await harness.Executor.ExecuteAsync(Lease(harness.Operation));

        Assert.Equal(ShareExecutionDisposition.Paused, outcome.Disposition);
        Assert.Equal(SharePauseReason.CancelledByUser, harness.Store.PauseReason);
        Assert.Empty(harness.Adapter.CreateRequests);
    }

    [Fact]
    public async Task PersistedCancelStillPausesWhenPreviouslyDiscoveredShareDisappears()
    {
        var harness = Harness(RandomSettings(), ExistingShareDecision.Cancel, []);

        var outcome = await harness.Executor.ExecuteAsync(Lease(harness.Operation));

        Assert.Equal(ShareExecutionDisposition.Paused, outcome.Disposition);
        Assert.Equal(SharePauseReason.CancelledByUser, harness.Store.PauseReason);
        Assert.Empty(harness.Adapter.CreateRequests);
    }

    [Fact]
    public async Task PersistedReuseNeverCreatesWhenPreviouslyDiscoveredShareDisappears()
    {
        var harness = Harness(RandomSettings(), ExistingShareDecision.Reuse, []);

        var outcome = await harness.Executor.ExecuteAsync(Lease(harness.Operation));

        Assert.Equal(ShareExecutionDisposition.Paused, outcome.Disposition);
        Assert.Equal(SharePauseReason.AwaitingExistingShareDecision, harness.Store.PauseReason);
        Assert.Empty(harness.Adapter.CreateRequests);
    }

    [Fact]
    public async Task ConfirmedReuseDoesNotFallbackWhenConfirmedShareWasReplaced()
    {
        var confirmed = Existing("confirmed", "https://pan.baidu.com/s/confirmed", "a1b2", true, true);
        var replacement = Existing("replacement", "https://pan.baidu.com/s/replacement", "c3d4", true, true);
        var harness = Harness(RandomSettings(), ExistingShareDecision.Reuse, [replacement], confirmedExisting: confirmed);

        var outcome = await harness.Executor.ExecuteAsync(Lease(harness.Operation));

        Assert.Equal(ShareExecutionDisposition.Paused, outcome.Disposition);
        Assert.Equal(SharePauseReason.AwaitingExistingShareDecision, harness.Store.PauseReason);
        Assert.Null(harness.Store.AcceptedResult);
    }

    [Fact]
    public async Task ConfirmedReusePausesWhenConfirmedShareDisappears()
    {
        var confirmed = Existing("confirmed", "https://pan.baidu.com/s/confirmed", "a1b2", true, true);
        var harness = Harness(RandomSettings(), ExistingShareDecision.Reuse, [], confirmedExisting: confirmed);

        var outcome = await harness.Executor.ExecuteAsync(Lease(harness.Operation));

        Assert.Equal(ShareExecutionDisposition.Paused, outcome.Disposition);
        Assert.Equal(SharePauseReason.AwaitingExistingShareDecision, harness.Store.PauseReason);
        Assert.Empty(harness.Adapter.CreateRequests);
        Assert.Null(harness.Store.AcceptedResult);
    }

    [Fact]
    public async Task ConfirmedIncompatibleMemberIsNeverReusedInMixedAccountDecision()
    {
        var incompatible=Existing("incompatible","https://pan.baidu.com/s/incompatible","a1b2",false,true);
        var harness=Harness(RandomSettings(),ExistingShareDecision.Reuse,[incompatible],confirmedExisting:incompatible);

        var outcome=await harness.Executor.ExecuteAsync(Lease(harness.Operation));

        Assert.Equal(ShareExecutionDisposition.Paused,outcome.Disposition);
        Assert.Null(harness.Store.AcceptedResult);
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

        var outcome = await harness.Executor.ExecuteAsync(Lease(harness.Operation));

        Assert.Equal(ShareExecutionDisposition.Succeeded, outcome.Disposition);
        Assert.Equal(4, harness.Adapter.CreateRequests.Count);
        Assert.Equal(3, harness.Store.DefiniteRetryCount);
        Assert.Equal([TimeSpan.FromSeconds(23), TimeSpan.FromSeconds(23), TimeSpan.FromSeconds(23)], harness.Delay.Delays);
    }

    [Fact]
    public async Task RetryNeverCallsAdapterUntilUnknownInFlightBoundaryIsDurable()
    {
        var failure = BaiduAdapterResult<ShareCreationReceipt>.Failure(
            BaiduErrorKind.TransientService, "private", submissionAcceptance: SubmissionAcceptance.DefinitelyNotAccepted);
        var harness = Harness(RandomSettings());
        harness.Adapter.CreateResults = new([failure, harness.Adapter.SuccessReceipt()]);
        harness.Store.BeginSubmissionAttemptAllowed = false;

        var outcome = await harness.Executor.ExecuteAsync(Lease(harness.Operation));

        Assert.Equal(ShareExecutionDisposition.Stale, outcome.Disposition);
        Assert.Single(harness.Adapter.CreateRequests);
        Assert.Equal(1, harness.Store.DefiniteRetryCount);
    }

    [Fact]
    public async Task StaleReservationTokenCannotBorrowTheLatestLease()
    {
        var harness = Harness(RandomSettings());

        var outcome = await harness.Executor.ExecuteAsync(
            new(harness.Operation.Id, harness.Operation.Generation, harness.Operation.LeaseGeneration - 1));

        Assert.Equal(ShareExecutionDisposition.Stale, outcome.Disposition);
        Assert.Empty(harness.Adapter.ListRequests);
        Assert.Empty(harness.Adapter.CreateRequests);
    }

    [Fact]
    public async Task UnknownAcceptanceBecomesPendingAfterFirstAttemptWithoutBlindRetry()
    {
        var harness = Harness(RandomSettings());
        harness.Adapter.CreateResults = new([
            BaiduAdapterResult<ShareCreationReceipt>.Failure(
                BaiduErrorKind.UnknownResponse, "private response", submissionAcceptance: SubmissionAcceptance.Unknown)]);

        var outcome = await harness.Executor.ExecuteAsync(Lease(harness.Operation));

        Assert.Equal(ShareExecutionDisposition.ResultPending, outcome.Disposition);
        Assert.Single(harness.Adapter.CreateRequests);
        Assert.True(harness.Store.ResultPending);
    }

    [Fact]
    public async Task ShareOptOutHasStableStatusAndZeroExternalCalls()
    {
        var harness = Harness(RandomSettings(), shareEnabled: false);

        var outcome = await harness.Executor.ExecuteAsync(Lease(harness.Operation));

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
        var member=new ExistingShareDiscoveryMember(summary.BatchJobId,AccountId,"settings","discovery",
            "exact","private-share","https://pan.baidu.com/s/private","a1b2",true,true,true);
        Assert.DoesNotContain("private-share",member.ToString(),StringComparison.Ordinal);
        Assert.DoesNotContain("a1b2",member.ToString(),StringComparison.Ordinal);
        Assert.DoesNotContain("https://",member.ToString(),StringComparison.Ordinal);
        var page=new ExistingShareDiscoveryPage([member],"private-cursor");
        Assert.DoesNotContain("private-cursor",page.ToString(),StringComparison.Ordinal);
        var operationId=Guid.NewGuid();
        Assert.DoesNotContain(operationId.ToString("D"),new ShareExecutionLease(operationId,1,7).ToString(),StringComparison.OrdinalIgnoreCase);
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
    public async Task RemoteDiscoveryChangeBeforeConfirmationMakesOldTokenStale()
    {
        var batch = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var settings = RandomSettings() with { BatchJobId = batch };
        var shares = new Dictionary<Guid, IReadOnlyList<BaiduShareRecord>>
        {
            [AccountId] = [Existing("one", "https://pan.baidu.com/s/one", "a1b2", true, true)],
        };
        var store = new RecordingDecisionStore([new(batch, AccountId, settings)],
            new Dictionary<Guid, IReadOnlyList<RemoteItemIdentitySet>> { [AccountId] = [ExactItems()] });
        var service = new ExistingShareDecisionService(
            new AccountShareAdapter(shares), new MultiSessionStore(AccountId), store, new FrozenTimeProvider(Now));
        var summary = await service.DiscoverAsync(batch);
        shares[AccountId] = [Existing("two", "https://pan.baidu.com/s/two", "c3d4", true, true)];

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
        bool shareEnabled = true,
        BaiduShareRecord? confirmedExisting = null)
    {
        var exact = ExactItems();
        var operation = new ShareOperationRecord(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), AccountId, 1,
            "Reserved", exact.Fingerprint, settings.SettingsFingerprint, 1, null, null, "Unknown", 0,
            Now.AddMinutes(2), null, null, null, null, Now, Now, 7);
        settings = settings with { BatchJobId = operation.BatchJobId };
        var store = new RecordingStore(new ShareExecutionContext(
            operation, settings, Selected: true, ShareEnabled: shareEnabled, exact, decision, confirmedExisting));
        var adapter = new RecordingShareAdapter(exact, existing ?? []);
        var delay = new RecordingDelay();
        var executor = new ShareOperationExecutor(
            adapter, new MemorySessionStore(), store, RetryPolicy.Default, delay, new FrozenTimeProvider(Now));
        return new(executor, operation, store, adapter, delay);
    }

    private static BatchShareSettingsRecord RandomSettings() =>
        new(Guid.NewGuid(), true, true, "Random", null, "Ask", Now,
            ShareSettingsFingerprint.Compute(true, true, ShareExtractionCodeBehavior.Random, null));

    private static ShareExecutionLease Lease(ShareOperationRecord operation) =>
        new(operation.Id, operation.Generation, operation.LeaseGeneration);

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
        public string Status { get; private set; } = context.Operation.Status;
        public string? ErrorCode { get; private set; }
        public string? SafeMessage { get; private set; }
        public string? LastDescriptor { get; private set; }
        public BaiduShareResultRecord? AcceptedResult { get; private set; }
        public SharePauseReason? PauseReason { get; private set; }
        public bool ResultPending { get; private set; }
        public int DefiniteRetryCount { get; private set; }
        public bool BeginSubmissionAttemptAllowed { get; set; } = true;
        private readonly List<ShareDiscoveryBaselineRecord> baseline = [];

        public Task<ShareExecutionContext?> LoadAsync(Guid operationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ShareExecutionContext?>(context);

        public Task<bool> TryMarkPreparingAsync(Guid operationId, int generation, long leaseGeneration, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            Status = "Preparing";
            return Task.FromResult(true);
        }

        public Task AppendDiscoveryBaselinePageAsync(Guid operationId, long leaseGeneration, IEnumerable<ShareDiscoveryBaselineRecord> entries, CancellationToken cancellationToken = default) =>
            AddBaselineAsync(entries);

        private Task AddBaselineAsync(IEnumerable<ShareDiscoveryBaselineRecord> entries)
        {
            foreach(var entry in entries)
                if(!baseline.Any(value=>value.ShareId==entry.ShareId))baseline.Add(entry);
            return Task.CompletedTask;
        }

        public Task<StringKeysetPage<ShareDiscoveryBaselineRecord>> GetDiscoveryBaselineKeysetPageAsync(Guid operationId, string? afterShareId, int pageSize, CancellationToken cancellationToken = default)
        {
            var values=baseline.OrderBy(value=>value.ShareId,StringComparer.Ordinal)
                .Where(value=>afterShareId is null||string.CompareOrdinal(value.ShareId,afterShareId)>0)
                .Take(pageSize+1).ToList();
            var more=values.Count>pageSize;if(more)values.RemoveAt(values.Count-1);
            return Task.FromResult(new StringKeysetPage<ShareDiscoveryBaselineRecord>(values,more?values[^1].ShareId:null));
        }

        public Task<bool> TryCompleteDiscoveryBaselineAsync(Guid operationId, int generation, long leaseGeneration, string fingerprint, int count, DateTimeOffset completedAt, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> TryMarkSubmittingAsync(Guid operationId, int generation, long leaseGeneration, string descriptor, DateTimeOffset submittedAt, CancellationToken cancellationToken = default)
        {
            LastDescriptor = descriptor;
            Status = "Submitting";
            return Task.FromResult(true);
        }

        public Task<bool> TryRecordAdapterRequestAsync(Guid operationId, int generation, long leaseGeneration, string requestId, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> TryRecordDefinitelyNotAcceptedRetryAsync(Guid operationId, int generation, long leaseGeneration, int expectedAttemptCount, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            DefiniteRetryCount++;
            return Task.FromResult(true);
        }

        public Task<bool> TryBeginSubmissionAttemptAsync(Guid operationId, int generation, long leaseGeneration, int expectedAttemptCount, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
            Task.FromResult(BeginSubmissionAttemptAllowed);

        public Task<bool> TryMarkResultPendingAsync(Guid operationId, int generation, long leaseGeneration, DateTimeOffset nextReconcileAt, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            ResultPending = true;
            Status = "ResultPending";
            return Task.FromResult(true);
        }

        public Task<bool> TryPauseAsync(Guid operationId, int generation, long leaseGeneration, SharePauseReason reason, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            PauseReason = reason;
            return Task.FromResult(true);
        }

        public Task<bool> TryFailAsync(Guid operationId, int generation, long leaseGeneration, string errorCode, string safeMessage, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            Status = "Failed";
            ErrorCode = errorCode;
            SafeMessage = safeMessage;
            return Task.FromResult(true);
        }

        public Task<bool> AcceptResultAsync(Guid operationId, ShareOperationStatus expectedStatus, long leaseGeneration, BaiduShareResultRecord result, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            Status = "Succeeded";
            AcceptedResult = result;
            return Task.FromResult(true);
        }

        public Task<bool> ReuseExistingResultAsync(Guid operationId, long leaseGeneration, BaiduShareResultRecord result, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            Status = "ReusedExisting";
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
        public string SuccessShareUrl { get; set; } = "https://pan.baidu.com/s/new";

        public BaiduAdapterResult<ShareCreationReceipt> SuccessReceipt()
        {
            var request = CreateRequests.LastOrDefault() ?? new PermanentShareRequest(exact);
            return BaiduAdapterResult<ShareCreationReceipt>.Success(new(
                Now, "adapter-request", request, "new-share", SuccessShareUrl, new(
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

    private sealed class OperationBoundRecordingAdapter(
        RemoteItemIdentitySet exact,
        bool prepareFails = false) : IOperationBoundBaiduShareAdapter
    {
        public List<string> Calls { get; } = [];
        public List<PermanentShareRequest> Requests { get; } = [];
        public Task<BaiduAdapterResult<BaiduSharePage>> ListSharesAsync(AccountSession session, string? cursor, CancellationToken cancellationToken) =>
            Task.FromResult(BaiduAdapterResult<BaiduSharePage>.Success(new([], null)));
        public Task<BaiduAdapterResult<ShareCreationReceipt>> CreatePermanentShareAsync(AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Bound transfer sharing must not use the legacy write method.");
        public Task<BaiduAdapterResult<SharePreparationReceipt>> PreparePermanentShareForOperationAsync(ShareOperationAutomationContext context, AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken)
        {
            Assert.Equal(exact.Fingerprint, context.ExactRemoteItems.Fingerprint);
            Calls.Add("prepare");
            if (prepareFails)
                return Task.FromResult(BaiduAdapterResult<SharePreparationReceipt>.Failure(
                    BaiduErrorKind.UnknownResponse, adapterDiagnostic: null,
                    submissionAcceptance: SubmissionAcceptance.Unknown));
            return Task.FromResult(BaiduAdapterResult<SharePreparationReceipt>.Success(SharePreparationReceipt.Ready, SubmissionAcceptance.DefinitelyNotAccepted));
        }
        public Task<BaiduAdapterResult<ShareCreationReceipt>> SubmitPermanentShareForOperationAsync(ShareOperationAutomationContext context, AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("submit"); Requests.Add(request);
            return Task.FromResult(BaiduAdapterResult<ShareCreationReceipt>.Success(new(
                Now, "adapter", request, "new-share", "https://pan.baidu.com/s/new-share",
                new EffectiveShareSettings(true, true, ShareExtractionCodeBehavior.Random, "r4Nd"))));
        }
        public Task<BaiduAdapterResult<ShareCreationReceipt>> ConfirmExactShareForOperationAsync(ShareOperationAutomationContext context, AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
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
        private readonly Dictionary<(Guid RunId, Guid AccountId), List<ExistingShareDiscoveryMember>> staged = [];
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

        public Task<long> BeginDiscoveryAsync(Guid runId, ShareDecisionAccountContext account, CancellationToken cancellationToken = default) =>
            Task.FromResult((long)exactSets[account.AccountId].Select(value => value.Fingerprint).Distinct().Count());

        public Task<StringKeysetPage<string>> GetDiscoveryRunExactSetFingerprintsKeysetPageAsync(Guid runId, Guid accountId, string? afterFingerprint, int pageSize, CancellationToken cancellationToken = default) =>
            GetDecisionExactSetFingerprintsKeysetPageAsync(accounts[0].BatchJobId, accountId, afterFingerprint, pageSize, cancellationToken);

        public Task StageDiscoveryPageAsync(Guid runId, ShareDecisionAccountContext account, IReadOnlyList<BaiduShareRecord> candidates, CancellationToken cancellationToken = default)
        {
            var values = staged.GetValueOrDefault((runId, account.AccountId));
            if (values is null) staged[(runId, account.AccountId)] = values = [];
            foreach (var share in candidates.Where(value => exactSets[account.AccountId].Any(set => set.Fingerprint == value.RemoteItems.Fingerprint)))
            {
                var compatible = share.IsPermanent && share.IsAccessUnlimited &&
                    (account.Settings.ExtractionCodeMode == "Random" || share.ExtractionCode == account.Settings.FixedExtractionCode);
                values.Add(new(account.BatchJobId, account.AccountId, account.Settings.SettingsFingerprint, string.Empty,
                    share.RemoteItems.Fingerprint, share.ShareId, share.ShareUrl, share.ExtractionCode,
                    share.IsPermanent, share.IsAccessUnlimited, compatible));
            }
            return Task.CompletedTask;
        }

        public Task<ExistingShareDiscoveryPage> GetStagedDiscoveryPageAsync(Guid runId, Guid accountId, string? afterCursor, int pageSize, CancellationToken cancellationToken = default)
        {
            var values = staged.GetValueOrDefault((runId, accountId), []).OrderBy(x => x.ExactItemSetFingerprint, StringComparer.Ordinal)
                .ThenBy(x => x.ShareId, StringComparer.Ordinal).ToArray();
            return Task.FromResult(new ExistingShareDiscoveryPage(values, null));
        }

        public Task AbortDiscoveryAsync(Guid runId, CancellationToken cancellationToken = default)
        {
            foreach (var key in staged.Keys.Where(key => key.RunId == runId).ToArray()) staged.Remove(key);
            return Task.CompletedTask;
        }

        public Task<bool> TrySaveDiscoveryAndPauseAsync(Guid runId, IReadOnlyList<BatchShareDiscoverySnapshotRecord> values, CancellationToken cancellationToken = default)
        {
            foreach (var snapshot in values)
            {
                snapshots[snapshot.AccountId] = snapshot;
                PersistedSnapshots.Add(snapshot);
            }
            foreach (var key in staged.Keys.Where(key => key.RunId == runId).ToArray()) staged.Remove(key);
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
