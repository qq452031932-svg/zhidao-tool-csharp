using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Application.Jobs;

public sealed class ReopenShortfallBatchService(IUnitOfWorkFactory unitOfWorkFactory)
{
    private const int PageSize = 1_000;
    private readonly IUnitOfWorkFactory unitOfWorkFactory =
        unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));

    public async Task<ReopenShortfallConfirmation> PreviewAsync(
        Guid batchJobId,
        CancellationToken cancellationToken = default)
    {
        if (batchJobId == Guid.Empty) throw new ArgumentException("A batch identifier is required.", nameof(batchJobId));
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        return await BuildPreviewAsync(batchJobId, unit, cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyAsync(
        Guid batchJobId,
        string confirmationToken,
        DateTimeOffset reopenedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationToken);
        var preview = await PreviewAsync(batchJobId, cancellationToken).ConfigureAwait(false);
        if (!CreateBatchService.FixedTimeEquals(preview.ConfirmationToken, confirmationToken))
            throw new BatchConfirmationException("The shortfall snapshot changed and must be confirmed again.");

        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await BuildPreviewAsync(batchJobId, unit, cancellationToken).ConfigureAwait(false);
            if (!CreateBatchService.FixedTimeEquals(current.ConfirmationToken, confirmationToken))
                throw new BatchConfirmationException("The shortfall snapshot changed and must be confirmed again.");
            if (reopenedAt < current.CompletedAt)
                throw new ArgumentOutOfRangeException(nameof(reopenedAt), "The reopen time cannot precede completion.");

            var allocations = await ReadAllocationsAsync(unit.Jobs, batchJobId, cancellationToken).ConfigureAwait(false);
            await unit.Jobs.UpdateAllocationsAsync(allocations.Select(allocation => allocation with
            {
                EffectiveTargetSuccessCount = allocation.TargetSuccessCount,
            }), cancellationToken).ConfigureAwait(false);
            var batch = await unit.Jobs.GetAsync(batchJobId, cancellationToken).ConfigureAwait(false)
                ?? throw new BatchDraftValidationException("The shortfall batch does not exist.");
            await unit.Jobs.UpdateAsync(batch with { Status = "PausedReady", CompletedAt = null }, cancellationToken)
                .ConfigureAwait(false);
            await unit.Jobs.AddExecutionLogsAsync(
                [new ExecutionLogRecord(Guid.NewGuid(), batchJobId, null, null, null,
                    "Finalization", "Information", "SHORTFALL_REOPENED", "Shortfall batch reopen was confirmed.", reopenedAt)],
                cancellationToken).ConfigureAwait(false);
            await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await CreateBatchService.TryRollbackAsync(unit).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<ReopenShortfallConfirmation> BuildPreviewAsync(
        Guid batchJobId,
        IUnitOfWork unit,
        CancellationToken cancellationToken)
    {
        var batch = await unit.Jobs.GetAsync(batchJobId, cancellationToken).ConfigureAwait(false)
            ?? throw new BatchDraftValidationException("The shortfall batch does not exist.");
        if (batch.Status != "CompletedWithShortfall" || batch.CompletedAt is null)
            throw new BatchDraftValidationException("Only a completed shortfall batch can be reopened.");
        var allocations = await ReadAllocationsAsync(unit.Jobs, batchJobId, cancellationToken).ConfigureAwait(false);
        if (allocations.Count == 0)
            throw new BatchDraftValidationException("The shortfall batch has no account allocations.");

        var accounts = allocations
            .OrderBy(allocation => allocation.PriorityOrder)
            .ThenBy(allocation => allocation.AccountId)
            .Select(allocation => new ReopenShortfallAccountSnapshot(
                allocation.AccountId,
                allocation.PriorityOrder,
                allocation.TargetSuccessCount,
                allocation.EffectiveTargetSuccessCount,
                allocation.FulfilledCount,
                allocation.ReservedSlotCount,
                Math.Max(0L, (long)allocation.TargetSuccessCount - allocation.FulfilledCount - allocation.ReservedSlotCount)))
            .ToImmutableArray();
        var target = accounts.Sum(account => (long)account.TargetSuccessCount);
        var effective = accounts.Sum(account => (long)account.EffectiveTargetSuccessCount);
        var fulfilled = accounts.Sum(account => (long)account.FulfilledCount);
        var reserved = accounts.Sum(account => (long)account.ReservedSlotCount);
        var shortfall = accounts.Sum(account => (long)account.ShortfallCount);
        if (shortfall == 0)
            throw new BatchDraftValidationException("The completed batch no longer has a shortfall to reopen.");

        var snapshotVersion = SnapshotVersion(batch, allocations, target, effective, fulfilled, reserved, shortfall);
        var confirmationToken = Hash($"reopen-shortfall-v1|{snapshotVersion}");
        return new ReopenShortfallConfirmation(
            batch.Id,
            batch.Status,
            batch.CompletedAt.Value,
            target,
            effective,
            fulfilled,
            reserved,
            shortfall,
            accounts,
            snapshotVersion,
            confirmationToken);
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

    private static string SnapshotVersion(
        BatchJobRecord batch,
        IEnumerable<AccountAllocationRecord> allocations,
        long target,
        long effective,
        long fulfilled,
        long reserved,
        long shortfall)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "reopen-snapshot-v1");
        Append(hash, batch.Id.ToString("N"));
        Append(hash, batch.Name);
        Append(hash, batch.SourceType);
        Append(hash, batch.TransferMode);
        Append(hash, batch.QuotaBasis);
        Append(hash, batch.ShortagePolicy);
        Append(hash, batch.ResourceCount);
        Append(hash, batch.OperationCount);
        Append(hash, batch.DefaultTargetPath);
        Append(hash, batch.FolderCreationMode);
        Append(hash, batch.DuplicatePolicy);
        Append(hash, batch.ValidationMode);
        Append(hash, batch.Status);
        Append(hash, batch.CreatedAt);
        Append(hash, batch.StartedAt);
        Append(hash, batch.CompletedAt);
        Append(hash, batch.ImportSessionId);
        Append(hash, batch.QueueOrder);
        Append(hash, target);
        Append(hash, effective);
        Append(hash, fulfilled);
        Append(hash, reserved);
        Append(hash, shortfall);
        foreach (var allocation in allocations.OrderBy(item => item.PriorityOrder).ThenBy(item => item.AccountId))
        {
            Append(hash, allocation.Id);
            Append(hash, allocation.BatchJobId);
            Append(hash, allocation.AccountId);
            Append(hash, allocation.PriorityOrder);
            Append(hash, allocation.DefaultTargetPath);
            Append(hash, allocation.NormalizedDefaultTargetPath);
            Append(hash, allocation.TargetItemCount);
            Append(hash, allocation.TargetSuccessCount);
            Append(hash, allocation.EffectiveTargetSuccessCount);
            Append(hash, allocation.AssignedItemCount);
            Append(hash, allocation.AttemptedItemCount);
            Append(hash, allocation.NewSuccessItemCount);
            Append(hash, allocation.AlreadySatisfiedItemCount);
            Append(hash, allocation.SuccessItemCount);
            Append(hash, allocation.FailedItemCount);
            Append(hash, allocation.LockedItemCount);
            Append(hash, allocation.ExclusiveCandidateCount);
            Append(hash, allocation.UnusedCandidateCount);
            Append(hash, allocation.ReservedSlotCount);
            Append(hash, allocation.FulfilledCount);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void Append(IncrementalHash hash, object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
        hash.AppendData(Encoding.UTF8.GetBytes(text));
        hash.AppendData([0]);
    }
}

public sealed record ReopenShortfallAccountSnapshot(
    Guid AccountId,
    int PriorityOrder,
    int TargetSuccessCount,
    int EffectiveTargetSuccessCount,
    int FulfilledCount,
    int ReservedSlotCount,
    long ShortfallCount)
{
    public override string ToString() =>
        $"ReopenShortfallAccountSnapshot {{ AccountId = [REDACTED], PriorityOrder = {PriorityOrder}, TargetSuccessCount = {TargetSuccessCount}, EffectiveTargetSuccessCount = {EffectiveTargetSuccessCount}, FulfilledCount = {FulfilledCount}, ReservedSlotCount = {ReservedSlotCount}, ShortfallCount = {ShortfallCount} }}";
}

public sealed class ReopenShortfallConfirmation
{
    public ReopenShortfallConfirmation(
        Guid batchJobId,
        string jobStatus,
        DateTimeOffset completedAt,
        long targetSuccessCount,
        long effectiveTargetSuccessCount,
        long fulfilledCount,
        long reservedSlotCount,
        long shortfallCount,
        IReadOnlyList<ReopenShortfallAccountSnapshot> accounts,
        string snapshotVersion,
        string confirmationToken)
    {
        BatchJobId = batchJobId;
        JobStatus = jobStatus;
        CompletedAt = completedAt;
        TargetSuccessCount = targetSuccessCount;
        EffectiveTargetSuccessCount = effectiveTargetSuccessCount;
        FulfilledCount = fulfilledCount;
        ReservedSlotCount = reservedSlotCount;
        ShortfallCount = shortfallCount;
        Accounts = (accounts ?? throw new ArgumentNullException(nameof(accounts))).ToImmutableArray();
        SnapshotVersion = snapshotVersion;
        ConfirmationToken = confirmationToken;
    }

    public Guid BatchJobId { get; }
    public string JobStatus { get; }
    public DateTimeOffset CompletedAt { get; }
    public long TargetSuccessCount { get; }
    public long EffectiveTargetSuccessCount { get; }
    public long FulfilledCount { get; }
    public long ReservedSlotCount { get; }
    public long ShortfallCount { get; }
    public ImmutableArray<ReopenShortfallAccountSnapshot> Accounts { get; }
    public string SnapshotVersion { get; }
    public string ConfirmationToken { get; }

    public override string ToString() =>
        $"ReopenShortfallConfirmation {{ BatchJobId = {BatchJobId}, JobStatus = {JobStatus}, TargetSuccessCount = {TargetSuccessCount}, EffectiveTargetSuccessCount = {EffectiveTargetSuccessCount}, FulfilledCount = {FulfilledCount}, ReservedSlotCount = {ReservedSlotCount}, ShortfallCount = {ShortfallCount}, Accounts = {Accounts.Length}, SnapshotVersion = [REDACTED], ConfirmationToken = [REDACTED] }}";
}
