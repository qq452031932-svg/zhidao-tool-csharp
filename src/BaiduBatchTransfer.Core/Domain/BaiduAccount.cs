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

    public void Disable(DateTimeOffset updatedAt) =>
        ChangeAvailabilityFromActive(AccountStatus.Disabled, updatedAt);

    public void MarkInvalid(DateTimeOffset updatedAt) =>
        ChangeAvailabilityFromActive(AccountStatus.Invalid, updatedAt);

    public void Reactivate(DateTimeOffset updatedAt)
    {
        if (Status is not (AccountStatus.Disabled or AccountStatus.Invalid))
        {
            throw new InvalidOperationException($"Cannot reactivate an account in {Status} status.");
        }

        EnsureTimestamp(updatedAt);
        Status = AccountStatus.Active;
        UpdatedAt = updatedAt;
    }

    public void Tombstone(DateTimeOffset deletedAt)
    {
        EnsureNotDeleted();
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
        if (Status != AccountStatus.Active)
        {
            throw new InvalidOperationException($"An account in {Status} status cannot be changed as active.");
        }
    }

    private void EnsureNotDeleted()
    {
        if (IsDeleted)
        {
            throw new InvalidOperationException("A tombstoned account cannot be changed.");
        }
    }

    private void ChangeAvailabilityFromActive(AccountStatus nextStatus, DateTimeOffset updatedAt)
    {
        EnsureActive();
        EnsureTimestamp(updatedAt);
        Status = nextStatus;
        UpdatedAt = updatedAt;
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
