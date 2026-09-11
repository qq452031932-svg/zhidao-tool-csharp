using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.App.Features.Licensing;
using Microsoft.Extensions.Hosting;

namespace BaiduBatchTransfer.App.Services;

public sealed class StandaloneShareCoordinatorHostedService : IHostedService
{
    private readonly Func<CancellationToken, Task> startCoordinator;
    private readonly Func<CancellationToken, Task> stopCoordinator;
    private readonly RemoteWriteExecutionMode remoteWriteMode;
    private readonly LicenseRuntimeAuthorizationState? authorizationState;
    private readonly IHostApplicationLifetime applicationLifetime;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object startGate = new();
    private Task? deferredStart;
    private Task? coordinatorStart;
    private int coordinatorStartAttempted;
    private int coordinatorStopped;
    private int failureReported;

    public StandaloneShareCoordinatorHostedService(
        StandaloneShareCoordinator coordinator,
        IHostApplicationLifetime applicationLifetime,
        RemoteWriteExecutionMode remoteWriteMode = RemoteWriteExecutionMode.Full,
        LicenseRuntimeAuthorizationState? authorizationState = null)
        : this(
            (coordinator ?? throw new ArgumentNullException(nameof(coordinator))).StartAsync,
            coordinator.StopAsync,
            remoteWriteMode,
            authorizationState,
            applicationLifetime)
    {
    }

    public StandaloneShareCoordinatorHostedService(
        Func<CancellationToken, Task> startCoordinator,
        Func<CancellationToken, Task> stopCoordinator,
        RemoteWriteExecutionMode remoteWriteMode = RemoteWriteExecutionMode.Full,
        LicenseRuntimeAuthorizationState? authorizationState = null,
        IHostApplicationLifetime? applicationLifetime = null)
    {
        this.startCoordinator = startCoordinator ?? throw new ArgumentNullException(nameof(startCoordinator));
        this.stopCoordinator = stopCoordinator ?? throw new ArgumentNullException(nameof(stopCoordinator));
        this.remoteWriteMode = remoteWriteMode;
        this.authorizationState = authorizationState;
        this.applicationLifetime = applicationLifetime ?? throw new ArgumentNullException(nameof(applicationLifetime));
        if (!Enum.IsDefined(remoteWriteMode)) throw new ArgumentOutOfRangeException(nameof(remoteWriteMode));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (remoteWriteMode != RemoteWriteExecutionMode.Full) return Task.CompletedTask;
        if (authorizationState is null || authorizationState.IsAuthorized)
            return GetOrStartCoordinator(cancellationToken);
        lock (startGate)
            deferredStart ??= ObserveDeferredStartAsync(lifetime.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lifetime.Cancel();
        if (deferredStart is not null)
        {
            try { await deferredStart.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
        if (Volatile.Read(ref coordinatorStartAttempted) != 0 &&
            Interlocked.Exchange(ref coordinatorStopped, 1) == 0)
            await stopCoordinator(cancellationToken).ConfigureAwait(false);
        lifetime.Dispose();
    }

    private async Task ObserveDeferredStartAsync(CancellationToken cancellationToken)
    {
        // Never continue into GetOrStartCoordinator while StartAsync still owns startGate.
        await Task.Yield();
        try
        {
            await authorizationState!.WaitUntilAuthorizedAsync(cancellationToken).ConfigureAwait(false);
            await GetOrStartCoordinator(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            if (Interlocked.Exchange(ref failureReported, 1) == 0)
                applicationLifetime.StopApplication();
        }
    }

    private Task GetOrStartCoordinator(CancellationToken cancellationToken)
    {
        lock (startGate)
            return coordinatorStart ??= StartCoordinatorAsync(cancellationToken);
    }

    private async Task StartCoordinatorAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref coordinatorStartAttempted, 1);
        await startCoordinator(cancellationToken).ConfigureAwait(false);
    }
}
