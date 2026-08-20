using System.Windows;
using BaiduBatchTransfer.App.Features.Accounts;
using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.BaiduAdapter.Authentication;

namespace BaiduBatchTransfer.App.Services;

public sealed class BaiduLoginWindowPresenter(
    LoginProfileBootstrapper profileBootstrapper,
    AddAccountService addAccountService,
    WebView2ProfileCloser profileCloser,
    WebViewCookieSessionBridge cookieBridge) : IBaiduLoginWindowPresenter
{
    public async Task ShowAsync(BaiduLoginWindowRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var application = System.Windows.Application.Current ??
            throw new InvalidOperationException("登录窗口只能从桌面应用中打开。");

        await application.Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = new BaiduLoginWindow(
                request.AccountId,
                request.OwnershipToken,
                profileBootstrapper,
                addAccountService,
                profileCloser,
                cookieBridge)
            {
                Owner = application.MainWindow,
            };
            window.ShowDialog();
        });
    }
}
