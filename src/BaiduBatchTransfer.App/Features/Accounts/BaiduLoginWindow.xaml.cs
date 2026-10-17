using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.BaiduAdapter.Authentication;
using BaiduBatchTransfer.Core.Domain;
using Microsoft.Web.WebView2.Core;

namespace BaiduBatchTransfer.App.Features.Accounts;

public partial class BaiduLoginWindow : Window
{
    private readonly Guid accountId;
    private readonly Guid ownershipToken;
    private readonly BaiduLoginWindowPurpose purpose;
    private readonly LoginProfileBootstrapper profileBootstrapper;
    private readonly BaiduLoginSessionRegistrar sessionRegistrar;
    private readonly WebView2ProfileCloser profileCloser;
    private LoginWindowCloseGate? closeGate;
    private readonly WebViewCookieSessionBridge cookieBridge;
    private readonly BaiduLoginViewModel viewModel = new();
    private readonly BaiduLoginDomAutomationCoordinator domAutomation = new();
    private readonly BaiduLoginAutoCaptureCoordinator captureCoordinator = new();
    private readonly BaiduLoginSettlingGate settlingGate = new(TimeSpan.FromSeconds(3));
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource profilePreparationStopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer pollTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private IDisposable? profileRegistration;
    private LoginProfileLease? profileLease;
    private AccountProfileOwnership? ownership;
    private bool profilePrepared;
    private bool pollInProgress;
    private bool cleanupInProgress;
    private bool allowClose;
    private readonly IRunLog? runLog;
    private string? blockedNavigationHost;
    private const string RunLogSource = "账号登录";

    public BaiduLoginWindow(
        Guid accountId,
        Guid ownershipToken,
        BaiduLoginWindowPurpose purpose,
        LoginProfileBootstrapper profileBootstrapper,
        BaiduLoginSessionRegistrar sessionRegistrar,
        WebView2ProfileCloser profileCloser,
        WebViewCookieSessionBridge cookieBridge,
        IRunLog? runLog = null)
    {
        this.runLog = runLog;
        if (accountId == Guid.Empty) throw new ArgumentException("An account ID is required.", nameof(accountId));
        if (ownershipToken == Guid.Empty) throw new ArgumentException("An ownership token is required.", nameof(ownershipToken));
        this.accountId = accountId;
        this.ownershipToken = ownershipToken;
        this.purpose = purpose;
        this.profileBootstrapper = profileBootstrapper ?? throw new ArgumentNullException(nameof(profileBootstrapper));
        this.sessionRegistrar = sessionRegistrar ?? throw new ArgumentNullException(nameof(sessionRegistrar));
        this.profileCloser = profileCloser ?? throw new ArgumentNullException(nameof(profileCloser));
        this.cookieBridge = cookieBridge ?? throw new ArgumentNullException(nameof(cookieBridge));
        ownership = AccountProfileOwnership.NewLoginProfile(ownershipToken);
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        pollTimer.Tick += OnPollTimer;
        captureCoordinator.CloseRequested += (_, _) => Dispatcher.BeginInvoke(CloseAfterRegistration);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            try { profileRegistration = profileCloser.Register(ownershipToken, CloseBrowserAsync); }
            catch
            {
                profilePreparationStopped.TrySetResult();
                throw;
            }
            closeGate = new LoginWindowCloseGate(
                profilePreparationStopped.Task,
                cancellationToken => profileCloser.CloseAsync(ownershipToken, cancellationToken));

            string profilePath;
            try
            {
                profileLease = await profileBootstrapper
                    .PrepareAttemptAsync(ownershipToken, lifetime.Token)
                    .ConfigureAwait(true);
                profilePath = profileLease.Path;
            }
            finally { profilePreparationStopped.TrySetResult(); }
            profilePrepared = true;
            lifetime.Token.ThrowIfCancellationRequested();

            var environment = await CoreWebView2Environment
                .CreateAsync(userDataFolder: profilePath)
                .ConfigureAwait(true);
            lifetime.Token.ThrowIfCancellationRequested();
            await Browser.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            lifetime.Token.ThrowIfCancellationRequested();

            Browser.CoreWebView2.NavigationStarting += OnNavigationStarting;
            Browser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Browser.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            Browser.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            viewModel.ReportWaiting();
            pollTimer.Start();
            Browser.CoreWebView2.Navigate(BaiduLoginFlowPolicy.InitialLoginUri.AbsoluteUri);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // The owned attempt profile is cleaned only after the window has actually closed,
            // so WebView2 has released its remaining file handles.
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ReportRuntimeMissing();
        }
        catch (Exception error)
        {
            if (!profilePrepared)
            {
                runLog?.Write(RunLogSource, $"【错误】登录窗口打不开：独立登录目录创建失败（{DescribeError(error)}）。");
                viewModel.ReportFailure(BaiduLoginUiText.ProfileFailed);
                return;
            }
            // 有的电脑没装组件时抛的不是 WebView2RuntimeNotFoundException，再确认一次。
            if (new WebView2RuntimeProbe().Check().IsMissing)
            {
                ReportRuntimeMissing();
                return;
            }
            var detail = DescribeError(error);
            runLog?.Write(RunLogSource, $"【错误】登录窗口的网页组件启动失败（{detail}，组件版本 {RuntimeVersion()}）。");
            viewModel.ReportFailure(BaiduLoginUiText.RuntimeStartFailed);
        }
    }

    private void ReportRuntimeMissing()
    {
        runLog?.Write(RunLogSource,
            "【错误】登录窗口打不开：这台电脑没有安装 WebView2 运行组件（Microsoft Edge WebView2 Runtime）。");
        viewModel.ReportRuntimeMissing();
    }

    // 只记异常类型和错误码，不记消息原文（可能带本机路径）。
    private static string DescribeError(Exception error) => $"{error.GetType().Name}，0x{error.HResult:X8}";

    private static string RuntimeVersion() => new WebView2RuntimeProbe().Check().Version ?? "未知";

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var destination = Uri.TryCreate(e.Uri, UriKind.Absolute, out var parsed) ? parsed : null;
        var decision = BaiduNavigationPolicy.Evaluate(destination);
        if (decision.IsAllowed) return;
        e.Cancel = true;
        // 只记域名：被运营商、公司网络认证页或百度验证页带走时，一看就知道是哪儿。
        blockedNavigationHost = destination?.IdnHost is { Length: > 0 } host ? host : "无法识别的地址";
        runLog?.Write(RunLogSource, $"【错误】登录页面被跳转到非百度网盘页面，已阻止（{blockedNavigationHost}）。");
        viewModel.ReportFailure(decision.SafeMessage ?? "已阻止不安全的页面跳转。");
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            blockedNavigationHost = null;
            return;
        }
        // 被中止的加载不是故障：我们拦下的跳转、重新扫码时的 Stop、页面自己又跳到下一页，都会这样结束。
        // 拦下跳转时保留「已阻止」的说明，不再盖成别的提示（原来一律盖成「请安装组件」）。
        if (blockedNavigationHost is not null || e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
        {
            blockedNavigationHost = null;
            return;
        }
        var reason = DescribeNavigationError(e.WebErrorStatus);
        runLog?.Write(RunLogSource,
            $"【错误】百度网盘登录页打不开：{reason}（{e.WebErrorStatus}，HTTP {e.HttpStatusCode}，组件版本 {RuntimeVersion()}）。");
        viewModel.ReportFailure(string.Format(BaiduLoginUiText.NavigationFailed, reason));
    }

    internal static string DescribeNavigationError(CoreWebView2WebErrorStatus status) => status switch
    {
        CoreWebView2WebErrorStatus.HostNameNotResolved => "域名解析失败",
        CoreWebView2WebErrorStatus.Timeout => "连接超时",
        CoreWebView2WebErrorStatus.Disconnected or CoreWebView2WebErrorStatus.ConnectionAborted or
            CoreWebView2WebErrorStatus.ConnectionReset => "网络连接中断",
        CoreWebView2WebErrorStatus.CannotConnect or CoreWebView2WebErrorStatus.ServerUnreachable => "连不上百度服务器",
        CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect or CoreWebView2WebErrorStatus.CertificateExpired or
            CoreWebView2WebErrorStatus.ClientCertificateContainsErrors or CoreWebView2WebErrorStatus.CertificateRevoked or
            CoreWebView2WebErrorStatus.CertificateIsInvalid => "安全证书有问题，可能被代理或安全软件拦截",
        CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse => "服务器返回异常",
        _ => "网络错误",
    };

    private void OnDownloadWebView2Click(object sender, RoutedEventArgs e)
    {
        if (!WebView2RuntimeText.TryOpenDownloadPage())
            viewModel.ReportFailure($"无法自动打开浏览器，请手动访问：{WebView2RuntimeText.DownloadPage}");
    }

    private async void OnPollTimer(object? sender, EventArgs e)
    {
        if (pollInProgress || lifetime.IsCancellationRequested || Browser.CoreWebView2 is null) return;
        pollInProgress = true;
        try { await PollLoginStateAsync(lifetime.Token).ConfigureAwait(true); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            viewModel.ReportFailure(BaiduLoginUiText.CaptureFailed);
            captureCoordinator.ResetAfterFailure();
        }
        finally { pollInProgress = false; }
    }

    private async Task PollLoginStateAsync(CancellationToken cancellationToken)
    {
        var currentUri = Uri.TryCreate(Browser.Source?.AbsoluteUri, UriKind.Absolute, out var parsed) ? parsed : null;
        var coreCookies = await Browser.CoreWebView2.CookieManager
            .GetCookiesAsync(string.Empty)
            .ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        var capturedAt = DateTimeOffset.UtcNow;
        var capturedCookies = BaiduLoginCookieCapturePolicy.CreateValidSnapshot(
            coreCookies.Select(MapCookie));
        var sessionCookies = capturedCookies.Select(MapSessionCookie).ToArray();
        var authentication = BaiduLoginAuthenticationDetector.Evaluate(currentUri, sessionCookies);

        if (!authentication.IsAuthenticated)
        {
            settlingGate.IsReady(authentication, capturedAt);
            await TryOpenLoginDialogAsync(currentUri, authentication).ConfigureAwait(true);
            return;
        }

        if (!settlingGate.IsReady(authentication, capturedAt))
        {
            viewModel.ReportSettling();
            return;
        }
        if (!captureCoordinator.TryBeginCapture(authentication)) return;

        pollTimer.Stop();
        viewModel.ReportCapturing();
        try
        {
            var session = cookieBridge.CreateUnboundSession(
                Guid.NewGuid(), capturedCookies, capturedAt, cancellationToken);
            var result = await sessionRegistrar.RegisterAsync(
                purpose, accountId, ownership!, session, capturedAt, cancellationToken).ConfigureAwait(true);
            var succeeded = result.AddResult is not null
                ? viewModel.ReportRegistrationResult(result.AddResult)
                : result.Succeeded;
            if (!succeeded && result.AddResult is null)
            {
                viewModel.ReportFailure(result.FailureMessage ?? BaiduLoginUiText.CaptureFailed);
                await ResetForAnotherScanAsync().ConfigureAwait(true);
            }
            if (succeeded)
            {
                captureCoordinator.CompleteRegistration();
            }
        }
        catch (AddAccountCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (AddAccountOperationException)
        {
            viewModel.ReportFailure(BaiduLoginUiText.CleanupPending);
        }
    }

    private async Task TryOpenLoginDialogAsync(
        Uri? currentUri,
        BaiduLoginAuthenticationState authentication)
    {
        if (!domAutomation.TryBeginAttempt(currentUri, authentication))
        {
            if (domAutomation.IsExhausted) viewModel.ReportFailure(BaiduLoginUiText.LoginButtonMissing);
            return;
        }

        var clicked = false;
        try
        {
            var result = await Browser.CoreWebView2
                .ExecuteScriptAsync(BaiduLoginDomClickPolicy.LoginControlClickScript)
                .ConfigureAwait(true);
            clicked = BaiduLoginDomClickPolicy.WasClickPerformed(result);
        }
        finally { domAutomation.CompleteAttempt(clicked); }
    }

    private static CapturedWebCookie MapCookie(CoreWebView2Cookie cookie) => new(
        cookie.Name,
        cookie.Value,
        cookie.Domain,
        cookie.Path,
        cookie.IsSession ? null : new DateTimeOffset(cookie.Expires.ToUniversalTime()),
        cookie.IsSecure,
        cookie.IsHttpOnly);

    private static SessionCookie MapSessionCookie(CapturedWebCookie cookie) => new(
        cookie.Name,
        cookie.Value,
        cookie.Domain,
        cookie.Path,
        cookie.ExpiresUtc,
        cookie.IsSecure,
        cookie.IsHttpOnly);

    private async Task CloseBrowserAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lifetime.Cancel();
        await Dispatcher.InvokeAsync(() =>
        {
            pollTimer.Stop();
            if (Browser.CoreWebView2 is not null)
            {
                Browser.CoreWebView2.Stop();
                Browser.CoreWebView2.CookieManager.DeleteAllCookies();
            }
            Browser.Dispose();
        });
        await profilePreparationStopped.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void CloseAfterRegistration()
    {
        Close();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose)
        {
            lifetime.Cancel();
            return;
        }

        e.Cancel = true;
        if (cleanupInProgress) return;
        cleanupInProgress = true;
        lifetime.Cancel();
        try
        {
            if (closeGate is not null)
                await closeGate.WaitForSafeCloseAsync(CancellationToken.None).ConfigureAwait(true);
            else
                await profilePreparationStopped.Task.ConfigureAwait(true);
            allowClose = true;
            Close();
        }
        catch (Exception)
        {
            cleanupInProgress = false;
            viewModel.ReportFailure(BaiduLoginUiText.CleanupPending);
        }
    }

    private Task ResetForAnotherScanAsync()
    {
        if (Browser.CoreWebView2 is null) return Task.CompletedTask;
        Browser.CoreWebView2.Stop();
        Browser.CoreWebView2.CookieManager.DeleteAllCookies();
        captureCoordinator.ResetAfterFailure();
        settlingGate.Reset();
        domAutomation.Reset();
        Browser.CoreWebView2.Navigate(BaiduLoginFlowPolicy.InitialLoginUri.AbsoluteUri);
        viewModel.ReportWaiting();
        pollTimer.Start();
        return Task.CompletedTask;
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        pollTimer.Stop();
        lifetime.Cancel();
        profileRegistration?.Dispose();
        try { Browser.Dispose(); }
        catch (Exception) { /* The profile closer may already have disposed the browser. */ }
        if (!profilePrepared || profileLease is null) return;
        try
        {
            await profileBootstrapper
                .CleanupAttemptAfterBrowserExitAsync(profileLease, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception)
        {
            // The owned/quarantined profile remains recoverable; account registration has
            // already completed independently and the login window must stay closed.
        }
    }
}
