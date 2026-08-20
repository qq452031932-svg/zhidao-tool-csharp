namespace BaiduBatchTransfer.Core.Abstractions;

public interface IAdapterCapabilityStore
{
    Task AcceptAsync(AdapterCapabilityAcceptanceRecord acceptance, CancellationToken cancellationToken = default);
    Task<AdapterCapabilityAcceptanceRecord?> GetAsync(
        string adapterKind,
        string protocolVersion,
        string adapterBuildVersion,
        CancellationToken cancellationToken = default);
}
