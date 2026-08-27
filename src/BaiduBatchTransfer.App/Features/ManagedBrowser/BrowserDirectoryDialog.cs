using Microsoft.Win32;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

public interface IBrowserDirectoryDialog
{
    string? Show();
}

public interface IBrowserDirectoryPicker
{
    Task<string?> PickBrowserDirectoryAsync(CancellationToken cancellationToken = default);
}

public sealed class BrowserDirectoryDialog : IBrowserDirectoryDialog
{
    public string? Show()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 Chrome 或 Edge 安装目录",
            Multiselect = false,
        };
        var owner = System.Windows.Application.Current?.MainWindow;
        var accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return accepted == true ? dialog.FolderName : null;
    }
}
