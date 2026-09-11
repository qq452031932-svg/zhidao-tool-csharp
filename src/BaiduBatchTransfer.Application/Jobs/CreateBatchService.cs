using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Core.Services;

namespace BaiduBatchTransfer.Application.Jobs;

public sealed class CreateBatchService
{
    private static readonly JsonSerializerOptions SummaryJsonOptions = new() { MaxDepth = 8 };
    private readonly IUnitOfWorkFactory unitOfWorkFactory;
    private readonly BatchDraftValidator validator;
    private readonly ReplicationPreflightEstimator preflightEstimator;
    private readonly SecureExtractionCodeGenerator extractionCodeGenerator;

    public CreateBatchService(
        IUnitOfWorkFactory unitOfWorkFactory,
        BatchDraftValidator validator,
        ReplicationPreflightEstimator preflightEstimator,
        SecureExtractionCodeGenerator? extractionCodeGenerator = null)
    {
        this.unitOfWorkFactory = unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        this.preflightEstimator = preflightEstimator ?? throw new ArgumentNullException(nameof(preflightEstimator));
        this.extractionCodeGenerator = extractionCodeGenerator ?? new SecureExtractionCodeGenerator();
    }

    public async Task<BatchConfirmationSummary> PreviewAsync(
        BatchDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        BatchDraftValidator.ValidateSelectionSnapshot(draft);
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        var validated = await validator.ValidateAsync(draft, unit, cancellationToken).ConfigureAwait(false);
        var estimate = await preflightEstimator.EstimateAsync(
            validated.Rows.Length,
            validated.OperationCount,
            validated.Accounts.Length,
            validated.ShareOperationCount,
            validated.ShareOperationCount,
            validated.Accounts.Length,
            validated.ResultColumnLayout.ColumnCount,
            cancellationToken).ConfigureAwait(false);
        return ToSummary(validated, estimate);
    }

    public async Task<CreatedBatchResult> ConfirmAsync(
        BatchDraft draft,
        string confirmationToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationToken);
        var replay = await TryReplayAsync(draft, confirmationToken, cancellationToken).ConfigureAwait(false);
        if (replay is not null) return replay;
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
            var currentEstimate = await preflightEstimator.EstimateAsync(
                validated.Rows.Length,
                validated.OperationCount,
                validated.Accounts.Length,
                validated.ShareOperationCount,
                validated.ShareOperationCount,
                validated.Accounts.Length,
                validated.ResultColumnLayout.ColumnCount,
                cancellationToken).ConfigureAwait(false);
            if (!currentEstimate.HasSufficientSpace)
                throw new StorageCapacityException(currentEstimate.RequiredFreeBytes, currentEstimate.AvailableFreeBytes);

            var now = DateTimeOffset.UtcNow;
            var resources = CreateResources(draft, validated, now);
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
                draft.QueueOrder,
                checked((int)(draft.TransferMode == TransferMode.Replication || draft.QuotaBasis == QuotaBasis.TaskCount
                    ? validated.ShareOperationCount
                    : 0)),
                draft.AcceptanceOnly,
                draft.DirectBackground,
                draft.MaxConcurrency,
                "Inert");

            await unit.Jobs.AddAsync(job, cancellationToken).ConfigureAwait(false);
            await unit.Jobs.SetShareSettingsAsync(new(
                draft.BatchJobId,
                draft.IsSharePermanent,
                draft.IsShareAccessUnlimited,
                draft.ExtractionCodeBehavior.ToString(),
                draft.FixedExtractionCode,
                draft.ExistingSharePolicy,
                now,
                draft.ShareSettingsFingerprint), cancellationToken).ConfigureAwait(false);
            var outputAccounts = validated.Accounts.Select(account => new BatchOutputAccountRecord(
                draft.BatchJobId,
                account.AccountId,
                account.OutputOrdinal,
                account.OutputLabel!,
                checked((int)account.PlannedTransferCount),
                checked((int)account.PlannedShareCount),
                account.ShareEnabled && account.PlannedShareCount > 0)).ToArray();
            await unit.Jobs.AddOutputAccountsAsync(outputAccounts, cancellationToken).ConfigureAwait(false);
            await unit.Jobs.AddSourceColumnsAsync(validated.ColumnDefinitions.Select(column => new BatchSourceColumnRecord(
                draft.BatchJobId,
                column.ColumnOrdinal,
                column.SourceHeader,
                column.MappedRole,
                column.IsRequired)), cancellationToken).ConfigureAwait(false);
            await unit.Jobs.AddResourcesAsync(resources, cancellationToken).ConfigureAwait(false);
            var outputByAccount = outputAccounts.ToDictionary(account => account.AccountId);
            foreach (var chunk in CreateOperations(draft, validated, resources, now).Chunk(500))
            {
                await unit.Jobs.AddOperationsAsync(chunk, cancellationToken).ConfigureAwait(false);
                await unit.Jobs.AddPendingShareOperationsAsync(
                    chunk.Where(operation => outputByAccount[operation.AssignedAccountId].ShareEnabled)
                        .Select(operation => new PendingShareOperationRecord(
                            Guid.NewGuid(),
                            operation.Id,
                            operation.ResourceItemId,
                            operation.BatchJobId,
                            operation.AssignedAccountId,
                            outputByAccount[operation.AssignedAccountId].OutputOrdinal,
                            draft.ShareSettingsFingerprint,
                            now,
                            draft.DirectBackground ? extractionCodeGenerator.Generate() : null)),
                    cancellationToken).ConfigureAwait(false);
            }
            await unit.Jobs.AddAllocationsAsync(allocations, cancellationToken).ConfigureAwait(false);
            if (draft.DirectBackground)
            {
                await unit.Jobs.SetResultDestinationAsync(
                    LocalResultDestinationRecord.Create(draft.BatchJobId, draft.ResultDirectory!, now),
                    cancellationToken).ConfigureAwait(false);
                if (!await unit.Jobs.HasExactDirectBackgroundGraphAsync(
                        draft.BatchJobId, cancellationToken).ConfigureAwait(false))
                    throw new BatchDraftValidationException(
                        "The direct-background transfer/share graph is incomplete or inconsistent.");
                await unit.Jobs.ActivateDirectBackgroundAsync(
                    draft.BatchJobId, cancellationToken).ConfigureAwait(false);
            }
            if (draft.AcceptanceOnly &&
                (unit.Jobs is not IAcceptanceExecutionProfileStore acceptanceStore ||
                 !await acceptanceStore.HasExactAcceptanceGraphAsync(
                     draft.BatchJobId, cancellationToken).ConfigureAwait(false)))
                throw new BatchDraftValidationException("The AcceptanceOnly operation graph is incomplete or inconsistent.");
            await unit.Jobs.WriteAuditAsync(
                new AuditEventRecord(Guid.NewGuid(), draft.BatchJobId, null, null, null,
                    AuditEventKind.BatchCreated, now), cancellationToken).ConfigureAwait(false);
            await unit.Jobs.AddCreationReceiptAsync(new(
                BatchConfirmationTokens.ForCommand(draft),
                HashToken(confirmationToken),
                validated.ConfirmationToken,
                draft.ImportSessionId,
                draft.BatchJobId,
                validated.Session.ColumnMappingFingerprint,
                now,
                JsonSerializer.Serialize(BatchConfirmationSummarySnapshot.From(preview), SummaryJsonOptions)),
                cancellationToken).ConfigureAwait(false);
            if (!await unit.Imports.TryFinalizeBatchCreationAsync(
                    validated.Session, draft.BatchJobId, now, cancellationToken).ConfigureAwait(false))
                throw new BatchConfirmationException("The batch preview changed and must be confirmed again.");
            await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new CreatedBatchResult(draft.BatchJobId, preview);
        }
        catch
        {
            await TryRollbackAsync(unit).ConfigureAwait(false);
            var existing = await TryReplayAsync(draft, confirmationToken, cancellationToken).ConfigureAwait(false);
            if (existing is not null) return existing;
            throw;
        }
    }

    private async Task<CreatedBatchResult?> TryReplayAsync(
        BatchDraft draft,
        string confirmationToken,
        CancellationToken cancellationToken)
    {
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        var receipt = await unit.Jobs.GetCreationReceiptAsync(draft.BatchJobId, cancellationToken).ConfigureAwait(false);
        if (receipt is null) return null;
        if (receipt.ImportSessionId != draft.ImportSessionId ||
            !FixedTimeEquals(receipt.CommandFingerprint, BatchConfirmationTokens.ForCommand(draft)) ||
            !FixedTimeEquals(receipt.ConfirmationTokenHash, HashToken(confirmationToken)) ||
            !FixedTimeEquals(receipt.SnapshotFingerprint, confirmationToken))
            throw new BatchConfirmationException("The batch preview changed and must be confirmed again.");

        try
        {
            var snapshot = JsonSerializer.Deserialize<BatchConfirmationSummarySnapshot>(
                receipt.ConfirmationSummaryJson, SummaryJsonOptions);
            if (snapshot is null || snapshot.Version != 1 || snapshot.ImportSessionId != receipt.ImportSessionId)
                throw new InvalidOperationException("The durable batch creation receipt is incomplete.");
            return new CreatedBatchResult(receipt.BatchJobId, snapshot.ToSummary(confirmationToken));
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The durable batch creation receipt is incomplete.");
        }
    }

    private static string HashToken(string confirmationToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(confirmationToken)));

    private static BatchConfirmationSummary ToSummary(
        ValidatedBatchDraft validated,
        StorageGrowthEstimate estimate) =>
        new(validated.Session.Id, validated.Session.UpdatedAt, validated.Rows.Length,
            validated.OperationCount, validated.Accounts, estimate, validated.ConfirmationToken,
            validated.ResultColumnLayout, validated.AcceptanceOnly,
            validated.DirectBackground, validated.MaxConcurrency);

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
                now,
                item.ResolvedOutputTitle));
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
                    yield return Operation(resource, staged, account, ordinal, "Replication", now, draft.AcceptanceOnly);
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
                assignment.IsLocked ? "Locked" : "Weighted", now, draft.AcceptanceOnly);
        }
    }

    private static TransferOperationRecord Operation(
        ResourceItemRecord resource,
        ValidatedStagedRow staged,
        BatchAccountConfirmation account,
        long ordinal,
        string source,
        DateTimeOffset now,
        bool acceptanceOnly)
    {
        var target = staged.Row.TargetPath ?? account.DefaultTargetPath;
        var normalizedTarget = staged.NormalizedTargetPath ?? account.NormalizedDefaultTargetPath;
        var folder = staged.Row.CreateFolderName ?? string.Empty;
        return new TransferOperationRecord(
            Guid.NewGuid(), resource.Id, resource.BatchJobId, resource.CandidateSequence, ordinal,
            target, normalizedTarget, folder, account.AccountId, source, "Waiting", null, 0,
            BatchFingerprint.Create(resource.NormalizedShareId, account.AccountId, normalizedTarget, folder),
            null, null, null, "NotPrepared", "Unoccupied", null, null, null, null, null, null, now, now,
            account.ExecutionProfile,
            acceptanceOnly);
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

internal sealed record BatchConfirmationSummarySnapshot(
    int Version,
    Guid ImportSessionId,
    DateTimeOffset ImportUpdatedAt,
    long ResourceCount,
    long OperationCount,
    BatchAccountConfirmationSnapshot[] Accounts,
    StorageGrowthEstimate StorageEstimate,
    ResultColumnLayoutSnapshot? ResultColumnLayout,
    bool AcceptanceOnly = false,
    bool DirectBackground = false,
    int MaxConcurrency = 1)
{
    public static BatchConfirmationSummarySnapshot From(BatchConfirmationSummary value) => new(
        1,
        value.ImportSessionId,
        value.ImportUpdatedAt,
        value.ResourceCount,
        value.OperationCount,
        value.Accounts.Select(BatchAccountConfirmationSnapshot.From).ToArray(),
        value.StorageEstimate,
        value.ResultColumnLayout is null ? null : ResultColumnLayoutSnapshot.From(value.ResultColumnLayout),
        value.AcceptanceOnly,
        value.DirectBackground,
        value.MaxConcurrency);

    public BatchConfirmationSummary ToSummary(string confirmationToken) => new(
        ImportSessionId,
        ImportUpdatedAt,
        ResourceCount,
        OperationCount,
        Accounts.Select(account => account.ToConfirmation()).ToArray(),
        StorageEstimate,
        confirmationToken,
        ResultColumnLayout?.ToLayout(),
        AcceptanceOnly,
        DirectBackground,
        MaxConcurrency);
}

internal sealed record BatchAccountConfirmationSnapshot(
    Guid AccountId,
    int PriorityOrder,
    string DefaultTargetPath,
    string NormalizedDefaultTargetPath,
    int TargetItemCount,
    int TargetSuccessCount,
    int LockedItemCount,
    int ExclusiveCandidateCount,
    int EffectiveTargetSuccessCount,
    int FulfilledCount,
    int ReservedSlotCount,
    int UnusedCandidateCount,
    int NewExclusiveCandidateCount,
    int ReachableTargetSuccessCount,
    long PlannedTransferCount,
    long PlannedShareCount,
    bool ShareEnabled,
    int OutputOrdinal,
    string? OutputLabel,
    ExecutionProfileSnapshot? ExecutionProfile = null)
{
    public static BatchAccountConfirmationSnapshot From(BatchAccountConfirmation value) => new(
        value.AccountId, value.PriorityOrder, value.DefaultTargetPath, value.NormalizedDefaultTargetPath,
        value.TargetItemCount, value.TargetSuccessCount, value.LockedItemCount, value.ExclusiveCandidateCount,
        value.EffectiveTargetSuccessCount, value.FulfilledCount, value.ReservedSlotCount,
        value.UnusedCandidateCount, value.NewExclusiveCandidateCount, value.ReachableTargetSuccessCount,
        value.PlannedTransferCount, value.PlannedShareCount, value.ShareEnabled, value.OutputOrdinal,
        value.OutputLabel, value.ExecutionProfile);

    public BatchAccountConfirmation ToConfirmation() => new(
        AccountId, PriorityOrder, DefaultTargetPath, NormalizedDefaultTargetPath,
        TargetItemCount, TargetSuccessCount, LockedItemCount, ExclusiveCandidateCount,
        EffectiveTargetSuccessCount, FulfilledCount, ReservedSlotCount, UnusedCandidateCount,
        NewExclusiveCandidateCount, ReachableTargetSuccessCount, PlannedTransferCount,
        PlannedShareCount, ShareEnabled, OutputOrdinal, OutputLabel, ExecutionProfile);
}

internal sealed record ResultColumnLayoutSnapshot(
    string[] SourceHeaders,
    string ResolvedTitleHeader,
    string OriginalShareUrlHeader,
    ResultAccountColumnLayout[] Accounts,
    string Fingerprint)
{
    public static ResultColumnLayoutSnapshot From(ResultColumnLayout value) => new(
        value.SourceHeaders.ToArray(), value.ResolvedTitleHeader, value.OriginalShareUrlHeader,
        value.Accounts.ToArray(), value.Fingerprint);

    public ResultColumnLayout ToLayout() => new(
        SourceHeaders, ResolvedTitleHeader, OriginalShareUrlHeader, Accounts, Fingerprint);
}

internal static class BatchFingerprint
{
    public static string Create(string normalizedShareId, Guid accountId, string normalizedPath, string folder) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"operation-v1\n{normalizedShareId}\n{accountId:N}\n{normalizedPath}\n{folder}")));
}
