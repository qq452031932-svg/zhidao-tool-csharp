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
        var valid = await backup.CreateBackupAsync(DatabaseMigrator.LatestVersion);
        await File.WriteAllTextAsync(Path.Combine(root, "backups", "database-v2-20000101T000000000Z.db.bak"), "broken");

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
        var (_, backup, _) = await CreateDatabaseAsync(); var valid = await backup.CreateBackupAsync(DatabaseMigrator.LatestVersion);
        File.Copy(valid.Path, Path.Combine(root, "backups", "database-v999999999999999999999-20260101T000000000Z.db.bak"));
        File.Copy(valid.Path, Path.Combine(root, "backups", "database-v2-20261399T999999999Z.db.bak"));
        var listed = await backup.ListValidatedBackupsAsync();
        Assert.Single(listed); Assert.Equal(valid.Path, listed[0].Path);
    }

    [Fact]
    public async Task ListingSkipsOversizedStoredSchemaVersionAndRetainsValidBackup()
    {
        var (_, backup, _) = await CreateDatabaseAsync(); var valid = await backup.CreateBackupAsync(DatabaseMigrator.LatestVersion);
        var malformed = Path.Combine(root, "backups", "database-v2-20260101T000000000Z.db.bak"); File.Copy(valid.Path, malformed);
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
        var accountId = Guid.NewGuid().ToString("D");
        await using (var connection = await factory.OpenConnectionAsync()) await MigrationTests.ExecuteAsync(connection, "INSERT INTO Accounts(Id,BaiduUserId,DisplayName,LoginMethod,Status,DailyLimit,DailyExecutedCount,SortOrder,IsEnabled,DailyCountDateKey,IsDeleted) VALUES($id,'marker','backup','WebView2','Active',0,0,0,1,'2026-01-01',0);", ("$id", accountId));
        var candidate = await backup.CreateBackupAsync(DatabaseMigrator.LatestVersion);
        await using (var connection = await factory.OpenConnectionAsync()) await MigrationTests.ExecuteAsync(connection, "UPDATE Accounts SET DisplayName='damaged-newer' WHERE Id=$id;", ("$id", accountId));
        var recovery = new DatabaseRecoveryService(factory, backup, migrator, new DatabaseIntegrityService(factory), Path.Combine(root, "quarantine"));

        var result = await recovery.RestoreAsync(candidate.Path);

        Assert.True(File.Exists(result.QuarantinePath));
        await using var verify = await factory.OpenConnectionAsync();
        await using var command = verify.CreateCommand(); command.CommandText = "SELECT DisplayName FROM Accounts WHERE Id=$id;"; command.Parameters.AddWithValue("$id", accountId);
        Assert.Equal("backup", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task RestoreAcceptsValidatedVersionFiveBackupAndMigratesItToLatestVersion()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "v5-restore.db");
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            for (var version = 1; version <= 5; version++)
            {
                await MigrationTests.ExecuteAsync(connection, DatabaseMigrator.ReadMigrationSql(version));
                await MigrationTests.ExecuteAsync(connection,
                    "INSERT INTO SchemaVersions(Version,AppliedAt,AppVersion) VALUES($version,'2026-01-01','old');",
                    ("$version", version));
            }
        }
        var factory = new SqliteConnectionFactory(path);
        var backup = new DatabaseBackupService(factory, Path.Combine(root, "v5-backups"));
        var migrator = new DatabaseMigrator(factory, backup, "test");
        var candidate = await backup.CreateBackupAsync(5);
        await migrator.MigrateAsync();
        var recovery = new DatabaseRecoveryService(factory, backup, migrator, new DatabaseIntegrityService(factory), Path.Combine(root, "v5-quarantine"));

        await recovery.RestoreAsync(candidate.Path);

        await using var verify = await factory.OpenConnectionAsync();
        await using var versionCommand = verify.CreateCommand();
        versionCommand.CommandText = "SELECT MAX(Version) FROM SchemaVersions;";
        Assert.Equal((long)DatabaseMigrator.LatestVersion,
            Convert.ToInt64(await versionCommand.ExecuteScalarAsync()));
        await using var indexCommand = verify.CreateCommand();
        indexCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_ExecutionLogs_Account_Id';";
        Assert.Equal(1L, Convert.ToInt64(await indexCommand.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task RestoreAcceptsValidatedVersionSevenBackupThenMigratesExactlyOnceToCurrentVersion()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "v7-restore.db");
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            for (var version = 1; version <= 7; version++)
            {
                await MigrationTests.ExecuteAsync(connection, DatabaseMigrator.ReadMigrationSql(version));
                await MigrationTests.ExecuteAsync(connection,
                    "INSERT INTO SchemaVersions(Version,AppliedAt,AppVersion) VALUES($version,'2026-01-01','old');",
                    ("$version", version));
            }
        }
        var factory = new SqliteConnectionFactory(path);
        var backup = new DatabaseBackupService(factory, Path.Combine(root, "v7-backups"));
        var migrator = new DatabaseMigrator(factory, backup, "test");
        var candidate = await backup.CreateBackupAsync(7);
        await migrator.MigrateAsync();
        var recovery = new DatabaseRecoveryService(factory, backup, migrator, new DatabaseIntegrityService(factory), Path.Combine(root, "v7-quarantine"));

        await recovery.RestoreAsync(candidate.Path);

        Assert.Equal(12, DatabaseMigrator.LatestVersion);
        await using var verify = await factory.OpenConnectionAsync();
        await using var versionCommand = verify.CreateCommand();
        versionCommand.CommandText = "SELECT MAX(Version), COUNT(*) FROM SchemaVersions WHERE Version=$version;";
        versionCommand.Parameters.AddWithValue("$version", DatabaseMigrator.LatestVersion);
        await using var reader = await versionCommand.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal((long)DatabaseMigrator.LatestVersion, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.True((await new DatabaseSchemaValidator().ValidateAsync(
            verify, DatabaseMigrator.LatestVersion)).IsValid);
    }

    [Fact]
    public async Task FailedRestorePreservesOriginalDatabase()
    {
        var (factory, backup, migrator) = await CreateDatabaseAsync();
        var accountId = Guid.NewGuid().ToString("D");
        await using (var connection = await factory.OpenConnectionAsync()) await MigrationTests.ExecuteAsync(connection, "INSERT INTO Accounts(Id,BaiduUserId,DisplayName,LoginMethod,Status,DailyLimit,DailyExecutedCount,SortOrder,IsEnabled,DailyCountDateKey,IsDeleted) VALUES($id,'marker','original','WebView2','Active',0,0,0,1,'2026-01-01',0);", ("$id", accountId));
        var valid = await backup.CreateBackupAsync(DatabaseMigrator.LatestVersion);
        var tooNew = Path.Combine(root, "backups", "database-v99-20260101T000000000Z.db.bak");
        File.Copy(valid.Path, tooNew);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={tooNew};Pooling=False"))
        {
            await connection.OpenAsync();
            await MigrationTests.ExecuteAsync(connection, "DELETE FROM SchemaVersions; INSERT INTO SchemaVersions(Version,AppliedAt,AppVersion) VALUES(99,'2026-01-01','future');");
        }
        var recovery = new DatabaseRecoveryService(factory, backup, migrator, new DatabaseIntegrityService(factory), Path.Combine(root, "quarantine"));

        await Assert.ThrowsAsync<InvalidDataException>(() => recovery.RestoreAsync(tooNew));

        await using var verify = await factory.OpenConnectionAsync();
        await using var command = verify.CreateCommand(); command.CommandText = "SELECT DisplayName FROM Accounts WHERE Id=$id;"; command.Parameters.AddWithValue("$id", accountId);
        Assert.Equal("original", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task AtomicReplaceFailureNeverDeletesOrChangesLiveDatabase()
    {
        var (factory, backup, migrator) = await CreateDatabaseAsync();
        var candidate = await backup.CreateBackupAsync(DatabaseMigrator.LatestVersion); var before = await File.ReadAllBytesAsync(factory.DatabasePath);
        var quarantineRoot = Path.Combine(root, "quarantine"); Directory.CreateDirectory(quarantineRoot);
        var fixedNow = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var files = new FaultInjectingFileOperations { FailReplaceCall = 1 };
        var recovery = new DatabaseRecoveryService(factory, backup, migrator, new DatabaseIntegrityService(factory), quarantineRoot, new FixedTimeProvider(fixedNow), files);
        await Assert.ThrowsAnyAsync<IOException>(() => recovery.RestoreAsync(candidate.Path));
        Assert.True(File.Exists(factory.DatabasePath)); Assert.Equal(before, await File.ReadAllBytesAsync(factory.DatabasePath));
    }

    [Fact]
    public async Task PostInstallValidationFailureRestoresOriginalAndPreservesFailedRestore()
    {
        var (factory, backup, migrator) = await CreateDatabaseAsync();
        var accountId = Guid.NewGuid().ToString("D");
        await using (var connection = await factory.OpenConnectionAsync())
            await MigrationTests.ExecuteAsync(connection, "INSERT INTO Accounts(Id,BaiduUserId,DisplayName,LoginMethod,Status,DailyLimit,DailyExecutedCount,SortOrder,IsEnabled,DailyCountDateKey,IsDeleted) VALUES($id,'u','backup','WebView2','Active',0,0,0,1,'2026-01-01',0);", ("$id", accountId));
        var candidate = await backup.CreateBackupAsync(DatabaseMigrator.LatestVersion);
        await using (var connection = await factory.OpenConnectionAsync())
            await MigrationTests.ExecuteAsync(connection, "UPDATE Accounts SET DisplayName='original-live' WHERE Id=$id;", ("$id", accountId));
        var files = new FaultInjectingFileOperations { CorruptInstalledDatabase = true, CreateInstalledSidecars = true };
        var recovery = new DatabaseRecoveryService(factory, backup, migrator, new DatabaseIntegrityService(factory), Path.Combine(root, "quarantine"), fileOperations: files);

        await Assert.ThrowsAnyAsync<Exception>(() => recovery.RestoreAsync(candidate.Path));

        Assert.False(File.Exists(factory.DatabasePath + "-wal"));
        Assert.False(File.Exists(factory.DatabasePath + "-shm"));
        Assert.NotNull(files.FailedRestorePath);
        Assert.True(File.Exists(files.FailedRestorePath + "-wal"));
        Assert.True(File.Exists(files.FailedRestorePath + "-shm"));
        await using var verify = await factory.OpenConnectionAsync();
        await using var command = verify.CreateCommand(); command.CommandText = "SELECT DisplayName FROM Accounts WHERE Id=$id;"; command.Parameters.AddWithValue("$id", accountId);
        Assert.Equal("original-live", await command.ExecuteScalarAsync());
        Assert.NotNull(files.FailedRestorePath); Assert.True(File.Exists(files.FailedRestorePath));
        Assert.NotNull(files.QuarantinePath); Assert.True(File.Exists(files.QuarantinePath));
    }

    [Fact]
    public async Task RollbackFailurePreservesOriginalQuarantineAndFailedRestoreAndAggregatesErrors()
    {
        var (factory, backup, migrator) = await CreateDatabaseAsync();
        var candidate = await backup.CreateBackupAsync(DatabaseMigrator.LatestVersion);
        var files = new FaultInjectingFileOperations { CorruptInstalledDatabase = true, FailReplaceCall = 2 };
        var recovery = new DatabaseRecoveryService(factory, backup, migrator, new DatabaseIntegrityService(factory), Path.Combine(root, "quarantine"), fileOperations: files);

        var error = await Assert.ThrowsAsync<AggregateException>(() => recovery.RestoreAsync(candidate.Path));

        Assert.True(error.InnerExceptions.Count >= 2);
        Assert.NotNull(files.QuarantinePath); Assert.True(File.Exists(files.QuarantinePath));
        Assert.NotNull(files.FailedRestorePath); Assert.True(File.Exists(files.FailedRestorePath));
    }

    [Fact]
    public async Task SuccessfulMainRollbackRestoresOriginalSidecarsDespiteFailedDiagnosticCopy()
    {
        var (factory, backup, migrator) = await CreateDatabaseAsync();
        var candidate = await backup.CreateBackupAsync(DatabaseMigrator.LatestVersion);
        var originalWal = new byte[] { 1, 2, 3 }; var originalShm = new byte[] { 4, 5, 6 };
        await File.WriteAllBytesAsync(factory.DatabasePath + "-wal", originalWal);
        await File.WriteAllBytesAsync(factory.DatabasePath + "-shm", originalShm);
        var files = new FaultInjectingFileOperations
        {
            CorruptInstalledDatabase = true,
            CreateInstalledSidecars = true,
            FailFailedRestoreCopy = true
        };
        var recovery = new DatabaseRecoveryService(factory, backup, migrator, new DatabaseIntegrityService(factory), Path.Combine(root, "quarantine"), fileOperations: files);

        await Assert.ThrowsAsync<AggregateException>(() => recovery.RestoreAsync(candidate.Path));

        Assert.Equal(originalWal, await File.ReadAllBytesAsync(factory.DatabasePath + "-wal"));
        Assert.Equal(originalShm, await File.ReadAllBytesAsync(factory.DatabasePath + "-shm"));
    }

    [Fact]
    public async Task SidecarSwapFailureIsIndependentAndPreservesEveryTrackedCopy()
    {
        var (factory, backup, migrator) = await CreateDatabaseAsync();
        var candidate = await backup.CreateBackupAsync(DatabaseMigrator.LatestVersion);
        var originalMain = await File.ReadAllBytesAsync(factory.DatabasePath);
        var originalWal = new byte[] { 1, 2, 3 }; var originalShm = new byte[] { 4, 5, 6 };
        await File.WriteAllBytesAsync(factory.DatabasePath + "-wal", originalWal);
        await File.WriteAllBytesAsync(factory.DatabasePath + "-shm", originalShm);
        var files = new FaultInjectingFileOperations
        {
            CorruptInstalledDatabase = true,
            CreateInstalledSidecars = true,
            FailSidecarReplaceSuffix = "-wal"
        };
        var recovery = new DatabaseRecoveryService(factory, backup, migrator, new DatabaseIntegrityService(factory), Path.Combine(root, "quarantine"), fileOperations: files);

        await Assert.ThrowsAsync<AggregateException>(() => recovery.RestoreAsync(candidate.Path));

        Assert.Equal(originalMain, await File.ReadAllBytesAsync(factory.DatabasePath));
        Assert.NotNull(files.QuarantinePath); Assert.NotNull(files.FailedRestorePath);
        Assert.True(File.Exists(factory.DatabasePath + "-wal"));
        Assert.Equal(originalWal, await File.ReadAllBytesAsync(files.QuarantinePath + "-wal"));
        Assert.Equal(originalShm, await File.ReadAllBytesAsync(factory.DatabasePath + "-shm"));
        Assert.True(File.Exists(files.FailedRestorePath + "-shm"));
    }

    [Fact]
    public async Task RestoreRejectsTraversalAndRequiresClosedConnections()
    {
        var (factory, backup, migrator) = await CreateDatabaseAsync();
        var recovery = new DatabaseRecoveryService(factory, backup, migrator, new DatabaseIntegrityService(factory), Path.Combine(root, "quarantine"));
        await Assert.ThrowsAsync<ArgumentException>(() => recovery.RestoreAsync(Path.Combine(root, "outside.db")));
        var candidate = await backup.CreateBackupAsync(DatabaseMigrator.LatestVersion);
        await using var open = await factory.OpenConnectionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.RestoreAsync(candidate.Path));
    }

    [Fact]
    public async Task ExclusiveMaintenanceBlocksNewConnectionsUntilReleased()
    {
        var (factory, _, _) = await CreateDatabaseAsync();
        await using var rawLifecycle = await factory.OpenConnectionAsync();
        rawLifecycle.Close();
        Task<Microsoft.Data.Sqlite.SqliteConnection> blocked;
        using (factory.EnterExclusiveMaintenance())
        {
            using (ExecutionContext.SuppressFlow()) blocked = Task.Run(() => factory.OpenConnectionAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => blocked);
            Task rawBlocked;
            using (ExecutionContext.SuppressFlow()) rawBlocked = Task.Run(rawLifecycle.Open);
            await Assert.ThrowsAsync<InvalidOperationException>(() => rawBlocked);
            Assert.Equal(0, factory.ActiveConnectionCount);
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
        Assert.DoesNotContain("userId", contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("999", contents, StringComparison.Ordinal);
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

    [Fact]
    public async Task DiagnosticZipOmitsEveryFreeFormLogMessageAndExportsOnlyStructuralFields()
    {
        Directory.CreateDirectory(root);
        var messages = new[]
        {
            "plain-unknown-secret-marker",
            "prefix mysteryCredential=opaque-future-value suffix",
            "{\"unknownFutureKey\":{\"nested\":[\"nested-secret-one\",\"nested-secret-two\"]}}",
            "truncated-json:{\"arbitrary\":\"truncated-secret",
            "pwd_without_known_delimiter arbitrary-password",
            "code-prefixed-but-novel::future-code-value"
        };
        var logs = messages.Select((message, index) =>
            new DiagnosticLogEntry("Transfer", "Warning", $"E{index + 1}", message)).ToArray();
        var service = new DiagnosticExportService(Path.Combine(root, "diagnostics"));

        var path = await service.CreateAsync(new DiagnosticExportRequest(
            "1.0", 1, ["ok", "integrity-secret-marker"], logs,
            new Dictionary<string, string> { ["os"] = "Windows", ["unknownMetadata"] = "metadata-secret-marker" }));

        using var zip = ZipFile.OpenRead(path);
        var contents = string.Join('\n', zip.Entries.Select(Read));
        foreach (var message in messages)
            Assert.DoesNotContain(message, contents, StringComparison.Ordinal);
        Assert.DoesNotContain("metadata-secret-marker", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("integrity-secret-marker", contents, StringComparison.Ordinal);

        using var logsJson = JsonDocument.Parse(Read(zip.GetEntry("logs.json")!));
        var exportedLogs = logsJson.RootElement.EnumerateArray().ToArray();
        Assert.Equal(messages.Length, exportedLogs.Length);
        for (var index = 0; index < exportedLogs.Length; index++)
        {
            var fields = exportedLogs[index].EnumerateObject().Select(x => x.Name).Order().ToArray();
            Assert.Equal(new[] { "EventCodeOmitted", "Level", "MessageOmitted", "Sequence", "Stage" }, fields);
            Assert.Equal(index + 1, exportedLogs[index].GetProperty("Sequence").GetInt32());
            Assert.True(exportedLogs[index].GetProperty("MessageOmitted").GetBoolean());
        }

        using var metadataJson = JsonDocument.Parse(Read(zip.GetEntry("metadata.json")!));
        var metadataFields = metadataJson.RootElement.EnumerateObject().Select(x => x.Name).ToArray();
        Assert.Equal(new[] { "os" }, metadataFields);

        static string Read(ZipArchiveEntry entry) { using var reader = new StreamReader(entry.Open()); return reader.ReadToEnd(); }
    }

    [Fact]
    public async Task DiagnosticZipMapsTokenShapedStructuralValuesToSemanticConstants()
    {
        Directory.CreateDirectory(root);
        var service = new DiagnosticExportService(Path.Combine(root, "diagnostics"));
        var path = await service.CreateAsync(new DiagnosticExportRequest(
            "AppTokenSecret123", 1, ["ok"],
            [new DiagnosticLogEntry("StageTokenSecret123", "LevelTokenSecret123", "EventTokenSecret123", "message")],
            new Dictionary<string, string>
            {
                ["os"] = "OsTokenSecret123", ["architecture"] = "ArchitectureTokenSecret123",
                ["runtime"] = "RuntimeTokenSecret123", ["appMode"] = "ModeTokenSecret123"
            }));

        using var zip = ZipFile.OpenRead(path);
        var contents = string.Join('\n', zip.Entries.Select(Read));
        foreach (var secret in new[] { "AppTokenSecret123", "StageTokenSecret123", "LevelTokenSecret123", "EventTokenSecret123", "OsTokenSecret123", "ArchitectureTokenSecret123", "RuntimeTokenSecret123", "ModeTokenSecret123" })
            Assert.DoesNotContain(secret, contents, StringComparison.Ordinal);

        using var manifest = JsonDocument.Parse(Read(zip.GetEntry("manifest.json")!));
        Assert.Equal("unknown", manifest.RootElement.GetProperty("AppVersion").GetString());
        using var logs = JsonDocument.Parse(Read(zip.GetEntry("logs.json")!));
        var log = logs.RootElement[0];
        Assert.Equal("Other", log.GetProperty("Stage").GetString());
        Assert.Equal("Other", log.GetProperty("Level").GetString());
        Assert.False(log.TryGetProperty("EventCode", out _));
        Assert.True(log.GetProperty("EventCodeOmitted").GetBoolean());
        using var metadata = JsonDocument.Parse(Read(zip.GetEntry("metadata.json")!));
        Assert.All(metadata.RootElement.EnumerateObject(), property => Assert.Equal("Other", property.Value.GetString()));

        static string Read(ZipArchiveEntry entry) { using var reader = new StreamReader(entry.Open()); return reader.ReadToEnd(); }
    }

    [Fact]
    public async Task DiagnosticMetadataDoesNotPermitPrimitiveBypassOfPerKeySemantics()
    {
        Directory.CreateDirectory(root);
        var service = new DiagnosticExportService(Path.Combine(root, "diagnostics"));
        var path = await service.CreateAsync(new DiagnosticExportRequest(
            "1.0", 1, ["ok"], [],
            new Dictionary<string, string> { ["os"] = "123456789", ["architecture"] = "true", ["runtime"] = "987654321", ["appMode"] = "false" }));

        using var zip = ZipFile.OpenRead(path);
        using var metadata = JsonDocument.Parse(Read(zip.GetEntry("metadata.json")!));
        Assert.All(metadata.RootElement.EnumerateObject(), property => Assert.Equal("Other", property.Value.GetString()));

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

    private sealed class FaultInjectingFileOperations : IDatabaseFileOperations
    {
        private int replaceCalls;
        private string? installedDatabasePath;
        public int? FailReplaceCall { get; init; }
        public bool CorruptInstalledDatabase { get; init; }
        public bool CreateInstalledSidecars { get; init; }
        public bool FailFailedRestoreCopy { get; init; }
        public string? FailSidecarReplaceSuffix { get; init; }
        public string? QuarantinePath { get; private set; }
        public string? FailedRestorePath { get; private set; }

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
        public bool Exists(string path)
        {
            if (CreateInstalledSidecars && replaceCalls >= 1 && installedDatabasePath is not null &&
                (path == installedDatabasePath + "-wal" || path == installedDatabasePath + "-shm") && !File.Exists(path))
                File.WriteAllBytes(path, [7, 7, 7]);
            return File.Exists(path);
        }
        public void Copy(string source, string destination, bool overwrite)
        {
            if (FailFailedRestoreCopy && destination.Contains("failed-restore", StringComparison.Ordinal) && !source.EndsWith("-wal", StringComparison.Ordinal) && !source.EndsWith("-shm", StringComparison.Ordinal))
                throw new IOException("Injected failed-restore copy failure.");
            File.Copy(source, destination, overwrite);
            if (destination.Contains("failed-restore", StringComparison.Ordinal)) FailedRestorePath = destination;
        }
        public void Move(string source, string destination) => File.Move(source, destination);
        public void Delete(string path) => File.Delete(path);
        public void Replace(string source, string destination, string? backupPath)
        {
            var call = ++replaceCalls;
            if (FailSidecarReplaceSuffix is not null && destination.EndsWith(FailSidecarReplaceSuffix, StringComparison.Ordinal))
                throw new IOException($"Injected sidecar replace failure for {FailSidecarReplaceSuffix}.");
            if (FailReplaceCall == call) throw new IOException($"Injected replace failure {call}.");
            File.Replace(source, destination, backupPath, ignoreMetadataErrors: true);
            if (call == 1)
            {
                installedDatabasePath = destination;
                QuarantinePath = backupPath;
                if (CorruptInstalledDatabase) File.WriteAllBytes(destination, [1, 2, 3, 4, 5]);
                if (CreateInstalledSidecars)
                {
                    File.WriteAllBytes(destination + "-wal", [9, 9, 9]);
                    File.WriteAllBytes(destination + "-shm", [8, 8, 8]);
                }
            }
        }
    }
}
