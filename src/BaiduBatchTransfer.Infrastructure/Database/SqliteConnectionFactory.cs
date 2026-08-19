using System.Data;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed class SqliteConnectionFactory
{
    private int activeConnectionCount;
    private int exclusiveMaintenance;
    private readonly AsyncLocal<bool> maintenanceOwner = new();

    public SqliteConnectionFactory(string databasePath, int busyTimeoutMilliseconds = 5000)
    {
        if (busyTimeoutMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(busyTimeoutMilliseconds));
        DatabasePath = Path.GetFullPath(databasePath ?? throw new ArgumentNullException(nameof(databasePath)));
        BusyTimeoutMilliseconds = busyTimeoutMilliseconds;
    }

    public string DatabasePath { get; }
    public int BusyTimeoutMilliseconds { get; }
    public int ActiveConnectionCount => Volatile.Read(ref activeConnectionCount);

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref exclusiveMaintenance) != 0 && !maintenanceOwner.Value)
            throw new InvalidOperationException("Database recovery is in progress.");
        var directory = Path.GetDirectoryName(DatabasePath) ?? throw new InvalidOperationException("Database path has no directory.");
        Directory.CreateDirectory(directory);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        Interlocked.Increment(ref activeConnectionCount);
        if (Volatile.Read(ref exclusiveMaintenance) != 0 && !maintenanceOwner.Value)
        {
            Interlocked.Decrement(ref activeConnectionCount);
            throw new InvalidOperationException("Database recovery is in progress.");
        }
        var connection = new SqliteConnection(builder.ToString());
        var counted = 1;
        connection.StateChange += (_, args) =>
        {
            if (args.OriginalState == ConnectionState.Open && args.CurrentState == ConnectionState.Closed &&
                Interlocked.Exchange(ref counted, 0) != 0)
                Interlocked.Decrement(ref activeConnectionCount);
        };
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            if (Interlocked.Exchange(ref counted, 0) != 0) Interlocked.Decrement(ref activeConnectionCount);
            throw;
        }
    }

    public IDisposable EnterExclusiveMaintenance()
    {
        if (Interlocked.CompareExchange(ref exclusiveMaintenance, 1, 0) != 0)
            throw new InvalidOperationException("Exclusive database maintenance is already active.");
        if (ActiveConnectionCount != 0)
        {
            Volatile.Write(ref exclusiveMaintenance, 0);
            throw new InvalidOperationException("All active database connections must be closed before recovery.");
        }
        maintenanceOwner.Value = true;
        return new MaintenanceLease(this);
    }

    private sealed class MaintenanceLease : IDisposable
    {
        private SqliteConnectionFactory? owner;
        public MaintenanceLease(SqliteConnectionFactory owner) => this.owner = owner;
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref owner, null);
            if (value is null) return;
            value.maintenanceOwner.Value = false;
            Volatile.Write(ref value.exclusiveMaintenance, 0);
        }
    }

    private async Task ConfigureAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA foreign_keys=ON; PRAGMA busy_timeout={BusyTimeoutMilliseconds};";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var currentMode = connection.CreateCommand(); currentMode.CommandText = "PRAGMA journal_mode;";
        var mode = Convert.ToString(await currentMode.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            await using var enableWal = connection.CreateCommand(); enableWal.CommandText = "PRAGMA journal_mode=WAL;";
            await enableWal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
