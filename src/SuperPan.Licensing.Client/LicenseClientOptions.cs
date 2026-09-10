using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace SuperPan.Licensing.Client;

public sealed class LicenseClientOptions
{
    public const int DefaultMaximumResponseBytes = 64 * 1024;
    public static readonly TimeSpan DefaultServerClockTolerance = TimeSpan.FromMinutes(5);

    public LicenseClientOptions(
        string softwareId,
        Uri serviceEndpoint,
        string signingKeyId,
        string signingPublicKeyPem,
        IEnumerable<string> tlsSpkiSha256Pins,
        bool allowLoopbackHttpForAcceptance = false,
        bool isAcceptanceBuild = false,
        int maximumResponseBytes = DefaultMaximumResponseBytes,
        TimeSpan? serverClockTolerance = null)
    {
        ValidateIdentifier(softwareId, nameof(softwareId));
        ValidateIdentifier(signingKeyId, nameof(signingKeyId));
        ArgumentNullException.ThrowIfNull(serviceEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(signingPublicKeyPem);
        ArgumentNullException.ThrowIfNull(tlsSpkiSha256Pins);

        if (!serviceEndpoint.IsAbsoluteUri || !string.IsNullOrEmpty(serviceEndpoint.UserInfo) ||
            !string.IsNullOrEmpty(serviceEndpoint.Query) || !string.IsNullOrEmpty(serviceEndpoint.Fragment))
        {
            throw new ArgumentException("The license service endpoint must be an absolute origin without credentials, query, or fragment.", nameof(serviceEndpoint));
        }

        if (allowLoopbackHttpForAcceptance && !isAcceptanceBuild)
        {
            throw new ArgumentException("Loopback HTTP is restricted to acceptance builds.", nameof(allowLoopbackHttpForAcceptance));
        }

        var isHttps = serviceEndpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isAllowedAcceptanceHttp = allowLoopbackHttpForAcceptance && isAcceptanceBuild &&
            serviceEndpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            serviceEndpoint.Host.Equals("127.0.0.1", StringComparison.Ordinal);
        if (!isHttps && !isAllowedAcceptanceHttp)
        {
            throw new ArgumentException("The license service endpoint must use HTTPS.", nameof(serviceEndpoint));
        }

        var pins = tlsSpkiSha256Pins.ToArray();
        var validPinPair = pins.Length == 2 && pins.All(IsValidPin) &&
            pins.Distinct(StringComparer.Ordinal).Count() == 2;
        if ((isHttps && !validPinPair) || (!isHttps && pins.Length != 0 && !validPinPair))
        {
            throw new ArgumentException("Exactly two distinct lowercase SHA-256 SPKI pins are required.", nameof(tlsSpkiSha256Pins));
        }

        try
        {
            var pem = signingPublicKeyPem.AsSpan().Trim();
            var fields = PemEncoding.Find(pem);
            var location = fields.Location.GetOffsetAndLength(pem.Length);
            if (location.Offset != 0 || location.Length != pem.Length ||
                !pem[fields.Label].SequenceEqual("PUBLIC KEY"))
            {
                throw new ArgumentException("The signing key must contain exactly one public-key PEM block.", nameof(signingPublicKeyPem));
            }
            using var key = ECDsa.Create();
            key.ImportFromPem(signingPublicKeyPem);
            var curveOid = key.ExportParameters(includePrivateParameters: false).Curve.Oid.Value;
            if (key.KeySize != 256 || curveOid != ECCurve.NamedCurves.nistP256.Oid.Value)
            {
                throw new ArgumentException("The signing public key must be ECDSA P-256.", nameof(signingPublicKeyPem));
            }
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            throw new ArgumentException("The signing public key must be a valid ECDSA P-256 public key.", nameof(signingPublicKeyPem));
        }

        var tolerance = serverClockTolerance ?? DefaultServerClockTolerance;
        if (tolerance <= TimeSpan.Zero || tolerance > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(serverClockTolerance));
        }
        if (maximumResponseBytes < 1 || maximumResponseBytes > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResponseBytes));
        }

        SoftwareId = softwareId;
        ServiceEndpoint = serviceEndpoint;
        SigningKeyId = signingKeyId;
        SigningPublicKeyPem = signingPublicKeyPem;
        TlsSpkiSha256Pins = Array.AsReadOnly(pins);
        AllowLoopbackHttpForAcceptance = allowLoopbackHttpForAcceptance;
        IsAcceptanceBuild = isAcceptanceBuild;
        MaximumResponseBytes = maximumResponseBytes;
        ServerClockTolerance = tolerance;
    }

    public string SoftwareId { get; }
    public Uri ServiceEndpoint { get; }
    public string SigningKeyId { get; }
    public string SigningPublicKeyPem { get; }
    public IReadOnlyList<string> TlsSpkiSha256Pins { get; }
    public bool AllowLoopbackHttpForAcceptance { get; }
    public bool IsAcceptanceBuild { get; }
    public int MaximumResponseBytes { get; }
    public TimeSpan ServerClockTolerance { get; }

    public static string ComputeSpkiSha256Pin(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
    }

    public bool ValidateServerCertificate(X509Certificate2? certificate, SslPolicyErrors policyErrors)
    {
        if (certificate is null || policyErrors != SslPolicyErrors.None)
        {
            return false;
        }

        string candidate;
        try
        {
            candidate = ComputeSpkiSha256Pin(certificate);
        }
        catch (CryptographicException)
        {
            return false;
        }

        var candidateBytes = Encoding.ASCII.GetBytes(candidate);
        foreach (var pin in TlsSpkiSha256Pins)
        {
            if (CryptographicOperations.FixedTimeEquals(candidateBytes, Encoding.ASCII.GetBytes(pin)))
            {
                return true;
            }
        }
        return false;
    }

    internal ECDsa CreateSigningPublicKey()
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(SigningPublicKeyPem);
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static bool IsValidPin(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, parameterName);
        if (value != value.Trim() || Encoding.UTF8.GetByteCount(value) > LicenseProtocol.MaxIdentifierLength)
        {
            throw new ArgumentException("The identifier is invalid.", parameterName);
        }
    }
}
