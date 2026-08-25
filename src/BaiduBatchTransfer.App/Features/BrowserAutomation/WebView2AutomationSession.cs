using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using Microsoft.Web.WebView2.Core;

namespace BaiduBatchTransfer.App.Features.BrowserAutomation;

public sealed class WebView2AutomationSessionFactory : IVisibleWebAutomationSessionFactory
{
    public Task<IVisibleWebAutomationSession> CreateAsync(
        WebAutomationRequestIdentity identity,
        string profilePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var window = new WebAutomationWindow
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        return Task.FromResult<IVisibleWebAutomationSession>(
            new WebView2AutomationSession(identity, profilePath, window));
    }
}

public sealed class WebView2AutomationSession : IVisibleWebAutomationSession, IRemoteResourcePickerSession
{
    private readonly WebAutomationRequestIdentity identity;
    private readonly string profilePath;
    private readonly WebAutomationWindow window;
    private CoreWebView2? core;
    private int closed;

    internal WebView2AutomationSession(
        WebAutomationRequestIdentity identity,
        string profilePath,
        WebAutomationWindow window)
    {
        this.identity = (identity ?? throw new ArgumentNullException(nameof(identity))).Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        this.profilePath = profilePath;
        this.window = window ?? throw new ArgumentNullException(nameof(window));
    }

    public CancellationToken UserClosed => window.UserClosed;

    public async Task ShowAsync(Uri initialUri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref closed) != 0) throw new ObjectDisposedException(nameof(WebView2AutomationSession));
        var decision = BaiduNavigationPolicy.Evaluate(initialUri);
        if (!decision.IsAllowed) throw new InvalidOperationException(decision.SafeMessage);

        window.Show();
        var environment = await CoreWebView2Environment
            .CreateAsync(userDataFolder: profilePath)
            .ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        await window.WebView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        core = window.WebView.CoreWebView2;
        Harden(core);
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.PermissionRequested += OnPermissionRequested;
        core.Navigate(initialUri.AbsoluteUri);
    }

    public Task ShowHandoffAsync(
        WebAutomationHandoffPrompt prompt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (prompt.Identity != identity)
            throw new InvalidOperationException("人工接管请求不属于当前窗口。");
        window.ShowHandoff(prompt);
        window.Activate();
        return Task.CompletedTask;
    }

    public Task<WebAutomationUserChoice> WaitForHandoffChoiceAsync(
        CancellationToken cancellationToken = default) =>
        window.WaitForHandoffChoiceAsync(cancellationToken);

    public Task<RemoteResourcePickerDecision> WaitForApplicationDecisionAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return window.Dispatcher.InvokeAsync(() =>
        {
            window.ShowResourcePickerActions();
            return window.WaitForResourcePickerDecisionAsync(cancellationToken);
        }).Task.Unwrap();
    }

    public Task<RemoteResourceSelectionPage> ReadSelectionPageAsync(
        string? pageToken,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<RemoteResourceSelectionPage>(
            new WebAutomationAuthorizationException(
                BaiduAutomationDenialReason.CatalogNotValidated,
                "资源选择定位协议尚未通过现场验证，未读取网页内容。"));
    }

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref closed, 1) != 0) return Task.CompletedTask;
        Detach();
        if (!window.IsClosed) window.CloseProgrammatically();
        window.WebView.Dispose();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref closed) == 0) await CloseAsync().ConfigureAwait(true);
    }

    private static void Harden(CoreWebView2 browser)
    {
        browser.Settings.AreDevToolsEnabled = false;
        browser.Settings.AreDefaultContextMenusEnabled = false;
        browser.Settings.IsStatusBarEnabled = false;
        browser.Settings.IsPasswordAutosaveEnabled = false;
        browser.Settings.IsGeneralAutofillEnabled = false;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var destination = Uri.TryCreate(e.Uri, UriKind.Absolute, out var parsed) ? parsed : null;
        if (BaiduNavigationPolicy.Evaluate(destination).IsAllowed) return;
        e.Cancel = true;
    }

    private static void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
    }

    private static void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;
        e.Handled = true;
    }

    private static void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.State = CoreWebView2PermissionState.Deny;
        e.Handled = true;
    }

    private void Detach()
    {
        if (core is null) return;
        core.NavigationStarting -= OnNavigationStarting;
        core.NewWindowRequested -= OnNewWindowRequested;
        core.DownloadStarting -= OnDownloadStarting;
        core.PermissionRequested -= OnPermissionRequested;
        core = null;
    }
}
