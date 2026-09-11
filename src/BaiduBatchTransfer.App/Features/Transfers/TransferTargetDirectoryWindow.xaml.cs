using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BaiduBatchTransfer.App.Features.Transfers;

public partial class TransferTargetDirectoryWindow : Window
{
    private readonly TransferTargetDirectoryPickerViewModel viewModel;

    public TransferTargetDirectoryWindow(TransferTargetDirectoryPickerViewModel viewModel)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        viewModel.Completed += OnCompleted;
    }

    public string? SelectedPath { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await viewModel.InitializeAsync();
    }

    private void OnCompleted(object? sender, string? path)
    {
        SelectedPath = path;
        DialogResult = path is not null;
    }

    private void DirectoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox directoryList ||
            e.OriginalSource is not DependencyObject hitElement ||
            ItemsControl.ContainerFromElement(directoryList, hitElement) is not ListBoxItem)
            return;
        if (viewModel.EnterSelectedDirectoryCommand.CanExecute(null))
            viewModel.EnterSelectedDirectoryCommand.Execute(null);
    }

    private async void CreateFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!viewModel.CanCreateFolder) return;
        var dialog = new TransferFolderNameWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await viewModel.CreateFolderAsync(dialog.FolderName);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Loaded -= OnLoaded;
        Closed -= OnClosed;
        viewModel.Completed -= OnCompleted;
        viewModel.Close();
    }
}
