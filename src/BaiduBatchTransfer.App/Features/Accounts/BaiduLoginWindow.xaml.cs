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
    private readonly LoginProfileBootstrapper profileBootstrapper;
    private readonly AddAccountService addAccountService;
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
    private AccountProfileOwnership? ownership;
    private bool profilePrepared;
    private bool pollInProgress;
    private bool registrationCompleted;
    private bool cleanupInProgress;
    private bool allowClose;

    public BaiduLoginWindow(
        Guid accountId,
        Guid ownershipToken,
        LoginProfileBootstrapper profileBootstrapper,
        AddAccountService addAccountService,
        WebView2ProfileCloser profileCloser,
        WebViewCookieSessionBridge cookieBridge)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("An account ID is required.", nameof(accountId));
        if (ownershipToken == Guid.Empty) throw new ArgumentException("An ownership token is required.", nameof(ownershipToken));
        this.accountId = accountId;
        this.ownershipToken = ownershipToken;
        this.profileBootstrapper = profileBootstrapper ?? throw new ArgumentNullException(nameof(profileBootstrapper));
        this.addAccountService = addAccountService ?? throw new ArgumentNullException(nameof(addAccountService));
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
            try { profileRegistration = profileCloser.Register(accountId, CloseBrowserAsync); }
            catch
            {
                profilePreparationStopped.TrySetResult();
                throw;
            }
            closeGate = new LoginWindowCloseGate(
                profilePreparationStopped.Task,
                cancellationToken => profileCloser.CloseAsync(accountId, cancellationToken));

            string profilePath;
            try
            {
                profilePath = await profileBootstrapper
                    .PrepareAsync(accountId, ownershipToken, lifetime.Token)
                    .ConfigureAwait(true);
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
            if (!cleanupInProgress)
                await CleanupAfterCanceledInitializationAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            viewModel.ReportFailure(profilePrepared
                ? BaiduLoginUiText.InitializationFailed
                : BaiduLoginUiText.ProfileFailed);
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var destination = Uri.TryCreate(e.Uri, UriKind.Absolute, out var parsed) ? parsed : null;
        var decision = BaiduNavigationPolicy.Evaluate(destination);
        if (decision.IsAllowed) return;
        e.Cancel = true;
        viewModel.ReportFailure(decision.SafeMessage ?? "已阻止不安全的页面跳转。");
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
            viewModel.ReportFailure(BaiduLoginUiText.InitializationFailed);
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
            var result = await addAccountService.AddAsync(
                new AddAccountRequest(accountId, session, capturedAt, ownership!),
                cancellationToken).ConfigureAwait(true);
            if (viewModel.ReportRegistrationResult(result))
            {
                registrationCompleted = true;
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
            if (!registrationCompleted && profilePrepared && ownership is not null)
            {
                var cleanup = await addAccountService
                    .RetryProfileCleanupAsync(accountId, ownership, CancellationToken.None)
                    .ConfigureAwait(true);
                if (cleanup == ProfileCleanupStatus.Pending)
                    viewModel.ReportFailure(BaiduLoginUiText.CleanupPending);
            }
            allowClose = true;
            Close();
        }
        catch (Exception)
        {
            cleanupInProgress = false;
            viewModel.ReportFailure(BaiduLoginUiText.CleanupPending);
        }
    }

    private async Task CleanupAfterCanceledInitializationAsync()
    {
        if (!profilePrepared || ownership is null) return;
        await addAccountService
            .RetryProfileCleanupAsync(accountId, ownership, CancellationToken.None)
            .ConfigureAwait(true);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        pollTimer.Stop();
        lifetime.Cancel();
        profileRegistration?.Dispose();
        try { Browser.Dispose(); }
        catch (Exception) { /* The profile closer may already have disposed the browser. */ }
    }
}
