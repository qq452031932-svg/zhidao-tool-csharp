namespace BaiduBatchTransfer.Core.Domain;

public sealed record BaiduAccountIdentity
{
    public BaiduAccountIdentity(string userId, string displayName, Uri? avatarUri = null)
    {
        UserId = RequireText(userId, nameof(userId));
        DisplayName = RequireText(displayName, nameof(displayName));
        if (avatarUri is not null && !IsTrustedAvatarUri(avatarUri))
            throw new ArgumentException("Avatar URI must use a trusted Baidu HTTPS host.", nameof(avatarUri));
        AvatarUri = avatarUri;
    }

    public string UserId { get; }

    public string DisplayName { get; }

    public Uri? AvatarUri { get; }

    public override string ToString() =>
        "BaiduAccountIdentity { UserId = [REDACTED], DisplayName = [REDACTED], AvatarUri = [REDACTED] }";

    public static bool IsTrustedAvatarUri(Uri value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.IsAbsoluteUri || value.Scheme != Uri.UriSchemeHttps || !value.IsDefaultPort)
            return false;
        var host = value.IdnHost;
        return IsHostOrSubdomain(host, "baidu.com") || IsHostOrSubdomain(host, "bdimg.com");
    }

    private static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private static bool IsHostOrSubdomain(string host, string root) =>
        string.Equals(host, root, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith($".{root}", StringComparison.OrdinalIgnoreCase);
}
