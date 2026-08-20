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
    public const string InitializationFailed = "登录页面无法打开，请安装或更新微软官方网页运行组件后重试。";
    public const string CaptureFailed = "登录状态读取失败，窗口会继续等待，请重新扫码确认。";
    public const string LoginButtonMissing = "未找到百度网盘首页的登录按钮。百度页面可能已经更新，请关闭窗口后重试。";
    public const string CleanupPending = "账号没有保存，本机登录目录仍待清理；下次启动会继续处理。";
}

public sealed class BaiduLoginViewModel : ObservableObject
{
    private string status = BaiduLoginUiText.Preparing;

    public string WindowTitle => BaiduLoginUiText.WindowTitle;
    public string Heading => BaiduLoginUiText.Heading;
    public string Instructions => BaiduLoginUiText.Instructions;
    public string Status { get => status; private set => SetProperty(ref status, value); }

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
