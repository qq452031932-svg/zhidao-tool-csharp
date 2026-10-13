using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.App.Services;
using System.Windows;

namespace BaiduBatchTransfer.App.Tests.Services;

public sealed class DialogServiceTests
{
    [Fact]
    public async Task CompletionAndConfirmationDialogsAlwaysUseUiDispatcher()
    {
        var dispatcher = new RecordingDispatcher();
        var presenter = new RecordingMessageBox(() => dispatcher.IsInsideInvocation);
        var service = new DialogService(dispatcher, new RecordingExcelFileDialog("unused.xlsx"), messageBox: presenter);

        await Task.Run(() => service.ShowMessageAsync("转存任务已完成", "完成 5 条，失败 0 条。"));
        var confirmed = await Task.Run(() => service.ConfirmAsync("确认", "继续？"));

        Assert.True(confirmed);
        Assert.Equal(2, dispatcher.InvocationCount);
        Assert.Equal(2, presenter.Calls.Count);
        Assert.All(presenter.Calls, call => Assert.True(call.InsideDispatcher));
        Assert.Equal(MessageBoxButton.OK, presenter.Calls[0].Buttons);
        Assert.Equal(MessageBoxButton.YesNo, presenter.Calls[1].Buttons);
    }

    [Fact]
    public async Task CancelledMessageDoesNotOpenDialog()
    {
        var dispatcher = new RecordingDispatcher();
        var presenter = new RecordingMessageBox(() => dispatcher.IsInsideInvocation);
        var service = new DialogService(dispatcher, new RecordingExcelFileDialog("unused.xlsx"), messageBox: presenter);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ShowMessageAsync("完成", "消息", cancellation.Token));
        Assert.Empty(presenter.Calls);
    }

    [Fact]
    public void MessageBoxUsesTheCurrentMainWindowAsOwner()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new Window();
                Window? currentOwner = null;
                Window? actualOwner = null;
                var presenter = new WpfMessageBoxPresenter(() => currentOwner, (owner, title, message, buttons, image) =>
                {
                    actualOwner = owner;
                    Assert.Equal("完成", title);
                    Assert.Equal("完成 5 条", message);
                    return MessageBoxResult.OK;
                });
                currentOwner = window;
                presenter.Show("完成", "完成 5 条", MessageBoxButton.OK, MessageBoxImage.Information);
                Assert.Same(window, actualOwner);
            }
            catch (Exception error) { failure = error; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class RecordingMessageBox(Func<bool> insideDispatcher) : IMessageBoxPresenter
    {
        public List<(bool InsideDispatcher, MessageBoxButton Buttons)> Calls { get; } = [];
        public MessageBoxResult Show(string title, string message, MessageBoxButton buttons, MessageBoxImage image)
        {
            Calls.Add((insideDispatcher(), buttons));
            return buttons == MessageBoxButton.YesNo ? MessageBoxResult.Yes : MessageBoxResult.OK;
        }
    }

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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            probeTask.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.False(probe.HasExited);
        probe.Release();
        Assert.True(probe.WaitUntilExited());
        Assert.Equal(@"C:\Browser\Application\chrome.exe", probe.ExactPath);
    }

    [Fact]
    public async Task CancelledBlockingProbesRespectSharedWorkerLimit()
    {
        using var probe = new CountingBlockingExactFileExistenceProbe();
        var scheduler = new BrowserFileProbeScheduler(maximumConcurrency: 2);
        var fileSystem = new BrowserDirectoryFileSystem(probe, scheduler);
        using var cancellation = new CancellationTokenSource();

        var tasks = Enumerable.Range(0, 20)
            .Select(index => fileSystem.FileExistsAsync(
                $@"C:\Browser\Application\probe-{index}.exe", cancellation.Token))
            .ToArray();
        Assert.True(probe.WaitUntilInvocationCount(2));

        cancellation.Cancel();
        await Task.WhenAll(tasks.Select(async task =>
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task)));

        Assert.Equal(2, probe.MaximumConcurrentInvocations);
        Assert.Equal(2, probe.InvocationCount);
        probe.Release();
        Assert.True(probe.WaitUntilAllExited());
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
        private readonly ManualResetEventSlim exited = new(false);

        public string? ExactPath { get; private set; }
        public int InvocationThreadId { get; private set; }
        public bool HasExited => exited.IsSet;

        public bool Exists(string exactPath)
        {
            ExactPath = exactPath;
            InvocationThreadId = Environment.CurrentManagedThreadId;
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            exited.Set();
            return result;
        }

        public bool WaitUntilEntered() => entered.Wait(TimeSpan.FromSeconds(5));
        public void Release() => release.Set();
        public bool WaitUntilExited() => exited.Wait(TimeSpan.FromSeconds(5));

        public void Dispose()
        {
            release.Set();
            entered.Dispose();
            release.Dispose();
            exited.Dispose();
        }
    }

    private sealed class CountingBlockingExactFileExistenceProbe : IExactFileExistenceProbe, IDisposable
    {
        private readonly ManualResetEventSlim release = new(false);
        private readonly ManualResetEventSlim allExited = new(false);
        private int activeInvocations;
        private int invocationCount;
        private int maximumConcurrentInvocations;

        public int InvocationCount => Volatile.Read(ref invocationCount);
        public int MaximumConcurrentInvocations => Volatile.Read(ref maximumConcurrentInvocations);

        public bool Exists(string exactPath)
        {
            var active = Interlocked.Increment(ref activeInvocations);
            Interlocked.Increment(ref invocationCount);
            UpdateMaximum(active);
            release.Wait(TimeSpan.FromSeconds(5));
            if (Interlocked.Decrement(ref activeInvocations) == 0)
                allExited.Set();
            return false;
        }

        public bool WaitUntilInvocationCount(int expected) =>
            SpinWait.SpinUntil(() => InvocationCount >= expected, TimeSpan.FromSeconds(5));

        public void Release() => release.Set();
        public bool WaitUntilAllExited() => allExited.Wait(TimeSpan.FromSeconds(5));

        private void UpdateMaximum(int value)
        {
            var observed = Volatile.Read(ref maximumConcurrentInvocations);
            while (value > observed)
            {
                var exchanged = Interlocked.CompareExchange(
                    ref maximumConcurrentInvocations, value, observed);
                if (exchanged == observed) return;
                observed = exchanged;
            }
        }

        public void Dispose()
        {
            release.Set();
            release.Dispose();
            allExited.Dispose();
        }
    }
}
