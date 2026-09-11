using System.Collections.Immutable;
using System.Reflection;
using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;
using BaiduBatchTransfer.Infrastructure.Security;
using static BaiduBatchTransfer.App.Tests.Features.ManagedChromiumProcessManagerTests;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedTransferPickerSessionTests
{
    [Fact]
    public async Task OneAccountLeaseSpansSourcePickerDirectoryFinalProofAndPermitCleanup()
    {
        using var f = await Fixture.CreateAsync();
        f.Authorization.ReleaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = f.Run();
        await f.Authorization.ReleaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var next = f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default);
        Assert.False(next.IsCompleted);
        Assert.Equal(new[] { ManagedBrowserAction.OpenTransferSource, ManagedBrowserAction.ReadTransferSource,
            ManagedBrowserAction.OpenTransferPicker, ManagedBrowserAction.ReadTransferDirectory }, f.Requests.Select(x => x.Action));

        f.Authorization.ReleaseGate.SetResult();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ManagedBrowserTransferPickerResultKind.PickerOpened, result.Kind);
        Assert.NotNull(result.Observation);
        Assert.Null(result.Quarantine);
        using var acquired = await next.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, f.Authorization.Acquired);
        Assert.Equal(1, f.Authorization.Disposed);
        Assert.Equal(6, f.Targets.Reads);
    }

    [Theory]
    [InlineData("installation-revision")]
    [InlineData("session-revision")]
    [InlineData("uid")]
    [InlineData("identity")]
    [InlineData("target")]
    [InlineData("source-uri")]
    public async Task ProofChangeAfterSourceObservationProducesZeroPickerMutation(string change)
    {
        using var f = await Fixture.CreateAsync();
        f.Targets.OnRead = async count =>
        {
            if (count != 4) return;
            if (change == "installation-revision") await f.Execute("UPDATE BrowserInstallations SET Revision=Revision+1;");
            if (change == "session-revision") await f.Execute("UPDATE ManagedBrowserSessions SET Revision=Revision+1;");
            if (change is "uid" or "identity") await f.ReplaceIdentity(change == "uid");
            if (change == "target") f.Targets.TargetId = "different-tab";
            if (change == "source-uri") f.Targets.Page = new("https://pan.baidu.com/s/different");
        };

        var result = await f.Run();

        Assert.NotEqual(ManagedBrowserTransferPickerResultKind.PickerOpened, result.Kind);
        Assert.Null(result.Observation);
        Assert.DoesNotContain(f.Requests, x => x.Action == ManagedBrowserAction.OpenTransferPicker);
        Assert.Equal(0, f.Authorization.Acquired);
        Assert.Null(result.Quarantine);
    }

    [Fact]
    public async Task PermitDenialBeforePickerDispatchReleasesWithoutQuarantine()
    {
        using var f = await Fixture.CreateAsync();
        f.Authorization.Allow = false;

        var result = await f.Run();

        Assert.Equal(ManagedBrowserTransferPickerResultKind.PageNotValidated, result.Kind);
        Assert.Null(result.Quarantine);
        Assert.DoesNotContain(f.Requests, request => request.Action == ManagedBrowserAction.OpenTransferPicker);
        using var acquired = await f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default)
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DurableChangeWhilePermitAcquisitionIsPendingCannotDispatchPicker()
    {
        using var f = await Fixture.CreateAsync();
        f.Authorization.AcquisitionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = f.Run();
        await f.Authorization.AcquisitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await f.Execute("UPDATE ManagedBrowserSessions SET Revision=Revision+1;");

        f.Authorization.AcquisitionGate.SetResult();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotEqual(ManagedBrowserTransferPickerResultKind.PickerOpened, result.Kind);
        Assert.Null(result.Observation);
        Assert.Null(result.Quarantine);
        Assert.DoesNotContain(f.Requests, request => request.Action == ManagedBrowserAction.OpenTransferPicker);
        Assert.Equal(1, f.Authorization.Acquired);
        Assert.Equal(1, f.Authorization.Disposed);
        using var acquired = await f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default)
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task BindingExpiryWhilePermitAcquisitionIsPendingCannotDispatchPicker()
    {
        using var f = await Fixture.CreateAsync();
        f.SetBindingExpiry(f.Clock.GetUtcNow().AddMilliseconds(500));
        f.Authorization.AcquisitionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = f.Run();
        await f.Authorization.AcquisitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        f.Clock.Advance(TimeSpan.FromMilliseconds(600));
        f.Authorization.AcquisitionGate.SetResult();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ManagedBrowserTransferPickerResultKind.PageNotValidated, result.Kind);
        Assert.Null(result.Observation);
        Assert.Null(result.Quarantine);
        Assert.Equal(1, f.Authorization.Acquired);
        Assert.Equal(1, f.Authorization.Disposed);
        Assert.DoesNotContain(f.Requests, request => request.Action == ManagedBrowserAction.OpenTransferPicker);
        using var acquired = await f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default)
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task OuterDeadlineDuringPermitAcquisitionIsTimedOutAndReleasesSafely()
    {
        using var f = await Fixture.CreateAsync();
        f.Timeout = 5000;
        f.Authorization.IgnoreAcquisitionCancellation = true;
        f.Authorization.AcquisitionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = f.Run();
        await f.Authorization.AcquisitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        f.Clock.Advance(TimeSpan.FromMilliseconds(f.Timeout));
        await f.Authorization.AcquisitionCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(f.Authorization.AcquisitionGate.Task.IsCompleted);
        Assert.False(run.IsCompleted);
        f.Authorization.AcquisitionGate.SetResult();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ManagedBrowserTransferPickerResultKind.TimedOut, result.Kind);
        Assert.Null(result.Observation);
        Assert.Null(result.Quarantine);
        Assert.DoesNotContain(f.Requests, request => request.Action == ManagedBrowserAction.OpenTransferPicker);
        Assert.Equal(1, f.Authorization.Acquired);
        Assert.Equal(1, f.Authorization.Disposed);
        using var acquired = await f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default)
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task InjectedClockExpiryDuringPermitAcquisitionIsTimedOutWithoutWallClockDelay()
    {
        using var f = await Fixture.CreateAsync();
        f.Timeout = 5000;
        f.Authorization.AcquisitionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = f.Run();
        await f.Authorization.AcquisitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        f.Clock.Advance(TimeSpan.FromMilliseconds(f.Timeout));
        f.Authorization.AcquisitionGate.SetResult();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ManagedBrowserTransferPickerResultKind.TimedOut, result.Kind);
        Assert.Null(result.Observation);
        Assert.Null(result.Quarantine);
        Assert.DoesNotContain(f.Requests, request => request.Action == ManagedBrowserAction.OpenTransferPicker);
        using var acquired = await f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default)
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CallerCancellationDuringPermitAcquisitionRemainsCancelled()
    {
        using var f = await Fixture.CreateAsync();
        f.Authorization.AcquisitionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = new CancellationTokenSource();
        var run = f.Run(cancelled.Token);
        await f.Authorization.AcquisitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancelled.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ManagedBrowserTransferPickerResultKind.Cancelled, result.Kind);
        Assert.Null(result.Quarantine);
        Assert.DoesNotContain(f.Requests, request => request.Action == ManagedBrowserAction.OpenTransferPicker);
        using var acquired = await f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default)
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void SessionCannotAcceptAForeignAccountLeaseFactory()
    {
        var constructors = typeof(ManagedTransferPickerSession).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.DoesNotContain(constructors.SelectMany(constructor => constructor.GetParameters()), parameter =>
            parameter.ParameterType == typeof(Func<Guid, CancellationToken, Task<IDisposable>>));
    }

    [Fact]
    public void PickerSessionExposesOnlyAnOpaqueHeldPreparationContinuation()
    {
        var method = typeof(ManagedTransferPickerSession).GetMethod(
            "BeginHeldPreparationAsync", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        Assert.Equal(typeof(Task<ManagedTransferPickerHeldPreparationResult>), method!.ReturnType);
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(ManagedTransferPickerHeldPreparation)));
        Assert.False(typeof(IAsyncDisposable).IsAssignableFrom(typeof(ManagedTransferPickerHeldPreparation)));
    }

    [Fact]
    public async Task ConsumedHeldPreparationTransfersDeadlineOwnershipToReturnedLease()
    {
        using var f = await Fixture.CreateAsync();
        var result = await f.BeginHeldPreparation();
        var held = Assert.IsType<ManagedTransferPickerHeldPreparation>(result.Preparation);

        var lease = held.ConsumeAccountLease();
        lease.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = held.Deadline.Token);
        using var reacquired = await f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default)
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CancellationDuringPermitCleanupAfterClickQuarantinesLease()
    {
        using var f = await Fixture.CreateAsync();
        f.Authorization.ReleaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = new CancellationTokenSource();
        var run = f.Run(cancelled.Token);
        await f.Authorization.ReleaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        Assert.False(run.IsCompleted);
        f.Authorization.ReleaseGate.SetResult();

        var result = await run;

        Assert.Equal(ManagedBrowserTransferPickerResultKind.OutcomeUnknown, result.Kind);
        Assert.NotNull(result.Quarantine);
        Assert.Null(result.Observation);
        Assert.False(f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default).IsCompleted);
    }

    [Theory]
    [InlineData("durable")]
    [InlineData("target")]
    [InlineData("source")]
    public async Task PickerOpenedIsNotPublishedUntilFinalDurableAndExactProof(string failure)
    {
        using var f = await Fixture.CreateAsync();
        f.AfterDirectoryRead = async () =>
        {
            if (failure == "durable") await f.Execute("UPDATE ManagedBrowserSessions SET Revision=Revision+1;");
            if (failure == "target") f.Targets.TargetId = "different-tab";
            if (failure == "source") f.Targets.Page = new("https://pan.baidu.com/s/different");
        };

        var result = await f.Run();

        Assert.Equal(ManagedBrowserTransferPickerResultKind.OutcomeUnknown, result.Kind);
        Assert.Null(result.Observation);
        Assert.NotNull(result.Quarantine);
        Assert.False(f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default).IsCompleted);
    }

    [Fact]
    public async Task CancellationBeforePickerWaitsForPendingSourceSendBeforeReleasing()
    {
        using var f = await Fixture.CreateAsync();
        using var cancelled = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<ManagedBrowserResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.SendOverride = (request, _) =>
        {
            if (request.Action == ManagedBrowserAction.OpenTransferSource) return f.NormalResponse(request);
            started.TrySetResult();
            return pending.Task;
        };
        var run = f.Run(cancelled.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ManagedBrowserTransferPickerResultKind.Cancelled, result.Kind);
        Assert.Null(result.Quarantine);
        var next = f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default);
        Assert.False(next.IsCompleted);

        pending.SetException(new IOException("private pending source failure"));
        using var acquired = await next.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.DoesNotContain("private", result.ToString());
    }

    [Fact]
    public async Task OutcomeUnknownTransfersLeaseToSelfReconcilingQuarantine()
    {
        using var f = await Fixture.CreateAsync();
        f.PickerKind = ManagedBrowserTransferPickerResultKind.PageNotValidated;
        var result = await f.Run();
        var quarantine = Assert.IsType<ManagedTransferPickerQuarantine>(result.Quarantine);
        Assert.Equal(ManagedBrowserTransferPickerResultKind.OutcomeUnknown, result.Kind);
        Assert.Null(result.Observation);
        var next = f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default);
        Assert.False(next.IsCompleted);

        var status = await quarantine.ReconcileAsync(2000, default);

        Assert.Equal(ManagedTransferPickerReconciliationStatus.Released, status);
        using var acquired = await next.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ManagedBrowserAction.ReadTransferDirectory, f.Requests[^1].Action);
    }

    [Fact]
    public async Task ReconciliationDeadlineUsesInjectedClockInsteadOfWallClock()
    {
        using var f = await Fixture.CreateAsync();
        f.PickerKind = ManagedBrowserTransferPickerResultKind.PageNotValidated;
        var quarantine = Assert.IsType<ManagedTransferPickerQuarantine>((await f.Run()).Quarantine);
        f.AfterDirectoryRead = () =>
        {
            f.Clock.Advance(TimeSpan.FromMilliseconds(5000));
            return Task.CompletedTask;
        };

        var status = await quarantine.ReconcileAsync(5000, default);

        Assert.Equal(ManagedTransferPickerReconciliationStatus.RetainedTimedOut, status);
        Assert.False(f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default).IsCompleted);
    }

    [Theory]
    [InlineData("stale", "RetainedStale")]
    [InlineData("target", "RetainedMismatch")]
    [InlineData("failed", "RetainedFailed")]
    [InlineData("directory", "RetainedInconclusive")]
    public async Task StaleMismatchedOrInconclusiveReconciliationRetainsLease(string failure, string expected)
    {
        using var f = await Fixture.CreateAsync();
        f.PickerKind = ManagedBrowserTransferPickerResultKind.PageNotValidated;
        var quarantine = Assert.IsType<ManagedTransferPickerQuarantine>((await f.Run()).Quarantine);
        if (failure == "stale") await f.Execute("UPDATE ManagedBrowserSessions SET Revision=Revision+1;");
        if (failure == "target") f.Targets.TargetId = "different-tab";
        if (failure == "failed") f.SendOverride = (_, _) => Task.FromException<ManagedBrowserResponse>(new IOException("private read failure"));
        if (failure == "directory") f.DirectoryKind = ManagedBrowserDirectoryListingResultKind.PageNotValidated;

        Assert.Equal(expected, (await quarantine.ReconcileAsync(2000, default)).ToString());

        Assert.False(f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default).IsCompleted);
    }

    [Fact]
    public async Task CancelledPendingReconciliationSendRetainsUntilSettlementAndLaterConclusiveRetry()
    {
        using var f = await Fixture.CreateAsync();
        f.PickerKind = ManagedBrowserTransferPickerResultKind.PageNotValidated;
        var quarantine = Assert.IsType<ManagedTransferPickerQuarantine>((await f.Run()).Quarantine);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<ManagedBrowserResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.SendOverride = (request, _) =>
        {
            started.TrySetResult();
            return pending.Task;
        };
        using var cancelled = new CancellationTokenSource();
        var reconcile = quarantine.ReconcileAsync(2000, cancelled.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        Assert.Equal(ManagedTransferPickerReconciliationStatus.RetainedCancelled,
            await reconcile.WaitAsync(TimeSpan.FromSeconds(2)));
        var next = f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default);
        Assert.False(next.IsCompleted);

        var captured = f.Requests[^1];
        pending.SetResult(f.DirectoryResponse(captured));
        f.SendOverride = null;
        Assert.Equal(ManagedTransferPickerReconciliationStatus.Released,
            await quarantine.ReconcileAsync(2000, default));
        using var acquired = await next.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CancelledPendingReconciliationObservesLateFaultWithoutRetry()
    {
        const string marker = "private-unretried-quarantine-read-fault";
        var unobserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(object? _, UnobservedTaskExceptionEventArgs args)
        {
            if (args.Exception.ToString().Contains(marker, StringComparison.Ordinal)) unobserved.TrySetResult();
            args.SetObserved();
        }
        TaskScheduler.UnobservedTaskException += Observe;
        try
        {
            var weak = await CreateLateFaultAsync();
            for (var attempt = 0; attempt < 8 && weak.IsAlive; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(25);
            }
            Assert.False(unobserved.Task.IsCompleted,
                "A detached quarantine read exposed its private late failure without a retry.");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Observe;
        }

        static async Task<WeakReference> CreateLateFaultAsync()
        {
            using var f = await Fixture.CreateAsync();
            f.PickerKind = ManagedBrowserTransferPickerResultKind.PageNotValidated;
            var quarantine = Assert.IsType<ManagedTransferPickerQuarantine>((await f.Run()).Quarantine);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new TaskCompletionSource<ManagedBrowserResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = pending.Task;
            f.SendOverride = (_, _) => { started.TrySetResult(); return task; };
            using var cancelled = new CancellationTokenSource();
            var reconcile = quarantine.ReconcileAsync(2000, cancelled.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancelled.Cancel();
            Assert.Equal(ManagedTransferPickerReconciliationStatus.RetainedCancelled,
                await reconcile.WaitAsync(TimeSpan.FromSeconds(2)));
            f.SendOverride = null;
            pending.SetException(new IOException(marker));
            return new WeakReference(task);
        }
    }

    [Fact]
    public async Task ReconciliationIsSingleFlightAndCannotReleaseTwice()
    {
        using var f = await Fixture.CreateAsync();
        f.PickerKind = ManagedBrowserTransferPickerResultKind.PageNotValidated;
        var quarantine = Assert.IsType<ManagedTransferPickerQuarantine>((await f.Run()).Quarantine);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<ManagedBrowserResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.SendOverride = (request, _) => { started.TrySetResult(); return pending.Task; };
        var first = quarantine.ReconcileAsync(2000, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ManagedTransferPickerReconciliationStatus.AlreadyInProgress,
            await quarantine.ReconcileAsync(2000, default));
        var captured = f.Requests[^1];
        pending.SetResult(f.DirectoryResponse(captured));
        Assert.Equal(ManagedTransferPickerReconciliationStatus.Released, await first);
        Assert.Equal(ManagedTransferPickerReconciliationStatus.AlreadyReleased,
            await quarantine.ReconcileAsync(2000, default));
    }

    [Fact]
    public async Task QuarantineHasNoRawReleaseSurfaceAndAllStringsRedactPrivateEvidence()
    {
        using var f = await Fixture.CreateAsync();
        f.PickerKind = ManagedBrowserTransferPickerResultKind.PageNotValidated;
        var result = await f.Run();
        var quarantine = Assert.IsType<ManagedTransferPickerQuarantine>(result.Quarantine);
        Assert.False(typeof(IDisposable).IsAssignableFrom(quarantine.GetType()));
        Assert.False(typeof(IAsyncDisposable).IsAssignableFrom(quarantine.GetType()));
        var methods = typeof(ManagedTransferPickerQuarantine).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.DoesNotContain(methods, method => method.Name.Contains("Release", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { typeof(int), typeof(CancellationToken) },
            typeof(ManagedTransferPickerQuarantine).GetMethod("ReconcileAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetParameters().Select(x => x.ParameterType));
        foreach (var text in new[] { result.ToString(), quarantine.ToString() })
        {
            Assert.DoesNotContain(f.Identity.StableBaiduUserId, text);
            Assert.DoesNotContain(Fixture.SourceUri, text);
            Assert.DoesNotContain("source-name", text);
            Assert.DoesNotContain("directory-name", text);
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal const string SourceUri = "https://pan.baidu.com/s/1synthetic?pwd=aB12";
        internal required string Root;
        internal required SqliteConnectionFactory Factory;
        internal required ManagedBrowserSessionReference Identity;
        internal required BrowserInstallationIdentity Installation;
        internal required FakeExecutable Executable;
        internal required FakeProfile ProfileLease;
        internal readonly FakeProcesses Processes = new();
        internal readonly FakeListeners Listeners = new();
        internal readonly SessionTargets Targets = new();
        internal ManagedChromiumProcessManager Manager = null!;
        internal ManagedBrowserLifecycleCoordinator Lifecycle = null!;
        internal ManagedChromiumSessionHandle Handle = null!;
        internal readonly Authorization Authorization = new();
        internal readonly ManualTimeProvider Clock = new(DateTimeOffset.UtcNow);
        internal readonly List<ManagedBrowserRequest> Requests = [];
        internal Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>>? SendOverride;
        internal Func<Task>? AfterDirectoryRead;
        internal ManagedBrowserTransferPickerResultKind PickerKind = ManagedBrowserTransferPickerResultKind.PickerOpened;
        internal ManagedBrowserDirectoryListingResultKind DirectoryKind = ManagedBrowserDirectoryListingResultKind.Observed;
        internal int Timeout = 2000;
        private BaiduCandidateDomMutationRequest mutation = null!;

        internal static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "picker-session-" + Guid.NewGuid().ToString("N"));
            var factory = new SqliteConnectionFactory(Path.Combine(root, "synthetic.db"));
            await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
            var installation = new BrowserInstallationIdentity(Guid.NewGuid(), BrowserFamily.Chrome,
                @"C:\SyntheticBrowser", @"C:\SyntheticBrowser\chrome.exe", "Google LLC", "Google Chrome", "synthetic-file");
            var identity = new ManagedBrowserSessionReference(Guid.NewGuid(), installation.InstallationId, Guid.NewGuid(),
                "synthetic-user", BrowserFamily.Chrome, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            var store = new SqliteManagedBrowserStateStore(factory);
            Assert.Equal(ManagedBrowserBindingStatus.Bound, await store.BindLoginAsync(true, installation, identity, null, default));
            var profile = ManagedBrowserProfileDescriptor.FromStoredSession(identity, root);
            var f = new Fixture { Root = root, Factory = factory, Identity = identity, Installation = installation,
                Executable = new(installation), ProfileLease = new(profile) };
            f.Manager = new((_, _) => Task.FromResult<IBrowserExecutableLaunchLease>(f.Executable),
                (_, _) => Task.FromResult<IManagedBrowserProfileLease>(f.ProfileLease), f.Processes, f.Listeners,
                f.Targets, TimeSpan.FromMilliseconds(200));
            f.Lifecycle = new(store, f.Manager, root);
            f.Handle = (await f.Manager.LaunchAsync(installation, profile)).Session!;
            f.Targets.Reads = 0;
            f.mutation = f.Mutation();
            return f;
        }

        internal Task<ManagedTransferPickerSessionResult> Run(CancellationToken token = default) =>
            new ManagedTransferPickerSession(Lifecycle, Manager, Send, Authorization, Clock)
                .OpenAsync(mutation, BaiduExecutionContext.Managed(Identity), Handle, SourceUri, Timeout, token);

        internal Task<ManagedTransferPickerHeldPreparationResult> BeginHeldPreparation()
        {
            var webIdentity = new WebAutomationRequestIdentity(mutation.Authorization.OperationId,
                mutation.Authorization.BatchId, Identity.AccountId, mutation.Authorization.Generation,
                WebAutomationOwnerKind.Transfer, ExecutionProfileSnapshot.Managed(
                    Identity.BrowserFamily, Identity.ProfileId, Identity.InstallationId, Identity.SessionId));
            var candidate = new BaiduCandidateSessionBinding(
                mutation.Authorization, mutation.Binding, Identity.StableBaiduUserId);
            return new ManagedTransferPickerSession(Lifecycle, Manager, Send, Authorization, Clock)
                .BeginHeldPreparationAsync(webIdentity, candidate, BaiduExecutionContext.Managed(Identity),
                    Handle, SourceUri, Timeout);
        }

        internal void SetBindingExpiry(DateTimeOffset expiresAtUtc) => mutation = new(
            mutation.Authorization, mutation.Binding with { ExpiresAtUtc = expiresAtUtc }, mutation.Action,
            mutation.ReadBinding, mutation.TypedValue, mutation.TargetExactId);

        private BaiduCandidateDomMutationRequest Mutation()
        {
            var capability = new AdapterCapabilityVersion("BaiduTransfer", "protocol", "selectors", "synthetic-build");
            var auth = new AdapterWriteAuthorizationRequest(capability, Identity.AccountId, Guid.NewGuid(), 7,
                Guid.NewGuid(), AdapterWriteAction.EnsureDirectory);
            return new(auth, new(AdapterWriteAuthorizationSource.ConsumedLease, capability, auth.AccountId,
                auth.OperationId, auth.Generation, auth.BatchId, Clock.GetUtcNow(), Clock.GetUtcNow().AddMinutes(5), false, null),
                BaiduAutomationAction.OpenDestinationPicker,
                new(new(SourceUri), BaiduCandidatePageState.ExternalShare, Identity.StableBaiduUserId, ["source-item"], null, null), null);
        }

        private Task<ManagedBrowserResponse> Send(ManagedBrowserRequest request, CancellationToken token)
        {
            Requests.Add(request);
            return SendOverride is null ? NormalResponse(request) : SendOverride(request, token);
        }

        internal async Task<ManagedBrowserResponse> NormalResponse(ManagedBrowserRequest request)
        {
            if (request.Action == ManagedBrowserAction.OpenTransferSource)
            {
                Targets.Page = new(SourceUri);
                return ManagedBrowserResponse.For(request,
                    new ManagedBrowserTransferSourceResult(ManagedBrowserTransferSourceResultKind.SourceOpened));
            }
            if (request.Action == ManagedBrowserAction.ReadTransferSource)
            {
                var item = new ManagedBrowserSourceItem("source-item", ManagedBrowserSourceItemKind.File, "source-name");
                var snapshot = new ManagedBrowserSourceSnapshot(Identity.StableBaiduUserId, "source-generation", 1,
                    true, true, [item], new string('0', 64));
                snapshot = new(Identity.StableBaiduUserId, snapshot.GenerationToken, 1, true, true, [item],
                    snapshot.ComputeFingerprint(SourceUri));
                return ManagedBrowserResponse.For(request,
                    new ManagedBrowserSourceListingResult(ManagedBrowserSourceListingResultKind.Observed, snapshot));
            }
            if (request.Action == ManagedBrowserAction.OpenTransferPicker)
                return ManagedBrowserResponse.For(request, new ManagedBrowserTransferPickerResult(PickerKind));
            if (AfterDirectoryRead is not null) await AfterDirectoryRead();
            return DirectoryKind == ManagedBrowserDirectoryListingResultKind.Observed
                ? DirectoryResponse(request)
                : ManagedBrowserResponse.For(request, new ManagedBrowserDirectoryListingResult(DirectoryKind));
        }

        internal ManagedBrowserResponse DirectoryResponse(ManagedBrowserRequest request)
        {
            var item = new ManagedBrowserSourceItem("directory-item", ManagedBrowserSourceItemKind.Folder, "directory-name");
            var snapshot = new ManagedBrowserDirectorySnapshot(Identity.StableBaiduUserId, "directory-generation",
                "root", "/", ["root"], 1, true, true, [item], new string('0', 64));
            snapshot = new(Identity.StableBaiduUserId, snapshot.GenerationToken, "root", "/", ["root"], 1,
                true, true, [item], snapshot.ComputeFingerprint(SourceUri));
            return ManagedBrowserResponse.For(request,
                new ManagedBrowserDirectoryListingResult(ManagedBrowserDirectoryListingResultKind.Observed, snapshot));
        }

        internal async Task Execute(string sql)
        {
            await using var connection = await Factory.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        internal async Task ReplaceIdentity(bool changeUid)
        {
            var replacement = new ManagedBrowserSessionReference(Guid.NewGuid(), Installation.InstallationId, Identity.AccountId,
                changeUid ? "changed-private-user" : Identity.StableBaiduUserId, Identity.BrowserFamily,
                Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            await Execute($"UPDATE ManagedBrowserSessions SET State='Retired',Revision=Revision+1 WHERE SessionId='{Identity.SessionId:D}';" +
                $"UPDATE Accounts SET BaiduUserId='{replacement.StableBaiduUserId}';");
            await new SqliteManagedBrowserStateStore(Factory).CreateSessionAsync(replacement, default);
            await Execute($"UPDATE ManagedBrowserSessions SET State='Active',Revision=Revision+1 WHERE SessionId='{replacement.SessionId:D}';");
        }

        public void Dispose() => Manager.Dispose();
    }

    private sealed class SessionTargets : IManagedBrowserTargetProbe
    {
        internal Uri Page = new("https://pan.baidu.com/");
        internal string TargetId = "fixed-tab";
        internal int Reads;
        internal Func<int, Task>? OnRead;
        public async Task<ManagedBrowserTargetSnapshot?> ReadAsync(DevToolsActivePort port, string? exactTargetId,
            CancellationToken token, Action<string>? targetDiscovered = null)
        {
            Reads++;
            if (OnRead is not null) await OnRead(Reads);
            return new(port.BrowserId, TargetId, Page);
        }
    }

    private sealed class Authorization : IBaiduCandidateActionAuthorizationSource
    {
        internal TaskCompletionSource? ReleaseGate;
        internal TaskCompletionSource? AcquisitionGate;
        internal readonly TaskCompletionSource AcquisitionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource AcquisitionCancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Allow = true;
        internal bool IgnoreAcquisitionCancellation;
        internal int Acquired, Disposed;
        public ValueTask<bool> IsAuthorizedAsync(BaiduCandidateDomRequest request, CancellationToken token) => ValueTask.FromResult(false);
        public ValueTask<IBaiduCandidateMutationPermit?> TryAcquireMutationPermitAsync(BaiduCandidateDomRequest request, CancellationToken token) =>
            ValueTask.FromResult<IBaiduCandidateMutationPermit?>(null);
        public async ValueTask<IBaiduCandidateMutationPermit?> TryAcquireTransferMutationPermitAsync(BaiduCandidateDomMutationRequest request,
            CancellationToken token)
        {
            Acquired++;
            AcquisitionStarted.TrySetResult();
            using var cancellationRegistration = token.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(), AcquisitionCancellationObserved);
            if (AcquisitionGate is not null)
            {
                if (IgnoreAcquisitionCancellation) await AcquisitionGate.Task;
                else await AcquisitionGate.Task.WaitAsync(token);
            }
            return Allow ? new Permit(this) : null;
        }
        private sealed class Permit(Authorization owner) : IBaiduCandidateMutationPermit
        {
            public async ValueTask DisposeAsync()
            {
                owner.Disposed++;
                owner.ReleaseStarted.TrySetResult();
                if (owner.ReleaseGate is not null) await owner.ReleaseGate.Task;
            }
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<ManualTimer> timers = [];
        private long timestamp;
        private DateTimeOffset current = now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => current;
        public override long GetTimestamp() => timestamp;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (gate) timers.Add(timer);
            return timer;
        }

        internal void Advance(TimeSpan duration)
        {
            ManualTimer[] due;
            lock (gate)
            {
                current += duration;
                timestamp += duration.Ticks;
                due = timers.Where(timer => timer.IsDue(timestamp)).ToArray();
            }
            foreach (var timer in due) timer.Fire(timestamp);
        }

        private void Remove(ManualTimer timer)
        {
            lock (gate) timers.Remove(timer);
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            private long dueAt = long.MaxValue;
            private long periodTicks = Timeout.InfiniteTimeSpan.Ticks;
            private bool disposed;

            internal bool IsDue(long now) => !disposed && dueAt <= now;

            internal void Fire(long now)
            {
                if (disposed || dueAt > now) return;
                dueAt = periodTicks == Timeout.InfiniteTimeSpan.Ticks ? long.MaxValue : now + periodTicks;
                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (disposed) return false;
                dueAt = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.timestamp + dueTime.Ticks;
                periodTicks = period.Ticks;
                return true;
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                owner.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
