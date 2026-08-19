using BaiduBatchTransfer.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Tests.Database;

public sealed class MigrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bbt-migration-{Guid.NewGuid():N}");

    [Fact]
    public async Task NewDatabaseEnablesSafetyPragmasAndCreatesCompleteVersionedSchema()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "app.db");
        var factory = new SqliteConnectionFactory(path, busyTimeoutMilliseconds: 4321);
        var migrator = new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "1.2.3");

        await migrator.MigrateAsync();
        await using var connection = await factory.OpenConnectionAsync();

        Assert.Equal("wal", await ScalarAsync<string>(connection, "PRAGMA journal_mode;"));
        Assert.Equal(1L, await ScalarAsync<long>(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(4321L, await ScalarAsync<long>(connection, "PRAGMA busy_timeout;"));

        var tables = await NamesAsync(connection, "table");
        Assert.Subset(new HashSet<string>(tables), new HashSet<string>
        {
            "ImportSessions", "StagedImportRows", "Accounts", "AccountRules", "BatchJobs",
            "ResourceItems", "TransferOperations", "AccountAllocations", "ExecutionLogs", "SchemaVersions"
        });
        var indexes = await NamesAsync(connection, "index");
        Assert.Contains("UX_TransferOperations_ActiveKey", indexes);
        Assert.Contains("IX_StagedImportRows_Session_Disposition", indexes);
        Assert.Contains("IX_TransferOperations_Account_Status_Lease_Ordinal", indexes);

        var operationColumns = await ColumnsAsync(connection, "TransferOperations");
        Assert.Contains("ConfirmationDescriptorJson", operationColumns);
        Assert.Contains("AdapterRequestId", operationColumns);
        Assert.Contains("RemoteItemIdsJson", operationColumns);
        Assert.Equal(1L, await ScalarAsync<long>(connection, "SELECT Version FROM SchemaVersions;"));
        Assert.True((await new DatabaseSchemaValidator().ValidateAsync(connection, 1)).IsValid);
    }

    [Fact]
    public async Task StagedRowsAcceptEverySpecifiedDispositionAndRejectUnknownValues()
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "app.db"));
        await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
        await using var connection = await factory.OpenConnectionAsync();
        var sessionId = Guid.NewGuid().ToString("D");
        await ExecuteAsync(connection, "INSERT INTO ImportSessions (Id, SourceType, SourceName, Status, CreatedAt, UpdatedAt) VALUES ($id,'Text','x','Draft','2026-01-01','2026-01-01');", ("$id", sessionId));

        foreach (var disposition in new[] { "Ready", "NeedsResolution", "Invalid", "Duplicate", "Conflict", "Excluded" })
        {
            await ExecuteAsync(connection, "INSERT INTO StagedImportRows (Id, ImportSessionId, SourceRowNumber, ShareUrl, Disposition, UpdatedAt) VALUES ($id,$session,$row,'https://example.invalid', $disposition,'2026-01-01');",
                ("$id", Guid.NewGuid().ToString("D")), ("$session", sessionId), ("$row", Array.IndexOf(new[] { "Ready", "NeedsResolution", "Invalid", "Duplicate", "Conflict", "Excluded" }, disposition) + 1), ("$disposition", disposition));
        }

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "INSERT INTO StagedImportRows (Id, ImportSessionId, SourceRowNumber, ShareUrl, Disposition, UpdatedAt) VALUES ($id,$session,99,'x','Mystery','2026-01-01');", ("$id", Guid.NewGuid().ToString("D")), ("$session", sessionId)));
    }

    [Fact]
    public async Task ActiveOperationKeyIsUniqueButCancelledOperationCanBeReplaced()
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "app.db"));
        await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
        await using var connection = await factory.OpenConnectionAsync();
        var ids = await SeedOperationParentsAsync(connection);
        const string sql = "INSERT INTO TransferOperations (Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,NormalizedTargetPath,CreateFolderName,AssignedAccountId,AllocationSource,Status,AttemptCount,OperationFingerprint,SubmitState,TargetSlotState,CreatedAt,UpdatedAt) VALUES ($id,$resource,$job,1,1,'/x','/x','folder',$account,'Rule',$status,0,$fingerprint,'NotPrepared','Unoccupied','2026-01-01','2026-01-01');";
        await ExecuteAsync(connection, sql, ("$id", Guid.NewGuid().ToString("D")), ("$resource", ids.Resource), ("$job", ids.Job), ("$account", ids.Account), ("$status", "Waiting"), ("$fingerprint", "a"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, sql, ("$id", Guid.NewGuid().ToString("D")), ("$resource", ids.Resource), ("$job", ids.Job), ("$account", ids.Account), ("$status", "Waiting"), ("$fingerprint", "b")));
        await ExecuteAsync(connection, "UPDATE TransferOperations SET Status='Cancelled' WHERE OperationFingerprint='a';");
        await ExecuteAsync(connection, sql, ("$id", Guid.NewGuid().ToString("D")), ("$resource", ids.Resource), ("$job", ids.Job), ("$account", ids.Account), ("$status", "Waiting"), ("$fingerprint", "a"));
    }

    [Fact]
    public async Task SubmissionStateRequiresConfirmationDescriptorBeforeBoundary()
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "app.db"));
        await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
        await using var connection = await factory.OpenConnectionAsync();
        var ids = await SeedOperationParentsAsync(connection);
        const string sql = "INSERT INTO TransferOperations (Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,NormalizedTargetPath,CreateFolderName,AssignedAccountId,AllocationSource,Status,AttemptCount,OperationFingerprint,ConfirmationDescriptorJson,SubmitState,TargetSlotState,CreatedAt,UpdatedAt) VALUES ($id,$resource,$job,1,1,'/x','/x','folder',$account,'Rule','Submitting',1,'boundary',NULL,'Submitting','Reserved','2026-01-01','2026-01-01');";
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, sql, ("$id", Guid.NewGuid().ToString("D")), ("$resource", ids.Resource), ("$job", ids.Job), ("$account", ids.Account)));
    }

    [Fact]
    public async Task ExistingDatabaseIsOnlineBackedUpBeforeSequentialUpgradeAndBackupIsValid()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "app.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, "CREATE TABLE SchemaVersions(Version INTEGER PRIMARY KEY, AppliedAt TEXT NOT NULL, AppVersion TEXT NOT NULL); INSERT INTO SchemaVersions VALUES(0,'2026-01-01','old');");
        }
        var factory = new SqliteConnectionFactory(path);
        var backup = new DatabaseBackupService(factory, Path.Combine(root, "backups"));

        await new DatabaseMigrator(factory, backup, "new").MigrateAsync();

        var candidates = await backup.ListValidatedBackupsAsync();
        var created = Assert.Single(candidates);
        Assert.Equal(0, created.SchemaVersion);
        Assert.True(created.IsValid);
    }

    [Fact]
    public async Task CorruptDatabaseFailsQuickCheckAndBlocksStartup()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "app.db");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5]);
        var integrity = new DatabaseIntegrityService(new SqliteConnectionFactory(path));

        var result = await integrity.CheckAsync();

        Assert.False(result.IsHealthy);
        await Assert.ThrowsAsync<DatabaseIntegrityException>(() => integrity.EnsureHealthyAsync());
    }

    [Fact]
    public async Task MigratorRunsStartupIntegrityGateForCurrentDatabase()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "app.db"); var factory = new SqliteConnectionFactory(path);
        var migrator = new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test");
        await migrator.MigrateAsync();
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5]);
        await Assert.ThrowsAsync<DatabaseIntegrityException>(() => migrator.MigrateAsync());
    }

    [Fact]
    public async Task SchemaFingerprintPreservesCheckLiteralCaseAndRejectsUnexpectedUserObjects()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "app.db");
        var factory = new SqliteConnectionFactory(path);
        await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "1.0").MigrateAsync();

        await using (var connection = await factory.OpenConnectionAsync())
        {
            await ExecuteAsync(connection, "PRAGMA writable_schema=ON; UPDATE sqlite_master SET sql=replace(sql, '''Cancelled''', '''cancelled''') WHERE name='UX_TransferOperations_ActiveKey'; PRAGMA writable_schema=OFF;");
        }
        await using (var connection = await factory.OpenConnectionAsync())
        {
            var changedLiteral = await new DatabaseSchemaValidator().ValidateAsync(connection, 1);
            Assert.False(changedLiteral.IsValid);
        }

        File.Delete(path);
        await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups-2")), "1.0").MigrateAsync();
        await using (var connection = await factory.OpenConnectionAsync())
        {
            await ExecuteAsync(connection, "CREATE TRIGGER malicious_trigger AFTER INSERT ON Accounts BEGIN DELETE FROM Accounts; END;");
            var extraTrigger = await new DatabaseSchemaValidator().ValidateAsync(connection, 1);
            Assert.False(extraTrigger.IsValid);
            await ExecuteAsync(connection, "DROP TRIGGER malicious_trigger; CREATE VIEW malicious_view AS SELECT * FROM Accounts;");
            var extraView = await new DatabaseSchemaValidator().ValidateAsync(connection, 1);
            Assert.False(extraView.IsValid);
            await ExecuteAsync(connection, "DROP VIEW malicious_view; CREATE TABLE malicious_table(Value TEXT);");
            var extraTable = await new DatabaseSchemaValidator().ValidateAsync(connection, 1);
            Assert.False(extraTable.IsValid);
        }
    }

    [Fact]
    public async Task MigratorRejectsMalformedPreexistingSchemaAfterMigrationAndPreservesUpgradeBackup()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "app.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, "CREATE TABLE Accounts(Id TEXT PRIMARY KEY, BaiduUserId TEXT, DisplayName TEXT, Status TEXT, IsEnabled INTEGER, IsDeleted INTEGER, SortOrder INTEGER); CREATE TABLE SchemaVersions(Version INTEGER PRIMARY KEY, AppliedAt TEXT NOT NULL, AppVersion TEXT NOT NULL); INSERT INTO SchemaVersions VALUES(0,'2026-01-01','old');");
        }
        var factory = new SqliteConnectionFactory(path);
        var backup = new DatabaseBackupService(factory, Path.Combine(root, "backups"));

        await Assert.ThrowsAsync<InvalidDataException>(() => new DatabaseMigrator(factory, backup, "1.0").MigrateAsync());

        await using (var live = await factory.OpenConnectionAsync())
        {
            Assert.Equal(0L, await ScalarAsync<long>(live, "SELECT MAX(Version) FROM SchemaVersions;"));
            Assert.Equal(0L, await ScalarAsync<long>(live, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ImportSessions';"));
        }
        var preserved = Assert.Single(await backup.ListValidatedBackupsAsync());
        Assert.Equal(0, preserved.SchemaVersion);
    }

    [Fact]
    public async Task BackupCancellationOrValidationFailureLeavesNoPartialBackup()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "app.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, "CREATE TABLE SchemaVersions(Version INTEGER PRIMARY KEY, AppliedAt TEXT NOT NULL, AppVersion TEXT NOT NULL); INSERT INTO SchemaVersions VALUES(1,'2026-01-01','bad');");
        }
        var factory = new SqliteConnectionFactory(path);
        var backupRoot = Path.Combine(root, "backups");
        var backup = new DatabaseBackupService(factory, backupRoot);

        await Assert.ThrowsAsync<InvalidDataException>(() => backup.CreateBackupAsync(1));
        Assert.Empty(Directory.EnumerateFiles(backupRoot));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backup.CreateBackupAsync(1, cancellation.Token));
        Assert.Empty(Directory.EnumerateFiles(backupRoot));
    }

    [Fact]
    public async Task ConcurrentBackupsPromoteToDistinctValidatedCanonicalFiles()
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "app.db"));
        var backup = new DatabaseBackupService(factory, Path.Combine(root, "backups"), new FixedTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        await new DatabaseMigrator(factory, backup, "1.0").MigrateAsync();

        var created = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => backup.CreateBackupAsync(1)));

        Assert.Equal(6, created.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(created, item => Assert.True(File.Exists(item.Path)));
        Assert.Equal(6, (await backup.ListValidatedBackupsAsync()).Count);
        Assert.DoesNotContain(Directory.EnumerateFiles(backup.BackupRoot), path => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static async Task<(string Resource, string Job, string Account)> SeedOperationParentsAsync(SqliteConnection c)
    {
        var account = Guid.NewGuid().ToString("D"); var job = Guid.NewGuid().ToString("D"); var resource = Guid.NewGuid().ToString("D");
        await ExecuteAsync(c, "INSERT INTO Accounts(Id,BaiduUserId,DisplayName,LoginMethod,Status,DailyLimit,DailyExecutedCount,SortOrder,IsEnabled,DailyCountDateKey,IsDeleted) VALUES($id,'u','n','WebView2','Active',0,0,0,1,'2026-01-01',0);", ("$id", account));
        await ExecuteAsync(c, "INSERT INTO BatchJobs(Id,Name,SourceType,TransferMode,QuotaBasis,ShortagePolicy,ResourceCount,OperationCount,DefaultTargetPath,FolderCreationMode,DuplicatePolicy,ValidationMode,Status,CreatedAt,QueueOrder) VALUES($id,'j','Text','Distribution','TaskCount','ProportionalReduction',1,1,'/','None','Skip','Immediate','Queued','2026-01-01',1);", ("$id", job));
        await ExecuteAsync(c, "INSERT INTO ResourceItems(Id,BatchJobId,CandidateSequence,ShareUrl,NormalizedShareId,FolderCreationModeOverride,GlobalValidityStatus,CandidateStatus,CreatedAt,UpdatedAt) VALUES($id,$job,1,'url','share','None','Unknown','Unallocated','2026-01-01','2026-01-01');", ("$id", resource), ("$job", job));
        return (resource, job, account);
    }

    internal static async Task ExecuteAsync(SqliteConnection connection, string sql, params (string, object?)[] values)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        var value = await command.ExecuteScalarAsync() ?? throw new InvalidDataException("Expected a scalar value.");
        return (T)Convert.ChangeType(value, typeof(T));
    }

    private static async Task<IReadOnlyList<string>> NamesAsync(SqliteConnection connection, string type)
    {
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT name FROM sqlite_master WHERE type=$type;"; command.Parameters.AddWithValue("$type", type);
        var names = new List<string>(); await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) names.Add(reader.GetString(0)); return names;
    }

    private static async Task<IReadOnlyList<string>> ColumnsAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand(); command.CommandText = $"PRAGMA table_info({table});";
        var names = new List<string>(); await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) names.Add(reader.GetString(1)); return names;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
