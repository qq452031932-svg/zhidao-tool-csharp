namespace BaiduBatchTransfer.App.Updates;

public sealed class VelopackUpdateRuntime : IApplicationUpdateRuntime
{
    private int disposed;

    private VelopackUpdateRuntime()
    {
    }

    public static IApplicationUpdateRuntime Create(ApplicationBuildInfo buildInfo)
    {
        ArgumentNullException.ThrowIfNull(buildInfo);
        return buildInfo.IsFeedConfigured
            ? new VelopackUpdateRuntime()
            : new UnavailableApplicationUpdateRuntime();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
    }
}
