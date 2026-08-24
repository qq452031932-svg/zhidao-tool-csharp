using System.Windows.Controls;

namespace BaiduBatchTransfer.App.Features.Shares;

public partial class ShareWorkspaceView : UserControl
{
    public ShareWorkspaceView(ShareWorkspaceViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }
}
