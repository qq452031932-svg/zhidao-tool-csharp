using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Tests;

public sealed class RemoteItemIdentitySetTests
{
    [Fact]
    public void RejectsEmptyBlankAndDuplicateExactIds()
    {
        Assert.Throws<ArgumentException>(() => new RemoteItemIdentitySet([]));
        Assert.Throws<ArgumentException>(() => new RemoteItemIdentity("   ", "文件"));
        Assert.Throws<ArgumentException>(() => new RemoteItemIdentitySet(
        [
            new RemoteItemIdentity("remote-1", "文件甲"),
            new RemoteItemIdentity("remote-1", "文件乙"),
        ]));
    }

    [Fact]
    public void CanonicalizesOrderAndFingerprintsOnlyOpaqueExactIds()
    {
        var first = new RemoteItemIdentitySet(
        [
            new RemoteItemIdentity("remote-2", "展示名乙"),
            new RemoteItemIdentity("remote-1", "展示名甲"),
        ]);
        var rebuiltWithDifferentNames = new RemoteItemIdentitySet(
        [
            new RemoteItemIdentity("remote-1", "重命名甲"),
            new RemoteItemIdentity("remote-2", "重命名乙"),
        ]);

        Assert.Equal(["remote-1", "remote-2"], first.Items.Select(x => x.ExactId));
        Assert.Equal(first, rebuiltWithDifferentNames);
        Assert.Equal(first.GetHashCode(), rebuiltWithDifferentNames.GetHashCode());
        Assert.Equal(first.Fingerprint, rebuiltWithDifferentNames.Fingerprint);
        Assert.Equal(
            "ccfa9f96bfc52436266badc26337f6da5394d83b7d81ff593e588ee7934c38f5",
            first.Fingerprint);
    }

    [Fact]
    public void IdentityEqualityUsesExactIdAndDisplayNameIsSanitizedForFallback()
    {
        var first = new RemoteItemIdentity("opaque-1", " \u0001\u202e  顶层文件夹  \r\n");
        var renamed = new RemoteItemIdentity("opaque-1", "另一个展示名");

        Assert.Equal("顶层文件夹", first.DisplayedTopLevelName);
        Assert.Equal(first, renamed);
        Assert.Equal(first.GetHashCode(), renamed.GetHashCode());
        Assert.DoesNotContain("opaque-1", first.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("顶层文件夹", first.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DisplayNameSanitizationDoesNotSplitUnicodeScalars()
    {
        var identity = new RemoteItemIdentity(
            "opaque-1",
            new string('界', 255) + "😀\u2028hidden");

        Assert.Equal(new string('界', 255), identity.DisplayedTopLevelName);
        Assert.DoesNotContain(identity.DisplayedTopLevelName, char.IsSurrogate);
    }
}

public sealed class BaiduAdapterShareResultTests
{
    [Fact]
    public void ShareReceiptAndListingCarryExactEvidenceAndRedactSensitiveValues()
    {
        var exactItems = new RemoteItemIdentitySet(
        [
            new RemoteItemIdentity("remote-1", "顶层文件夹"),
        ]);
        var request = new PermanentShareRequest(exactItems, "a1B2");
        var receipt = new ShareCreationReceipt(
            new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero),
            "request-42",
            request,
            "share-42",
            "https://pan.baidu.com/s/private-share",
            "a1B2");
        var record = new BaiduShareRecord(
            "share-42",
            exactItems,
            "https://pan.baidu.com/s/private-share",
            "a1B2",
            isPermanent: true,
            isAccessUnlimited: true);
        var page = new BaiduSharePage([record], "private-next-cursor");

        Assert.Equal("request-42", receipt.AdapterRequestId);
        Assert.Equal(exactItems, receipt.Request.RemoteItems);
        Assert.True(receipt.Request.IsPermanent);
        Assert.True(receipt.Request.IsAccessUnlimited);
        Assert.Equal(exactItems, page.Items.Single().RemoteItems);
        Assert.True(page.Items.Single().IsPermanent);
        Assert.True(page.Items.Single().IsAccessUnlimited);
        Assert.DoesNotContain("private-share", receipt.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("a1B2", receipt.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-next-cursor", page.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ShareSubmissionFailureCarriesTypedAcceptance()
    {
        var unknown = BaiduAdapterResult<ShareCreationReceipt>.Failure(
            BaiduErrorKind.TransientService,
            "private response");
        var definitelyNotAccepted = BaiduAdapterResult<ShareCreationReceipt>.Failure(
            BaiduErrorKind.ExpiredLogin,
            "private response",
            submissionAcceptance: SubmissionAcceptance.DefinitelyNotAccepted);

        Assert.Equal(SubmissionAcceptance.Unknown, unknown.SubmissionAcceptance);
        Assert.Equal(
            SubmissionAcceptance.DefinitelyNotAccepted,
            definitelyNotAccepted.SubmissionAcceptance);
    }

    [Fact]
    public void SharePagePreservesOpaqueCursorAndStatusModelSeparatesPipelineStages()
    {
        const string opaqueCursor = "  opaque cursor \t";
        var page = new BaiduSharePage([], opaqueCursor);

        Assert.Equal(opaqueCursor, page.NextCursor);
        Assert.Contains(ShareOperationStatus.WaitingTransfer, Enum.GetValues<ShareOperationStatus>());
        Assert.Contains(ShareOperationStatus.WaitingShare, Enum.GetValues<ShareOperationStatus>());
        Assert.Contains(ShareOperationStatus.ReusedExisting, Enum.GetValues<ShareOperationStatus>());
    }
}
