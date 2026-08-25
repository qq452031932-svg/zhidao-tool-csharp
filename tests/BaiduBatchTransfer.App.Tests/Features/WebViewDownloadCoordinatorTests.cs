using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Excel;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class WebViewDownloadCoordinatorTests
{
    [Fact]
    public void SessionRouterDeniesDownloadsUnlessExactlyOneCaptureIsArmed()
    {
        var identity = Identity();
        var router = new WebViewDownloadEventRouter(identity);
        var unclaimed = new FakeDownloadOperation("unclaimed.xlsx");
        router.Route(unclaimed);
        Assert.True(unclaimed.WasCancelled);

        IWebViewDownloadOperation? observed = null;
        using (router.Subscribe(operation => observed = operation))
        {
            var claimed = new FakeDownloadOperation("claimed.xlsx");
            router.Route(claimed);
            Assert.Same(claimed, observed);
            Assert.False(claimed.WasCancelled);
            Assert.Throws<InvalidOperationException>(() => router.Subscribe(_ => { }));
        }

        var late = new FakeDownloadOperation("late.xlsx");
        router.Route(late);
        Assert.True(late.WasCancelled);
    }

    [Fact]
    public async Task RejectsSessionIdentityMismatchBeforeSubscribingOrClicking()
    {
        using var directory = new TemporaryDirectory();
        var expected = Identity();
        var source = new FakeDownloadSource(expected with { AccountId = Guid.NewGuid() });
        var coordinator = new WebViewDownloadCoordinator(source);
        var clicked = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CaptureAsync(
            expected, directory.ExportRoot, _ => { clicked = true; return Task.CompletedTask; }));

        Assert.False(clicked);
        Assert.False(source.HasSubscriber);
    }

    [Fact]
    public async Task ArmsCaptureBeforeClickAndPublishesToDeterministicIdentityPath()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        var source = new FakeDownloadSource(identity);
        var coordinator = new WebViewDownloadCoordinator(source);
        var download = new FakeDownloadOperation("百度建议名称.crdownload");
        var workbook = await ValidWorkbookAsync();

        var receipt = await coordinator.CaptureAsync(identity, directory.ExportRoot, _ =>
        {
            Assert.True(source.HasSubscriber);
            source.Raise(download);
            download.Complete(workbook);
            return Task.CompletedTask;
        });

        Assert.Equal(identity, receipt.Identity);
        Assert.Equal(WebViewDownloadCoordinator.BuildOfficialFileName(identity), Path.GetFileName(receipt.FinalPath));
        Assert.Equal(workbook, await File.ReadAllBytesAsync(receipt.FinalPath));
        Assert.DoesNotContain("百度建议名称", receipt.FinalPath, StringComparison.Ordinal);
        Assert.False(source.HasSubscriber);
    }

    [Fact]
    public async Task ZeroByteOrInterruptedDownloadNeverPublishes()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        var source = new FakeDownloadSource(identity);
        var coordinator = new WebViewDownloadCoordinator(source);
        var zero = new FakeDownloadOperation("zero.xlsx");

        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.CaptureAsync(
            identity, directory.ExportRoot, _ => { source.Raise(zero); zero.Complete([]); return Task.CompletedTask; }));

        var interrupted = new FakeDownloadOperation("interrupted.xlsx");
        await Assert.ThrowsAsync<IOException>(() => coordinator.CaptureAsync(
            identity, directory.ExportRoot, _ => { source.Raise(interrupted); interrupted.Interrupt(); return Task.CompletedTask; }));

        Assert.Empty(Directory.EnumerateFiles(directory.ExportRoot, "*.xlsx", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExistingValidDeterministicArtifactIsCrashRecoveryAndDoesNotClickAgain()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        var source = new FakeDownloadSource(identity);
        var coordinator = new WebViewDownloadCoordinator(source);
        var official = Directory.CreateDirectory(Path.Combine(directory.ExportRoot, "官方导出明细")).FullName;
        var expected = Path.Combine(official, WebViewDownloadCoordinator.BuildOfficialFileName(identity));
        await File.WriteAllBytesAsync(expected, await ValidWorkbookAsync());
        var clicked = false;

        var receipt = await coordinator.CaptureAsync(identity, directory.ExportRoot, _ =>
        {
            clicked = true;
            return Task.CompletedTask;
        });

        Assert.True(receipt.WasRecovered);
        Assert.False(clicked);
        Assert.Equal(expected, receipt.FinalPath);
    }

    [Fact]
    public async Task ExistingInvalidDeterministicArtifactFailsClosedWithoutOverwriteOrClick()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        var source = new FakeDownloadSource(identity);
        var coordinator = new WebViewDownloadCoordinator(source);
        var official = Directory.CreateDirectory(Path.Combine(directory.ExportRoot, "官方导出明细")).FullName;
        var expected = Path.Combine(official, WebViewDownloadCoordinator.BuildOfficialFileName(identity));
        await File.WriteAllBytesAsync(expected, [7]);
        var clicked = false;

        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.CaptureAsync(
            identity, directory.ExportRoot, _ => { clicked = true; return Task.CompletedTask; }));

        Assert.False(clicked);
        Assert.Equal(new byte[] { 7 }, await File.ReadAllBytesAsync(expected));
    }

    [Fact]
    public async Task SecondDownloadIsDeniedAndCannotReplaceTheArmedOperation()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        var source = new FakeDownloadSource(identity);
        var coordinator = new WebViewDownloadCoordinator(source);
        var first = new FakeDownloadOperation("first.xlsx");
        var second = new FakeDownloadOperation("second.xlsx");

        await coordinator.CaptureAsync(identity, directory.ExportRoot, async _ =>
        {
            source.Raise(first);
            source.Raise(second);
            first.Complete(await ValidWorkbookAsync());
        });

        Assert.True(second.WasCancelled);
    }

    [Fact]
    public async Task CancellationDeactivatesCaptureAndLateOldSessionDownloadIsDenied()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        var source = new FakeDownloadSource(identity);
        var coordinator = new WebViewDownloadCoordinator(source);
        var active = new FakeDownloadOperation("active.xlsx");
        using var cancellation = new CancellationTokenSource();

        var capture = coordinator.CaptureAsync(identity, directory.ExportRoot, _ =>
        {
            source.Raise(active);
            cancellation.Cancel();
            return Task.CompletedTask;
        }, cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        Assert.True(active.WasCancelled);
        Assert.False(source.HasSubscriber);

        var late = new FakeDownloadOperation("late.xlsx");
        source.Raise(late);
        Assert.True(late.WasCancelled);
        Assert.Empty(Directory.EnumerateFiles(directory.ExportRoot, "*.xlsx", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MissingDownloadEventTimesOutAndReleasesTheCapture()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        var source = new FakeDownloadSource(identity);
        var coordinator = new WebViewDownloadCoordinator(
            source, downloadTimeout: TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAsync<TimeoutException>(() => coordinator.CaptureAsync(
            identity, directory.ExportRoot, _ => Task.CompletedTask));

        Assert.False(source.HasSubscriber);
        var late = new FakeDownloadOperation("late.xlsx");
        source.Raise(late);
        Assert.True(late.WasCancelled);
    }

    [Fact]
    public async Task HungDownloadTriggerUsesTheSameTimeoutAndCannotHoldTheSession()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        var source = new FakeDownloadSource(identity);
        var coordinator = new WebViewDownloadCoordinator(
            source, downloadTimeout: TimeSpan.FromMilliseconds(25));
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var capture = coordinator.CaptureAsync(
            identity, directory.ExportRoot, _ => never.Task);
        Assert.Same(capture, await Task.WhenAny(capture, Task.Delay(TimeSpan.FromMilliseconds(500))));
        await Assert.ThrowsAsync<TimeoutException>(() => capture);

        Assert.False(source.HasSubscriber);
        var late = new FakeDownloadOperation("late.xlsx");
        source.Raise(late);
        Assert.True(late.WasCancelled);
    }

    [Fact]
    public async Task DestinationSetupFailureCompletesCaptureWithTheFailure()
    {
        using var directory = new TemporaryDirectory();
        var identity = Identity();
        var source = new FakeDownloadSource(identity);
        var coordinator = new WebViewDownloadCoordinator(source);
        var download = new FakeDownloadOperation("failed.xlsx")
        {
            DestinationError = new IOException("simulated destination failure"),
        };

        await Assert.ThrowsAsync<IOException>(() => coordinator.CaptureAsync(
            identity, directory.ExportRoot, _ =>
            {
                source.Raise(download);
                return Task.CompletedTask;
            }));

        Assert.True(download.WasCancelled);
        Assert.False(source.HasSubscriber);
    }

    private static WebAutomationRequestIdentity Identity() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 3, WebAutomationOwnerKind.StandaloneShare);

    private static async Task<byte[]> ValidWorkbookAsync()
    {
        await using var stream = new MemoryStream();
        await new OpenXmlReplacementResultWriter().WriteAsync(
            stream, new ReplacementWorkbookLayout(["测试"]),
            Rows(new ReplacementWorkbookRow(1, ["内容"])));
        return stream.ToArray();
    }

    private static async IAsyncEnumerable<ReplacementWorkbookRow> Rows(params ReplacementWorkbookRow[] rows)
    {
        foreach (var row in rows) { yield return row; await Task.Yield(); }
    }

    private sealed class FakeDownloadSource(WebAutomationRequestIdentity identity) : IWebViewDownloadEventSource
    {
        private Action<IWebViewDownloadOperation>? handler;
        public WebAutomationRequestIdentity Identity { get; } = identity;
        public bool HasSubscriber => handler is not null;
        public IDisposable Subscribe(Action<IWebViewDownloadOperation> onDownloadStarting)
        {
            Assert.Null(handler);
            handler = onDownloadStarting;
            return new Subscription(() => handler = null);
        }
        public void Raise(IWebViewDownloadOperation operation)
        {
            if (handler is null) operation.Cancel(); else handler(operation);
        }
    }

    private sealed class FakeDownloadOperation(string suggestedFileName) : IWebViewDownloadOperation
    {
        private readonly TaskCompletionSource<WebViewDownloadCompletionState> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string SuggestedFileName { get; } = suggestedFileName;
        public string? DestinationPath { get; private set; }
        public bool WasCancelled { get; private set; }
        public Exception? DestinationError { get; init; }
        public void SetDestinationPath(string path)
        {
            if (DestinationError is not null) throw DestinationError;
            DestinationPath = path;
        }
        public Task<WebViewDownloadCompletionState> WaitForCompletionAsync(CancellationToken cancellationToken) =>
            completion.Task.WaitAsync(cancellationToken);
        public void Cancel() { WasCancelled = true; completion.TrySetResult(WebViewDownloadCompletionState.Cancelled); }
        public void Complete(byte[] content)
        {
            if (DestinationPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DestinationPath)!);
                File.WriteAllBytes(DestinationPath, content);
            }
            completion.TrySetResult(WebViewDownloadCompletionState.Completed);
        }
        public void Interrupt() => completion.TrySetResult(WebViewDownloadCompletionState.Interrupted);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) dispose(); }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), $"task11-download-{Guid.NewGuid():N}");
            ExportRoot = Directory.CreateDirectory(Path.Combine(Root, "export")).FullName;
        }
        public string Root { get; }
        public string ExportRoot { get; }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}
