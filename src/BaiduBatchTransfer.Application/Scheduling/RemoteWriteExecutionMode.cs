namespace BaiduBatchTransfer.Application.Scheduling;

/// <summary>
/// Controls which durable remote-write entry points may run in this process.
/// AcceptanceOnly is intentionally handled by the exact acceptance runner and
/// never enables either global coordinator.
/// </summary>
public enum RemoteWriteExecutionMode
{
    Disabled = 0,
    AcceptanceOnly = 1,
    Full = 2,
    DirectBackground = 3,
}
