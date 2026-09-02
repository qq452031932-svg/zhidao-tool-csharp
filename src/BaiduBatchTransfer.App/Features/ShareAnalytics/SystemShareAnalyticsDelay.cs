namespace BaiduBatchTransfer.App.Features.ShareAnalytics;

public sealed class SystemShareAnalyticsDelay : IShareAnalyticsDelay
{
    public Task WaitAsync(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token);
}
