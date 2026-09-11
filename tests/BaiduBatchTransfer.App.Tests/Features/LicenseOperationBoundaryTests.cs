using BaiduBatchTransfer.App.Features.Licensing;
using BaiduBatchTransfer.App;
using SuperPan.Licensing.Client;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class LicenseOperationBoundaryTests
{
    [Fact]
    public async Task StartupRejectionKeepsRemoteWorkWaitingUntilLaterActivationSucceeds()
    {
        var state = new LicenseRuntimeAuthorizationState();

        state.CompleteStartupValidation();
        var waiting = state.WaitUntilAuthorizedAsync(CancellationToken.None);

        Assert.True(state.StartupValidationCompleted);
        Assert.False(state.IsAuthorized);
        Assert.False(waiting.IsCompleted);

        var recovery = new RecordingRecovery();
        await state.AuthorizeAfterRecoveryAsync(recovery.RecoverAuthorizedAsync, CancellationToken.None);

        await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, recovery.CallCount);
        Assert.True(state.IsAuthorized);
    }

    [Fact]
    public async Task ConcurrentAuthorizationRunsRecoveryOnceBeforeReleasingRemoteWork()
    {
        var state = new LicenseRuntimeAuthorizationState();
        state.CompleteStartupValidation();
        var waiting = state.WaitUntilAuthorizedAsync();
        var recovery = new RecordingRecovery(blockFirstCall: true, waiter: waiting);

        var first = state.AuthorizeAfterRecoveryAsync(recovery.RecoverAuthorizedAsync, CancellationToken.None);
        await recovery.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = state.AuthorizeAfterRecoveryAsync(recovery.RecoverAuthorizedAsync, CancellationToken.None);

        Assert.False(waiting.IsCompleted);
        recovery.Release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, recovery.CallCount);
        Assert.True(state.StartupValidationCompleted);
        Assert.True(state.IsAuthorized);
    }

    [Fact]
    public async Task SuccessfulActivationSignalsAuthorizationAndReleasesWaitingCoordinators()
    {
        var state = new LicenseRuntimeAuthorizationState();
        state.CompleteStartupValidation();
        var transferCoordinator = state.WaitUntilAuthorizedAsync();
        var shareCoordinator = state.WaitUntilAuthorizedAsync();
        var recovery = new RecordingRecovery(waiter: transferCoordinator);
        var service = new LicenseActivationService(
            (_, _) => Task.FromResult(new LicenseGateResult(
                LicenseOperationKind.Startup,
                IsAuthorized: true,
                UsedCachedLease: false,
                LicenseCodes.Ok)),
            state,
            recovery);

        var result = await service.ActivateAsync("CARD-123", CancellationToken.None);

        Assert.True(result.IsAuthorized);
        Assert.Equal(1, recovery.CallCount);
        await Task.WhenAll(transferCoordinator, shareCoordinator)
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(state.IsAuthorized);
    }

    [Fact]
    public async Task FailedActivationLeavesCoordinatorsWaitingAndReturnsOnlySafeCode()
    {
        var state = new LicenseRuntimeAuthorizationState();
        state.CompleteStartupValidation();
        var waiting = state.WaitUntilAuthorizedAsync();
        var service = new LicenseActivationService(
            (_, _) => Task.FromResult(new LicenseGateResult(
                LicenseOperationKind.Startup,
                IsAuthorized: false,
                UsedCachedLease: false,
                "raw-secret-server-code")),
            state,
            new RecordingRecovery());

        var result = await service.ActivateAsync("CARD-123", CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.Equal("authorization_unavailable", result.Code);
        Assert.False(waiting.IsCompleted);
        Assert.False(state.IsAuthorized);
    }

    [Fact]
    public async Task RecoveryFailureKeepsCoordinatorsBlockedAndLaterActivationRetries()
    {
        var state = new LicenseRuntimeAuthorizationState();
        state.CompleteStartupValidation();
        var waiting = state.WaitUntilAuthorizedAsync();
        var recovery = new RecordingRecovery(failFirstCall: true, waiter: waiting);
        var service = new LicenseActivationService(
            (_, _) => Task.FromResult(new LicenseGateResult(
                LicenseOperationKind.Startup,
                IsAuthorized: true,
                UsedCachedLease: false,
                LicenseCodes.Ok)),
            state,
            recovery);

        var failed = await service.ActivateAsync("CARD-123", CancellationToken.None);

        Assert.False(failed.IsAuthorized);
        Assert.Equal("authorization_unavailable", failed.Code);
        Assert.False(waiting.IsCompleted);
        Assert.False(state.IsAuthorized);

        var succeeded = await service.ActivateAsync("CARD-123", CancellationToken.None);

        Assert.True(succeeded.IsAuthorized);
        Assert.Equal(2, recovery.CallCount);
        await waiting.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private sealed class RecordingRecovery(
        bool blockFirstCall = false,
        bool failFirstCall = false,
        Task? waiter = null) : IAppRecoveryRunner
    {
        public bool IsAvailable => true;
        public string UnavailableMessage => string.Empty;
        public int CallCount { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PrepareLocalAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task RecoverAuthorizedAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            Assert.False(waiter?.IsCompleted ?? false);
            Started.TrySetResult();
            if (blockFirstCall && CallCount == 1)
                await Release.Task.WaitAsync(cancellationToken);
            if (failFirstCall && CallCount == 1)
                throw new InvalidDataException("recovery failed");
        }
    }
}
