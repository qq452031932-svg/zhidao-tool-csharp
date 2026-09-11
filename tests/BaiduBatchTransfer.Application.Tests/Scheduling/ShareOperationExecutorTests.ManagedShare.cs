using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Tests.Scheduling;

public sealed partial class ShareOperationExecutorTests
{
    [Fact]
    public async Task AcceptanceOnlyManagedShareUsesBoundSessionAndPersistsExistingShareResult()
    {
        var exact = new RemoteItemIdentitySet([
            new("destination-exact-id-1", "application.apk"),
            new("destination-exact-id-2", "assets")]);
        var managedSession = new ManagedBrowserSessionReference(
            Guid.NewGuid(), Guid.NewGuid(), AccountId, "baidu-user", BrowserFamily.Chrome,
            "synthetic-profile", "synthetic-ownership");
        var profile = ExecutionProfileSnapshot.Managed(
            managedSession.BrowserFamily, managedSession.ProfileId,
            managedSession.InstallationId, managedSession.SessionId);
        var settings = RandomSettings();
        var operation = new ShareOperationRecord(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), settings.BatchJobId, AccountId, 1,
            "Reserved", exact.Fingerprint, settings.SettingsFingerprint, 1, null, null, "Unknown", 0,
            Now.AddMinutes(2), null, null, null, null, Now, Now, 7, profile);
        var store = new RecordingStore(new ShareExecutionContext(
            operation, settings, Selected: true, ShareEnabled: true, exact,
            ExistingShareDecision.Regenerate, AcceptanceOnly: true));
        var managed = new ManagedShareAdapter(exact);
        var executor = new ShareOperationExecutor(
            new ExecutionBoundShareAdapter(new OperationBoundRecordingAdapter(exact), managed),
            new ReadyExecutionResolver(BaiduExecutionContext.Managed(managedSession)),
            store, RetryPolicy.Default, new RecordingDelay(), new FrozenTimeProvider(Now));

        var result = await executor.ExecuteAsync(Lease(operation));

        Assert.Equal(ShareExecutionDisposition.Succeeded, result.Disposition);
        Assert.Equal(["discover", "prepare", "submit"], managed.Calls);
        Assert.NotNull(store.AcceptedResult);
        Assert.Equal(exact.Fingerprint, store.AcceptedResult!.ExactItemSetFingerprint);
        Assert.Equal("https://pan.baidu.com/s/managed-share?pwd=r4Nd", store.AcceptedResult.ShareUrl);
    }

    [Fact]
    public async Task BoundShareExecutorRejectsOrdinaryShareBeforeExecutionResolution()
    {
        var exact = ExactItems();
        var managedSession = new ManagedBrowserSessionReference(
            Guid.NewGuid(), Guid.NewGuid(), AccountId, "baidu-user", BrowserFamily.Chrome,
            "synthetic-profile", "synthetic-ownership");
        var profile = ExecutionProfileSnapshot.Managed(
            managedSession.BrowserFamily, managedSession.ProfileId,
            managedSession.InstallationId, managedSession.SessionId);
        var settings = RandomSettings();
        var operation = new ShareOperationRecord(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), settings.BatchJobId, AccountId, 1,
            "Reserved", exact.Fingerprint, settings.SettingsFingerprint, 1, null, null, "Unknown", 0,
            Now.AddMinutes(2), null, null, null, null, Now, Now, 7, profile);
        var store = new RecordingStore(new ShareExecutionContext(
            operation, settings, Selected: true, ShareEnabled: true, exact,
            ExistingShareDecision.Regenerate, AcceptanceOnly: false));
        var resolver = new ReadyExecutionResolver(BaiduExecutionContext.Managed(managedSession));
        var managed = new ManagedShareAdapter(exact);
        var executor = new ShareOperationExecutor(
            new ExecutionBoundShareAdapter(new OperationBoundRecordingAdapter(exact), managed),
            resolver, store, RetryPolicy.Default, new RecordingDelay(), new FrozenTimeProvider(Now));

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(Lease(operation)));

        Assert.Equal(0, resolver.CallCount);
        Assert.Empty(managed.Calls);
    }

    [Fact]
    public async Task DirectCookieOptInUsesCookieSessionForManagedSnapshot()
    {
        var exact = ExactItems();
        var profile = ExecutionProfileSnapshot.Managed(
            BrowserFamily.Chrome, "managed-profile", Guid.NewGuid(), Guid.NewGuid());
        var settings = RandomSettings();
        var operation = new ShareOperationRecord(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), settings.BatchJobId, AccountId, 1,
            "Reserved", exact.Fingerprint, settings.SettingsFingerprint, 1, null, null, "Unknown", 0,
            Now.AddMinutes(2), null, null, null, null, Now, Now, 7, profile);
        var store = new RecordingStore(new ShareExecutionContext(
            operation, settings, Selected: true, ShareEnabled: true, exact,
            ExistingShareDecision.Regenerate, AcceptanceOnly: false));
        var adapter = new RecordingShareAdapter(exact, []);
        var executor = new ShareOperationExecutor(
            adapter,
            new MemorySessionStore(),
            store,
            RetryPolicy.Default,
            new RecordingDelay(),
            new FrozenTimeProvider(Now),
            allowLegacySessionForAnyProfile: true);

        var result = await executor.ExecuteAsync(Lease(operation));

        Assert.Equal(ShareExecutionDisposition.Succeeded, result.Disposition);
        Assert.Single(adapter.CreateRequests);
        Assert.Null(store.PauseReason);
    }

    private sealed class ReadyExecutionResolver(BaiduExecutionContext execution) : IBaiduExecutionContextResolver
    {
        public int CallCount { get; private set; }

        public Task<BaiduExecutionResolution> ResolveAsync(
            Guid accountId, ExecutionProfileSnapshot profile, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(BaiduExecutionResolution.Ready(execution));
        }
    }

    private sealed class ManagedShareAdapter(RemoteItemIdentitySet exact) : IManagedChromiumShareAdapter
    {
        public List<string> Calls { get; } = [];

        public Task<BaiduAdapterResult<ShareDiscoverySnapshot>> DiscoverPermanentSharesForOperationAsync(
            ShareOperationAutomationContext context, ManagedBrowserSessionReference session,
            PermanentShareRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("discover");
            return Task.FromResult(BaiduAdapterResult<ShareDiscoverySnapshot>.Success(
                new(WebAutomationBaseline.Create([]), [], []),
                SubmissionAcceptance.DefinitelyNotAccepted));
        }

        public Task<BaiduAdapterResult<SharePreparationReceipt>> PreparePermanentShareForOperationAsync(
            ShareOperationAutomationContext context, ManagedBrowserSessionReference session,
            PermanentShareRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("prepare");
            return Task.FromResult(BaiduAdapterResult<SharePreparationReceipt>.Success(
                SharePreparationReceipt.Ready, SubmissionAcceptance.DefinitelyNotAccepted));
        }

        public Task<BaiduAdapterResult<ShareCreationReceipt>> SubmitPermanentShareForOperationAsync(
            ShareOperationAutomationContext context, ManagedBrowserSessionReference session,
            PermanentShareRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("submit");
            Assert.Equal(exact, context.ExactRemoteItems);
            return Task.FromResult(BaiduAdapterResult<ShareCreationReceipt>.Success(new(
                Now, "managed-adapter-request", request, "managed-share-id",
                "https://pan.baidu.com/s/managed-share",
                new EffectiveShareSettings(true, true, ShareExtractionCodeBehavior.Random, "r4Nd"))));
        }

        public Task<BaiduAdapterResult<ShareCreationReceipt>> ConfirmExactShareForOperationAsync(
            ShareOperationAutomationContext context, ManagedBrowserSessionReference session,
            PermanentShareRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected confirmation.");
    }
}
