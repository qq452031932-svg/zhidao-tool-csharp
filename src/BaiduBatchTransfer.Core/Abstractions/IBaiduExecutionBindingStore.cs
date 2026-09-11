using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Abstractions;

public interface IBaiduExecutionBindingStore
{
    /// <summary>Reads one persisted snapshot; does not establish live identity or grant write authority.</summary>
    Task<BaiduExecutionBindingSnapshot> LoadAsync(Guid accountId, ExecutionProfileSnapshot profile, CancellationToken cancellationToken);
}

public sealed class BaiduExecutionAccount(Guid id, string baiduUserId, string status, bool isEnabled, bool isDeleted)
{
    public Guid Id { get; } = id;
    public string BaiduUserId { get; } = baiduUserId;
    public string Status { get; } = status;
    public bool IsEnabled { get; } = isEnabled;
    public bool IsDeleted { get; } = isDeleted;

    public override string ToString() => "BaiduExecutionAccount { Metadata = [REDACTED] }";
}

public sealed class BaiduExecutionBindingSnapshot(
    BaiduExecutionAccount? account, StoredBrowserInstallation? installation, StoredManagedBrowserSession? session)
{
    public BaiduExecutionAccount? Account { get; } = account;
    public StoredBrowserInstallation? Installation { get; } = installation;
    public StoredManagedBrowserSession? Session { get; } = session;

    public override string ToString() =>
        "BaiduExecutionBindingSnapshot { Account = [REDACTED], Installation = [REDACTED], Session = [REDACTED] }";
}
