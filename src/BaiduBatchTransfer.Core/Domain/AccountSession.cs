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
        CapturedAtUtc = capturedAt.ToUniversalTime();
    }

    public string AccountId { get; }

    public IReadOnlyDictionary<string, string> Cookies { get; }

    public DateTimeOffset CapturedAtUtc { get; }
}
