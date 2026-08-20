using BaiduBatchTransfer.BaiduAdapter.Http;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.Tests;

public sealed class LiveAdapterAvailabilityTests
{
    [Fact]
    public void LiveCapabilityIsExplicitAndKeepsTransferAndShareDisabled()
    {
        var capability = LiveBaiduNetdiskAdapter.Capability;

        Assert.False(capability.TransferValidated);
        Assert.False(capability.ShareValidated);
        Assert.Equal("百度转存和分享能力尚未通过现场验收。", capability.SafeReason);
    }

    [Fact]
    public async Task LiveTransferAndShareMethodsFailClosedWithoutCallingUnverifiedEndpoints()
    {
        using var identityClient = new BaiduSessionIdentityClient();
        IBaiduNetdiskAdapter transfer = new LiveBaiduNetdiskAdapter(identityClient);
        IBaiduShareAdapter share = new LiveBaiduShareAdapter();
        var session = Session();
        var exactItems = new RemoteItemIdentitySet(
        [
            new RemoteItemIdentity("remote-1", "顶层文件夹"),
        ]);

        var transferResult = await transfer.ConfirmExactTransferAsync(
            session,
            Query(),
            CancellationToken.None);
        var createResult = await share.CreatePermanentShareAsync(
            session,
            new PermanentShareRequest(exactItems),
            CancellationToken.None);
        var listResult = await share.ListSharesAsync(session, null, CancellationToken.None);

        Assert.False(transferResult.IsSuccess);
        Assert.False(createResult.IsSuccess);
        Assert.False(listResult.IsSuccess);
        Assert.Equal(BaiduErrorKind.NotValidated, transferResult.ErrorKind);
        Assert.Equal(BaiduErrorKind.NotValidated, createResult.ErrorKind);
        Assert.Equal(BaiduErrorKind.NotValidated, listResult.ErrorKind);
        Assert.Equal("百度转存或分享能力尚未通过现场验收。", transferResult.SafeMessage);
        Assert.Equal(SubmissionAcceptance.DefinitelyNotAccepted, transferResult.SubmissionAcceptance);
        Assert.Equal(SubmissionAcceptance.DefinitelyNotAccepted, createResult.SubmissionAcceptance);

        var whitespaceCursor = await share.ListSharesAsync(session, "  \t", CancellationToken.None);
        Assert.False(whitespaceCursor.IsSuccess);
        Assert.Equal(BaiduErrorKind.NotValidated, whitespaceCursor.ErrorKind);
    }

    private static AccountSession Session() => new(
        "account-1",
        [new SessionCookie("BDUSS", "private", ".baidu.com", "/", null, true, true)],
        DateTimeOffset.UtcNow);

    private static TransferConfirmationQuery Query() => new(
        "share-identity",
        new RemotePath("/batch/2026"),
        "outer-name-fingerprint",
        "operation-fingerprint",
        DateTimeOffset.UtcNow.AddMinutes(-1),
        DateTimeOffset.UtcNow.AddMinutes(1));
}
