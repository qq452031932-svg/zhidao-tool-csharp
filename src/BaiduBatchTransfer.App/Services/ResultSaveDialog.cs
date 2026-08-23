using Microsoft.Win32;
using System.Windows;

namespace BaiduBatchTransfer.App.Services;

public interface IResultSaveDialog
{
    Task<string?> PickDestinationDirectoryAsync(CancellationToken cancellationToken = default);
}

public interface IFolderDialog
{
    string? Show();
}

public sealed class WpfFolderDialog : IFolderDialog
{
    public string? Show()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择结果表格保存目录",
            Multiselect = false,
        };
        var owner = System.Windows.Application.Current?.MainWindow;
        var accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return accepted == true ? dialog.FolderName : null;
    }
}

public sealed class ResultSaveDialog(IUiDispatcher dispatcher, IFolderDialog folderDialog) : IResultSaveDialog
{
    public Task<string?> PickDestinationDirectoryAsync(CancellationToken cancellationToken = default) =>
        dispatcher.InvokeAsync(folderDialog.Show, cancellationToken);
}
