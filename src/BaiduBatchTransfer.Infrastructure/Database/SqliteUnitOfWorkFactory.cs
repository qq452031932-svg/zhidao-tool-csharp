using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed class SqliteUnitOfWorkFactory(SqliteConnectionFactory connectionFactory) : IUnitOfWorkFactory
{
    private readonly SqliteConnectionFactory connectionFactory =
        connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    public async Task<IUnitOfWork> BeginAsync(CancellationToken cancellationToken = default) =>
        await SqliteUnitOfWork.BeginAsync(connectionFactory, cancellationToken).ConfigureAwait(false);
}
