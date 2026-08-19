using BaiduBatchTransfer.Application.Scheduling;

namespace BaiduBatchTransfer.Application.Tests.Scheduling;

public sealed class NetworkExecutionGateTests
{
    [Fact]
    public void RecoveryRequiresExplicitUserConfirmationBeforeNewWork()
    {
        var gate = new NetworkExecutionGate(initiallyOnline: true);
        gate.ReportOffline();
        gate.ReportNetworkAvailable();

        Assert.False(gate.CanAcquireNewWork);

        gate.ConfirmResume();
        Assert.True(gate.CanAcquireNewWork);
    }
}
