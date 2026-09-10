using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using SuperPan.Licensing.Client;

namespace SuperPan.Licensing.Client.Tests;

public sealed class SignedLicenseEnvelopeVerifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EnvelopeAcceptsValidP256SignatureOverExactPayloadBytes()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = CreateVerifier(signer);
        var claims = CreateClaims();

        var verified = verifier.Verify(Sign(claims, signer), claims.RequestId, claims.SoftwareId!, claims.MachineHash!);

        Assert.Equal(claims, verified);
    }

    [Fact]
    public void EnvelopeRejectsWrongSigner()
    {
        using var trusted = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = CreateVerifier(trusted);

        Assert.Throws<LicenseEnvelopeVerificationException>(() =>
            verifier.Verify(Sign(CreateClaims(), attacker), "request-1", "software-1", "machine-1"));
    }

    [Fact]
    public void EnvelopeRejectsPayloadChangedAfterSigning()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = CreateVerifier(signer);
        var envelope = JsonSerializer.Deserialize<SignedLicenseEnvelope>(Sign(CreateClaims(), signer))!;
        var payload = Encoding.UTF8.GetString(Convert.FromBase64String(envelope.Payload))
            .Replace("package-1", "package-2", StringComparison.Ordinal);
        var changed = envelope with { Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)) };

        Assert.Throws<LicenseEnvelopeVerificationException>(() =>
            verifier.Verify(JsonSerializer.SerializeToUtf8Bytes(changed), "request-1", "software-1", "machine-1"));
    }

    [Theory]
    [InlineData("other-request", "software-1", "machine-1")]
    [InlineData("request-1", "other-software", "machine-1")]
    [InlineData("request-1", "software-1", "other-machine")]
    public void EnvelopeRejectsMismatchedBoundFields(string requestId, string softwareId, string machineHash)
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = CreateVerifier(signer);

        Assert.Throws<LicenseEnvelopeVerificationException>(() =>
            verifier.Verify(Sign(CreateClaims(), signer), requestId, softwareId, machineHash));
    }

    [Fact]
    public void EnvelopeRejectsUnknownKeyId()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = CreateVerifier(signer);

        Assert.Throws<LicenseEnvelopeVerificationException>(() =>
            verifier.Verify(Sign(CreateClaims(), signer, "unknown-key"), "request-1", "software-1", "machine-1"));
    }

    [Fact]
    public void EnvelopeRejectsUnknownDecisionCodePairAfterSignatureVerification()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = CreateVerifier(signer);
        var claims = CreateClaims() with { Code = "future_code" };

        Assert.Throws<LicenseEnvelopeVerificationException>(() =>
            verifier.Verify(Sign(claims, signer), "request-1", "software-1", "machine-1"));
    }

    [Theory]
    [InlineData(-301)]
    [InlineData(301)]
    public void EnvelopeRejectsStaleOrFutureServerTime(int offsetSeconds)
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = CreateVerifier(signer);
        var serverTime = Now.AddSeconds(offsetSeconds);
        var claims = CreateClaims() with
        {
            ServerTime = serverTime,
            LeaseUntil = serverTime.AddMinutes(30),
        };

        Assert.Throws<LicenseEnvelopeVerificationException>(() =>
            verifier.Verify(Sign(claims, signer), "request-1", "software-1", "machine-1"));
    }

    [Fact]
    public void EnvelopeRejectsLeaseLongerThanThirtyMinutes()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = CreateVerifier(signer);
        var claims = CreateClaims() with { LeaseUntil = Now.AddMinutes(30).AddTicks(1) };

        Assert.Throws<LicenseEnvelopeVerificationException>(() =>
            verifier.Verify(Sign(claims, signer), "request-1", "software-1", "machine-1"));
    }

    [Fact]
    public void EnvelopeRejectsLeaseBeyondCardExpiry()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = CreateVerifier(signer);
        var claims = CreateClaims() with
        {
            ExpiresAt = Now.AddMinutes(10),
            LeaseUntil = Now.AddMinutes(11),
        };

        Assert.Throws<LicenseEnvelopeVerificationException>(() =>
            verifier.Verify(Sign(claims, signer), "request-1", "software-1", "machine-1"));
    }

    [Fact]
    public void EnvelopeRejectsLeaseAlreadyExpiredByLocalTrustedTime()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = CreateVerifier(signer);
        var serverTime = Now.AddMinutes(-4);
        var claims = CreateClaims() with
        {
            ServerTime = serverTime,
            LeaseUntil = Now.AddMinutes(-1),
        };

        Assert.Throws<LicenseEnvelopeVerificationException>(() =>
            verifier.Verify(Sign(claims, signer), "request-1", "software-1", "machine-1"));
    }

    [Fact]
    public void CachedEnvelopeAllowsStaleServerTimeButReverifiesSignatureBindingAndCurrentLease()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = CreateVerifier(signer);
        var claims = CreateClaims() with
        {
            ServerTime = Now.AddMinutes(-10),
            LeaseUntil = Now.AddMinutes(20),
        };
        var envelope = Sign(claims, signer);

        var verified = verifier.VerifyCached(envelope, "software-1", "machine-1");

        Assert.Equal(claims, verified);
        Assert.Throws<LicenseEnvelopeVerificationException>(() =>
            verifier.VerifyCached(envelope, "software-other", "machine-1"));
        Assert.Throws<LicenseEnvelopeVerificationException>(() =>
            verifier.VerifyCached(envelope, "software-1", "machine-other"));
    }

    [Fact]
    public void CachedEnvelopeRejectsTamperingExpiryAndFutureServerTime()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = CreateVerifier(signer);
        var valid = CreateClaims();
        var parsed = JsonSerializer.Deserialize<SignedLicenseEnvelope>(Sign(valid, signer))!;
        var tamperedPayload = Convert.FromBase64String(parsed.Payload);
        tamperedPayload[^2] ^= 1;
        var tampered = JsonSerializer.SerializeToUtf8Bytes(parsed with { Payload = Convert.ToBase64String(tamperedPayload) });

        Assert.Throws<LicenseEnvelopeVerificationException>(() =>
            verifier.VerifyCached(tampered, "software-1", "machine-1"));
        Assert.Throws<LicenseEnvelopeVerificationException>(() => verifier.VerifyCached(
            Sign(valid with { LeaseUntil = Now }, signer), "software-1", "machine-1"));
        Assert.Throws<LicenseEnvelopeVerificationException>(() => verifier.VerifyCached(
            Sign(valid with { ServerTime = Now.AddMinutes(6), LeaseUntil = Now.AddMinutes(30) }, signer),
            "software-1", "machine-1"));
    }

    [Fact]
    public void LicenseApiClientOptionsRequireHttpsExactlyTwoDistinctLowercasePinsAndP256Key()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = signer.ExportSubjectPublicKeyInfoPem();
        var pinA = new string('a', 64);
        var pinB = new string('b', 64);

        _ = new LicenseClientOptions("software-1", new Uri("https://license.example/"), "key-1", pem, [pinA, pinB]);

        Assert.Throws<ArgumentException>(() => new LicenseClientOptions("software-1", new Uri("http://license.example/"), "key-1", pem, [pinA, pinB]));
        Assert.Throws<ArgumentException>(() => new LicenseClientOptions("software-1", new Uri("https://license.example/"), "key-1", pem, [pinA]));
        Assert.Throws<ArgumentException>(() => new LicenseClientOptions("software-1", new Uri("https://license.example/"), "key-1", pem, [pinA, pinA]));
        Assert.Throws<ArgumentException>(() => new LicenseClientOptions("software-1", new Uri("https://license.example/"), "key-1", pem, [pinA.ToUpperInvariant(), pinB]));
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<ArgumentException>(() => new LicenseClientOptions("software-1", new Uri("https://license.example/"), "key-1", p384.ExportSubjectPublicKeyInfoPem(), [pinA, pinB]));
        Assert.Throws<ArgumentException>(() => new LicenseClientOptions("software-1", new Uri("https://license.example/"), "key-1", signer.ExportECPrivateKeyPem(), [pinA, pinB]));
    }

    [Fact]
    public void LicenseApiClientOptionsAllowOnlyExplicitIpv4LoopbackHttpInAcceptanceBuilds()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = signer.ExportSubjectPublicKeyInfoPem();
        string[] pins = [new string('a', 64), new string('b', 64)];

        _ = new LicenseClientOptions("software-1", new Uri("http://127.0.0.1:19091/"), "key-1", pem, [], allowLoopbackHttpForAcceptance: true, isAcceptanceBuild: true);

        Assert.Throws<ArgumentException>(() => new LicenseClientOptions("software-1", new Uri("http://127.0.0.1:19091/"), "key-1", pem, pins, allowLoopbackHttpForAcceptance: true, isAcceptanceBuild: false));
        Assert.Throws<ArgumentException>(() => new LicenseClientOptions("software-1", new Uri("http://localhost:19091/"), "key-1", pem, pins, allowLoopbackHttpForAcceptance: true, isAcceptanceBuild: true));
        Assert.Throws<ArgumentException>(() => new LicenseClientOptions("software-1", new Uri("http://[::1]:19091/"), "key-1", pem, pins, allowLoopbackHttpForAcceptance: true, isAcceptanceBuild: true));
    }

    [Fact]
    public void LicenseApiClientOptionsAcceptEitherConfiguredSpkiPinAndRejectUnpinnedOrInvalidCertificates()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var currentKey = RSA.Create(2048);
        using var nextKey = RSA.Create(2048);
        using var otherKey = RSA.Create(2048);
        using var current = CreateCertificate("current", currentKey);
        using var next = CreateCertificate("next", nextKey);
        using var other = CreateCertificate("other", otherKey);
        var options = new LicenseClientOptions(
            "software-1",
            new Uri("https://license.example/"),
            "key-1",
            signingKey.ExportSubjectPublicKeyInfoPem(),
            [LicenseClientOptions.ComputeSpkiSha256Pin(current), LicenseClientOptions.ComputeSpkiSha256Pin(next)]);

        Assert.True(options.ValidateServerCertificate(current, SslPolicyErrors.None));
        Assert.True(options.ValidateServerCertificate(next, SslPolicyErrors.None));
        Assert.False(options.ValidateServerCertificate(other, SslPolicyErrors.None));
        Assert.False(options.ValidateServerCertificate(current, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(options.ValidateServerCertificate(null, SslPolicyErrors.None));
    }

    private static SignedLicenseEnvelopeVerifier CreateVerifier(ECDsa signer)
    {
        var options = CreateOptions(signer);
        return new SignedLicenseEnvelopeVerifier(options, new FixedTimeProvider(Now));
    }

    internal static LicenseClientOptions CreateOptions(ECDsa signer, int maximumResponseBytes = 64 * 1024) =>
        new(
            "software-1",
            new Uri("https://license.example/"),
            "key-current",
            signer.ExportSubjectPublicKeyInfoPem(),
            [new string('a', 64), new string('b', 64)],
            maximumResponseBytes: maximumResponseBytes);

    internal static LicenseClaims CreateClaims(
        string requestId = "request-1",
        string decision = LicenseDecisions.Allow,
        string code = LicenseCodes.Ok) =>
        new(
            LicenseProtocol.Version,
            requestId,
            Now,
            decision,
            code,
            decision == LicenseDecisions.Allow ? "card-1" : null,
            decision == LicenseDecisions.Allow ? "software-1" : null,
            decision == LicenseDecisions.Allow ? "machine-1" : null,
            decision == LicenseDecisions.Allow ? "package-1" : null,
            decision == LicenseDecisions.Allow ? Now.AddDays(1) : null,
            decision == LicenseDecisions.Allow ? Now.AddMinutes(30) : null);

    internal static byte[] Sign(LicenseClaims claims, ECDsa signer, string keyId = "key-current")
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(claims);
        var signature = signer.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return JsonSerializer.SerializeToUtf8Bytes(new SignedLicenseEnvelope(
            keyId,
            Convert.ToBase64String(payload),
            Convert.ToBase64String(signature)));
    }

    private static X509Certificate2 CreateCertificate(string commonName, RSA key)
    {
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(Now.AddDays(-1), Now.AddDays(1));
    }

    internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
