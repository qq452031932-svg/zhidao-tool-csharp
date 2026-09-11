namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

public enum ManagedSessionOperationResultKind
{
    Succeeded = 1,
    ContractUnavailable = 2,
    LoginRequired = 3,
    VerificationRequired = 4,
    ExplicitlyRejected = 5,
    OutcomeUnknown = 6,
}

public sealed class ManagedSessionOperationResult
{
    private ManagedSessionOperationResult(ManagedSessionOperationResultKind kind, string safeMessage)
    {
        Kind = kind;
        SafeMessage = safeMessage;
    }

    public ManagedSessionOperationResultKind Kind { get; }
    public string SafeMessage { get; }

    public static ManagedSessionOperationResult Succeeded() =>
        new(ManagedSessionOperationResultKind.Succeeded, "操作成功");

    public static ManagedSessionOperationResult ContractUnavailable() =>
        new(ManagedSessionOperationResultKind.ContractUnavailable, "当前操作接口尚未启用");

    public override string ToString() =>
        $"ManagedSessionOperationResult {{ Kind = {Kind}, SafeMessage = {SafeMessage} }}";
}

public interface IManagedSessionOperationDispatcher
{
    Task<ManagedSessionOperationResult> DispatchAsync(
        ManagedSessionOperationRequest request,
        CancellationToken cancellationToken);
}

public sealed class ManagedSessionOperationContractAvailability
{
    private static readonly HashSet<ManagedSessionOperationKind> MutationKinds =
        [ManagedSessionOperationKind.EnsureDirectory,
         ManagedSessionOperationKind.SubmitTransfer,
         ManagedSessionOperationKind.SubmitShare];

    private readonly HashSet<ManagedSessionOperationKind> enabledMutations;

    private ManagedSessionOperationContractAvailability(IEnumerable<ManagedSessionOperationKind> enabledMutations)
    {
        this.enabledMutations = new HashSet<ManagedSessionOperationKind>(enabledMutations);
        if (this.enabledMutations.Any(kind => !MutationKinds.Contains(kind)))
            throw new ArgumentException("Only named mutations can be enabled.", nameof(enabledMutations));
    }

    public static ManagedSessionOperationContractAvailability ReadOnly { get; } = new([]);

    public ManagedSessionOperationContractAvailability WithEnabledMutation(ManagedSessionOperationKind kind)
    {
        if (!MutationKinds.Contains(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        return new ManagedSessionOperationContractAvailability(enabledMutations.Append(kind));
    }

    public bool CanDispatch(ManagedSessionOperationKind kind) =>
        !MutationKinds.Contains(kind) || enabledMutations.Contains(kind);
}

public sealed class ManagedSessionOperationClient
{
    private readonly IManagedSessionOperationDispatcher dispatcher;
    private readonly ManagedSessionOperationContractAvailability availability;

    public ManagedSessionOperationClient(
        IManagedSessionOperationDispatcher dispatcher,
        ManagedSessionOperationContractAvailability availability)
    {
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.availability = availability ?? throw new ArgumentNullException(nameof(availability));
    }

    public Task<ManagedSessionOperationResult> InspectSourceAsync(
        ManagedSessionOperationRequest request, CancellationToken cancellationToken) =>
        DispatchNamedAsync(ManagedSessionOperationKind.InspectSource, request, cancellationToken);

    public Task<ManagedSessionOperationResult> EnsureDirectoryAsync(
        ManagedSessionOperationRequest request, CancellationToken cancellationToken) =>
        DispatchNamedAsync(ManagedSessionOperationKind.EnsureDirectory, request, cancellationToken);

    public Task<ManagedSessionOperationResult> SubmitTransferAsync(
        ManagedSessionOperationRequest request, CancellationToken cancellationToken) =>
        DispatchNamedAsync(ManagedSessionOperationKind.SubmitTransfer, request, cancellationToken);

    public Task<ManagedSessionOperationResult> ObserveTransferAsync(
        ManagedSessionOperationRequest request, CancellationToken cancellationToken) =>
        DispatchNamedAsync(ManagedSessionOperationKind.ObserveTransfer, request, cancellationToken);

    public Task<ManagedSessionOperationResult> SubmitShareAsync(
        ManagedSessionOperationRequest request, CancellationToken cancellationToken) =>
        DispatchNamedAsync(ManagedSessionOperationKind.SubmitShare, request, cancellationToken);

    public Task<ManagedSessionOperationResult> ObserveShareAsync(
        ManagedSessionOperationRequest request, CancellationToken cancellationToken) =>
        DispatchNamedAsync(ManagedSessionOperationKind.ObserveShare, request, cancellationToken);

    private Task<ManagedSessionOperationResult> DispatchNamedAsync(
        ManagedSessionOperationKind expected,
        ManagedSessionOperationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind != expected)
            throw new ArgumentException("The request does not match the named operation.", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        return availability.CanDispatch(expected)
            ? dispatcher.DispatchAsync(request, cancellationToken)
            : Task.FromResult(ManagedSessionOperationResult.ContractUnavailable());
    }
}
