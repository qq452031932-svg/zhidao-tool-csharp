using System.Windows.Controls;

namespace BaiduBatchTransfer.App.Features.Common;

public partial class DeferredWorkspaceView : UserControl
{
    public DeferredWorkspaceView(DeferredWorkspaceViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }
}
