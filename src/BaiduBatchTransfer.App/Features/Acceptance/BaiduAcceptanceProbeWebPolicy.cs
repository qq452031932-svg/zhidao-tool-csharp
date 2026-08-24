namespace BaiduBatchTransfer.App.Features.Acceptance;

public static class BaiduAcceptanceProbeWebPolicy
{
    public static bool IsDownloadAllowed() => false;

    public static bool IsAllowedNavigation(Uri? destination) =>
        destination is not null && IsOfficialHttps(destination);

    public static bool IsAllowedRequest(Uri? destination, string? method) =>
        destination is not null &&
        (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)) &&
        IsOfficialHttps(destination);

    private static bool IsOfficialHttps(Uri destination) =>
        destination.IsAbsoluteUri &&
        destination.Scheme == Uri.UriSchemeHttps &&
        destination.IsDefaultPort &&
        string.IsNullOrEmpty(destination.UserInfo) &&
        (string.Equals(destination.IdnHost, "baidu.com", StringComparison.OrdinalIgnoreCase) ||
         destination.IdnHost.EndsWith(".baidu.com", StringComparison.OrdinalIgnoreCase));
}
