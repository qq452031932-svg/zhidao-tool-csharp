using System.Collections.Immutable;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Core.Services;

namespace BaiduBatchTransfer.Application.Jobs;

public sealed class AppendCandidatesService
{
    private const string AppendEventCode = "CANDIDATES_APPENDED";
    private const int PageSize = 1_000;
    private readonly IUnitOfWorkFactory unitOfWorkFactory;
    private readonly ReplicationPreflightEstimator preflightEstimator;
    private readonly BaiduPathNormalizer pathNormalizer = new();

    public AppendCandidatesService(
        IUnitOfWorkFactory unitOfWorkFactory,
        ReplicationPreflightEstimator preflightEstimator)
    {
        this.unitOfWorkFactory = unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
        this.preflightEstimator = preflightEstimator ?? throw new ArgumentNullException(nameof(preflightEstimator));
    }

    public async Task<BatchConfirmationSummary> PreviewAsync(
        AppendCandidatesDraft draft,
        CancellationToken cancellationToken = default)
    {
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        var validated = await ValidateAsync(draft, unit, cancellationToken).ConfigureAwait(false);
        var estimate = await preflightEstimator.EstimateAsync(
            validated.Rows.Length, validated.OperationCount, validated.Accounts.Length, cancellationToken).ConfigureAwait(false);
        return new BatchConfirmationSummary(
            validated.Session.Id,
            validated.Session.UpdatedAt,
            validated.Rows.Length,
            validated.OperationCount,
            validated.Accounts,
            estimate,
            validated.ConfirmationToken);
    }

    public async Task<AppendCandidatesResult?> GetResultAsync(
        AppendCandidatesDraft draft,
        string confirmationToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationToken);
        var auditId = BatchCommandIdempotency.AuditId(
            "append-candidates", confirmationToken, draft.BatchJobId, draft.ImportSessionId);
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        var audit = await unit.Jobs.GetExecutionLogAsync(auditId, cancellationToken).ConfigureAwait(false);
        if (audit is null) return null;
        if (audit.BatchJobId != draft.BatchJobId || audit.EventCode != AppendEventCode)
            throw new BatchConfirmationException("The confirmation token is already bound to a different command.");
        return new AppendCandidatesResult(draft.BatchJobId, draft.ImportSessionId, audit.CreatedAt, audit.EventCode);
    }

    public async Task<AppendCandidatesResult> ConfirmAsync(
        AppendCandidatesDraft draft,
        string confirmationToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationToken);
        var auditId = BatchCommandIdempotency.AuditId(
            "append-candidates", confirmationToken, draft.BatchJobId, draft.ImportSessionId);
        await using var gate = await BatchCommandIdempotency.AcquireAsync(auditId, cancellationToken).ConfigureAwait(false);
        var completed = await GetResultAsync(draft, confirmationToken, cancellationToken).ConfigureAwait(false);
        if (completed is not null) return completed;
        var preview = await PreviewAsync(draft, cancellationToken).ConfigureAwait(false);
        if (!CreateBatchService.FixedTimeEquals(preview.ConfirmationToken, confirmationToken))
            throw new BatchConfirmationException("The append preview changed and must be confirmed again.");
        if (!preview.StorageEstimate.HasSufficientSpace)
            throw new StorageCapacityException(
                preview.StorageEstimate.RequiredFreeBytes,
                preview.StorageEstimate.AvailableFreeBytes);

        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var validated = await ValidateAsync(draft, unit, cancellationToken).ConfigureAwait(false);
            if (!CreateBatchService.FixedTimeEquals(validated.ConfirmationToken, confirmationToken))
                throw new BatchConfirmationException("The append preview changed and must be confirmed again.");
            var now = DateTimeOffset.UtcNow;
            await unit.Jobs.WriteAuditAsync(
                new AuditEventRecord(auditId, draft.BatchJobId, null, null, null,
                    AuditEventKind.CandidatesAppended, now), cancellationToken).ConfigureAwait(false);
            var sequence = validated.MaximumCandidateSequence;
            var resources = validated.Rows.Select(row => new ResourceItemRecord(
                Guid.NewGuid(),
                draft.BatchJobId,
                row.Row.Id,
                sequence = checked(sequence + 1),
                row.Row.ShareUrl,
                row.Row.NormalizedShareId!,
                row.Row.ExtractionCode,
                row.Row.Note,
                row.Row.TargetPath,
                row.NormalizedTargetPath,
                row.Row.CreateFolderName,
                validated.Batch.FolderCreationMode,
                "Unknown",
                row.ConstraintAccountId,
                "Unallocated",
                null,
                null,
                now,
                now)).ToArray();

            await unit.Jobs.AddResourcesAsync(resources, cancellationToken).ConfigureAwait(false);
            var updatedAllocations = validated.Allocations.Select(allocation => allocation with
            {
                ExclusiveCandidateCount = checked(allocation.ExclusiveCandidateCount +
                    resources.Count(resource => resource.CandidateConstraintAccountId == allocation.AccountId)),
            }).ToArray();
            await unit.Jobs.UpdateAllocationsAsync(updatedAllocations, cancellationToken).ConfigureAwait(false);
            var updatedBatch = validated.Batch with
            {
                ResourceCount = checked(validated.Batch.ResourceCount + resources.Length),
                Status = validated.Batch.Status == "AwaitingCandidates" ? "PausedReady" : validated.Batch.Status,
            };
            await unit.Jobs.UpdateAsync(updatedBatch, cancellationToken).ConfigureAwait(false);
            await unit.Imports.UpdateSessionAsync(validated.Session with
            {
                Status = "BatchCreated",
                CreatedBatchJobId = draft.BatchJobId,
                UpdatedAt = now,
            }, cancellationToken).ConfigureAwait(false);
            await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new AppendCandidatesResult(
                draft.BatchJobId, draft.ImportSessionId, now, AppendEventCode);
        }
        catch
        {
            await CreateBatchService.TryRollbackAsync(unit).ConfigureAwait(false);
            var recovered = await GetResultAsync(draft, confirmationToken, cancellationToken).ConfigureAwait(false);
            if (recovered is not null) return recovered;
            throw;
        }
    }

    private async Task<ValidatedAppend> ValidateAsync(
        AppendCandidatesDraft draft,
        IUnitOfWork unit,
        CancellationToken cancellationToken)
    {
        var batch = await unit.Jobs.GetAsync(draft.BatchJobId, cancellationToken).ConfigureAwait(false)
            ?? throw new BatchDraftValidationException("The target batch does not exist.");
        if (batch.TransferMode != "Distribution" || batch.QuotaBasis != "SuccessTarget")
            throw new BatchDraftValidationException("Only success-target distribution batches accept candidate appends.");
        if (batch.Status is not ("AwaitingCandidates" or "PausedReady"))
            throw new BatchDraftValidationException("The target batch is not paused for candidate changes.");
        var session = await unit.Imports.GetSessionAsync(draft.ImportSessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new BatchDraftValidationException("The append import does not exist.");
        var confirmableStatus = session.Status == "ReadyToConfirm" ||
            (session.Status == "Draft" && batch.ValidationMode != "BatchPrecheck");
        if (!confirmableStatus || session.AppendToBatchJobId != draft.BatchJobId || session.CreatedBatchJobId is not null)
            throw new BatchDraftValidationException("The append import is not ready for this batch.");
        await BatchDraftValidator.ValidateSessionCountersAsync(session, unit.Imports, cancellationToken)
            .ConfigureAwait(false);

        var allocations = await ReadAllocationsAsync(unit.Jobs, draft.BatchJobId, cancellationToken).ConfigureAwait(false);
        if (allocations.Count == 0) throw new BatchDraftValidationException("The target batch has no account allocations.");
        var accountIds = allocations.Select(allocation => allocation.AccountId).ToHashSet();
        var existingIds = new HashSet<string>(StringComparer.Ordinal);
        var availableCandidates = new List<AppendReachabilityCandidate>();
        long maximumSequence = 0;
        for (var pageNumber = 1; ; pageNumber++)
        {
            var page = await unit.Jobs.GetResourcesPageAsync(
                draft.BatchJobId, new PageQuery(pageNumber, PageSize), cancellationToken).ConfigureAwait(false);
            foreach (var resource in page.Items)
            {
                existingIds.Add(resource.NormalizedShareId);
                maximumSequence = Math.Max(maximumSequence, resource.CandidateSequence);
                if (Enum.TryParse<CandidateStatus>(resource.CandidateStatus, out var status) &&
                    status is CandidateStatus.Unallocated or CandidateStatus.Unused)
                {
                    availableCandidates.Add(new AppendReachabilityCandidate(
                        resource.Id,
                        checked((int)resource.CandidateSequence),
                        resource.CandidateConstraintAccountId,
                        resource.NormalizedTargetPathOverride,
                        resource.CreateFolderName ?? string.Empty));
                }
            }
            if ((long)pageNumber * PageSize >= page.TotalCount) break;
        }

        var rows = ImmutableArray.CreateBuilder<ValidatedStagedRow>();
        var after = 0;
        var readySeen = 0;
        while (true)
        {
            var page = await unit.Imports.GetRowsAfterSourceRowAsync(
                session.Id, "Ready", after, PageSize, cancellationToken).ConfigureAwait(false);
            if (page.Count == 0) break;
            after = page[^1].SourceRowNumber;
            readySeen = checked(readySeen + page.Count);
            foreach (var row in page)
            {
                if (row.PrecheckStatus == "Failed") continue;
                if (batch.ValidationMode == "BatchPrecheck" && row.PrecheckStatus != "Succeeded")
                    throw new BatchDraftValidationException("An append row has not completed the required batch precheck.");
                if (string.IsNullOrWhiteSpace(row.NormalizedShareId))
                    throw new BatchDraftValidationException("A ready append row is missing its normalized identity.");
                if (!existingIds.Add(row.NormalizedShareId)) continue;
                if (row.MappedAccountId is { } mapped && !accountIds.Contains(mapped))
                    throw new BatchDraftValidationException("An append candidate is constrained to an unavailable account.");
                string? normalizedTarget = null;
                if (row.TargetPath is not null)
                {
                    try { normalizedTarget = pathNormalizer.Normalize(row.TargetPath).ComparisonValue; }
                    catch (ArgumentException) { throw new BatchDraftValidationException("An append target path is invalid."); }
                }
                rows.Add(new ValidatedStagedRow(row, normalizedTarget, row.MappedAccountId));
            }
        }
        if (readySeen != session.ReadyRows)
            throw new BatchDraftValidationException("The append counters no longer match its staged rows.");
        if (rows.Count == 0)
            throw new BatchDraftValidationException("The append import has no new candidates after batch-level deduplication.");

        var appendedSequence = maximumSequence;
        foreach (var row in rows)
        {
            availableCandidates.Add(new AppendReachabilityCandidate(
                row.Row.Id,
                checked((int)(appendedSequence = checked(appendedSequence + 1))),
                row.ConstraintAccountId,
                row.NormalizedTargetPath,
                row.Row.CreateFolderName ?? string.Empty));
        }
        if (!Enum.TryParse<ShortagePolicy>(batch.ShortagePolicy, out var shortagePolicy))
            throw new BatchDraftValidationException("The batch has an unsupported shortage policy.");
        var targetAccounts = allocations.OrderBy(allocation => allocation.PriorityOrder).Select(allocation =>
            new SuccessTargetAccount(
                allocation.AccountId,
                allocation.TargetSuccessCount,
                allocation.TargetSuccessCount,
                allocation.FulfilledCount,
                allocation.ReservedSlotCount,
                allocation.PriorityOrder)).ToArray();
        var reachablePolicy = shortagePolicy == ShortagePolicy.WaitForCandidates
            ? ShortagePolicy.ProportionalReduction
            : shortagePolicy;
        var operationKeys = await ReadNonCancelledOperationKeysAsync(
            unit.Jobs, draft.BatchJobId, cancellationToken).ConfigureAwait(false);
        var reachableTargets = SimulateReachableTargets(
            reachablePolicy, targetAccounts, allocations, availableCandidates, operationKeys);

        var confirmations = allocations.OrderBy(allocation => allocation.PriorityOrder).Select(allocation =>
        {
            var newExclusive = rows.Count(row => row.ConstraintAccountId == allocation.AccountId);
            return new BatchAccountConfirmation(
                allocation.AccountId,
                allocation.PriorityOrder,
                allocation.DefaultTargetPath,
                allocation.NormalizedDefaultTargetPath,
                allocation.TargetItemCount,
                allocation.TargetSuccessCount,
                allocation.LockedItemCount,
                checked(allocation.ExclusiveCandidateCount + newExclusive),
                allocation.EffectiveTargetSuccessCount,
                allocation.FulfilledCount,
                allocation.ReservedSlotCount,
                allocation.UnusedCandidateCount,
                newExclusive,
                reachableTargets[allocation.AccountId]);
        })
            .ToImmutableArray();
        var operationCount = (long)rows.Count;
        return new ValidatedAppend(
            batch, session, rows.ToImmutable(), allocations.ToImmutableArray(), confirmations,
            maximumSequence, operationCount,
            BatchConfirmationTokens.ForAppend(draft, session, batch, rows, confirmations, operationCount));
    }

    private static async Task<List<AccountAllocationRecord>> ReadAllocationsAsync(
        IJobRepository jobs,
        Guid batchJobId,
        CancellationToken cancellationToken)
    {
        var result = new List<AccountAllocationRecord>();
        for (var pageNumber = 1; ; pageNumber++)
        {
            var page = await jobs.GetAllocationsPageAsync(
                batchJobId, new PageQuery(pageNumber, PageSize), cancellationToken).ConfigureAwait(false);
            result.AddRange(page.Items);
            if (result.Count >= page.TotalCount) return result;
        }
    }

    private static async Task<HashSet<AppendOperationKey>> ReadNonCancelledOperationKeysAsync(
        IJobRepository jobs,
        Guid batchJobId,
        CancellationToken cancellationToken)
    {
        var keys = new HashSet<AppendOperationKey>();
        for (var pageNumber = 1; ; pageNumber++)
        {
            var page = await jobs.GetOperationsPageAsync(
                batchJobId, new PageQuery(pageNumber, PageSize), cancellationToken).ConfigureAwait(false);
            foreach (var operation in page.Items)
            {
                if (!string.Equals(operation.Status, "Cancelled", StringComparison.Ordinal))
                {
                    keys.Add(new AppendOperationKey(
                        operation.ResourceItemId,
                        operation.AssignedAccountId,
                        operation.NormalizedTargetPath,
                        operation.CreateFolderName));
                }
            }
            if ((long)pageNumber * PageSize >= page.TotalCount) return keys;
        }
    }

    private static IReadOnlyDictionary<Guid, int> SimulateReachableTargets(
        ShortagePolicy policy,
        IReadOnlyList<SuccessTargetAccount> accounts,
        IReadOnlyList<AccountAllocationRecord> allocations,
        IReadOnlyCollection<AppendReachabilityCandidate> candidates,
        IReadOnlySet<AppendOperationKey> operationKeys)
    {
        var current = accounts.ToDictionary(account => account.AccountId);
        var defaults = allocations.ToDictionary(
            allocation => allocation.AccountId,
            allocation => allocation.NormalizedDefaultTargetPath);
        var ordered = candidates.OrderBy(candidate => candidate.CandidateSequence).ToArray();
        var exclusive = ordered
            .Where(candidate => candidate.ConstraintAccountId is not null)
            .GroupBy(candidate => candidate.ConstraintAccountId!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var publicCandidates = ordered.Where(candidate => candidate.ConstraintAccountId is null).ToArray();
        var exclusiveOffsets = accounts.ToDictionary(account => account.AccountId, _ => 0);
        var publicOffsets = accounts.ToDictionary(account => account.AccountId, _ => 0);
        var consumed = new HashSet<Guid>();
        var allocator = new SuccessTargetAllocator();

        bool IsBlocked(AppendReachabilityCandidate candidate, Guid accountId) =>
            operationKeys.Contains(new AppendOperationKey(
                candidate.ResourceId,
                accountId,
                candidate.NormalizedTargetPathOverride ?? defaults[accountId],
                candidate.CreateFolderName));

        AppendReachabilityCandidate? PeekPool(
            Guid accountId,
            IReadOnlyList<AppendReachabilityCandidate> pool,
            IDictionary<Guid, int> offsets)
        {
            var offset = offsets[accountId];
            while (offset < pool.Count &&
                   (consumed.Contains(pool[offset].ResourceId) || IsBlocked(pool[offset], accountId)))
            {
                offset++;
            }
            offsets[accountId] = offset;
            return offset < pool.Count ? pool[offset] : null;
        }

        AppendReachabilityCandidate? PeekAccount(Guid accountId)
        {
            if (exclusive.TryGetValue(accountId, out var accountCandidates))
            {
                var candidate = PeekPool(accountId, accountCandidates, exclusiveOffsets);
                if (candidate is not null) return candidate;
            }
            return PeekPool(accountId, publicCandidates, publicOffsets);
        }

        while (true)
        {
            var eligibleAccountIds = current.Values
                .Where(account => account.AvailableSlots > 0 && PeekAccount(account.AccountId) is not null)
                .Select(account => account.AccountId)
                .ToHashSet();
            if (eligibleAccountIds.Count == 0) break;
            var account = policy == ShortagePolicy.AccountPriority
                ? current.Values
                    .Where(value => eligibleAccountIds.Contains(value.AccountId))
                    .OrderBy(value => value.PriorityOrder)
                    .ThenBy(value => value.AccountId)
                    .First()
                : allocator.SelectAccount(current.Values, eligibleAccountIds)!;
            var candidate = PeekAccount(account.AccountId)!;
            consumed.Add(candidate.ResourceId);
            current[account.AccountId] = account.AddSimulatedFulfillment();
        }

        var hasPendingReservations = accounts.Any(account => account.ReservedSlotCount > 0);
        return accounts.ToDictionary(
            account => account.AccountId,
            account => hasPendingReservations
                ? account.EffectiveTarget
                : current[account.AccountId].FulfilledCount + current[account.AccountId].ReservedSlotCount);
    }
}

internal sealed record AppendReachabilityCandidate(
    Guid ResourceId,
    int CandidateSequence,
    Guid? ConstraintAccountId,
    string? NormalizedTargetPathOverride,
    string CreateFolderName);

internal sealed record AppendOperationKey(
    Guid ResourceItemId,
    Guid AccountId,
    string NormalizedTargetPath,
    string CreateFolderName);

internal sealed record ValidatedAppend(
    BatchJobRecord Batch,
    ImportSessionRecord Session,
    ImmutableArray<ValidatedStagedRow> Rows,
    ImmutableArray<AccountAllocationRecord> Allocations,
    ImmutableArray<BatchAccountConfirmation> Accounts,
    long MaximumCandidateSequence,
    long OperationCount,
    string ConfirmationToken);
