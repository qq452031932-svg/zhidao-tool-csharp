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
        Assert.Equal(first.Fingerprint, RemoteItemIdentitySet.ComputeFingerprintFromCanonicalOrder(
            first.Items.Select(item => item.ExactId)));
        Assert.Equal(
            "ccfa9f96bfc52436266badc26337f6da5394d83b7d81ff593e588ee7934c38f5",
            first.Fingerprint);
    }

    [Fact]
    public async Task AsyncCanonicalFingerprintConsumesAStreamingSequence()
    {
        static async IAsyncEnumerable<string> Stream()
        {
            yield return "remote-1";
            await Task.Yield();
            yield return "remote-2";
        }

        var expected = new RemoteItemIdentitySet([new("remote-2", "B"), new("remote-1", "A")]);
        Assert.Equal(expected.Fingerprint,
            await RemoteItemIdentitySet.ComputeFingerprintFromCanonicalOrderAsync(Stream()));
    }

    [Fact]
    public void CanonicalSortKeysPreserveDotNetOrdinalOrderAcrossUnicodePlanes()
    {
        var supplementary = "\U00010000";
        var privateUseBmp = "\uE000";

        Assert.True(string.CompareOrdinal(supplementary, privateUseBmp) < 0);
        Assert.True(RemoteItemIdentitySet.GetCanonicalSortKey(supplementary)
            .AsSpan().SequenceCompareTo(RemoteItemIdentitySet.GetCanonicalSortKey(privateUseBmp)) < 0);

        var set = new RemoteItemIdentitySet([
            new(privateUseBmp, "BMP"),
            new(supplementary, "Supplementary"),
        ]);
        Assert.Equal([supplementary, privateUseBmp], set.Items.Select(item => item.ExactId));
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

    [Fact]
    public void RejectsLoneHighSurrogateBeforeFingerprinting()
    {
        var malformedId = "prefix\ud800";
        var identity = new RemoteItemIdentity(malformedId, "文件");

        var error = Assert.Throws<ArgumentException>(() => new RemoteItemIdentitySet([identity]));

        Assert.DoesNotContain(malformedId, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsLoneLowSurrogateBeforeFingerprinting()
    {
        var malformedId = "prefix\udc00";
        var identity = new RemoteItemIdentity(malformedId, "文件");

        var error = Assert.Throws<ArgumentException>(() => new RemoteItemIdentitySet([identity]));

        Assert.DoesNotContain(malformedId, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsDistinctMalformedIdsThatWouldCollapseUnderReplacementFallback()
    {
        var loneHighSurrogate = new RemoteItemIdentity("same-prefix\ud800", "文件甲");
        var loneLowSurrogate = new RemoteItemIdentity("same-prefix\udc00", "文件乙");

        Assert.Throws<ArgumentException>(() => new RemoteItemIdentitySet([loneHighSurrogate]));
        Assert.Throws<ArgumentException>(() => new RemoteItemIdentitySet([loneLowSurrogate]));
    }
}

public sealed class BaiduAdapterShareResultTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("abcde")]
    [InlineData("a-b1")]
    [InlineData("中文ab")]
    public void PermanentShareRequestRejectsInvalidBaiduFixedExtractionCodesWithoutEchoingValue(string code)
    {
        var exception = Assert.Throws<ArgumentException>(() => new PermanentShareRequest(
            new RemoteItemIdentitySet([new("remote", "name")]), code));

        Assert.DoesNotContain(code, exception.ToString(), StringComparison.Ordinal);
    }

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
            new EffectiveShareSettings(
                isPermanent: true,
                isAccessUnlimited: true,
                ShareExtractionCodeBehavior.Fixed,
                "a1B2"));
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
        Assert.Equal(ShareSettingsMatchStatus.Matched, receipt.SettingsMatchStatus);
        Assert.True(receipt.IsFinalAcceptance);
        Assert.Equal(exactItems, page.Items.Single().RemoteItems);
        Assert.True(page.Items.Single().IsPermanent);
        Assert.True(page.Items.Single().IsAccessUnlimited);
        Assert.DoesNotContain("private-share", receipt.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("a1B2", receipt.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-next-cursor", page.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("a1B2", receipt.EffectiveSettings.ToString(), StringComparison.Ordinal);
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

    [Theory]
    [InlineData(false, true, ShareExtractionCodeBehavior.Fixed, "a1B2")]
    [InlineData(true, false, ShareExtractionCodeBehavior.Fixed, "a1B2")]
    [InlineData(true, true, ShareExtractionCodeBehavior.Fixed, "z9Y8")]
    [InlineData(true, true, ShareExtractionCodeBehavior.Random, "a1B2")]
    public void ReceiptMarksObservedSettingsThatDoNotMatchTheFixedRequest(
        bool isPermanent,
        bool isAccessUnlimited,
        ShareExtractionCodeBehavior codeBehavior,
        string effectiveCode)
    {
        var request = RequestWithFixedCode();
        var receipt = Receipt(
            request,
            new EffectiveShareSettings(
                isPermanent,
                isAccessUnlimited,
                codeBehavior,
                effectiveCode));

        Assert.Equal(ShareSettingsMatchStatus.Mismatched, receipt.SettingsMatchStatus);
        Assert.False(receipt.IsFinalAcceptance);
    }

    [Fact]
    public void UnconfirmedEffectiveSettingsKeepReceiptPending()
    {
        var receipt = Receipt(RequestWithFixedCode(), EffectiveShareSettings.Unconfirmed);

        Assert.Equal(ShareSettingsMatchStatus.Pending, receipt.SettingsMatchStatus);
        Assert.False(receipt.IsFinalAcceptance);
        Assert.Null(receipt.ExtractionCode);
    }

    [Fact]
    public void RandomCodeRequestRequiresObservedRandomBehaviorAndGeneratedCode()
    {
        var items = ExactItems();
        var request = new PermanentShareRequest(items);
        var receipt = Receipt(
            request,
            new EffectiveShareSettings(
                isPermanent: true,
                isAccessUnlimited: true,
                ShareExtractionCodeBehavior.Random,
                "r4Nd"));

        Assert.Equal(ShareSettingsMatchStatus.Matched, receipt.SettingsMatchStatus);
        Assert.Equal("r4Nd", receipt.ExtractionCode);
        Assert.True(receipt.IsFinalAcceptance);
    }

    [Theory]
    [InlineData("安全原因\u202e")]
    [InlineData("安全\u2066原因")]
    [InlineData("安全原因\u2028下一行")]
    [InlineData("安全原因\u2029下一段")]
    public void AdapterCapabilityRejectsUnsafeUnicodeReasonCharacters(string unsafeReason)
    {
        Assert.Throws<ArgumentException>(() => new AdapterCapability(false, false, unsafeReason));
    }

    private static RemoteItemIdentitySet ExactItems() => new(
    [
        new RemoteItemIdentity("remote-1", "顶层文件夹"),
    ]);

    private static PermanentShareRequest RequestWithFixedCode() => new(ExactItems(), "a1B2");

    private static ShareCreationReceipt Receipt(
        PermanentShareRequest request,
        EffectiveShareSettings effectiveSettings) => new(
            new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero),
            "request-42",
            request,
            "share-42",
            "https://pan.baidu.com/s/private-share",
            effectiveSettings);
}
