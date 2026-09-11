using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed partial class SqliteManagedBrowserStateStore
{
    public async Task<BaiduExecutionBindingSnapshot> LoadAsync(Guid accountId, ExecutionProfileSnapshot profile, CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("An account ID is required.", nameof(accountId));
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await using var transaction = connection.BeginTransaction(deferred: true);
        var account = await LoadExecutionAccountAsync(connection, transaction, accountId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (profile.Engine == BaiduExecutionEngine.WebView2V1)
            return new(account, null, null);

        var installation = await LoadInstallationAsync(connection, transaction, profile.InstallationId!.Value, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var session = await LoadSessionAsync(connection, transaction, profile.SessionId!.Value, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new(account, installation, session);
    }

    private static async Task<BaiduExecutionAccount?> LoadExecutionAccountAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid accountId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Deliberately excludes cookie ciphertext and the legacy WebView2 profile directory.
        command.CommandText = """
            SELECT Id,BaiduUserId,Status,IsEnabled,IsDeleted FROM Accounts WHERE Id=$id;
            """;
        command.Parameters.AddWithValue("$id", accountId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var found = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!found) return null;
        return new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4));
    }
}
