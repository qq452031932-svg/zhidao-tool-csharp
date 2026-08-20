using System.ComponentModel;
using System.Windows;
using BaiduBatchTransfer.App.Services;

namespace BaiduBatchTransfer.App.Features.Accounts;

public partial class AccountManagementWindow : Window
{
    private readonly AccountManagementViewModel viewModel;
    private readonly CancellationTokenSource lifetime = new();
    private bool loaded;

    public AccountManagementWindow(AccountManagementViewModel viewModel)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        viewModel.RefreshRequested += OnRefreshRequested;
    }

    public AccountManagementRefreshResult RefreshResult { get; private set; } =
        AccountManagementRefreshResult.None;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (loaded) return;
        loaded = true;
        await viewModel.LoadFirstPageAsync(lifetime.Token).ConfigureAwait(true);
    }

    private void OnRefreshRequested(object? sender, AccountManagementRefreshEventArgs e)
    {
        RefreshResult = new AccountManagementRefreshResult(true, e.Status);
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e) => lifetime.Cancel();

    private void OnClosed(object? sender, EventArgs e)
    {
        lifetime.Cancel();
        viewModel.RefreshRequested -= OnRefreshRequested;
        lifetime.Dispose();
    }
}
