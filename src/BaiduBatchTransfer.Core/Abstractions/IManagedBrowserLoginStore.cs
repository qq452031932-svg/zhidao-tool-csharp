using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Abstractions;

public enum ManagedBrowserBindingStatus
{
    Bound, DuplicateIdentity, AccountIdentityMismatch, AccountUnavailable, Conflict, ActiveWorkBlocked
}

public interface IManagedBrowserLoginStore
{
    Task<ManagedBrowserBindingStatus> BindLoginAsync(bool addNew, BrowserInstallationIdentity installation,
        ManagedBrowserSessionReference session, StoredManagedBrowserSession? expectedCurrent, CancellationToken cancellationToken);
}
