using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Database.Repositories;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed class SqliteUnitOfWork : IUnitOfWork
{
    private readonly SqliteConnection connection;
    private readonly SqliteTransaction transaction;
    private bool completed;

    private SqliteUnitOfWork(SqliteConnection connection, SqliteTransaction transaction)
    {
        this.connection = connection; this.transaction = transaction;
        Imports = new SqliteImportRepository(connection, transaction);
        Jobs = new SqliteJobRepository(connection, transaction);
        Accounts = new SqliteAccountRepository(connection, transaction);
    }

    public IImportRepository Imports { get; }
    public IJobRepository Jobs { get; }
    public IAccountRepository Accounts { get; }

    public static async Task<SqliteUnitOfWork> BeginAsync(SqliteConnectionFactory factory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(connection, connection.BeginTransaction(deferred: true));
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive(); await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); completed = true;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive(); await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false); completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!completed)
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (InvalidOperationException) { }
            completed = true;
        }
        await transaction.DisposeAsync().ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private void EnsureActive()
    {
        if (completed) throw new InvalidOperationException("The unit of work has already completed.");
    }
}
