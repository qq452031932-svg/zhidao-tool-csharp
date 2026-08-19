using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Tests.Scheduling;

public sealed class RetryPolicyTests
{
    [Fact]
    public void TransientFailureAllowsExactlyThreeRetriesWithControlledBackoff()
    {
        var policy = new RetryPolicy([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)]);

        Assert.Equal(TimeSpan.FromSeconds(1), policy.Decide(BaiduErrorKind.TransientService, 0).Delay);
        Assert.Equal(TimeSpan.FromSeconds(2), policy.Decide(BaiduErrorKind.TransientService, 1).Delay);
        Assert.Equal(TimeSpan.FromSeconds(4), policy.Decide(BaiduErrorKind.TransientService, 2).Delay);
        Assert.False(policy.Decide(BaiduErrorKind.TransientService, 3).ShouldRetry);
    }

    [Fact]
    public void ServerWaitOverridesShorterClientBackoff()
    {
        var policy = new RetryPolicy([TimeSpan.FromSeconds(1)]);

        var decision = policy.Decide(BaiduErrorKind.TransientService, 0, TimeSpan.FromSeconds(30));

        Assert.True(decision.ShouldRetry);
        Assert.Equal(TimeSpan.FromSeconds(30), decision.Delay);
    }

    [Fact]
    public void ServerWaitIsClampedToMaximumDelaySupportedByTaskDelay()
    {
        var decision = RetryPolicy.Default.Decide(
            BaiduErrorKind.TransientService,
            completedRetries: 0,
            serverWait: TimeSpan.MaxValue);

        Assert.True(decision.ShouldRetry);
        Assert.Equal(RetryPolicy.MaximumSupportedDelay, decision.Delay);
    }

    [Theory]
    [InlineData(BaiduErrorKind.InvalidLink)]
    [InlineData(BaiduErrorKind.InvalidExtractionCode)]
    [InlineData(BaiduErrorKind.ExpiredLogin)]
    [InlineData(BaiduErrorKind.VerificationRequired)]
    [InlineData(BaiduErrorKind.InsufficientCapacity)]
    [InlineData(BaiduErrorKind.InvalidTargetPath)]
    [InlineData(BaiduErrorKind.UnknownResponse)]
    public void DefiniteAndUnknownFailuresAreNeverRetried(BaiduErrorKind errorKind)
    {
        Assert.False(RetryPolicy.Default.Decide(errorKind, 0).ShouldRetry);
    }
}
