using BaiduBatchTransfer.App.Features.Acceptance;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Threading;

namespace BaiduBatchTransfer.App.Services;

public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default);
    Task ShowMessageAsync(string title, string message, CancellationToken cancellationToken = default);
    Task<string?> PickExcelFileAsync(CancellationToken cancellationToken = default);
}

public interface IUiDispatcher
{
    Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default);
}

public sealed class WpfUiDispatcher : IUiDispatcher
{
    public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        var dispatcher = System.Windows.Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("The application UI dispatcher is unavailable.");
        if (dispatcher.CheckAccess()) return Task.FromResult(action());
        return dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task;
    }
}

public interface IExcelFileDialog
{
    string? Show();
}

public sealed class WpfExcelFileDialog : IExcelFileDialog
{
    public string? Show()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Excel 导入文件",
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            CheckFileExists = true,
            Multiselect = false,
        };
        var owner = System.Windows.Application.Current?.MainWindow;
        var accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return accepted == true ? dialog.FileName : null;
    }
}

public sealed class DialogService(IUiDispatcher dispatcher, IExcelFileDialog excelFileDialog) : IDialogService
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

    public Task<string?> PickExcelFileAsync(CancellationToken cancellationToken = default) =>
        dispatcher.InvokeAsync(excelFileDialog.Show, cancellationToken);
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
    public string StatusMessage => "百度登录功能尚不可用，当前版本不能开始转存。";
    public Task OpenAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(StatusMessage);
}

public interface IAdapterAvailability
{
    bool CanStartTransfers { get; }
    string StatusMessage { get; }
    bool RequiresBulkPrecheckBeforeStart => true;
}

public sealed class UnavailableAdapterAvailability : IAdapterAvailability
{
    public bool CanStartTransfers => false;
    public string StatusMessage => "百度转存功能尚不可用，当前版本不能开始任务。";
}

internal sealed class TemporaryAcceptanceAdapterAvailability(
    AcceptanceWriteLeaseOptions options) : IAdapterAvailability
{
    public bool CanStartTransfers => options.IsEnabled;
    public bool RequiresBulkPrecheckBeforeStart => false;
    public string StatusMessage => CanStartTransfers
        ? "百度转存现场验收模式已启用，每项实际操作仍需单独确认。"
        : "百度转存功能尚不可用，当前版本不能开始任务。";
}
