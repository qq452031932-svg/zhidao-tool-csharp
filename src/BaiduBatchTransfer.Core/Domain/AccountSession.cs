using System.Collections.ObjectModel;

namespace BaiduBatchTransfer.Core.Domain;

public sealed class AccountSession
{
    public AccountSession(
        string captureId,
        IEnumerable<SessionCookie> cookies,
        DateTimeOffset capturedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        ArgumentNullException.ThrowIfNull(cookies);
        var snapshot = Snapshot(cookies);
        AccountId = captureId.Trim();
        SessionCookies = Array.AsReadOnly(snapshot);
        Cookies = CreateCompatibilityCookieView(snapshot);
        CapturedAtUtc = capturedAt.ToUniversalTime();
    }

    public AccountSession(
        Guid localAccountId,
        string baiduUserId,
        IEnumerable<SessionCookie> cookies,
        DateTimeOffset capturedAt)
    {
        if (localAccountId == Guid.Empty) throw new ArgumentException("A local account ID is required.", nameof(localAccountId));
        ArgumentException.ThrowIfNullOrWhiteSpace(baiduUserId);
        ArgumentNullException.ThrowIfNull(cookies);

        var snapshot = Snapshot(cookies);

        LocalAccountId = localAccountId;
        BaiduUserId = baiduUserId.Trim();
        AccountId = localAccountId.ToString("D");
        SessionCookies = Array.AsReadOnly(snapshot);
        Cookies = CreateCompatibilityCookieView(snapshot);
        CapturedAtUtc = capturedAt.ToUniversalTime();
    }

    public string AccountId { get; }

    public Guid? LocalAccountId { get; }

    public string? BaiduUserId { get; }

    public IReadOnlyDictionary<string, string> Cookies { get; }

    public IReadOnlyList<SessionCookie> SessionCookies { get; }

    public DateTimeOffset CapturedAtUtc { get; }

    public bool IsBoundTo(Guid localAccountId, string baiduUserId) =>
        LocalAccountId == localAccountId && string.Equals(BaiduUserId, baiduUserId, StringComparison.Ordinal);

    public AccountSession Bind(Guid localAccountId, string baiduUserId) =>
        new(localAccountId, baiduUserId, SessionCookies, CapturedAtUtc);

    public override string ToString() =>
        "AccountSession { AccountBinding = [REDACTED], Cookies = [REDACTED] }";

    private static SessionCookie[] Snapshot(IEnumerable<SessionCookie> cookies)
    {
        var snapshot = cookies.Select(cookie =>
            cookie ?? throw new ArgumentException("Cookies cannot contain null entries.", nameof(cookies))).ToArray();
        if (snapshot.Length == 0) throw new ArgumentException("At least one session cookie is required.", nameof(cookies));
        return snapshot;
    }

    private static IReadOnlyDictionary<string, string> CreateCompatibilityCookieView(SessionCookie[] cookies) =>
        new ReadOnlyDictionary<string, string>(cookies
            .GroupBy(cookie => cookie.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal));
}
