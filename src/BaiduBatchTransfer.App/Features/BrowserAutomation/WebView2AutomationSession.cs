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
    : IBoundVisibleWebAutomationSessionFactory, IReadOnlyTransferRecoverySessionFactory
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

    public Task<IVisibleWebAutomationSession> CreateReadOnlyTransferRecoveryAsync(
        WebAutomationRequestIdentity identity,
        string profilePath,
        ReadOnlyTransferRecoveryBinding binding,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var window = new WebAutomationWindow
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        return Task.FromResult<IVisibleWebAutomationSession>(
            new WebView2AutomationSession(
                identity, profilePath, window, candidateAuthorizationSource,
                candidateBinding: null, transferRecoveryBinding: binding));
    }
}

public sealed class WebView2AutomationSession :
    IVisibleWebAutomationSession,
    IAcceptanceDirectoryPreparationSession,
    IRemoteResourcePickerSession,
    IWebViewDownloadEventSource,
    ITransferAutomationSession,
    ITransferDirectoryPreparationSession,
    IShareResultExportSession
{
    private readonly WebAutomationRequestIdentity identity;
    private readonly string profilePath;
    private readonly WebAutomationWindow? window;
    private readonly WebViewDownloadEventRouter downloadRouter;
    private readonly IBaiduCandidateActionAuthorizationSource? candidateAuthorizationSource;
    private readonly BaiduCandidateSessionBinding? candidateBinding;
    private readonly ReadOnlyTransferRecoveryBinding? transferRecoveryBinding;
    private readonly CancellationToken headlessUserClosed;
    private IBaiduCandidateDomExecutor? candidateExecutor;
    private Uri? currentOfficialUri;
    private RemoteItemIdentitySet? selectedRemoteItems;
    private BaiduTransferCandidateObservation? lastTransferObservation;
    private CoreWebView2? core;
    private int closed;

    internal WebView2AutomationSession(
        WebAutomationRequestIdentity identity,
        string profilePath,
        WebAutomationWindow window,
        IBaiduCandidateActionAuthorizationSource candidateAuthorizationSource,
        BaiduCandidateSessionBinding? candidateBinding = null,
        ReadOnlyTransferRecoveryBinding? transferRecoveryBinding = null)
    {
        this.identity = (identity ?? throw new ArgumentNullException(nameof(identity))).Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        this.profilePath = profilePath;
        this.window = window ?? throw new ArgumentNullException(nameof(window));
        this.candidateAuthorizationSource = candidateAuthorizationSource ??
            throw new ArgumentNullException(nameof(candidateAuthorizationSource));
        this.candidateBinding = candidateBinding;
        this.transferRecoveryBinding = transferRecoveryBinding;
        if (candidateBinding is not null && transferRecoveryBinding is not null)
            throw new ArgumentException("Read-only recovery cannot carry a write binding.");
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

    internal WebView2AutomationSession(
        WebAutomationRequestIdentity identity,
        ReadOnlyTransferRecoveryBinding transferRecoveryBinding,
        IBaiduCandidateDomExecutor candidateExecutor,
        CancellationToken userClosed)
    {
        this.identity = (identity ?? throw new ArgumentNullException(nameof(identity))).Validate();
        this.transferRecoveryBinding = transferRecoveryBinding ??
            throw new ArgumentNullException(nameof(transferRecoveryBinding));
        if (transferRecoveryBinding.OperationId != identity.OperationId ||
            transferRecoveryBinding.BatchId != identity.BatchId ||
            transferRecoveryBinding.AccountId != identity.AccountId ||
            transferRecoveryBinding.Generation != identity.Generation)
            throw new InvalidOperationException("Read-only recovery identity does not match the session.");
        this.candidateExecutor = candidateExecutor ?? throw new ArgumentNullException(nameof(candidateExecutor));
        headlessUserClosed = userClosed;
        profilePath = "[HEADLESS]";
        currentOfficialUri = transferRecoveryBinding.OfficialTargetUri;
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
        if (candidateBinding is not null || transferRecoveryBinding is not null)
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
        if (BaiduNavigationPolicy.Evaluate(destination).IsAllowed)
        {
            currentOfficialUri = destination;
            return;
        }
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

    public async Task<TransferSourceListing> OpenAndReadSourceForSubmissionAsync(
        ShareReference share,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(share);
        await OpenSourceAsync(share, cancellationToken).ConfigureAwait(false);
        if (share.ExtractionCode is not null)
            await ((ITransferDirectoryPreparationSession)this).EnterExtractionCodeAsync(
                share.ExtractionCode, cancellationToken).ConfigureAwait(false);
        return await ReadCompleteSourceAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task EnterExtractionCodeAsync(string extractionCode, CancellationToken cancellationToken) =>
        UnprovenAsync(extractionCode, cancellationToken);

    public Task<TransferDirectoryListing> OpenDestinationAndReadStableListingAsync(
        RemoteDirectory destination,
        CancellationToken cancellationToken) => UnprovenAsync<TransferDirectoryListing>(destination, cancellationToken);

    public async Task<TransferDirectoryListing> OpenExactTargetAndReadStableListingAsync(
        RemoteDirectory destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var exactTargetUri = new Uri(
            $"https://pan.baidu.com/disk/main#/all?path={Uri.EscapeDataString(destination.Path.Value)}");
        ValidateReadOnlyRecovery(destination, exactTargetUri);
        await NavigateOfficialAsync(exactTargetUri, cancellationToken).ConfigureAwait(false);
        var observed = await ObserveTransferAsync(
            BaiduCandidatePageState.OwnDrive,
            [],
            destination.RemoteId,
            destination.Path,
            cancellationToken).ConfigureAwait(false);
        if (observed.AncestorExactIds.IsEmpty ||
            !string.Equals(observed.AncestorExactIds[^1], destination.RemoteId, StringComparison.Ordinal))
            throw NotValidated(BaiduAutomationDenialReason.FieldEvidenceNotValidated);
        ValidateReadOnlyRecovery(destination, exactTargetUri);
        return ToDirectoryListing(observed);
    }

    Task ITransferAutomationSession.PersistBaselineAsync(
        WebAutomationBaseline baseline,
        string observationFingerprint,
        CancellationToken cancellationToken) => FenceAsync(cancellationToken);

    public Task PersistTargetPreparationAsync(
        TransferTargetPreparationEvidence evidence,
        CancellationToken cancellationToken) => FenceAsync(cancellationToken);

    public Task ReturnToExternalShareAsync(ShareReference share, CancellationToken cancellationToken) =>
        UnprovenAsync(share, cancellationToken);

    Task ITransferAutomationSession.MarkSubmittingAsync(CancellationToken cancellationToken) =>
        FenceAsync(cancellationToken);

    public Task MarkSubmissionResultPendingAsync(CancellationToken cancellationToken) =>
        FenceAsync(cancellationToken);

    public Task MarkSubmissionDefinitelyNotAcceptedAsync(CancellationToken cancellationToken) =>
        FenceAsync(cancellationToken);

    public Task<TransferOfficialAcceptance> SaveWholeShareAsync(
        RemoteDirectory destination,
        DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken) => UnprovenAsync<TransferOfficialAcceptance>(destination, cancellationToken);

    public async Task PrepareWholeShareSubmissionAsync(
        RemoteDirectory destination,
        TransferSourceEvidence source,
        DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);
        if (duplicatePolicy != DuplicatePolicy.Skip)
            throw NotValidated(BaiduAutomationDenialReason.FieldEvidenceNotValidated);
        var sourceObservation = lastTransferObservation is
            { PageState: BaiduCandidatePageState.ExternalShare } observed
            ? observed
            : throw NotValidated(BaiduAutomationDenialReason.ObservationNotValidated);
        if (!sourceObservation.Items.Select(item => item.ExactId)
                .SequenceEqual(source.Items.Items.Select(item => item.ExactId), StringComparer.Ordinal))
            throw NotValidated(BaiduAutomationDenialReason.ObservationNotValidated);
        await MutateTransferAsync(
            BaiduAutomationAction.SelectExactItems, sourceObservation, null, null,
            cancellationToken).ConfigureAwait(false);
        var current = await OpenDestinationPickerAsync(cancellationToken).ConfigureAwait(false);
        if (current.Directory?.Path.Value != "/")
            current = await OpenVerifiedRootAsync(cancellationToken).ConfigureAwait(false);
        var segments = destination.Path.Value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var matches = current.DetailedItems.Where(item =>
                item.Kind == TransferRemoteItemKind.Folder &&
                string.Equals(item.ExactServerName, segment, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                throw NotValidated(BaiduAutomationDenialReason.FieldEvidenceNotValidated);
            var next = matches[0];
            var nextPath = new RemotePath(
                current.Directory!.Path.Value == "/"
                    ? $"/{segment}"
                    : $"{current.Directory.Path.Value}/{segment}");
            current = await EnterDirectoryAsync(
                new RemoteDirectory(next.Identity.ExactId, nextPath), cancellationToken)
                .ConfigureAwait(false);
        }
        if (current.Directory != destination)
            throw NotValidated(BaiduAutomationDenialReason.FieldEvidenceNotValidated);
    }

    public async Task<TransferOfficialAcceptance> SaveWholeShareExactlyOnceAsync(
        RemoteDirectory destination,
        TransferSourceEvidence source,
        DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);
        if (duplicatePolicy != DuplicatePolicy.Skip)
            throw NotValidated(BaiduAutomationDenialReason.FieldEvidenceNotValidated);
        var targetObservation = RequireLastDirectoryObservation();
        if (targetObservation.ParentExactId != destination.RemoteId ||
            targetObservation.CanonicalPath != destination.Path)
            throw NotValidated(BaiduAutomationDenialReason.ObservationNotValidated);
        await MutateTransferAsync(
            BaiduAutomationAction.ClickConfirmTransfer, targetObservation, null, null,
            cancellationToken).ConfigureAwait(false);
        var result = await ObserveTransferAsync(
            BaiduCandidatePageState.TransferResult, [], destination.RemoteId,
            destination.Path, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.OfficialRequestId) ||
            result.OfficialIdentityMapping is null ||
            !result.OfficialIdentityMapping.Covers(source.Items))
            return new TransferOfficialAcceptance(
                TransferOfficialAcceptanceStatus.Unobserved,
                null, null, null, result.ObservationToken, destination.RemoteId);
        var remoteItems = new RemoteItemIdentitySet(result.OfficialIdentityMapping.Entries.Select(entry =>
            new RemoteItemIdentity(entry.DestinationExactId, null)));
        return new TransferOfficialAcceptance(
            TransferOfficialAcceptanceStatus.Accepted,
            result.OfficialRequestId,
            remoteItems,
            result.OfficialIdentityMapping,
            result.ObservationToken,
            destination.RemoteId);
    }

    public Task PersistOfficialAcceptanceAsync(
        TransferOfficialAcceptance acceptance,
        CancellationToken cancellationToken) => FenceAsync(cancellationToken);

    public Task<TransferDirectoryListing> ReopenDestinationAndReadStableListingAsync(
        RemoteDirectory destination,
        CancellationToken cancellationToken) =>
        OpenExactTargetAndReadStableListingAsync(destination, cancellationToken);

    public Task OpenSourceAsync(ShareReference share, CancellationToken cancellationToken) =>
        NavigateOfficialAsync(new Uri(share.OriginalUrl), cancellationToken);

    public async Task<TransferSourceListing> ReadCompleteSourceAsync(CancellationToken cancellationToken)
    {
        var observation = await ObserveTransferAsync(
            BaiduCandidatePageState.ExternalShare, [], null, null, cancellationToken).ConfigureAwait(false);
        var capability = candidateBinding?.Binding.Capability ??
            throw NotValidated(BaiduAutomationDenialReason.AcceptanceMissing);
        var items = new RemoteItemIdentitySet(observation.Items.Select(item =>
            new RemoteItemIdentity(item.ExactId, item.ServerName)));
        return new TransferSourceListing(new TransferSourceEvidence(
            items, observation.TotalCount, observation.ReachedEnd, observation.IsStable,
            observation.GenerationToken, observation.BaiduUserId, capability,
            observation.ObservationToken));
    }

    async Task ITransferDirectoryPreparationSession.EnterExtractionCodeAsync(
        string extractionCode,
        CancellationToken cancellationToken)
    {
        var observed = await ObserveTransferAsync(
            BaiduCandidatePageState.ExtractionCode, [], null, null, cancellationToken).ConfigureAwait(false);
        await MutateTransferAsync(
            BaiduAutomationAction.SetExtractionCode, observed, extractionCode, null,
            cancellationToken).ConfigureAwait(false);
        observed = await ObserveTransferAsync(
            BaiduCandidatePageState.ExtractionCode, [], null, null, cancellationToken).ConfigureAwait(false);
        await MutateTransferAsync(
            BaiduAutomationAction.SubmitExtraction, observed, null, null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TransferDirectoryListing> OpenDestinationPickerAsync(
        CancellationToken cancellationToken)
    {
        var source = await ObserveTransferAsync(
            BaiduCandidatePageState.ExternalShare, [], null, null, cancellationToken).ConfigureAwait(false);
        await MutateTransferAsync(
            BaiduAutomationAction.OpenDestinationPicker, source, null, null,
            cancellationToken).ConfigureAwait(false);
        return ToDirectoryListing(await ObserveTransferAsync(
            BaiduCandidatePageState.DestinationPicker, [], null, null, cancellationToken)
            .ConfigureAwait(false));
    }

    public async Task<TransferDirectoryListing> OpenVerifiedRootAsync(CancellationToken cancellationToken)
    {
        var current = RequireLastDirectoryObservation();
        if (current.CanonicalPath?.Value == "/") return ToDirectoryListing(current);
        if (current.AncestorExactIds.IsEmpty)
            throw NotValidated(BaiduAutomationDenialReason.FieldEvidenceNotValidated);
        var rootExactId = current.AncestorExactIds[0];
        await MutateTransferAsync(
            BaiduAutomationAction.EnterDirectory, current, null, rootExactId,
            cancellationToken).ConfigureAwait(false);
        return ToDirectoryListing(await ObserveTransferAsync(
            BaiduCandidatePageState.DestinationPicker, [], rootExactId,
            new RemotePath("/"), cancellationToken).ConfigureAwait(false));
    }

    public async Task<TransferDirectoryListing> EnterDirectoryAsync(
        RemoteDirectory directory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var current = RequireLastDirectoryObservation();
        await MutateTransferAsync(
            BaiduAutomationAction.EnterDirectory, current, null, directory.RemoteId,
            cancellationToken).ConfigureAwait(false);
        return ToDirectoryListing(await ObserveTransferAsync(
            BaiduCandidatePageState.DestinationPicker, [], directory.RemoteId,
            directory.Path, cancellationToken).ConfigureAwait(false));
    }

    public Task BeginCreateDirectoryAsync(string segment, CancellationToken cancellationToken) =>
        MutateTransferAsync(
            BaiduAutomationAction.CreateDirectory, RequireLastDirectoryObservation(), segment,
            null, cancellationToken);

    public async Task ConfirmDirectoryCreationAsync(CancellationToken cancellationToken)
    {
        var observed = await ReobserveLastDirectoryAsync(cancellationToken).ConfigureAwait(false);
        await MutateTransferAsync(
            BaiduAutomationAction.ConfirmDirectoryCreation, observed, null, null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TransferDirectoryListing> ReadCurrentDirectoryAsync(
        CancellationToken cancellationToken) =>
        ToDirectoryListing(await ReobserveLastDirectoryAsync(cancellationToken).ConfigureAwait(false));

    public async Task CancelDestinationPickerAsync(CancellationToken cancellationToken)
    {
        var observed = await ReobserveLastDirectoryAsync(cancellationToken).ConfigureAwait(false);
        await MutateTransferAsync(
            BaiduAutomationAction.CancelDestinationPicker, observed, null, null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TransferDirectoryListing> ReadExactParentForRecoveryAsync(
        RemoteDirectory parent,
        IReadOnlyList<string> expectedAncestors,
        TransferDirectoryRecoveryAuthorization authorization,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(expectedAncestors);
        ArgumentNullException.ThrowIfNull(authorization);
        if (authorization.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
            authorization.OperationId != identity.OperationId ||
            authorization.BatchId != identity.BatchId ||
            authorization.AccountId != identity.AccountId ||
            authorization.ParentExactId != parent.RemoteId ||
            authorization.ParentPath != parent.Path ||
            candidateBinding is null ||
            candidateBinding.ExpectedBaiduUserId != authorization.BaiduUserId ||
            candidateBinding.Binding.Capability != authorization.Capability)
            throw NotValidated(BaiduAutomationDenialReason.AcceptanceMissing);
        var exactTargetUri = new Uri(
            $"https://pan.baidu.com/disk/main#/all?path={Uri.EscapeDataString(parent.Path.Value)}");
        await NavigateOfficialAsync(exactTargetUri, cancellationToken)
            .ConfigureAwait(false);
        var observed = await ObserveTransferAsync(
            BaiduCandidatePageState.OwnDrive, [], parent.RemoteId, parent.Path, cancellationToken)
            .ConfigureAwait(false);
        if (expectedAncestors.Count > 0 && !observed.AncestorExactIds.SequenceEqual(
                expectedAncestors, StringComparer.Ordinal))
            throw NotValidated(BaiduAutomationDenialReason.FieldEvidenceNotValidated);
        return ToDirectoryListing(observed);
    }

    private Task NavigateOfficialAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!BaiduNavigationPolicy.Evaluate(uri).IsAllowed)
            throw NotValidated(BaiduAutomationDenialReason.OfficialUriNotAllowed);
        currentOfficialUri = uri;
        lastTransferObservation = null;
        if (core is not null)
            core.Navigate(uri.AbsoluteUri);
        return Task.CompletedTask;
    }

    private async Task<BaiduTransferCandidateObservation> ObserveTransferAsync(
        BaiduCandidatePageState pageState,
        IEnumerable<string> exactIds,
        string? parentExactId,
        RemotePath? path,
        CancellationToken cancellationToken)
    {
        using var fence = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, UserClosed);
        fence.Token.ThrowIfCancellationRequested();
        var uid = candidateBinding?.ExpectedBaiduUserId ?? transferRecoveryBinding?.BaiduUserId;
        if (uid is not { Length: > 0 } ||
            candidateExecutor is not IBaiduTransferCandidateDomExecutor executor ||
            currentOfficialUri is null)
            throw NotValidated(BaiduAutomationDenialReason.AcceptanceMissing);
        var request = new BaiduCandidateDomReadRequest(
            currentOfficialUri, pageState, uid, exactIds, parentExactId, path);
        var result = await executor.ObserveTransferAsync(request, fence.Token).ConfigureAwait(false);
        if (!result.IsSuccess || result.Observation is null) throw NotValidated(result.DenialReason);
        lastTransferObservation = result.Observation;
        return result.Observation;
    }

    private async Task MutateTransferAsync(
        BaiduAutomationAction action,
        BaiduTransferCandidateObservation observation,
        string? typedValue,
        string? targetExactId,
        CancellationToken cancellationToken)
    {
        using var fence = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, UserClosed);
        fence.Token.ThrowIfCancellationRequested();
        if (candidateBinding is null || candidateExecutor is not IBaiduTransferCandidateDomExecutor executor ||
            currentOfficialUri is null)
            throw NotValidated(BaiduAutomationDenialReason.AcceptanceMissing);
        var read = new BaiduCandidateDomReadRequest(
            currentOfficialUri, observation.PageState, observation.BaiduUserId,
            observation.Items.Select(item => item.ExactId), observation.ParentExactId,
            observation.CanonicalPath);
        var request = new BaiduCandidateDomMutationRequest(
            candidateBinding.Authorization, candidateBinding.Binding, action, read, typedValue,
            targetExactId);
        var result = await executor.MutateTransferAsync(request, observation, fence.Token)
            .ConfigureAwait(false);
        if (!result.IsSuccess) throw NotValidated(result.DenialReason);
    }

    private BaiduTransferCandidateObservation RequireLastDirectoryObservation() =>
        lastTransferObservation is
            { PageState: BaiduCandidatePageState.DestinationPicker, ParentExactId: not null,
              CanonicalPath: not null } observation
            ? observation
            : throw NotValidated(BaiduAutomationDenialReason.ObservationNotValidated);

    private Task<BaiduTransferCandidateObservation> ReobserveLastDirectoryAsync(
        CancellationToken cancellationToken)
    {
        var previous = RequireLastDirectoryObservation();
        return ObserveTransferAsync(
            BaiduCandidatePageState.DestinationPicker,
            previous.Items.Select(item => item.ExactId), previous.ParentExactId,
            previous.CanonicalPath, cancellationToken);
    }

    private static TransferDirectoryListing ToDirectoryListing(
        BaiduTransferCandidateObservation observation)
    {
        if (observation.ParentExactId is null || observation.CanonicalPath is null)
            throw NotValidated(BaiduAutomationDenialReason.FieldEvidenceNotValidated);
        return TransferDirectoryListing.Exact(
            new RemoteDirectory(observation.ParentExactId, observation.CanonicalPath),
            observation.AncestorExactIds,
            observation.Items.Select(item => new TransferRemoteItem(
                new RemoteItemIdentity(item.ExactId, item.ServerName),
                item.Kind == RemoteResourceKind.Folder
                    ? TransferRemoteItemKind.Folder
                    : TransferRemoteItemKind.File,
                item.ServerName)).ToArray(),
            observation.TotalCount, observation.ReachedEnd, observation.IsStable,
            observation.GenerationToken, observation.BaiduUserId, observation.ObservationToken);
    }

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

    private void ValidateReadOnlyRecovery(RemoteDirectory destination, Uri officialTargetUri)
    {
        if (transferRecoveryBinding is null) return;
        if (candidateBinding is not null || transferRecoveryBinding.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
            transferRecoveryBinding.OperationId != identity.OperationId ||
            transferRecoveryBinding.BatchId != identity.BatchId ||
            transferRecoveryBinding.AccountId != identity.AccountId ||
            transferRecoveryBinding.Generation != identity.Generation ||
            transferRecoveryBinding.Target != destination ||
            transferRecoveryBinding.OfficialTargetUri != officialTargetUri ||
            currentOfficialUri is not null && currentOfficialUri != officialTargetUri)
            throw NotValidated(BaiduAutomationDenialReason.AcceptanceMissing);
    }

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
