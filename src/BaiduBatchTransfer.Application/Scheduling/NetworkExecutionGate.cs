namespace BaiduBatchTransfer.Application.Scheduling;

public sealed class NetworkExecutionGate
{
    private readonly object sync = new();
    private bool networkAvailable;
    private bool resumeConfirmed;

    public NetworkExecutionGate(bool initiallyOnline)
    {
        networkAvailable = initiallyOnline;
        resumeConfirmed = initiallyOnline;
    }

    public bool CanAcquireNewWork
    {
        get { lock (sync) return networkAvailable && resumeConfirmed; }
    }

    public void ReportOffline()
    {
        lock (sync)
        {
            networkAvailable = false;
            resumeConfirmed = false;
        }
    }

    public void ReportNetworkAvailable()
    {
        lock (sync) networkAvailable = true;
    }

    public void ConfirmResume()
    {
        lock (sync)
        {
            if (!networkAvailable)
                throw new InvalidOperationException("Network availability must be observed before resuming.");
            resumeConfirmed = true;
        }
    }
}
