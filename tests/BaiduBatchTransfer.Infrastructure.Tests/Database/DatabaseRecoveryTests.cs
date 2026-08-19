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
    public async Task ListingRejectsSameVersionImpostorWithTableNamesButWrongSchema()
    {
        var (_, backup, _) = await CreateDatabaseAsync(); Directory.CreateDirectory(Path.Combine(root, "backups"));
        var impostor = Path.Combine(root, "backups", "database-v1-20260101T000000000Z.db.bak");
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={impostor};Pooling=False"))
        {
            await connection.OpenAsync();
            foreach (var table in new[] { "ImportSessions", "StagedImportRows", "Accounts", "AccountRules", "BatchJobs", "ResourceItems", "TransferOperations", "AccountAllocations", "ExecutionLogs" })
                await MigrationTests.ExecuteAsync(connection, $"CREATE TABLE {table}(Id TEXT PRIMARY KEY);");
            await MigrationTests.ExecuteAsync(connection, "CREATE TABLE SchemaVersions(Version INTEGER PRIMARY KEY); INSERT INTO SchemaVersions VALUES(1);");
        }
        Assert.Empty(await backup.ListValidatedBackupsAsync());
    }

    [Fact]
    public async Task ListingSkipsOverflowVersionAndImpossibleTimestampWithoutAborting()
    {
        var (_, backup, _) = await CreateDatabaseAsync(); var valid = await backup.CreateBackupAsync(1);
        File.Copy(valid.Path, Path.Combine(root, "backups", "database-v999999999999999999999-20260101T000000000Z.db.bak"));
        File.Copy(valid.Path, Path.Combine(root, "backups", "database-v1-20261399T999999999Z.db.bak"));
        var listed = await backup.ListValidatedBackupsAsync();
        Assert.Single(listed); Assert.Equal(valid.Path, listed[0].Path);
    }

    [Fact]
    public async Task ListingSkipsOversizedStoredSchemaVersionAndRetainsValidBackup()
    {
        var (_, backup, _) = await CreateDatabaseAsync(); var valid = await backup.CreateBackupAsync(1);
        var malformed = Path.Combine(root, "backups", "database-v1-20260101T000000000Z.db.bak"); File.Copy(valid.Path, malformed);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={malformed};Pooling=False"))
        {
            await connection.OpenAsync();
            await MigrationTests.ExecuteAsync(connection, "DROP TABLE SchemaVersions; CREATE TABLE SchemaVersions(Version TEXT PRIMARY KEY,AppliedAt TEXT NOT NULL,AppVersion TEXT NOT NULL); INSERT INTO SchemaVersions VALUES('999999999999999999999999999999999999','2026-01-01','bad');");
        }
        var listed = await backup.ListValidatedBackupsAsync();
        Assert.Single(listed); Assert.Equal(valid.Path, listed[0].Path);
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

        await Assert.ThrowsAsync<InvalidDataException>(() => recovery.RestoreAsync(tooNew));

        await using var verify = await factory.OpenConnectionAsync();
        await using var command = verify.CreateCommand(); command.CommandText = "SELECT Value FROM Marker;";
        Assert.Equal("original", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task QuarantineCollisionNeverDeletesOrChangesLiveDatabase()
    {
        var (factory, backup, migrator) = await CreateDatabaseAsync();
        await using (var connection = await factory.OpenConnectionAsync()) await MigrationTests.ExecuteAsync(connection, "CREATE TABLE Marker(Value TEXT); INSERT INTO Marker VALUES('original');");
        var candidate = await backup.CreateBackupAsync(1); var before = await File.ReadAllBytesAsync(factory.DatabasePath);
        var quarantineRoot = Path.Combine(root, "quarantine"); Directory.CreateDirectory(quarantineRoot);
        var fixedNow = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await File.WriteAllTextAsync(Path.Combine(quarantineRoot, "database-damaged-20260101T000000000Z.db"), "collision");
        var recovery = new DatabaseRecoveryService(factory, backup, migrator, new DatabaseIntegrityService(factory), quarantineRoot, new FixedTimeProvider(fixedNow));
        await Assert.ThrowsAnyAsync<IOException>(() => recovery.RestoreAsync(candidate.Path));
        Assert.True(File.Exists(factory.DatabasePath)); Assert.Equal(before, await File.ReadAllBytesAsync(factory.DatabasePath));
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
             new DiagnosticLogEntry("Http", "Error", "E3", "{\"Cookie\":[\"SESSION=array-cookie-secret\"],\"Authorization\":[\"Bearer array-bearer-secret\"]}"),
             new DiagnosticLogEntry("Http", "Error", "E4", "https://example.invalid/private?code=url-secret file://C:/private/profile \\\\server\\share\\profile C:\\Users\\Alice\\AppData\\Local\\BaiduBatch\\app.db C:\\profiles\\account-1 {\"userId\":\"json-user-secret\",\"accountId\":[\"json-account-secret\"],\"extractionCode\":\"json-code-secret\",\"accessToken\":[\"json-access-secret\",\"json-access-secret-two\"],\"token\":\"json-token-secret\"}"),
             new DiagnosticLogEntry("https://stage-secret.invalid", "Bearer level-secret", "C:\\event-secret", "safe"),
             new DiagnosticLogEntry("Http", "Error", "E5", "{\"responseBody\":{\"nested\":{\"secret\":\"body-object-secret\"}},\"child\":{\"response_body\":[\"body-array-secret-one\",{\"secret\":\"body-array-secret-two\"}]},\"other\":{\"body\":\"body-scalar-secret\"}}")],
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
        Assert.DoesNotContain("session-secret2", contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("json-cookie-secret", contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("json-bearer-secret", contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("array-cookie-secret", contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("array-bearer-secret", contents, StringComparison.OrdinalIgnoreCase);
        foreach (var forbidden in new[] { "example.invalid", "url-secret", "private/profile", "server", "AppData", "app.db", "profiles", "json-user-secret", "json-account-secret", "json-code-secret", "json-access-secret", "json-access-secret-two", "json-token-secret", "stage-secret", "level-secret", "event-secret", "body-object-secret", "body-array-secret-one", "body-array-secret-two", "body-scalar-secret" })
            Assert.DoesNotContain(forbidden, contents, StringComparison.OrdinalIgnoreCase);

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

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
