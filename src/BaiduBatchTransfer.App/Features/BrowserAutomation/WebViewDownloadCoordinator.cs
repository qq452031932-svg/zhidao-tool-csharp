using System.IO;
using BaiduBatchTransfer.Core.Abstractions;
using DocumentFormat.OpenXml.Packaging;

namespace BaiduBatchTransfer.App.Features.BrowserAutomation;

public enum WebViewDownloadCompletionState
{
    Completed,
    Cancelled,
    Interrupted,
}

public interface IWebViewDownloadOperation
{
    string SuggestedFileName { get; }
    void SetDestinationPath(string path);
    Task<WebViewDownloadCompletionState> WaitForCompletionAsync(CancellationToken cancellationToken);
    void Cancel();
}

public interface IWebViewDownloadEventSource
{
    WebAutomationRequestIdentity Identity { get; }
    IDisposable Subscribe(Action<IWebViewDownloadOperation> onDownloadStarting);
}

public sealed class WebViewDownloadEventRouter(WebAutomationRequestIdentity identity) : IWebViewDownloadEventSource
{
    private readonly object gate = new();
    private Action<IWebViewDownloadOperation>? subscriber;
    public WebAutomationRequestIdentity Identity { get; } =
        (identity ?? throw new ArgumentNullException(nameof(identity))).Validate();

    public IDisposable Subscribe(Action<IWebViewDownloadOperation> onDownloadStarting)
    {
        ArgumentNullException.ThrowIfNull(onDownloadStarting);
        lock (gate)
        {
            if (subscriber is not null) throw new InvalidOperationException("当前窗口已有下载接管请求。");
            subscriber = onDownloadStarting;
        }
        return new Subscription(this, onDownloadStarting);
    }

    public void Route(IWebViewDownloadOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (gate)
        {
            if (subscriber is null)
            {
                operation.Cancel();
                return;
            }
            try { subscriber(operation); }
            catch
            {
                operation.Cancel();
                throw;
            }
        }
    }

    private void Unsubscribe(Action<IWebViewDownloadOperation> expected)
    {
        lock (gate)
        {
            if (ReferenceEquals(subscriber, expected)) subscriber = null;
        }
    }

    private sealed class Subscription(
        WebViewDownloadEventRouter owner,
        Action<IWebViewDownloadOperation> subscriber) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) owner.Unsubscribe(subscriber);
        }
    }
}

public interface IDownloadedWorkbookValidator
{
    Task<bool> IsValidAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class OpenXmlDownloadedWorkbookValidator : IDownloadedWorkbookValidator
{
    public Task<bool> IsValidAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var document = SpreadsheetDocument.Open(path, false);
            var workbook = document.WorkbookPart?.Workbook;
            var valid = workbook is not null && workbook.Sheets is not null;
            return Task.FromResult(valid);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or FileFormatException or OpenXmlPackageException)
        {
            return Task.FromResult(false);
        }
    }
}

public sealed record WebViewDownloadReceipt(
    WebAutomationRequestIdentity Identity,
    string FinalPath,
    bool WasRecovered)
{
    public override string ToString() =>
        $"WebViewDownloadReceipt {{ Identity = {Identity}, WasRecovered = {WasRecovered}, FinalPath = [REDACTED] }}";
}

public sealed class WebViewDownloadCoordinator
{
    private readonly IWebViewDownloadEventSource source;
    private readonly IDownloadedWorkbookValidator validator;
    private readonly TimeSpan downloadTimeout;
    private int captureInProgress;

    public WebViewDownloadCoordinator(
        IWebViewDownloadEventSource source,
        IDownloadedWorkbookValidator? validator = null,
        TimeSpan? downloadTimeout = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.validator = validator ?? new OpenXmlDownloadedWorkbookValidator();
        this.downloadTimeout = downloadTimeout ?? TimeSpan.FromMinutes(2);
        if (this.downloadTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(downloadTimeout));
    }

    public async Task<WebViewDownloadReceipt> CaptureAsync(
        WebAutomationRequestIdentity identity,
        string destinationDirectory,
        Func<CancellationToken, Task> triggerDownloadAsync,
        CancellationToken cancellationToken = default)
    {
        identity = (identity ?? throw new ArgumentNullException(nameof(identity))).Validate();
        if (identity.OwnerKind is not (WebAutomationOwnerKind.TransferShare or WebAutomationOwnerKind.StandaloneShare))
            throw new InvalidOperationException("当前操作类型不允许接管下载。");
        if (source.Identity != identity)
            throw new InvalidOperationException("下载窗口不属于当前操作。");
        var destination = NormalizeAbsoluteDirectory(destinationDirectory, nameof(destinationDirectory));
        ArgumentNullException.ThrowIfNull(triggerDownloadAsync);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref captureInProgress, 1, 0) != 0)
            throw new InvalidOperationException("当前浏览器已有下载任务。");

        IWebViewDownloadOperation? active = null;
        string? stagingPath = null;
        IDisposable? subscription = null;
        var gate = new object();
        var accepting = true;
        var published = false;
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var officialDirectory = Directory.CreateDirectory(Path.Combine(destination, "官方导出明细")).FullName;
            var finalPath = Path.Combine(officialDirectory, BuildOfficialFileName(identity));
            if (File.Exists(finalPath))
            {
                if (!await validator.IsValidAsync(finalPath, cancellationToken).ConfigureAwait(false))
                    throw new InvalidDataException("已存在的官方明细文件不完整，未覆盖该文件。");
                return new(identity, finalPath, WasRecovered: true);
            }

            var stagingDirectory = Directory.CreateDirectory(Path.Combine(officialDirectory, ".pending")).FullName;
            var nonce = Guid.NewGuid().ToString("N");
            stagingPath = Path.Combine(stagingDirectory, $"{identity.OperationId:N}-g{identity.Generation}-{nonce}.xlsx");

            void OnDownloadStarting(IWebViewDownloadOperation operation)
            {
                if (operation is null) return;
                lock (gate)
                {
                    if (!accepting || active is not null || completion.Task.IsCompleted)
                    {
                        operation.Cancel();
                        return;
                    }
                    active = operation;
                    try
                    {
                        operation.SetDestinationPath(stagingPath);
                        _ = ObserveCompletionAsync(operation, stagingPath, completion);
                    }
                    catch (Exception exception)
                    {
                        accepting = false;
                        operation.Cancel();
                        completion.TrySetException(exception);
                    }
                }
            }

            subscription = source.Subscribe(OnDownloadStarting);
            using var timeoutCancellation = new CancellationTokenSource(downloadTimeout);
            using var phaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCancellation.Token);
            using var cancellationRegistration = phaseCancellation.Token.Register(() =>
            {
                lock (gate)
                {
                    accepting = false;
                    active?.Cancel();
                }
                completion.TrySetCanceled(phaseCancellation.Token);
            });

            try
            {
                var trigger = triggerDownloadAsync(phaseCancellation.Token);
                _ = trigger.ContinueWith(
                    task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                await trigger.WaitAsync(phaseCancellation.Token).ConfigureAwait(false);
                var completedPath = await completion.Task
                    .WaitAsync(phaseCancellation.Token)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!await validator.IsValidAsync(completedPath, cancellationToken).ConfigureAwait(false))
                    throw new InvalidDataException("下载文件不是完整可读的 Excel 工作簿。");

                lock (gate) accepting = false;
                subscription.Dispose();
                subscription = null;
                cancellationToken.ThrowIfCancellationRequested();
                if (source.Identity != identity)
                    throw new InvalidOperationException("下载窗口归属已变化。");
                try
                {
                    File.Move(completedPath, finalPath, overwrite: false);
                    published = true;
                    return new(identity, finalPath, WasRecovered: false);
                }
                catch (IOException) when (File.Exists(finalPath))
                {
                    if (!await validator.IsValidAsync(finalPath, cancellationToken).ConfigureAwait(false)) throw;
                    TryDelete(completedPath);
                    published = true;
                    return new(identity, finalPath, WasRecovered: true);
                }
            }
            catch (OperationCanceledException) when (
                timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("等待百度导出下载超时。");
            }
        }
        finally
        {
            lock (gate)
            {
                accepting = false;
                if (!published) active?.Cancel();
            }
            subscription?.Dispose();
            if (!published && stagingPath is not null) TryDelete(stagingPath);
            Interlocked.Exchange(ref captureInProgress, 0);
        }
    }

    public static string BuildOfficialFileName(WebAutomationRequestIdentity identity)
    {
        identity = (identity ?? throw new ArgumentNullException(nameof(identity))).Validate();
        return $"百度官方明细_{identity.BatchId:N}_{identity.OperationId:N}_g{identity.Generation}.xlsx";
    }

    private static async Task ObserveCompletionAsync(
        IWebViewDownloadOperation operation,
        string stagingPath,
        TaskCompletionSource<string> completion)
    {
        try
        {
            var state = await operation.WaitForCompletionAsync(CancellationToken.None).ConfigureAwait(false);
            if (state != WebViewDownloadCompletionState.Completed)
            {
                completion.TrySetException(new IOException("浏览器下载未完成。"));
                return;
            }
            var info = new FileInfo(stagingPath);
            if (!info.Exists || info.Length <= 0)
                throw new InvalidDataException("下载文件不完整。");
            using (var stream = new FileStream(stagingPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                if (stream.Length <= 0) throw new InvalidDataException("下载文件不完整。");
            }
            completion.TrySetResult(stagingPath);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private static string NormalizeAbsoluteDirectory(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("目录必须是绝对路径。", parameterName);
        return Path.GetFullPath(path);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
