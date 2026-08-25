using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Tests.Scheduling;

public sealed class NetworkExecutionGateTests
{
    [Fact]
    public void RecoveryRequiresExplicitUserConfirmationBeforeNewWork()
    {
        var gate = new NetworkExecutionGate(initiallyOnline: true);
        gate.ReportOffline();
        gate.ReportNetworkAvailable();

        Assert.False(gate.CanAcquireNewWork);

        gate.ConfirmResume();
        Assert.True(gate.CanAcquireNewWork);
    }

    [Fact]
    public async Task UnacceptedOperationBoundTransferWriteDoesNotReachInnerAdapter()
    {
        var inner = new CountingOperationBoundTransferAdapter();
        var wrapped = Assert.IsAssignableFrom<IOperationBoundBaiduNetdiskAdapter>(
            CoordinatedBaiduNetdiskAdapter.Wrap(
                inner,
                new BaiduAdapterConcurrencyGate(),
                new RejectingWriteGate(),
                new AdapterCapabilityVersion("BaiduTransfer", "p1", "s1", "b1")));
        var accountId = Guid.NewGuid();
        var share = new ShareReference("share", "https://pan.baidu.com/s/share", null);
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 1, share);

        var result = await wrapped.SaveWholeShareForOperationAsync(
            context,
            new AccountSession(
                accountId,
                "baidu-user",
                [new SessionCookie("BDUSS", "private", ".baidu.com", "/", null, true, true)],
                DateTimeOffset.UtcNow),
            share,
            new RemoteDirectory("root", new RemotePath("/")),
            DuplicatePolicy.Skip,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduErrorKind.NotValidated, result.ErrorKind);
        Assert.Equal(0, inner.WriteCount);
    }

    [Fact]
    public async Task OperationBoundDirectoryWriteUsesExactOperationIdentity()
    {
        var inner = new CountingOperationBoundTransferAdapter();
        var gate = new ExactRequestWriteGate();
        var wrapped = Assert.IsAssignableFrom<IOperationBoundBaiduNetdiskAdapter>(
            CoordinatedBaiduNetdiskAdapter.Wrap(
                inner,
                new BaiduAdapterConcurrencyGate(),
                gate,
                new AdapterCapabilityVersion("BaiduTransfer", "p1", "s1", "b1")));
        var accountId = Guid.NewGuid();
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 3,
            new ShareReference("share", "https://pan.baidu.com/s/share", null));
        var session = new AccountSession(
            accountId, "baidu-user",
            [new SessionCookie("BDUSS", "private", ".baidu.com", "/", null, true, true)],
            DateTimeOffset.UtcNow);

        await wrapped.EnsureDirectoryForOperationAsync(
            context, session, new RemotePath("/target"), CancellationToken.None);

        Assert.Equal(context.OperationId, gate.Seen!.OperationId);
        Assert.Equal(context.Generation, gate.Seen.Generation);
        Assert.Equal(accountId, gate.Seen.AccountId);
        Assert.Equal(1, inner.DirectoryWriteCount);
    }

    [Fact]
    public async Task TransferEnsurePrepareSubmitFreezeOneExactBindingBeforeEachInnerCall()
    {
        var inner = new CountingOperationBoundTransferAdapter();
        var capability = new AdapterCapabilityVersion("BaiduTransfer", "p1", "s1", "b1");
        var gate = new BindingWriteGate(capability);
        var wrapped = Assert.IsAssignableFrom<IOperationBoundBaiduNetdiskAdapter>(
            CoordinatedBaiduNetdiskAdapter.Wrap(
                inner, new BaiduAdapterConcurrencyGate(), gate, capability));
        var accountId = Guid.NewGuid();
        var share = new ShareReference("share", "https://pan.baidu.com/s/share", null);
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 1, share);
        var session = new AccountSession(
            accountId, "baidu-user",
            [new SessionCookie("BDUSS", "private", ".baidu.com", "/", null, true, true)],
            DateTimeOffset.UtcNow);

        var ensured = await wrapped.EnsureDirectoryForOperationAsync(
            context, session, new RemotePath("/target"), CancellationToken.None);
        var prepared = await wrapped.PrepareWholeShareForOperationAsync(
            context, session, share, ensured.Value!, DuplicatePolicy.Skip, CancellationToken.None);
        var submitted = await wrapped.SaveWholeShareForOperationAsync(
            context, session, share, ensured.Value!, DuplicatePolicy.Skip, CancellationToken.None);

        Assert.True(ensured.IsSuccess);
        Assert.True(prepared.IsSuccess);
        Assert.True(submitted.IsSuccess);
        Assert.Equal(1, gate.CreateCount);
        Assert.Equal(AdapterWriteAction.EnsureDirectory, gate.CreatedFor!.Action);
        Assert.Equal(
            [AdapterWriteAction.PrepareTransfer, AdapterWriteAction.SubmitTransfer],
            gate.ValidatedRequests.Select(request => request.Action));
        var binding = Assert.IsType<AdapterWriteAuthorizationBinding>(inner.EnsureContext!.AuthorizationBinding);
        Assert.Same(binding, inner.PreparedContext!.AuthorizationBinding);
        Assert.Same(binding, inner.SubmitContext!.AuthorizationBinding);
        Assert.All(gate.ValidatedBindings, value => Assert.Same(binding, value));
        Assert.Equal(1, gate.ReleaseCount);
    }

    [Fact]
    public async Task TransferBindingRejectsCrossIdentityAndIsRemovedAfterPrepareFailure()
    {
        var inner = new CountingOperationBoundTransferAdapter { FailPrepare = true };
        var capability = new AdapterCapabilityVersion("BaiduTransfer", "p1", "s1", "b1");
        var gate = new BindingWriteGate(capability);
        var wrapped = Assert.IsAssignableFrom<IOperationBoundBaiduNetdiskAdapter>(
            CoordinatedBaiduNetdiskAdapter.Wrap(
                inner, new BaiduAdapterConcurrencyGate(), gate, capability));
        var accountId = Guid.NewGuid();
        var share = new ShareReference("share", "https://pan.baidu.com/s/share", null);
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 1, share);
        var session = new AccountSession(
            accountId, "baidu-user",
            [new SessionCookie("BDUSS", "private", ".baidu.com", "/", null, true, true)],
            DateTimeOffset.UtcNow);
        var destination = new RemoteDirectory("target", new RemotePath("/target"));

        Assert.True((await wrapped.EnsureDirectoryForOperationAsync(
            context, session, destination.Path, CancellationToken.None)).IsSuccess);
        var cross = await wrapped.PrepareWholeShareForOperationAsync(
            context with { OperationId = Guid.NewGuid() }, session, share, destination,
            DuplicatePolicy.Skip, CancellationToken.None);
        var failed = await wrapped.PrepareWholeShareForOperationAsync(
            context, session, share, destination, DuplicatePolicy.Skip, CancellationToken.None);
        var submit = await wrapped.SaveWholeShareForOperationAsync(
            context, session, share, destination, DuplicatePolicy.Skip, CancellationToken.None);

        Assert.Equal(BaiduErrorKind.NotValidated, cross.ErrorKind);
        Assert.Equal(BaiduErrorKind.NotValidated, failed.ErrorKind);
        Assert.Equal(BaiduErrorKind.NotValidated, submit.ErrorKind);
        Assert.Equal(1, inner.PrepareCount);
        Assert.Equal(0, inner.WriteCount);
    }

    [Fact]
    public async Task FailedEnsureReleasesBindingAndCannotReachPrepare()
    {
        var inner = new CountingOperationBoundTransferAdapter { FailEnsure = true };
        var capability = new AdapterCapabilityVersion("BaiduTransfer", "p1", "s1", "b1");
        var gate = new BindingWriteGate(capability);
        var wrapped = Assert.IsAssignableFrom<IOperationBoundBaiduNetdiskAdapter>(
            CoordinatedBaiduNetdiskAdapter.Wrap(
                inner, new BaiduAdapterConcurrencyGate(), gate, capability));
        var accountId = Guid.NewGuid();
        var share = new ShareReference("share", "https://pan.baidu.com/s/share", null);
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 1, share);
        var session = new AccountSession(
            accountId, "baidu-user",
            [new SessionCookie("BDUSS", "private", ".baidu.com", "/", null, true, true)],
            DateTimeOffset.UtcNow);
        var destination = new RemoteDirectory("target", new RemotePath("/target"));

        var ensured = await wrapped.EnsureDirectoryForOperationAsync(
            context, session, destination.Path, CancellationToken.None);
        var prepared = await wrapped.PrepareWholeShareForOperationAsync(
            context, session, share, destination, DuplicatePolicy.Skip, CancellationToken.None);

        Assert.Equal(BaiduErrorKind.NotValidated, ensured.ErrorKind);
        Assert.Equal(BaiduErrorKind.NotValidated, prepared.ErrorKind);
        Assert.Equal(1, gate.ReleaseCount);
        Assert.Equal(0, inner.PrepareCount);
    }

    [Fact]
    public async Task CancelledEnsureReleasesBindingAndCannotReachPrepare()
    {
        var inner = new CountingOperationBoundTransferAdapter { CancelEnsure = true };
        var capability = new AdapterCapabilityVersion("BaiduTransfer", "p1", "s1", "b1");
        var gate = new BindingWriteGate(capability);
        var wrapped = Assert.IsAssignableFrom<IOperationBoundBaiduNetdiskAdapter>(
            CoordinatedBaiduNetdiskAdapter.Wrap(
                inner, new BaiduAdapterConcurrencyGate(), gate, capability));
        var accountId = Guid.NewGuid();
        var share = new ShareReference("share", "https://pan.baidu.com/s/share", null);
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 1, share);
        var session = new AccountSession(
            accountId, "baidu-user",
            [new SessionCookie("BDUSS", "private", ".baidu.com", "/", null, true, true)],
            DateTimeOffset.UtcNow);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            wrapped.EnsureDirectoryForOperationAsync(
                context, session, new RemotePath("/target"), CancellationToken.None));
        var prepared = await wrapped.PrepareWholeShareForOperationAsync(
            context, session, share, new RemoteDirectory("target", new RemotePath("/target")),
            DuplicatePolicy.Skip, CancellationToken.None);

        Assert.Equal(BaiduErrorKind.NotValidated, prepared.ErrorKind);
        Assert.Equal(1, gate.ReleaseCount);
        Assert.Equal(0, inner.PrepareCount);
    }

    [Fact]
    public async Task CancelledPrepareReleasesBindingAndCannotReachSubmit()
    {
        var inner = new CountingOperationBoundTransferAdapter { CancelPrepare = true };
        var capability = new AdapterCapabilityVersion("BaiduTransfer", "p1", "s1", "b1");
        var gate = new BindingWriteGate(capability);
        var wrapped = Assert.IsAssignableFrom<IOperationBoundBaiduNetdiskAdapter>(
            CoordinatedBaiduNetdiskAdapter.Wrap(
                inner, new BaiduAdapterConcurrencyGate(), gate, capability));
        var accountId = Guid.NewGuid();
        var share = new ShareReference("share", "https://pan.baidu.com/s/share", null);
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 1, share);
        var session = new AccountSession(
            accountId, "baidu-user",
            [new SessionCookie("BDUSS", "private", ".baidu.com", "/", null, true, true)],
            DateTimeOffset.UtcNow);
        var destination = new RemoteDirectory("target", new RemotePath("/target"));
        Assert.True((await wrapped.EnsureDirectoryForOperationAsync(
            context, session, destination.Path, CancellationToken.None)).IsSuccess);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            wrapped.PrepareWholeShareForOperationAsync(
                context, session, share, destination, DuplicatePolicy.Skip, CancellationToken.None));
        var submitted = await wrapped.SaveWholeShareForOperationAsync(
            context, session, share, destination, DuplicatePolicy.Skip, CancellationToken.None);

        Assert.Equal(BaiduErrorKind.NotValidated, submitted.ErrorKind);
        Assert.Equal(1, gate.ReleaseCount);
        Assert.Equal(0, inner.WriteCount);
    }

    [Fact]
    public async Task DuplicatePrepareWaitingBehindOwnerCannotReleaseTheHealthyBinding()
    {
        var inner = new CountingOperationBoundTransferAdapter { BlockFirstPrepare = true };
        var capability = new AdapterCapabilityVersion("BaiduTransfer", "p1", "s1", "b1");
        var gate = new BindingWriteGate(capability) { RejectDuplicateActions = true };
        var wrapped = Assert.IsAssignableFrom<IOperationBoundBaiduNetdiskAdapter>(
            CoordinatedBaiduNetdiskAdapter.Wrap(
                inner, new BaiduAdapterConcurrencyGate(), gate, capability));
        var accountId = Guid.NewGuid();
        var share = new ShareReference("share", "https://pan.baidu.com/s/share", null);
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 1, share);
        var session = new AccountSession(
            accountId, "baidu-user",
            [new SessionCookie("BDUSS", "private", ".baidu.com", "/", null, true, true)],
            DateTimeOffset.UtcNow);
        var destination = new RemoteDirectory("target", new RemotePath("/target"));
        Assert.True((await wrapped.EnsureDirectoryForOperationAsync(
            context, session, destination.Path, CancellationToken.None)).IsSuccess);

        var owner = wrapped.PrepareWholeShareForOperationAsync(
            context, session, share, destination, DuplicatePolicy.Skip, CancellationToken.None);
        await inner.PrepareEntered;
        var duplicate = wrapped.PrepareWholeShareForOperationAsync(
            context, session, share, destination, DuplicatePolicy.Skip, CancellationToken.None);
        inner.AllowPrepareToComplete();

        Assert.True((await owner).IsSuccess);
        Assert.Equal(BaiduErrorKind.NotValidated, (await duplicate).ErrorKind);
        Assert.True((await wrapped.SaveWholeShareForOperationAsync(
            context, session, share, destination, DuplicatePolicy.Skip, CancellationToken.None)).IsSuccess);
        Assert.Equal(1, inner.PrepareCount);
        Assert.Equal(1, inner.WriteCount);
        Assert.Equal(1, gate.ReleaseCount);
    }

    [Fact]
    public async Task CancelledPrepareWaiterCannotReleaseTheOwnersHealthyBinding()
    {
        var inner = new CountingOperationBoundTransferAdapter { BlockFirstPrepare = true };
        var capability = new AdapterCapabilityVersion("BaiduTransfer", "p1", "s1", "b1");
        var gate = new BindingWriteGate(capability);
        var wrapped = Assert.IsAssignableFrom<IOperationBoundBaiduNetdiskAdapter>(
            CoordinatedBaiduNetdiskAdapter.Wrap(
                inner, new BaiduAdapterConcurrencyGate(), gate, capability));
        var accountId = Guid.NewGuid();
        var share = new ShareReference("share", "https://pan.baidu.com/s/share", null);
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 1, share);
        var session = new AccountSession(
            accountId, "baidu-user",
            [new SessionCookie("BDUSS", "private", ".baidu.com", "/", null, true, true)],
            DateTimeOffset.UtcNow);
        var destination = new RemoteDirectory("target", new RemotePath("/target"));
        Assert.True((await wrapped.EnsureDirectoryForOperationAsync(
            context, session, destination.Path, CancellationToken.None)).IsSuccess);

        var owner = wrapped.PrepareWholeShareForOperationAsync(
            context, session, share, destination, DuplicatePolicy.Skip, CancellationToken.None);
        await inner.PrepareEntered;
        using var cancellation = new CancellationTokenSource();
        var waiter = wrapped.PrepareWholeShareForOperationAsync(
            context, session, share, destination, DuplicatePolicy.Skip, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        inner.AllowPrepareToComplete();
        Assert.True((await owner).IsSuccess);
        Assert.True((await wrapped.SaveWholeShareForOperationAsync(
            context, session, share, destination, DuplicatePolicy.Skip, CancellationToken.None)).IsSuccess);
        Assert.Equal(1, inner.PrepareCount);
        Assert.Equal(1, inner.WriteCount);
        Assert.Equal(1, gate.ReleaseCount);
    }

    [Fact]
    public async Task AbandonedSuccessfulEnsureIsPrunedAtOriginalExpiryWithoutRenewal()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var inner = new CountingOperationBoundTransferAdapter();
        var capability = new AdapterCapabilityVersion("BaiduTransfer", "p1", "s1", "b1");
        var gate = new BindingWriteGate(capability, clock) { BindingLifetime = TimeSpan.FromMinutes(1) };
        var wrapped = new CoordinatedOperationBoundBaiduNetdiskAdapter(
            inner, new BaiduAdapterConcurrencyGate(), gate, capability, clock);
        var accountId = Guid.NewGuid();
        var share = new ShareReference("share", "https://pan.baidu.com/s/share", null);
        var abandoned = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 1, share);
        var next = abandoned with { OperationId = Guid.NewGuid(), Generation = 2 };
        var session = new AccountSession(
            accountId, "baidu-user",
            [new SessionCookie("BDUSS", "private", ".baidu.com", "/", null, true, true)],
            DateTimeOffset.UtcNow);

        Assert.True((await wrapped.EnsureDirectoryForOperationAsync(
            abandoned, session, new RemotePath("/first"), CancellationToken.None)).IsSuccess);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True((await wrapped.EnsureDirectoryForOperationAsync(
            next, session, new RemotePath("/next"), CancellationToken.None)).IsSuccess);
        var stalePrepare = await wrapped.PrepareWholeShareForOperationAsync(
            abandoned, session, share, new RemoteDirectory("first", new RemotePath("/first")),
            DuplicatePolicy.Skip, CancellationToken.None);

        Assert.Equal(BaiduErrorKind.NotValidated, stalePrepare.ErrorKind);
        Assert.Equal(1, gate.ReleaseCount);
        Assert.Equal(2, gate.CreateCount);
    }

    [Fact]
    public async Task UnacceptedOperationBoundShareWriteDoesNotReachInnerAdapter()
    {
        var inner = new CountingOperationBoundShareAdapter();
        var wrapped = Assert.IsAssignableFrom<IOperationBoundBaiduShareAdapter>(
            CoordinatedBaiduShareAdapter.Wrap(
                inner,
                new BaiduAdapterConcurrencyGate(),
                new RejectingWriteGate(),
                new AdapterCapabilityVersion("BaiduShare", "p1", "s1", "b1")));
        var accountId = Guid.NewGuid();
        var items = new RemoteItemIdentitySet([new("remote-1", "item")]);
        var context = new ShareOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 1,
            WebAutomationOwnerKind.StandaloneShare, items);

        var result = await wrapped.SubmitPermanentShareForOperationAsync(
            context,
            new AccountSession(
                accountId,
                "baidu-user",
                [new SessionCookie("BDUSS", "private", ".baidu.com", "/", null, true, true)],
                DateTimeOffset.UtcNow),
            new PermanentShareRequest(items),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduErrorKind.NotValidated, result.ErrorKind);
        Assert.Equal(0, inner.WriteCount);
    }

    [Fact]
    public async Task SharePreparationObtainsAndInjectsImmutableAuthorizationBeforeInnerAdapter()
    {
        var inner = new CountingOperationBoundShareAdapter();
        var capability = new AdapterCapabilityVersion("BaiduShare", "p1", "s1", "b1");
        var gate = new BindingWriteGate(capability);
        var wrapped = Assert.IsAssignableFrom<IOperationBoundBaiduShareAdapter>(
            CoordinatedBaiduShareAdapter.Wrap(
                inner, new BaiduAdapterConcurrencyGate(), gate, capability));
        var accountId = Guid.NewGuid();
        var items = new RemoteItemIdentitySet([new("remote-1", "item")]);
        var context = new ShareOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 1,
            WebAutomationOwnerKind.StandaloneShare, items);
        var session = new AccountSession(
            accountId, "baidu-user",
            [new SessionCookie("BDUSS", "private", ".baidu.com", "/", null, true, true)],
            DateTimeOffset.UtcNow);

        var prepared = await wrapped.PreparePermanentShareForOperationAsync(
            context, session, new PermanentShareRequest(items), CancellationToken.None);

        Assert.True(prepared.IsSuccess);
        Assert.Equal(AdapterWriteAction.PrepareShare, gate.CreatedFor!.Action);
        var binding = Assert.IsType<AdapterWriteAuthorizationBinding>(inner.PreparedContext!.AuthorizationBinding);
        Assert.Equal(AdapterWriteAuthorizationSource.ConsumedLease, binding.Source);
        Assert.Equal(context.OperationId, binding.OperationId);
        Assert.Equal(capability, binding.Capability);
        Assert.True(gate.BindingCreatedBeforeInner);

        var submitted = await wrapped.SubmitPermanentShareForOperationAsync(
            context, session, new PermanentShareRequest(items), CancellationToken.None);

        Assert.True(submitted.IsSuccess);
        Assert.Equal(AdapterWriteAction.SubmitShare, gate.ValidatedFor!.Action);
        Assert.Same(binding, gate.ValidatedBinding);
        Assert.Equal(1, gate.CreateCount);
        Assert.Equal(1, gate.ReleaseCount);
    }

    [Fact]
    public async Task CancelledShareSubmitWaiterCannotReleaseTheActiveOwnerBinding()
    {
        var inner = new CountingOperationBoundShareAdapter { BlockFirstSubmit = true };
        var capability = new AdapterCapabilityVersion("BaiduShare", "p1", "s1", "b1");
        var gate = new BindingWriteGate(capability);
        var wrapped = Assert.IsAssignableFrom<IOperationBoundBaiduShareAdapter>(
            CoordinatedBaiduShareAdapter.Wrap(
                inner, new BaiduAdapterConcurrencyGate(), gate, capability));
        var accountId = Guid.NewGuid();
        var items = new RemoteItemIdentitySet([new("remote-1", "item")]);
        var context = new ShareOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 1,
            WebAutomationOwnerKind.StandaloneShare, items);
        var session = new AccountSession(
            accountId, "baidu-user",
            [new SessionCookie("BDUSS", "private", ".baidu.com", "/", null, true, true)],
            DateTimeOffset.UtcNow);
        var request = new PermanentShareRequest(items);
        Assert.True((await wrapped.PreparePermanentShareForOperationAsync(
            context, session, request, CancellationToken.None)).IsSuccess);

        var owner = wrapped.SubmitPermanentShareForOperationAsync(
            context, session, request, CancellationToken.None);
        await inner.SubmitEntered;
        using var cancellation = new CancellationTokenSource();
        var waiter = wrapped.SubmitPermanentShareForOperationAsync(
            context, session, request, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.Equal(0, gate.ReleaseCount);
        inner.AllowSubmitToComplete();
        Assert.True((await owner).IsSuccess);
        Assert.Equal(1, gate.ReleaseCount);
    }

    [Fact]
    public async Task DeniedShareSubmitDoesNotReleaseBindingNeededByTheStageOwner()
    {
        var inner = new CountingOperationBoundShareAdapter();
        var capability = new AdapterCapabilityVersion("BaiduShare", "p1", "s1", "b1");
        var gate = new BindingWriteGate(capability);
        gate.ValidationResults.Enqueue(false);
        gate.ValidationResults.Enqueue(true);
        var wrapped = Assert.IsAssignableFrom<IOperationBoundBaiduShareAdapter>(
            CoordinatedBaiduShareAdapter.Wrap(
                inner, new BaiduAdapterConcurrencyGate(), gate, capability));
        var accountId = Guid.NewGuid();
        var items = new RemoteItemIdentitySet([new("remote-1", "item")]);
        var context = new ShareOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 1,
            WebAutomationOwnerKind.StandaloneShare, items);
        var session = new AccountSession(
            accountId, "baidu-user",
            [new SessionCookie("BDUSS", "private", ".baidu.com", "/", null, true, true)],
            DateTimeOffset.UtcNow);
        var request = new PermanentShareRequest(items);
        Assert.True((await wrapped.PreparePermanentShareForOperationAsync(
            context, session, request, CancellationToken.None)).IsSuccess);

        var denied = await wrapped.SubmitPermanentShareForOperationAsync(
            context, session, request, CancellationToken.None);
        var owned = await wrapped.SubmitPermanentShareForOperationAsync(
            context, session, request, CancellationToken.None);

        Assert.Equal(BaiduErrorKind.NotValidated, denied.ErrorKind);
        Assert.True(owned.IsSuccess);
        Assert.Equal(1, inner.WriteCount);
        Assert.Equal(1, gate.ReleaseCount);
    }

    [Fact]
    public async Task DeniedSharePreparationDoesNotReachInnerAdapter()
    {
        var inner = new CountingOperationBoundShareAdapter();
        var capability = new AdapterCapabilityVersion("BaiduShare", "p1", "s1", "b1");
        var wrapped = Assert.IsAssignableFrom<IOperationBoundBaiduShareAdapter>(
            CoordinatedBaiduShareAdapter.Wrap(
                inner, new BaiduAdapterConcurrencyGate(), new RejectingWriteGate(), capability));
        var accountId = Guid.NewGuid();
        var items = new RemoteItemIdentitySet([new("remote-1", "item")]);
        var context = new ShareOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 1,
            WebAutomationOwnerKind.StandaloneShare, items);

        var result = await wrapped.PreparePermanentShareForOperationAsync(
            context,
            new AccountSession(
                accountId, "baidu-user",
                [new SessionCookie("BDUSS", "private", ".baidu.com", "/", null, true, true)],
                DateTimeOffset.UtcNow),
            new PermanentShareRequest(items),
            CancellationToken.None);

        Assert.Equal(BaiduErrorKind.NotValidated, result.ErrorKind);
        Assert.Equal(0, inner.PrepareCount);
    }

    private sealed class RejectingWriteGate : IAdapterWriteAuthorizationGate
    {
        public ValueTask<bool> TryAuthorizeAsync(
            AdapterWriteAuthorizationRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    }

    private sealed class ExactRequestWriteGate : IAdapterWriteAuthorizationGate
    {
        public AdapterWriteAuthorizationRequest? Seen { get; private set; }
        public ValueTask<bool> TryAuthorizeAsync(AdapterWriteAuthorizationRequest request, CancellationToken cancellationToken = default)
        {
            Seen = request;
            return ValueTask.FromResult(true);
        }

        public ValueTask<AdapterWriteAuthorizationBinding?> TryCreateBindingAsync(
            AdapterWriteAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            Seen = request;
            return ValueTask.FromResult<AdapterWriteAuthorizationBinding?>(new(
                AdapterWriteAuthorizationSource.ConsumedLease,
                request.Capability,
                request.AccountId,
                request.OperationId,
                request.Generation,
                request.BatchId,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(5),
                request.IsRecovery,
                null));
        }
    }

    private sealed class BindingWriteGate(
        AdapterCapabilityVersion capability,
        TimeProvider? timeProvider = null) : IAdapterWriteAuthorizationGate
    {
        private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
        public int CreateCount { get; private set; }
        public AdapterWriteAuthorizationRequest? CreatedFor { get; private set; }
        public AdapterWriteAuthorizationRequest? ValidatedFor { get; private set; }
        public AdapterWriteAuthorizationBinding? ValidatedBinding { get; private set; }
        public List<AdapterWriteAuthorizationRequest> ValidatedRequests { get; } = [];
        public List<AdapterWriteAuthorizationBinding> ValidatedBindings { get; } = [];
        public bool BindingCreatedBeforeInner { get; private set; }
        public int ReleaseCount { get; private set; }
        public TimeSpan BindingLifetime { get; init; } = TimeSpan.FromMinutes(5);
        public bool RejectDuplicateActions { get; init; }
        public Queue<bool> ValidationResults { get; } = [];
        private readonly HashSet<AdapterWriteAction> validatedActions = [];

        public ValueTask<bool> TryAuthorizeAsync(AdapterWriteAuthorizationRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);

        public ValueTask<AdapterWriteAuthorizationBinding?> TryCreateBindingAsync(
            AdapterWriteAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            CreateCount++;
            CreatedFor = request;
            BindingCreatedBeforeInner = true;
            return ValueTask.FromResult<AdapterWriteAuthorizationBinding?>(new(
                AdapterWriteAuthorizationSource.ConsumedLease,
                capability,
                request.AccountId,
                request.OperationId,
                request.Generation,
                request.BatchId,
                timeProvider.GetUtcNow(),
                timeProvider.GetUtcNow() + BindingLifetime,
                request.IsRecovery,
                null));
        }

        public ValueTask<bool> TryValidateBindingAsync(
            AdapterWriteAuthorizationRequest request,
            AdapterWriteAuthorizationBinding binding,
            CancellationToken cancellationToken = default)
        {
            ValidatedFor = request;
            ValidatedBinding = binding;
            ValidatedRequests.Add(request);
            ValidatedBindings.Add(binding);
            return ValueTask.FromResult(
                ValidationResults.TryDequeue(out var result)
                    ? result
                    : !RejectDuplicateActions || validatedActions.Add(request.Action));
        }

        public ValueTask ReleaseBindingAsync(
            AdapterWriteAuthorizationBinding binding,
            CancellationToken cancellationToken = default)
        {
            ReleaseCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingOperationBoundTransferAdapter : IOperationBoundBaiduNetdiskAdapter
    {
        public int WriteCount { get; private set; }
        public int DirectoryWriteCount { get; private set; }
        public int PrepareCount { get; private set; }
        public bool FailPrepare { get; init; }
        public bool FailEnsure { get; init; }
        public bool CancelEnsure { get; init; }
        public bool CancelPrepare { get; init; }
        public bool BlockFirstPrepare { get; init; }
        private readonly TaskCompletionSource prepareEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource allowPrepare = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TransferOperationAutomationContext? EnsureContext { get; private set; }
        public TransferOperationAutomationContext? PreparedContext { get; private set; }
        public TransferOperationAutomationContext? SubmitContext { get; private set; }

        public Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryForOperationAsync(TransferOperationAutomationContext context, AccountSession session, RemotePath path, CancellationToken cancellationToken)
        {
            DirectoryWriteCount++;
            EnsureContext = context;
            if (CancelEnsure)
                return Task.FromCanceled<BaiduAdapterResult<RemoteDirectory>>(
                    new CancellationToken(canceled: true));
            return Task.FromResult(FailEnsure
                ? BaiduAdapterResult<RemoteDirectory>.Failure(
                    BaiduErrorKind.NotValidated, adapterDiagnostic: null)
                : BaiduAdapterResult<RemoteDirectory>.Success(new RemoteDirectory("target", path)));
        }

        public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareForOperationAsync(TransferOperationAutomationContext context, AccountSession session, ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken)
        {
            WriteCount++;
            SubmitContext = context;
            return Task.FromResult(BaiduAdapterResult<TransferReceipt>.Success(
                new TransferReceipt(DateTimeOffset.UtcNow, "request")));
        }

        public Task PrepareEntered => prepareEntered.Task;

        public void AllowPrepareToComplete() => allowPrepare.TrySetResult();

        public async Task<BaiduAdapterResult<TransferPreparationReceipt>> PrepareWholeShareForOperationAsync(TransferOperationAutomationContext context, AccountSession session, ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken)
        {
            PrepareCount++;
            PreparedContext = context;
            if (BlockFirstPrepare && PrepareCount == 1)
            {
                prepareEntered.TrySetResult();
                await allowPrepare.Task.WaitAsync(cancellationToken);
            }
            if (CancelPrepare)
                throw new OperationCanceledException(new CancellationToken(canceled: true));
            return FailPrepare
                ? BaiduAdapterResult<TransferPreparationReceipt>.Failure(
                    BaiduErrorKind.NotValidated, adapterDiagnostic: null)
                : BaiduAdapterResult<TransferPreparationReceipt>.Success(TransferPreparationReceipt.Ready);
        }
        public Task<BaiduAdapterResult<TransferConfirmationResult>> ConfirmExactTransferForOperationAsync(TransferOperationAutomationContext context, AccountSession session, TransferConfirmationQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<BaiduAccountIdentity>> ValidateSessionAsync(AccountSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<AccountCapacity>> GetCapacityAsync(AccountSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<System.Collections.Immutable.ImmutableArray<RemoteDirectory>>> ListDirectoriesAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<ShareMetadata>> InspectShareAsync(AccountSession session, ShareReference share, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareAsync(AccountSession session, ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferConfirmation>> ConfirmTransferAsync(AccountSession session, TransferConfirmationQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MutableTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private DateTimeOffset current = value;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan value) => current += value;
    }

    private sealed class CountingOperationBoundShareAdapter : IOperationBoundBaiduShareAdapter
    {
        public int WriteCount { get; private set; }
        public int PrepareCount { get; private set; }
        public bool BlockFirstSubmit { get; init; }
        private readonly TaskCompletionSource submitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource allowSubmit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ShareOperationAutomationContext? PreparedContext { get; private set; }

        public Task SubmitEntered => submitEntered.Task;

        public void AllowSubmitToComplete() => allowSubmit.TrySetResult();

        public async Task<BaiduAdapterResult<ShareCreationReceipt>> SubmitPermanentShareForOperationAsync(ShareOperationAutomationContext context, AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken)
        {
            WriteCount++;
            if (BlockFirstSubmit && WriteCount == 1)
            {
                submitEntered.TrySetResult();
                await allowSubmit.Task.WaitAsync(cancellationToken);
            }
            return BaiduAdapterResult<ShareCreationReceipt>.Success(new(
                DateTimeOffset.UtcNow,
                "request-id",
                request,
                "share-id",
                "https://pan.baidu.com/s/share-id",
                new(true, true, ShareExtractionCodeBehavior.Random, "a1b2")));
        }

        public Task<BaiduAdapterResult<SharePreparationReceipt>> PreparePermanentShareForOperationAsync(ShareOperationAutomationContext context, AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken)
        {
            PrepareCount++;
            PreparedContext = context;
            return Task.FromResult(BaiduAdapterResult<SharePreparationReceipt>.Success(SharePreparationReceipt.Ready));
        }
        public Task<BaiduAdapterResult<ShareCreationReceipt>> ConfirmExactShareForOperationAsync(ShareOperationAutomationContext context, AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<ShareCreationReceipt>> CreatePermanentShareAsync(AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<BaiduSharePage>> ListSharesAsync(AccountSession session, string? cursor, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
