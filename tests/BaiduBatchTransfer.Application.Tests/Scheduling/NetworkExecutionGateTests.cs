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
    }

    private sealed class BindingWriteGate(AdapterCapabilityVersion capability) : IAdapterWriteAuthorizationGate
    {
        public int CreateCount { get; private set; }
        public AdapterWriteAuthorizationRequest? CreatedFor { get; private set; }
        public AdapterWriteAuthorizationRequest? ValidatedFor { get; private set; }
        public AdapterWriteAuthorizationBinding? ValidatedBinding { get; private set; }
        public bool BindingCreatedBeforeInner { get; private set; }

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
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(5),
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
            return ValueTask.FromResult(true);
        }
    }

    private sealed class CountingOperationBoundTransferAdapter : IOperationBoundBaiduNetdiskAdapter
    {
        public int WriteCount { get; private set; }
        public int DirectoryWriteCount { get; private set; }

        public Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryForOperationAsync(TransferOperationAutomationContext context, AccountSession session, RemotePath path, CancellationToken cancellationToken)
        {
            DirectoryWriteCount++;
            return Task.FromResult(BaiduAdapterResult<RemoteDirectory>.Success(new RemoteDirectory("target", path)));
        }

        public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareForOperationAsync(TransferOperationAutomationContext context, AccountSession session, ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken)
        {
            WriteCount++;
            throw new InvalidOperationException("The denied write reached the inner adapter.");
        }

        public Task<BaiduAdapterResult<TransferPreparationReceipt>> PrepareWholeShareForOperationAsync(TransferOperationAutomationContext context, AccountSession session, ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferConfirmationResult>> ConfirmExactTransferForOperationAsync(TransferOperationAutomationContext context, AccountSession session, TransferConfirmationQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<BaiduAccountIdentity>> ValidateSessionAsync(AccountSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<AccountCapacity>> GetCapacityAsync(AccountSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<System.Collections.Immutable.ImmutableArray<RemoteDirectory>>> ListDirectoriesAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<ShareMetadata>> InspectShareAsync(AccountSession session, ShareReference share, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareAsync(AccountSession session, ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferConfirmation>> ConfirmTransferAsync(AccountSession session, TransferConfirmationQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CountingOperationBoundShareAdapter : IOperationBoundBaiduShareAdapter
    {
        public int WriteCount { get; private set; }
        public int PrepareCount { get; private set; }
        public ShareOperationAutomationContext? PreparedContext { get; private set; }

        public Task<BaiduAdapterResult<ShareCreationReceipt>> SubmitPermanentShareForOperationAsync(ShareOperationAutomationContext context, AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken)
        {
            WriteCount++;
            return Task.FromResult(BaiduAdapterResult<ShareCreationReceipt>.Success(new(
                DateTimeOffset.UtcNow,
                "request-id",
                request,
                "share-id",
                "https://pan.baidu.com/s/share-id",
                new(true, true, ShareExtractionCodeBehavior.Random, "a1b2"))));
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
