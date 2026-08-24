namespace BaiduBatchTransfer.Core.Domain;

public enum RemoteResourceKind
{
    File,
    Folder,
}

public sealed record OwnedRemoteSelection
{
    public OwnedRemoteSelection(
        Guid accountId,
        RemoteItemIdentitySet remoteItems,
        string displayName,
        RemoteResourceKind kind,
        RemotePath parentPath)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("An owning account is required.", nameof(accountId));
        ArgumentNullException.ThrowIfNull(remoteItems);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentNullException.ThrowIfNull(parentPath);

        AccountId = accountId;
        RemoteItems = remoteItems;
        DisplayName = displayName.Trim();
        Kind = kind;
        ParentPath = parentPath;
    }

    public Guid AccountId { get; }

    public RemoteItemIdentitySet RemoteItems { get; }

    public string DisplayName { get; }

    public RemoteResourceKind Kind { get; }

    public RemotePath ParentPath { get; }

    public bool HasSameExecutionIdentity(OwnedRemoteSelection? other) =>
        other is not null && AccountId == other.AccountId && RemoteItems == other.RemoteItems;

    public override string ToString() =>
        $"OwnedRemoteSelection {{ AccountId = {AccountId}, Kind = {Kind}, ExactItems = [REDACTED], DisplayName = [REDACTED], ParentPath = [REDACTED] }}";
}
