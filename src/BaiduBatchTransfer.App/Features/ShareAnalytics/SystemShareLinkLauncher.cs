using System.Diagnostics;

namespace BaiduBatchTransfer.App.Features.ShareAnalytics;

public interface IExternalProcessStarter
{
    void Start(ProcessStartInfo startInfo);
}

public sealed class ExternalProcessStarter : IExternalProcessStarter
{
    public void Start(ProcessStartInfo startInfo) => Process.Start(startInfo);
}

public sealed class SystemShareLinkLauncher(IExternalProcessStarter processStarter) : IShareLinkLauncher
{
    private readonly IExternalProcessStarter processStarter =
        processStarter ?? throw new ArgumentNullException(nameof(processStarter));

    public bool TryOpen(Uri uri, out string safeMessage)
    {
        if (!IsAllowed(uri))
        {
            safeMessage = "链接地址不安全，未打开。";
            return false;
        }
        try
        {
            processStarter.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            safeMessage = "已在默认浏览器中打开链接。";
            return true;
        }
        catch
        {
            safeMessage = "无法打开链接，请稍后重试。";
            return false;
        }
    }

    internal static bool IsAllowed(Uri? uri) => uri is not null &&
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) &&
        string.Equals(uri.IdnHost, "pan.baidu.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith("/s/", StringComparison.Ordinal) && uri.AbsolutePath.Length > 3;
}
