using System.Windows.Controls;
using System.Windows.Input;

namespace BaiduBatchTransfer.App.Features.ShareAnalytics;

public partial class ShareAnalyticsView : UserControl
{
    public ShareAnalyticsView(ShareAnalyticsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        TitleSearchInput.KeyDown += OnTitleSearchKeyDown;
    }

    private static void OnTitleSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox input) return;
        input.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }
}
