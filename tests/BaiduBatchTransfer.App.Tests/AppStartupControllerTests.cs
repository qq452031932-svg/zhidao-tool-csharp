using BaiduBatchTransfer.App;

namespace BaiduBatchTransfer.App.Tests;

public sealed class AppStartupControllerTests
{
    [Fact]
    public async Task RecoveryAlwaysCompletesBeforeHostServicesCanStart()
    {
        var order = new List<string>();
        var controller = new AppStartupController(new FakeRecovery(order, available: true));

        var result = await controller.StartAsync(_ => { order.Add("host"); return Task.CompletedTask; });

        Assert.Equal(["guard-integrity-leases-counters", "host"], order);
        Assert.True(result.CoordinatorMayStart);
    }

    [Fact]
    public async Task MissingWindowsGuardKeepsDatabaseAndCoordinatorStopped()
    {
        var order = new List<string>();
        var controller = new AppStartupController(new FakeRecovery(order, available: false));

        var result = await controller.StartAsync(_ => { order.Add("host"); return Task.CompletedTask; });

        Assert.Empty(order);
        Assert.False(result.CoordinatorMayStart);
        Assert.Contains("单实例保护尚不可用", result.StatusMessage);
    }

    [Fact]
    public async Task ExistingApplicationInstanceIsReportedWithoutStartingHost()
    {
        var order = new List<string>();
        var controller = new AppStartupController(new FakeRecovery(order, available: true, rejectAsDuplicate: true));

        var result = await controller.StartAsync(_ => { order.Add("host"); return Task.CompletedTask; });

        Assert.Empty(order);
        Assert.False(result.CoordinatorMayStart);
        Assert.Equal("软件已经在运行，请切换到已打开的窗口。", result.StatusMessage);
    }

    private sealed class FakeRecovery(List<string> order, bool available, bool rejectAsDuplicate = false) : IAppRecoveryRunner
    {
        public bool IsAvailable => available;
        public string UnavailableMessage => "单实例保护尚不可用，调度未启动。";
        public Task RecoverAsync(CancellationToken cancellationToken)
        {
            if (rejectAsDuplicate) throw new BaiduBatchTransfer.Application.Scheduling.SingleInstanceUnavailableException();
            order.Add("guard-integrity-leases-counters");
            return Task.CompletedTask;
        }
    }
}
