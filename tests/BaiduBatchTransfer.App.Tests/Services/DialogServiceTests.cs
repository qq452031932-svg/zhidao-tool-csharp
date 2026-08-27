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
        var browserDialog = new RecordingBrowserDirectoryDialog(
            @"C:\Browser\Application", () => dispatcher.IsInsideInvocation);
        var service = new DialogService(dispatcher, fileDialog, browserDialog);

        var selected = await Task.Run(() =>
            service.PickBrowserDirectoryAsync(CancellationToken.None));

        Assert.Equal(@"C:\Browser\Application", selected);
        Assert.Equal(1, dispatcher.InvocationCount);
        Assert.Equal(1, browserDialog.InvocationCount);
        Assert.True(browserDialog.WasShownInsideDispatcher);
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

    [Fact]
    public async Task ExactFileProbeRunsOffTheDispatcherThread()
    {
        var dispatcher = new RecordingDispatcher();
        using var probe = new BlockingExactFileExistenceProbe(result: true);
        var fileSystem = new BrowserDirectoryFileSystem(probe);

        var probeTask = await dispatcher.InvokeAsync(
            () => fileSystem.FileExistsAsync(@"C:\Browser\Application\chrome.exe", CancellationToken.None));

        Assert.True(probe.WaitUntilEntered());
        Assert.False(probeTask.IsCompleted);
        Assert.NotEqual(dispatcher.LastInvocationThreadId, probe.InvocationThreadId);
        probe.Release();
        Assert.True(await probeTask);
        Assert.Equal(@"C:\Browser\Application\chrome.exe", probe.ExactPath);
    }

    [Fact]
    public async Task CancellationDuringBlockingProbePropagatesAtTheIoCompletionBoundary()
    {
        using var cancellation = new CancellationTokenSource();
        using var probe = new BlockingExactFileExistenceProbe(result: true);
        var fileSystem = new BrowserDirectoryFileSystem(probe);

        var probeTask = fileSystem.FileExistsAsync(
            @"C:\Browser\Application\chrome.exe", cancellation.Token);
        Assert.True(probe.WaitUntilEntered());
        cancellation.Cancel();
        Assert.False(probeTask.IsCompleted);
        probe.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probeTask);
        Assert.Equal(@"C:\Browser\Application\chrome.exe", probe.ExactPath);
    }

    private sealed class RecordingDispatcher : IUiDispatcher
    {
        private readonly AsyncLocal<int> invocationDepth = new();

        public int InvocationCount { get; private set; }
        public int LastInvocationThreadId { get; private set; }
        public bool IsInsideInvocation => invocationDepth.Value > 0;

        public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            LastInvocationThreadId = Environment.CurrentManagedThreadId;
            invocationDepth.Value++;
            try
            {
                return Task.FromResult(action());
            }
            finally
            {
                invocationDepth.Value--;
            }
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

    private sealed class RecordingBrowserDirectoryDialog(
        string selectedPath,
        Func<bool>? isInsideDispatcher = null) : IBrowserDirectoryDialog
    {
        public int InvocationCount { get; private set; }
        public bool WasShownInsideDispatcher { get; private set; }

        public string? Show()
        {
            InvocationCount++;
            WasShownInsideDispatcher = isInsideDispatcher?.Invoke() ?? false;
            return selectedPath;
        }
    }

    private sealed class BlockingExactFileExistenceProbe(bool result)
        : IExactFileExistenceProbe, IDisposable
    {
        private readonly ManualResetEventSlim entered = new(false);
        private readonly ManualResetEventSlim release = new(false);

        public string? ExactPath { get; private set; }
        public int InvocationThreadId { get; private set; }

        public bool Exists(string exactPath)
        {
            ExactPath = exactPath;
            InvocationThreadId = Environment.CurrentManagedThreadId;
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return result;
        }

        public bool WaitUntilEntered() => entered.Wait(TimeSpan.FromSeconds(5));
        public void Release() => release.Set();

        public void Dispose()
        {
            release.Set();
            entered.Dispose();
            release.Dispose();
        }
    }
}
