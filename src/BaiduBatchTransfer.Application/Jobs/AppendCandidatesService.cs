using System.Collections.Immutable;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Core.Services;

namespace BaiduBatchTransfer.Application.Jobs;

public sealed class AppendCandidatesService
{
    private const int PageSize = 1_000;
    private const int WriteBatchSize = 500;
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

    public async Task ConfirmAsync(
        AppendCandidatesDraft draft,
        string confirmationToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationToken);
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

            foreach (var chunk in resources.Chunk(WriteBatchSize))
                await unit.Jobs.AddResourcesAsync(chunk, cancellationToken).ConfigureAwait(false);
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
            await unit.Jobs.AddExecutionLogsAsync(
                [new ExecutionLogRecord(Guid.NewGuid(), draft.BatchJobId, null, null, null,
                    "Finalization", "Information", "CANDIDATES_APPENDED", "Candidate append was confirmed.", now)],
                cancellationToken).ConfigureAwait(false);
            await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await CreateBatchService.TryRollbackAsync(unit).ConfigureAwait(false);
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
        var availableCandidates = new List<AllocationCandidate>();
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
                    availableCandidates.Add(new AllocationCandidate(
                        resource.Id,
                        checked((int)resource.CandidateSequence),
                        resource.CandidateConstraintAccountId,
                        status));
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
            availableCandidates.Add(new AllocationCandidate(
                row.Row.Id,
                checked((int)(appendedSequence = checked(appendedSequence + 1))),
                row.ConstraintAccountId,
                CandidateStatus.Unallocated));
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
        var reachableTargets = new ShortageSimulator()
            .Simulate(reachablePolicy, targetAccounts, availableCandidates)
            .EffectiveTargets;

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
}

internal sealed record ValidatedAppend(
    BatchJobRecord Batch,
    ImportSessionRecord Session,
    ImmutableArray<ValidatedStagedRow> Rows,
    ImmutableArray<AccountAllocationRecord> Allocations,
    ImmutableArray<BatchAccountConfirmation> Accounts,
    long MaximumCandidateSequence,
    long OperationCount,
    string ConfirmationToken);
