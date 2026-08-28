using BaiduBatchTransfer.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

/// <summary>One durable prompt slot, shared with the application's migration/backup lifecycle.</summary>
public sealed class SqliteBrowserDirectoryState(SqliteConnectionFactory factory) : IBrowserDirectoryState
{
    public async Task<BrowserDirectoryStateSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadAsync(connection, null, cancellationToken).ConfigureAwait(false);
    }

    public Task<BrowserDirectoryIntentBeginResult> TryBeginEnsurePromptAsync(long expectedRevision,
        BrowserDirectoryIntentClaim claim, CancellationToken cancellationToken) =>
        BeginAsync(expectedRevision, claim, cancellationToken);

    public Task<BrowserDirectoryIntentBeginResult> TryBeginReselectAsync(
        BrowserDirectoryIntentClaim claim, CancellationToken cancellationToken) => BeginAsync(null, claim, cancellationToken);

    private async Task<BrowserDirectoryIntentBeginResult> BeginAsync(long? expectedRevision,
        BrowserDirectoryIntentClaim claim, CancellationToken token)
    {
        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadAsync(connection, transaction, token).ConfigureAwait(false);
        if (current.PendingIntent is not null || (expectedRevision is not null && current.Revision != expectedRevision))
            return BrowserDirectoryIntentBeginResult.Conflict();
        var intent = new BrowserDirectorySelectionIntent(checked(current.Revision + 1), claim.Kind, claim.OwnerEpoch, claim.IntentId);
        await WriteAsync(connection, transaction, new(current.Directory, intent.Revision, intent), token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return BrowserDirectoryIntentBeginResult.Acquired(intent);
    }

    public async Task<BrowserDirectoryStateSaveResult> TrySaveAsync(string selectedDirectory, long expectedRevision,
        BrowserDirectorySelectionIntent selectionIntent, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedDirectory);
        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadAsync(connection, transaction, token).ConfigureAwait(false);
        if (current.Revision != expectedRevision || !Matches(current.PendingIntent, selectionIntent))
            return BrowserDirectoryStateSaveResult.Conflict();
        var revision = checked(current.Revision + 1);
        await WriteAsync(connection, transaction, new(selectedDirectory, revision, null), token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return BrowserDirectoryStateSaveResult.Committed(revision);
    }

    public async Task<bool> TryAbandonSelectionAsync(BrowserDirectorySelectionIntent selectionIntent, CancellationToken token)
    {
        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadAsync(connection, transaction, token).ConfigureAwait(false);
        if (!Matches(current.PendingIntent, selectionIntent)) return false;
        await WriteAsync(connection, transaction, new(current.Directory, checked(current.Revision + 1), null), token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return true;
    }

    private static bool Matches(BrowserDirectorySelectionIntent? left, BrowserDirectorySelectionIntent right) =>
        left is not null && left.Revision == right.Revision && left.Kind == right.Kind &&
        left.OwnerEpoch == right.OwnerEpoch && left.IntentId == right.IntentId;

    private static async Task<BrowserDirectoryStateSnapshot> ReadAsync(SqliteConnection connection,
        SqliteTransaction? transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Directory,Revision,IntentKind,OwnerEpoch,IntentId FROM BrowserDirectorySelection WHERE Singleton=1;";
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("Browser selection state is unavailable.");
        var revision = reader.GetInt64(1);
        return new(reader.IsDBNull(0) ? null : reader.GetString(0), revision, reader.IsDBNull(2) ? null :
            new BrowserDirectorySelectionIntent(revision, (BrowserDirectorySelectionIntentKind)reader.GetInt32(2),
                Guid.Parse(reader.GetString(3)), Guid.Parse(reader.GetString(4))));
    }

    private static async Task WriteAsync(SqliteConnection connection, SqliteTransaction transaction,
        BrowserDirectoryStateSnapshot value, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE BrowserDirectorySelection SET Directory=$directory,Revision=$revision,IntentKind=$kind,OwnerEpoch=$owner,IntentId=$intent WHERE Singleton=1;";
        command.Parameters.AddWithValue("$directory", (object?)value.Directory ?? DBNull.Value);
        command.Parameters.AddWithValue("$revision", value.Revision);
        command.Parameters.AddWithValue("$kind", value.PendingIntent is null ? DBNull.Value : (object)(int)value.PendingIntent.Kind);
        command.Parameters.AddWithValue("$owner", (object?)value.PendingIntent?.OwnerEpoch.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$intent", (object?)value.PendingIntent?.IntentId.ToString("D") ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Browser selection state is unavailable.");
    }
}
