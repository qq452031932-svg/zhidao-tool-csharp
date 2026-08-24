using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using BaiduBatchTransfer.App.Services;
using Microsoft.Web.WebView2.Core;

namespace BaiduBatchTransfer.App.Features.Acceptance;

public partial class BaiduAcceptanceProbeWindow : Window
{
    private readonly Guid accountId;
    private readonly string profilePath;
    private readonly WebView2ProfileCloser profileCloser;
    private readonly IBaiduAcceptanceProbeOutputStore outputStore;
    private readonly BaiduAcceptanceProbeSession session = new();
    private readonly CancellationTokenSource lifetime = new();
    private IDisposable? profileRegistration;
    private CoreWebView2DevToolsProtocolEventReceiver? requestReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? responseReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? loadingFinishedReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? loadingFailedReceiver;
    private bool allowClose;
    private bool saving;

    public BaiduAcceptanceProbeWindow(
        Guid accountId,
        string profilePath,
        WebView2ProfileCloser profileCloser,
        IBaiduAcceptanceProbeOutputStore outputStore)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("An account ID is required.", nameof(accountId));
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        this.accountId = accountId;
        this.profilePath = Path.GetFullPath(profilePath);
        this.profileCloser = profileCloser ?? throw new ArgumentNullException(nameof(profileCloser));
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
            var environment = await CoreWebView2Environment
                .CreateAsync(userDataFolder: profilePath)
                .ConfigureAwait(true);
            lifetime.Token.ThrowIfCancellationRequested();
            await Browser.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            lifetime.Token.ThrowIfCancellationRequested();
            profileRegistration = profileCloser.Register(accountId, CloseBrowserAsync);
            ConfigureBrowser(Browser.CoreWebView2);
            await EnableNetworkObservationAsync(Browser.CoreWebView2).ConfigureAwait(true);
            StatusText.Text = "安全探针已启动。请完成一次测试转存和分享，完成后点击“保存脱敏证据并关闭”。";
            Browser.CoreWebView2.Navigate("https://pan.baidu.com/disk/main#/index");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            StatusText.Text = "现场验收窗口初始化失败。未保存任何网络数据。";
        }
    }

    private void ConfigureBrowser(CoreWebView2 core)
    {
        core.NavigationStarting += OnNavigationStarting;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
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
        var decision = BaiduNavigationPolicy.Evaluate(destination);
        if (decision.IsAllowed) return;
        e.Cancel = true;
        StatusText.Text = decision.SafeMessage ?? "已阻止不安全的页面跳转。";
    }

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
        catch (JsonException) { }
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
        catch (Exception exception) when (exception is JsonException or OverflowException) { }
    }

    private async void OnLoadingFinished(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        string? requestId = null;
        try
        {
            using (var document = JsonDocument.Parse(e.ParameterObjectAsJson))
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
                JsonSerializer.Serialize(new { requestId })).ConfigureAwait(true);
            using var bodyDocument = JsonDocument.Parse(response);
            var root = bodyDocument.RootElement;
            var isBase64 = root.TryGetProperty("base64Encoded", out var encoded) && encoded.GetBoolean();
            var body = !isBase64 && TryGetString(root, "body", out var text) ? text : null;
            session.Complete(requestId, body);
            StatusText.Text = $"已捕获 {session.Snapshot().Count} 种脱敏接口结构。请继续完成测试操作。";
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
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
        catch (JsonException) { }
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
                StatusText.Text = "还没有捕获到可保存的百度 JSON 接口结构。请先完成一次测试操作。";
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

    private async Task CloseBrowserAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Dispatcher.InvokeAsync(() => Browser.Dispose());
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        lifetime.Cancel();
        profileRegistration?.Dispose();
        try { Browser.Dispose(); }
        catch (Exception) { }
    }
}
