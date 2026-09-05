using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BaiduBatchTransfer.App.Updates;

internal sealed class SignedFeedManifest
{
    internal const string ReleaseFeedFileName = "releases.win-x64-stable.json";

    private const string AssetCatalogFileName = "assets.win-x64-stable.json";
    private const string ReleasesIndexFileName = "RELEASES-win-x64-stable";
    private const string SetupFileName = "SuperPan.BaiduBatchTransfer-Setup.exe";
    private const string PortableFileName = "SuperPan.BaiduBatchTransfer-Portable.zip";
    private const string PackagePrefix = "SuperPan.BaiduBatchTransfer-";
    private const string FullPackageSuffix = "-full.nupkg";
    private const string DeltaPackageSuffix = "-delta.nupkg";
    private const string ManifestFileName = "upload-manifest.sha256";
    private const string ManifestSignatureFileName = "upload-manifest.sha256.sig";
    private const int ExpectedRsaKeySize = 3072;
    private const int Sha256HexLength = 64;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly IReadOnlyDictionary<string, SignedFeedManifestEntry> entriesByFileName;

    private SignedFeedManifest(IReadOnlyList<SignedFeedManifestEntry> entries)
    {
        Entries = entries;
        entriesByFileName = entries.ToDictionary(
            entry => entry.FileName,
            StringComparer.Ordinal);
        ReleaseFeedEntry = entriesByFileName[ReleaseFeedFileName];
    }

    internal IReadOnlyList<SignedFeedManifestEntry> Entries { get; }

    internal SignedFeedManifestEntry ReleaseFeedEntry { get; }

    internal static SignedFeedManifest Verify(
        ReadOnlySpan<byte> manifestBytes,
        ReadOnlySpan<byte> signatureBytes)
    {
        using var publicKey = OtaPublisherTrust.CreateVerifiedPublicKey();
        return Verify(manifestBytes, signatureBytes, publicKey);
    }

    internal static SignedFeedManifest Verify(
        ReadOnlySpan<byte> manifestBytes,
        ReadOnlySpan<byte> signatureBytes,
        RSA publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        if (publicKey.KeySize != ExpectedRsaKeySize)
        {
            throw new InvalidDataException(
                "The OTA manifest RSA key must be exactly 3072 bits.");
        }

        if (signatureBytes.Length != ExpectedRsaKeySize / 8)
            throw new InvalidDataException("The OTA manifest signature has an invalid length.");

        bool signatureIsValid;
        try
        {
            signatureIsValid = publicKey.VerifyData(
                manifestBytes,
                signatureBytes,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            throw new InvalidDataException("The OTA manifest signature is invalid.");
        }

        if (!signatureIsValid)
            throw new InvalidDataException("The OTA manifest signature is invalid.");

        return ParseCanonicalBytes(manifestBytes);
    }

    internal bool TryGetEntry(
        string? fileName,
        [NotNullWhen(true)] out SignedFeedManifestEntry? entry)
    {
        entry = null;
        return IsAllowedProductionFileName(fileName) &&
               entriesByFileName.TryGetValue(fileName!, out entry);
    }

    private static SignedFeedManifest ParseCanonicalBytes(ReadOnlySpan<byte> manifestBytes)
    {
        if (manifestBytes.IsEmpty ||
            manifestBytes.StartsWith(Encoding.UTF8.Preamble) ||
            manifestBytes[^1] != (byte)'\n' ||
            (manifestBytes.Length > 1 && manifestBytes[^2] == (byte)'\n') ||
            manifestBytes.Contains((byte)'\r'))
        {
            throw new InvalidDataException("The OTA manifest encoding is not canonical UTF-8/LF.");
        }

        string manifestText;
        try
        {
            manifestText = StrictUtf8.GetString(manifestBytes);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("The OTA manifest encoding is not valid UTF-8.");
        }

        var lines = manifestText[..^1].Split('\n', StringSplitOptions.None);
        if (lines.Length == 0 || lines.Any(string.IsNullOrEmpty))
            throw new InvalidDataException("The OTA manifest contains a blank line.");

        var entries = lines.Select(ParseLine).ToArray();
        var exactFileNames = new HashSet<string>(StringComparer.Ordinal);
        var caseInsensitiveFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!exactFileNames.Add(entry.FileName))
                throw new InvalidDataException("The OTA manifest contains a duplicate filename.");
            if (!caseInsensitiveFileNames.Add(entry.FileName))
            {
                throw new InvalidDataException(
                    "The OTA manifest contains a case-insensitive filename collision.");
            }
        }

        string? previousFileName = null;
        foreach (var entry in entries)
        {
            if (!IsAllowedProductionFileName(entry.FileName))
                throw new InvalidDataException("The OTA manifest contains an invalid filename.");

            if (previousFileName is not null &&
                CompareOrdinalUtf8(previousFileName, entry.FileName) >= 0)
            {
                throw new InvalidDataException(
                    "The OTA manifest filenames are not in canonical byte order.");
            }

            previousFileName = entry.FileName;
        }

        if (!exactFileNames.Contains(ReleaseFeedFileName))
            throw new InvalidDataException("The OTA manifest does not cover the production release feed.");

        return new SignedFeedManifest(
            new ReadOnlyCollection<SignedFeedManifestEntry>(entries));
    }

    private static SignedFeedManifestEntry ParseLine(string line)
    {
        if (line.Length <= Sha256HexLength + 2 ||
            line[Sha256HexLength] != ' ' ||
            line[Sha256HexLength + 1] != ' ' ||
            line[Sha256HexLength + 2] == ' ' ||
            !line.AsSpan(0, Sha256HexLength).ContainsOnlyLowerHex())
        {
            throw new InvalidDataException("The OTA manifest contains a malformed line.");
        }

        var fileName = line[(Sha256HexLength + 2)..];
        if (fileName.Length == 0 || fileName[0] == ' ' || fileName[^1] == ' ')
            throw new InvalidDataException("The OTA manifest contains a malformed line.");

        return new SignedFeedManifestEntry(
            line[..Sha256HexLength],
            fileName);
    }

    private static bool IsAllowedProductionFileName([NotNullWhen(true)] string? fileName)
    {
        if (string.IsNullOrEmpty(fileName) ||
            fileName.Length > 255 ||
            fileName is "." or ".." or ManifestFileName or ManifestSignatureFileName ||
            fileName[0] is '/' or '\\' ||
            fileName[^1] is ' ' or '.' ||
            fileName.Contains('/') ||
            fileName.Contains('\\') ||
            fileName.Contains(':') ||
            fileName.Any(character => character < '!' || character > '~'))
        {
            return false;
        }

        if (fileName is ReleaseFeedFileName or AssetCatalogFileName or ReleasesIndexFileName or
            SetupFileName or PortableFileName)
        {
            return true;
        }

        return TryGetProductionPackageVersion(fileName, out var version) &&
               IsCanonicalProductVersion(version);
    }

    private static bool TryGetProductionPackageVersion(
        string fileName,
        [NotNullWhen(true)] out string? version)
    {
        version = null;
        if (!fileName.StartsWith(PackagePrefix, StringComparison.Ordinal))
            return false;

        var suffixLength = fileName.EndsWith(FullPackageSuffix, StringComparison.Ordinal)
            ? FullPackageSuffix.Length
            : fileName.EndsWith(DeltaPackageSuffix, StringComparison.Ordinal)
                ? DeltaPackageSuffix.Length
                : 0;
        if (suffixLength == 0)
            return false;

        var versionLength = fileName.Length - PackagePrefix.Length - suffixLength;
        if (versionLength <= 0)
            return false;

        version = fileName.Substring(PackagePrefix.Length, versionLength);
        return true;
    }

    private static bool IsCanonicalProductVersion(string version)
    {
        var parts = version.Split('.', StringSplitOptions.None);
        if (parts.Length != 3)
            return false;

        foreach (var part in parts)
        {
            if (part.Length == 0 ||
                (part.Length > 1 && part[0] == '0') ||
                part.Any(character => character is < '0' or > '9') ||
                !ushort.TryParse(part, out var number) ||
                number > 65534)
            {
                return false;
            }
        }

        return true;
    }

    private static int CompareOrdinalUtf8(string left, string right)
    {
        var leftBytes = StrictUtf8.GetBytes(left);
        var rightBytes = StrictUtf8.GetBytes(right);
        return leftBytes.AsSpan().SequenceCompareTo(rightBytes);
    }
}

internal sealed class SignedFeedManifestEntry
{
    internal SignedFeedManifestEntry(string sha256, string fileName)
    {
        Sha256 = sha256;
        FileName = fileName;
    }

    internal string Sha256 { get; }

    internal string FileName { get; }
}

internal static class SignedFeedManifestSpanExtensions
{
    internal static bool ContainsOnlyLowerHex(this ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }

        return true;
    }
}
