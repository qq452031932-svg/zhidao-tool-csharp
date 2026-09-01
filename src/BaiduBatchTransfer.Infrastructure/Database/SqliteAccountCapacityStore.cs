using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed class SqliteAccountCapacityStore(SqliteConnectionFactory connectionFactory) : IAccountCapacityStore
{
    private readonly SqliteConnectionFactory connectionFactory =
        connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    public async Task<bool> TryUpdateAsync(
        Guid accountId,
        string expectedBaiduUserId,
        AccountCapacity capacity,
        DateTimeOffset validatedAt,
        CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("An account id is required.", nameof(accountId));
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedBaiduUserId);
        ArgumentNullException.ThrowIfNull(capacity);

        await using var connection = await connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Accounts
            SET CapacityTotal=$total, CapacityUsed=$used, LastValidatedAt=$validated
            WHERE Id=$id AND BaiduUserId=$user AND Status='Active' AND IsEnabled=1 AND IsDeleted=0;
            """;
        command.Parameters.AddWithValue("$total", capacity.TotalBytes);
        command.Parameters.AddWithValue("$used", capacity.UsedBytes);
        command.Parameters.AddWithValue("$validated", validatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$id", accountId.ToString("D"));
        command.Parameters.AddWithValue("$user", expectedBaiduUserId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }
}
