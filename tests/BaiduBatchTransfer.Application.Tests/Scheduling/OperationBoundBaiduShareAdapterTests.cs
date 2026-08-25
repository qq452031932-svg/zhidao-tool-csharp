using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Tests.Scheduling;

public sealed class OperationBoundBaiduShareAdapterTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(WebAutomationOwnerKind.TransferShare)]
    [InlineData(WebAutomationOwnerKind.StandaloneShare)]
    public void ContextAcceptsOnlyShareOwnersAndRetainsExactItems(WebAutomationOwnerKind ownerKind)
    {
        var exactItems = ExactItems();

        var context = new ShareOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2, ownerKind, exactItems).Validate();

        Assert.Equal(ownerKind, context.OwnerKind);
        Assert.Same(exactItems, context.ExactRemoteItems);
    }

    [Theory]
    [InlineData(WebAutomationOwnerKind.Transfer)]
    [InlineData(WebAutomationOwnerKind.RemoteSelection)]
    public void ContextRejectsNonShareOwners(WebAutomationOwnerKind ownerKind)
    {
        var context = new ShareOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, ownerKind, ExactItems());

        Assert.Throws<ArgumentOutOfRangeException>(() => context.Validate());
    }

    [Fact]
    public void CoordinatedWrapperLeavesLegacyAdapterLegacy()
    {
        var wrapped = CoordinatedBaiduShareAdapter.Wrap(
            new LegacyShareAdapter(), new BaiduAdapterConcurrencyGate());

        Assert.IsType<CoordinatedBaiduShareAdapter>(wrapped);
        Assert.IsNotAssignableFrom<IOperationBoundBaiduShareAdapter>(wrapped);
    }

    [Fact]
    public async Task CoordinatedWrapperPreservesOperationBoundCapabilityAndDelegatesAllBoundPhases()
    {
        var inner = new OperationBoundShareAdapter();
        var wrapped = CoordinatedBaiduShareAdapter.Wrap(inner, new BaiduAdapterConcurrencyGate());
        var operationBound = Assert.IsAssignableFrom<IOperationBoundBaiduShareAdapter>(wrapped);
        var exactItems = ExactItems();
        var accountId = Guid.NewGuid();
        var context = new ShareOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 3,
            WebAutomationOwnerKind.StandaloneShare, exactItems).Validate();
        var session = Session(accountId);
        var request = new PermanentShareRequest(exactItems);

        await operationBound.PreparePermanentShareForOperationAsync(
            context, session, request, CancellationToken.None);
        await operationBound.SubmitPermanentShareForOperationAsync(
            context, session, request, CancellationToken.None);
        await operationBound.ConfirmExactShareForOperationAsync(
            context, session, request, CancellationToken.None);

        Assert.Equal(3, inner.BoundCalls.Count);
        Assert.All(inner.BoundCalls, call =>
        {
            Assert.Same(context, call.Context);
            Assert.Same(session, call.Session);
            Assert.Same(request, call.Request);
        });
        Assert.Equal(["prepare", "submit", "confirm"], inner.BoundCalls.Select(call => call.Phase));
    }

    private static RemoteItemIdentitySet ExactItems() =>
        new([new RemoteItemIdentity("remote-exact-id", "Private display name")]);

    private static AccountSession Session(Guid accountId) =>
        new(accountId, "baidu-user", [new SessionCookie("BDUSS", "secret", ".baidu.com", "/", null, true, true)], Now);

    private class LegacyShareAdapter : IBaiduShareAdapter
    {
        public Task<BaiduAdapterResult<ShareCreationReceipt>> CreatePermanentShareAsync(
            AccountSession session,
            PermanentShareRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result(request));

        public Task<BaiduAdapterResult<BaiduSharePage>> ListSharesAsync(
            AccountSession session,
            string? cursor,
            CancellationToken cancellationToken) =>
            Task.FromResult(BaiduAdapterResult<BaiduSharePage>.Success(new([], null)));

        protected static BaiduAdapterResult<ShareCreationReceipt> Result(PermanentShareRequest request) =>
            BaiduAdapterResult<ShareCreationReceipt>.Success(new(
                Now,
                "adapter-request",
                request,
                "share-id",
                "https://pan.baidu.com/s/share-id",
                new(true, true, ShareExtractionCodeBehavior.Random, "a1b2")));
    }

    private sealed class OperationBoundShareAdapter : LegacyShareAdapter, IOperationBoundBaiduShareAdapter
    {
        public List<(string Phase, ShareOperationAutomationContext Context, AccountSession Session,
            PermanentShareRequest Request)> BoundCalls { get; } = [];

        public Task<BaiduAdapterResult<SharePreparationReceipt>> PreparePermanentShareForOperationAsync(
            ShareOperationAutomationContext context,
            AccountSession session,
            PermanentShareRequest request,
            CancellationToken cancellationToken)
        {
            BoundCalls.Add(("prepare", context, session, request));
            return Task.FromResult(BaiduAdapterResult<SharePreparationReceipt>.Success(SharePreparationReceipt.Ready));
        }

        public Task<BaiduAdapterResult<ShareCreationReceipt>> SubmitPermanentShareForOperationAsync(
            ShareOperationAutomationContext context,
            AccountSession session,
            PermanentShareRequest request,
            CancellationToken cancellationToken)
        {
            BoundCalls.Add(("submit", context, session, request));
            return Task.FromResult(Result(request));
        }

        public Task<BaiduAdapterResult<ShareCreationReceipt>> ConfirmExactShareForOperationAsync(
            ShareOperationAutomationContext context,
            AccountSession session,
            PermanentShareRequest request,
            CancellationToken cancellationToken)
        {
            BoundCalls.Add(("confirm", context, session, request));
            return Task.FromResult(Result(request));
        }
    }
}
