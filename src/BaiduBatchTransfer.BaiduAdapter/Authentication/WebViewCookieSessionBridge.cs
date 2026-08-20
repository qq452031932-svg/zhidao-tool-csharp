using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.Authentication;

public sealed record CapturedWebCookie(
    string Name,
    string Value,
    string Domain,
    string Path,
    DateTimeOffset? ExpiresUtc,
    bool IsSecure,
    bool IsHttpOnly)
{
    public override string ToString() => "CapturedWebCookie { SensitiveData = [REDACTED] }";
}

public sealed class WebViewCookieSessionBridge
{
    public AccountSession CreateUnboundSession(
        Guid captureId,
        IEnumerable<CapturedWebCookie> cookies,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        if (captureId == Guid.Empty) throw new ArgumentException("A capture ID is required.", nameof(captureId));
        ArgumentNullException.ThrowIfNull(cookies);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = new List<SessionCookie>();
        foreach (var cookie in cookies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(cookie);
            snapshot.Add(new SessionCookie(
                cookie.Name,
                cookie.Value,
                cookie.Domain,
                cookie.Path,
                cookie.ExpiresUtc,
                cookie.IsSecure,
                cookie.IsHttpOnly));
        }

        return new AccountSession(captureId.ToString("D"), snapshot, capturedAt);
    }
}
