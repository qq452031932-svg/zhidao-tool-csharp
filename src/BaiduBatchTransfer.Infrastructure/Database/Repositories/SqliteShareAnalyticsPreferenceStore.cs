using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Infrastructure.Database.Repositories;

public sealed class SqliteShareAnalyticsPreferenceStore(SqliteConnectionFactory connectionFactory)
    : IShareAnalyticsPreferenceStore
{
    private readonly SqliteConnectionFactory connectionFactory =
        connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    public async Task<Guid?> LoadLastViewedAccountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT LastViewedAccountId
            FROM ShareAnalyticsPreferences
            WHERE SingletonId=1;
            """;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null)
            throw new InvalidDataException("The share analytics preference row is missing.");
        if (value is DBNull) return null;
        if (value is not string text || !Guid.TryParse(text, out var accountId) || accountId == Guid.Empty)
            throw new InvalidDataException("The stored share analytics account ID is invalid.");
        return accountId;
    }

    public async Task SaveLastViewedAccountAsync(
        Guid? accountId,
        CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("Account ID must not be empty.", nameof(accountId));

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ShareAnalyticsPreferences
            SET LastViewedAccountId=$account,UpdatedAtUtc=$updated
            WHERE SingletonId=1;
            """;
        command.Parameters.AddWithValue(
            "$account",
            accountId is null ? DBNull.Value : accountId.Value.ToString("D"));
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidDataException("The share analytics preference row is missing.");
    }
}
