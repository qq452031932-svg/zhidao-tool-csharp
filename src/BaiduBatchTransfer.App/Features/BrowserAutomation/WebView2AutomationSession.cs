using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using Microsoft.Web.WebView2.Core;
using System.IO;
using System.Windows.Threading;
using System.Security.Cryptography;
using System.Text;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.App.Features.BrowserAutomation;

public sealed class WebView2AutomationSessionFactory(
    IBaiduCandidateActionAuthorizationSource candidateAuthorizationSource)
    : IBoundVisibleWebAutomationSessionFactory
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
            new WebView2AutomationSession(identity, profilePath, window, candidateAuthorizationSource));
    }

    public Task<IVisibleWebAutomationSession> CreateBoundAsync(
        WebAutomationRequestIdentity identity,
        string profilePath,
        BaiduCandidateSessionBinding binding,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        binding.ValidateIdentity(identity);
        var window = new WebAutomationWindow
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        return Task.FromResult<IVisibleWebAutomationSession>(
            new WebView2AutomationSession(
                identity, profilePath, window, candidateAuthorizationSource, binding));
    }
}

public sealed class WebView2AutomationSession :
    IVisibleWebAutomationSession,
    IAcceptanceDirectoryPreparationSession,
    IRemoteResourcePickerSession,
    IWebViewDownloadEventSource,
    ITransferAutomationSession,
    IShareResultExportSession
{
    private readonly WebAutomationRequestIdentity identity;
    private readonly string profilePath;
    private readonly WebAutomationWindow? window;
    private readonly WebViewDownloadEventRouter downloadRouter;
    private readonly IBaiduCandidateActionAuthorizationSource? candidateAuthorizationSource;
    private readonly BaiduCandidateSessionBinding? candidateBinding;
    private readonly CancellationToken headlessUserClosed;
    private IBaiduCandidateDomExecutor? candidateExecutor;
    private Uri? currentOfficialUri;
    private RemoteItemIdentitySet? selectedRemoteItems;
    private CoreWebView2? core;
    private int closed;

    internal WebView2AutomationSession(
        WebAutomationRequestIdentity identity,
        string profilePath,
        WebAutomationWindow window,
        IBaiduCandidateActionAuthorizationSource candidateAuthorizationSource,
        BaiduCandidateSessionBinding? candidateBinding = null)
    {
        this.identity = (identity ?? throw new ArgumentNullException(nameof(identity))).Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        this.profilePath = profilePath;
        this.window = window ?? throw new ArgumentNullException(nameof(window));
        this.candidateAuthorizationSource = candidateAuthorizationSource ??
            throw new ArgumentNullException(nameof(candidateAuthorizationSource));
        this.candidateBinding = candidateBinding;
        ValidateCandidateIdentity(candidateBinding, this.identity);
        downloadRouter = new WebViewDownloadEventRouter(this.identity);
    }

    internal WebView2AutomationSession(
        WebAutomationRequestIdentity identity,
        BaiduCandidateSessionBinding candidateBinding,
        IBaiduCandidateDomExecutor candidateExecutor,
        CancellationToken userClosed)
    {
        this.identity = (identity ?? throw new ArgumentNullException(nameof(identity))).Validate();
        this.candidateBinding = candidateBinding ?? throw new ArgumentNullException(nameof(candidateBinding));
        this.candidateExecutor = candidateExecutor ?? throw new ArgumentNullException(nameof(candidateExecutor));
        headlessUserClosed = userClosed;
        profilePath = "[HEADLESS]";
        currentOfficialUri = new Uri("https://pan.baidu.com/disk/main");
        ValidateCandidateIdentity(candidateBinding, this.identity);
        downloadRouter = new WebViewDownloadEventRouter(this.identity);
    }

    public CancellationToken UserClosed => window?.UserClosed ?? headlessUserClosed;
    public WebAutomationRequestIdentity Identity => identity;

    public IDisposable Subscribe(Action<IWebViewDownloadOperation> onDownloadStarting) =>
        downloadRouter.Subscribe(onDownloadStarting);

    public async Task ShowAsync(Uri initialUri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref closed) != 0) throw new ObjectDisposedException(nameof(WebView2AutomationSession));
        var decision = BaiduNavigationPolicy.Evaluate(initialUri);
        if (!decision.IsAllowed) throw new InvalidOperationException(decision.SafeMessage);
        if (window is null)
        {
            currentOfficialUri = initialUri;
            return;
        }

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
        currentOfficialUri = initialUri;
        if (candidateBinding is not null)
            candidateExecutor = new WebView2CandidateDomExecutor(
                new CoreWebView2CandidateDomScriptHost(core), candidateAuthorizationSource!);
        core.Navigate(initialUri.AbsoluteUri);
    }

    public Task ShowHandoffAsync(
        WebAutomationHandoffPrompt prompt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (window is null) throw new InvalidOperationException("A headless test session cannot show handoff UI.");
        if (prompt.Identity != identity)
            throw new InvalidOperationException("人工接管请求不属于当前窗口。");
        window.ShowHandoff(prompt);
        window.Activate();
        return Task.CompletedTask;
    }

    public Task<WebAutomationUserChoice> WaitForHandoffChoiceAsync(
        CancellationToken cancellationToken = default) =>
        window is null
            ? Task.FromException<WebAutomationUserChoice>(
                new InvalidOperationException("A headless test session has no handoff UI."))
            : window.WaitForHandoffChoiceAsync(cancellationToken);

    public Task<RemoteResourcePickerDecision> WaitForApplicationDecisionAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (window is null)
            return Task.FromException<RemoteResourcePickerDecision>(
                new InvalidOperationException("A headless test session has no resource picker UI."));
        return window.Dispatcher.InvokeAsync(() =>
        {
            window.ShowResourcePickerActions();
            return window.WaitForResourcePickerDecisionAsync(cancellationToken);
        }).Task.Unwrap();
    }

    public async Task<AcceptanceDirectoryPreparationEvidence?> ConfirmDirectoryPreparedAsync(
        WebAutomationRequestIdentity requestIdentity,
        RemotePath requestedPath,
        CancellationToken cancellationToken = default)
    {
        if (requestIdentity != identity)
            throw new InvalidOperationException("目录确认请求不属于当前窗口。");
        if (window is null) throw new InvalidOperationException("A headless test session has no directory UI.");
        var confirmed = await window.Dispatcher.InvokeAsync(() =>
        {
            window.ShowDirectoryPreparationActions(requestedPath);
            return window.WaitForDirectoryPreparationDecisionAsync(cancellationToken);
        }).Task.Unwrap().ConfigureAwait(true);
        if (!confirmed) return null;
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"manual-directory|{identity.OperationId:D}|{identity.Generation}|{requestedPath.Value}")));
        return new(new RemoteDirectory(requestedPath.Value, requestedPath), fingerprint);
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
        if (window is not null)
        {
            if (!window.IsClosed) window.CloseProgrammatically();
            window.WebView.Dispose();
        }
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

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Handled = true;
        var operation = new CoreWebViewDownloadOperation(e, window!.Dispatcher);
        try { downloadRouter.Route(operation); }
        catch { operation.Cancel(); }
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

    public async Task SelectExactRemoteItemsAsSingleGroupAsync(
        RemoteItemIdentitySet remoteItems,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remoteItems);
        await ExecuteMutationAsync(
            BaiduAutomationAction.SelectExactItems,
            BaiduCandidatePageState.OwnDrive,
            remoteItems.Items.Select(item => item.ExactId),
            cancellationToken).ConfigureAwait(false);
        selectedRemoteItems = remoteItems;
    }

    public Task ConfigurePermanentShareAsync(
        PermanentShareRequest request,
        CancellationToken cancellationToken) =>
        UnprovenAsync(request, cancellationToken);

    public Task<ShareOfficialListing> ReadCompleteStableOfficialShareListAsync(
        CancellationToken cancellationToken) => UnprovenAsync<ShareOfficialListing>(null, cancellationToken);

    Task IShareAutomationSession.PersistBaselineAsync(
        WebAutomationBaseline baseline,
        string observationFingerprint,
        CancellationToken cancellationToken) => FenceAsync(cancellationToken);

    Task IShareAutomationSession.MarkSubmittingAsync(CancellationToken cancellationToken) =>
        FenceAsync(cancellationToken);

    public Task<ShareOfficialAcceptance> CreateShareOnceAsync(CancellationToken cancellationToken) =>
        UnprovenAsync<ShareOfficialAcceptance>(null, cancellationToken);

    public Task PersistOfficialAcceptanceAsync(
        ShareOfficialAcceptance acceptance,
        CancellationToken cancellationToken) => FenceAsync(cancellationToken);

    public async Task ExportOfficialResultAsync(CancellationToken cancellationToken)
    {
        if (selectedRemoteItems is null)
        {
            await UnprovenAsync<object>(null, cancellationToken).ConfigureAwait(false);
            return;
        }
        await ExecuteMutationAsync(
            BaiduAutomationAction.ExportResult,
            BaiduCandidatePageState.ShareResult,
            selectedRemoteItems.Items.Select(item => item.ExactId),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<TransferSourceListing> OpenExternalShareAsync(
        ShareReference share,
        CancellationToken cancellationToken) => UnprovenAsync<TransferSourceListing>(share, cancellationToken);

    public Task EnterExtractionCodeAsync(string extractionCode, CancellationToken cancellationToken) =>
        UnprovenAsync(extractionCode, cancellationToken);

    public Task<TransferDirectoryListing> OpenDestinationAndReadStableListingAsync(
        RemoteDirectory destination,
        CancellationToken cancellationToken) => UnprovenAsync<TransferDirectoryListing>(destination, cancellationToken);

    Task ITransferAutomationSession.PersistBaselineAsync(
        WebAutomationBaseline baseline,
        string observationFingerprint,
        CancellationToken cancellationToken) => FenceAsync(cancellationToken);

    public Task ReturnToExternalShareAsync(ShareReference share, CancellationToken cancellationToken) =>
        UnprovenAsync(share, cancellationToken);

    Task ITransferAutomationSession.MarkSubmittingAsync(CancellationToken cancellationToken) =>
        FenceAsync(cancellationToken);

    public Task<TransferOfficialAcceptance> SaveWholeShareAsync(
        RemoteDirectory destination,
        DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken) => UnprovenAsync<TransferOfficialAcceptance>(destination, cancellationToken);

    public Task PersistOfficialAcceptanceAsync(
        TransferOfficialAcceptance acceptance,
        CancellationToken cancellationToken) => FenceAsync(cancellationToken);

    public Task<TransferDirectoryListing> ReopenDestinationAndReadStableListingAsync(
        RemoteDirectory destination,
        CancellationToken cancellationToken) => UnprovenAsync<TransferDirectoryListing>(destination, cancellationToken);

    private async Task ExecuteMutationAsync(
        BaiduAutomationAction action,
        BaiduCandidatePageState pageState,
        IEnumerable<string> exactIds,
        CancellationToken cancellationToken)
    {
        using var fence = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, UserClosed);
        fence.Token.ThrowIfCancellationRequested();
        if (candidateBinding is null || candidateExecutor is null || currentOfficialUri is null)
            throw NotValidated(BaiduAutomationDenialReason.AcceptanceMissing);
        var request = new BaiduCandidateDomRequest(
            candidateBinding.Authorization,
            candidateBinding.Binding,
            action,
            currentOfficialUri,
            pageState,
            exactIds);
        var observed = await candidateExecutor.ObserveAsync(request, fence.Token).ConfigureAwait(false);
        if (!observed.IsSuccess || observed.Observation is null) throw NotValidated(observed.DenialReason);
        fence.Token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref closed) != 0) throw new ObjectDisposedException(nameof(WebView2AutomationSession));
        var mutated = await candidateExecutor.MutateAsync(
            request, observed.Observation, fence.Token).ConfigureAwait(false);
        if (!mutated.IsSuccess) throw NotValidated(mutated.DenialReason);
    }

    private async Task FenceAsync(CancellationToken cancellationToken)
    {
        using var fence = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, UserClosed);
        fence.Token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref closed) != 0) throw new ObjectDisposedException(nameof(WebView2AutomationSession));
        await Task.CompletedTask;
    }

    private async Task UnprovenAsync(object? value, CancellationToken cancellationToken)
    {
        await FenceAsync(cancellationToken).ConfigureAwait(false);
        throw NotValidated(BaiduAutomationDenialReason.FieldEvidenceNotValidated);
    }

    private async Task<T> UnprovenAsync<T>(object? value, CancellationToken cancellationToken)
    {
        await FenceAsync(cancellationToken).ConfigureAwait(false);
        throw NotValidated(BaiduAutomationDenialReason.FieldEvidenceNotValidated);
    }

    private static WebAutomationAuthorizationException NotValidated(BaiduAutomationDenialReason reason) =>
        new(reason, "Candidate WebView2 field protocol is not validated for this typed operation.");

    private static void ValidateCandidateIdentity(
        BaiduCandidateSessionBinding? candidate,
        WebAutomationRequestIdentity identity)
    {
        candidate?.ValidateIdentity(identity);
    }

    private sealed class CoreWebViewDownloadOperation : IWebViewDownloadOperation
    {
        private readonly CoreWebView2DownloadStartingEventArgs starting;
        private readonly CoreWebView2DownloadOperation operation;
        private readonly Dispatcher dispatcher;
        private readonly TaskCompletionSource<WebViewDownloadCompletionState> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int terminal;

        public CoreWebViewDownloadOperation(
            CoreWebView2DownloadStartingEventArgs starting,
            Dispatcher dispatcher)
        {
            this.starting = starting ?? throw new ArgumentNullException(nameof(starting));
            operation = starting.DownloadOperation;
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            operation.StateChanged += OnStateChanged;
            ObserveState();
        }

        public string SuggestedFileName => Path.GetFileName(starting.ResultFilePath);

        public void SetDestinationPath(string path)
        {
            if (!dispatcher.CheckAccess())
                throw new InvalidOperationException("下载路径必须在浏览器线程设置。");
            starting.ResultFilePath = path;
        }

        public Task<WebViewDownloadCompletionState> WaitForCompletionAsync(CancellationToken cancellationToken) =>
            completion.Task.WaitAsync(cancellationToken);

        public void Cancel()
        {
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            if (dispatcher.CheckAccess()) CancelCore();
            else dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(CancelCore));
        }

        private void CancelCore()
        {
            try { operation.Cancel(); }
            catch (InvalidOperationException) { }
        }

        private void OnStateChanged(object? sender, object e) => ObserveState();

        private void ObserveState()
        {
            var state = operation.State switch
            {
                CoreWebView2DownloadState.Completed => WebViewDownloadCompletionState.Completed,
                CoreWebView2DownloadState.Interrupted => WebViewDownloadCompletionState.Interrupted,
                _ => (WebViewDownloadCompletionState?)null,
            };
            if (state is null || Interlocked.Exchange(ref terminal, 1) != 0) return;
            operation.StateChanged -= OnStateChanged;
            completion.TrySetResult(state.Value);
        }
    }
}
