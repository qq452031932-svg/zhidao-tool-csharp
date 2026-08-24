using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed class SqliteWebAutomationRequestOwnershipStore(
    SqliteConnectionFactory factory) : IWebAutomationRequestOwnershipStore
{
    public async Task<bool> IsBoundAsync(
        Guid operationId,
        Guid batchId,
        Guid accountId,
        int generation,
        WebAutomationOwnerKind ownerKind,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation is required.", nameof(operationId));
        if (batchId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(batchId));
        if (accountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(accountId));
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
        if (!Enum.IsDefined(ownerKind)) throw new ArgumentOutOfRangeException(nameof(ownerKind));

        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = ownerKind switch
        {
            WebAutomationOwnerKind.Transfer => """
                SELECT COUNT(*) FROM TransferOperations
                WHERE Id=$operation AND BatchJobId=$batch AND AssignedAccountId=$account;
                """,
            WebAutomationOwnerKind.TransferShare => """
                SELECT COUNT(*) FROM ShareOperations
                WHERE Id=$operation AND BatchJobId=$batch AND AccountId=$account AND Generation=$generation;
                """,
            WebAutomationOwnerKind.StandaloneShare => """
                SELECT COUNT(*) FROM StandaloneShareOperations
                WHERE Id=$operation AND BatchId=$batch AND AccountId=$account AND Generation=$generation;
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(ownerKind)),
        };
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        command.Parameters.AddWithValue("$account", accountId.ToString("D"));
        command.Parameters.AddWithValue("$generation", generation);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }
}
