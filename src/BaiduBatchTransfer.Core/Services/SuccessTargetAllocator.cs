using System.Numerics;

namespace BaiduBatchTransfer.Core.Services;

public sealed class SuccessTargetAllocator
{
    public SuccessTargetAccount? SelectAccount(
        IEnumerable<SuccessTargetAccount> accounts,
        IReadOnlySet<Guid>? eligibleAccountIds = null)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        SuccessTargetAccount? selected = null;
        foreach (var account in accounts)
        {
            if (account.OriginalTarget == 0 ||
                account.AvailableSlots == 0 ||
                (eligibleAccountIds is not null && !eligibleAccountIds.Contains(account.AccountId)))
            {
                continue;
            }

            if (selected is null || Compare(account, selected) < 0)
            {
                selected = account;
            }
        }

        return selected;
    }

    private static int Compare(SuccessTargetAccount left, SuccessTargetAccount right)
    {
        var leftProgress = new BigInteger(left.FulfilledCount + (long)left.ReservedSlotCount);
        var rightProgress = new BigInteger(right.FulfilledCount + (long)right.ReservedSlotCount);
        var ratioComparison = (leftProgress * right.OriginalTarget)
            .CompareTo(rightProgress * left.OriginalTarget);
        return ratioComparison != 0
            ? ratioComparison
            : left.PriorityOrder.CompareTo(right.PriorityOrder);
    }
}

public sealed record SuccessTargetAccount
{
    public SuccessTargetAccount(
        Guid accountId,
        int originalTarget,
        int effectiveTarget,
        int fulfilledCount,
        int reservedSlotCount,
        int priorityOrder)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(originalTarget);
        ArgumentOutOfRangeException.ThrowIfNegative(effectiveTarget);
        ArgumentOutOfRangeException.ThrowIfNegative(fulfilledCount);
        ArgumentOutOfRangeException.ThrowIfNegative(reservedSlotCount);
        ArgumentOutOfRangeException.ThrowIfNegative(priorityOrder);

        if (effectiveTarget > originalTarget)
        {
            throw new ArgumentOutOfRangeException(nameof(effectiveTarget), "The effective target cannot exceed the original target.");
        }

        if ((long)fulfilledCount + reservedSlotCount > effectiveTarget)
        {
            throw new ArgumentOutOfRangeException(nameof(reservedSlotCount), "Fulfilled and reserved counts cannot exceed the effective target.");
        }

        AccountId = accountId;
        OriginalTarget = originalTarget;
        EffectiveTarget = effectiveTarget;
        FulfilledCount = fulfilledCount;
        ReservedSlotCount = reservedSlotCount;
        PriorityOrder = priorityOrder;
    }

    public Guid AccountId { get; }

    public int OriginalTarget { get; }

    public int EffectiveTarget { get; }

    public int FulfilledCount { get; }

    public int ReservedSlotCount { get; }

    public int PriorityOrder { get; }

    public int AvailableSlots => EffectiveTarget - FulfilledCount - ReservedSlotCount;

    public SuccessTargetAccount AddSimulatedFulfillment() => new(
        AccountId,
        OriginalTarget,
        EffectiveTarget,
        checked(FulfilledCount + 1),
        ReservedSlotCount,
        PriorityOrder);
}
