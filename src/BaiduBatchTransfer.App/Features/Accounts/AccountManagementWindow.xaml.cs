using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using BaiduBatchTransfer.App.Services;

namespace BaiduBatchTransfer.App.Features.Accounts;

public partial class AccountManagementWindow : Window
{
    private readonly AccountManagementViewModel viewModel;
    private readonly CancellationTokenSource lifetime = new();
    private bool loaded;
    private bool closePending;
    private bool allowClose;

    public AccountManagementWindow(AccountManagementViewModel viewModel)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
        viewModel.AttachLifetime(lifetime.Token);
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        viewModel.RefreshRequested += OnRefreshRequested;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
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
        lifetime.Cancel();
        closePending = true;
        CloseWhenIdle();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        lifetime.Cancel();
        if (!viewModel.IsBusy)
        {
            allowClose = true;
            return;
        }
        closePending = true;
        e.Cancel = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        lifetime.Cancel();
        viewModel.RefreshRequested -= OnRefreshRequested;
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        viewModel.DetachLifetime();
        lifetime.Dispose();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccountManagementViewModel.IsBusy))
        {
            CloseWhenIdle();
            FocusConfirmationWhenReady();
        }
        else if (e.PropertyName is nameof(AccountManagementViewModel.IsConfirmationVisible) or
                 nameof(AccountManagementViewModel.NeedsPreviewRefresh))
        {
            FocusConfirmationWhenReady();
        }
    }

    private void CloseWhenIdle()
    {
        if (!closePending || viewModel.IsBusy) return;
        closePending = false;
        allowClose = true;
        Dispatcher.BeginInvoke(Close, DispatcherPriority.Normal);
    }

    private void FocusConfirmationWhenReady()
    {
        if (!viewModel.IsConfirmationVisible || viewModel.IsBusy || closePending) return;
        Dispatcher.BeginInvoke(
            () =>
            {
                if (viewModel.NeedsPreviewRefresh)
                    ReloadPreviewButton.Focus();
                else
                    ConfirmRemovalButton.Focus();
            },
            DispatcherPriority.Input);
    }
}
