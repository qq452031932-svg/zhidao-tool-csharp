using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.App.Features.Licensing;
using SuperPan.Licensing.Client;

namespace BaiduBatchTransfer.App;

public interface IAppRecoveryRunner
{
    bool IsAvailable { get; }
    string UnavailableMessage { get; }
    Task PrepareLocalAsync(CancellationToken cancellationToken);
    Task RecoverAuthorizedAsync(CancellationToken cancellationToken);
}

public sealed class StartupRecoveryRunner(
    StartupRecoveryService recoveryService,
    IRemoteSelectionSessionStore remoteSelectionSessions,
    ILocalResultExportCoordinator localResultExports,
    TimeProvider timeProvider) : IAppRecoveryRunner
{
    public bool IsAvailable => true;
    public string UnavailableMessage => string.Empty;

    public Task PrepareLocalAsync(CancellationToken cancellationToken) =>
        recoveryService.PrepareLocalAsync(cancellationToken);

    public async Task RecoverAuthorizedAsync(CancellationToken cancellationToken)
    {
        var result = await recoveryService.RecoverPreparedAsync(cancellationToken).ConfigureAwait(false);
        if (!result.CoordinatorMayStart)
            throw new InvalidOperationException("启动恢复未允许调度器运行。");
        await remoteSelectionSessions.RecoverIncompleteAsync(
            timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        await localResultExports.RecoverAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class UnavailableAppRecoveryRunner : IAppRecoveryRunner
{
    public bool IsAvailable => false;
    public string UnavailableMessage => "Windows 单实例保护尚不可用，数据库和任务调度均未启动。";
    public Task PrepareLocalAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException(UnavailableMessage);
    public Task RecoverAuthorizedAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException(UnavailableMessage);
}

public sealed record AppStartupResult(
    bool CoordinatorMayStart,
    string StatusMessage,
    bool LocalUiMayOpen = true,
    bool LicenseAuthorized = true);

public sealed class AppRuntimeState
{
    public bool CoordinatorMayStart { get; internal set; }
    public string StatusMessage { get; internal set; } = "尚未执行启动恢复。";
}

public sealed class AppStartupController
{
    private readonly IAppRecoveryRunner recoveryRunner;
    private readonly AppRuntimeState runtimeState;
    private readonly ILicenseAuthorizationGate licenseGate;
    private readonly LicenseRuntimeAuthorizationState authorizationState;

    public AppStartupController(IAppRecoveryRunner recoveryRunner)
        : this(
            recoveryRunner,
            new AppRuntimeState(),
            AllowAllLicenseAuthorizationGate.Instance,
            new LicenseRuntimeAuthorizationState())
    {
    }

    public AppStartupController(
        IAppRecoveryRunner recoveryRunner,
        AppRuntimeState runtimeState,
        ILicenseAuthorizationGate licenseGate,
        LicenseRuntimeAuthorizationState authorizationState)
    {
        this.recoveryRunner = recoveryRunner ?? throw new ArgumentNullException(nameof(recoveryRunner));
        this.runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
        this.licenseGate = licenseGate ?? throw new ArgumentNullException(nameof(licenseGate));
        this.authorizationState = authorizationState ?? throw new ArgumentNullException(nameof(authorizationState));
    }

    public async Task<AppStartupResult> StartAsync(
        Func<CancellationToken, Task> startHost,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startHost);
        runtimeState.CoordinatorMayStart = false;
        if (!recoveryRunner.IsAvailable)
        {
            runtimeState.StatusMessage = recoveryRunner.UnavailableMessage;
            return new(false, runtimeState.StatusMessage, LocalUiMayOpen: false, LicenseAuthorized: false);
        }

        try
        {
            await recoveryRunner.PrepareLocalAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SingleInstanceUnavailableException)
        {
            // 占锁的进程未必有可见窗口（崩溃残留、后台测试宿主等），
            // 只说“请切换到已打开的窗口”会让用户找不到任何窗口而卡死。
            runtimeState.StatusMessage =
                "软件已经在运行。如果找不到窗口，请在任务管理器结束「知道综合工具」进程后重试。";
            return new(false, runtimeState.StatusMessage, LocalUiMayOpen: false, LicenseAuthorized: false);
        }

        LicenseGateResult license;
        try
        {
            license = await licenseGate
                .AuthorizeAsync(LicenseOperationKind.Startup, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            license = new LicenseGateResult(
                LicenseOperationKind.Startup,
                IsAuthorized: false,
                UsedCachedLease: false,
                Code: "authorization_unavailable");
        }
        authorizationState.CompleteStartupValidation();
        if (license.IsAuthorized)
        {
            await authorizationState.AuthorizeAfterRecoveryAsync(
                recoveryRunner.RecoverAuthorizedAsync,
                cancellationToken).ConfigureAwait(false);
        }
        await startHost(cancellationToken).ConfigureAwait(false);
        runtimeState.CoordinatorMayStart = true;
        runtimeState.StatusMessage = license.IsAuthorized
            ? "启动恢复完成，任务调度可以运行。"
            : "软件尚未激活，联网功能暂不可用。";
        return new(
            true,
            runtimeState.StatusMessage,
            LocalUiMayOpen: true,
            LicenseAuthorized: license.IsAuthorized);
    }

    private sealed class AllowAllLicenseAuthorizationGate : ILicenseAuthorizationGate
    {
        public static readonly AllowAllLicenseAuthorizationGate Instance = new();

        public Task<LicenseGateResult> AuthorizeAsync(
            LicenseOperationKind operation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new LicenseGateResult(
                operation,
                IsAuthorized: true,
                UsedCachedLease: false,
                LicenseCodes.Ok));
        }
    }
}
