using BaiduBatchTransfer.App;
using BaiduBatchTransfer.App.Features.Licensing;
using SuperPan.Licensing.Client;

namespace BaiduBatchTransfer.App.Tests;

public sealed class AppStartupControllerTests
{
    [Fact]
    public async Task StartupValidatesExactlyOnceBeforeHostServicesStart()
    {
        var order = new List<string>();
        var gate = new RecordingLicenseGate(order, isAuthorized: true);
        var authorization = new LicenseRuntimeAuthorizationState();
        var controller = new AppStartupController(
            new FakeRecovery(order, available: true),
            new AppRuntimeState(),
            gate,
            authorization);

        var result = await controller.StartAsync(_ =>
        {
            order.Add("host");
            return Task.CompletedTask;
        });

        Assert.Equal(["local-preparation", "license:Startup", "authorized-recovery", "host"], order);
        Assert.Equal(1, gate.CallCount);
        Assert.True(result.CoordinatorMayStart);
        Assert.True(result.LocalUiMayOpen);
        Assert.True(result.LicenseAuthorized);
        Assert.True(authorization.IsAuthorized);
    }

    [Fact]
    public async Task StartupRejectionStillStartsHostAndAllowsLocalUi()
    {
        var order = new List<string>();
        var gate = new RecordingLicenseGate(order, isAuthorized: false);
        var authorization = new LicenseRuntimeAuthorizationState();
        var recovery = new FakeRecovery(order, available: true);
        var controller = new AppStartupController(
            recovery,
            new AppRuntimeState(),
            gate,
            authorization);

        var result = await controller.StartAsync(_ =>
        {
            order.Add("host");
            return Task.CompletedTask;
        });

        Assert.Equal(["local-preparation", "license:Startup", "host"], order);
        Assert.Equal(0, recovery.AuthorizedRecoveryCallCount);
        Assert.Equal(1, gate.CallCount);
        Assert.True(result.CoordinatorMayStart);
        Assert.True(result.LocalUiMayOpen);
        Assert.False(result.LicenseAuthorized);
        Assert.True(authorization.StartupValidationCompleted);
        Assert.False(authorization.IsAuthorized);
        Assert.Equal("软件尚未激活，联网功能暂不可用。", result.StatusMessage);
        Assert.True(result.ActivationInputRequired);
        Assert.False(result.LicenseVerificationDeferred);
    }

    [Theory]
    [InlineData(LicenseCodes.ServiceUnavailable)]
    [InlineData(LicenseCodes.RateLimited)]
    [InlineData("authorization_unavailable")]
    public async Task AnUnreachableServiceIsNotTreatedAsAMissingLicence(string code)
    {
        // 授权与否只用一个布尔表示时，"这台机器没有卡密"和"暂时连不上授权服务器"
        // 长得一模一样，于是网络抖动的付费用户也会被弹激活窗、被告知"你没激活"。
        var order = new List<string>();
        var controller = new AppStartupController(
            new FakeRecovery(order, available: true),
            new AppRuntimeState(),
            new RecordingLicenseGate(order, isAuthorized: false, code: code),
            new LicenseRuntimeAuthorizationState());

        var result = await controller.StartAsync(_ => Task.CompletedTask);

        Assert.False(result.LicenseAuthorized);
        Assert.False(result.ActivationInputRequired);
        Assert.True(result.LicenseVerificationDeferred);
        Assert.Equal("暂时无法连接授权服务器，联网功能稍后重试。", result.StatusMessage);
    }

    [Theory]
    [InlineData(LicenseCodes.LicenseNotFound)]
    [InlineData(LicenseCodes.LicenseExpired)]
    [InlineData(LicenseCodes.MachineMismatch)]
    [InlineData(LicenseCodes.AppDisabled)]
    public async Task AnExplicitServerRejectionStillAsksForACard(string code)
    {
        var order = new List<string>();
        var controller = new AppStartupController(
            new FakeRecovery(order, available: true),
            new AppRuntimeState(),
            new RecordingLicenseGate(order, isAuthorized: false, code: code),
            new LicenseRuntimeAuthorizationState());

        var result = await controller.StartAsync(_ => Task.CompletedTask);

        Assert.True(result.ActivationInputRequired);
        Assert.False(result.LicenseVerificationDeferred);
    }

    [Fact]
    public async Task StartupGateExceptionSkipsAllRecoveryWritesButStillStartsLocalUi()
    {
        var order = new List<string>();
        var authorization = new LicenseRuntimeAuthorizationState();
        var recovery = new FakeRecovery(order, available: true);
        var controller = new AppStartupController(
            recovery,
            new AppRuntimeState(),
            new RecordingLicenseGate(order, isAuthorized: false, throwOnAuthorize: true),
            authorization);

        var result = await controller.StartAsync(_ =>
        {
            order.Add("host");
            return Task.CompletedTask;
        });

        Assert.Equal(["local-preparation", "license:Startup", "host"], order);
        Assert.Equal(0, recovery.AuthorizedRecoveryCallCount);
        Assert.True(result.CoordinatorMayStart);
        Assert.True(result.LocalUiMayOpen);
        Assert.False(result.LicenseAuthorized);
        Assert.True(authorization.StartupValidationCompleted);
        Assert.False(authorization.IsAuthorized);
    }

    [Fact]
    public async Task RecoveryAlwaysCompletesBeforeHostServicesCanStart()
    {
        var order = new List<string>();
        var controller = new AppStartupController(new FakeRecovery(order, available: true));

        var result = await controller.StartAsync(_ => { order.Add("host"); return Task.CompletedTask; });

        Assert.Equal(["local-preparation", "authorized-recovery", "host"], order);
        Assert.True(result.CoordinatorMayStart);
    }

    [Fact]
    public async Task DatabasePreparationFailureStopsBeforeLicenseAndHost()
    {
        var order = new List<string>();
        var gate = new RecordingLicenseGate(order, isAuthorized: true);
        var controller = new AppStartupController(
            new FakeRecovery(order, available: true, preparationFailure: new InvalidDataException("database failed")),
            new AppRuntimeState(),
            gate,
            new LicenseRuntimeAuthorizationState());

        await Assert.ThrowsAsync<InvalidDataException>(() => controller.StartAsync(_ =>
        {
            order.Add("host");
            return Task.CompletedTask;
        }));

        Assert.Equal(["local-preparation"], order);
        Assert.Equal(0, gate.CallCount);
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
        Assert.Equal("软件已经在运行。如果找不到窗口，请在任务管理器结束「知道综合工具」进程后重试。", result.StatusMessage);
    }

    private sealed class FakeRecovery(
        List<string> order,
        bool available,
        bool rejectAsDuplicate = false,
        Exception? preparationFailure = null) : IAppRecoveryRunner
    {
        public bool IsAvailable => available;
        public string UnavailableMessage => "单实例保护尚不可用，调度未启动。";
        public int AuthorizedRecoveryCallCount { get; private set; }

        public Task PrepareLocalAsync(CancellationToken cancellationToken)
        {
            if (rejectAsDuplicate) throw new BaiduBatchTransfer.Application.Scheduling.SingleInstanceUnavailableException();
            order.Add("local-preparation");
            return preparationFailure is null
                ? Task.CompletedTask
                : Task.FromException(preparationFailure);
        }

        public Task RecoverAuthorizedAsync(CancellationToken cancellationToken)
        {
            AuthorizedRecoveryCallCount++;
            order.Add("authorized-recovery");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLicenseGate(
        List<string> order,
        bool isAuthorized,
        bool throwOnAuthorize = false,
        string? code = null) : ILicenseAuthorizationGate
    {
        public int CallCount { get; private set; }

        public Task<LicenseGateResult> AuthorizeAsync(
            LicenseOperationKind operation,
            CancellationToken cancellationToken)
        {
            CallCount++;
            order.Add($"license:{operation}");
            if (throwOnAuthorize) throw new InvalidOperationException("simulated gate failure");
            return Task.FromResult(new LicenseGateResult(
                operation,
                isAuthorized,
                UsedCachedLease: false,
                code ?? (isAuthorized ? LicenseCodes.Ok : "activation_required")));
        }
    }
}
