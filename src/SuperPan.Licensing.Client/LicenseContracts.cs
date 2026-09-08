using System.Text;
using System.Text.Json.Serialization;

namespace SuperPan.Licensing.Client;

public static class LicenseProtocol
{
    public const int Version = 1;
    public const int MaxIdentifierLength = 128;
}

public static class LicenseDecisions
{
    public const string Allow = "allow";
    public const string Deny = "deny";
}

public static class LicenseCodes
{
    public const string Ok = "ok";
    public const string InvalidRequest = "invalid_request";
    public const string UnsupportedProtocol = "unsupported_protocol";
    public const string LicenseNotFound = "license_not_found";
    public const string LicenseExpired = "license_expired";
    public const string AppNotAllowed = "app_not_allowed";
    public const string AppDisabled = "app_disabled";
    public const string MachineMismatch = "machine_mismatch";
    public const string RateLimited = "rate_limited";
    public const string ServiceUnavailable = "service_unavailable";

    public static IReadOnlyList<string> All { get; } =
    [
        Ok,
        InvalidRequest,
        UnsupportedProtocol,
        LicenseNotFound,
        LicenseExpired,
        AppNotAllowed,
        AppDisabled,
        MachineMismatch,
        RateLimited,
        ServiceUnavailable,
    ];
}

public sealed record SignedLicenseEnvelope(
    [property: JsonPropertyName("key_id")] string KeyId,
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("signature")] string Signature);

public sealed record LicenseClaims(
    [property: JsonPropertyName("protocol_version")] int ProtocolVersion,
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("server_time")] DateTimeOffset ServerTime,
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("card_id")] string? CardId,
    [property: JsonPropertyName("software_id")] string? SoftwareId,
    [property: JsonPropertyName("machine_hash")] string? MachineHash,
    [property: JsonPropertyName("package_name")] string? PackageName,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("lease_until")] DateTimeOffset? LeaseUntil);

public static class LicenseContractValidator
{
    private static readonly HashSet<string> DenialCodes =
    [
        LicenseCodes.InvalidRequest,
        LicenseCodes.UnsupportedProtocol,
        LicenseCodes.LicenseNotFound,
        LicenseCodes.LicenseExpired,
        LicenseCodes.AppNotAllowed,
        LicenseCodes.AppDisabled,
        LicenseCodes.MachineMismatch,
        LicenseCodes.RateLimited,
        LicenseCodes.ServiceUnavailable,
    ];

    public static void Validate(SignedLicenseEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateIdentifier(envelope.KeyId, "key_id");
        ArgumentException.ThrowIfNullOrEmpty(envelope.Payload, "payload");
        ArgumentException.ThrowIfNullOrEmpty(envelope.Signature, "signature");
    }

    public static void Validate(LicenseClaims claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        if (claims.ProtocolVersion != LicenseProtocol.Version)
        {
            throw new ArgumentException(
                $"protocol_version must be {LicenseProtocol.Version}.",
                nameof(claims));
        }

        ValidateIdentifier(claims.RequestId, "request_id");
        ValidateOptionalIdentifier(claims.CardId, "card_id");
        ValidateOptionalIdentifier(claims.SoftwareId, "software_id");
        ValidateOptionalIdentifier(claims.MachineHash, "machine_hash");
        ValidateOptionalIdentifier(claims.PackageName, "package_name");

        var isAllowed = claims.Decision == LicenseDecisions.Allow && claims.Code == LicenseCodes.Ok;
        if (isAllowed)
        {
            ValidateIdentifier(claims.CardId, "card_id");
            ValidateIdentifier(claims.SoftwareId, "software_id");
            ValidateIdentifier(claims.MachineHash, "machine_hash");
            ValidateIdentifier(claims.PackageName, "package_name");
            if (claims.ExpiresAt is null)
            {
                throw new ArgumentException("expires_at is required for allowed claims.", nameof(claims));
            }
            if (claims.LeaseUntil is null)
            {
                throw new ArgumentException("lease_until is required for allowed claims.", nameof(claims));
            }
        }

        var pairIsKnown = isAllowed ||
            claims.Decision == LicenseDecisions.Deny && DenialCodes.Contains(claims.Code);
        if (!pairIsKnown)
        {
            throw new ArgumentException(
                $"Unknown decision/code pair '{claims.Decision}/{claims.Code}'.",
                nameof(claims));
        }
    }

    private static void ValidateIdentifier(string? value, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, name);
        if (Encoding.UTF8.GetByteCount(value) > LicenseProtocol.MaxIdentifierLength)
        {
            throw new ArgumentException(
                $"{name} exceeds {LicenseProtocol.MaxIdentifierLength} UTF-8 bytes.",
                name);
        }
    }

    private static void ValidateOptionalIdentifier(string? value, string name)
    {
        if (value is not null)
        {
            ValidateIdentifier(value, name);
        }
    }
}
