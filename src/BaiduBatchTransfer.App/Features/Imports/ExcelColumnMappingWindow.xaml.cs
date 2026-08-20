using System.Windows;

namespace BaiduBatchTransfer.App.Features.Imports;

public partial class ExcelColumnMappingWindow : Window
{
    private readonly ExcelColumnMappingViewModel viewModel;

    public ExcelColumnMappingWindow(ExcelColumnMappingViewModel viewModel)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.TryConfirm()) DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
