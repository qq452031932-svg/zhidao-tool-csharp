namespace BaiduBatchTransfer.App.Services;

public static class BaiduNavigationPolicy
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "pan.baidu.com",
        "passport.baidu.com",
    };

    public static BaiduNavigationDecision Evaluate(Uri? destination)
    {
        if (destination is not { IsAbsoluteUri: true } ||
            destination.Scheme != Uri.UriSchemeHttps || !destination.IsDefaultPort ||
            !string.IsNullOrEmpty(destination.UserInfo) || !AllowedHosts.Contains(destination.IdnHost))
        {
            return new(false, "已阻止跳转到未授权的网站。");
        }
        return new(true, null);
    }
}

public sealed record BaiduNavigationDecision(bool IsAllowed, string? SafeMessage);
