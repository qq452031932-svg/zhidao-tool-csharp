using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Tests.Domain;

public sealed class TargetSlotTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 19, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AvailableSlots_SubtractsFulfilledAndReservedCounts()
    {
        var allocation = CreateAllocation(effectiveTarget: 5, fulfilled: 2, reserved: 1);

        Assert.Equal(2, allocation.AvailableSlots);
    }

    [Fact]
    public void ZeroTarget_HasNoAvailableSlots()
    {
        var allocation = CreateAllocation(effectiveTarget: 0);

        Assert.Equal(0, allocation.AvailableSlots);
        Assert.Throws<InvalidOperationException>(allocation.ReserveSlot);
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(1, -1, 0)]
    [InlineData(1, 0, -1)]
    [InlineData(2, 2, 1)]
    [InlineData(int.MaxValue, int.MaxValue, 1)]
    public void InvalidCounters_AreRejected(int effectiveTarget, int fulfilled, int reserved)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateAllocation(effectiveTarget, fulfilled, reserved));
    }

    [Fact]
    public void ReserveSlot_IncrementsReservedCount()
    {
        var allocation = CreateAllocation(effectiveTarget: 2);

        allocation.ReserveSlot();

        Assert.Equal(1, allocation.ReservedSlotCount);
        Assert.Equal(1, allocation.AvailableSlots);
    }

    [Fact]
    public void MarkFulfilled_ConvertsExactlyOneReservationToFulfilled()
    {
        var allocation = CreateAllocation(effectiveTarget: 2, reserved: 2);

        allocation.MarkFulfilled();

        Assert.Equal(1, allocation.ReservedSlotCount);
        Assert.Equal(1, allocation.FulfilledCount);
        Assert.Equal(0, allocation.AvailableSlots);
    }

    [Fact]
    public void DefinitiveFailure_ReleasesExactlyOneReservation()
    {
        var allocation = CreateAllocation(effectiveTarget: 2, reserved: 2);

        allocation.MarkDefinitivelyFailed();

        Assert.Equal(1, allocation.ReservedSlotCount);
        Assert.Equal(0, allocation.FulfilledCount);
        Assert.Equal(1, allocation.AvailableSlots);
    }

    [Fact]
    public void ResultPending_RetainsReservedOperationSlot()
    {
        var operation = TransferOperation.CreateReserved(
            Guid.Parse("20000000-0000-0000-0000-000000000001"),
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            Guid.Parse("90000000-0000-0000-0000-000000000001"),
            CreatedAt);
        operation.MarkPreparing(CreatedAt.AddSeconds(1));
        operation.MarkSubmitting(CreatedAt.AddSeconds(2));

        operation.MarkResultPending("response timeout", CreatedAt.AddSeconds(3));

        Assert.Equal(TargetSlotState.Reserved, operation.TargetSlotState);
    }

    [Fact]
    public void AllocationResultPending_RetainsReservedCount()
    {
        var allocation = CreateAllocation(effectiveTarget: 1, reserved: 1);

        allocation.MarkResultPending();

        Assert.Equal(1, allocation.ReservedSlotCount);
        Assert.Equal(0, allocation.FulfilledCount);
        Assert.Equal(0, allocation.AvailableSlots);
    }

    [Fact]
    public void ReservationCannotBeConsumedOrReleasedTwice()
    {
        var fulfilled = CreateAllocation(effectiveTarget: 1, reserved: 1);
        fulfilled.MarkFulfilled();

        var failed = CreateAllocation(effectiveTarget: 1, reserved: 1);
        failed.MarkDefinitivelyFailed();

        Assert.Throws<InvalidOperationException>(fulfilled.MarkFulfilled);
        Assert.Throws<InvalidOperationException>(failed.MarkDefinitivelyFailed);
    }

    private static AccountAllocation CreateAllocation(
        int effectiveTarget,
        int fulfilled = 0,
        int reserved = 0) => new(
        Guid.Parse("40000000-0000-0000-0000-000000000001"),
        Guid.Parse("10000000-0000-0000-0000-000000000001"),
        Guid.Parse("90000000-0000-0000-0000-000000000001"),
        effectiveTarget,
        fulfilled,
        reserved);
}
