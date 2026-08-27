using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.App.Services;

namespace BaiduBatchTransfer.App.Tests.Services;

public sealed class DialogServiceTests
{
    [Fact]
    public async Task AsyncExcelSelectionAlwaysUsesUiDispatcher()
    {
        var dispatcher = new RecordingDispatcher();
        var fileDialog = new RecordingExcelFileDialog("selected.xlsx");
        var service = new DialogService(dispatcher, fileDialog);

        var selected = await Task.Run(() => service.PickExcelFileAsync(CancellationToken.None));

        Assert.Equal("selected.xlsx", selected);
        Assert.Equal(1, dispatcher.InvocationCount);
        Assert.Equal(1, fileDialog.InvocationCount);
    }

    [Fact]
    public async Task CancelledExcelSelectionDoesNotOpenDialog()
    {
        var dispatcher = new RecordingDispatcher();
        var fileDialog = new RecordingExcelFileDialog("private-path.xlsx");
        var service = new DialogService(dispatcher, fileDialog);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.PickExcelFileAsync(cancellation.Token));

        Assert.Equal(0, fileDialog.InvocationCount);
        Assert.DoesNotContain("private-path", service.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AsyncBrowserDirectorySelectionAlwaysUsesUiDispatcher()
    {
        var dispatcher = new RecordingDispatcher();
        var fileDialog = new RecordingExcelFileDialog("selected.xlsx");
        var browserDialog = new RecordingBrowserDirectoryDialog(@"C:\Browser\Application");
        var service = new DialogService(dispatcher, fileDialog, browserDialog);

        var selected = await Task.Run(() =>
            service.PickBrowserDirectoryAsync(CancellationToken.None));

        Assert.Equal(@"C:\Browser\Application", selected);
        Assert.Equal(1, dispatcher.InvocationCount);
        Assert.Equal(1, browserDialog.InvocationCount);
    }

    [Fact]
    public async Task CancelledBrowserDirectorySelectionDoesNotOpenDialog()
    {
        var dispatcher = new RecordingDispatcher();
        var fileDialog = new RecordingExcelFileDialog("selected.xlsx");
        var browserDialog = new RecordingBrowserDirectoryDialog(@"C:\Private\Browser");
        var service = new DialogService(dispatcher, fileDialog, browserDialog);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.PickBrowserDirectoryAsync(cancellation.Token));

        Assert.Equal(0, browserDialog.InvocationCount);
        Assert.DoesNotContain("Private", service.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecordingDispatcher : IUiDispatcher
    {
        public int InvocationCount { get; private set; }

        public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            return Task.FromResult(action());
        }
    }

    private sealed class RecordingExcelFileDialog(string selectedPath) : IExcelFileDialog
    {
        public int InvocationCount { get; private set; }

        public string? Show()
        {
            InvocationCount++;
            return selectedPath;
        }
    }

    private sealed class RecordingBrowserDirectoryDialog(string selectedPath) : IBrowserDirectoryDialog
    {
        public int InvocationCount { get; private set; }

        public string? Show()
        {
            InvocationCount++;
            return selectedPath;
        }
    }
}
