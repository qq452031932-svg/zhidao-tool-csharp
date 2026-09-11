using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;
using BaiduBatchTransfer.Infrastructure.Security;
using static BaiduBatchTransfer.App.Tests.Features.ManagedChromiumProcessManagerTests;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedTransferSourceSessionTests
{
    [Theory]
    [InlineData(ManagedBrowserSourceItemKind.Folder)]
    [InlineData(ManagedBrowserSourceItemKind.File)]
    public async Task OpensThenReadsExactOwnedSourceWithImmutableBindings(ManagedBrowserSourceItemKind kind)
    {
        using var f = await Fixture.CreateAsync();
        f.ItemKind = kind;
        var result = await f.Run();
        Assert.Equal(ManagedBrowserSourceListingResultKind.Observed, result.Kind);
        var item = Assert.Single(result.Observation!.Items);
        Assert.Equal("source-item", item.ExactId);
        Assert.Equal("synthetic name", item.ServerName);
        Assert.Equal(kind.ToString(), item.Kind.ToString());
        Assert.Equal(new[] { ManagedBrowserAction.OpenTransferSource, ManagedBrowserAction.ReadTransferSource }, f.Requests.Select(r => r.Action));
        Assert.All(f.Requests, request =>
        {
            Assert.Equal(f.OperationId, request.OperationId);
            Assert.Equal(f.Identity.AccountId, request.AccountId);
            Assert.Equal(7, request.Generation);
        });
        Assert.NotEqual(f.Requests[0].CorrelationId, f.Requests[1].CorrelationId);
        var opened = Assert.IsType<ManagedBrowserOpenTransferSourceRequest>(f.Requests[0].Payload);
        var read = Assert.IsType<ManagedBrowserReadTransferSourceRequest>(f.Requests[1].Payload).Binding;
        Assert.Equal(opened.BrowserId, read.BrowserId);
        Assert.Equal(opened.TargetId, read.TargetId);
        Assert.Equal(opened.DevToolsEndpoint, read.DevToolsEndpoint);
        Assert.Equal(opened.SourceUri, read.SourceUri);
        Assert.Equal(opened.ExpectedBaiduUserId, read.ExpectedBaiduUserId);
        Assert.InRange(read.TimeoutMilliseconds, 1, opened.TimeoutMilliseconds);
        Assert.Equal(new[] { "fixed-tab", "fixed-tab", "fixed-tab" }, f.Targets.ExactTargets);
        Assert.Equal(1, f.Processes.LaunchCount);
        Assert.Equal(0, f.Processes.Process.Terminations);
        Assert.False(f.Executable.Disposed);
        Assert.False(f.ProfileLease.Disposed);
        Assert.DoesNotContain(Fixture.SourceUri, result.ToString());
        Assert.DoesNotContain(f.Identity.StableBaiduUserId, result.ToString());
    }

    [Theory]
    [InlineData("operation")] [InlineData("generation")] [InlineData("source")]
    [InlineData("timeout-zero")] [InlineData("timeout-large")] [InlineData("legacy")]
    [InlineData("null-context")] [InlineData("null-handle")]
    public async Task InvalidOrLegacyInputNeverUsesHelper(string failure)
    {
        using var f = await Fixture.CreateAsync();
        if (failure == "operation") f.OperationId = Guid.Empty;
        if (failure == "generation") f.Generation = 0;
        if (failure == "source") f.Source = "https://evil.example/s/source";
        if (failure == "timeout-zero") f.Timeout = 0;
        if (failure == "timeout-large") f.Timeout = ManagedBrowserProtocol.MaximumOperationTimeoutMilliseconds + 1;
        if (failure == "legacy") f.Context = BaiduExecutionContext.Legacy(new AccountSession("synthetic",
            [new SessionCookie("synthetic", "not-a-credential", ".baidu.com", "/", null, true, true)], DateTimeOffset.UtcNow));
        if (failure == "null-context") f.Context = null!;
        if (failure == "null-handle") f.Handle = null!;
        Assert.Equal(ManagedBrowserSourceListingResultKind.PageNotValidated, (await f.Run()).Kind);
        Assert.Empty(f.Requests);
        Assert.Empty(f.Targets.ExactTargets);
    }

    [Theory]
    [InlineData("session")] [InlineData("installation")] [InlineData("account")]
    [InlineData("uid")] [InlineData("family")] [InlineData("profile")] [InlineData("ownership")]
    public async Task EveryImmutableExecutionIdentityFieldMustMatchPersistedSession(string changed)
    {
        using var f = await Fixture.CreateAsync();
        f.Context = BaiduExecutionContext.Managed(f.ChangeIdentity(changed));
        var result = await f.Run();
        Assert.Equal(ManagedBrowserSourceListingResultKind.AccountIdentityMismatch, result.Kind);
        Assert.Null(result.Observation);
        Assert.Empty(f.Requests);
    }

    [Theory]
    [InlineData("installation")] [InlineData("family")] [InlineData("account")]
    [InlineData("profile")] [InlineData("ownership")] [InlineData("path")]
    public async Task EveryHandleBindingMustMatchPersistedIdentityBeforeHelper(string changed)
    {
        using var f = await Fixture.CreateAsync();
        var identity = f.ChangeIdentity(changed);
        var profile = ManagedBrowserProfileDescriptor.FromStoredSession(identity, changed == "path" ? Path.Combine(f.Root, "other") : f.Root);
        var installation = changed == "installation" || changed == "family"
            ? new BrowserInstallationIdentity(identity.InstallationId, identity.BrowserFamily, @"C:\SyntheticBrowser",
                changed == "family" ? @"C:\SyntheticBrowser\msedge.exe" : @"C:\SyntheticBrowser\chrome.exe",
                changed == "family" ? "Microsoft Corporation" : "Google LLC",
                changed == "family" ? "Microsoft Edge" : "Google Chrome", "synthetic-file") : f.Installation;
        f.Handle = new(123, installation, profile, Fixture.Port(), "fixed-tab");
        Assert.Equal(ManagedBrowserSourceListingResultKind.AccountIdentityMismatch, (await f.Run()).Kind);
        Assert.Empty(f.Requests);
    }

    [Theory]
    [InlineData("missing")] [InlineData("retired")]
    public async Task MissingOrRetiredSessionNeverUsesHelper(string failure)
    {
        using var f = await Fixture.CreateAsync();
        await f.Execute(failure == "missing" ? "DELETE FROM ManagedBrowserSessions;" : "UPDATE ManagedBrowserSessions SET State='Retired',Revision=Revision+1;");
        Assert.Equal(ManagedBrowserSourceListingResultKind.AccountIdentityMismatch, (await f.Run()).Kind);
        Assert.Empty(f.Requests);
    }

    [Theory]
    [InlineData("copy", ManagedBrowserSourceListingResultKind.BrowserUnavailable)]
    [InlineData("owner", ManagedBrowserSourceListingResultKind.BrowserUnavailable)]
    [InlineData("initial-page", ManagedBrowserSourceListingResultKind.PageNotValidated)]
    [InlineData("share-page", ManagedBrowserSourceListingResultKind.PageNotValidated)]
    public async Task InitialHandleProofFailureNeverOpensSource(string failure, ManagedBrowserSourceListingResultKind expected)
    {
        using var f = await Fixture.CreateAsync();
        if (failure == "copy") f.Handle = new(123, f.Installation, f.ProfileLease.Profile, Fixture.Port(), "fixed-tab");
        if (failure == "owner") f.Listeners.Owner = 999;
        if (failure == "initial-page") f.Targets.Page = new("https://evil.example/");
        if (failure == "share-page") f.Targets.Page = new(Fixture.SourceUri);
        Assert.Equal(expected, (await f.Run()).Kind);
        Assert.Empty(f.Requests);
        Assert.Equal(1, f.Processes.LaunchCount);
    }

    [Theory]
    [InlineData(ManagedBrowserTransferSourceResultKind.ExtractionRequired, ManagedBrowserSourceListingResultKind.ExtractionRequired)]
    [InlineData(ManagedBrowserTransferSourceResultKind.LoginRequired, ManagedBrowserSourceListingResultKind.LoginRequired)]
    [InlineData(ManagedBrowserTransferSourceResultKind.VerificationRequired, ManagedBrowserSourceListingResultKind.VerificationRequired)]
    [InlineData(ManagedBrowserTransferSourceResultKind.PageNotValidated, ManagedBrowserSourceListingResultKind.PageNotValidated)]
    [InlineData(ManagedBrowserTransferSourceResultKind.BrowserUnavailable, ManagedBrowserSourceListingResultKind.BrowserUnavailable)]
    [InlineData(ManagedBrowserTransferSourceResultKind.AccountIdentityMismatch, ManagedBrowserSourceListingResultKind.AccountIdentityMismatch)]
    [InlineData(ManagedBrowserTransferSourceResultKind.TimedOut, ManagedBrowserSourceListingResultKind.TimedOut)]
    [InlineData(ManagedBrowserTransferSourceResultKind.Cancelled, ManagedBrowserSourceListingResultKind.Cancelled)]
    public async Task NonSuccessOpenNeverReadsOrRetries(ManagedBrowserTransferSourceResultKind stage, ManagedBrowserSourceListingResultKind expected)
    {
        using var f = await Fixture.CreateAsync();
        f.OpenKind = stage;
        var result = await f.Run();
        Assert.Equal(expected, result.Kind);
        Assert.Null(result.Observation);
        Assert.Single(f.Requests);
        Assert.Single(f.Targets.ExactTargets);
    }

    [Theory]
    [InlineData("correlation")] [InlineData("operation")] [InlineData("account")]
    [InlineData("generation")] [InlineData("action")] [InlineData("type")]
    public async Task WrongOpenEnvelopeCannotAdvanceToRead(string changed)
    {
        using var f = await Fixture.CreateAsync();
        f.OverrideSend = (request, _) => Task.FromResult(new ManagedBrowserResponse(
            changed == "correlation" ? Guid.NewGuid() : request.CorrelationId,
            changed == "operation" ? Guid.NewGuid() : request.OperationId,
            changed == "account" ? Guid.NewGuid() : request.AccountId,
            changed == "generation" ? request.Generation + 1 : request.Generation,
            changed == "action" ? ManagedBrowserAction.ObserveAccountIdentity : request.Action,
            changed is "type" or "action" ? new ManagedBrowserIdentityResult(ManagedBrowserIdentityResultKind.LoginRequired)
                : new ManagedBrowserTransferSourceResult(ManagedBrowserTransferSourceResultKind.SourceOpened)));
        Assert.Equal(ManagedBrowserSourceListingResultKind.PageNotValidated, (await f.Run()).Kind);
        Assert.Single(f.Requests);
    }

    [Theory]
    [InlineData("page", ManagedBrowserSourceListingResultKind.PageNotValidated)]
    [InlineData("owner", ManagedBrowserSourceListingResultKind.BrowserUnavailable)]
    public async Task SourceProofFailureAfterOpenNeverReads(string failure, ManagedBrowserSourceListingResultKind expected)
    {
        using var f = await Fixture.CreateAsync();
        f.AfterOpen = () =>
        {
            if (failure == "page") f.Targets.Page = new("https://pan.baidu.com/s/wrong");
            else f.Listeners.Owner = 999;
            return Task.CompletedTask;
        };
        Assert.Equal(expected, (await f.Run()).Kind);
        Assert.Single(f.Requests);
    }

    [Theory]
    [InlineData("read-denial", ManagedBrowserSourceListingResultKind.ExtractionRequired)]
    [InlineData("uid", ManagedBrowserSourceListingResultKind.AccountIdentityMismatch)]
    [InlineData("late-page", ManagedBrowserSourceListingResultKind.PageNotValidated)]
    [InlineData("late-owner", ManagedBrowserSourceListingResultKind.BrowserUnavailable)]
    [InlineData("session-revision", ManagedBrowserSourceListingResultKind.AccountIdentityMismatch)]
    [InlineData("installation-revision", ManagedBrowserSourceListingResultKind.AccountIdentityMismatch)]
    [InlineData("stored-uid", ManagedBrowserSourceListingResultKind.AccountIdentityMismatch)]
    [InlineData("stored-ownership", ManagedBrowserSourceListingResultKind.AccountIdentityMismatch)]
    public async Task ReadOrLateProofFailuresDiscardCandidateWithoutReplay(string failure, ManagedBrowserSourceListingResultKind expected)
    {
        using var f = await Fixture.CreateAsync();
        if (failure == "read-denial") f.ReadKind = ManagedBrowserSourceListingResultKind.ExtractionRequired;
        if (failure == "uid") f.ReadUid = "wrong-private-user";
        f.AfterRead = async () =>
        {
            if (failure == "late-page") f.Targets.Page = new("https://pan.baidu.com/s/wrong");
            if (failure == "late-owner") f.Listeners.Owner = 999;
            if (failure == "session-revision") await f.Execute("UPDATE ManagedBrowserSessions SET Revision=Revision+1;");
            if (failure == "installation-revision") await f.Execute("UPDATE BrowserInstallations SET Revision=Revision+1;");
            if (failure is "stored-uid" or "stored-ownership") await f.ReplaceIdentity(failure == "stored-uid");
        };
        var result = await f.Run();
        Assert.Equal(expected, result.Kind);
        Assert.Null(result.Observation);
        Assert.Equal(2, f.Requests.Count);
        Assert.Equal(0, f.Processes.Process.Terminations);
    }

    [Theory]
    [InlineData("before", 0)] [InlineData("open", 1)] [InlineData("read", 2)] [InlineData("final-proof", 2)]
    public async Task UserCancellationAtAnyStageNeverPublishesCandidate(string stage, int calls)
    {
        using var f = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        if (stage == "before") cancellation.Cancel();
        if (stage == "open") f.AfterOpen = () => { cancellation.Cancel(); return Task.CompletedTask; };
        if (stage == "read") f.AfterRead = () => { cancellation.Cancel(); return Task.CompletedTask; };
        if (stage == "final-proof") f.Targets.OnRead = count => { if (count == 3) cancellation.Cancel(); };
        var result = await f.Run(cancellation.Token);
        Assert.Equal(ManagedBrowserSourceListingResultKind.Cancelled, result.Kind);
        Assert.Null(result.Observation);
        Assert.Equal(calls, f.Requests.Count);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DeadlineBoundsUncooperativeHelperAndNeverReplays(bool read)
    {
        using var f = await Fixture.CreateAsync();
        f.Timeout = 250;
        var late = new TaskCompletionSource<ManagedBrowserResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.OverrideSend = (request, _) => read && request.Action == ManagedBrowserAction.OpenTransferSource
            ? f.NormalResponse(request) : late.Task;
        var result = await f.Run().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ManagedBrowserSourceListingResultKind.TimedOut, result.Kind);
        Assert.Null(result.Observation);
        Assert.Equal(read ? 2 : 1, f.Requests.Count);
        var nextLease = f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default);
        Assert.False(nextLease.IsCompleted); // Late navigation must not overlap a subsequent account operation.
        late.SetResult(ManagedBrowserResponse.For(f.Requests[^1], read
            ? new ManagedBrowserSourceListingResult(ManagedBrowserSourceListingResultKind.Observed, f.Snapshot())
            : new ManagedBrowserTransferSourceResult(ManagedBrowserTransferSourceResultKind.SourceOpened)));
        using var released = await nextLease.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, f.Processes.Process.Terminations);
    }

    [Fact]
    public async Task AccountLeaseIsHeldThroughReadAndReleasedAfterCompletion()
    {
        using var f = await Fixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.AfterRead = async () => { entered.SetResult(); await release.Task; };
        var run = f.Run();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var nextLease = f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default);
        Assert.False(nextLease.IsCompleted);
        release.SetResult();
        Assert.Equal(ManagedBrowserSourceListingResultKind.Observed, (await run).Kind);
        using var acquired = await nextLease.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task PublicWorkflowSanitizesThrowingAccountLeaseDisposal()
    {
        using var f = await Fixture.CreateAsync();
        var lease = new ThrowingLease();
        var acquireCalls = 0;

        var result = await f.RunWithAccountLease((_, _) =>
        {
            acquireCalls++;
            return Task.FromResult<IDisposable>(lease);
        });

        Assert.Equal(ManagedBrowserSourceListingResultKind.Observed, result.Kind);
        Assert.Equal(1, acquireCalls);
        Assert.Equal(1, lease.DisposeCalls);
        Assert.DoesNotContain("private-account-lease-disposal", result.ToString());
    }

    [Fact]
    public async Task CallerOwnedLeaseWorkflowReturnsPublicObservationWithoutAcquiringOrReleasingAnotherLease()
    {
        using var leasedFixture = await Fixture.CreateAsync();
        var ownerLease = await leasedFixture.Lifecycle.AcquireAccountLeaseAsync(leasedFixture.Identity.AccountId, default);
        try
        {
            var leased = await leasedFixture.RunUnderAccountLease().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(ManagedBrowserSourceListingResultKind.Observed, leased.ReadResult.Kind);
            Assert.True(leased.PendingSendsSettled.IsCompletedSuccessfully);

            var nextLease = leasedFixture.Lifecycle.AcquireAccountLeaseAsync(leasedFixture.Identity.AccountId, default);
            Assert.False(nextLease.IsCompleted); // The leased workflow neither reacquires nor releases the caller's fence.
            ownerLease.Dispose();
            using var acquired = await nextLease.WaitAsync(TimeSpan.FromSeconds(2));
            ownerLease = null!;

            using var publicFixture = await Fixture.CreateAsync();
            var publicResult = await publicFixture.Run();
            Assert.Equal(publicResult.Kind, leased.ReadResult.Kind);
            AssertEquivalent(publicResult.Observation, leased.ReadResult.Observation);
        }
        finally
        {
            ownerLease?.Dispose();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CallerOwnedLeaseWorkflowReportsPendingSendSettlementWithoutLeakingFault(bool read, bool fault)
    {
        using var f = await Fixture.CreateAsync();
        using var cancelled = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<ManagedBrowserResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.OverrideSend = (request, _) =>
        {
            if (read && request.Action == ManagedBrowserAction.OpenTransferSource) return f.NormalResponse(request);
            entered.SetResult();
            return pending.Task;
        };
        var ownerLease = await f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default);
        try
        {
            var run = f.RunUnderAccountLease(cancelled.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancelled.Cancel();
            var leased = await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(ManagedBrowserSourceListingResultKind.Cancelled, leased.ReadResult.Kind);
            Assert.False(leased.PendingSendsSettled.IsCompleted);

            var nextLease = f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default);
            Assert.False(nextLease.IsCompleted);
            if (fault) pending.SetException(new IOException("private-late-source-fault"));
            else pending.SetResult(ManagedBrowserResponse.For(f.Requests[^1],
                read ? new ManagedBrowserSourceListingResult(ManagedBrowserSourceListingResultKind.Observed, f.Snapshot())
                    : new ManagedBrowserTransferSourceResult(ManagedBrowserTransferSourceResultKind.SourceOpened)));
            await leased.PendingSendsSettled.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(leased.PendingSendsSettled.IsCompletedSuccessfully);
            Assert.False(nextLease.IsCompleted); // Settlement is reported, but only the owner releases its lease.
            Assert.Equal(read ? 2 : 1, f.Requests.Count);
            ownerLease.Dispose();
            using var acquired = await nextLease.WaitAsync(TimeSpan.FromSeconds(2));
            ownerLease = null!;
        }
        finally
        {
            ownerLease?.Dispose();
        }
    }

    [Fact]
    public async Task DeadlineIncludesWaitingForAccountLeaseAndStartsNoHelper()
    {
        using var f = await Fixture.CreateAsync();
        using var lease = await f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default);
        f.Timeout = 40;
        Assert.Equal(ManagedBrowserSourceListingResultKind.TimedOut, (await f.Run()).Kind);
        Assert.Empty(f.Requests);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SynchronousLateHelperSuccessCannotAdvanceOrPublish(bool read)
    {
        using var f = await Fixture.CreateAsync();
        f.Timeout = 250;
        f.OverrideSend = (request, _) =>
        {
            if (read == (request.Action == ManagedBrowserAction.ReadTransferSource)) Thread.Sleep(500);
            return f.NormalResponse(request);
        };
        var result = await f.Run();
        Assert.Equal(ManagedBrowserSourceListingResultKind.TimedOut, result.Kind);
        Assert.Null(result.Observation);
        Assert.Equal(read ? 2 : 1, f.Requests.Count);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task CancelledPendingHelperRetainsLeaseUntilSuccessOrFaultSettles(bool read, bool fault)
    {
        using var f = await Fixture.CreateAsync();
        using var cancelled = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<ManagedBrowserResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.OverrideSend = (request, _) =>
        {
            if (read && request.Action == ManagedBrowserAction.OpenTransferSource) return f.NormalResponse(request);
            entered.SetResult();
            return pending.Task;
        };
        var run = f.Run(cancelled.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ManagedBrowserSourceListingResultKind.Cancelled, result.Kind);
        Assert.Null(result.Observation);
        var nextLease = f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default);
        Assert.False(nextLease.IsCompleted);
        if (fault) pending.SetException(new IOException("private-late-fault"));
        else pending.SetResult(await f.NormalResponse(f.Requests[^1]));
        using var released = await nextLease.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(read ? 2 : 1, f.Requests.Count);
        Assert.Equal(0, f.Processes.Process.Terminations);
    }

    [Theory]
    [InlineData("io", ManagedBrowserSourceListingResultKind.BrowserUnavailable)]
    [InlineData("timeout", ManagedBrowserSourceListingResultKind.TimedOut)]
    [InlineData("cancel", ManagedBrowserSourceListingResultKind.Cancelled)]
    [InlineData("protocol", ManagedBrowserSourceListingResultKind.PageNotValidated)]
    [InlineData("unavailable", ManagedBrowserSourceListingResultKind.BrowserUnavailable)]
    public async Task HelperFailuresRemainBoundedAndNeverReplay(string failure, ManagedBrowserSourceListingResultKind expected)
    {
        using var f = await Fixture.CreateAsync();
        f.OverrideSend = (_, _) => Task.FromException<ManagedBrowserResponse>(failure switch
        {
            "io" => new IOException("private-source-user-path"),
            "timeout" => new TimeoutException("private-source-user-path"),
            "cancel" => new OperationCanceledException("private-source-user-path"),
            "protocol" => new ManagedBrowserHelperClientException(ManagedBrowserHelperClientError.ProtocolViolation),
            _ => new ManagedBrowserHelperClientException(ManagedBrowserHelperClientError.ChildExited),
        });
        var result = await f.Run();
        Assert.Equal(expected, result.Kind);
        Assert.Null(result.Observation);
        Assert.DoesNotContain("private", result.ToString());
        Assert.Single(f.Requests);
        using var released = await f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AmbiguousPersistedIdentityFailsClosedBeforeHelperOrAfterRead(bool late)
    {
        using var f = await Fixture.CreateAsync();
        if (late) f.AfterRead = f.AddAmbiguousSession;
        else await f.AddAmbiguousSession();
        var result = await f.Run();
        Assert.Equal(ManagedBrowserSourceListingResultKind.AccountIdentityMismatch, result.Kind);
        Assert.Null(result.Observation);
        Assert.Equal(late ? 2 : 0, f.Requests.Count);
    }

    private sealed class Fixture : IDisposable
    {
        public const string SourceUri = "https://pan.baidu.com/s/1synthetic?pwd=aB12";
        public required string Root;
        public required SqliteConnectionFactory Factory;
        public required ManagedBrowserSessionReference Identity;
        public required BrowserInstallationIdentity Installation;
        public required FakeExecutable Executable;
        public required FakeProfile ProfileLease;
        public FakeProcesses Processes = new();
        public FakeListeners Listeners = new();
        public SourceTargets Targets = new();
        public ManagedChromiumProcessManager Manager = null!;
        public ManagedBrowserLifecycleCoordinator Lifecycle = null!;
        public ManagedChromiumSessionHandle Handle = null!;
        public BaiduExecutionContext Context = null!;
        public Guid OperationId = Guid.NewGuid();
        public long Generation = 7;
        public int Timeout = 2000;
        public string Source = SourceUri, ReadUid = "synthetic-user";
        public ManagedBrowserTransferSourceResultKind OpenKind = ManagedBrowserTransferSourceResultKind.SourceOpened;
        public ManagedBrowserSourceListingResultKind ReadKind = ManagedBrowserSourceListingResultKind.Observed;
        public ManagedBrowserSourceItemKind ItemKind = ManagedBrowserSourceItemKind.Folder;
        public List<ManagedBrowserRequest> Requests = [];
        public Func<Task>? AfterOpen, AfterRead;
        public Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>>? OverrideSend;

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "source-session-" + Guid.NewGuid().ToString("N"));
            var factory = new SqliteConnectionFactory(Path.Combine(root, "synthetic.db"));
            await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
            var installation = new BrowserInstallationIdentity(Guid.NewGuid(), BrowserFamily.Chrome,
                @"C:\SyntheticBrowser", @"C:\SyntheticBrowser\chrome.exe", "Google LLC", "Google Chrome", "synthetic-file");
            var identity = new ManagedBrowserSessionReference(Guid.NewGuid(), installation.InstallationId, Guid.NewGuid(), "synthetic-user",
                BrowserFamily.Chrome, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            var store = new SqliteManagedBrowserStateStore(factory);
            Assert.Equal(ManagedBrowserBindingStatus.Bound, await store.BindLoginAsync(true, installation, identity, null, default));
            var profile = ManagedBrowserProfileDescriptor.FromStoredSession(identity, root);
            var f = new Fixture { Root = root, Factory = factory, Identity = identity, Installation = installation,
                Executable = new(installation), ProfileLease = new(profile), Context = BaiduExecutionContext.Managed(identity) };
            f.Manager = new((_, _) => Task.FromResult<IBrowserExecutableLaunchLease>(f.Executable),
                (_, _) => Task.FromResult<IManagedBrowserProfileLease>(f.ProfileLease), f.Processes, f.Listeners, f.Targets, TimeSpan.FromMilliseconds(200));
            f.Lifecycle = new(store, f.Manager, root);
            f.Handle = (await f.Manager.LaunchAsync(installation, profile)).Session!;
            f.Targets.ExactTargets.Clear();
            return f;
        }
        public Task<ManagedTransferSourceReadResult> Run(CancellationToken token = default) =>
            new ManagedTransferSourceSession(Lifecycle, Manager, Send).OpenAndReadAsync(OperationId, Generation,
                Context, Handle, Source, Timeout, token);
        public Task<ManagedTransferSourceLeasedReadResult> RunUnderAccountLease(CancellationToken token = default) =>
            new ManagedTransferSourceSession(Lifecycle, Manager, Send).OpenAndReadUnderAccountLeaseAsync(OperationId, Generation,
                Context, Handle, Source, Timeout, token);
        public Task<ManagedTransferSourceReadResult> RunWithAccountLease(
            Func<Guid, CancellationToken, Task<IDisposable>> acquireAccountLease, CancellationToken token = default) =>
            new ManagedTransferSourceSession(Lifecycle, Manager, Send, acquireAccountLease).OpenAndReadAsync(OperationId, Generation,
                Context, Handle, Source, Timeout, token);
        private Task<ManagedBrowserResponse> Send(ManagedBrowserRequest request, CancellationToken token)
        {
            Requests.Add(request);
            return OverrideSend is null ? NormalResponse(request) : OverrideSend(request, token);
        }
        public async Task<ManagedBrowserResponse> NormalResponse(ManagedBrowserRequest request)
        {
            if (request.Action == ManagedBrowserAction.OpenTransferSource)
            {
                Targets.Page = new(SourceUri);
                if (AfterOpen is not null) await AfterOpen();
                return ManagedBrowserResponse.For(request, new ManagedBrowserTransferSourceResult(OpenKind));
            }
            if (AfterRead is not null) await AfterRead();
            return ManagedBrowserResponse.For(request, new ManagedBrowserSourceListingResult(ReadKind,
                ReadKind == ManagedBrowserSourceListingResultKind.Observed ? Snapshot() : null));
        }
        public ManagedBrowserSourceSnapshot Snapshot()
        {
            var items = new[] { new ManagedBrowserSourceItem("source-item", ItemKind, "synthetic name") };
            var snapshot = new ManagedBrowserSourceSnapshot(ReadUid, "synthetic-generation", 1, true, true, items, new string('0', 64));
            return new(ReadUid, snapshot.GenerationToken, 1, true, true, items, snapshot.ComputeFingerprint(SourceUri));
        }
        public ManagedBrowserSessionReference ChangeIdentity(string changed) => new(changed == "session" ? Guid.NewGuid() : Identity.SessionId,
            changed == "installation" ? Guid.NewGuid() : Identity.InstallationId,
            changed == "account" ? Guid.NewGuid() : Identity.AccountId,
            changed == "uid" ? "wrong-user" : Identity.StableBaiduUserId,
            changed == "family" ? BrowserFamily.Edge : Identity.BrowserFamily,
            changed == "profile" ? Guid.NewGuid().ToString("N") : Identity.ProfileId,
            changed == "ownership" ? Guid.NewGuid().ToString("N") : Identity.ProfileOwnershipId);
        public async Task Execute(string sql)
        {
            await using var connection = await Factory.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
        public async Task ReplaceIdentity(bool changeUid)
        {
            // Exercise durable replacement; the real store correctly forbids mutating session identity in place.
            var replacement = new ManagedBrowserSessionReference(Guid.NewGuid(), Installation.InstallationId, Identity.AccountId,
                changeUid ? "changed-private-user" : Identity.StableBaiduUserId, Identity.BrowserFamily,
                Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            await Execute($"UPDATE ManagedBrowserSessions SET State='Retired',Revision=Revision+1 WHERE SessionId='{Identity.SessionId:D}';" +
                $"UPDATE Accounts SET BaiduUserId='{replacement.StableBaiduUserId}';");
            await new SqliteManagedBrowserStateStore(Factory).CreateSessionAsync(replacement, default);
            await Execute($"UPDATE ManagedBrowserSessions SET State='Active',Revision=Revision+1 WHERE SessionId='{replacement.SessionId:D}';");
        }
        public async Task AddAmbiguousSession()
        {
            var store = new SqliteManagedBrowserStateStore(Factory);
            var edge = new BrowserInstallationIdentity(Guid.NewGuid(), BrowserFamily.Edge, @"C:\SyntheticEdge", @"C:\SyntheticEdge\msedge.exe",
                "Microsoft Corporation", "Microsoft Edge", "synthetic-edge-file");
            await store.CreateInstallationAsync(edge, default);
            var other = new ManagedBrowserSessionReference(Guid.NewGuid(), edge.InstallationId, Identity.AccountId, Identity.StableBaiduUserId,
                BrowserFamily.Edge, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            await store.CreateSessionAsync(other, default);
            await Execute($"UPDATE ManagedBrowserSessions SET State='Active' WHERE SessionId='{other.SessionId:D}';");
        }
        public static DevToolsActivePort Port() => new(49152, FakeTargets.FixedBrowserId, new("http://127.0.0.1:49152/"),
            new($"ws://127.0.0.1:49152/devtools/browser/{FakeTargets.FixedBrowserId:D}"));
        public void Dispose() => Manager.Dispose();
    }

    private static void AssertEquivalent(BaiduTransferCandidateObservation? expected, BaiduTransferCandidateObservation? actual)
    {
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected.PageState, actual.PageState);
        Assert.Equal(expected.BaiduUserId, actual.BaiduUserId);
        Assert.Equal(expected.ParentExactId, actual.ParentExactId);
        Assert.Equal(expected.CanonicalPath, actual.CanonicalPath);
        Assert.Equal(expected.AncestorExactIds, actual.AncestorExactIds);
        Assert.Equal(expected.Items.Length, actual.Items.Length);
        for (var index = 0; index < expected.Items.Length; index++)
        {
            Assert.Equal(expected.Items[index].ExactId, actual.Items[index].ExactId);
            Assert.Equal(expected.Items[index].Kind, actual.Items[index].Kind);
            Assert.Equal(expected.Items[index].ServerName, actual.Items[index].ServerName);
        }
        Assert.Equal(expected.TotalCount, actual.TotalCount);
        Assert.Equal(expected.ReachedEnd, actual.ReachedEnd);
        Assert.Equal(expected.IsComplete, actual.IsComplete);
        Assert.Equal(expected.IsStable, actual.IsStable);
        Assert.Equal(expected.GenerationToken, actual.GenerationToken);
        Assert.Equal(expected.ObservationToken, actual.ObservationToken);
        Assert.Equal(expected.OfficialRequestId, actual.OfficialRequestId);
        Assert.Equal(expected.OfficialIdentityMapping, actual.OfficialIdentityMapping);
    }

    private sealed class ThrowingLease : IDisposable
    {
        public int DisposeCalls { get; private set; }
        public void Dispose()
        {
            DisposeCalls++;
            throw new IOException("private-account-lease-disposal");
        }
    }

    private sealed class SourceTargets : IManagedBrowserTargetProbe
    {
        public Uri Page = new("https://pan.baidu.com/");
        public List<string?> ExactTargets = [];
        public Action<int>? OnRead;
        public Task<ManagedBrowserTargetSnapshot?> ReadAsync(DevToolsActivePort port, string? exactTargetId, CancellationToken token,
            Action<string>? targetDiscovered = null)
        {
            ExactTargets.Add(exactTargetId);
            OnRead?.Invoke(ExactTargets.Count);
            return Task.FromResult<ManagedBrowserTargetSnapshot?>(new(port.BrowserId, "fixed-tab", Page));
        }
    }
}
