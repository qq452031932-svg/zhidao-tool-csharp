using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.App.Services;

namespace BaiduBatchTransfer.App.Features.BrowserAutomation;

public enum VisibleWebAutomationDisposition
{
    ActionCompleted,
    Skipped,
    BatchCancelled,
}

public enum WebAutomationHandoffReason
{
    LoginExpired,
    Verification,
    Captcha,
    RiskControl,
    UnknownPage,
}

public enum WebAutomationUserChoice
{
    Resume,
    Skip,
    CancelBatch,
    WindowClosed,
}

public sealed record WebAutomationRequestIdentity(
    Guid OperationId,
    Guid BatchId,
    Guid AccountId,
    int Generation,
    WebAutomationOwnerKind OwnerKind)
{
    public WebAutomationRequestIdentity Validate()
    {
        if (OperationId == Guid.Empty) throw new ArgumentException("An operation is required.", nameof(OperationId));
        if (BatchId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(BatchId));
        if (AccountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(AccountId));
        if (Generation < 1) throw new ArgumentOutOfRangeException(nameof(Generation));
        if (!Enum.IsDefined(OwnerKind)) throw new ArgumentOutOfRangeException(nameof(OwnerKind));
        return this;
    }
}

public sealed record VisibleWebAutomationRequest<T>
{
    public VisibleWebAutomationRequest(
        WebAutomationRequestIdentity identity,
        string profilePath,
        BaiduNavigationAuthorizationRequest navigation,
        Func<IVisibleWebAutomationSession, VisibleWebAutomationContext, CancellationToken, Task<T>> action,
        BaiduCandidateSessionBinding? candidateBinding = null)
    {
        Identity = (identity ?? throw new ArgumentNullException(nameof(identity))).Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        ProfilePath = profilePath;
        Navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        Action = action ?? throw new ArgumentNullException(nameof(action));
        CandidateBinding = candidateBinding;
    }

    public WebAutomationRequestIdentity Identity { get; init; }
    public string ProfilePath { get; init; }
    public BaiduNavigationAuthorizationRequest Navigation { get; init; }
    public Func<IVisibleWebAutomationSession, VisibleWebAutomationContext, CancellationToken, Task<T>> Action { get; init; }
    public BaiduCandidateSessionBinding? CandidateBinding { get; init; }

    public override string ToString() =>
        $"VisibleWebAutomationRequest {{ Identity = {Identity}, ProfilePath = [REDACTED], Navigation = [REDACTED], Action = [REDACTED] }}";
}

public sealed record BaiduCandidateSessionBinding(
    AdapterWriteAuthorizationRequest Authorization,
    AdapterWriteAuthorizationBinding Binding)
{
    public void Validate(
        WebAutomationRequestIdentity identity,
        BaiduNavigationAuthorizationRequest navigation)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ValidateIdentity(identity);
        var scope = navigation.AcceptanceScope;
        if (scope is null ||
            scope.AdapterKind != Binding.Capability.AdapterKind ||
            scope.AccountId != Binding.AccountId || scope.OperationId != Binding.OperationId ||
            scope.Generation != Binding.Generation || scope.BatchId != Binding.BatchId ||
            navigation.ProtocolVersion != Binding.Capability.ProtocolVersion ||
            navigation.SelectorVersion != Binding.Capability.SelectorVersion ||
            navigation.ApplicationBuildVersion != Binding.Capability.BuildVersion)
            throw new InvalidOperationException("Candidate session binding does not match its exact operation and capability.");
    }

    public void ValidateIdentity(WebAutomationRequestIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(Authorization);
        ArgumentNullException.ThrowIfNull(Binding);
        var ownerMatches = Binding.Capability.AdapterKind switch
        {
            "BaiduTransfer" => identity.OwnerKind == WebAutomationOwnerKind.Transfer,
            "BaiduShare" => identity.OwnerKind is WebAutomationOwnerKind.TransferShare or
                WebAutomationOwnerKind.StandaloneShare,
            _ => false,
        };
        var bootstrapActionMatches = Binding.Capability.AdapterKind switch
        {
            "BaiduTransfer" => Authorization.Action == AdapterWriteAction.EnsureDirectory,
            "BaiduShare" => Authorization.Action == AdapterWriteAction.PrepareShare,
            _ => false,
        };
        if (!ownerMatches ||
            !bootstrapActionMatches ||
            identity.AccountId != Authorization.AccountId ||
            identity.OperationId != Authorization.OperationId ||
            identity.Generation != Authorization.Generation ||
            identity.BatchId != Authorization.BatchId ||
            Authorization.Capability != Binding.Capability ||
            Authorization.AccountId != Binding.AccountId ||
            Authorization.OperationId != Binding.OperationId ||
            Authorization.Generation != Binding.Generation ||
            Authorization.BatchId != Binding.BatchId ||
            Authorization.IsRecovery != Binding.IsRecovery)
            throw new InvalidOperationException("Candidate session binding does not match its exact operation identity.");
    }

    public override string ToString() =>
        "BaiduCandidateSessionBinding { Identity = [REDACTED], Versions = [REDACTED], Source = [REDACTED] }";
}

public sealed record VisibleWebAutomationResult<T>(
    VisibleWebAutomationDisposition Disposition,
    T? Value,
    WebAutomationAttemptState PersistentState);

public interface IVisibleWebAutomationBroker
{
    Task<VisibleWebAutomationResult<T>> EnqueueAsync<T>(
        VisibleWebAutomationRequest<T> request,
        CancellationToken cancellationToken = default);
}

public sealed record WebAutomationHandoffPrompt(
    WebAutomationRequestIdentity Identity,
    WebAutomationHandoffReason Reason,
    string SafeMessage);

public interface IVisibleWebAutomationSession : IAsyncDisposable
{
    CancellationToken UserClosed { get; }

    Task ShowAsync(Uri initialUri, CancellationToken cancellationToken = default);

    Task ShowHandoffAsync(
        WebAutomationHandoffPrompt prompt,
        CancellationToken cancellationToken = default);

    Task<WebAutomationUserChoice> WaitForHandoffChoiceAsync(
        CancellationToken cancellationToken = default);

    Task CloseAsync(CancellationToken cancellationToken = default);
}

public sealed record AcceptanceDirectoryPreparationEvidence(
    RemoteDirectory Directory,
    string ObservationFingerprint);

public interface IAcceptanceDirectoryPreparationSession
{
    Task<AcceptanceDirectoryPreparationEvidence?> ConfirmDirectoryPreparedAsync(
        WebAutomationRequestIdentity identity,
        RemotePath requestedPath,
        CancellationToken cancellationToken = default);
}

public interface IVisibleWebAutomationSessionFactory
{
    Task<IVisibleWebAutomationSession> CreateAsync(
        WebAutomationRequestIdentity identity,
        string profilePath,
        CancellationToken cancellationToken = default);
}

public interface IBoundVisibleWebAutomationSessionFactory : IVisibleWebAutomationSessionFactory
{
    Task<IVisibleWebAutomationSession> CreateBoundAsync(
        WebAutomationRequestIdentity identity,
        string profilePath,
        BaiduCandidateSessionBinding binding,
        CancellationToken cancellationToken = default);
}

public interface IWebAutomationUiDispatcher
{
    Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default);
    Task InvokeAsync(Func<Task> action, CancellationToken cancellationToken = default);
}

public interface IWebAutomationNavigationAuthorizer
{
    BaiduAutomationDecision Authorize(BaiduNavigationAuthorizationRequest request);
}

public interface IAcceptanceNavigationAuthorizationSource
{
    bool IsAuthorized(BaiduNavigationAuthorizationRequest request);
}

public interface IWebAutomationBatchCancellation
{
    Task<WebAutomationBatchCancellationResult> CancelAsync(
        WebAutomationRequestIdentity identity,
        long expectedAttemptRevision,
        WebAutomationAttemptState expectedResumeStage,
        CancellationToken cancellationToken = default);

    Task<bool> IsCancelledAsync(
        Guid batchId,
        WebAutomationOwnerKind ownerKind,
        CancellationToken cancellationToken = default);

    IDisposable Register(
        Guid batchId,
        WebAutomationOwnerKind ownerKind,
        Action cancellationRequested);
}

public sealed record WebAutomationBatchCancellationResult(
    bool IsCancelled,
    WebAutomationAttemptState PersistentState);

public sealed class WebAutomationAuthorizationException(
    BaiduAutomationDenialReason denialReason,
    string message) : BaiduAutomationNotValidatedException(denialReason, message);

public sealed class ProductionWebAutomationNavigationAuthorizer(
    IAcceptanceNavigationAuthorizationSource? acceptanceSource = null) : IWebAutomationNavigationAuthorizer
{
    public BaiduAutomationDecision Authorize(BaiduNavigationAuthorizationRequest request)
    {
        var production = BaiduSelectorCatalog.Production.AuthorizeNavigation(request);
        if (production.IsAllowed) return production;
        if (request.Action is BaiduAutomationAction.OpenOfficialUri &&
            BaiduNavigationPolicy.Evaluate(request.OfficialUri).IsAllowed &&
            acceptanceSource?.IsAuthorized(request) == true)
            return BaiduAutomationDecision.Allowed();
        return production;
    }
}

public sealed record VisibleWebAutomationQueueOptions
{
    public TimeSpan ActionStopTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan SessionTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan WindowCloseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public VisibleWebAutomationQueueOptions Validate()
    {
        if (ActionStopTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ActionStopTimeout));
        if (SessionTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(SessionTimeout));
        if (WindowCloseTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(WindowCloseTimeout));
        return this;
    }
}
