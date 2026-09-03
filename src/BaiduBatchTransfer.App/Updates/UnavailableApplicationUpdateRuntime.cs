namespace BaiduBatchTransfer.App.Updates;

public sealed class UnavailableApplicationUpdateRuntime : IApplicationUpdateRuntime
{
    private int disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
    }
}
