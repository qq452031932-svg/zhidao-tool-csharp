using System.Windows.Controls;

namespace BaiduBatchTransfer.App.Features.Imports;

public partial class ImportView : UserControl
{
    public ImportView(ImportViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }
}
