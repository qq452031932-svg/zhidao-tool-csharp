using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Abstractions;

public interface ISessionProtector
{
    SessionProtectionResult Protect(AccountSession session);

    SessionProtectionResult Unprotect(
        ReadOnlySpan<byte> protectedBytes,
        Guid expectedLocalAccountId,
        string expectedBaiduUserId);
}

public enum SessionProtectionFailure
{
    None,
    InvalidSession,
    InvalidCiphertext,
    UnsupportedVersion,
    BindingMismatch
}

public sealed class SessionProtectionResult
{
    private readonly byte[]? protectedBytes;

    private SessionProtectionResult(
        byte[]? protectedBytes,
        AccountSession? session,
        SessionProtectionFailure failure,
        string? safeMessage)
    {
        this.protectedBytes = protectedBytes;
        Session = session;
        Failure = failure;
        SafeMessage = safeMessage;
    }

    public bool IsSuccess => Failure == SessionProtectionFailure.None;
    public byte[]? ProtectedBytes => protectedBytes is null ? null : (byte[])protectedBytes.Clone();
    public AccountSession? Session { get; }
    public SessionProtectionFailure Failure { get; }
    public string? SafeMessage { get; }

    public static SessionProtectionResult Protected(ReadOnlySpan<byte> protectedBytes) =>
        new(protectedBytes.ToArray(), null, SessionProtectionFailure.None, null);

    public static SessionProtectionResult Unprotected(AccountSession session) =>
        new(null, session ?? throw new ArgumentNullException(nameof(session)), SessionProtectionFailure.None, null);

    public static SessionProtectionResult Failed(SessionProtectionFailure failure, string safeMessage)
    {
        if (failure == SessionProtectionFailure.None) throw new ArgumentOutOfRangeException(nameof(failure));
        ArgumentException.ThrowIfNullOrWhiteSpace(safeMessage);
        return new(null, null, failure, safeMessage);
    }

    public override string ToString() =>
        $"SessionProtectionResult {{ IsSuccess = {IsSuccess}, SensitiveData = [REDACTED] }}";
}
