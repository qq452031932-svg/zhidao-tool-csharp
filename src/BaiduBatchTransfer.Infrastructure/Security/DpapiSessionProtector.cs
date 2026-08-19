using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Infrastructure.Security;

public sealed class DpapiSessionProtector : ISessionProtector
{
    public const string DefaultPurpose = "BaiduBatchTransfer/AccountSession/v1";
    private const int MaxCiphertextBytes = SessionEnvelope.MaxSerializedBytes + 16 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false
    };

    private readonly byte[] entropy;

    public DpapiSessionProtector(string purpose = DefaultPurpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        if (purpose.Length > 1024) throw new ArgumentOutOfRangeException(nameof(purpose));
        entropy = DeriveEntropy(purpose);
    }

    public SessionProtectionResult Protect(AccountSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!OperatingSystem.IsWindows())
            return Failure(SessionProtectionFailure.InvalidSession);

        byte[]? plaintext = null;
        try
        {
            var envelope = SessionEnvelope.FromSession(session);
            plaintext = JsonSerializer.SerializeToUtf8Bytes(Payload.From(envelope), SerializerOptions);
            if (plaintext.Length > SessionEnvelope.MaxSerializedBytes)
                return Failure(SessionProtectionFailure.InvalidSession);

            var ciphertext = ProtectedData.Protect(plaintext, entropy, DataProtectionScope.CurrentUser);
            return SessionProtectionResult.Protected(ciphertext);
        }
        catch (ArgumentException)
        {
            return Failure(SessionProtectionFailure.InvalidSession);
        }
        catch (NotSupportedException)
        {
            return Failure(SessionProtectionFailure.UnsupportedVersion);
        }
        catch (CryptographicException)
        {
            return Failure(SessionProtectionFailure.InvalidSession);
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public SessionProtectionResult Unprotect(
        ReadOnlySpan<byte> protectedBytes,
        Guid expectedLocalAccountId,
        string expectedBaiduUserId)
    {
        if (!OperatingSystem.IsWindows() || protectedBytes.IsEmpty || protectedBytes.Length > MaxCiphertextBytes ||
            expectedLocalAccountId == Guid.Empty || string.IsNullOrWhiteSpace(expectedBaiduUserId))
        {
            return Failure(SessionProtectionFailure.InvalidCiphertext);
        }

        byte[]? ciphertext = null;
        byte[]? plaintext = null;
        try
        {
            ciphertext = protectedBytes.ToArray();
            plaintext = ProtectedData.Unprotect(ciphertext, entropy, DataProtectionScope.CurrentUser);
            if (plaintext.Length == 0 || plaintext.Length > SessionEnvelope.MaxSerializedBytes)
                return Failure(SessionProtectionFailure.InvalidCiphertext);

            var payload = JsonSerializer.Deserialize<Payload>(plaintext, SerializerOptions);
            if (payload is null) return Failure(SessionProtectionFailure.InvalidCiphertext);
            if (payload.Version != SessionEnvelope.CurrentVersion)
                return Failure(SessionProtectionFailure.UnsupportedVersion);

            var envelope = payload.ToEnvelope();
            if (envelope.LocalAccountId != expectedLocalAccountId ||
                !string.Equals(envelope.BaiduUserId, expectedBaiduUserId, StringComparison.Ordinal))
            {
                return Failure(SessionProtectionFailure.BindingMismatch);
            }

            return SessionProtectionResult.Unprotected(envelope.ToSession());
        }
        catch (CryptographicException)
        {
            return Failure(SessionProtectionFailure.InvalidCiphertext);
        }
        catch (JsonException)
        {
            return Failure(SessionProtectionFailure.InvalidCiphertext);
        }
        catch (ArgumentException)
        {
            return Failure(SessionProtectionFailure.InvalidCiphertext);
        }
        catch (NotSupportedException)
        {
            return Failure(SessionProtectionFailure.UnsupportedVersion);
        }
        finally
        {
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal static byte[] DeriveEntropy(string purpose) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(purpose));

    private static SessionProtectionResult Failure(SessionProtectionFailure failure) =>
        SessionProtectionResult.Failed(failure, "The saved session could not be read safely.");

    private sealed class Payload
    {
        public int Version { get; set; }
        public Guid LocalAccountId { get; set; }
        public string? BaiduUserId { get; set; }
        public DateTimeOffset CapturedAtUtc { get; set; }
        public CookiePayload[]? Cookies { get; set; }

        public static Payload From(SessionEnvelope envelope) => new()
        {
            Version = envelope.Version,
            LocalAccountId = envelope.LocalAccountId,
            BaiduUserId = envelope.BaiduUserId,
            CapturedAtUtc = envelope.CapturedAtUtc,
            Cookies = envelope.Cookies.Select(CookiePayload.From).ToArray()
        };

        public SessionEnvelope ToEnvelope()
        {
            if (BaiduUserId is null || Cookies is null) throw new JsonException("Required session data is missing.");
            if (Cookies.Any(cookie => cookie is null)) throw new JsonException("A cookie entry is invalid.");
            return new SessionEnvelope(
                Version,
                LocalAccountId,
                BaiduUserId,
                CapturedAtUtc,
                Cookies.Select(cookie => cookie.ToCookie()));
        }
    }

    private sealed class CookiePayload
    {
        public string? Name { get; set; }
        public string? Value { get; set; }
        public string? Domain { get; set; }
        public string? Path { get; set; }
        public DateTimeOffset? ExpiresUtc { get; set; }
        public bool IsSecure { get; set; }
        public bool IsHttpOnly { get; set; }

        public static CookiePayload From(SessionCookie cookie) => new()
        {
            Name = cookie.Name,
            Value = cookie.Value,
            Domain = cookie.Domain,
            Path = cookie.Path,
            ExpiresUtc = cookie.ExpiresUtc,
            IsSecure = cookie.IsSecure,
            IsHttpOnly = cookie.IsHttpOnly
        };

        public SessionCookie ToCookie()
        {
            if (Name is null || Value is null || Domain is null || Path is null)
                throw new JsonException("Required cookie data is missing.");
            return new SessionCookie(Name, Value, Domain, Path, ExpiresUtc, IsSecure, IsHttpOnly);
        }
    }
}
