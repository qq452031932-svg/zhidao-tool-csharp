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

public sealed class ManagedBrowserAccountLoginLauncher : IAccountLoginLauncher, IAccountReloginLauncher
{
    internal const string ResumeProfileArgumentPrefix = "--managed-browser-account-id=";
    private readonly IManagedBrowserLoginWindowPresenter presenter;
    private readonly object addAccountSync = new();
    private Guid? firstAddAccountId;

    public ManagedBrowserAccountLoginLauncher(
        IManagedBrowserLoginWindowPresenter presenter,
        Guid? firstAddAccountId = null)
    {
        this.presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        if (firstAddAccountId == Guid.Empty)
            throw new ArgumentException("A resumed account id cannot be empty.", nameof(firstAddAccountId));
        this.firstAddAccountId = firstAddAccountId;
    }

    public bool IsAvailable => true;
    public string StatusMessage => "使用专用 Chrome 或 Edge 窗口登录；真实扫码识别尚未验收。";
    public async Task OpenAsync(CancellationToken cancellationToken = default) =>
        await presenter.ShowAsync(ManagedBrowserLoginRequest.AddNew(ReserveAddAccountId()), cancellationToken).ConfigureAwait(true);
    public Task<ManagedBrowserLoginResult> ReloginAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        presenter.ShowAsync(ManagedBrowserLoginRequest.ReloginExisting(accountId), cancellationToken);

    internal static Guid? ResumeProfileFromStartup(
        IReadOnlyList<string> arguments,
        bool acceptanceEnabled)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!acceptanceEnabled) return null;
        var values = arguments
            .Where(argument => argument.StartsWith(ResumeProfileArgumentPrefix, StringComparison.Ordinal))
            .Select(argument => argument[ResumeProfileArgumentPrefix.Length..])
            .ToArray();
        return values.Length == 1 && Guid.TryParseExact(values[0], "N", out var accountId) &&
            accountId != Guid.Empty
            ? accountId
            : null;
    }

    private Guid ReserveAddAccountId()
    {
        lock (addAccountSync)
        {
            var accountId = firstAddAccountId ?? Guid.NewGuid();
            firstAddAccountId = null;
            return accountId;
        }
    }
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
