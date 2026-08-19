namespace BaiduBatchTransfer.Core.Domain;

public sealed class BaiduAccount
{
    public BaiduAccount(
        Guid id,
        string baiduUserId,
        string displayName,
        string? encryptedSession,
        string? webView2ProfileDirectory,
        DateTimeOffset createdAt)
    {
        Id = id;
        BaiduUserId = RequireText(baiduUserId, nameof(baiduUserId));
        DisplayName = RequireText(displayName, nameof(displayName));
        EncryptedSession = encryptedSession;
        WebView2ProfileDirectory = webView2ProfileDirectory;
        Status = AccountStatus.Active;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; }

    public string BaiduUserId { get; }

    public string DisplayName { get; private set; }

    public string? EncryptedSession { get; private set; }

    public string? WebView2ProfileDirectory { get; private set; }

    public AccountStatus Status { get; private set; }

    public bool IsDeleted { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public void Rename(string displayName, DateTimeOffset updatedAt)
    {
        EnsureActive();
        EnsureTimestamp(updatedAt);
        DisplayName = RequireText(displayName, nameof(displayName));
        UpdatedAt = updatedAt;
    }

    public void UpdateSessionReferences(
        string encryptedSession,
        string webView2ProfileDirectory,
        DateTimeOffset updatedAt)
    {
        EnsureActive();
        EnsureTimestamp(updatedAt);
        EncryptedSession = RequireText(encryptedSession, nameof(encryptedSession));
        WebView2ProfileDirectory = RequireText(webView2ProfileDirectory, nameof(webView2ProfileDirectory));
        UpdatedAt = updatedAt;
    }

    public void Tombstone(DateTimeOffset deletedAt)
    {
        EnsureActive();
        EnsureTimestamp(deletedAt);
        EncryptedSession = null;
        WebView2ProfileDirectory = null;
        Status = AccountStatus.Deleted;
        IsDeleted = true;
        DeletedAt = deletedAt;
        UpdatedAt = deletedAt;
    }

    private void EnsureActive()
    {
        if (IsDeleted)
        {
            throw new InvalidOperationException("A tombstoned account cannot be changed.");
        }
    }

    private void EnsureTimestamp(DateTimeOffset timestamp)
    {
        if (timestamp < UpdatedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(timestamp), "Timestamp cannot move backwards.");
        }
    }

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value;
}
