namespace BaiduBatchTransfer.Core.Abstractions;

public interface IUnitOfWorkFactory
{
    Task<IUnitOfWork> BeginAsync(CancellationToken cancellationToken = default);
}
