using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Tests.Domain;

public sealed class ShareSettingsFingerprintTests
{
    [Fact]
    public void CanonicalFingerprintBindsEveryImmutableShareSetting()
    {
        var random = ShareSettingsFingerprint.Compute(true, true, ShareExtractionCodeBehavior.Random, null);

        Assert.Equal(random, ShareSettingsFingerprint.Compute(
            true, true, ShareExtractionCodeBehavior.Random, null));
        Assert.NotEqual(random, ShareSettingsFingerprint.Compute(
            false, true, ShareExtractionCodeBehavior.Random, null));
        Assert.NotEqual(random, ShareSettingsFingerprint.Compute(
            true, false, ShareExtractionCodeBehavior.Random, null));
        Assert.NotEqual(random, ShareSettingsFingerprint.Compute(
            true, true, ShareExtractionCodeBehavior.Fixed, "abcd"));
        Assert.Throws<ArgumentException>(() => ShareSettingsFingerprint.Compute(
            true, true, ShareExtractionCodeBehavior.Random, "alias-code"));
        Assert.Throws<ArgumentException>(() => ShareSettingsFingerprint.Compute(
            true, true, ShareExtractionCodeBehavior.Fixed, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShareSettingsFingerprint.Compute(
            true, true, ShareExtractionCodeBehavior.Unknown, null));
        Assert.DoesNotContain("abcd", ShareSettingsFingerprint.Compute(
            true, true, ShareExtractionCodeBehavior.Fixed, "abcd").ToString());
    }
}
