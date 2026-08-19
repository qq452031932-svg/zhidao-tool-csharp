using BaiduBatchTransfer.Core.Services;

namespace BaiduBatchTransfer.Core.Tests.Services;

public sealed class ReplicationExpanderTests
{
    [Fact]
    public void OneThousandResourcesAndThreeAccounts_StreamThreeThousandUniqueIntents()
    {
        var accounts = Enumerable.Range(1, 3).Select(Id).ToArray();
        var resources = Enumerable.Range(1, 1_000)
            .Select(sequence => new ReplicationResource(Id(sequence + 10), sequence));

        var intents = new ReplicationExpander().Expand(resources, accounts);

        Assert.False(intents is ICollection<ReplicationIntent>);
        var materialized = intents.ToArray();
        Assert.Equal(3_000, materialized.Length);
        Assert.Equal(3_000, materialized.Select(intent => (intent.ResourceId, intent.AccountId)).Distinct().Count());
        Assert.Equal(
            new[] { accounts[0], accounts[1], accounts[2] },
            materialized.Take(3).Select(intent => intent.AccountId));
        Assert.Equal(
            Enumerable.Range(1, 3).Select(value => (long)value),
            materialized.Take(3).Select(intent => intent.AllocationOrdinal));
    }

    [Fact]
    public void CountEstimation_IsChecked()
    {
        Assert.Equal(3_000, ReplicationExpander.EstimateIntentCount(1_000, 3));
        Assert.Throws<OverflowException>(() => ReplicationExpander.EstimateIntentCount(long.MaxValue, 2));
    }

    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");
}
