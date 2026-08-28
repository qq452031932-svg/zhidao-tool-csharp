using System.ComponentModel;
using System.Windows;
using BaiduBatchTransfer.App.Services;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

public partial class ManagedBrowserLoginWindow : Window, IManagedBrowserLoginDialog
{
    private readonly ManagedBrowserLoginViewModel viewModel;
    private bool loaded;
    private bool closePending;
    public ManagedBrowserLoginWindow(ManagedBrowserLoginViewModel viewModel)
    {
        this.viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += (_, _) => { viewModel.EndLifetime(); viewModel.PropertyChanged -= OnStateChanged; };
        viewModel.PropertyChanged += OnStateChanged;
    }
    void IManagedBrowserLoginDialog.ShowDialog() => ShowDialog();
    public void RequestClose() => Dispatcher.BeginInvoke(() => { if (IsVisible) Close(); });
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (loaded) return;
        loaded = true;
        await viewModel.CheckLoginAsync();
    }
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        viewModel.EndLifetime();
        if (!viewModel.IsBusy) return;
        closePending = true;
        e.Cancel = true;
    }
    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(viewModel.IsBusy) && closePending && !viewModel.IsBusy)
        {
            closePending = false;
            RequestClose();
        }
    }
}
