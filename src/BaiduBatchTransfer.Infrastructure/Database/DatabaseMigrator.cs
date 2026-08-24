using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed class DatabaseMigrator
{
    public const int LatestVersion = 9;
    private readonly SqliteConnectionFactory factory;
    private readonly DatabaseBackupService backupService;
    private readonly DatabaseIntegrityService integrityService;
    private readonly DatabaseSchemaValidator schemaValidator = new();
    private readonly string appVersion;

    public DatabaseMigrator(SqliteConnectionFactory factory, DatabaseBackupService backupService, string appVersion)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
        integrityService = new DatabaseIntegrityService(factory);
        this.appVersion = string.IsNullOrWhiteSpace(appVersion) ? throw new ArgumentException("App version is required.", nameof(appVersion)) : appVersion;
    }

    public async Task<int> MigrateAsync(CancellationToken cancellationToken = default)
    {
        var existed = File.Exists(factory.DatabasePath) && new FileInfo(factory.DatabasePath).Length > 0;
        if (existed) await integrityService.EnsureHealthyAsync(cancellationToken).ConfigureAwait(false);
        var current = await GetCurrentVersionAsync(cancellationToken).ConfigureAwait(false);
        if (current > LatestVersion) throw new InvalidOperationException($"Database version {current} is newer than supported version {LatestVersion}.");
        if (existed && current < LatestVersion) await backupService.CreateBackupAsync(current, cancellationToken).ConfigureAwait(false);

        for (var next = current + 1; next <= LatestVersion; next++)
        {
            await ApplyMigrationAsync(next, cancellationToken).ConfigureAwait(false);
        }
        await integrityService.EnsureHealthyAsync(cancellationToken).ConfigureAwait(false);
        await using (var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            var schema = await schemaValidator.ValidateAsync(connection, LatestVersion, cancellationToken).ConfigureAwait(false);
            if (!schema.IsValid) throw new InvalidDataException($"Database schema validation failed: {string.Join("; ", schema.Errors)}");
        }
        return LatestVersion;
    }

    private async Task<int> GetCurrentVersionAsync(CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='SchemaVersions';";
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0) return 0;
        await using var version = connection.CreateCommand(); version.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaVersions;";
        return Convert.ToInt32(await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    internal async Task ApplyMigrationAsync(int version, CancellationToken cancellationToken)
    {
        var sql = ReadMigrationSql(version);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction; command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await using var versionCommand = connection.CreateCommand(); versionCommand.Transaction = (SqliteTransaction)transaction;
            versionCommand.CommandText = "INSERT INTO SchemaVersions(Version,AppliedAt,AppVersion) VALUES($version,$applied,$app);";
            versionCommand.Parameters.AddWithValue("$version", version); versionCommand.Parameters.AddWithValue("$applied", DateTimeOffset.UtcNow.ToString("O")); versionCommand.Parameters.AddWithValue("$app", appVersion);
            await versionCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            var schema = await schemaValidator.ValidateAsync(connection, version, cancellationToken, (SqliteTransaction)transaction).ConfigureAwait(false);
            if (!schema.IsValid) throw new InvalidDataException($"Database schema validation failed before commit: {string.Join("; ", schema.Errors)}");
            await using var foreignKeys = connection.CreateCommand();
            foreignKeys.Transaction = (SqliteTransaction)transaction;
            foreignKeys.CommandText = "PRAGMA foreign_key_check;";
            await using var violations = await foreignKeys.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await violations.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("Database relationship validation failed before commit.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
    }

    internal static string ReadMigrationSql(int version)
    {
        var prefix = $"Database.Migrations.{version:000}_";
        var assembly = typeof(DatabaseMigrator).Assembly;
        var resource = assembly.GetManifestResourceNames().SingleOrDefault(x =>
            x.Contains(prefix, StringComparison.Ordinal) && x.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Missing migration resource with prefix {prefix}.");
        using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException($"Cannot read {resource}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
