namespace BaiduBatchTransfer.Core.Domain;

public sealed class BatchJob
{
    public BatchJob(
        Guid id,
        string name,
        TransferMode transferMode,
        QuotaBasis quotaBasis,
        ShortagePolicy shortagePolicy,
        DateTimeOffset createdAt)
    {
        Id = id;
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("A batch name is required.", nameof(name))
            : name;
        TransferMode = transferMode;
        QuotaBasis = quotaBasis;
        ShortagePolicy = shortagePolicy;
        Status = BatchStatus.Queued;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public string Name { get; }

    public TransferMode TransferMode { get; }

    public QuotaBasis QuotaBasis { get; }

    public ShortagePolicy ShortagePolicy { get; }

    public BatchStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public void Activate(DateTimeOffset startedAt)
    {
        if (Status is not (BatchStatus.Queued or BatchStatus.Paused or BatchStatus.PausedReady))
        {
            throw new InvalidOperationException($"Cannot activate a batch in {Status} status.");
        }

        var earliestStart = StartedAt ?? CreatedAt;
        if (startedAt < earliestStart)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startedAt),
                "A resume time cannot precede the original start time.");
        }

        Status = BatchStatus.Active;
        StartedAt ??= startedAt;
    }

    public void Pause()
    {
        EnsureStatus(BatchStatus.Active);
        Status = BatchStatus.Paused;
    }

    public void WaitForCandidates()
    {
        EnsureStatus(BatchStatus.Active);
        Status = BatchStatus.AwaitingCandidates;
    }

    public void MarkCandidatesReady()
    {
        EnsureStatus(BatchStatus.AwaitingCandidates);
        Status = BatchStatus.PausedReady;
    }

    public void Complete(DateTimeOffset completedAt, bool hasShortfall = false)
    {
        EnsureStatus(BatchStatus.Active);
        EnsureCompletionTime(completedAt);
        Status = hasShortfall ? BatchStatus.CompletedWithShortfall : BatchStatus.Completed;
        CompletedAt = completedAt;
    }

    public void ReopenAfterShortfall(bool userConfirmed, DateTimeOffset reopenedAt)
    {
        EnsureStatus(BatchStatus.CompletedWithShortfall);
        if (!userConfirmed)
        {
            throw new InvalidOperationException("Explicit user confirmation is required to reopen a shortfall batch.");
        }

        if (CompletedAt is null || reopenedAt < CompletedAt.Value)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reopenedAt),
                "Reopen time cannot precede shortfall completion.");
        }

        Status = BatchStatus.PausedReady;
        CompletedAt = null;
    }

    public void Cancel(DateTimeOffset cancelledAt)
    {
        if (Status is BatchStatus.Completed or BatchStatus.CompletedWithShortfall or BatchStatus.Cancelled)
        {
            throw new InvalidOperationException($"Cannot cancel a batch in {Status} status.");
        }

        EnsureCompletionTime(cancelledAt);
        Status = BatchStatus.Cancelled;
        CompletedAt = cancelledAt;
    }

    private void EnsureStatus(BatchStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"Expected batch status {expected}, but was {Status}.");
        }
    }

    private void EnsureCompletionTime(DateTimeOffset completedAt)
    {
        var earliest = StartedAt ?? CreatedAt;
        if (completedAt < earliest)
        {
            throw new ArgumentOutOfRangeException(nameof(completedAt), "Completion time cannot move backwards.");
        }
    }
}
