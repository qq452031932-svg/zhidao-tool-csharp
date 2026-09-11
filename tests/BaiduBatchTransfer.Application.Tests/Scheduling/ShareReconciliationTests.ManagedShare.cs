using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Tests.Scheduling;

public sealed partial class ShareReconciliationTests
{
    [Fact]
    public async Task AcceptanceOnlyManagedUnknownUsesReadOnlyBoundConfirmation()
    {
        var accountId = Guid.NewGuid();
        var exact = new RemoteItemIdentitySet([
            new("destination-exact-id-1", "application.apk"),
            new("destination-exact-id-2", "assets")]);
        var session = new ManagedBrowserSessionReference(
            Guid.NewGuid(), Guid.NewGuid(), accountId, "baidu-user", BrowserFamily.Chrome,
            "synthetic-profile", "synthetic-ownership");
        var profile = ExecutionProfileSnapshot.Managed(
            session.BrowserFamily, session.ProfileId, session.InstallationId, session.SessionId);
        var settingsFingerprint = ShareSettingsFingerprint.Compute(
            true, true, ShareExtractionCodeBehavior.Random, null);
        var operation = new ShareOperationRecord(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), accountId, 1,
            "ResultPending", exact.Fingerprint, settingsFingerprint, 1, "{}", "request", "Unknown", 1,
            null, Now.AddMinutes(-1), Now, null, null, Now.AddMinutes(-2), Now, 4, profile);
        var settings = new BatchShareSettingsRecord(
            operation.BatchJobId, true, true, "Random", null, "Regenerate", Now, settingsFingerprint);
        var store = new RecordingStore(new ShareExecutionContext(
            operation, settings, true, true, exact, ExistingShareDecision.Regenerate,
            AcceptanceOnly: true));
        var managed = new ReadOnlyManagedShareAdapter();
        var service = new ShareOperationReconciliationService(
            new ExecutionBoundShareAdapter(new BoundReconciliationAdapter(), managed),
            new ManagedReconciliationResolver(BaiduExecutionContext.Managed(session)),
            store, new RecordingDelay(),
            new ShareReconciliationOptions(1, 10, TimeSpan.Zero, TimeSpan.FromMinutes(1)),
            new FrozenTimeProvider(Now));

        var result = await service.ReconcileAsync(operation.Id);

        Assert.Equal(ShareReconciliationDisposition.Accepted, result.Disposition);
        Assert.Equal(1, managed.ConfirmCalls);
        Assert.Equal(0, managed.DiscoverCalls);
        Assert.Equal(0, managed.PrepareCalls);
        Assert.Equal(0, managed.SubmitCalls);
        Assert.NotNull(store.Accepted);
        Assert.Equal(exact.Fingerprint, store.Accepted!.ExactItemSetFingerprint);
    }

    private sealed class ManagedReconciliationResolver(BaiduExecutionContext execution)
        : IBaiduExecutionContextResolver
    {
        public Task<BaiduExecutionResolution> ResolveAsync(
            Guid accountId, ExecutionProfileSnapshot profile, CancellationToken cancellationToken) =>
            Task.FromResult(BaiduExecutionResolution.Ready(execution));
    }

    private sealed class ReadOnlyManagedShareAdapter : IManagedChromiumShareAdapter
    {
        public int DiscoverCalls { get; private set; }
        public int PrepareCalls { get; private set; }
        public int SubmitCalls { get; private set; }
        public int ConfirmCalls { get; private set; }

        public Task<BaiduAdapterResult<ShareDiscoverySnapshot>> DiscoverPermanentSharesForOperationAsync(
            ShareOperationAutomationContext context, ManagedBrowserSessionReference session,
            PermanentShareRequest request, CancellationToken cancellationToken)
        {
            DiscoverCalls++;
            throw new InvalidOperationException("Recovery cannot discover through a submit-capable path.");
        }

        public Task<BaiduAdapterResult<SharePreparationReceipt>> PreparePermanentShareForOperationAsync(
            ShareOperationAutomationContext context, ManagedBrowserSessionReference session,
            PermanentShareRequest request, CancellationToken cancellationToken)
        {
            PrepareCalls++;
            throw new InvalidOperationException("Recovery cannot prepare.");
        }

        public Task<BaiduAdapterResult<ShareCreationReceipt>> SubmitPermanentShareForOperationAsync(
            ShareOperationAutomationContext context, ManagedBrowserSessionReference session,
            PermanentShareRequest request, CancellationToken cancellationToken)
        {
            SubmitCalls++;
            throw new InvalidOperationException("Recovery cannot submit.");
        }

        public Task<BaiduAdapterResult<ShareCreationReceipt>> ConfirmExactShareForOperationAsync(
            ShareOperationAutomationContext context, ManagedBrowserSessionReference session,
            PermanentShareRequest request, CancellationToken cancellationToken)
        {
            ConfirmCalls++;
            Assert.True(context.IsRecovery);
            return Task.FromResult(BaiduAdapterResult<ShareCreationReceipt>.Success(new(
                Now, "adapter-request", request, "share-id", "https://pan.baidu.com/s/share-id",
                new EffectiveShareSettings(true, true, ShareExtractionCodeBehavior.Random, "r4nd"))));
        }
    }
}
