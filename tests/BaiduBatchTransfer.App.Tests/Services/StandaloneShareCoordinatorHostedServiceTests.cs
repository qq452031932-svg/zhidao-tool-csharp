using BaiduBatchTransfer.App.Features.Licensing;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Scheduling;
using Microsoft.Extensions.Hosting;

namespace BaiduBatchTransfer.App.Tests.Services;

public sealed class StandaloneShareCoordinatorHostedServiceTests
{
    [Fact]
    public async Task RecoveredShareWorkWaitsForStartupOrLaterAuthorization()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var authorization = new LicenseRuntimeAuthorizationState();
        var lifetime = new RecordingHostLifetime();
        var service = new StandaloneShareCoordinatorHostedService(
            _ => { started.TrySetResult(); return Task.CompletedTask; },
            _ => Task.CompletedTask,
            RemoteWriteExecutionMode.Full,
            authorization,
            lifetime);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(75);
        Assert.False(started.Task.IsCompleted);

        authorization.CompleteStartupValidation();
        await Task.Delay(75);
        Assert.False(started.Task.IsCompleted);

        await authorization.AuthorizeAfterRecoveryAsync(_ => Task.CompletedTask);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(0, lifetime.StopCalls);
    }

    [Fact]
    public async Task AlreadyAuthorizedStartWaitsForCoordinatorInitialScan()
    {
        var authorization = new LicenseRuntimeAuthorizationState();
        authorization.CompleteStartupValidation();
        await authorization.AuthorizeAfterRecoveryAsync(_ => Task.CompletedTask);
        var initialScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new StandaloneShareCoordinatorHostedService(
            _ => initialScan.Task,
            _ => Task.CompletedTask,
            RemoteWriteExecutionMode.Full,
            authorization,
            new RecordingHostLifetime());

        var starting = service.StartAsync(CancellationToken.None);

        Assert.False(starting.IsCompleted);
        initialScan.TrySetResult();
        await starting.WaitAsync(TimeSpan.FromSeconds(1));
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AlreadyAuthorizedStartFailurePropagatesToGenericHost()
    {
        var failure = new InvalidDataException("initial scan failed");
        var authorization = new LicenseRuntimeAuthorizationState();
        authorization.CompleteStartupValidation();
        await authorization.AuthorizeAfterRecoveryAsync(_ => Task.CompletedTask);
        var service = new StandaloneShareCoordinatorHostedService(
            _ => Task.FromException(failure),
            _ => Task.CompletedTask,
            RemoteWriteExecutionMode.Full,
            authorization,
            new RecordingHostLifetime());

        var observed = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.StartAsync(CancellationToken.None));

        Assert.Same(failure, observed);
    }

    [Fact]
    public async Task PartialCoordinatorStartFailureIsStoppedExactlyOnce()
    {
        var stops = 0;
        var authorization = new LicenseRuntimeAuthorizationState();
        authorization.CompleteStartupValidation();
        await authorization.AuthorizeAfterRecoveryAsync(_ => Task.CompletedTask);
        var service = new StandaloneShareCoordinatorHostedService(
            _ => Task.FromException(new InvalidDataException("initial scan failed")),
            _ =>
            {
                Interlocked.Increment(ref stops);
                return Task.CompletedTask;
            },
            RemoteWriteExecutionMode.Full,
            authorization,
            new RecordingHostLifetime());

        await Assert.ThrowsAsync<InvalidDataException>(() => service.StartAsync(CancellationToken.None));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, stops);
    }

    [Fact]
    public async Task DeferredStartFailureStopsTheApplication()
    {
        var authorization = new LicenseRuntimeAuthorizationState();
        authorization.CompleteStartupValidation();
        var failure = new InvalidDataException("delayed scan failed");
        var lifetime = new RecordingHostLifetime();
        var service = new StandaloneShareCoordinatorHostedService(
            _ => Task.FromException(failure),
            _ => Task.CompletedTask,
            RemoteWriteExecutionMode.Full,
            authorization,
            lifetime);

        await service.StartAsync(CancellationToken.None);
        await authorization.AuthorizeAfterRecoveryAsync(_ => Task.CompletedTask);

        await lifetime.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, lifetime.StopCalls);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DeferredStartFailureFlowsThroughApplicationStoppingToShutdown()
    {
        var authorization = new LicenseRuntimeAuthorizationState();
        authorization.CompleteStartupValidation();
        var lifetime = new RecordingHostLifetime();
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stoppingConsumer = new ApplicationStoppingShutdownCoordinator(
            lifetime.ApplicationStopping,
            () => true,
            operation => operation(),
            () => Task.FromResult(true),
            () => shutdown.TrySetResult(),
            () => throw new Xunit.Sdk.XunitException("Safe shutdown unexpectedly failed."));
        var service = new StandaloneShareCoordinatorHostedService(
            _ => Task.FromException(new InvalidDataException("delayed scan failed")),
            _ => Task.CompletedTask,
            RemoteWriteExecutionMode.Full,
            authorization,
            lifetime);

        await service.StartAsync(CancellationToken.None);
        await authorization.AuthorizeAfterRecoveryAsync(_ => Task.CompletedTask);

        await shutdown.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, lifetime.StopCalls);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopBeforeAuthorizationDoesNotReportFailureOrStartCoordinator()
    {
        var starts = 0;
        var authorization = new LicenseRuntimeAuthorizationState();
        var lifetime = new RecordingHostLifetime();
        var service = new StandaloneShareCoordinatorHostedService(
            _ => { Interlocked.Increment(ref starts); return Task.CompletedTask; },
            _ => Task.CompletedTask,
            RemoteWriteExecutionMode.Full,
            authorization,
            lifetime);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, starts);
        Assert.Equal(0, lifetime.StopCalls);
    }

    [Fact]
    public async Task RepeatedDeferredStartCallsStartCoordinatorOnlyOnce()
    {
        var starts = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var authorization = new LicenseRuntimeAuthorizationState();
        authorization.CompleteStartupValidation();
        var service = new StandaloneShareCoordinatorHostedService(
            _ =>
            {
                Interlocked.Increment(ref starts);
                started.TrySetResult();
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask,
            RemoteWriteExecutionMode.Full,
            authorization,
            new RecordingHostLifetime());

        await service.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
        await authorization.AuthorizeAfterRecoveryAsync(_ => Task.CompletedTask);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, starts);
    }

    private sealed class RecordingHostLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource started = new();
        private readonly CancellationTokenSource stopping = new();
        private readonly CancellationTokenSource stopped = new();

        public CancellationToken ApplicationStarted => started.Token;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => stopped.Token;
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StopCalls { get; private set; }

        public void StopApplication()
        {
            StopCalls++;
            stopping.Cancel();
            Stopped.TrySetResult();
        }
    }
}
