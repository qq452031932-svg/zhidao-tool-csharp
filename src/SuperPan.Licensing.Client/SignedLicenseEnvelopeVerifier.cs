using System.Security.Cryptography;
using System.Text.Json;

namespace SuperPan.Licensing.Client;

public interface ICachedLicenseEnvelopeVerifier
{
    LicenseClaims VerifyCached(ReadOnlySpan<byte> signedEnvelope, string expectedSoftwareId, string expectedMachineHash);
}

public sealed class LicenseEnvelopeVerificationException : Exception
{
    public LicenseEnvelopeVerificationException(string message) : base(message)
    {
    }
}

public sealed class SignedLicenseEnvelopeVerifier : IDisposable, ICachedLicenseEnvelopeVerifier
{
    private static readonly HashSet<string> EnvelopeFields = new(StringComparer.Ordinal)
    {
        "key_id", "payload", "signature",
    };

    private static readonly HashSet<string> ClaimsFields = new(StringComparer.Ordinal)
    {
        "protocol_version", "request_id", "server_time", "decision", "code", "card_id",
        "software_id", "machine_hash", "package_name", "expires_at", "lease_until",
    };

    private readonly LicenseClientOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ECDsa publicKey;
    private bool disposed;

    public SignedLicenseEnvelopeVerifier(LicenseClientOptions options, TimeProvider? timeProvider = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        publicKey = options.CreateSigningPublicKey();
    }

    public LicenseClaims Verify(
        ReadOnlySpan<byte> responseBody,
        string expectedRequestId,
        string expectedSoftwareId,
        string expectedMachineHash)
    {
        var claims = VerifySignatureAndParse(responseBody);
        ValidateBindingsAndTime(claims, expectedRequestId, expectedSoftwareId, expectedMachineHash);
        return claims;
    }

    public LicenseClaims VerifyCached(
        ReadOnlySpan<byte> signedEnvelope,
        string expectedSoftwareId,
        string expectedMachineHash)
    {
        var claims = VerifySignatureAndParse(signedEnvelope);
        ValidateCachedBindingsAndTime(claims, expectedSoftwareId, expectedMachineHash);
        return claims;
    }

    private LicenseClaims VerifySignatureAndParse(ReadOnlySpan<byte> responseBody)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (responseBody.IsEmpty || responseBody.Length > options.MaximumResponseBytes)
        {
            throw Invalid("The signed response size is invalid.");
        }

        SignedLicenseEnvelope envelope;
        try
        {
            using var document = JsonDocument.Parse(responseBody.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            ValidateShape(document.RootElement, EnvelopeFields);
            envelope = new SignedLicenseEnvelope(
                RequiredString(document.RootElement, "key_id"),
                RequiredString(document.RootElement, "payload"),
                RequiredString(document.RootElement, "signature"));
            LicenseContractValidator.Validate(envelope);
        }
        catch (LicenseEnvelopeVerificationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            throw Invalid("The signed envelope is malformed.");
        }

        if (!string.Equals(envelope.KeyId, options.SigningKeyId, StringComparison.Ordinal))
        {
            throw Invalid("The response signing key is not trusted.");
        }

        byte[] payload;
        byte[] signature;
        try
        {
            payload = Convert.FromBase64String(envelope.Payload);
            signature = Convert.FromBase64String(envelope.Signature);
        }
        catch (FormatException)
        {
            throw Invalid("The signed envelope encoding is malformed.");
        }
        if (payload.Length == 0 || payload.Length > options.MaximumResponseBytes || signature.Length == 0)
        {
            throw Invalid("The signed envelope encoding is invalid.");
        }

        bool signatureIsValid;
        try
        {
            signatureIsValid = publicKey.VerifyData(
                payload,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            signatureIsValid = false;
        }
        if (!signatureIsValid)
        {
            throw Invalid("The response signature is invalid.");
        }

        LicenseClaims claims;
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
            ValidateShape(document.RootElement, ClaimsFields);
            claims = JsonSerializer.Deserialize<LicenseClaims>(payload) ?? throw new JsonException();
            LicenseContractValidator.Validate(claims);
        }
        catch (LicenseEnvelopeVerificationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            throw Invalid("The signed claims are invalid.");
        }

        return claims;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        publicKey.Dispose();
    }

    private void ValidateBindingsAndTime(
        LicenseClaims claims,
        string expectedRequestId,
        string expectedSoftwareId,
        string expectedMachineHash)
    {
        if (!string.Equals(claims.RequestId, expectedRequestId, StringComparison.Ordinal))
        {
            throw Invalid("The response request binding is invalid.");
        }
        if (claims.SoftwareId is not null && !string.Equals(claims.SoftwareId, expectedSoftwareId, StringComparison.Ordinal))
        {
            throw Invalid("The response software binding is invalid.");
        }
        if (claims.MachineHash is not null && !string.Equals(claims.MachineHash, expectedMachineHash, StringComparison.Ordinal))
        {
            throw Invalid("The response machine binding is invalid.");
        }

        var now = timeProvider.GetUtcNow();
        var difference = claims.ServerTime - now;
        if (difference < -options.ServerClockTolerance || difference > options.ServerClockTolerance)
        {
            throw Invalid("The response server time is outside the accepted window.");
        }

        if (claims.Decision != LicenseDecisions.Allow)
        {
            return;
        }
        var expiresAt = claims.ExpiresAt!.Value;
        var leaseUntil = claims.LeaseUntil!.Value;
        if (expiresAt <= now || leaseUntil <= now || expiresAt < claims.ServerTime || leaseUntil <= claims.ServerTime ||
            leaseUntil > claims.ServerTime.AddMinutes(30) || leaseUntil > expiresAt)
        {
            throw Invalid("The response lease is invalid.");
        }
    }

    private void ValidateCachedBindingsAndTime(
        LicenseClaims claims,
        string expectedSoftwareId,
        string expectedMachineHash)
    {
        if (claims.Decision != LicenseDecisions.Allow || claims.Code != LicenseCodes.Ok ||
            !string.Equals(claims.SoftwareId, expectedSoftwareId, StringComparison.Ordinal) ||
            !string.Equals(claims.MachineHash, expectedMachineHash, StringComparison.Ordinal))
        {
            throw Invalid("The cached response binding is invalid.");
        }

        var now = timeProvider.GetUtcNow();
        if (claims.ServerTime - now > options.ServerClockTolerance)
        {
            throw Invalid("The cached response server time is invalid.");
        }

        var expiresAt = claims.ExpiresAt!.Value;
        var leaseUntil = claims.LeaseUntil!.Value;
        if (expiresAt <= now || leaseUntil <= now || expiresAt < claims.ServerTime || leaseUntil <= claims.ServerTime ||
            leaseUntil > claims.ServerTime.AddMinutes(30) || leaseUntil > expiresAt)
        {
            throw Invalid("The cached response lease is invalid.");
        }
    }

    private static void ValidateShape(JsonElement root, HashSet<string> expectedFields)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("The signed value must be a JSON object.");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!expectedFields.Contains(property.Name) || !seen.Add(property.Name))
            {
                throw Invalid("The signed value contains unexpected or duplicate fields.");
            }
        }
        if (seen.Count != expectedFields.Count)
        {
            throw Invalid("The signed value is missing required fields.");
        }
    }

    private static string RequiredString(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid("The signed envelope field type is invalid.");
        }
        return value.GetString() ?? throw Invalid("The signed envelope field is empty.");
    }

    private static LicenseEnvelopeVerificationException Invalid(string message) => new(message);
}
