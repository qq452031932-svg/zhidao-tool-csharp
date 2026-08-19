using System.Security.Cryptography;
using System.Text;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Jobs;

public sealed record ReassignmentAllocation(
    Guid AccountId,
    int TargetCount,
    int EffectiveTargetCount,
    int MinimumRetainedTargetCount = 0);

public sealed record ReassignmentOperation(
    Guid OperationId,
    long SequenceNumber,
    Guid AssignedAccountId,
    string Status,
    string SubmitState,
    bool IsAccountExclusive);

public sealed record ReassignmentCandidate(
    Guid ResourceItemId,
    long CandidateSequence,
    string CandidateStatus,
    Guid? ReservedAccountId,
    Guid? ConstraintAccountId);

public sealed record ReassignmentState(
    Guid BatchJobId,
    string BatchVersion,
    QuotaBasis QuotaBasis,
    IReadOnlyList<ReassignmentAllocation> Allocations,
    IReadOnlyList<ReassignmentOperation> Operations,
    IReadOnlyList<ReassignmentCandidate> Candidates);

public sealed record ReassignmentRequest(
    Guid BatchJobId,
    Guid FailedAccountId,
    IReadOnlyDictionary<Guid, int> NewTargetCounts);

public sealed record ReassignmentOperationMove(
    Guid OperationId,
    Guid OldAccountId,
    Guid NewAccountId);

public sealed record ReassignmentCandidateMove(
    Guid ResourceItemId,
    Guid? OldAccountId,
    Guid? NewAccountId);

public sealed record ReassignmentAllocationDelta(
    Guid AccountId,
    int OldTarget,
    int NewTarget,
    int Delta);

public sealed record ReassignmentMutation(
    Guid BatchJobId,
    IReadOnlyList<ReassignmentOperationMove> OperationMoves,
    IReadOnlyList<ReassignmentCandidateMove> CandidateMoves,
    IReadOnlyList<ReassignmentAllocationDelta> AllocationDeltas);

public sealed record ReassignmentPreview(
    Guid BatchJobId,
    string BatchVersion,
    QuotaBasis QuotaBasis,
    IReadOnlyList<ReassignmentOperationMove> OperationMoves,
    IReadOnlyList<ReassignmentCandidateMove> CandidateMoves,
    IReadOnlyList<ReassignmentAllocationDelta> AllocationDeltas,
    int AffectedCount,
    string ConfirmationToken);

public interface IReassignmentStore
{
    Task<ReassignmentState?> LoadAsync(Guid batchJobId, CancellationToken cancellationToken = default);
    Task<bool> ApplyAsync(
        ReassignmentMutation mutation,
        string expectedBatchVersion,
        CancellationToken cancellationToken = default);
}

public sealed class ReassignmentConfirmationException(string message) : InvalidOperationException(message);
public sealed class ReassignmentStaleStateException(string message) : InvalidOperationException(message);
public sealed class ReassignmentValidationException(string message) : InvalidOperationException(message);

public sealed class ReassignRemainingWorkService
{
    private readonly IReassignmentStore store;

    public ReassignRemainingWorkService(IReassignmentStore store) =>
        this.store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<ReassignmentPreview> PreviewAsync(
        ReassignmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.BatchJobId == Guid.Empty || request.FailedAccountId == Guid.Empty)
            throw new ReassignmentValidationException("A batch and failed account are required.");
        if (request.NewTargetCounts.Count == 0 || request.NewTargetCounts.Values.Any(value => value < 0))
            throw new ReassignmentValidationException("Non-negative replacement targets are required.");

        var state = await store.LoadAsync(request.BatchJobId, cancellationToken).ConfigureAwait(false)
            ?? throw new ReassignmentValidationException("The batch no longer exists.");
        if (string.IsNullOrWhiteSpace(state.BatchVersion))
            throw new ReassignmentValidationException("The batch state cannot be confirmed.");

        var current = state.Allocations.ToDictionary(item => item.AccountId);
        if (!current.ContainsKey(request.FailedAccountId) ||
            request.NewTargetCounts.Keys.Any(accountId => !current.ContainsKey(accountId)))
            throw new ReassignmentValidationException("The reassignment contains an account outside this batch.");
        if (request.NewTargetCounts.Count != current.Count ||
            current.Keys.Any(accountId => !request.NewTargetCounts.ContainsKey(accountId)))
            throw new ReassignmentValidationException("A target is required for every participating account.");

        var oldTotal = current.Values.Sum(item => (long)item.TargetCount);
        var newTotal = request.NewTargetCounts.Values.Sum(value => (long)value);
        if (oldTotal != newTotal)
            throw new ReassignmentValidationException("Reassignment must preserve the batch target total.");
        if (current.Values.Any(item => request.NewTargetCounts[item.AccountId] < item.MinimumRetainedTargetCount))
            throw new ReassignmentValidationException("A target cannot be reduced below reserved or fulfilled work.");

        var deltas = current.Values
            .OrderBy(item => item.AccountId)
            .Select(item => new ReassignmentAllocationDelta(
                item.AccountId,
                item.TargetCount,
                request.NewTargetCounts[item.AccountId],
                checked(request.NewTargetCounts[item.AccountId] - item.TargetCount)))
            .Where(item => item.Delta != 0)
            .ToArray();
        if (deltas.Length == 0)
            throw new ReassignmentValidationException("At least one failed-account target must be reassigned.");
        var failedDelta = deltas.Where(item => item.AccountId == request.FailedAccountId)
            .Select(item => item.Delta).SingleOrDefault();
        if (failedDelta >= 0 || deltas.Any(item => item.AccountId != request.FailedAccountId && item.Delta < 0) ||
            deltas.Where(item => item.AccountId != request.FailedAccountId && item.Delta > 0).Sum(item => item.Delta) != -failedDelta)
            throw new ReassignmentValidationException("Only the failed account target can be redistributed to healthy accounts.");

        ReassignmentOperationMove[] operationMoves;
        ReassignmentCandidateMove[] candidateMoves = [];
        int affectedCount;
        if (state.QuotaBasis == QuotaBasis.TaskCount)
        {
            operationMoves = BuildTaskQuotaMoves(state, request, deltas);
            affectedCount = operationMoves.Length;
        }
        else
        {
            // Success-target candidates stay unreserved and public. Submitted, reserved,
            // result-pending, fulfilled and account-exclusive work is never rewritten here.
            operationMoves = [];
            affectedCount = checked(deltas.Where(item => item.Delta > 0).Sum(item => item.Delta));
        }

        var token = CreateToken(state.BatchJobId, state.BatchVersion, state.QuotaBasis,
            operationMoves, candidateMoves, deltas);
        return new ReassignmentPreview(
            state.BatchJobId,
            state.BatchVersion,
            state.QuotaBasis,
            operationMoves,
            candidateMoves,
            deltas,
            affectedCount,
            token);
    }

    public async Task ApplyAsync(
        ReassignmentPreview preview,
        string confirmationToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationToken);
        if (!FixedTimeEquals(preview.ConfirmationToken, confirmationToken))
            throw new ReassignmentConfirmationException("The reassignment preview must be explicitly confirmed.");

        var mutation = new ReassignmentMutation(
            preview.BatchJobId,
            preview.OperationMoves,
            preview.CandidateMoves,
            preview.AllocationDeltas);
        if (!await store.ApplyAsync(mutation, preview.BatchVersion, cancellationToken).ConfigureAwait(false))
            throw new ReassignmentStaleStateException("The batch changed; create and confirm a new reassignment preview.");
    }

    private static ReassignmentOperationMove[] BuildTaskQuotaMoves(
        ReassignmentState state,
        ReassignmentRequest request,
        IReadOnlyList<ReassignmentAllocationDelta> deltas)
    {
        var required = deltas.Where(item => item.AccountId == request.FailedAccountId && item.Delta < 0)
            .Select(item => -item.Delta).SingleOrDefault();
        var destinations = new Queue<Guid>(deltas
            .Where(item => item.AccountId != request.FailedAccountId && item.Delta > 0)
            .OrderBy(item => item.AccountId)
            .SelectMany(item => Enumerable.Repeat(item.AccountId, item.Delta)));
        if (destinations.Count != required)
            throw new ReassignmentValidationException("Replacement targets do not match the failed account reduction.");

        var eligible = state.Operations
            .Where(item =>
                item.AssignedAccountId == request.FailedAccountId &&
                !item.IsAccountExclusive &&
                string.Equals(item.Status, "Waiting", StringComparison.Ordinal) &&
                string.Equals(item.SubmitState, "NotPrepared", StringComparison.Ordinal))
            .OrderBy(item => item.SequenceNumber)
            .ThenBy(item => item.OperationId)
            .Take(required)
            .ToArray();
        if (eligible.Length != required)
            throw new ReassignmentValidationException("Not enough never-submitted unlocked operations can be reassigned.");

        return eligible.Select(item => new ReassignmentOperationMove(
            item.OperationId,
            item.AssignedAccountId,
            destinations.Dequeue())).ToArray();
    }

    private static string CreateToken(
        Guid batchId,
        string version,
        QuotaBasis basis,
        IEnumerable<ReassignmentOperationMove> operationMoves,
        IEnumerable<ReassignmentCandidateMove> candidateMoves,
        IEnumerable<ReassignmentAllocationDelta> deltas)
    {
        var lines = new List<string> { "reassignment-v1", batchId.ToString("N"), version, basis.ToString() };
        lines.AddRange(operationMoves.OrderBy(item => item.OperationId)
            .Select(item => $"O|{item.OperationId:N}|{item.OldAccountId:N}|{item.NewAccountId:N}"));
        lines.AddRange(candidateMoves.OrderBy(item => item.ResourceItemId)
            .Select(item => $"C|{item.ResourceItemId:N}|{item.OldAccountId:N}|{item.NewAccountId:N}"));
        lines.AddRange(deltas.OrderBy(item => item.AccountId)
            .Select(item => $"A|{item.AccountId:N}|{item.OldTarget}|{item.NewTarget}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var a = Encoding.ASCII.GetBytes(left);
        var b = Encoding.ASCII.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
