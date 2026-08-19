namespace BaiduBatchTransfer.Core.Domain;

public sealed class TransferOperation
{
    private TransferOperation(
        Guid id,
        Guid resourceItemId,
        Guid batchJobId,
        Guid assignedAccountId,
        DateTimeOffset createdAt)
    {
        Id = id;
        ResourceItemId = resourceItemId;
        BatchJobId = batchJobId;
        AssignedAccountId = assignedAccountId;
        Status = OperationStatus.Waiting;
        TargetSlotState = TargetSlotState.Unoccupied;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; }

    public Guid ResourceItemId { get; }

    public Guid BatchJobId { get; }

    public Guid AssignedAccountId { get; }

    public OperationStatus Status { get; private set; }

    public TargetSlotState TargetSlotState { get; private set; }

    public int AttemptCount { get; private set; }

    public bool SubmissionMayHaveOccurred { get; private set; }

    public DateTimeOffset? TargetSlotReservedAt { get; private set; }

    public DateTimeOffset? SubmitStartedAt { get; private set; }

    public string? LastErrorCode { get; private set; }

    public string? LastErrorMessage { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static TransferOperation CreateWaiting(
        Guid id,
        Guid resourceItemId,
        Guid batchJobId,
        Guid assignedAccountId,
        DateTimeOffset createdAt) =>
        new(id, resourceItemId, batchJobId, assignedAccountId, createdAt);

    public static TransferOperation CreateReserved(
        Guid id,
        Guid resourceItemId,
        Guid batchJobId,
        Guid assignedAccountId,
        DateTimeOffset reservedAt)
    {
        var operation = new TransferOperation(
            id,
            resourceItemId,
            batchJobId,
            assignedAccountId,
            reservedAt);
        operation.ReserveTargetSlot(reservedAt);
        return operation;
    }

    public void ReserveTargetSlot(DateTimeOffset reservedAt)
    {
        EnsureStatus(OperationStatus.Waiting);
        EnsureTimestamp(reservedAt);
        if (TargetSlotState is not (TargetSlotState.Unoccupied or TargetSlotState.Released))
        {
            throw new InvalidOperationException($"Cannot reserve a slot in {TargetSlotState} state.");
        }

        TargetSlotState = TargetSlotState.Reserved;
        TargetSlotReservedAt = reservedAt;
        Status = OperationStatus.Reserved;
        UpdatedAt = reservedAt;
    }

    public void MarkPreparing(DateTimeOffset preparedAt)
    {
        EnsureStatus(OperationStatus.Reserved);
        EnsureReservedSlot();
        EnsureTimestamp(preparedAt);
        Status = OperationStatus.Preparing;
        AttemptCount++;
        LastErrorCode = null;
        LastErrorMessage = null;
        UpdatedAt = preparedAt;
    }

    public void MarkSubmitting(DateTimeOffset submitStartedAt)
    {
        EnsureStatus(OperationStatus.Preparing);
        EnsureReservedSlot();
        EnsureTimestamp(submitStartedAt);
        Status = OperationStatus.Submitting;
        SubmissionMayHaveOccurred = true;
        SubmitStartedAt = submitStartedAt;
        UpdatedAt = submitStartedAt;
    }

    public void MarkResultPending(string reason, DateTimeOffset updatedAt)
    {
        if (Status is not (OperationStatus.Preparing or OperationStatus.Submitting))
        {
            throw new InvalidOperationException($"Cannot mark {Status} operation as result pending.");
        }

        EnsureReservedSlot();
        EnsureTimestamp(updatedAt);
        Status = OperationStatus.ResultPending;
        SubmissionMayHaveOccurred = true;
        LastErrorMessage = string.IsNullOrWhiteSpace(reason) ? null : reason;
        UpdatedAt = updatedAt;
    }

    public void MarkSucceeded(DateTimeOffset updatedAt)
    {
        if (Status is not (OperationStatus.Preparing or OperationStatus.Submitting or OperationStatus.ResultPending))
        {
            throw new InvalidOperationException($"Cannot mark {Status} operation as succeeded.");
        }

        EnsureReservedSlot();
        EnsureTimestamp(updatedAt);
        TargetSlotState = TargetSlotState.Fulfilled;
        Status = OperationStatus.Succeeded;
        LastErrorCode = null;
        LastErrorMessage = null;
        UpdatedAt = updatedAt;
    }

    public void MarkDefinitivelyFailed(
        string errorCode,
        string errorMessage,
        DateTimeOffset updatedAt)
    {
        if (Status is not (
            OperationStatus.Reserved or
            OperationStatus.Preparing or
            OperationStatus.Submitting or
            OperationStatus.ResultPending))
        {
            throw new InvalidOperationException($"Cannot fail an operation in {Status} status.");
        }

        EnsureReservedSlot();
        EnsureTimestamp(updatedAt);
        TargetSlotState = TargetSlotState.Released;
        Status = OperationStatus.Failed;
        LastErrorCode = errorCode;
        LastErrorMessage = errorMessage;
        UpdatedAt = updatedAt;
    }

    public void Cancel(DateTimeOffset cancelledAt)
    {
        if (SubmissionMayHaveOccurred)
        {
            throw new InvalidOperationException(
                "Cannot cancel an operation after submission may have occurred.");
        }

        if (Status is not (OperationStatus.Waiting or OperationStatus.Reserved or OperationStatus.Preparing))
        {
            throw new InvalidOperationException(
                $"Cannot cancel an operation in {Status} status because submission may have occurred or it is terminal.");
        }

        EnsureTimestamp(cancelledAt);
        if (TargetSlotState == TargetSlotState.Reserved)
        {
            TargetSlotState = TargetSlotState.Released;
        }

        Status = OperationStatus.Cancelled;
        UpdatedAt = cancelledAt;
    }

    public void ResetForRetry(DateTimeOffset updatedAt)
    {
        EnsureStatus(OperationStatus.Failed);
        EnsureTimestamp(updatedAt);
        if (TargetSlotState != TargetSlotState.Released)
        {
            throw new InvalidOperationException("Only a released target slot can be retried.");
        }

        Status = OperationStatus.Waiting;
        LastErrorCode = null;
        LastErrorMessage = null;
        UpdatedAt = updatedAt;
    }

    private void EnsureStatus(OperationStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"Expected operation status {expected}, but was {Status}.");
        }
    }

    private void EnsureReservedSlot()
    {
        if (TargetSlotState != TargetSlotState.Reserved)
        {
            throw new InvalidOperationException($"Expected a reserved target slot, but was {TargetSlotState}.");
        }
    }

    private void EnsureTimestamp(DateTimeOffset timestamp)
    {
        if (timestamp < UpdatedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(timestamp), "Timestamp cannot move backwards.");
        }
    }
}
