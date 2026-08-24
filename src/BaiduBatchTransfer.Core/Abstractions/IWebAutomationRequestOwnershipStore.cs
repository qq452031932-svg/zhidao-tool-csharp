namespace BaiduBatchTransfer.Core.Abstractions;

public interface IWebAutomationRequestOwnershipStore
{
    Task<bool> IsBoundAsync(
        Guid operationId,
        Guid batchId,
        Guid accountId,
        int generation,
        WebAutomationOwnerKind ownerKind,
        CancellationToken cancellationToken = default);
}
