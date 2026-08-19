using System.Runtime.ExceptionServices;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed record DatabaseRecoveryResult(string RestoredBackupPath, string QuarantinePath);

public sealed class DatabaseRecoveryService
{
    private readonly SqliteConnectionFactory factory;
    private readonly DatabaseBackupService backupService;
    private readonly DatabaseMigrator migrator;
    private readonly DatabaseIntegrityService integrityService;
    private readonly string quarantineRoot;
    private readonly TimeProvider timeProvider;
    private readonly IDatabaseFileOperations files;

    public DatabaseRecoveryService(
        SqliteConnectionFactory factory,
        DatabaseBackupService backupService,
        DatabaseMigrator migrator,
        DatabaseIntegrityService integrityService,
        string quarantineRoot,
        TimeProvider? timeProvider = null,
        IDatabaseFileOperations? fileOperations = null)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
        this.migrator = migrator ?? throw new ArgumentNullException(nameof(migrator));
        this.integrityService = integrityService ?? throw new ArgumentNullException(nameof(integrityService));
        this.quarantineRoot = Path.GetFullPath(quarantineRoot ?? throw new ArgumentNullException(nameof(quarantineRoot)));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        files = fileOperations ?? new DatabaseFileOperations();
    }

    public Task<IReadOnlyList<DatabaseBackupDescriptor>> ListValidatedBackupsAsync(CancellationToken cancellationToken = default) =>
        backupService.ListValidatedBackupsAsync(cancellationToken);

    public async Task<DatabaseRecoveryResult> RestoreAsync(string selectedBackupPath, CancellationToken cancellationToken = default)
    {
        var backup = DatabaseBackupService.EnsureDirectChild(selectedBackupPath, backupService.BackupRoot);
        using var maintenance = factory.EnterExclusiveMaintenance();
        cancellationToken.ThrowIfCancellationRequested();
        if (!DatabaseBackupService.TryGetBackupVersion(backup, out var backupVersion) ||
            !await backupService.ValidateBackupAsync(backup, cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("The selected backup is not a validated SQLite backup.");

        var liveRoot = Path.GetDirectoryName(factory.DatabasePath) ?? throw new InvalidOperationException("Database path has no directory.");
        files.CreateDirectory(liveRoot);
        files.CreateDirectory(quarantineRoot);
        var identity = $"{timeProvider.GetUtcNow():yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        var quarantine = DatabaseBackupService.EnsureDirectChild(Path.Combine(quarantineRoot, $"database-damaged-{identity}.db"), quarantineRoot);
        var failedRestore = DatabaseBackupService.EnsureDirectChild(Path.Combine(quarantineRoot, $"database-failed-restore-{identity}.db"), quarantineRoot);
        var staging = DatabaseBackupService.EnsureDirectChild(Path.Combine(liveRoot, $"database-restore-{Guid.NewGuid():N}.tmp"), liveRoot);
        var rollbackStaging = DatabaseBackupService.EnsureDirectChild(Path.Combine(liveRoot, $"database-rollback-{Guid.NewGuid():N}.tmp"), liveRoot);
        var movedSidecars = new List<(string Live, string Quarantined)>();
        var installed = false;
        var originalExisted = files.Exists(factory.DatabasePath);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            files.Copy(backup, staging, overwrite: false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!await backupService.ValidateDatabaseFileAsync(staging, backupVersion, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("The staged restore is not a validated application database.");

            MoveSidecarIfPresent(factory.DatabasePath + "-wal", quarantine + "-wal", movedSidecars);
            MoveSidecarIfPresent(factory.DatabasePath + "-shm", quarantine + "-shm", movedSidecars);
            cancellationToken.ThrowIfCancellationRequested();
            if (originalExisted) files.Replace(staging, factory.DatabasePath, quarantine);
            else files.Move(staging, factory.DatabasePath);
            installed = true;

            await migrator.MigrateAsync(cancellationToken).ConfigureAwait(false);
            await integrityService.EnsureHealthyAsync(cancellationToken).ConfigureAwait(false);
            return new(backup, quarantine);
        }
        catch (Exception original)
        {
            var cleanupErrors = new List<Exception>();
            var mainRollbackSucceeded = !installed;
            if (installed)
            {
                TryCleanup(() =>
                {
                    if (files.Exists(factory.DatabasePath)) files.Copy(factory.DatabasePath, failedRestore, overwrite: false);
                }, cleanupErrors);

                if (originalExisted)
                {
                    TryCleanup(() =>
                    {
                        if (!files.Exists(quarantine)) throw new IOException("The original database quarantine is missing.");
                        files.Copy(quarantine, rollbackStaging, overwrite: false);
                        files.Replace(rollbackStaging, factory.DatabasePath, backupPath: null);
                        mainRollbackSucceeded = true;
                    }, cleanupErrors);
                }
                else
                {
                    TryCleanup(() =>
                    {
                        if (files.Exists(factory.DatabasePath)) files.Delete(factory.DatabasePath);
                        mainRollbackSucceeded = true;
                    }, cleanupErrors);
                }
            }

            if (installed && mainRollbackSucceeded)
            {
                foreach (var suffix in new[] { "-wal", "-shm" })
                {
                    var sidecarStaging = rollbackStaging + suffix;
                    TryCleanup(() =>
                    {
                        var live = factory.DatabasePath + suffix;
                        var quarantined = quarantine + suffix;
                        var hasLive = files.Exists(live);
                        var hasQuarantined = files.Exists(quarantined);
                        if (hasLive && hasQuarantined)
                        {
                            files.Copy(quarantined, sidecarStaging, overwrite: false);
                            files.Replace(sidecarStaging, live, failedRestore + suffix);
                        }
                        else if (hasLive) files.Move(live, failedRestore + suffix);
                        else if (hasQuarantined) files.Move(quarantined, live);
                    }, cleanupErrors);
                    TryCleanup(() => { if (files.Exists(sidecarStaging)) files.Delete(sidecarStaging); }, cleanupErrors);
                }
            }
            else if (!installed)
            {
                foreach (var (live, quarantined) in movedSidecars.AsEnumerable().Reverse())
                    TryCleanup(() => { if (files.Exists(quarantined)) files.Move(quarantined, live); }, cleanupErrors);
            }
            TryCleanup(() => { if (files.Exists(staging)) files.Delete(staging); }, cleanupErrors);
            TryCleanup(() => { if (files.Exists(rollbackStaging)) files.Delete(rollbackStaging); }, cleanupErrors);

            if (cleanupErrors.Count != 0) throw new AggregateException("Database restore and recovery cleanup failed.", [original, .. cleanupErrors]);
            ExceptionDispatchInfo.Capture(original).Throw();
            throw;
        }
    }

    private void MoveSidecarIfPresent(string live, string quarantined, ICollection<(string, string)> moved)
    {
        if (!files.Exists(live)) return;
        files.Move(live, quarantined);
        moved.Add((live, quarantined));
    }

    private static void TryCleanup(Action action, ICollection<Exception> errors)
    {
        try { action(); }
        catch (Exception error) { errors.Add(error); }
    }
}
