using System.Windows;

namespace BaiduBatchTransfer.App.Features.Licensing;

public interface ILicenseActivationPresenter
{
    Task<bool> PresentAsync(CancellationToken cancellationToken = default);
}

public sealed class WpfLicenseActivationPresenter(
    string productName,
    Func<string, CancellationToken, Task<LicenseActivationResult>> activateAsync)
    : ILicenseActivationPresenter
{
    private readonly string productName = string.IsNullOrWhiteSpace(productName)
        ? throw new ArgumentException("A product name is required.", nameof(productName))
        : productName.Trim();
    private readonly Func<string, CancellationToken, Task<LicenseActivationResult>> activateAsync =
        activateAsync ?? throw new ArgumentNullException(nameof(activateAsync));

    public Task<bool> PresentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var viewModel = new LicenseActivationViewModel(productName, activateAsync);
        var window = new LicenseActivationWindow(viewModel);
        var owner = System.Windows.Application.Current?.MainWindow;
        if (owner is not null && owner != window) window.Owner = owner;

        using var registration = cancellationToken.Register(() =>
        {
            if (window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished) return;
            _ = window.Dispatcher.BeginInvoke(window.CancelActivationAndClose);
        });
        var activated = window.ShowDialog() == true;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(activated);
    }
}
