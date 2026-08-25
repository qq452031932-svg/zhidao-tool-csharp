using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.App;

public interface IAppRecoveryRunner
{
    bool IsAvailable { get; }
    string UnavailableMessage { get; }
    Task RecoverAsync(CancellationToken cancellationToken);
}

public sealed class StartupRecoveryRunner(
    StartupRecoveryService recoveryService,
    IRemoteSelectionSessionStore remoteSelectionSessions,
    ILocalResultExportCoordinator localResultExports,
    TimeProvider timeProvider) : IAppRecoveryRunner
{
    public bool IsAvailable => true;
    public string UnavailableMessage => string.Empty;
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        var result = await recoveryService.RecoverAsync(cancellationToken).ConfigureAwait(false);
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
    public Task RecoverAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException(UnavailableMessage);
}

public sealed record AppStartupResult(bool CoordinatorMayStart, string StatusMessage);

public sealed class AppRuntimeState
{
    public bool CoordinatorMayStart { get; internal set; }
    public string StatusMessage { get; internal set; } = "尚未执行启动恢复。";
}

public sealed class AppStartupController(IAppRecoveryRunner recoveryRunner, AppRuntimeState runtimeState)
{
    public AppStartupController(IAppRecoveryRunner recoveryRunner)
        : this(recoveryRunner, new AppRuntimeState()) { }

    public async Task<AppStartupResult> StartAsync(
        Func<CancellationToken, Task> startHost,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startHost);
        runtimeState.CoordinatorMayStart = false;
        if (!recoveryRunner.IsAvailable)
        {
            runtimeState.StatusMessage = recoveryRunner.UnavailableMessage;
            return new(false, runtimeState.StatusMessage);
        }

        try
        {
            await recoveryRunner.RecoverAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SingleInstanceUnavailableException)
        {
            runtimeState.StatusMessage = "软件已经在运行，请切换到已打开的窗口。";
            return new(false, runtimeState.StatusMessage);
        }
        await startHost(cancellationToken).ConfigureAwait(false);
        runtimeState.CoordinatorMayStart = true;
        runtimeState.StatusMessage = "启动恢复完成，任务调度可以运行。";
        return new(true, runtimeState.StatusMessage);
    }
}
