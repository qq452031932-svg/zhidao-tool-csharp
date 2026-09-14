using System.Windows;
using System.ComponentModel;

namespace BaiduBatchTransfer.App.Features.Licensing;

public partial class LicenseActivationWindow : Window
{
    private readonly LicenseActivationViewModel viewModel;
    private bool allowControlledClose;

    public LicenseActivationWindow(LicenseActivationViewModel viewModel)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    internal void CancelActivationAndClose()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(CancelActivationAndClose);
            return;
        }

        viewModel.ActivateCommand.Cancel();
        allowControlledClose = true;
        Close();
    }

    private void OnCloseRequested(object? sender, LicenseActivationCloseRequestedEventArgs request)
    {
        if (!request.IsActivated && viewModel.IsActivating) return;
        allowControlledClose = true;
        DialogResult = request.IsActivated;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (viewModel.IsActivating && !allowControlledClose)
            e.Cancel = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        viewModel.CloseRequested -= OnCloseRequested;
        Closing -= OnClosing;
        Closed -= OnClosed;
    }
}
