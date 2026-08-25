using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class WebView2AutomationSessionTests
{
    [Fact]
    public void SessionImplementsTypedTransferShareAndExportContracts()
    {
        Assert.Contains(typeof(ITransferAutomationSession), typeof(WebView2AutomationSession).GetInterfaces());
        Assert.Contains(typeof(IShareAutomationSession), typeof(WebView2AutomationSession).GetInterfaces());
        Assert.Contains(typeof(IShareResultExportSession), typeof(WebView2AutomationSession).GetInterfaces());
    }

    [Fact]
    public async Task ExactShareSelectionDelegatesObserveAndSingleMutation()
    {
        var identity = Identity();
        var binding = Binding(identity);
        var executor = new RecordingCandidateExecutor();
        await using var session = new WebView2AutomationSession(
            identity, binding, executor, CancellationToken.None);
        var remoteItems = new RemoteItemIdentitySet([new("remote-1", "item")]);

        await ((IShareAutomationSession)session)
            .SelectExactRemoteItemsAsSingleGroupAsync(remoteItems, CancellationToken.None);

        Assert.Equal(1, executor.ObserveCount);
        Assert.Equal(1, executor.MutateCount);
        Assert.Equal(BaiduAutomationAction.SelectExactItems, executor.Seen!.Action);
        Assert.True(executor.Seen.ExpectedExactIds.SequenceEqual(["remote-1"], StringComparer.Ordinal));
        Assert.Same(binding.Binding, executor.Seen.Binding);
    }

    [Fact]
    public async Task UnprovenListingsAndAcceptanceNeverFabricateTypedResults()
    {
        var identity = Identity();
        var executor = new RecordingCandidateExecutor();
        await using var session = new WebView2AutomationSession(
            identity, Binding(identity), executor, CancellationToken.None);

        var transfer = (ITransferAutomationSession)session;
        var share = (IShareAutomationSession)session;
        var transferError = await Assert.ThrowsAsync<WebAutomationAuthorizationException>(() =>
            transfer.OpenExternalShareAsync(
                new ShareReference("share", "https://pan.baidu.com/s/share", null),
                CancellationToken.None));
        var listingError = await Assert.ThrowsAsync<WebAutomationAuthorizationException>(() =>
            share.ReadCompleteStableOfficialShareListAsync(CancellationToken.None));
        var acceptanceError = await Assert.ThrowsAsync<WebAutomationAuthorizationException>(() =>
            share.CreateShareOnceAsync(CancellationToken.None));

        Assert.Equal(BaiduAutomationDenialReason.FieldEvidenceNotValidated, transferError.DenialReason);
        Assert.Equal(BaiduAutomationDenialReason.FieldEvidenceNotValidated, listingError.DenialReason);
        Assert.Equal(BaiduAutomationDenialReason.FieldEvidenceNotValidated, acceptanceError.DenialReason);
        Assert.Equal(0, executor.ObserveCount + executor.MutateCount);
    }

    [Fact]
    public async Task CancellationAndUserCloseFenceTypedMutationBeforeExecutor()
    {
        var identity = Identity();
        var binding = Binding(identity);
        var executor = new RecordingCandidateExecutor();
        using var userClosed = new CancellationTokenSource();
        await using var session = new WebView2AutomationSession(
            identity, binding, executor, userClosed.Token);
        var remoteItems = new RemoteItemIdentitySet([new("remote-1", "item")]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ((IShareAutomationSession)session).SelectExactRemoteItemsAsSingleGroupAsync(
                remoteItems, cancelled.Token));
        userClosed.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ((IShareAutomationSession)session).SelectExactRemoteItemsAsSingleGroupAsync(
                remoteItems, CancellationToken.None));

        Assert.Equal(0, executor.ObserveCount + executor.MutateCount);
    }

    [Fact]
    public async Task UserCloseAfterObservationFencesMutationEvenWhenExecutorIgnoresCancellation()
    {
        var identity = Identity();
        using var userClosed = new CancellationTokenSource();
        var executor = new RecordingCandidateExecutor
        {
            AfterObserve = userClosed.Cancel,
            IgnoreMutationCancellation = true,
        };
        await using var session = new WebView2AutomationSession(
            identity, Binding(identity), executor, userClosed.Token);
        var remoteItems = new RemoteItemIdentitySet([new("remote-1", "item")]);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ((IShareAutomationSession)session).SelectExactRemoteItemsAsSingleGroupAsync(
                remoteItems, CancellationToken.None));

        Assert.Equal(1, executor.ObserveCount);
        Assert.Equal(0, executor.MutateCount);
    }

    [Fact]
    public void MismatchedSessionIdentityCannotReceiveCandidateBinding()
    {
        var identity = Identity();
        var binding = Binding(identity);

        Assert.Throws<InvalidOperationException>(() => new WebView2AutomationSession(
            identity with { OperationId = Guid.NewGuid() },
            binding,
            new RecordingCandidateExecutor(),
            CancellationToken.None));
    }

    private static WebAutomationRequestIdentity Identity() => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        1,
        WebAutomationOwnerKind.StandaloneShare);

    private static BaiduCandidateSessionBinding Binding(WebAutomationRequestIdentity identity)
    {
        var capability = new AdapterCapabilityVersion(
            "BaiduShare", BaiduSelectorCatalog.ProtocolVersion, BaiduSelectorCatalog.SelectorVersion, "build-1");
        var authorization = new AdapterWriteAuthorizationRequest(
            capability, identity.AccountId, identity.OperationId, identity.Generation,
            identity.BatchId, AdapterWriteAction.PrepareShare);
        return new(
            authorization,
            new AdapterWriteAuthorizationBinding(
                AdapterWriteAuthorizationSource.ConsumedLease,
                capability,
                identity.AccountId,
                identity.OperationId,
                identity.Generation,
                identity.BatchId,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(5),
                false,
                null));
    }

    private sealed class RecordingCandidateExecutor : IBaiduCandidateDomExecutor
    {
        public int ObserveCount { get; private set; }
        public int MutateCount { get; private set; }
        public BaiduCandidateDomRequest? Seen { get; private set; }
        public Action? AfterObserve { get; init; }
        public bool IgnoreMutationCancellation { get; init; }

        public Task<BaiduCandidateDomResult> ObserveAsync(
            BaiduCandidateDomRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObserveCount++;
            Seen = request;
            AfterObserve?.Invoke();
            return Task.FromResult(BaiduCandidateDomResult.Success(new(
                request.OfficialUri,
                request.ExpectedPageState,
                1,
                true,
                true,
                request.ExpectedExactIds,
                new('a', 64))));
        }

        public Task<BaiduCandidateDomResult> MutateAsync(
            BaiduCandidateDomRequest request,
            BaiduCandidateDomObservation observation,
            CancellationToken cancellationToken = default)
        {
            if (!IgnoreMutationCancellation) cancellationToken.ThrowIfCancellationRequested();
            MutateCount++;
            Seen = request;
            return Task.FromResult(BaiduCandidateDomResult.Success(observation));
        }
    }
}
