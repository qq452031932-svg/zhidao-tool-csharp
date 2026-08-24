namespace BaiduBatchTransfer.App.Features.Acceptance;

public sealed class BaiduAcceptanceProbeObservationCoordinator : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly object gate = new();
    private readonly HashSet<Task> tasks = [];

    public void Track(Func<CancellationToken, Task> observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        Task task;
        lock (gate)
        {
            if (cancellation.IsCancellationRequested) return;
            task = RunAsync(observation);
            tasks.Add(task);
        }
        _ = task.ContinueWith(_ =>
        {
            lock (gate) tasks.Remove(task);
        }, TaskScheduler.Default);
    }

    public async Task StopAsync(TimeSpan timeout)
    {
        cancellation.Cancel();
        Task[] pending;
        lock (gate) pending = tasks.ToArray();
        try { await Task.WhenAll(pending).WaitAsync(timeout).ConfigureAwait(false); }
        catch (Exception) { }
    }

    public void Dispose() => cancellation.Dispose();

    private async Task RunAsync(Func<CancellationToken, Task> observation)
    {
        try { await observation(cancellation.Token).ConfigureAwait(false); }
        catch (Exception) { }
    }
}
