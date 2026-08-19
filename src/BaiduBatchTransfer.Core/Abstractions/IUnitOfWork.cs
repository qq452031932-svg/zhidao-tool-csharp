namespace BaiduBatchTransfer.Core.Abstractions;

public interface IUnitOfWork : IAsyncDisposable
{
    IImportRepository Imports { get; }
    IJobRepository Jobs { get; }
    IAccountRepository Accounts { get; }
    Task CommitAsync(CancellationToken cancellationToken = default);
    Task RollbackAsync(CancellationToken cancellationToken = default);
}
