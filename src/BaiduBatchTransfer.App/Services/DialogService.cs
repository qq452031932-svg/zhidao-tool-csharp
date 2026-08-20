using Microsoft.Win32;
using System.Windows;

namespace BaiduBatchTransfer.App.Services;

public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default);
    Task ShowMessageAsync(string title, string message, CancellationToken cancellationToken = default);
    string? PickExcelFile();
}

public sealed class DialogService : IDialogService
{
    public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        return Task.FromResult(result == MessageBoxResult.Yes);
    }

    public Task ShowMessageAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        return Task.CompletedTask;
    }

    public string? PickExcelFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Excel 导入文件",
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            CheckFileExists = true,
            Multiselect = false,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}

public interface IAccountLoginLauncher
{
    bool IsAvailable { get; }
    string StatusMessage { get; }
    Task OpenAsync(CancellationToken cancellationToken = default);
}

public interface IAccountSummaryProvider
{
    Task<int> GetActiveAccountCountAsync(CancellationToken cancellationToken = default);
}

public sealed class UnavailableAccountLoginLauncher : IAccountLoginLauncher
{
    public bool IsAvailable => false;
    public string StatusMessage => "百度登录适配器尚不可用，当前版本不能开始转存。";
    public Task OpenAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(StatusMessage);
}

public interface IAdapterAvailability
{
    bool CanStartTransfers { get; }
    string StatusMessage { get; }
}

public sealed class UnavailableAdapterAvailability : IAdapterAvailability
{
    public bool CanStartTransfers => false;
    public string StatusMessage => "百度转存适配器尚不可用，当前版本不能开始任务。";
}
