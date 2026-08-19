using System.Security.Cryptography;
using System.Text;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Core.Services;

namespace BaiduBatchTransfer.Application.Jobs;

public sealed class CreateBatchService
{
    private readonly IUnitOfWorkFactory unitOfWorkFactory;
    private readonly BatchDraftValidator validator;
    private readonly ReplicationPreflightEstimator preflightEstimator;

    public CreateBatchService(
        IUnitOfWorkFactory unitOfWorkFactory,
        BatchDraftValidator validator,
        ReplicationPreflightEstimator preflightEstimator)
    {
        this.unitOfWorkFactory = unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        this.preflightEstimator = preflightEstimator ?? throw new ArgumentNullException(nameof(preflightEstimator));
    }

    public async Task<BatchConfirmationSummary> PreviewAsync(
        BatchDraft draft,
        CancellationToken cancellationToken = default)
    {
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        var validated = await validator.ValidateAsync(draft, unit, cancellationToken).ConfigureAwait(false);
        var estimate = await preflightEstimator.EstimateAsync(
            validated.Rows.Length,
            validated.OperationCount,
            validated.Accounts.Length,
            cancellationToken).ConfigureAwait(false);
        return ToSummary(validated, estimate);
    }

    public async Task<CreatedBatchResult> ConfirmAsync(
        BatchDraft draft,
        string confirmationToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationToken);
        var preview = await PreviewAsync(draft, cancellationToken).ConfigureAwait(false);
        if (!FixedTimeEquals(preview.ConfirmationToken, confirmationToken))
            throw new BatchConfirmationException("The batch preview changed and must be confirmed again.");
        if (!preview.StorageEstimate.HasSufficientSpace)
            throw new StorageCapacityException(
                preview.StorageEstimate.RequiredFreeBytes,
                preview.StorageEstimate.AvailableFreeBytes);

        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var validated = await validator.ValidateAsync(draft, unit, cancellationToken).ConfigureAwait(false);
            if (!FixedTimeEquals(validated.ConfirmationToken, confirmationToken))
                throw new BatchConfirmationException("The batch preview changed and must be confirmed again.");
            if (validated.ImmediateOperationCount > int.MaxValue)
                throw new BatchDraftValidationException("The immediate operation count exceeds the supported database counter range.");

            var now = DateTimeOffset.UtcNow;
            var resources = CreateResources(draft, validated, now);
            var operations = CreateOperations(draft, validated, resources, now);
            var allocations = CreateAllocations(draft, validated, resources);
            var job = new BatchJobRecord(
                draft.BatchJobId,
                draft.Name,
                validated.Session.SourceType,
                draft.TransferMode.ToString(),
                draft.QuotaBasis.ToString(),
                draft.ShortagePolicy.ToString(),
                resources.Count,
                checked((int)validated.ImmediateOperationCount),
                draft.DefaultTargetPath,
                draft.FolderCreationMode,
                draft.DuplicatePolicy.ToString(),
                draft.ValidationMode,
                "Queued",
                now,
                null,
                null,
                draft.ImportSessionId,
                draft.QueueOrder);

            await unit.Jobs.AddAsync(job, cancellationToken).ConfigureAwait(false);
            await unit.Jobs.AddResourcesAsync(resources, cancellationToken).ConfigureAwait(false);
            await unit.Jobs.AddOperationsAsync(operations, cancellationToken).ConfigureAwait(false);
            await unit.Jobs.AddAllocationsAsync(allocations, cancellationToken).ConfigureAwait(false);
            await unit.Jobs.WriteAuditAsync(
                new AuditEventRecord(Guid.NewGuid(), draft.BatchJobId, null, null, null,
                    AuditEventKind.BatchCreated, now), cancellationToken).ConfigureAwait(false);
            await unit.Imports.UpdateSessionAsync(validated.Session with
            {
                Status = "BatchCreated",
                CreatedBatchJobId = draft.BatchJobId,
                UpdatedAt = now,
            }, cancellationToken).ConfigureAwait(false);
            await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new CreatedBatchResult(draft.BatchJobId, preview);
        }
        catch
        {
            await TryRollbackAsync(unit).ConfigureAwait(false);
            throw;
        }
    }

    private static BatchConfirmationSummary ToSummary(
        ValidatedBatchDraft validated,
        StorageGrowthEstimate estimate) =>
        new(validated.Session.Id, validated.Session.UpdatedAt, validated.Rows.Length,
            validated.OperationCount, validated.Accounts, estimate, validated.ConfirmationToken);

    private static List<ResourceItemRecord> CreateResources(
        BatchDraft draft,
        ValidatedBatchDraft validated,
        DateTimeOffset now)
    {
        var resources = new List<ResourceItemRecord>(validated.Rows.Length);
        for (var index = 0; index < validated.Rows.Length; index++)
        {
            var item = validated.Rows[index];
            resources.Add(new ResourceItemRecord(
                Guid.NewGuid(),
                draft.BatchJobId,
                item.Row.Id,
                index + 1L,
                item.Row.ShareUrl,
                item.Row.NormalizedShareId!,
                item.Row.ExtractionCode,
                item.Row.Note,
                item.Row.TargetPath,
                item.NormalizedTargetPath,
                item.Row.CreateFolderName,
                draft.FolderCreationMode,
                "Unknown",
                item.ConstraintAccountId,
                draft.TransferMode == TransferMode.Replication || draft.QuotaBasis == QuotaBasis.TaskCount
                    ? "Expanded"
                    : "Unallocated",
                null,
                null,
                now,
                now));
        }
        return resources;
    }

    private static IEnumerable<TransferOperationRecord> CreateOperations(
        BatchDraft draft,
        ValidatedBatchDraft validated,
        IReadOnlyList<ResourceItemRecord> resources,
        DateTimeOffset now)
    {
        if (draft.TransferMode == TransferMode.Distribution && draft.QuotaBasis == QuotaBasis.SuccessTarget)
            yield break;

        long ordinal = 0;
        if (draft.TransferMode == TransferMode.Replication)
        {
            foreach (var resource in resources)
            {
                var staged = validated.Rows[checked((int)resource.CandidateSequence - 1)];
                foreach (var account in validated.Accounts)
                {
                    ordinal = checked(ordinal + 1);
                    yield return Operation(resource, staged, account, ordinal, "Replication", now);
                }
            }
            yield break;
        }

        var candidates = resources.Select(resource => new TaskQuotaCandidate(
            resource.Id,
            checked((int)resource.CandidateSequence),
            resource.CandidateConstraintAccountId)).ToArray();
        var accounts = validated.Accounts.Select(account => new TaskQuotaAccount(
            account.AccountId,
            account.TargetItemCount,
            account.PriorityOrder)).ToArray();
        var assignments = new TaskQuotaAllocator().Allocate(accounts, candidates)
            .ToDictionary(assignment => assignment.ResourceId);
        var confirmations = validated.Accounts.ToDictionary(account => account.AccountId);
        foreach (var resource in resources)
        {
            var assignment = assignments[resource.Id];
            var account = confirmations[assignment.AccountId];
            var staged = validated.Rows[checked((int)resource.CandidateSequence - 1)];
            ordinal = checked(ordinal + 1);
            yield return Operation(resource, staged, account, ordinal,
                assignment.IsLocked ? "Locked" : "Weighted", now);
        }
    }

    private static TransferOperationRecord Operation(
        ResourceItemRecord resource,
        ValidatedStagedRow staged,
        BatchAccountConfirmation account,
        long ordinal,
        string source,
        DateTimeOffset now)
    {
        var target = staged.Row.TargetPath ?? account.DefaultTargetPath;
        var normalizedTarget = staged.NormalizedTargetPath ?? account.NormalizedDefaultTargetPath;
        var folder = staged.Row.CreateFolderName ?? string.Empty;
        return new TransferOperationRecord(
            Guid.NewGuid(), resource.Id, resource.BatchJobId, resource.CandidateSequence, ordinal,
            target, normalizedTarget, folder, account.AccountId, source, "Waiting", null, 0,
            BatchFingerprint.Create(resource.NormalizedShareId, account.AccountId, normalizedTarget, folder),
            null, null, null, "NotPrepared", "Unoccupied", null, null, null, null, null, null, now, now);
    }

    private static List<AccountAllocationRecord> CreateAllocations(
        BatchDraft draft,
        ValidatedBatchDraft validated,
        IReadOnlyList<ResourceItemRecord> resources)
    {
        return validated.Accounts.Select(account => new AccountAllocationRecord(
            Guid.NewGuid(),
            draft.BatchJobId,
            account.AccountId,
            account.PriorityOrder,
            account.DefaultTargetPath,
            account.NormalizedDefaultTargetPath,
            account.TargetItemCount,
            account.TargetSuccessCount,
            account.EffectiveTargetSuccessCount,
            draft.TransferMode == TransferMode.Replication ? resources.Count : account.TargetItemCount,
            0, 0, 0, 0, 0,
            account.LockedItemCount,
            account.ExclusiveCandidateCount,
            0, 0, 0)).ToList();
    }

    internal static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    internal static async Task TryRollbackAsync(IUnitOfWork unit)
    {
        try { await unit.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { /* Preserve the original finalization failure; DisposeAsync performs cleanup too. */ }
    }
}

public sealed record CreatedBatchResult(Guid BatchJobId, BatchConfirmationSummary Confirmation);

internal static class BatchFingerprint
{
    public static string Create(string normalizedShareId, Guid accountId, string normalizedPath, string folder) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"operation-v1\n{normalizedShareId}\n{accountId:N}\n{normalizedPath}\n{folder}")));
}
