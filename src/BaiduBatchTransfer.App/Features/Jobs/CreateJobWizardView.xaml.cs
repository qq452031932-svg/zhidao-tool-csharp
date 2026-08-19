using System.Windows.Controls;

namespace BaiduBatchTransfer.App.Features.Jobs;

public partial class CreateJobWizardView : UserControl
{
    public CreateJobWizardView(CreateJobWizardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }
}
