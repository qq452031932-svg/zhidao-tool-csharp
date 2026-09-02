using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Tests;

public sealed class AccountShareAnalyticsTests
{
    [Fact]
    public void ObservationRequiresExactlyTheThreeKnownMetrics()
    {
        var duplicate = new[]
        {
            Metric(AccountShareMetric.Transfer, 1),
            Metric(AccountShareMetric.Transfer, 2),
            Metric(AccountShareMetric.Download, 3),
        };

        Assert.Throws<ArgumentException>(() => Observation(duplicate));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Metric((AccountShareMetric)99, 1));
    }

    [Fact]
    public void CountsMustBeNonNegativeWhenPresent()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Metric(AccountShareMetric.Transfer, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AccountShareMetricObservation(
                AccountShareMetric.Transfer,
                1,
                new Dictionary<DateOnly, long> { [new(2026, 9, 2)] = -1 }));
    }

    [Fact]
    public void ConstructionCopiesCallerOwnedCollections()
    {
        var daily = new Dictionary<DateOnly, long> { [new(2026, 9, 2)] = 7 };
        var metrics = new List<AccountShareMetricObservation>
        {
            new(AccountShareMetric.Transfer, 10, daily),
            Metric(AccountShareMetric.View, 20),
            Metric(AccountShareMetric.Download, 30),
        };
        var observation = Observation(metrics);

        daily[new(2026, 9, 2)] = 99;
        metrics.Clear();

        Assert.Equal(3, observation.Metrics.Count);
        Assert.Equal(7, observation.Metrics[0].DirectDailyValues![new(2026, 9, 2)]);
    }

    [Fact]
    public void SensitiveValuesAreRedactedFromDiagnostics()
    {
        var observation = Observation(
            [
                Metric(AccountShareMetric.Transfer, 123),
                Metric(AccountShareMetric.View, 456),
                Metric(AccountShareMetric.Download, 789),
            ]);
        var page = new AccountSharePage([observation], "private-cursor");

        var diagnostic = $"{observation} {page}";
        Assert.DoesNotContain("private-share-id", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private-title", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private-link", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private-cursor", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("123", diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://pan.baidu.com/s/private-link")]
    [InlineData("https://example.com/s/private-link")]
    [InlineData("https://user@pan.baidu.com/s/private-link")]
    public void ObservationRejectsUnsafeShareUris(string uri)
    {
        Assert.Throws<ArgumentException>(() => Observation(
            [
                Metric(AccountShareMetric.Transfer, 1),
                Metric(AccountShareMetric.View, 2),
                Metric(AccountShareMetric.Download, 3),
            ],
            new Uri(uri)));
    }

    private static AccountShareMetricObservation Metric(
        AccountShareMetric metric,
        long? total) => new(metric, total);

    private static AccountShareObservation Observation(
        IReadOnlyList<AccountShareMetricObservation> metrics,
        Uri? uri = null) => new(
            "private-share-id",
            "private-title",
            uri ?? new Uri("https://pan.baidu.com/s/private-link"),
            new DateTimeOffset(2026, 9, 2, 1, 2, 3, TimeSpan.Zero),
            AccountShareRemoteStatus.Valid,
            metrics);
}
