using System.Windows;

namespace BaiduBatchTransfer.App.Features.Transfers;

public sealed class WpfTransferTargetDirectoryPicker(
    ITransferTargetDirectorySource source,
    ITransferTargetDirectoryCreator creator) : ITransferTargetDirectoryPicker
{
    public Task<string?> PickAsync(
        Guid accountId,
        string initialPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var viewModel = new TransferTargetDirectoryPickerViewModel(
            accountId, initialPath, source, creator);
        var window = new TransferTargetDirectoryWindow(viewModel);
        var owner = System.Windows.Application.Current?.MainWindow;
        if (owner is not null && owner != window) window.Owner = owner;
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            if (window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished) return;
            _ = window.Dispatcher.BeginInvoke(window.Close);
        });
        var accepted = window.ShowDialog() == true;
        return Task.FromResult(accepted ? window.SelectedPath : null);
    }
}
