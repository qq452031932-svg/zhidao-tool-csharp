using BaiduBatchTransfer.Application.Accounts;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BaiduBatchTransfer.App.Features.Accounts;

public static class BaiduLoginUiText
{
    public const string WindowTitle = "百度网盘账号登录";
    public const string Heading = "扫码登录百度网盘";
    public const string Instructions = "请使用百度或百度网盘手机客户端扫描二维码并在手机上确认。登录成功后会自动加密保存并关闭；不会保存密码，也不会绕过安全验证。";
    public const string Preparing = "正在准备独立登录环境……";
    public const string Waiting = "请扫码并在手机上确认登录。";
    public const string Settling = "登录成功，正在等待百度完成页面初始化……";
    public const string Capturing = "已确认登录，正在安全保存账号……";
    public const string Succeeded = "账号已保存，正在关闭窗口……";
    public const string Duplicate = "这个百度账号已经添加，无需重复登录。";
    public const string Rejected = "登录状态未通过验证，请重新扫码后再试。";
    public const string ProfileFailed = "独立账号目录无法安全创建，请关闭窗口后重试。";
    public const string RuntimeStartFailed =
        "登录页面组件启动失败，具体原因已写入运行日志。可以重新安装微软网页运行组件，或暂时关闭安全软件后关闭窗口重试。";
    public const string NavigationFailed =
        "百度网盘登录页打不开（{0}）。请检查网络、代理或安全软件，能用浏览器打开百度网盘网页后，关闭窗口重试。";
    public const string CaptureFailed = "登录状态读取失败，窗口会继续等待，请重新扫码确认。";
    public const string LoginButtonMissing = "等待百度网盘首页加载超时，未能自动打开登录框。请在上方页面中手动点击「去登录」，或关闭窗口后重试。";
    public const string CleanupPending = "账号没有保存，本机登录目录仍待清理；下次启动会继续处理。";
}

public sealed class BaiduLoginViewModel : ObservableObject
{
    private string status = BaiduLoginUiText.Preparing;
    private bool showRuntimeDownload;

    public string WindowTitle => BaiduLoginUiText.WindowTitle;
    public string Heading => BaiduLoginUiText.Heading;
    public string Instructions => BaiduLoginUiText.Instructions;
    public string Status { get => status; private set => SetProperty(ref status, value); }

    /// <summary>没装 WebView2 时显示「下载 WebView2」按钮。</summary>
    public bool ShowRuntimeDownload { get => showRuntimeDownload; private set => SetProperty(ref showRuntimeDownload, value); }

    public void ReportRuntimeMissing()
    {
        Status = Services.WebView2RuntimeText.Missing;
        ShowRuntimeDownload = true;
    }

    public void ReportWaiting() => Status = BaiduLoginUiText.Waiting;
    public void ReportSettling() => Status = BaiduLoginUiText.Settling;
    public void ReportCapturing() => Status = BaiduLoginUiText.Capturing;
    public void ReportFailure(string safeChineseMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeChineseMessage);
        Status = safeChineseMessage.Trim();
    }

    public bool ReportRegistrationResult(AddAccountResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Status = result.Status switch
        {
            AddAccountStatus.Added => BaiduLoginUiText.Succeeded,
            AddAccountStatus.DuplicateIdentity => BaiduLoginUiText.Duplicate,
            AddAccountStatus.InvalidProfilePath or AddAccountStatus.AccountIdCollision => BaiduLoginUiText.ProfileFailed,
            _ => BaiduLoginUiText.Rejected,
        };
        return result.Status == AddAccountStatus.Added;
    }
}
