using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed record DatabaseBackupDescriptor(string Path, int SchemaVersion, DateTimeOffset CreatedAt, bool IsValid);

public sealed partial class DatabaseBackupService
{
    private static readonly string[] RequiredTables = ["ImportSessions", "StagedImportRows", "Accounts", "AccountRules", "BatchJobs", "ResourceItems", "TransferOperations", "AccountAllocations", "ExecutionLogs", "SchemaVersions"];
    private readonly SqliteConnectionFactory factory;

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
        if (!integrity || !await ValidateApplicationIdentityAsync(destination, schemaVersion, cancellationToken).ConfigureAwait(false))
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
            try
            {
                var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
                await using var connection = new SqliteConnection(builder.ToString());
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                var expectedVersion = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                if (!await QuickCheckAsync(connection, cancellationToken).ConfigureAwait(false) ||
                    !await ValidateApplicationIdentityAsync(connection, expectedVersion, cancellationToken).ConfigureAwait(false)) continue;
                var created = DateTimeOffset.ParseExact(match.Groups[2].Value, "yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
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
        try
        {
            var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var expectedVersion = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            return await QuickCheckAsync(connection, cancellationToken).ConfigureAwait(false) &&
                await ValidateApplicationIdentityAsync(connection, expectedVersion, cancellationToken).ConfigureAwait(false);
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

    private static async Task<bool> ValidateApplicationIdentityAsync(SqliteConnection connection, int expectedVersion, CancellationToken cancellationToken)
    {
        await using var tablesCommand = connection.CreateCommand(); tablesCommand.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
        var tables = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await tablesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) tables.Add(reader.GetString(0));
        if (!tables.Contains("SchemaVersions")) return false;
        if (expectedVersion >= 1 && RequiredTables.Any(table => !tables.Contains(table))) return false;
        await using var versionCommand = connection.CreateCommand(); versionCommand.CommandText = "SELECT COALESCE(MAX(Version),-1) FROM SchemaVersions;";
        return Convert.ToInt32(await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == expectedVersion;
    }

    [GeneratedRegex("^database-v(\\d+)-(\\d{8}T\\d{9}Z)\\.db\\.bak$", RegexOptions.CultureInvariant)]
    private static partial Regex BackupName();
}
