using System.Collections.Immutable;
using System.Numerics;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Jobs;

public sealed record BatchWorkloadAccountInput(
    Guid AccountId,
    int PriorityOrder,
    string DisplayName,
    bool IsSelected,
    bool ShareEnabled,
    long DistributionCount,
    bool IsAvailable = true)
{
    public override string ToString() =>
        $"BatchWorkloadAccountInput {{ Identity = [REDACTED], PriorityOrder = {PriorityOrder}, IsSelected = {IsSelected}, ShareEnabled = {ShareEnabled}, DistributionCount = {DistributionCount}, IsAvailable = {IsAvailable}, DisplayName = [REDACTED] }}";
}

public sealed record BatchAccountWorkload(
    Guid AccountId,
    int OutputOrdinal,
    string OutputLabel,
    long PlannedTransferCount,
    long PlannedShareCount,
    bool ShareEnabled)
{
    public override string ToString() =>
        $"BatchAccountWorkload {{ Identity = [REDACTED], OutputOrdinal = {OutputOrdinal}, PlannedTransferCount = {PlannedTransferCount}, PlannedShareCount = {PlannedShareCount}, ShareEnabled = {ShareEnabled}, OutputLabel = [REDACTED] }}";
}

public sealed class BatchWorkloadPlan
{
    public BatchWorkloadPlan(IEnumerable<BatchAccountWorkload> accounts)
    {
        Accounts = (accounts ?? throw new ArgumentNullException(nameof(accounts))).ToImmutableArray();
        checked
        {
            TransferCount = Accounts.Sum(account => account.PlannedTransferCount);
            ShareCount = Accounts.Sum(account => account.PlannedShareCount);
        }
    }

    public ImmutableArray<BatchAccountWorkload> Accounts { get; }
    public long TransferCount { get; }
    public long ShareCount { get; }
    public long TotalActionCount => checked(TransferCount + ShareCount);

    public override string ToString() =>
        $"BatchWorkloadPlan {{ Accounts = {Accounts.Length}, TransferCount = {TransferCount}, ShareCount = {ShareCount}, TotalActionCount = {TotalActionCount} }}";
}

public sealed class BatchWorkloadPlanner
{
    public BatchWorkloadPlan Plan(
        long resourceCount,
        TransferMode transferMode,
        IReadOnlyList<BatchWorkloadAccountInput> accounts,
        IReadOnlyDictionary<Guid, long>? mappedAccountCounts = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(resourceCount);
        ArgumentNullException.ThrowIfNull(accounts);
        var selected = accounts.Where(account => account.IsSelected)
            .OrderBy(account => account.PriorityOrder)
            .ToArray();
        if (selected.Length == 0)
            throw new BatchDraftValidationException("请至少选择一个参与账号。");
        if (selected.Any(account => account.AccountId == Guid.Empty || !account.IsAvailable))
            throw new BatchDraftValidationException("所选账号已失效、停用或不可用。");
        if (selected.Select(account => account.AccountId).Distinct().Count() != selected.Length ||
            selected.Select(account => account.PriorityOrder).Distinct().Count() != selected.Length)
            throw new BatchDraftValidationException("参与账号和顺序必须唯一。");
        if (selected.Any(account => account.DistributionCount < 0))
            throw new BatchDraftValidationException("账号工作量不能为负数。");

        var labels = ResultColumnLayoutBuilder.CreateSafeAccountLabels(selected.Select(x => x.DisplayName).ToArray());
        var transfers = transferMode switch
        {
            TransferMode.Replication => selected.ToDictionary(account => account.AccountId, _ => resourceCount),
            TransferMode.Distribution => PlanDistribution(resourceCount, selected, mappedAccountCounts),
            _ => throw new BatchDraftValidationException("转存模式无效。"),
        };

        return new BatchWorkloadPlan(selected.Select((account, index) =>
        {
            var transferCount = transfers[account.AccountId];
            return new BatchAccountWorkload(
                account.AccountId,
                index + 1,
                labels[index],
                transferCount,
                account.ShareEnabled ? transferCount : 0,
                account.ShareEnabled);
        }));
    }

    private static Dictionary<Guid, long> PlanDistribution(
        long resourceCount,
        IReadOnlyList<BatchWorkloadAccountInput> selected,
        IReadOnlyDictionary<Guid, long>? mappedAccountCounts)
    {
        var selectedIds = selected.Select(account => account.AccountId).ToHashSet();
        var result = selected.ToDictionary(account => account.AccountId, _ => 0L);
        long mappedTotal = 0;
        if (mappedAccountCounts is not null)
        {
            foreach (var pair in mappedAccountCounts)
            {
                if (pair.Value < 0 || !selectedIds.Contains(pair.Key))
                    throw new BatchDraftValidationException("逐行账号对应已失效或不在所选账号中。");
                result[pair.Key] = checked(result[pair.Key] + pair.Value);
                mappedTotal = checked(mappedTotal + pair.Value);
            }
        }
        if (mappedTotal > resourceCount)
            throw new BatchDraftValidationException("逐行账号对应数量超过资源数。");

        long requestedTotal = 0;
        foreach (var account in selected)
            requestedTotal = checked(requestedTotal + account.DistributionCount);
        if (requestedTotal == resourceCount && selected.All(account => account.DistributionCount >= result[account.AccountId]))
            return selected.ToDictionary(account => account.AccountId, account => account.DistributionCount);

        var remaining = resourceCount - mappedTotal;
        if (remaining == 0) return result;
        var weights = selected.Select(account => Math.Max(0, account.DistributionCount - result[account.AccountId])).ToArray();
        var totalWeight = weights.Aggregate(BigInteger.Zero, (sum, value) => sum + value);
        if (totalWeight == BigInteger.Zero)
        {
            result[selected[0].AccountId] = checked(result[selected[0].AccountId] + remaining);
            return result;
        }

        var assigned = 0L;
        var remainders = new (int Index, BigInteger Remainder)[selected.Count];
        for (var index = 0; index < selected.Count; index++)
        {
            var quotient = BigInteger.DivRem(new BigInteger(weights[index]) * remaining, totalWeight, out var remainder);
            var value = checked((long)quotient);
            result[selected[index].AccountId] = checked(result[selected[index].AccountId] + value);
            assigned = checked(assigned + value);
            remainders[index] = (index, remainder);
        }
        foreach (var item in remainders.OrderByDescending(item => item.Remainder).ThenBy(item => item.Index)
                     .Take(checked((int)(remaining - assigned))))
            result[selected[item.Index].AccountId]++;
        return result;
    }
}
