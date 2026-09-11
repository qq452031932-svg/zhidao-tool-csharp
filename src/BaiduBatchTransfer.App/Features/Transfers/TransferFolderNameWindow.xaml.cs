using System.Windows;

namespace BaiduBatchTransfer.App.Features.Transfers;

public partial class TransferFolderNameWindow : Window
{
    public TransferFolderNameWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            FolderNameInput.Focus();
            FolderNameInput.SelectAll();
        };
    }

    public string FolderName => FolderNameInput.Text;

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
