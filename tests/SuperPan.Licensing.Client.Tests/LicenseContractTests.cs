using System.Text.Json;
using SuperPan.Licensing.Client;

namespace SuperPan.Licensing.Client.Tests;

public sealed class LicenseContractTests
{
    [Fact]
    public void ProtocolConstantsAreStable()
    {
        Assert.Equal(1, LicenseProtocol.Version);
        Assert.Equal(
            [
                "ok",
                "invalid_request",
                "unsupported_protocol",
                "license_not_found",
                "license_expired",
                "app_not_allowed",
                "app_disabled",
                "machine_mismatch",
                "rate_limited",
                "service_unavailable",
            ],
            LicenseCodes.All);
    }

    [Fact]
    public void SignedEnvelopeUsesStableJsonFieldNames()
    {
        var envelope = new SignedLicenseEnvelope(
            "license-signing-local-1",
            "cGF5bG9hZA==",
            "c2lnbmF0dXJl");

        var json = JsonSerializer.Serialize(envelope);

        Assert.Equal(
            "{\"key_id\":\"license-signing-local-1\",\"payload\":\"cGF5bG9hZA==\",\"signature\":\"c2lnbmF0dXJl\"}",
            json);
    }

    [Fact]
    public void ClaimsUseStableJsonFieldNames()
    {
        var json = JsonSerializer.Serialize(CreateValidClaims());
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            [
                "protocol_version", "request_id", "server_time", "decision", "code",
                "card_id", "software_id", "machine_hash", "package_name", "expires_at", "lease_until",
            ],
            document.RootElement.EnumerateObject().Select(property => property.Name));
    }

    [Theory]
    [InlineData("maybe", LicenseCodes.Ok)]
    [InlineData(LicenseDecisions.Deny, "other")]
    [InlineData(LicenseDecisions.Allow, LicenseCodes.LicenseExpired)]
    [InlineData(LicenseDecisions.Deny, LicenseCodes.Ok)]
    public void ClaimsRejectUnknownDecisionCodePairs(string decision, string code)
    {
        var claims = CreateValidClaims() with { Decision = decision, Code = code };

        Assert.ThrowsAny<ArgumentException>(() => LicenseContractValidator.Validate(claims));
    }

    [Fact]
    public void DenialClaimsDoNotRequireSuccessOnlyIdentifiers()
    {
        var claims = CreateValidClaims() with
        {
            Decision = LicenseDecisions.Deny,
            Code = LicenseCodes.LicenseNotFound,
            CardId = null,
            SoftwareId = null,
            MachineHash = null,
            PackageName = null,
            ExpiresAt = null,
            LeaseUntil = null,
        };

        LicenseContractValidator.Validate(claims);
    }

    [Fact]
    public void AllowedClaimsRequireSuccessOnlyFields()
    {
        var claims = CreateValidClaims() with { CardId = null };

        Assert.ThrowsAny<ArgumentException>(() => LicenseContractValidator.Validate(claims));
    }

    [Fact]
    public void ClaimsRejectOverlongIdentifiers()
    {
        var claims = CreateValidClaims() with
        {
            RequestId = new string('r', LicenseProtocol.MaxIdentifierLength + 1),
        };

        Assert.Throws<ArgumentException>(() => LicenseContractValidator.Validate(claims));
    }

    [Fact]
    public void SignedEnvelopeRejectsOverlongKeyId()
    {
        var envelope = new SignedLicenseEnvelope(
            new string('k', LicenseProtocol.MaxIdentifierLength + 1),
            "cGF5bG9hZA==",
            "c2lnbmF0dXJl");

        Assert.Throws<ArgumentException>(() => LicenseContractValidator.Validate(envelope));
    }

    private static LicenseClaims CreateValidClaims()
    {
        var now = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        return new LicenseClaims(
            LicenseProtocol.Version,
            "request-1",
            now,
            LicenseDecisions.Allow,
            LicenseCodes.Ok,
            "card-1",
            "software-1",
            "machine-1",
            "package-1",
            now.AddDays(1),
            now.AddMinutes(30));
    }
}
