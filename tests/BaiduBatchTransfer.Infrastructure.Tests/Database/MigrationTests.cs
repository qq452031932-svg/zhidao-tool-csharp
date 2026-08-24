using BaiduBatchTransfer.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Tests.Database;

public sealed class MigrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bbt-migration-{Guid.NewGuid():N}");

    // Field ownership for v9:
    // - transfer submit evidence remains in TransferOperations;
    // - transfer-triggered share evidence remains in ShareOperations/ShareOperationGenerations;
    // - standalone share facts never fabricate a TransferOperation or relax ShareOperations.TransferOperationId;
    // - local paths exist only in BatchResultDestinations/StandaloneShareBatches/LocalResultExports;
    // - browser recovery stores only stable remote ids, canonical keys and sanitized fingerprints.
    [Fact]
    public async Task VersionOneUpgradeCreatesV9WorkspacePersistenceWithoutSensitiveWebFields()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "v1-to-v9.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, DatabaseMigrator.ReadMigrationSql(1));
            await ExecuteAsync(connection,
                "INSERT INTO SchemaVersions(Version,AppliedAt,AppVersion) VALUES(1,'2026-01-01','old');");
        }

        var factory = new SqliteConnectionFactory(path);
        await new DatabaseMigrator(
            factory,
            new DatabaseBackupService(factory, Path.Combine(root, "v9-backups")),
            "new").MigrateAsync();

        await using var upgraded = await factory.OpenConnectionAsync();
        Assert.Equal(9L, await ScalarAsync<long>(upgraded, "SELECT MAX(Version) FROM SchemaVersions;"));
        Assert.True((await new DatabaseSchemaValidator().ValidateAsync(upgraded, 9)).IsValid);
        var tables = await NamesAsync(upgraded, "table");
        Assert.Subset(new HashSet<string>(tables), new HashSet<string>
        {
            "StandaloneShareBatches", "StandaloneShareOperations", "StandaloneShareRemoteItems",
            "StandaloneShareResults", "BatchResultDestinations", "LocalResultExports",
            "WebAutomationAttempts", "WebAutomationAttemptRemoteItems",
        });

        var attemptColumns = await ColumnsAsync(upgraded, "WebAutomationAttempts");
        Assert.DoesNotContain(attemptColumns, column =>
            column.Contains("Url", StringComparison.OrdinalIgnoreCase) ||
            column.Contains("Cookie", StringComparison.OrdinalIgnoreCase) ||
            column.Contains("Header", StringComparison.OrdinalIgnoreCase) ||
            column.Contains("Response", StringComparison.OrdinalIgnoreCase) ||
            column.Contains("Extraction", StringComparison.OrdinalIgnoreCase) ||
            column.Contains("Uid", StringComparison.OrdinalIgnoreCase) ||
            column.Contains("Path", StringComparison.OrdinalIgnoreCase));

        var shareColumns = await ColumnsAsync(upgraded, "ShareOperations");
        Assert.Contains("TransferOperationId", shareColumns);
        var fkRows = await ForeignKeysAsync(upgraded, "ShareOperations");
        Assert.Contains(fkRows, row => row.Table == "TransferOperations" && row.From == "TransferOperationId");
    }

    [Fact]
    public async Task VersionEightUpgradeAppliesNineExactlyOnceAndCurrentMigrationIsIdempotent()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "v8-to-v9.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            for (var version = 1; version <= 8; version++)
            {
                await ExecuteAsync(connection, DatabaseMigrator.ReadMigrationSql(version));
                await ExecuteAsync(connection,
                    "INSERT INTO SchemaVersions(Version,AppliedAt,AppVersion) VALUES($version,'2026-01-01','old');",
                    ("$version", version));
            }
            Assert.True((await new DatabaseSchemaValidator().ValidateAsync(connection, 8)).IsValid);
        }
        var factory = new SqliteConnectionFactory(path);
        var migrator = new DatabaseMigrator(
            factory, new DatabaseBackupService(factory, Path.Combine(root, "v8-backups")), "new");

        await migrator.MigrateAsync();
        await migrator.MigrateAsync();

        await using var upgraded = await factory.OpenConnectionAsync();
        Assert.Equal(1L, await ScalarAsync<long>(
            upgraded, "SELECT COUNT(*) FROM SchemaVersions WHERE Version=9;"));
        Assert.True((await new DatabaseSchemaValidator().ValidateAsync(upgraded, 9)).IsValid);
    }

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
        Assert.Contains("IX_TransferOperations_Batch_AllocationOrdinal", indexes);
        Assert.Contains("IX_TransferOperations_Batch_Status_AllocationOrdinal", indexes);
        Assert.Contains("IX_TransferOperations_Status_Lease", indexes);
        Assert.Contains("IX_ResourceItems_Batch_Status_Constraint_Sequence", indexes);
        Assert.Contains("IX_StagedImportRows_Precheck_Keyset", indexes);
        Assert.Contains("IX_ResourceItems_Result_Filter_Keyset", indexes);
        Assert.Contains("IX_TransferOperations_Result_Account_Keyset", indexes);
        Assert.Contains("IX_TransferOperations_Result_UpdatedInstant_Keyset", indexes);
        Assert.Contains("IX_StagedImportRows_Session_Normalized_Source", indexes);
        Assert.Contains("IX_StagedImportRows_Session_RequestedAccount", indexes);
        Assert.Contains("IX_AccountAllocations_Account_Batch", indexes);
        Assert.Contains("IX_ResourceItems_Constraint_Batch", indexes);
        Assert.Contains("IX_ResourceItems_Reserved_Batch", indexes);
        Assert.Contains("IX_ImportSessions_Precheck_Id", indexes);
        Assert.Contains("IX_ImportSessions_CreatedBatch_Id", indexes);
        Assert.Contains("IX_ImportSessions_AppendBatch_Id", indexes);
        Assert.Contains("IX_BatchJobs_ImportSession_Id", indexes);
        Assert.Contains("IX_TransferOperations_Resource_Batch", indexes);
        Assert.Contains("IX_ExecutionLogs_Account_Id", indexes);
        Assert.Contains("IX_ExecutionLogs_Resource_Id", indexes);
        Assert.Contains("IX_ExecutionLogs_Operation_Id", indexes);
        Assert.Contains("IX_TransferOperations_Batch_Account_Status_State", indexes);
        Assert.Contains("IX_StagedImportRows_Duplicate_Id", indexes);
        Assert.Contains("IX_ResourceItems_StagedImportRow_Id", indexes);
        Assert.DoesNotContain("IX_TransferOperations_Batch_Status_Sequence", indexes);

        var operationColumns = await ColumnsAsync(connection, "TransferOperations");
        Assert.Contains("ConfirmationDescriptorJson", operationColumns);
        Assert.Contains("AdapterRequestId", operationColumns);
        Assert.Contains("RemoteItemIdsJson", operationColumns);
        Assert.Contains("NextAllocationOrdinal", await ColumnsAsync(connection, "BatchJobs"));
        Assert.Equal((long)DatabaseMigrator.LatestVersion, await ScalarAsync<long>(connection, "SELECT MAX(Version) FROM SchemaVersions;"));
        Assert.True((await new DatabaseSchemaValidator().ValidateAsync(connection, DatabaseMigrator.LatestVersion)).IsValid);
        var duplicatePlan = await QueryPlanAsync(connection, """
            SELECT NormalizedShareId FROM StagedImportRows
            WHERE ImportSessionId='00000000-0000-0000-0000-000000000001'
              AND Disposition IN ('Ready','Duplicate','Conflict')
            GROUP BY NormalizedShareId HAVING COUNT(*)>1 ORDER BY NormalizedShareId LIMIT 1000;
            """);
        Assert.Contains(duplicatePlan, detail =>
            detail.Contains("IX_StagedImportRows_Session_Normalized_Source", StringComparison.Ordinal));
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
    public async Task VersionOneUpgradeBackfillsPersistentOrdinalAndPreservesValidV1Backup()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "app.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, DatabaseMigrator.ReadMigrationSql(1));
            await ExecuteAsync(connection,
                "INSERT INTO SchemaVersions(Version,AppliedAt,AppVersion) VALUES(1,'2026-01-01','old');");
            var ids = await SeedOperationParentsAsync(connection);
            const string operation = "INSERT INTO TransferOperations(Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,NormalizedTargetPath,CreateFolderName,AssignedAccountId,AllocationSource,Status,AttemptCount,OperationFingerprint,SubmitState,TargetSlotState,CreatedAt,UpdatedAt) VALUES($id,$resource,$job,1,$ordinal,'/x','/x',$folder,$account,'Rule','Cancelled',0,$fingerprint,'NotPrepared','Released','2026-01-01','2026-01-01');";
            await ExecuteAsync(connection, operation, ("$id", Guid.NewGuid().ToString("D")), ("$resource", ids.Resource), ("$job", ids.Job), ("$ordinal", 4), ("$folder", "a"), ("$account", ids.Account), ("$fingerprint", "a"));
            await ExecuteAsync(connection, operation, ("$id", Guid.NewGuid().ToString("D")), ("$resource", ids.Resource), ("$job", ids.Job), ("$ordinal", 11), ("$folder", "b"), ("$account", ids.Account), ("$fingerprint", "b"));
            Assert.True((await new DatabaseSchemaValidator().ValidateAsync(connection, 1)).IsValid);
        }
        var factory = new SqliteConnectionFactory(path);
        var backup = new DatabaseBackupService(factory, Path.Combine(root, "backups"));

        await new DatabaseMigrator(factory, backup, "new").MigrateAsync();

        await using var upgraded = await factory.OpenConnectionAsync();
        Assert.Equal(12L, await ScalarAsync<long>(upgraded, "SELECT NextAllocationOrdinal FROM BatchJobs;"));
        Assert.Equal((long)DatabaseMigrator.LatestVersion, await ScalarAsync<long>(upgraded, "SELECT MAX(Version) FROM SchemaVersions;"));
        Assert.True((await new DatabaseSchemaValidator().ValidateAsync(upgraded, DatabaseMigrator.LatestVersion)).IsValid);
        var preserved = Assert.Single(await backup.ListValidatedBackupsAsync());
        Assert.Equal(1, preserved.SchemaVersion);
        Assert.True(preserved.IsValid);
    }

    [Fact]
    public async Task VersionTwoUpgradeAddsResultIndexesAndPreservesValidatedV2Backup()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "v2-upgrade.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, DatabaseMigrator.ReadMigrationSql(1));
            await ExecuteAsync(connection,
                "INSERT INTO SchemaVersions(Version,AppliedAt,AppVersion) VALUES(1,'2026-01-01','old');");
            await ExecuteAsync(connection, DatabaseMigrator.ReadMigrationSql(2));
            await ExecuteAsync(connection,
                "INSERT INTO SchemaVersions(Version,AppliedAt,AppVersion) VALUES(2,'2026-01-02','old');");
            Assert.True((await new DatabaseSchemaValidator().ValidateAsync(connection, 2)).IsValid);
        }
        var factory = new SqliteConnectionFactory(path);
        var backup = new DatabaseBackupService(factory, Path.Combine(root, "v2-backups"));

        await new DatabaseMigrator(factory, backup, "new").MigrateAsync();

        await using var upgraded = await factory.OpenConnectionAsync();
        Assert.Equal((long)DatabaseMigrator.LatestVersion, await ScalarAsync<long>(upgraded, "SELECT MAX(Version) FROM SchemaVersions;"));
        Assert.True((await new DatabaseSchemaValidator().ValidateAsync(upgraded, DatabaseMigrator.LatestVersion)).IsValid);
        var indexes = await NamesAsync(upgraded, "index");
        Assert.Contains("IX_TransferOperations_Result_UpdatedInstant_Keyset", indexes);
        var preserved = Assert.Single(await backup.ListValidatedBackupsAsync());
        Assert.Equal(2, preserved.SchemaVersion);
        Assert.True(preserved.IsValid);
    }

    [Fact]
    public async Task VersionThreeUpgradeAddsDraftClassificationIndexesAndPreservesValidatedV3Backup()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "v3-upgrade.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            for (var version = 1; version <= 3; version++)
            {
                await ExecuteAsync(connection, DatabaseMigrator.ReadMigrationSql(version));
                await ExecuteAsync(connection,
                    "INSERT INTO SchemaVersions(Version,AppliedAt,AppVersion) VALUES($version,'2026-01-01','old');",
                    ("$version", version));
            }
            Assert.True((await new DatabaseSchemaValidator().ValidateAsync(connection, 3)).IsValid);
        }
        var factory = new SqliteConnectionFactory(path);
        var backup = new DatabaseBackupService(factory, Path.Combine(root, "v3-backups"));

        await new DatabaseMigrator(factory, backup, "new").MigrateAsync();

        await using var upgraded = await factory.OpenConnectionAsync();
        Assert.Equal((long)DatabaseMigrator.LatestVersion, await ScalarAsync<long>(upgraded, "SELECT MAX(Version) FROM SchemaVersions;"));
        Assert.True((await new DatabaseSchemaValidator().ValidateAsync(upgraded, DatabaseMigrator.LatestVersion)).IsValid);
        var indexes = await NamesAsync(upgraded, "index");
        Assert.Contains("IX_StagedImportRows_Session_Normalized_Source", indexes);
        Assert.Contains("IX_StagedImportRows_Session_RequestedAccount", indexes);
        var preserved = Assert.Single(await backup.ListValidatedBackupsAsync());
        Assert.Equal(3, preserved.SchemaVersion);
        Assert.True(preserved.IsValid);
    }

    [Fact]
    public async Task VersionFourUpgradeAddsAccountRemovalIndexesAndPreservesValidatedV4Backup()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "v4-upgrade.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            for (var version = 1; version <= 4; version++)
            {
                await ExecuteAsync(connection, DatabaseMigrator.ReadMigrationSql(version));
                await ExecuteAsync(connection,
                    "INSERT INTO SchemaVersions(Version,AppliedAt,AppVersion) VALUES($version,'2026-01-01','old');",
                    ("$version", version));
            }
            Assert.True((await new DatabaseSchemaValidator().ValidateAsync(connection, 4)).IsValid);
        }
        var factory = new SqliteConnectionFactory(path);
        var backup = new DatabaseBackupService(factory, Path.Combine(root, "v4-backups"));

        await new DatabaseMigrator(factory, backup, "new").MigrateAsync();

        await using var upgraded = await factory.OpenConnectionAsync();
        Assert.Equal((long)DatabaseMigrator.LatestVersion, await ScalarAsync<long>(upgraded, "SELECT MAX(Version) FROM SchemaVersions;"));
        Assert.True((await new DatabaseSchemaValidator().ValidateAsync(upgraded, DatabaseMigrator.LatestVersion)).IsValid);
        var indexes = await NamesAsync(upgraded, "index");
        Assert.Contains("IX_AccountAllocations_Account_Batch", indexes);
        Assert.Contains("IX_TransferOperations_Resource_Batch", indexes);
        var preserved = Assert.Single(await backup.ListValidatedBackupsAsync());
        Assert.Equal(4, preserved.SchemaVersion);
        Assert.True(preserved.IsValid);
    }

    [Fact]
    public async Task VersionFiveUpgradeAddsCleanupSafetyIndexesAndPreservesValidatedV5Backup()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "v5-upgrade.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            for (var version = 1; version <= 5; version++)
            {
                await ExecuteAsync(connection, DatabaseMigrator.ReadMigrationSql(version));
                await ExecuteAsync(connection,
                    "INSERT INTO SchemaVersions(Version,AppliedAt,AppVersion) VALUES($version,'2026-01-01','old');",
                    ("$version", version));
            }
            Assert.True((await new DatabaseSchemaValidator().ValidateAsync(connection, 5)).IsValid);
        }
        var factory = new SqliteConnectionFactory(path);
        var backup = new DatabaseBackupService(factory, Path.Combine(root, "v5-backups"));

        await new DatabaseMigrator(factory, backup, "new").MigrateAsync();

        await using var upgraded = await factory.OpenConnectionAsync();
        Assert.Equal((long)DatabaseMigrator.LatestVersion, await ScalarAsync<long>(upgraded, "SELECT MAX(Version) FROM SchemaVersions;"));
        Assert.True((await new DatabaseSchemaValidator().ValidateAsync(upgraded, DatabaseMigrator.LatestVersion)).IsValid);
        var indexes = await NamesAsync(upgraded, "index");
        Assert.Contains("IX_ExecutionLogs_Account_Id", indexes);
        Assert.Contains("IX_TransferOperations_Batch_Account_Status_State", indexes);
        var preserved = Assert.Single(await backup.ListValidatedBackupsAsync());
        Assert.Equal(5, preserved.SchemaVersion);
        Assert.True(preserved.IsValid);
    }

    [Fact]
    public async Task VersionSixUpgradeAddsDurableRemovalMarkerAndPreservesGenericDisabledRows()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "v6-upgrade.db");
        var accountId = Guid.NewGuid().ToString("D");
        var credentialedDisabledId = Guid.NewGuid().ToString("D");
        var legacyPendingId = Guid.NewGuid().ToString("D");
        var legacyProfilePath = Path.Combine(root, "profiles", legacyPendingId);
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            for (var version = 1; version <= 6; version++)
            {
                await ExecuteAsync(connection, DatabaseMigrator.ReadMigrationSql(version));
                await ExecuteAsync(connection,
                    "INSERT INTO SchemaVersions(Version,AppliedAt,AppVersion) VALUES($version,'2026-01-01','old');",
                    ("$version", version));
            }
            await ExecuteAsync(connection, """
                INSERT INTO Accounts(
                  Id,BaiduUserId,DisplayName,LoginMethod,Status,DailyLimit,DailyExecutedCount,
                  SortOrder,IsEnabled,DailyCountDateKey,IsDeleted)
                VALUES($id,'disabled-user','普通停用账号','WebView2','Disabled',0,0,0,0,'2026-08-20',0);
                """, ("$id", accountId));
            await ExecuteAsync(connection, """
                INSERT INTO Accounts(
                  Id,BaiduUserId,DisplayName,EncryptedSession,WebView2ProfileDirectory,LoginMethod,Status,
                  DailyLimit,DailyExecutedCount,SortOrder,IsEnabled,LastValidatedAt,DailyCountDateKey,
                  IsDeleted,DeletedAt)
                VALUES($id,'credentialed-disabled-user','保留会话的普通停用账号',X'010203',$profile,'WebView2','Disabled',
                  0,0,1,0,'2026-08-20T08:00:00Z','2026-08-20',0,NULL);
                """, ("$id", credentialedDisabledId), ("$profile", Path.Combine(root, "profiles", credentialedDisabledId)));
            await ExecuteAsync(connection, """
                INSERT INTO Accounts(
                  Id,BaiduUserId,DisplayName,EncryptedSession,WebView2ProfileDirectory,LoginMethod,Status,
                  DailyLimit,DailyExecutedCount,SortOrder,IsEnabled,LastValidatedAt,DailyCountDateKey,
                  IsDeleted,DeletedAt)
                VALUES($id,'legacy-pending-user','旧版待清理账号',NULL,$profile,'WebView2','Disabled',
                  0,0,1,0,NULL,'2026-08-20',0,NULL);
                """, ("$id", legacyPendingId), ("$profile", legacyProfilePath));
            Assert.True((await new DatabaseSchemaValidator().ValidateAsync(connection, 6)).IsValid);
        }
        var factory = new SqliteConnectionFactory(path);
        var backup = new DatabaseBackupService(factory, Path.Combine(root, "v6-backups"));

        await new DatabaseMigrator(factory, backup, "new").MigrateAsync();

        await using var upgraded = await factory.OpenConnectionAsync();
        Assert.Equal((long)DatabaseMigrator.LatestVersion, await ScalarAsync<long>(upgraded, "SELECT MAX(Version) FROM SchemaVersions;"));
        Assert.True((await new DatabaseSchemaValidator().ValidateAsync(upgraded, DatabaseMigrator.LatestVersion)).IsValid);
        var accountColumns = await ColumnsAsync(upgraded, "Accounts");
        Assert.Contains("RemovalState", accountColumns);
        Assert.Contains("RemovalGeneration", accountColumns);
        Assert.Equal("None", await ScalarAsync<string>(upgraded,
            "SELECT RemovalState FROM Accounts WHERE Id=$id;", ("$id", accountId)));
        Assert.Equal("missing", await ScalarAsync<string>(upgraded,
            "SELECT COALESCE(RemovalGeneration,'missing') FROM Accounts WHERE Id=$id;", ("$id", accountId)));
        Assert.Equal("None", await ScalarAsync<string>(upgraded,
            "SELECT RemovalState FROM Accounts WHERE Id=$id;", ("$id", credentialedDisabledId)));
        Assert.Equal("CleanupPending", await ScalarAsync<string>(upgraded,
            "SELECT RemovalState FROM Accounts WHERE Id=$id;", ("$id", legacyPendingId)));
        var legacyGeneration = await ScalarAsync<string>(upgraded,
            "SELECT COALESCE(RemovalGeneration,'missing') FROM Accounts WHERE Id=$id;", ("$id", legacyPendingId));
        Assert.Matches("^[0-9A-F]{64}$", legacyGeneration);
        var triggers = await NamesAsync(upgraded, "trigger");
        Assert.Contains("TR_Accounts_RemovalState_Insert", triggers);
        Assert.Contains("TR_Accounts_RemovalState_Update", triggers);
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(upgraded,
            "UPDATE Accounts SET RemovalState='CleanupPending' WHERE Id=$id;", ("$id", accountId)));
        var preserved = Assert.Single(await backup.ListValidatedBackupsAsync());
        Assert.Equal(6, preserved.SchemaVersion);
        Assert.True(preserved.IsValid);
    }

    [Fact]
    public async Task VersionSevenUpgradeCreatesShareSchemaWithoutBackfillAndPreservesTransferHistory()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "v7-upgrade.db");
        string operationId;
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            for (var version = 1; version <= 7; version++)
            {
                await ExecuteAsync(connection, DatabaseMigrator.ReadMigrationSql(version));
                await ExecuteAsync(connection,
                    "INSERT INTO SchemaVersions(Version,AppliedAt,AppVersion) VALUES($version,'2026-01-01','old');",
                    ("$version", version));
            }
            var ids = await SeedOperationParentsAsync(connection);
            operationId = Guid.NewGuid().ToString("D");
            await ExecuteAsync(connection, """
                INSERT INTO TransferOperations(
                  Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,NormalizedTargetPath,
                  CreateFolderName,AssignedAccountId,AllocationSource,Status,OutcomeKind,AttemptCount,
                  OperationFingerprint,ConfirmationDescriptorJson,AdapterRequestId,RemoteItemIdsJson,SubmitState,
                  TargetSlotState,TargetSlotReservedAt,DailyCountedAt,SubmitStartedAt,LastErrorCode,LastErrorMessage,
                  LeaseExpiresAt,CreatedAt,UpdatedAt)
                VALUES($id,$resource,$job,7,9,'/Target','/target','Folder',$account,'Rule','ResultPending',NULL,3,
                  'history-fingerprint','{"safe":true}','request-opaque','["remote-opaque"]','PendingConfirmation',
                  'Reserved','2026-08-20T01:00:00Z','2026-08-20T01:01:00Z','2026-08-20T01:02:00Z','SAFE_CODE',
                  'safe message','2026-08-20T01:03:00Z','2026-08-20T00:00:00Z','2026-08-20T01:04:00Z');
                """, ("$id", operationId), ("$resource", ids.Resource), ("$job", ids.Job), ("$account", ids.Account));
            await ExecuteAsync(connection, """
                INSERT INTO ExecutionLogs(Id,BatchJobId,ResourceItemId,TransferOperationId,AccountId,Stage,Level,EventCode,SanitizedMessage,CreatedAt)
                VALUES($id,$job,$resource,$operation,$account,'Transfer','Warning','PENDING','safe message','2026-08-20T01:05:00Z');
                """, ("$id", Guid.NewGuid().ToString("D")), ("$job", ids.Job), ("$resource", ids.Resource),
                ("$operation", operationId), ("$account", ids.Account));
            Assert.True((await new DatabaseSchemaValidator().ValidateAsync(connection, 7)).IsValid);
        }
        var factory = new SqliteConnectionFactory(path);
        var backup = new DatabaseBackupService(factory, Path.Combine(root, "v7-backups"));
        var before = await ReadOperationAsync(factory, operationId);

        await new DatabaseMigrator(factory, backup, "new").MigrateAsync();

        Assert.Equal(9, DatabaseMigrator.LatestVersion);
        await using var upgraded = await factory.OpenConnectionAsync();
        Assert.True((await new DatabaseSchemaValidator().ValidateAsync(upgraded, DatabaseMigrator.LatestVersion)).IsValid);
        Assert.Equal(before, await ReadOperationAsync(factory, operationId));
        Assert.Equal(0L, await ScalarAsync<long>(upgraded, "SELECT COUNT(*) FROM BatchShareSettings;"));
        Assert.Equal(0L, await ScalarAsync<long>(upgraded, "SELECT COUNT(*) FROM ShareOperations;"));
        Assert.Contains("SettingsFingerprint", await ColumnsAsync(upgraded, "ShareOperations"));
        Assert.Contains("CanonicalSortKey", await ColumnsAsync(upgraded, "ShareOperationRemoteItems"));
        Assert.Contains("SourceContentFingerprint", await ColumnsAsync(upgraded, "ImportSessions"));
        Assert.Contains("HeaderRowNumber", await ColumnsAsync(upgraded, "ImportSessions"));
        Assert.Contains("CandidateSequence", await ColumnsAsync(upgraded, "BaiduShareResults"));
        Assert.Contains("OutputOrdinal", await ColumnsAsync(upgraded, "BaiduShareResults"));
        Assert.Contains("BatchJobId, IsAccepted, IsCurrent, CandidateSequence, OutputOrdinal, Id",
            await ScalarAsync<string>(upgraded,
                "SELECT sql FROM sqlite_master WHERE type='index' AND name='IX_BaiduShareResults_Batch_Accepted_Current_Cursor';"));
        Assert.Contains("SettingsFingerprint", await ScalarAsync<string>(upgraded,
            "SELECT sql FROM sqlite_master WHERE type='index' AND name='UX_ShareOperations_ActiveCreateKey';"));
        Assert.Equal(0L, await ScalarAsync<long>(upgraded, "SELECT ShareOperationCount FROM BatchJobs LIMIT 1;"));
        Assert.Equal(1L, await ScalarAsync<long>(upgraded,
            "SELECT COUNT(*) FROM ExecutionLogs WHERE TransferOperationId=$id;", ("$id", operationId)));
        await ExecuteAsync(upgraded, "UPDATE TransferOperations SET Status='Paused' WHERE Id=$id;", ("$id", operationId));
        var preserved = Assert.Single(await backup.ListValidatedBackupsAsync());
        Assert.Equal(7, preserved.SchemaVersion);
        Assert.True(preserved.IsValid);
    }

    [Fact]
    public async Task MalformedPreexistingV8ObjectLeavesValidatedV7DatabaseUnchanged()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "malformed-v8.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            for (var version = 1; version <= 7; version++)
            {
                await ExecuteAsync(connection, DatabaseMigrator.ReadMigrationSql(version));
                await ExecuteAsync(connection,
                    "INSERT INTO SchemaVersions(Version,AppliedAt,AppVersion) VALUES($version,'2026-01-01','old');",
                    ("$version", version));
            }
            await ExecuteAsync(connection, "CREATE TABLE BatchShareSettings(BadColumn TEXT);");
        }
        var factory = new SqliteConnectionFactory(path);
        var backup = new DatabaseBackupService(factory, Path.Combine(root, "malformed-backups"));

        var migrator = new DatabaseMigrator(factory, backup, "new");
        await Assert.ThrowsAsync<InvalidDataException>(() => migrator.ApplyMigrationAsync(8, default));

        Assert.Equal(9, DatabaseMigrator.LatestVersion);
        await using var live = await factory.OpenConnectionAsync();
        Assert.Equal(7L, await ScalarAsync<long>(live, "SELECT MAX(Version) FROM SchemaVersions;"));
        Assert.Equal(new[] { "BadColumn" }, await ColumnsAsync(live, "BatchShareSettings"));
        Assert.DoesNotContain("ColumnMappingFingerprint", await ColumnsAsync(live, "ImportSessions"));
        Assert.Equal(0L, await ScalarAsync<long>(live,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ShareOperations';"));
    }

    [Fact]
    public async Task AccountRemovalClosureQueriesUseVersionSixIndexesUnderImmediateWriterLock()
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "plans.db"));
        await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "plan-backups")), "test").MigrateAsync();
        await using var connection = await factory.OpenConnectionAsync();
        await using var transaction = connection.BeginTransaction(deferred: false);

        await AssertUsesAsync(connection, transaction, "IX_AccountAllocations_Account_Batch",
            "SELECT BatchJobId FROM AccountAllocations WHERE AccountId='00000000-0000-0000-0000-000000000001';");
        await AssertUsesAsync(connection, transaction, "IX_ResourceItems_Constraint_Batch",
            "SELECT BatchJobId FROM ResourceItems WHERE CandidateConstraintAccountId='00000000-0000-0000-0000-000000000001';");
        await AssertUsesAsync(connection, transaction, "IX_ResourceItems_Reserved_Batch",
            "SELECT BatchJobId FROM ResourceItems WHERE ReservedAccountId='00000000-0000-0000-0000-000000000001';");
        await AssertUsesAsync(connection, transaction, "IX_ImportSessions_Precheck_Id",
            "SELECT Id FROM ImportSessions WHERE PrecheckAccountId='00000000-0000-0000-0000-000000000001';");
        await AssertUsesAsync(connection, transaction, "IX_ImportSessions_CreatedBatch_Id",
            "SELECT Id FROM ImportSessions WHERE CreatedBatchJobId='00000000-0000-0000-0000-000000000001';");
        await AssertUsesAsync(connection, transaction, "IX_ImportSessions_AppendBatch_Id",
            "SELECT Id FROM ImportSessions WHERE AppendToBatchJobId='00000000-0000-0000-0000-000000000001';");
        await AssertUsesAsync(connection, transaction, "IX_BatchJobs_ImportSession_Id",
            "SELECT Id FROM BatchJobs WHERE ImportSessionId='00000000-0000-0000-0000-000000000001';");
        await AssertUsesAsync(connection, transaction, "IX_TransferOperations_Resource_Batch",
            "SELECT BatchJobId FROM TransferOperations WHERE ResourceItemId='00000000-0000-0000-0000-000000000001';");
        await AssertUsesAsync(connection, transaction, "IX_ExecutionLogs_Account_Id",
            "SELECT Id FROM ExecutionLogs WHERE AccountId='00000000-0000-0000-0000-000000000001';");
        await AssertUsesAsync(connection, transaction, "IX_ExecutionLogs_Resource_Id",
            "SELECT Id FROM ExecutionLogs WHERE ResourceItemId='00000000-0000-0000-0000-000000000001';");
        await AssertUsesAsync(connection, transaction, "IX_ExecutionLogs_Operation_Id",
            "SELECT Id FROM ExecutionLogs WHERE TransferOperationId='00000000-0000-0000-0000-000000000001';");
        await AssertUsesAsync(connection, transaction, "IX_TransferOperations_Batch_Account_Status_State",
            "SELECT Id FROM TransferOperations WHERE BatchJobId='00000000-0000-0000-0000-000000000001' AND AssignedAccountId='00000000-0000-0000-0000-000000000002' AND Status='Cancelled' AND SubmitState='NotPrepared' AND TargetSlotState='Released';");
        await AssertUsesAsync(connection, transaction, "IX_StagedImportRows_Duplicate_Id",
            "SELECT Id FROM StagedImportRows WHERE DuplicateOfStagedRowId='00000000-0000-0000-0000-000000000001';");
        await AssertUsesAsync(connection, transaction, "IX_ResourceItems_StagedImportRow_Id",
            "SELECT Id FROM ResourceItems WHERE StagedImportRowId='00000000-0000-0000-0000-000000000001';");
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task OrdinalOverflowRollsBackVersionTwoMigrationAndPreservesVersionOneBackup()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "app.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, DatabaseMigrator.ReadMigrationSql(1));
            await ExecuteAsync(connection,
                "INSERT INTO SchemaVersions(Version,AppliedAt,AppVersion) VALUES(1,'2026-01-01','old');");
            var ids = await SeedOperationParentsAsync(connection);
            await ExecuteAsync(connection,
                "INSERT INTO TransferOperations(Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,NormalizedTargetPath,CreateFolderName,AssignedAccountId,AllocationSource,Status,AttemptCount,OperationFingerprint,SubmitState,TargetSlotState,CreatedAt,UpdatedAt) VALUES($id,$resource,$job,1,$ordinal,'/x','/x','a',$account,'Rule','Cancelled',0,'overflow','NotPrepared','Released','2026-01-01','2026-01-01');",
                ("$id", Guid.NewGuid().ToString("D")), ("$resource", ids.Resource), ("$job", ids.Job),
                ("$ordinal", long.MaxValue), ("$account", ids.Account));
        }
        var factory = new SqliteConnectionFactory(path);
        var backup = new DatabaseBackupService(factory, Path.Combine(root, "backups"));

        var error = await Record.ExceptionAsync(() =>
            new DatabaseMigrator(factory, backup, "new").MigrateAsync());
        Assert.True(error is SqliteException or InvalidDataException, error?.ToString());

        await using var live = await factory.OpenConnectionAsync();
        Assert.Equal(1L, await ScalarAsync<long>(live, "SELECT MAX(Version) FROM SchemaVersions;"));
        Assert.DoesNotContain("NextAllocationOrdinal", await ColumnsAsync(live, "BatchJobs"));
        var preserved = Assert.Single(await backup.ListValidatedBackupsAsync());
        Assert.Equal(1, preserved.SchemaVersion);
        Assert.True(preserved.IsValid);
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
            var changedLiteral = await new DatabaseSchemaValidator().ValidateAsync(connection, DatabaseMigrator.LatestVersion);
            Assert.False(changedLiteral.IsValid);
        }

        File.Delete(path);
        await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups-2")), "1.0").MigrateAsync();
        await using (var connection = await factory.OpenConnectionAsync())
        {
            await ExecuteAsync(connection, "CREATE TRIGGER malicious_trigger AFTER INSERT ON Accounts BEGIN DELETE FROM Accounts; END;");
            var extraTrigger = await new DatabaseSchemaValidator().ValidateAsync(connection, DatabaseMigrator.LatestVersion);
            Assert.False(extraTrigger.IsValid);
            await ExecuteAsync(connection, "DROP TRIGGER malicious_trigger; CREATE VIEW malicious_view AS SELECT * FROM Accounts;");
            var extraView = await new DatabaseSchemaValidator().ValidateAsync(connection, DatabaseMigrator.LatestVersion);
            Assert.False(extraView.IsValid);
            await ExecuteAsync(connection, "DROP VIEW malicious_view; CREATE TABLE malicious_table(Value TEXT);");
            var extraTable = await new DatabaseSchemaValidator().ValidateAsync(connection, DatabaseMigrator.LatestVersion);
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

        var created = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => backup.CreateBackupAsync(DatabaseMigrator.LatestVersion)));

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

    private static async Task<string> ReadOperationAsync(SqliteConnectionFactory factory, string operationId)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,NormalizedTargetPath,
              CreateFolderName,AssignedAccountId,AllocationSource,Status,OutcomeKind,AttemptCount,OperationFingerprint,
              ConfirmationDescriptorJson,AdapterRequestId,RemoteItemIdsJson,SubmitState,TargetSlotState,
              TargetSlotReservedAt,DailyCountedAt,SubmitStartedAt,LastErrorCode,LastErrorMessage,LeaseExpiresAt,CreatedAt,UpdatedAt
            FROM TransferOperations WHERE Id=$id;
            """;
        command.Parameters.AddWithValue("$id", operationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(index =>
            reader.IsDBNull(index) ? "<null>" : Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture)));
    }

    internal static async Task ExecuteAsync(SqliteConnection connection, string sql, params (string, object?)[] values)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(
        SqliteConnection connection,
        string sql,
        params (string, object?)[] values)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = await command.ExecuteScalarAsync() ?? throw new InvalidDataException("Expected a scalar value.");
        return (T)Convert.ChangeType(result, typeof(T));
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

    private static async Task<IReadOnlyList<(string Table, string From)>> ForeignKeysAsync(
        SqliteConnection connection,
        string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list({table});";
        var values = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add((reader.GetString(2), reader.GetString(3)));
        return values;
    }

    private static async Task<IReadOnlyList<string>> QueryPlanAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand(); command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        var details = new List<string>(); await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) details.Add(reader.GetString(3));
        return details;
    }

    private static async Task AssertUsesAsync(SqliteConnection connection, SqliteTransaction transaction, string index, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        var details = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) details.Add(reader.GetString(3));
        Assert.Contains(details, detail => detail.Contains(index, StringComparison.Ordinal));
        Assert.DoesNotContain(details, detail => detail.StartsWith("SCAN ", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
