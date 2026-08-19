using System.Collections.ObjectModel;

namespace BaiduBatchTransfer.Core.Domain;

public sealed class AccountSession
{
    public AccountSession(
        string accountId,
        IReadOnlyDictionary<string, string> cookies,
        DateTimeOffset capturedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(cookies);

        var cookieSnapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in cookies)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(cookies));
            ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(cookies));
            cookieSnapshot.Add(name, value);
        }

        if (cookieSnapshot.Count == 0)
        {
            throw new ArgumentException("At least one session cookie is required.", nameof(cookies));
        }

        AccountId = accountId.Trim();
        Cookies = new ReadOnlyDictionary<string, string>(cookieSnapshot);
        SessionCookies = Array.AsReadOnly(cookieSnapshot.Select(pair => new SessionCookie(
            pair.Key,
            pair.Value,
            ".baidu.com",
            "/",
            capturedAt.ToUniversalTime().AddDays(1),
            isSecure: true,
            isHttpOnly: true)).ToArray());
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

        var snapshot = cookies.Select(cookie => cookie ?? throw new ArgumentException("Cookies cannot contain null entries.", nameof(cookies))).ToArray();
        if (snapshot.Length == 0) throw new ArgumentException("At least one session cookie is required.", nameof(cookies));

        LocalAccountId = localAccountId;
        BaiduUserId = baiduUserId.Trim();
        AccountId = localAccountId.ToString("D");
        SessionCookies = Array.AsReadOnly(snapshot);
        Cookies = new ReadOnlyDictionary<string, string>(snapshot
            .GroupBy(cookie => cookie.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal));
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
}
