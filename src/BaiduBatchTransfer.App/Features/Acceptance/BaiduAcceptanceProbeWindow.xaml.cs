using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace BaiduBatchTransfer.App.Features.Acceptance;

public partial class BaiduAcceptanceProbeWindow : Window, IBaiduAcceptanceProbeWindow
{
    private readonly string profilePath;
    private readonly IBaiduAcceptanceProbeOutputStore outputStore;
    private readonly BaiduAcceptanceProbeSession session = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly BaiduAcceptanceProbeObservationCoordinator observations = new();
    private readonly TaskCompletionSource initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CoreWebView2Environment? environment;
    private uint? browserProcessId;
    private CoreWebView2DevToolsProtocolEventReceiver? requestReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? responseReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? loadingFinishedReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? loadingFailedReceiver;
    private IDisposable? downloadSubscription;
    private bool allowClose;
    private bool saving;
    private Task? teardownTask;

    public BaiduAcceptanceProbeWindow(
        Guid accountId,
        string profilePath,
        IBaiduAcceptanceProbeOutputStore outputStore)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("An account ID is required.", nameof(accountId));
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        this.profilePath = Path.GetFullPath(profilePath);
        this.outputStore = outputStore ?? throw new ArgumentNullException(nameof(outputStore));
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            environment = await CoreWebView2Environment
                .CreateAsync(userDataFolder: profilePath)
                .ConfigureAwait(true);
            lifetime.Token.ThrowIfCancellationRequested();
            await Browser.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            lifetime.Token.ThrowIfCancellationRequested();
            browserProcessId = Browser.CoreWebView2.BrowserProcessId;
            ConfigureBrowser(Browser.CoreWebView2);
            await EnableNetworkObservationAsync(Browser.CoreWebView2).ConfigureAwait(true);
            StatusText.Text = "隔离的只读观察页已启动；不会登录、转存、分享或提交远端写入。";
            Browser.CoreWebView2.Navigate("https://pan.baidu.com/");
            initialization.TrySetResult();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            initialization.TrySetCanceled(lifetime.Token);
        }
        catch (Exception)
        {
            initialization.TrySetException(new InvalidOperationException("现场验收窗口初始化失败。"));
            StatusText.Text = "现场验收窗口初始化失败。未保存任何网络数据。";
            allowClose = true;
            Close();
        }
    }

    private void ConfigureBrowser(CoreWebView2 core)
    {
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        downloadSubscription = BaiduAcceptanceProbeDownloadSafety.Configure(new WebView2BaiduAcceptanceProbeDownloadHost(core));
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsScriptEnabled = false;
    }

    private async Task EnableNetworkObservationAsync(CoreWebView2 core)
    {
        requestReceiver = core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
        responseReceiver = core.GetDevToolsProtocolEventReceiver("Network.responseReceived");
        loadingFinishedReceiver = core.GetDevToolsProtocolEventReceiver("Network.loadingFinished");
        loadingFailedReceiver = core.GetDevToolsProtocolEventReceiver("Network.loadingFailed");
        requestReceiver.DevToolsProtocolEventReceived += OnRequestWillBeSent;
        responseReceiver.DevToolsProtocolEventReceived += OnResponseReceived;
        loadingFinishedReceiver.DevToolsProtocolEventReceived += OnLoadingFinished;
        loadingFailedReceiver.DevToolsProtocolEventReceived += OnLoadingFailed;
        await core.CallDevToolsProtocolMethodAsync(
            "Network.enable",
            "{\"maxTotalBufferSize\":1048576,\"maxResourceBufferSize\":262144,\"maxPostDataSize\":0}")
            .ConfigureAwait(true);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var destination = Uri.TryCreate(e.Uri, UriKind.Absolute, out var parsed) ? parsed : null;
        if (BaiduAcceptanceProbeWebPolicy.IsAllowedNavigation(destination)) return;
        e.Cancel = true;
        StatusText.Text = "已阻止非白名单页面跳转。";
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        StatusText.Text = "已阻止打开新窗口。";
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var destination = Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var parsed) ? parsed : null;
            if (BaiduAcceptanceProbeWebPolicy.IsAllowedRequest(destination, e.Request.Method)) return;
            BlockRequest(e);
        }
        catch (COMException)
        {
            try { BlockRequest(e); }
            catch (COMException) { }
        }
    }

    private void BlockRequest(CoreWebView2WebResourceRequestedEventArgs e) =>
        e.Response = environment?.CreateWebResourceResponse(
            new MemoryStream(), 403, "Blocked", "Content-Type: text/plain");

    private void OnRequestWillBeSent(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = document.RootElement;
            if (!TryGetString(root, "requestId", out var requestId) ||
                !root.TryGetProperty("request", out var request) ||
                !TryGetString(request, "url", out var url) ||
                !TryGetString(request, "method", out var method) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var destination)) return;
            session.ObserveRequest(requestId, destination, method);
        }
        catch (Exception exception) when (exception is JsonException or COMException) { }
    }

    private void OnResponseReceived(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = document.RootElement;
            if (!TryGetString(root, "requestId", out var requestId) ||
                !root.TryGetProperty("response", out var response)) return;
            var status = response.TryGetProperty("status", out var statusElement) &&
                statusElement.TryGetDouble(out var statusNumber)
                ? checked((int)Math.Round(statusNumber))
                : 0;
            _ = TryGetString(response, "mimeType", out var mimeType);
            session.ObserveResponse(requestId, status, mimeType);
        }
        catch (Exception exception) when (exception is JsonException or OverflowException or COMException) { }
    }

    private void OnLoadingFinished(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e) =>
        TryTrackLoadingFinished(e);

    private void TryTrackLoadingFinished(CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            var payload = e.ParameterObjectAsJson;
            observations.Track(cancellationToken => ObserveLoadingFinishedAsync(payload, cancellationToken));
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ObjectDisposedException) { }
    }

    private async Task ObserveLoadingFinishedAsync(string parameterObjectAsJson, CancellationToken cancellationToken)
    {
        string? requestId = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var document = JsonDocument.Parse(parameterObjectAsJson))
            {
                if (!TryGetString(document.RootElement, "requestId", out requestId)) return;
            }
            if (!session.ShouldInspectBody(requestId))
            {
                session.Complete(requestId, null);
                return;
            }
            var response = await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync(
                "Network.getResponseBody",
                JsonSerializer.Serialize(new { requestId })).WaitAsync(cancellationToken).ConfigureAwait(true);
            using var bodyDocument = JsonDocument.Parse(response);
            var root = bodyDocument.RootElement;
            var isBase64 = root.TryGetProperty("base64Encoded", out var encoded) && encoded.GetBoolean();
            var body = !isBase64 && TryGetString(root, "body", out var text) ? text : null;
            session.Complete(requestId, body);
            StatusText.Text = $"已捕获 {session.Snapshot().Count} 种脱敏接口结构。只读观察仍在进行。";
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
            ArgumentException or COMException or ObjectDisposedException or OperationCanceledException)
        {
            if (!string.IsNullOrWhiteSpace(requestId)) session.Complete(requestId, null);
        }
    }

    private void OnLoadingFailed(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.ParameterObjectAsJson);
            if (TryGetString(document.RootElement, "requestId", out var requestId))
                session.Complete(requestId, null);
        }
        catch (Exception exception) when (exception is JsonException or COMException) { }
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value = property.GetString() ?? string.Empty);
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (saving) return;
        saving = true;
        try
        {
            var evidence = session.Snapshot();
            if (evidence.Count == 0)
            {
                StatusText.Text = "还没有捕获到可保存的百度 JSON 接口结构。请等待只读页面加载。";
                return;
            }
            await outputStore.SaveAsync(evidence, lifetime.Token).ConfigureAwait(true);
            StatusText.Text = "脱敏证据已保存在本机。";
            allowClose = true;
            Close();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            StatusText.Text = "保存脱敏证据失败；未显示或记录异常原文。";
        }
        finally { saving = false; }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        allowClose = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (saving)
        {
            e.Cancel = true;
            return;
        }
        if (!allowClose)
        {
            var decision = MessageBox.Show(
                "关闭后，本次尚未保存的脱敏证据会丢弃。确定关闭吗？",
                "百度现场验收",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (decision != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        lifetime.Cancel();
    }

    public Task WaitForInitializationAsync(CancellationToken cancellationToken = default) =>
        initialization.Task.WaitAsync(cancellationToken);

    public async Task RequestCloseAsync(CancellationToken cancellationToken = default)
    {
        var close = Dispatcher.InvokeAsync(() => { allowClose = true; Close(); }).Task;
        await close.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task WaitForClosedAsync(CancellationToken cancellationToken = default) =>
        closed.Task.WaitAsync(cancellationToken);

    private void OnClosed(object? sender, EventArgs e)
    {
        lifetime.Cancel();
        initialization.TrySetException(new InvalidOperationException("现场验收窗口在初始化前关闭。"));
        teardownTask ??= CompleteTeardownAsync();
    }

    private async Task CompleteTeardownAsync()
    {
        EventHandler<CoreWebView2BrowserProcessExitedEventArgs>? exited = null;
        try
        {
            UnsubscribeNetworkObservation();
            await observations.StopAsync(BaiduAcceptanceProbeTeardownTiming.ObservationStopTimeout).ConfigureAwait(true);
            var lifecycle = new BaiduAcceptanceProbeBrowserLifecycle(
                browserProcessId ?? uint.MaxValue, () => Browser.Dispose(), BaiduAcceptanceProbeTeardownTiming.BrowserExitTimeout);
            if (environment is not null)
            {
                exited = (_, eventArgs) =>
                {
                    lifecycle.NotifyBrowserProcessExited(eventArgs.BrowserProcessId);
                };
                environment.BrowserProcessExited += exited;
            }
            if (environment is not null)
            {
                await lifecycle.DisposeAfterExitAsync().ConfigureAwait(true);
            }
            else Browser.Dispose();
        }
        catch (Exception) { }
        finally
        {
            try
            {
                if (environment is not null && exited is not null) environment.BrowserProcessExited -= exited;
            }
            catch (COMException) { }
            try { observations.Dispose(); }
            catch (Exception) { }
            closed.TrySetResult();
        }
    }

    private void UnsubscribeNetworkObservation()
    {
        try { if (requestReceiver is not null) requestReceiver.DevToolsProtocolEventReceived -= OnRequestWillBeSent; } catch (COMException) { }
        try { if (responseReceiver is not null) responseReceiver.DevToolsProtocolEventReceived -= OnResponseReceived; } catch (COMException) { }
        try { if (loadingFinishedReceiver is not null) loadingFinishedReceiver.DevToolsProtocolEventReceived -= OnLoadingFinished; } catch (COMException) { }
        try { if (loadingFailedReceiver is not null) loadingFailedReceiver.DevToolsProtocolEventReceived -= OnLoadingFailed; } catch (COMException) { }
        try
        {
            if (Browser.CoreWebView2 is not { } core) return;
            core.NavigationStarting -= OnNavigationStarting;
            core.NewWindowRequested -= OnNewWindowRequested;
            downloadSubscription?.Dispose();
            core.WebResourceRequested -= OnWebResourceRequested;
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException) { }
    }
}
