namespace BaiduBatchTransfer.Core.Domain;

public sealed class AccountAllocation
{
    public AccountAllocation(
        Guid id,
        Guid batchJobId,
        Guid accountId,
        int effectiveTargetSuccessCount,
        int fulfilledCount = 0,
        int reservedSlotCount = 0)
    {
        ValidateCounts(effectiveTargetSuccessCount, fulfilledCount, reservedSlotCount);
        Id = id;
        BatchJobId = batchJobId;
        AccountId = accountId;
        EffectiveTargetSuccessCount = effectiveTargetSuccessCount;
        FulfilledCount = fulfilledCount;
        ReservedSlotCount = reservedSlotCount;
    }

    public Guid Id { get; }

    public Guid BatchJobId { get; }

    public Guid AccountId { get; }

    public int EffectiveTargetSuccessCount { get; private set; }

    public int FulfilledCount { get; private set; }

    public int ReservedSlotCount { get; private set; }

    public int AvailableSlots => Math.Max(
        0,
        EffectiveTargetSuccessCount - FulfilledCount - ReservedSlotCount);

    public void ChangeEffectiveTarget(int effectiveTargetSuccessCount)
    {
        ValidateCounts(effectiveTargetSuccessCount, FulfilledCount, ReservedSlotCount);
        EffectiveTargetSuccessCount = effectiveTargetSuccessCount;
    }

    public void ReserveSlot()
    {
        if (AvailableSlots == 0)
        {
            throw new InvalidOperationException("No target slots are available.");
        }

        ReservedSlotCount++;
        EnsureCurrentCountsValid();
    }

    public void MarkFulfilled()
    {
        EnsureReservationExists();
        ReservedSlotCount--;
        FulfilledCount++;
        EnsureCurrentCountsValid();
    }

    public void MarkDefinitivelyFailed()
    {
        EnsureReservationExists();
        ReservedSlotCount--;
        EnsureCurrentCountsValid();
    }

    public void MarkResultPending()
    {
        EnsureReservationExists();
        EnsureCurrentCountsValid();
    }

    private void EnsureReservationExists()
    {
        if (ReservedSlotCount == 0)
        {
            throw new InvalidOperationException("No reserved target slot exists.");
        }
    }

    private void EnsureCurrentCountsValid() =>
        ValidateCounts(EffectiveTargetSuccessCount, FulfilledCount, ReservedSlotCount);

    private static void ValidateCounts(
        int effectiveTargetSuccessCount,
        int fulfilledCount,
        int reservedSlotCount)
    {
        if (effectiveTargetSuccessCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(effectiveTargetSuccessCount));
        }

        if (fulfilledCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fulfilledCount));
        }

        if (reservedSlotCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reservedSlotCount));
        }

        if (fulfilledCount > effectiveTargetSuccessCount - reservedSlotCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reservedSlotCount),
                "Fulfilled and reserved counts cannot exceed the effective target.");
        }
    }
}
