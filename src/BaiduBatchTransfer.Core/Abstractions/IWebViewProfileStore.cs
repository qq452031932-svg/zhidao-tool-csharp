namespace BaiduBatchTransfer.Core.Abstractions;

public enum OwnedProfileDeletionStatus
{
    Completed,
    OwnershipMismatch
}

public interface IWebViewProfileStore
{
    Task CloseAsync(Guid accountId, CancellationToken cancellationToken);

    Task DeleteAsync(Guid accountId, string exactProfilePath, CancellationToken cancellationToken);

    Task RegisterOwnershipAsync(
        Guid accountId,
        string exactProfilePath,
        Guid ownershipToken,
        CancellationToken cancellationToken);

    Task<bool> CommitOwnedAsync(
        Guid accountId,
        string exactProfilePath,
        Guid ownershipToken,
        Func<CancellationToken, Task> commitAction,
        CancellationToken cancellationToken);

    Task<OwnedProfileDeletionStatus> DeleteOwnedAsync(
        Guid accountId,
        string exactProfilePath,
        Guid ownershipToken,
        Func<CancellationToken, Task<bool>> confirmNoActiveAccount,
        CancellationToken cancellationToken);
}

public interface IWebViewProfileCloser
{
    Task CloseAsync(Guid accountId, CancellationToken cancellationToken);
}

public interface IAccountProfilePathPolicy
{
    string GetAccountProfilePath(Guid accountId);

    void EnsureSafeAccountPath(Guid accountId, string candidatePath);
}
