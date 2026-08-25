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

    private sealed class RejectingWriteGate : IAdapterWriteAuthorizationGate
    {
        public ValueTask<bool> TryAuthorizeAsync(
            AdapterWriteAuthorizationRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    }

    private sealed class CountingOperationBoundTransferAdapter : IOperationBoundBaiduNetdiskAdapter
    {
        public int WriteCount { get; private set; }

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

        public Task<BaiduAdapterResult<ShareCreationReceipt>> SubmitPermanentShareForOperationAsync(ShareOperationAutomationContext context, AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken)
        {
            WriteCount++;
            throw new InvalidOperationException("The denied write reached the inner adapter.");
        }

        public Task<BaiduAdapterResult<SharePreparationReceipt>> PreparePermanentShareForOperationAsync(ShareOperationAutomationContext context, AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<ShareCreationReceipt>> ConfirmExactShareForOperationAsync(ShareOperationAutomationContext context, AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<ShareCreationReceipt>> CreatePermanentShareAsync(AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<BaiduSharePage>> ListSharesAsync(AccountSession session, string? cursor, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
