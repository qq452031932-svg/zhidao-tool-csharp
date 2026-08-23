using System.Windows.Controls;

namespace BaiduBatchTransfer.App.Features.Jobs;

public partial class JobResultsView : UserControl
{
    public JobResultsView(JobResultsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }
}
