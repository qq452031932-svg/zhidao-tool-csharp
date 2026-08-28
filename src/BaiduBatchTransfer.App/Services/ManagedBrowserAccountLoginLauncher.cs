using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Services;

public interface IManagedBrowserLoginWindowPresenter
{
    Task<ManagedBrowserLoginResult> ShowAsync(ManagedBrowserLoginRequest request, CancellationToken cancellationToken);
}

public interface IAccountReloginLauncher
{
    Task<ManagedBrowserLoginResult> ReloginAsync(Guid accountId, CancellationToken cancellationToken = default);
}

public sealed class ManagedBrowserAccountLoginLauncher(IManagedBrowserLoginWindowPresenter presenter)
    : IAccountLoginLauncher, IAccountReloginLauncher
{
    public bool IsAvailable => true;
    public string StatusMessage => "使用专用 Chrome 或 Edge 窗口登录；真实扫码识别尚未验收。";
    public async Task OpenAsync(CancellationToken cancellationToken = default) =>
        await presenter.ShowAsync(ManagedBrowserLoginRequest.AddNew(Guid.NewGuid()), cancellationToken).ConfigureAwait(true);
    public Task<ManagedBrowserLoginResult> ReloginAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        presenter.ShowAsync(ManagedBrowserLoginRequest.ReloginExisting(accountId), cancellationToken);
}

public interface IManagedBrowserLoginDialog
{
    void ShowDialog();
    // Implementations must enqueue closure on their UI dispatcher, never close from cancellation threads.
    void RequestClose();
}

public sealed class ManagedBrowserLoginWindowPresenter(
    IManagedBrowserLoginOperations operations,
    IUiDispatcher dispatcher,
    Func<ManagedBrowserLoginViewModel, IManagedBrowserLoginDialog> windowFactory,
    Func<Guid, CancellationToken, Task<BrowserFamily?>> preferredFamily) : IManagedBrowserLoginWindowPresenter
{
    public async Task<ManagedBrowserLoginResult> ShowAsync(ManagedBrowserLoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var family = request.Kind == ManagedBrowserLoginRequestKind.ReloginExisting
            ? await preferredFamily(request.AccountId, cancellationToken).ConfigureAwait(false) : null;
        return await dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vm = new ManagedBrowserLoginViewModel(request, operations, family);
            var dialog = windowFactory(vm);
            using var registration = cancellationToken.Register(dialog.RequestClose);
            try { dialog.ShowDialog(); }
            finally { vm.EndLifetime(); }
            return vm.Result;
        }, cancellationToken).ConfigureAwait(false);
    }
}
