using System.Windows.Controls;

namespace BaiduBatchTransfer.App.Features.Home;

public partial class HomeWorkspaceView : UserControl
{
    public HomeWorkspaceView()
    {
        InitializeComponent();
    }

    public HomeWorkspaceView(HomeWorkspaceViewModel viewModel) : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }
}
