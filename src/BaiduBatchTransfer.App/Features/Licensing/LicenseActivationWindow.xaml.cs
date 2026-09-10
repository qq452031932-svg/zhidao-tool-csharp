using System.Windows;

namespace BaiduBatchTransfer.App.Features.Licensing;

public partial class LicenseActivationWindow : Window
{
    private readonly LicenseActivationViewModel viewModel;

    public LicenseActivationWindow(LicenseActivationViewModel viewModel)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
        Closed += OnClosed;
    }

    private void OnCloseRequested(object? sender, LicenseActivationCloseRequestedEventArgs request) =>
        DialogResult = request.IsActivated;

    private void OnClosed(object? sender, EventArgs e)
    {
        viewModel.CloseRequested -= OnCloseRequested;
        Closed -= OnClosed;
    }
}
