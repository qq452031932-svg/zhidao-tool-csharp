using System.Collections.ObjectModel;
using System.Text;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Infrastructure.Security;

public sealed class SessionEnvelope
{
    public const int CurrentVersion = 1;
    public const int MaxCookieCount = 128;
    public const int MaxCookieNameLength = 256;
    public const int MaxSerializedBytes = 256 * 1024;
    public const int MaxBaiduUserIdLength = 256;

    public SessionEnvelope(
        int version,
        Guid localAccountId,
        string baiduUserId,
        DateTimeOffset capturedAtUtc,
        IEnumerable<SessionCookie> cookies)
    {
        if (version != CurrentVersion) throw new NotSupportedException("The session envelope version is not supported.");
        if (localAccountId == Guid.Empty) throw new ArgumentException("A local account ID is required.", nameof(localAccountId));
        ArgumentException.ThrowIfNullOrWhiteSpace(baiduUserId);
        if (baiduUserId.Length > MaxBaiduUserIdLength || baiduUserId.Any(char.IsControl))
            throw new ArgumentException("The account identity is invalid or too large.", nameof(baiduUserId));
        ArgumentNullException.ThrowIfNull(cookies);

        var cookieSnapshot = cookies.Select(cookie => cookie ?? throw new ArgumentException("Cookies cannot contain null entries.", nameof(cookies))).ToArray();
        if (cookieSnapshot.Length is 0 or > MaxCookieCount)
            throw new ArgumentOutOfRangeException(nameof(cookies), "The cookie count is outside the supported range.");
        if (cookieSnapshot.Any(cookie => cookie.Name.Length > MaxCookieNameLength))
            throw new ArgumentException("A cookie name is too large.", nameof(cookies));
        if (cookieSnapshot.GroupBy(cookie => (cookie.Name, cookie.Domain, cookie.Path)).Any(group => group.Count() > 1))
            throw new ArgumentException("Duplicate cookie identities are not allowed.", nameof(cookies));

        var captured = capturedAtUtc.ToUniversalTime();
        if (captured == default || cookieSnapshot.Any(cookie => cookie.ExpiresUtc is { } expiry && expiry < captured))
            throw new ArgumentException("Cookie expiry metadata is invalid.", nameof(cookies));

        Version = version;
        LocalAccountId = localAccountId;
        BaiduUserId = baiduUserId.Trim();
        CapturedAtUtc = captured;
        Cookies = new ReadOnlyCollection<SessionCookie>(cookieSnapshot);
    }

    public int Version { get; }
    public Guid LocalAccountId { get; }
    public string BaiduUserId { get; }
    public DateTimeOffset CapturedAtUtc { get; }
    public IReadOnlyList<SessionCookie> Cookies { get; }

    public static SessionEnvelope FromSession(AccountSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.LocalAccountId is not { } localAccountId || string.IsNullOrWhiteSpace(session.BaiduUserId))
            throw new ArgumentException("The session is not bound to a local and remote account identity.", nameof(session));
        return new(CurrentVersion, localAccountId, session.BaiduUserId, session.CapturedAtUtc, session.SessionCookies);
    }

    public AccountSession ToSession() => new(LocalAccountId, BaiduUserId, Cookies, CapturedAtUtc);

    public override string ToString() =>
        $"SessionEnvelope {{ Version = {Version}, AccountBinding = [REDACTED], Cookies = [REDACTED], CookieCount = {Cookies.Count} }}";

    internal static int GetUtf8Size(string value) => Encoding.UTF8.GetByteCount(value);
}
