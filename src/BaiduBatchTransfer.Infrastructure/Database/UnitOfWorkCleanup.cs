using System.Runtime.ExceptionServices;

namespace BaiduBatchTransfer.Infrastructure.Database;

internal static class UnitOfWorkCleanup
{
    internal static async ValueTask DisposeAsync(
        bool rollbackRequired,
        Func<ValueTask> rollback,
        Func<ValueTask> disposeTransaction,
        Func<ValueTask> disposeConnection)
    {
        var errors = new List<Exception>();
        try
        {
            if (rollbackRequired)
            {
                try { await rollback().ConfigureAwait(false); }
                catch (Exception error) { errors.Add(error); }
            }
        }
        finally
        {
            try
            {
                try { await disposeTransaction().ConfigureAwait(false); }
                catch (Exception error) { errors.Add(error); }
            }
            finally
            {
                try { await disposeConnection().ConfigureAwait(false); }
                catch (Exception error) { errors.Add(error); }
            }
        }

        if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException("Unit of work cleanup failed.", errors);
    }
}
