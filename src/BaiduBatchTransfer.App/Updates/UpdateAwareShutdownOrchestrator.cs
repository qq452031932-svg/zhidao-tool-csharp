namespace BaiduBatchTransfer.App.Updates;

public sealed class UpdateAwareShutdownOrchestrator
{
    public const string UpdaterLaunchFailureMessage =
        "更新程序未能启动，本机数据和当前版本未被删除。";

    private readonly Func<CancellationToken, Task<bool>> safeShutdownAsync;
    private readonly Func<bool> launchPreparedUpdateAndRestart;
    private readonly Action restoreWindow;
    private readonly Action<string> reportSafeError;
    private readonly Action exitApplication;
    private int state;

    public UpdateAwareShutdownOrchestrator(
        Func<CancellationToken, Task<bool>> safeShutdownAsync,
        Func<bool> launchPreparedUpdateAndRestart,
        Action restoreWindow,
        Action<string> reportSafeError,
        Action exitApplication)
    {
        this.safeShutdownAsync = safeShutdownAsync ??
            throw new ArgumentNullException(nameof(safeShutdownAsync));
        this.launchPreparedUpdateAndRestart = launchPreparedUpdateAndRestart ??
            throw new ArgumentNullException(nameof(launchPreparedUpdateAndRestart));
        this.restoreWindow = restoreWindow ?? throw new ArgumentNullException(nameof(restoreWindow));
        this.reportSafeError = reportSafeError ?? throw new ArgumentNullException(nameof(reportSafeError));
        this.exitApplication = exitApplication ?? throw new ArgumentNullException(nameof(exitApplication));
    }

    public async Task<bool> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
            return false;

        bool safeShutdownCompleted;
        try
        {
            safeShutdownCompleted = await safeShutdownAsync(cancellationToken);
        }
        catch
        {
            safeShutdownCompleted = false;
        }

        if (!safeShutdownCompleted)
        {
            RestoreAfterSafeShutdownFailure();
            return false;
        }

        Volatile.Write(ref state, 2);
        try
        {
            launchPreparedUpdateAndRestart();
        }
        catch
        {
            try
            {
                reportSafeError(UpdaterLaunchFailureMessage);
            }
            catch
            {
                // Reporting must not strand the process after the host has been disposed.
            }
        }
        finally
        {
            exitApplication();
        }

        return true;
    }

    private void RestoreAfterSafeShutdownFailure()
    {
        try
        {
            restoreWindow();
        }
        catch
        {
            // Recovery is best-effort and must not escape a WPF async closing handler.
        }
        finally
        {
            Volatile.Write(ref state, 0);
        }
    }
}
