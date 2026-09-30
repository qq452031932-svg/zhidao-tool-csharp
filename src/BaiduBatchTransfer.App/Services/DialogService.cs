using BaiduBatchTransfer.App.Features.Acceptance;
using BaiduBatchTransfer.App.Features.ManagedBrowser;
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

public interface ITextClipboard
{
    Task SetTextAsync(string text, CancellationToken cancellationToken = default);
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

public sealed class DialogService(
    IUiDispatcher dispatcher,
    IExcelFileDialog excelFileDialog,
    IBrowserDirectoryDialog? browserDirectoryDialog = null,
    IMessageBoxPresenter? messageBox = null) : IDialogService, IBrowserDirectoryPicker
{
    private readonly IMessageBoxPresenter messageBox = messageBox ?? new WpfMessageBoxPresenter();
    public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        return dispatcher.InvokeAsync(
            () => messageBox.Show(title, message, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes,
            cancellationToken);
    }

    public Task ShowMessageAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        return dispatcher.InvokeAsync(
            () => messageBox.Show(title, message, MessageBoxButton.OK, MessageBoxImage.Information),
            cancellationToken);
    }

    public Task<string?> PickExcelFileAsync(CancellationToken cancellationToken = default) =>
        dispatcher.InvokeAsync(excelFileDialog.Show, cancellationToken);

    public Task<string?> PickBrowserDirectoryAsync(CancellationToken cancellationToken = default) =>
        dispatcher.InvokeAsync(
            (browserDirectoryDialog ?? new BrowserDirectoryDialog()).Show,
            cancellationToken);
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

public interface IAdapterAvailability
{
    bool CanStartTransfers { get; }
    string StatusMessage { get; }
    bool RequiresBulkPrecheckBeforeStart => true;
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

internal sealed class DirectBackgroundAdapterAvailability : IAdapterAvailability
{
    public bool CanStartTransfers => true;
    public bool RequiresBulkPrecheckBeforeStart => false;
    public string StatusMessage => "后台转存和自动分享已就绪。";
}
