using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;

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
        Func<IVisibleWebAutomationSession, VisibleWebAutomationContext, CancellationToken, Task<T>> action)
    {
        Identity = (identity ?? throw new ArgumentNullException(nameof(identity))).Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        ProfilePath = profilePath;
        Navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        Action = action ?? throw new ArgumentNullException(nameof(action));
    }

    public WebAutomationRequestIdentity Identity { get; init; }
    public string ProfilePath { get; init; }
    public BaiduNavigationAuthorizationRequest Navigation { get; init; }
    public Func<IVisibleWebAutomationSession, VisibleWebAutomationContext, CancellationToken, Task<T>> Action { get; init; }

    public override string ToString() =>
        $"VisibleWebAutomationRequest {{ Identity = {Identity}, ProfilePath = [REDACTED], Navigation = [REDACTED], Action = [REDACTED] }}";
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

public interface IVisibleWebAutomationSessionFactory
{
    Task<IVisibleWebAutomationSession> CreateAsync(
        WebAutomationRequestIdentity identity,
        string profilePath,
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
    string message) : InvalidOperationException(message)
{
    public BaiduAutomationDenialReason DenialReason { get; } = denialReason;
}

public sealed class ProductionWebAutomationNavigationAuthorizer : IWebAutomationNavigationAuthorizer
{
    public BaiduAutomationDecision Authorize(BaiduNavigationAuthorizationRequest request) =>
        BaiduSelectorCatalog.Production.AuthorizeNavigation(request);
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
