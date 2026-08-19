namespace BaiduBatchTransfer.Core.Domain;

public sealed record BaiduAccountIdentity
{
    public BaiduAccountIdentity(string userId, string displayName)
    {
        UserId = RequireText(userId, nameof(userId));
        DisplayName = RequireText(displayName, nameof(displayName));
    }

    public string UserId { get; }

    public string DisplayName { get; }

    public override string ToString() =>
        "BaiduAccountIdentity { UserId = [REDACTED], DisplayName = [REDACTED] }";

    private static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}
