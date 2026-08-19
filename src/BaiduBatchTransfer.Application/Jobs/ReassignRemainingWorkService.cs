using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Jobs;

public sealed record ReassignmentAllocation(
    Guid AccountId,
    int TargetCount,
    int EffectiveTargetCount,
    int MinimumRetainedTargetCount,
    string AccountStatus,
    bool IsEnabled,
    bool IsDeleted)
{
    public bool IsHealthy =>
        string.Equals(AccountStatus, "Active", StringComparison.Ordinal) && IsEnabled && !IsDeleted;
}

public sealed record ReassignmentOperation(
    Guid OperationId,
    long SequenceNumber,
    Guid AssignedAccountId,
    string Status,
    string SubmitState,
    bool IsAccountExclusive,
    IReadOnlySet<Guid>? CompatibleTargetAccountIds = null,
    bool HasCrossedSubmissionBoundary = false)
{
    public bool CanMoveTo(Guid accountId) =>
        CompatibleTargetAccountIds is null || CompatibleTargetAccountIds.Contains(accountId);
}

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
    string ConfirmationToken,
    ReassignmentRequest Request);

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
        if (current[request.FailedAccountId].IsHealthy)
            throw new ReassignmentValidationException("The reassignment source account is still healthy.");

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
        if (deltas.Any(item => item.Delta > 0 && !current[item.AccountId].IsHealthy))
            throw new ReassignmentValidationException("Replacement targets must be active, enabled, and not deleted.");

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
            operationMoves = [];
            affectedCount = checked(deltas.Where(item => item.Delta > 0).Sum(item => item.Delta));
        }

        var canonicalRequest = new ReassignmentRequest(
            request.BatchJobId,
            request.FailedAccountId,
            new ReadOnlyDictionary<Guid, int>(new Dictionary<Guid, int>(request.NewTargetCounts)));
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
            token,
            canonicalRequest);
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

        var suppliedShapeToken = CreateToken(
            preview.BatchJobId,
            preview.BatchVersion,
            preview.QuotaBasis,
            preview.OperationMoves,
            preview.CandidateMoves,
            preview.AllocationDeltas);
        if (!FixedTimeEquals(preview.ConfirmationToken, suppliedShapeToken))
            throw new ReassignmentConfirmationException("The reassignment preview was modified after confirmation.");

        ReassignmentPreview canonical;
        try
        {
            canonical = await PreviewAsync(preview.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (ReassignmentValidationException)
        {
            throw new ReassignmentStaleStateException(
                "The batch changed; create and confirm a new reassignment preview.");
        }
        if (!string.Equals(canonical.BatchVersion, preview.BatchVersion, StringComparison.Ordinal))
            throw new ReassignmentStaleStateException("The batch changed; create and confirm a new reassignment preview.");
        if (!FixedTimeEquals(canonical.ConfirmationToken, preview.ConfirmationToken))
            throw new ReassignmentConfirmationException("The reassignment request no longer matches its canonical preview.");

        var mutation = new ReassignmentMutation(
            canonical.BatchJobId,
            canonical.OperationMoves,
            canonical.CandidateMoves,
            canonical.AllocationDeltas);
        if (!await store.ApplyAsync(mutation, canonical.BatchVersion, cancellationToken).ConfigureAwait(false))
            throw new ReassignmentStaleStateException("The batch changed; create and confirm a new reassignment preview.");
    }

    private static ReassignmentOperationMove[] BuildTaskQuotaMoves(
        ReassignmentState state,
        ReassignmentRequest request,
        IReadOnlyList<ReassignmentAllocationDelta> deltas)
    {
        var required = deltas.Where(item => item.AccountId == request.FailedAccountId && item.Delta < 0)
            .Select(item => -item.Delta).SingleOrDefault();
        var destinationCapacity = DestinationCapacity(request, deltas);
        if (destinationCapacity.Values.Sum() != required)
            throw new ReassignmentValidationException("Replacement targets do not match the failed account reduction.");

        var eligible = state.Operations
            .Where(item =>
                item.AssignedAccountId == request.FailedAccountId &&
                !item.IsAccountExclusive &&
                !item.HasCrossedSubmissionBoundary &&
                string.Equals(item.Status, "Waiting", StringComparison.Ordinal) &&
                string.Equals(item.SubmitState, "NotPrepared", StringComparison.Ordinal))
            .OrderBy(item => item.SequenceNumber)
            .ThenBy(item => item.OperationId)
            .ToArray();
        return MatchOperations(eligible, destinationCapacity, required,
            "Not enough never-submitted unlocked operations can be reassigned.");
    }

    private static SortedDictionary<Guid, int> DestinationCapacity(
        ReassignmentRequest request,
        IReadOnlyList<ReassignmentAllocationDelta> deltas) =>
        new(deltas
            .Where(item => item.AccountId != request.FailedAccountId && item.Delta > 0)
            .ToDictionary(item => item.AccountId, item => item.Delta));

    private static ReassignmentOperationMove[] MatchOperations(
        IEnumerable<ReassignmentOperation> candidates,
        IReadOnlyDictionary<Guid, int> capacity,
        int required,
        string failureMessage)
    {
        if (required == 0) return [];
        var ordered = candidates.OrderBy(item => item.SequenceNumber).ThenBy(item => item.OperationId).ToArray();
        var assignedByAccount = capacity.Keys.ToDictionary(accountId => accountId, _ => new List<ReassignmentOperation>());
        var accountByOperation = new Dictionary<Guid, Guid>();

        bool TryAssign(ReassignmentOperation operation, HashSet<Guid> visitedAccounts)
        {
            foreach (var accountId in capacity.Keys.OrderBy(value => value))
            {
                if (!operation.CanMoveTo(accountId) || !visitedAccounts.Add(accountId)) continue;
                var assigned = assignedByAccount[accountId];
                if (assigned.Count < capacity[accountId])
                {
                    assigned.Add(operation);
                    accountByOperation[operation.OperationId] = accountId;
                    return true;
                }
                foreach (var displaced in assigned.ToArray())
                {
                    if (!TryAssign(displaced, visitedAccounts)) continue;
                    assigned.Remove(displaced);
                    assigned.Add(operation);
                    accountByOperation[operation.OperationId] = accountId;
                    return true;
                }
            }
            return false;
        }

        foreach (var operation in ordered)
        {
            if (!TryAssign(operation, [])) continue;
            if (accountByOperation.Count == required) break;
        }
        if (accountByOperation.Count != required)
            throw new ReassignmentValidationException(failureMessage);
        return ordered
            .Where(item => accountByOperation.ContainsKey(item.OperationId))
            .Select(item => new ReassignmentOperationMove(
                item.OperationId, item.AssignedAccountId, accountByOperation[item.OperationId]))
            .ToArray();
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
