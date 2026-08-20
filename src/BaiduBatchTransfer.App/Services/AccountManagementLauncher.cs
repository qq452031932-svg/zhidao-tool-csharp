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

public sealed class AccountManagementWindowPresenter(
    Func<AccountManagementWindow> windowFactory) : IAccountManagementWindowPresenter
{
    private readonly Func<AccountManagementWindow> windowFactory =
        windowFactory ?? throw new ArgumentNullException(nameof(windowFactory));

    public Task<AccountManagementRefreshResult> ShowAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var window = windowFactory();
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
