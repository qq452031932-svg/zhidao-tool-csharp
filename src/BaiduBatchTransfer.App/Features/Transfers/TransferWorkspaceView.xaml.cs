using System.Windows.Controls;
using BaiduBatchTransfer.App.Features.Jobs;

namespace BaiduBatchTransfer.App.Features.Transfers;

public partial class TransferWorkspaceView : UserControl
{
    public TransferWorkspaceView(TransferWorkspaceViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        ResultsHost.Content = new JobResultsView(viewModel.RunPanel.Results);
    }
}
