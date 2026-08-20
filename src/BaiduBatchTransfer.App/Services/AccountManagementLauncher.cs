using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.App.Features.Accounts;
using System.Windows;

namespace BaiduBatchTransfer.App.Services;

public sealed class AccountManagementRefreshEventArgs(AccountExitStatus status) : EventArgs
{
    public AccountExitStatus Status { get; } = status;
}

public sealed record AccountManagementRefreshResult(bool RefreshRequired, AccountExitStatus? Status)
{
    public static AccountManagementRefreshResult None { get; } = new(false, null);
}

public interface IAccountManagementWindowPresenter
{
    Task<AccountManagementRefreshResult> ShowAsync(CancellationToken cancellationToken);
}

public interface IAccountManagementLauncher
{
    event EventHandler<AccountManagementRefreshEventArgs>? RefreshRequested;
    Task<AccountManagementRefreshResult> OpenAsync(CancellationToken cancellationToken = default);
}

public interface IAccountManagementOwnedPrompt
{
    bool Show(Window owner, string title, string message);
}

public sealed class WpfAccountManagementOwnedPrompt : IAccountManagementOwnedPrompt
{
    public bool Show(Window owner, string title, string message) =>
        MessageBox.Show(
            owner,
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;
}

public sealed class OwnedAccountManagementConfirmation(IAccountManagementOwnedPrompt prompt)
    : IAccountManagementConfirmation
{
    private readonly IAccountManagementOwnedPrompt prompt =
        prompt ?? throw new ArgumentNullException(nameof(prompt));
    private Window? owner;

    public void AttachOwner(Window value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (owner is not null && !ReferenceEquals(owner, value))
            throw new InvalidOperationException("确认服务已绑定账号管理窗口。");
        owner = value;
    }

    public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attachedOwner = owner ?? throw new InvalidOperationException("账号管理窗口尚未绑定。");
        var result = prompt.Show(attachedOwner, title, message);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(result);
    }
}

public sealed class AccountManagementWindowPresenter : IAccountManagementWindowPresenter
{
    private readonly Func<IAccountManagementConfirmation, AccountManagementWindow> windowFactory;
    private readonly IAccountManagementOwnedPrompt prompt;

    public AccountManagementWindowPresenter(
        Func<IAccountManagementConfirmation, AccountManagementWindow> windowFactory,
        IAccountManagementOwnedPrompt? prompt = null)
    {
        this.windowFactory = windowFactory ?? throw new ArgumentNullException(nameof(windowFactory));
        this.prompt = prompt ?? new WpfAccountManagementOwnedPrompt();
    }

    public Task<AccountManagementRefreshResult> ShowAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var confirmation = new OwnedAccountManagementConfirmation(prompt);
        var window = windowFactory(confirmation);
        confirmation.AttachOwner(window);
        if (System.Windows.Application.Current?.MainWindow is { } owner && !ReferenceEquals(owner, window))
            window.Owner = owner;
        using var registration = cancellationToken.Register(() =>
            window.Dispatcher.BeginInvoke(() => window.Close()));
        window.ShowDialog();
        return Task.FromResult(window.RefreshResult);
    }
}

public sealed class AccountManagementLauncher(IAccountManagementWindowPresenter presenter) : IAccountManagementLauncher
{
    private readonly IAccountManagementWindowPresenter presenter =
        presenter ?? throw new ArgumentNullException(nameof(presenter));

    public event EventHandler<AccountManagementRefreshEventArgs>? RefreshRequested;

    public async Task<AccountManagementRefreshResult> OpenAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await presenter.ShowAsync(cancellationToken).ConfigureAwait(true);
        if (result.RefreshRequired && result.Status is AccountExitStatus.Removed or AccountExitStatus.CleanupPending)
            RefreshRequested?.Invoke(this, new AccountManagementRefreshEventArgs(result.Status.Value));
        return result;
    }
}
