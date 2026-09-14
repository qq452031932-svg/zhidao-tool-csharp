namespace BaiduBatchTransfer.App.Features.Common;

public interface IBackgroundOperationRunner
{
    Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken);
}

public sealed class BackgroundOperationRunner : IBackgroundOperationRunner
{
    public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        Task.Run(() => operation(cancellationToken), cancellationToken);
}
