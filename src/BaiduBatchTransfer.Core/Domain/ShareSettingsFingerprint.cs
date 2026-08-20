using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BaiduBatchTransfer.Core.Domain;

public static class ShareSettingsFingerprint
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static string Compute(
        bool isPermanent,
        bool isAccessUnlimited,
        ShareExtractionCodeBehavior extractionCodeBehavior,
        string? fixedExtractionCode)
    {
        if (extractionCodeBehavior is not ShareExtractionCodeBehavior.Random and
            not ShareExtractionCodeBehavior.Fixed)
        {
            throw new ArgumentOutOfRangeException(nameof(extractionCodeBehavior));
        }

        if (extractionCodeBehavior == ShareExtractionCodeBehavior.Random)
        {
            if (fixedExtractionCode is not null)
                throw new ArgumentException("Random extraction-code settings cannot contain a fixed code.", nameof(fixedExtractionCode));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(fixedExtractionCode))
                throw new ArgumentException("Fixed extraction-code settings require a fixed code.", nameof(fixedExtractionCode));
            fixedExtractionCode = fixedExtractionCode.Trim();
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "transfer-share-settings-v1");
        Append(hash, isPermanent ? "1" : "0");
        Append(hash, isAccessUnlimited ? "1" : "0");
        Append(hash, extractionCodeBehavior == ShareExtractionCodeBehavior.Random ? "random" : "fixed");
        Append(hash, fixedExtractionCode ?? string.Empty);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        byte[] encoded;
        try
        {
            encoded = StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("Share settings must contain valid UTF-16 text.", nameof(value));
        }

        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, encoded.Length);
        hash.AppendData(length);
        hash.AppendData(encoded);
    }
}
