using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed record DatabaseBackupDescriptor(string Path, int SchemaVersion, DateTimeOffset CreatedAt, bool IsValid);

public sealed partial class DatabaseBackupService
{
    private readonly SqliteConnectionFactory factory;
    private readonly DatabaseSchemaValidator schemaValidator = new();

    public DatabaseBackupService(SqliteConnectionFactory factory, string backupRoot)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        BackupRoot = Path.GetFullPath(backupRoot ?? throw new ArgumentNullException(nameof(backupRoot)));
    }

    public string BackupRoot { get; }

    public async Task<DatabaseBackupDescriptor> CreateBackupAsync(int schemaVersion, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(BackupRoot);
        var createdAt = DateTimeOffset.UtcNow;
        var path = Path.Combine(BackupRoot, $"database-v{schemaVersion}-{createdAt:yyyyMMddTHHmmssfffZ}.db.bak");
        path = EnsureDirectChild(path, BackupRoot);
        await using var source = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var destinationString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        await using var destination = new SqliteConnection(destinationString);
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(destination);
        var integrity = await QuickCheckAsync(destination, cancellationToken).ConfigureAwait(false);
        if (!integrity || !(await schemaValidator.ValidateAsync(destination, schemaVersion, cancellationToken).ConfigureAwait(false)).IsValid)
            throw new InvalidDataException("The SQLite online backup is not a valid application database.");
        return new(path, schemaVersion, createdAt, true);
    }

    public async Task<IReadOnlyList<DatabaseBackupDescriptor>> ListValidatedBackupsAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(BackupRoot)) return [];
        var result = new List<DatabaseBackupDescriptor>();
        foreach (var candidate in Directory.EnumerateFiles(BackupRoot).OrderByDescending(File.GetCreationTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = EnsureDirectChild(candidate, BackupRoot);
            var match = BackupName().Match(Path.GetFileName(path));
            if (!match.Success) continue;
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var expectedVersion) ||
                !DateTimeOffset.TryParseExact(match.Groups[2].Value, "yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created)) continue;
            try
            {
                var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
                await using var connection = new SqliteConnection(builder.ToString());
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                if (!await QuickCheckAsync(connection, cancellationToken).ConfigureAwait(false) ||
                    !(await schemaValidator.ValidateAsync(connection, expectedVersion, cancellationToken).ConfigureAwait(false)).IsValid) continue;
                result.Add(new(path, expectedVersion, created, true));
            }
            catch (SqliteException) { }
            catch (InvalidDataException) { }
        }
        return result;
    }

    internal async Task<bool> ValidateBackupAsync(string path, CancellationToken cancellationToken)
    {
        path = EnsureDirectChild(path, BackupRoot);
        if (!File.Exists(path)) return false;
        var match = BackupName().Match(Path.GetFileName(path));
        if (!match.Success) return false;
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var expectedVersion) ||
            !DateTimeOffset.TryParseExact(match.Groups[2].Value, "yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _)) return false;
        try
        {
            var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return await QuickCheckAsync(connection, cancellationToken).ConfigureAwait(false) &&
                (await schemaValidator.ValidateAsync(connection, expectedVersion, cancellationToken).ConfigureAwait(false)).IsValid;
        }
        catch (SqliteException) { return false; }
    }

    internal static string EnsureDirectChild(string path, string root)
    {
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var canonicalPath = Path.GetFullPath(path);
        if (!canonicalPath.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(canonicalPath)?.TrimEnd(Path.DirectorySeparatorChar), canonicalRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Path must be a direct child of the configured root.", nameof(path));
        return canonicalPath;
    }

    private static async Task<bool> QuickCheckAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.CommandText = "PRAGMA quick_check;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var any = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            any = true;
            if (!string.Equals(reader.GetString(0), "ok", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return any;
    }

    [GeneratedRegex("^database-v(\\d+)-(\\d{8}T\\d{9}Z)\\.db\\.bak$", RegexOptions.CultureInvariant)]
    private static partial Regex BackupName();
}
