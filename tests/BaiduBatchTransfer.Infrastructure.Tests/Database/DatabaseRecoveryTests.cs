using System.IO.Compression;
using System.Text.Json;
using BaiduBatchTransfer.Infrastructure.Database;

namespace BaiduBatchTransfer.Infrastructure.Tests.Database;

public sealed class DatabaseRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bbt-recovery-{Guid.NewGuid():N}");

    [Fact]
    public async Task ListingReturnsOnlyValidatedCanonicalBackups()
    {
        var (factory, backup, _) = await CreateDatabaseAsync();
        var valid = await backup.CreateBackupAsync(1);
        await File.WriteAllTextAsync(Path.Combine(root, "backups", "database-v1-20000101T000000000Z.db.bak"), "broken");

        var listed = await backup.ListValidatedBackupsAsync();

        Assert.Single(listed);
        Assert.Equal(valid.Path, listed[0].Path);
    }

    [Fact]
    public async Task ListingRejectsUnrelatedHealthySqliteDatabaseWithBackupName()
    {
        var (_, backup, _) = await CreateDatabaseAsync();
        Directory.CreateDirectory(Path.Combine(root, "backups"));
        var unrelated = Path.Combine(root, "backups", "database-v1-20260101T000000000Z.db.bak");
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={unrelated};Pooling=False"))
        {
            await connection.OpenAsync();
            await MigrationTests.ExecuteAsync(connection, "CREATE TABLE Unrelated(Value TEXT);");
        }
        Assert.Empty(await backup.ListValidatedBackupsAsync());
    }

    [Fact]
    public async Task RestoreQuarantinesCurrentDatabaseAndReopensValidatedBackup()
    {
        var (factory, backup, migrator) = await CreateDatabaseAsync();
        await using (var connection = await factory.OpenConnectionAsync()) await MigrationTests.ExecuteAsync(connection, "CREATE TABLE Marker(Value TEXT); INSERT INTO Marker VALUES('backup');");
        var candidate = await backup.CreateBackupAsync(1);
        await using (var connection = await factory.OpenConnectionAsync()) await MigrationTests.ExecuteAsync(connection, "UPDATE Marker SET Value='damaged-newer';");
        var recovery = new DatabaseRecoveryService(factory, backup, migrator, new DatabaseIntegrityService(factory), Path.Combine(root, "quarantine"));

        var result = await recovery.RestoreAsync(candidate.Path);

        Assert.True(File.Exists(result.QuarantinePath));
        await using var verify = await factory.OpenConnectionAsync();
        await using var command = verify.CreateCommand(); command.CommandText = "SELECT Value FROM Marker;";
        Assert.Equal("backup", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task FailedRestorePreservesOriginalDatabase()
    {
        var (factory, backup, migrator) = await CreateDatabaseAsync();
        await using (var connection = await factory.OpenConnectionAsync()) await MigrationTests.ExecuteAsync(connection, "CREATE TABLE Marker(Value TEXT); INSERT INTO Marker VALUES('original');");
        var valid = await backup.CreateBackupAsync(1);
        var tooNew = Path.Combine(root, "backups", "database-v99-20260101T000000000Z.db.bak");
        File.Copy(valid.Path, tooNew);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={tooNew};Pooling=False"))
        {
            await connection.OpenAsync();
            await MigrationTests.ExecuteAsync(connection, "UPDATE SchemaVersions SET Version=99;");
        }
        var recovery = new DatabaseRecoveryService(factory, backup, migrator, new DatabaseIntegrityService(factory), Path.Combine(root, "quarantine"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.RestoreAsync(tooNew));

        await using var verify = await factory.OpenConnectionAsync();
        await using var command = verify.CreateCommand(); command.CommandText = "SELECT Value FROM Marker;";
        Assert.Equal("original", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task RestoreRejectsTraversalAndRequiresClosedConnections()
    {
        var (factory, backup, migrator) = await CreateDatabaseAsync();
        var recovery = new DatabaseRecoveryService(factory, backup, migrator, new DatabaseIntegrityService(factory), Path.Combine(root, "quarantine"));
        await Assert.ThrowsAsync<ArgumentException>(() => recovery.RestoreAsync(Path.Combine(root, "outside.db")));
        var candidate = await backup.CreateBackupAsync(1);
        await using var open = await factory.OpenConnectionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.RestoreAsync(candidate.Path));
    }

    [Fact]
    public async Task ExclusiveMaintenanceBlocksNewConnectionsUntilReleased()
    {
        var (factory, _, _) = await CreateDatabaseAsync();
        Task<Microsoft.Data.Sqlite.SqliteConnection> blocked;
        using (factory.EnterExclusiveMaintenance())
        {
            using (ExecutionContext.SuppressFlow()) blocked = Task.Run(() => factory.OpenConnectionAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => blocked);
        }
        await using var reopened = await factory.OpenConnectionAsync();
        Assert.Equal(System.Data.ConnectionState.Open, reopened.State);
    }

    [Fact]
    public async Task DiagnosticZipUsesAllowlistAndRedactsSecrets()
    {
        Directory.CreateDirectory(root);
        var service = new DiagnosticExportService(Path.Combine(root, "diagnostics"));
        var request = new DiagnosticExportRequest("1.0", 1, ["ok"],
            [new DiagnosticLogEntry("Transfer", "Error", "E1", "Cookie: BAIDUID=x; BDUSS=session-secret; STOKEN=session-secret2 Authorization: Bearer token-secret https://pan.baidu.com/s/abc?pwd=1234 userId=999 11111111-1111-1111-1111-111111111111 提取码=abcd response body: private"),
             new DiagnosticLogEntry("Http", "Error", "E2", "{\"Cookie\":\"SESSION=json-cookie-secret\",\"Authorization\":\"Bearer json-bearer-secret\"}"),
             new DiagnosticLogEntry("Http", "Error", "E3", "{\"Cookie\":[\"SESSION=array-cookie-secret\"],\"Authorization\":[\"Bearer array-bearer-secret\"]}")],
            new Dictionary<string, string> { ["os"] = "Windows", ["databasePath"] = "C:\\secret\\app.db" });

        var path = await service.CreateAsync(request);

        using var zip = ZipFile.OpenRead(path);
        Assert.Equal(new[] { "integrity.txt", "logs.json", "manifest.json", "metadata.json" }, zip.Entries.Select(x => x.FullName).Order());
        var contents = string.Join('\n', zip.Entries.Select(Read));
        Assert.DoesNotContain("secret", contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1234", contents);
        Assert.DoesNotContain("999", contents);
        Assert.DoesNotContain("11111111-1111-1111-1111-111111111111", contents);
        Assert.DoesNotContain("abcd", contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("response body", contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("app.db", contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BDUSS", contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("STOKEN", contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("json-cookie-secret", contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("json-bearer-secret", contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("array-cookie-secret", contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("array-bearer-secret", contents, StringComparison.OrdinalIgnoreCase);

        static string Read(ZipArchiveEntry entry) { using var reader = new StreamReader(entry.Open()); return reader.ReadToEnd(); }
    }

    private async Task<(SqliteConnectionFactory Factory, DatabaseBackupService Backup, DatabaseMigrator Migrator)> CreateDatabaseAsync()
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "app.db"));
        var backup = new DatabaseBackupService(factory, Path.Combine(root, "backups"));
        var migrator = new DatabaseMigrator(factory, backup, "test"); await migrator.MigrateAsync();
        return (factory, backup, migrator);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
