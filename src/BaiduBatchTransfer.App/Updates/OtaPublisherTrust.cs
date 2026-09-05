using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BaiduBatchTransfer.App.Updates;

internal static class OtaPublisherTrust
{
    internal const string EmbeddedCertificateResourceName =
        "BaiduBatchTransfer.App.Updates.SuperPan.OtaPublisher.cer";

    internal const string SubjectPublicKeyInfoSha256 =
        "f905188d79bb427c1a6eacc4bc9741d68a407228f643f6a50bfd791211da6cff";

    internal static RSA CreateVerifiedPublicKey() =>
        LoadVerifiedPublicKey(
            typeof(OtaPublisherTrust).Assembly,
            EmbeddedCertificateResourceName,
            SubjectPublicKeyInfoSha256);

    internal static RSA LoadVerifiedPublicKey(
        Assembly assembly,
        string resourceName,
        string expectedSubjectPublicKeyInfoSha256)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"The embedded OTA publisher certificate '{resourceName}' is unavailable.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return VerifyCertificate(buffer.ToArray(), expectedSubjectPublicKeyInfoSha256);
    }

    internal static RSA VerifyCertificate(
        ReadOnlySpan<byte> rawCertificate,
        string expectedSubjectPublicKeyInfoSha256)
    {
        var expectedPin = DecodeExpectedPin(expectedSubjectPublicKeyInfoSha256);
        X509Certificate2 certificate;
        try
        {
            certificate = new X509Certificate2(rawCertificate.ToArray());
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException(
                "The OTA publisher certificate is malformed.",
                exception);
        }

        using (certificate)
        using (var certificateRsa = certificate.GetRSAPublicKey())
        {
            if (certificateRsa is null)
                throw new InvalidOperationException("The OTA publisher certificate is not RSA.");
            if (certificateRsa.KeySize != 3072)
            {
                throw new InvalidOperationException(
                    $"The OTA publisher RSA key must be exactly 3072 bits, not {certificateRsa.KeySize}.");
            }

            var subjectPublicKeyInfo = certificate.PublicKey.ExportSubjectPublicKeyInfo();
            var actualPin = SHA256.HashData(subjectPublicKeyInfo);
            var matches = CryptographicOperations.FixedTimeEquals(actualPin, expectedPin);
            CryptographicOperations.ZeroMemory(actualPin);
            CryptographicOperations.ZeroMemory(expectedPin);
            if (!matches)
                throw new InvalidOperationException("The OTA publisher certificate SPKI pin does not match.");

            var publicParameters = certificateRsa.ExportParameters(includePrivateParameters: false);
            var publicRsa = RSA.Create();
            try
            {
                publicRsa.ImportParameters(publicParameters);
                return publicRsa;
            }
            catch
            {
                publicRsa.Dispose();
                throw;
            }
        }
    }

    private static byte[] DecodeExpectedPin(string expectedPin)
    {
        ArgumentNullException.ThrowIfNull(expectedPin);
        if (expectedPin.Length != 64 || expectedPin.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new InvalidOperationException(
                "The configured OTA publisher SPKI pin must be 64 lowercase hexadecimal characters.");
        }

        return Convert.FromHexString(expectedPin);
    }
}
