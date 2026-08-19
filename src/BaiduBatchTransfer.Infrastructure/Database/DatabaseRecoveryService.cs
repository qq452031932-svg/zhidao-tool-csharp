namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed record DatabaseRecoveryResult(string RestoredBackupPath, string QuarantinePath);

public sealed class DatabaseRecoveryService
{
    private readonly SqliteConnectionFactory factory;
    private readonly DatabaseBackupService backupService;
    private readonly DatabaseMigrator migrator;
    private readonly DatabaseIntegrityService integrityService;
    private readonly string quarantineRoot;

    public DatabaseRecoveryService(
        SqliteConnectionFactory factory,
        DatabaseBackupService backupService,
        DatabaseMigrator migrator,
        DatabaseIntegrityService integrityService,
        string quarantineRoot)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
        this.migrator = migrator ?? throw new ArgumentNullException(nameof(migrator));
        this.integrityService = integrityService ?? throw new ArgumentNullException(nameof(integrityService));
        this.quarantineRoot = Path.GetFullPath(quarantineRoot ?? throw new ArgumentNullException(nameof(quarantineRoot)));
    }

    public Task<IReadOnlyList<DatabaseBackupDescriptor>> ListValidatedBackupsAsync(CancellationToken cancellationToken = default) =>
        backupService.ListValidatedBackupsAsync(cancellationToken);

    public async Task<DatabaseRecoveryResult> RestoreAsync(string selectedBackupPath, CancellationToken cancellationToken = default)
    {
        var backup = DatabaseBackupService.EnsureDirectChild(selectedBackupPath, backupService.BackupRoot);
        using var maintenance = factory.EnterExclusiveMaintenance();
        if (!await backupService.ValidateBackupAsync(backup, cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("The selected backup is not a validated SQLite backup.");

        Directory.CreateDirectory(quarantineRoot);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
        var quarantine = DatabaseBackupService.EnsureDirectChild(Path.Combine(quarantineRoot, $"database-damaged-{stamp}.db"), quarantineRoot);
        var staging = DatabaseBackupService.EnsureDirectChild(Path.Combine(Path.GetDirectoryName(factory.DatabasePath)!, $"database-restore-{Guid.NewGuid():N}.tmp"), Path.GetDirectoryName(factory.DatabasePath)!);
        var moved = new List<(string Original, string Quarantine)>();
        try
        {
            MoveToQuarantineIfPresent(factory.DatabasePath, quarantine, moved);
            MoveToQuarantineIfPresent(factory.DatabasePath + "-wal", quarantine + "-wal", moved);
            MoveToQuarantineIfPresent(factory.DatabasePath + "-shm", quarantine + "-shm", moved);
            File.Copy(backup, staging, overwrite: false);
            File.Move(staging, factory.DatabasePath);
            await migrator.MigrateAsync(cancellationToken).ConfigureAwait(false);
            await integrityService.EnsureHealthyAsync(cancellationToken).ConfigureAwait(false);
            return new(backup, quarantine);
        }
        catch
        {
            DeleteExactIfPresent(staging);
            DeleteExactIfPresent(factory.DatabasePath);
            DeleteExactIfPresent(factory.DatabasePath + "-wal");
            DeleteExactIfPresent(factory.DatabasePath + "-shm");
            foreach (var (original, quarantined) in moved.AsEnumerable().Reverse())
                if (File.Exists(quarantined)) File.Move(quarantined, original);
            throw;
        }
    }

    private static void MoveToQuarantineIfPresent(string original, string quarantine, ICollection<(string, string)> moved)
    {
        if (!File.Exists(original)) return;
        File.Move(original, quarantine);
        moved.Add((original, quarantine));
    }

    private static void DeleteExactIfPresent(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
