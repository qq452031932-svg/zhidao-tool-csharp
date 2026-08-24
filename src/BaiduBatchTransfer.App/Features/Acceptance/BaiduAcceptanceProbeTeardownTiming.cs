namespace BaiduBatchTransfer.App.Features.Acceptance;

public static class BaiduAcceptanceProbeTeardownTiming
{
    public static readonly TimeSpan ObservationStopTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan BrowserExitTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan PresenterReportingTimeout = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan CloseRequestTimeout = TimeSpan.FromSeconds(1);
}
