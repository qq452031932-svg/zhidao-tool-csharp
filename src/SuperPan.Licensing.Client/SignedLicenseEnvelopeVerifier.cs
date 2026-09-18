using System.Security.Cryptography;
using System.Text.Json;

namespace SuperPan.Licensing.Client;

public interface ICachedLicenseEnvelopeVerifier
{
    LicenseClaims VerifyCached(ReadOnlySpan<byte> signedEnvelope, string expectedSoftwareId, string expectedMachineHash);

    // 启动与联网操作对"租约有多新"的要求本来就不同：批量转存、分享这些操作本身就要
    // 联网访问网盘，能连网盘就能连授权服务，所以它们继续要求租约未过期。而打开软件
    // 根本不需要联网，却因为同一条规则被一起卡住——服务器抖 30 分钟，所有付费用户
    // 都会被弹"未激活"。stalenessAllowance 只放宽"多久没去服务器复验"，卡密自身的
    // 到期时间(expires_at)依旧强制，过期卡拿不到任何宽限。
    LicenseClaims VerifyCached(
        ReadOnlySpan<byte> signedEnvelope,
        string expectedSoftwareId,
        string expectedMachineHash,
        TimeSpan stalenessAllowance);
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

    // 客户端能接受的最长"服务端签发租约"。原先钉死在 30 分钟，与服务端
    // validationLease 完全相等，于是服务端一旦想把租约放长，所有旧客户端会把
    // 合法回复判成非法信封、当场集体锁死——客户端与服务端被硬绑在一起。
    // 这个上限本身也拦不住什么：能签出更长租约的人已经握有签名私钥。放宽到 7 天
    // 是为了让将来调整服务端租约时不必先等客户端全量普及。
    public static readonly TimeSpan MaximumIssuedLease = TimeSpan.FromDays(7);

    // 启动时允许的最大"离线未复验"时长上限，防止调用方传进一个荒谬的值。
    public static readonly TimeSpan MaximumStalenessAllowance = TimeSpan.FromDays(30);

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
        string expectedMachineHash) =>
        VerifyCached(signedEnvelope, expectedSoftwareId, expectedMachineHash, TimeSpan.Zero);

    public LicenseClaims VerifyCached(
        ReadOnlySpan<byte> signedEnvelope,
        string expectedSoftwareId,
        string expectedMachineHash,
        TimeSpan stalenessAllowance)
    {
        if (stalenessAllowance < TimeSpan.Zero || stalenessAllowance > MaximumStalenessAllowance)
        {
            throw new ArgumentOutOfRangeException(nameof(stalenessAllowance));
        }
        var claims = VerifySignatureAndParse(signedEnvelope);
        ValidateCachedBindingsAndTime(claims, expectedSoftwareId, expectedMachineHash, stalenessAllowance);
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
            leaseUntil > claims.ServerTime.Add(MaximumIssuedLease) || leaseUntil > expiresAt)
        {
            throw Invalid("The response lease is invalid.");
        }
    }

    private void ValidateCachedBindingsAndTime(
        LicenseClaims claims,
        string expectedSoftwareId,
        string expectedMachineHash,
        TimeSpan stalenessAllowance)
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
        // expiresAt 是卡密买到的服务期，任何情况下都不放宽：过期的月卡不会因为离线
        // 而多用一天。放宽的只有 leaseUntil——"多久该回服务器复验一次"。
        if (expiresAt <= now || leaseUntil <= now - stalenessAllowance ||
            expiresAt < claims.ServerTime || leaseUntil <= claims.ServerTime ||
            leaseUntil > claims.ServerTime.Add(MaximumIssuedLease) || leaseUntil > expiresAt)
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
