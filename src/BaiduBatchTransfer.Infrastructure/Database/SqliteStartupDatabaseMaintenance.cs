using BaiduBatchTransfer.Application.Scheduling;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed class SqliteStartupDatabaseMaintenance : IStartupDatabaseMaintenance
{
    private readonly DatabaseMigrator migrator;
    private readonly DatabaseIntegrityService integrity;

    public SqliteStartupDatabaseMaintenance(
        DatabaseMigrator migrator,
        DatabaseIntegrityService integrity)
    {
        this.migrator = migrator ?? throw new ArgumentNullException(nameof(migrator));
        this.integrity = integrity ?? throw new ArgumentNullException(nameof(integrity));
    }

    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        await migrator.MigrateAsync(cancellationToken).ConfigureAwait(false);
        await integrity.EnsureHealthyAsync(cancellationToken).ConfigureAwait(false);
    }
}
