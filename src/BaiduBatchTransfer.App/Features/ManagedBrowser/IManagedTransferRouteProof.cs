using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

internal enum ManagedTransferRouteState
{
    OriginalSource = 1,
    OfficialTargetRoot = 2,
    ExactTargetDirectory = 3,
}

/// <summary>A closed set of fixed route expectations. Values remain redacted in diagnostics.</summary>
internal sealed class ManagedTransferRouteExpectation
{
    private ManagedTransferRouteExpectation(
        ManagedTransferRouteState state,
        string? directoryExactId = null,
        string? canonicalPath = null,
        IReadOnlyList<string>? ancestorExactIds = null)
    {
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        if (state == ManagedTransferRouteState.ExactTargetDirectory)
        {
            if (directoryExactId is null || canonicalPath is null || ancestorExactIds is null)
                throw new ArgumentException("An exact target directory proof is required.");
            ManagedBrowserTargetInventoryValidation.ValidateDirectoryIdentity(
                directoryExactId, canonicalPath, ancestorExactIds);
            DirectoryExactId = directoryExactId;
            CanonicalPath = canonicalPath;
            AncestorExactIds = Array.AsReadOnly(ancestorExactIds.ToArray());
        }
        else if (directoryExactId is not null || canonicalPath is not null || ancestorExactIds is not null)
        {
            throw new ArgumentException("This fixed route does not accept directory values.");
        }
        State = state;
    }

    internal static ManagedTransferRouteExpectation OriginalSource { get; } =
        new(ManagedTransferRouteState.OriginalSource);

    internal static ManagedTransferRouteExpectation OfficialTargetRoot { get; } =
        new(ManagedTransferRouteState.OfficialTargetRoot);

    internal static ManagedTransferRouteExpectation ExactTargetDirectory(
        string directoryExactId,
        string canonicalPath,
        IReadOnlyList<string> ancestorExactIds) =>
        new(ManagedTransferRouteState.ExactTargetDirectory,
            directoryExactId, canonicalPath, ancestorExactIds);

    internal ManagedTransferRouteState State { get; }
    internal string? DirectoryExactId { get; }
    internal string? CanonicalPath { get; }
    internal IReadOnlyList<string> AncestorExactIds { get; } = Array.Empty<string>();

    public override string ToString() =>
        $"ManagedTransferRouteExpectation {{ State = {State}, Route = [REDACTED] }}";
}

/// <summary>
/// Exact immutable input to an offline-injected route proof. Constructing it binds the owned handle to the same
/// browser, loopback endpoint and TargetId used by the fixed helper action.
/// </summary>
internal sealed class ManagedTransferRouteProofRequest
{
    internal ManagedTransferRouteProofRequest(
        ManagedChromiumSessionHandle exactSession,
        ManagedBrowserSessionReference exactManagedSession,
        WebAutomationRequestIdentity requestIdentity,
        ManagedBrowserTargetActionBinding binding,
        ManagedTransferRouteExpectation expectedRoute)
    {
        ExactSession = exactSession ?? throw new ArgumentNullException(nameof(exactSession));
        ArgumentNullException.ThrowIfNull(exactManagedSession);
        ArgumentNullException.ThrowIfNull(requestIdentity);
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        ExpectedRoute = expectedRoute ?? throw new ArgumentNullException(nameof(expectedRoute));
        requestIdentity.Validate();
        var execution = requestIdentity.ExecutionProfile;
        if (requestIdentity.OwnerKind != WebAutomationOwnerKind.Transfer ||
            requestIdentity.AccountId != exactManagedSession.AccountId ||
            exactSession.Profile.AccountId != exactManagedSession.AccountId ||
            exactSession.Installation.InstallationId != exactManagedSession.InstallationId ||
            exactSession.Installation.BrowserFamily != exactManagedSession.BrowserFamily ||
            exactSession.Profile.Family != exactManagedSession.BrowserFamily ||
            !Guid.TryParse(exactManagedSession.ProfileId, out var profileId) ||
            profileId != exactSession.Profile.ProfileId ||
            !Guid.TryParse(exactManagedSession.ProfileOwnershipId, out var ownershipId) ||
            ownershipId != exactSession.Profile.OwnershipId ||
            execution.Engine != BaiduExecutionEngine.ManagedChromiumV1 ||
            execution.InstallationId != exactManagedSession.InstallationId ||
            execution.SessionId != exactManagedSession.SessionId ||
            execution.BrowserFamily != exactManagedSession.BrowserFamily ||
            !string.Equals(execution.ProfileId, exactManagedSession.ProfileId, StringComparison.Ordinal) ||
            !string.Equals(exactManagedSession.StableBaiduUserId,
                binding.ExpectedBaiduUserId, StringComparison.Ordinal) ||
            !string.Equals(exactManagedSession.StableBaiduUserId,
                binding.ExpectedSource.BaiduUserId, StringComparison.Ordinal) ||
            !string.Equals(binding.ExpectedSource.ComputeFingerprint(binding.SourceUri),
                binding.ExpectedSource.ObservationFingerprint, StringComparison.Ordinal))
            throw new ArgumentException("The exact managed session identity does not match.");
        if (exactSession.BrowserId != binding.BrowserId ||
            !string.Equals(exactSession.TargetId, binding.TargetId, StringComparison.Ordinal) ||
            !string.Equals(exactSession.Endpoint.AbsoluteUri,
                binding.DevToolsEndpoint.AbsoluteUri, StringComparison.Ordinal))
            throw new ArgumentException("The exact owned browser binding does not match.", nameof(binding));

        ExactManagedSession = new ManagedBrowserSessionReference(
            exactManagedSession.SessionId, exactManagedSession.InstallationId,
            exactManagedSession.AccountId, exactManagedSession.StableBaiduUserId,
            exactManagedSession.BrowserFamily, exactManagedSession.ProfileId,
            exactManagedSession.ProfileOwnershipId);
        var copiedExecution = new ExecutionProfileSnapshot(
            execution.Engine, execution.BrowserFamily, execution.ProfileId,
            execution.InstallationId, execution.SessionId);
        RequestIdentity = new WebAutomationRequestIdentity(
            requestIdentity.OperationId, requestIdentity.BatchId, requestIdentity.AccountId,
            requestIdentity.Generation, requestIdentity.OwnerKind, copiedExecution);
    }

    internal ManagedChromiumSessionHandle ExactSession { get; }
    internal ManagedBrowserSessionReference ExactManagedSession { get; }
    internal WebAutomationRequestIdentity RequestIdentity { get; }
    internal ManagedBrowserTargetActionBinding Binding { get; }
    internal ManagedTransferRouteExpectation ExpectedRoute { get; }

    public override string ToString() =>
        "ManagedTransferRouteProofRequest { Session = [REDACTED], Binding = [REDACTED], Route = [REDACTED] }";
}

/// <summary>
/// Offline-only injected proof seam. Implementations may observe an already-owned exact target, but cannot
/// navigate it or obtain mutation authority through this contract.
/// </summary>
internal interface IManagedTransferRouteProof
{
    Task<bool> VerifyAsync(ManagedTransferRouteProofRequest request, CancellationToken cancellationToken);
}
