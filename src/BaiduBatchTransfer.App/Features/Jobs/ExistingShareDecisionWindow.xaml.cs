using System.Windows;

namespace BaiduBatchTransfer.App.Features.Jobs;

public partial class ExistingShareDecisionWindow : Window
{
    private readonly ExistingShareDecisionViewModel viewModel;

    public ExistingShareDecisionWindow(ExistingShareDecisionViewModel viewModel)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        InitializeComponent();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!viewModel.TryConfirm()) return;
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
