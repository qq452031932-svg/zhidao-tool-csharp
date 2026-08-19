using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed record DatabaseIntegrityResult(bool IsHealthy, IReadOnlyList<string> Output);

public sealed class DatabaseIntegrityException : Exception
{
    public DatabaseIntegrityException(string message, Exception? innerException = null) : base(message, innerException) { }
}

public sealed class DatabaseIntegrityService
{
    private readonly SqliteConnectionFactory factory;
    public DatabaseIntegrityService(SqliteConnectionFactory factory) => this.factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async Task<DatabaseIntegrityResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand(); command.CommandText = "PRAGMA quick_check;";
            var output = new List<string>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) output.Add(reader.GetString(0));
            return new(output.Count == 1 && string.Equals(output[0], "ok", StringComparison.OrdinalIgnoreCase), output);
        }
        catch (SqliteException exception)
        {
            return new(false, [$"SQLite integrity check failed: {exception.SqliteErrorCode}"]);
        }
        catch (InvalidOperationException exception)
        {
            return new(false, [$"SQLite integrity check failed: {exception.GetType().Name}"]);
        }
    }

    public async Task EnsureHealthyAsync(CancellationToken cancellationToken = default)
    {
        var result = await CheckAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsHealthy) throw new DatabaseIntegrityException(string.Join(Environment.NewLine, result.Output));
    }
}
