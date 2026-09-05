using System.Security.Cryptography;
using System.Text;
using BaiduBatchTransfer.App.Updates;

namespace BaiduBatchTransfer.App.Tests.Updates;

public sealed class SignedFeedManifestTests : IClassFixture<SignedFeedManifestTests.SyntheticRsaFixture>
{
    private const string ReleaseFeedFileName = "releases.win-x64-stable.json";
    private const string FullPackageFileName =
        "SuperPan.BaiduBatchTransfer-1.2.3-full.nupkg";
    private const string ReleaseHash =
        "1111111111111111111111111111111111111111111111111111111111111111";
    private const string PackageHash =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly RSA syntheticSigningKey;

    public SignedFeedManifestTests(SyntheticRsaFixture fixture)
    {
        syntheticSigningKey = fixture.SigningKey;
    }

    [Fact]
    public void VerifiesExactCanonicalBytesAndReturnsImmutableEntries()
    {
        var bytes = CanonicalManifest(
            (FullPackageFileName, PackageHash),
            (ReleaseFeedFileName, ReleaseHash));

        var manifest = Verify(bytes, syntheticSigningKey);

        Assert.Equal(2, manifest.Entries.Count);
        Assert.Equal(FullPackageFileName, manifest.Entries[0].FileName);
        Assert.Equal(PackageHash, manifest.Entries[0].Sha256);
        Assert.Same(manifest.Entries[1], manifest.ReleaseFeedEntry);
        Assert.Equal(ReleaseFeedFileName, manifest.ReleaseFeedEntry.FileName);
        Assert.DoesNotContain(
            typeof(SignedFeedManifestEntry).GetProperties(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic),
            property => property.SetMethod is not null);
        var mutableView = Assert.IsAssignableFrom<IList<SignedFeedManifestEntry>>(manifest.Entries);
        Assert.True(mutableView.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => mutableView.Clear());
    }

    [Fact]
    public void FindsOnlyExactValidatedTopLevelFileNames()
    {
        var manifest = Verify(CanonicalManifest(
            (FullPackageFileName, PackageHash),
            (ReleaseFeedFileName, ReleaseHash)), syntheticSigningKey);

        Assert.True(manifest.TryGetEntry(FullPackageFileName, out var package));
        Assert.NotNull(package);
        Assert.Equal(PackageHash, package.Sha256);
        Assert.False(manifest.TryGetEntry(FullPackageFileName.ToUpperInvariant(), out _));
        Assert.False(manifest.TryGetEntry("../" + FullPackageFileName, out _));
        Assert.False(manifest.TryGetEntry("directory/" + FullPackageFileName, out _));
        Assert.False(manifest.TryGetEntry(string.Empty, out _));
    }

    [Theory]
    [InlineData("RELEASES-win-x64-stable")]
    [InlineData("SuperPan.BaiduBatchTransfer-1.2.3-full.nupkg")]
    [InlineData("SuperPan.BaiduBatchTransfer-1.2.3-delta.nupkg")]
    [InlineData("SuperPan.BaiduBatchTransfer-Portable.zip")]
    [InlineData("SuperPan.BaiduBatchTransfer-Setup.exe")]
    [InlineData("assets.win-x64-stable.json")]
    [InlineData("releases.win-x64-stable.json")]
    public void AcceptsOnlyRequiredProductionAssetForms(string fileName)
    {
        var entries = fileName == ReleaseFeedFileName
            ? new[] { (fileName, ReleaseHash) }
            : new[] { (fileName, PackageHash), (ReleaseFeedFileName, ReleaseHash) };

        var manifest = Verify(CanonicalManifest(entries), syntheticSigningKey);

        Assert.True(manifest.TryGetEntry(fileName, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../releases.win-x64-stable.json")]
    [InlineData("directory/release.nupkg")]
    [InlineData("directory\\release.nupkg")]
    [InlineData("C:release.nupkg")]
    [InlineData("C:\\release.nupkg")]
    [InlineData("\\\\server\\release.nupkg")]
    [InlineData("/release.nupkg")]
    [InlineData("release\tname.nupkg")]
    [InlineData("réleases.win-x64-stable.json")]
    [InlineData("releases．win-x64-stable.json")]
    [InlineData("README.txt")]
    [InlineData("releases.win-x64-ota-acceptance.json")]
    [InlineData("SuperPan.BaiduBatchTransfer.OtaAcceptance-1.2.3-full.nupkg")]
    [InlineData("superpan.BaiduBatchTransfer-1.2.3-full.nupkg")]
    [InlineData("SuperPan.BaiduBatchTransfer-01.2.3-full.nupkg")]
    [InlineData("SuperPan.BaiduBatchTransfer-1.2-full.nupkg")]
    [InlineData("SuperPan.BaiduBatchTransfer-65535.2.3-full.nupkg")]
    [InlineData("SuperPan.BaiduBatchTransfer-1.2.3-Full.nupkg")]
    [InlineData("upload-manifest.sha256")]
    [InlineData("upload-manifest.sha256.sig")]
    public void RejectsUnsafeAmbiguousReservedOrForeignFileNames(string fileName)
    {
        var bytes = ManifestWithUncheckedNames(fileName, ReleaseFeedFileName);

        var exception = Assert.Throws<InvalidDataException>(() =>
            Verify(bytes, syntheticSigningKey));

        AssertSafeFailure(exception);
    }

    [Theory]
    [InlineData("SuperPan.BaiduBatchTransfer-1.2.3-alpha-full.nupkg")]
    [InlineData("SuperPan.BaiduBatchTransfer-1.2.3+build-full.nupkg")]
    [InlineData("SuperPan.BaiduBatchTransfer-1.2.3.4-full.nupkg")]
    public void RejectsNonProductionVersionForms(string fileName)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            Verify(ManifestWithUncheckedNames(fileName, ReleaseFeedFileName), syntheticSigningKey));

        AssertSafeFailure(exception);
    }

    [Theory]
    [MemberData(nameof(NonCanonicalManifestBytes))]
    public void RejectsNonCanonicalEncodingOrLineEndings(byte[] bytes)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            Verify(bytes, syntheticSigningKey));

        AssertSafeFailure(exception);
    }

    public static TheoryData<byte[]> NonCanonicalManifestBytes()
    {
        var canonical = CanonicalManifest((ReleaseFeedFileName, ReleaseHash));
        var withoutFinalLf = canonical[..^1];
        var withBom = Encoding.UTF8.GetPreamble().Concat(canonical).ToArray();
        var withCrLf = Encoding.ASCII.GetBytes(
            $"{ReleaseHash}  {ReleaseFeedFileName}\r\n");
        var withCr = Encoding.ASCII.GetBytes(
            $"{ReleaseHash}  {ReleaseFeedFileName}\r");
        var withTwoFinalLfs = canonical.Concat(new byte[] { (byte)'\n' }).ToArray();
        var withBlankMiddleLine = Encoding.ASCII.GetBytes(
            $"{PackageHash}  {FullPackageFileName}\n\n{ReleaseHash}  {ReleaseFeedFileName}\n");
        var invalidUtf8 = canonical.ToArray();
        invalidUtf8[0] = 0xff;

        return new TheoryData<byte[]>
        {
            Array.Empty<byte>(),
            withoutFinalLf,
            withBom,
            withCrLf,
            withCr,
            withTwoFinalLfs,
            withBlankMiddleLine,
            invalidUtf8,
        };
    }

    [Theory]
    [MemberData(nameof(MalformedLines))]
    public void RejectsMalformedHashesAndSeparators(string line)
    {
        var bytes = Encoding.ASCII.GetBytes(line + "\n");

        var exception = Assert.Throws<InvalidDataException>(() =>
            Verify(bytes, syntheticSigningKey));

        AssertSafeFailure(exception);
    }

    public static TheoryData<string> MalformedLines() => new()
    {
        $"{new string('a', 63)}  {ReleaseFeedFileName}",
        $"{new string('a', 65)}  {ReleaseFeedFileName}",
        $"{new string('A', 64)}  {ReleaseFeedFileName}",
        $"{new string('g', 64)}  {ReleaseFeedFileName}",
        $"{ReleaseHash} {ReleaseFeedFileName}",
        $"{ReleaseHash}   {ReleaseFeedFileName}",
        $" {ReleaseHash}  {ReleaseFeedFileName}",
        $"{ReleaseHash}  {ReleaseFeedFileName} ",
    };

    [Fact]
    public void RejectsManifestWithoutTheFixedReleaseFeedEntry()
    {
        var exception = Assert.Throws<InvalidDataException>(() => Verify(
            CanonicalManifest((FullPackageFileName, PackageHash)), syntheticSigningKey));

        AssertSafeFailure(exception);
    }

    [Fact]
    public void ManifestLimitsMatchTheServerInventoryAndCanonicalLineBounds()
    {
        Assert.Equal(254, SignedFeedManifest.MaxEntries);
        Assert.Equal(
            (64 + 2 + 255 + 1) * SignedFeedManifest.MaxEntries,
            SignedFeedManifest.MaxManifestBytes);
    }

    [Fact]
    public void AcceptsExactlyTheMaximumNumberOfCanonicalEntries()
    {
        var entries = Enumerable.Range(0, SignedFeedManifest.MaxEntries - 1)
            .Select(index => (
                $"SuperPan.BaiduBatchTransfer-1.0.{index}-full.nupkg",
                PackageHash))
            .Append((ReleaseFeedFileName, ReleaseHash))
            .ToArray();

        var manifest = Verify(CanonicalManifest(entries), syntheticSigningKey);

        Assert.Equal(SignedFeedManifest.MaxEntries, manifest.Entries.Count);
    }

    [Fact]
    public void RejectsZeroByteManifestBeforeSignatureChecks()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            SignedFeedManifest.VerifyWithPublicKeyForTesting(
                Array.Empty<byte>(), Array.Empty<byte>(), syntheticSigningKey));

        Assert.Contains("empty", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signature", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllowsTheExactManifestByteLimitToReachSignatureValidation()
    {
        var bytes = BytesAtManifestLimit(SignedFeedManifest.MaxManifestBytes);

        var exception = Assert.Throws<InvalidDataException>(() =>
            SignedFeedManifest.VerifyWithPublicKeyForTesting(
                bytes, Array.Empty<byte>(), syntheticSigningKey));

        Assert.Contains("signature", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("size", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsOneByteOverManifestLimitBeforeSignatureChecks()
    {
        var bytes = BytesAtManifestLimit(SignedFeedManifest.MaxManifestBytes + 1);

        var exception = Assert.Throws<InvalidDataException>(() =>
            SignedFeedManifest.VerifyWithPublicKeyForTesting(
                bytes, Array.Empty<byte>(), syntheticSigningKey));

        Assert.Contains("size", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signature", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllowsTheExactEntryLimitToReachSignatureValidation()
    {
        var bytes = Enumerable.Repeat((byte)'\n', SignedFeedManifest.MaxEntries).ToArray();

        var exception = Assert.Throws<InvalidDataException>(() =>
            SignedFeedManifest.VerifyWithPublicKeyForTesting(
                bytes, Array.Empty<byte>(), syntheticSigningKey));

        Assert.Contains("signature", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("entries", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsOneEntryOverLimitBeforeSignatureChecks()
    {
        var bytes = Enumerable.Repeat(
            (byte)'\n',
            SignedFeedManifest.MaxEntries + 1).ToArray();

        var exception = Assert.Throws<InvalidDataException>(() =>
            SignedFeedManifest.VerifyWithPublicKeyForTesting(
                bytes, Array.Empty<byte>(), syntheticSigningKey));

        Assert.Contains("entries", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signature", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsUnterminatedEntryOverLimitBeforeSignatureChecks()
    {
        var bytes = Enumerable.Repeat(
                (byte)'\n',
                SignedFeedManifest.MaxEntries)
            .Append((byte)'x')
            .ToArray();

        var exception = Assert.Throws<InvalidDataException>(() =>
            SignedFeedManifest.VerifyWithPublicKeyForTesting(
                bytes, Array.Empty<byte>(), syntheticSigningKey));

        Assert.Contains("entries", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signature", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsEntriesNotStrictlyOrderedByOrdinalUtf8FileNameBytes()
    {
        var bytes = Encoding.ASCII.GetBytes(
            $"{ReleaseHash}  {ReleaseFeedFileName}\n" +
            $"{PackageHash}  {FullPackageFileName}\n");

        var exception = Assert.Throws<InvalidDataException>(() =>
            Verify(bytes, syntheticSigningKey));

        AssertSafeFailure(exception);
    }

    [Fact]
    public void RejectsDuplicateFileNames()
    {
        var bytes = Encoding.ASCII.GetBytes(
            $"{ReleaseHash}  {ReleaseFeedFileName}\n" +
            $"{ReleaseHash}  {ReleaseFeedFileName}\n");

        var exception = Assert.Throws<InvalidDataException>(() =>
            Verify(bytes, syntheticSigningKey));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
        AssertSafeFailure(exception);
    }

    [Fact]
    public void RejectsOrdinalIgnoreCaseFileNameCollisions()
    {
        var bytes = ManifestWithUncheckedNames(
            ReleaseFeedFileName,
            ReleaseFeedFileName.ToUpperInvariant());

        var exception = Assert.Throws<InvalidDataException>(() =>
            Verify(bytes, syntheticSigningKey));

        Assert.Contains("collision", exception.Message, StringComparison.OrdinalIgnoreCase);
        AssertSafeFailure(exception);
    }

    [Fact]
    public void RejectsSignatureFromWrongKey()
    {
        using var wrongKey = RSA.Create(3072);
        var bytes = CanonicalManifest((ReleaseFeedFileName, ReleaseHash));
        var signature = Sign(bytes, syntheticSigningKey);

        var exception = Assert.Throws<InvalidDataException>(() =>
            SignedFeedManifest.VerifyWithPublicKeyForTesting(bytes, signature, wrongKey));

        Assert.Contains("signature", exception.Message, StringComparison.OrdinalIgnoreCase);
        AssertSafeFailure(exception);
    }

    [Fact]
    public void RejectsModifiedManifestBytes()
    {
        var bytes = CanonicalManifest((ReleaseFeedFileName, ReleaseHash));
        var signature = Sign(bytes, syntheticSigningKey);
        bytes[0] = (byte)'2';

        var exception = Assert.Throws<InvalidDataException>(() =>
            SignedFeedManifest.VerifyWithPublicKeyForTesting(
                bytes, signature, syntheticSigningKey));

        AssertSafeFailure(exception);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(383)]
    [InlineData(385)]
    public void RejectsAbsentEmptyOrIncorrectLengthSignatures(int signatureLength)
    {
        var bytes = CanonicalManifest((ReleaseFeedFileName, ReleaseHash));

        var exception = Assert.Throws<InvalidDataException>(() =>
            SignedFeedManifest.VerifyWithPublicKeyForTesting(
                bytes, new byte[signatureLength], syntheticSigningKey));

        Assert.Contains("signature", exception.Message, StringComparison.OrdinalIgnoreCase);
        AssertSafeFailure(exception);
    }

    [Fact]
    public void RejectsPssSignatureBecauseTheWireFormatIsPkcs1V15()
    {
        var bytes = CanonicalManifest((ReleaseFeedFileName, ReleaseHash));
        var signature = syntheticSigningKey.SignData(
            bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

        var exception = Assert.Throws<InvalidDataException>(() =>
            SignedFeedManifest.VerifyWithPublicKeyForTesting(
                bytes, signature, syntheticSigningKey));

        AssertSafeFailure(exception);
    }

    [Fact]
    public void RejectsInjectedRsaThatIsNot3072Bits()
    {
        using var signingKey = RSA.Create(2048);
        var bytes = CanonicalManifest((ReleaseFeedFileName, ReleaseHash));
        var signature = Sign(bytes, signingKey);

        var exception = Assert.Throws<InvalidDataException>(() =>
            SignedFeedManifest.VerifyWithPublicKeyForTesting(bytes, signature, signingKey));

        Assert.Contains("3072", exception.Message, StringComparison.Ordinal);
        AssertSafeFailure(exception);
    }

    [Fact]
    public void InjectedPublicRsaRemainsOwnedByCaller()
    {
        using var publicKey = RSA.Create();
        publicKey.ImportParameters(
            syntheticSigningKey.ExportParameters(includePrivateParameters: false));
        var bytes = CanonicalManifest((ReleaseFeedFileName, ReleaseHash));
        var signature = Sign(bytes, syntheticSigningKey);

        _ = SignedFeedManifest.VerifyWithPublicKeyForTesting(bytes, signature, publicKey);

        Assert.True(publicKey.VerifyData(
            bytes,
            signature,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void DefaultVerificationUsesThePinnedOtaPublisherPublicKey()
    {
        var bytes = CanonicalManifest((ReleaseFeedFileName, ReleaseHash));
        var syntheticSignature = Sign(bytes, syntheticSigningKey);

        var exception = Assert.Throws<InvalidDataException>(() =>
            SignedFeedManifest.Verify(bytes, syntheticSignature));

        Assert.Contains("signature", exception.Message, StringComparison.OrdinalIgnoreCase);
        AssertSafeFailure(exception);
    }

    private static SignedFeedManifest Verify(byte[] bytes, RSA signingKey) =>
        SignedFeedManifest.VerifyWithPublicKeyForTesting(
            bytes, Sign(bytes, signingKey), signingKey);

    private static byte[] Sign(byte[] bytes, RSA signingKey) =>
        signingKey.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    private static byte[] CanonicalManifest(params (string FileName, string Hash)[] entries) =>
        Encoding.ASCII.GetBytes(string.Concat(entries
            .OrderBy(entry => entry.FileName, Utf8OrdinalComparer.Instance)
            .Select(entry => $"{entry.Hash}  {entry.FileName}\n")));

    private static byte[] ManifestWithUncheckedNames(params string[] fileNames) =>
        Encoding.UTF8.GetBytes(string.Concat(fileNames
            .OrderBy(fileName => fileName, Utf8OrdinalComparer.Instance)
            .Select(fileName => $"{ReleaseHash}  {fileName}\n")));

    private static byte[] BytesAtManifestLimit(int length)
    {
        var bytes = Enumerable.Repeat((byte)'x', length).ToArray();
        for (var index = 1; index <= SignedFeedManifest.MaxEntries; index++)
            bytes[^index] = (byte)'\n';
        return bytes;
    }

    private static void AssertSafeFailure(Exception exception)
    {
        Assert.DoesNotContain(ReleaseHash, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(PackageHash, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN PRIVATE KEY", exception.ToString(), StringComparison.Ordinal);
    }

    public sealed class SyntheticRsaFixture : IDisposable
    {
        internal RSA SigningKey { get; } = RSA.Create(3072);

        public void Dispose()
        {
            SigningKey.Dispose();
        }
    }

    private sealed class Utf8OrdinalComparer : IComparer<string>
    {
        internal static Utf8OrdinalComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            ArgumentNullException.ThrowIfNull(left);
            ArgumentNullException.ThrowIfNull(right);
            return Encoding.UTF8.GetBytes(left).AsSpan()
                .SequenceCompareTo(Encoding.UTF8.GetBytes(right));
        }
    }
}
