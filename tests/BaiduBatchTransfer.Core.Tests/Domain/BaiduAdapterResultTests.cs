using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Tests.Domain;

public sealed class BaiduAdapterResultTests
{
    [Fact]
    public void TransientFailureCarriesTypedServerRetryDelayWithoutLeakingDiagnostic()
    {
        var result = BaiduAdapterResult<string>.Failure(
            BaiduErrorKind.TransientService,
            "Retry-After: 23; cookie=private",
            TimeSpan.FromSeconds(23));

        Assert.Equal(TimeSpan.FromSeconds(23), result.RetryAfter);
        Assert.DoesNotContain("private", result.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("The service is temporarily unavailable.", result.SafeMessage);
    }

    [Fact]
    public void RejectsNegativeServerRetryDelay()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BaiduAdapterResult<string>.Failure(
                BaiduErrorKind.TransientService,
                null,
                TimeSpan.FromSeconds(-1)));
    }
}
