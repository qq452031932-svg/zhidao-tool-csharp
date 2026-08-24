using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Abstractions;

public interface IRemoteResourcePicker
{
    Task<IReadOnlyList<OwnedRemoteSelection>> PickAsync(
        Guid accountId,
        CancellationToken cancellationToken = default);
}
