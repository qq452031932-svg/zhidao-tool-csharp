using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Database;
using BaiduBatchTransfer.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class VisibleWebAutomationQueueTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "VisibleWebAutomationQueueTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Verification_PausesGlobalQueueUntilUserResumes()
    {
        await using var fixture = CreateFixture();
        var first = fixture.Queue.EnqueueAsync(fixture.Request(async (_, context, token) =>
        {
            await context.RequestUserHandoffAsync(
                WebAutomationAttemptState.Preparing,
                WebAutomationHandoffReason.Verification,
                fixture.Fingerprint,
                token);
            return "first";
        }));
        var second = fixture.Queue.EnqueueAsync(fixture.Request((_, _, _) => Task.FromResult("second")));

        var session = await fixture.Factory.WaitForSessionAsync(0);
        await session.HandoffShown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Factory.CreatedCount);

        session.Decide(WebAutomationUserChoice.Resume);
        var results = await Task.WhenAll(first, second);

        Assert.Equal(["first", "second"], results.Select(x => x.Value));
        Assert.All(results, x => Assert.Equal(VisibleWebAutomationDisposition.ActionCompleted, x.Disposition));
        Assert.Equal(1, fixture.Factory.MaximumConcurrentSessions);
    }

    [Fact]
    public async Task PersistedHandoffIsRestoredBeforeTheSameGenerationCallbackRuns()
    {
        await using var fixture = CreateFixture();
        var callbackStarted = false;
        var request = fixture.Request((_, _, _) =>
        {
            callbackStarted = true;
            return Task.FromResult(1);
        });
        var now = DateTimeOffset.UtcNow;
        fixture.Store.Seed(new(
            request.Identity.OperationId,
            request.Identity.AccountId,
            request.Identity.Generation,
            request.Identity.OwnerKind,
            WebAutomationAttemptState.UserHandoff,
            WebAutomationAttemptState.Preparing,
            null,
            fixture.Fingerprint,
            true,
            4,
            now.AddMinutes(-1),
            now));

        var completion = fixture.Queue.EnqueueAsync(request);
        var session = await fixture.Factory.WaitForSessionAsync(0);
        await session.HandoffShown.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(callbackStarted);

        session.Decide(WebAutomationUserChoice.Resume);
        var result = await completion;

        Assert.True(callbackStarted);
        Assert.Equal(VisibleWebAutomationDisposition.ActionCompleted, result.Disposition);
    }

    [Fact]
    public async Task ExistingTerminalGenerationCannotRunTheCallbackAgain()
    {
        await using var fixture = CreateFixture();
        var callbackStarted = false;
        var request = fixture.Request((_, _, _) =>
        {
            callbackStarted = true;
            return Task.FromResult(1);
        });
        var now = DateTimeOffset.UtcNow;
        fixture.Store.Seed(new(
            request.Identity.OperationId,
            request.Identity.AccountId,
            request.Identity.Generation,
            request.Identity.OwnerKind,
            WebAutomationAttemptState.DefinitelyNotSubmitted,
            null,
            null,
            fixture.Fingerprint,
            false,
            5,
            now.AddMinutes(-1),
            now));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Queue.EnqueueAsync(request));

        Assert.False(callbackStarted);
        Assert.Equal(0, fixture.Factory.CreatedCount);
    }

    [Fact]
    public async Task WrongBatchBindingIsRejectedBeforeWindowOrCallback()
    {
        await using var fixture = CreateFixture();
        var callbackStarted = false;
        var request = fixture.Request((_, _, _) =>
        {
            callbackStarted = true;
            return Task.FromResult(1);
        });
        fixture.Ownership.RejectedIdentity = request.Identity;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Queue.EnqueueAsync(request));

        Assert.False(callbackStarted);
        Assert.Equal(0, fixture.Factory.CreatedCount);
    }

    [Fact]
    public async Task Queue_IsFifoAcrossAccountsAndUsesDispatcherForWindowOperations()
    {
        await using var fixture = CreateFixture();
        var order = new List<Guid>();
        var firstAccount = Guid.NewGuid();
        var secondAccount = Guid.NewGuid();
        fixture.CreateProfile(firstAccount);
        fixture.CreateProfile(secondAccount);

        var first = fixture.Queue.EnqueueAsync(fixture.Request(
            (_, _, _) => { order.Add(firstAccount); return Task.FromResult(1); }, firstAccount));
        var second = fixture.Queue.EnqueueAsync(fixture.Request(
            (_, _, _) => { order.Add(secondAccount); return Task.FromResult(2); }, secondAccount));

        await Task.WhenAll(first, second);

        Assert.Equal([firstAccount, secondAccount], order);
        Assert.Equal(1, fixture.Factory.MaximumConcurrentSessions);
        Assert.True(fixture.Factory.AllWindowCallsHadDispatcherAccess);
    }

    [Fact]
    public async Task CancelBeforeStart_DoesNotCreateAWindow()
    {
        await using var fixture = CreateFixture();
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = fixture.Queue.EnqueueAsync(fixture.Request(async (_, _, token) =>
        {
            await blocker.Task.WaitAsync(token);
            return 1;
        }));
        await fixture.Factory.WaitForSessionAsync(0);
        using var cancellation = new CancellationTokenSource();
        var second = fixture.Queue.EnqueueAsync(
            fixture.Request((_, _, _) => Task.FromResult(2)), cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(1, fixture.Factory.CreatedCount);

        blocker.TrySetResult();
        await first;
    }

    [Theory]
    [InlineData(WebAutomationUserChoice.Skip, VisibleWebAutomationDisposition.Skipped)]
    [InlineData(WebAutomationUserChoice.CancelBatch, VisibleWebAutomationDisposition.BatchCancelled)]
    [InlineData(WebAutomationUserChoice.WindowClosed, VisibleWebAutomationDisposition.Skipped)]
    public async Task HandoffTerminalChoices_EndCurrentItemBeforeNextStarts(
        WebAutomationUserChoice choice,
        VisibleWebAutomationDisposition expected)
    {
        await using var fixture = CreateFixture();
        var firstRequest = fixture.Request(async (_, context, token) =>
        {
            await context.RequestUserHandoffAsync(
                WebAutomationAttemptState.Preparing,
                WebAutomationHandoffReason.Captcha,
                fixture.Fingerprint,
                token);
            return 1;
        });
        var first = fixture.Queue.EnqueueAsync(firstRequest);
        var second = fixture.Queue.EnqueueAsync(fixture.Request((_, _, _) => Task.FromResult(2)));
        var session = await fixture.Factory.WaitForSessionAsync(0);
        await session.HandoffShown.Task.WaitAsync(TimeSpan.FromSeconds(5));

        session.Decide(choice);
        var result = await first;
        var next = await second;

        Assert.Equal(expected, result.Disposition);
        Assert.Equal(VisibleWebAutomationDisposition.ActionCompleted, next.Disposition);
        Assert.Equal(1, fixture.Factory.MaximumConcurrentSessions);
        if (choice == WebAutomationUserChoice.CancelBatch)
            Assert.Equal([firstRequest.Identity], fixture.Cancellation.Cancelled);
    }

    [Fact]
    public async Task CancelBatchStopsActiveAndAllQueuedItemsButOtherBatchContinuesFifo()
    {
        await using var fixture = CreateFixture();
        var sameBatchCallbacks = 0;
        var otherBatchCallback = false;
        var activeRequest = fixture.Request(async (_, context, token) =>
        {
            await context.RequestUserHandoffAsync(
                WebAutomationAttemptState.Preparing,
                WebAutomationHandoffReason.Captcha,
                fixture.Fingerprint,
                token);
            return 1;
        });
        var sameBatchOne = fixture.Request((_, _, _) =>
        {
            Interlocked.Increment(ref sameBatchCallbacks);
            return Task.FromResult(2);
        }) with
        {
            Identity = fixture.RequestIdentity(activeRequest.Identity.BatchId),
        };
        var sameBatchTwo = fixture.Request((_, _, _) =>
        {
            Interlocked.Increment(ref sameBatchCallbacks);
            return Task.FromResult(3);
        }) with
        {
            Identity = fixture.RequestIdentity(activeRequest.Identity.BatchId),
        };
        var otherBatch = fixture.Request((_, _, _) =>
        {
            otherBatchCallback = true;
            return Task.FromResult(4);
        });

        var active = fixture.Queue.EnqueueAsync(activeRequest);
        var queuedOne = fixture.Queue.EnqueueAsync(sameBatchOne);
        var queuedTwo = fixture.Queue.EnqueueAsync(sameBatchTwo);
        var other = fixture.Queue.EnqueueAsync(otherBatch);
        var session = await fixture.Factory.WaitForSessionAsync(0);
        await session.HandoffShown.Task.WaitAsync(TimeSpan.FromSeconds(5));

        session.Decide(WebAutomationUserChoice.CancelBatch);

        Assert.Equal(VisibleWebAutomationDisposition.BatchCancelled, (await active).Disposition);
        Assert.Equal(VisibleWebAutomationDisposition.BatchCancelled, (await queuedOne).Disposition);
        Assert.Equal(VisibleWebAutomationDisposition.BatchCancelled, (await queuedTwo).Disposition);
        Assert.Equal(VisibleWebAutomationDisposition.ActionCompleted, (await other).Disposition);
        Assert.Equal(0, sameBatchCallbacks);
        Assert.True(otherBatchCallback);
        Assert.Equal(2, fixture.Factory.CreatedCount);
    }

    [Fact]
    public async Task PersistedBatchCancellationBeforeEnqueueNeverOpensWindowOrRunsCallback()
    {
        await using var fixture = CreateFixture();
        var callbackStarted = false;
        var request = fixture.Request((_, _, _) =>
        {
            callbackStarted = true;
            return Task.FromResult(1);
        });
        fixture.Cancellation.SeedCancelled(request.Identity.BatchId, request.Identity.OwnerKind);

        var result = await fixture.Queue.EnqueueAsync(request);

        Assert.Equal(VisibleWebAutomationDisposition.BatchCancelled, result.Disposition);
        Assert.False(callbackStarted);
        Assert.Equal(0, fixture.Factory.CreatedCount);
    }

    [Fact]
    public async Task LateHandoffFromReleasedGeneration_CannotPauseNextItem()
    {
        await using var fixture = CreateFixture();
        VisibleWebAutomationContext? oldContext = null;
        var first = await fixture.Queue.EnqueueAsync(fixture.Request((_, context, _) =>
        {
            oldContext = context;
            return Task.FromResult(1);
        }));

        var nextGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondTask = fixture.Queue.EnqueueAsync(fixture.Request(async (_, _, token) =>
        {
            await nextGate.Task.WaitAsync(token);
            return 2;
        }));
        await fixture.Factory.WaitForSessionAsync(1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => oldContext!.RequestUserHandoffAsync(
            WebAutomationAttemptState.Preparing,
            WebAutomationHandoffReason.UnknownPage,
            fixture.Fingerprint));
        Assert.False(secondTask.IsCompleted);
        nextGate.TrySetResult();
        var second = await secondTask;

        Assert.Equal(VisibleWebAutomationDisposition.ActionCompleted, first.Disposition);
        Assert.Equal(VisibleWebAutomationDisposition.ActionCompleted, second.Disposition);
    }

    [Fact]
    public async Task ProfileMustExistBeCanonicalAndBelongToRequestAccount()
    {
        await using var fixture = CreateFixture();
        var account = Guid.NewGuid();
        var other = Guid.NewGuid();
        var accountPath = fixture.CreateProfile(account);
        var otherPath = fixture.CreateProfile(other);

        await Assert.ThrowsAsync<IOException>(() => fixture.Queue.EnqueueAsync(
            fixture.Request((_, _, _) => Task.FromResult(1), account) with { ProfilePath = otherPath }));
        await Assert.ThrowsAsync<IOException>(() => fixture.Queue.EnqueueAsync(
            fixture.Request((_, _, _) => Task.FromResult(1), account) with { ProfilePath = Path.Combine(accountPath, "..", account.ToString("D")) }));

        Directory.Delete(accountPath);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => fixture.Queue.EnqueueAsync(
            fixture.Request((_, _, _) => Task.FromResult(1), account)));
        Assert.Equal(0, fixture.Factory.CreatedCount);
    }

    [Fact]
    public async Task LoginProfileLeasePreventsSimultaneousAutomationUse()
    {
        await using var fixture = CreateFixture();
        var account = Guid.NewGuid();
        fixture.CreateProfile(account);
        using var loginLease = fixture.ProfileCloser.Register(account, _ => Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Queue.EnqueueAsync(
            fixture.Request((_, _, _) => Task.FromResult(1), account)));

        Assert.Equal(0, fixture.Factory.CreatedCount);
    }

    [Fact]
    public async Task ProfileCloseRequestCancelsActiveCallbackBeforeLeaseIsReleased()
    {
        await using var fixture = CreateFixture();
        var active = fixture.Queue.EnqueueAsync(fixture.Request(async (_, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 1;
        }));
        var session = await fixture.Factory.WaitForSessionAsync(0);

        await fixture.ProfileCloser.CloseAsync(fixture.DefaultAccount, CancellationToken.None);
        var result = await active.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(VisibleWebAutomationDisposition.Skipped, result.Disposition);
        Assert.True(session.CloseCalls > 0);
    }

    [Fact]
    public async Task ProductionAuthorizationFailsClosedBeforeCreatingOrNavigatingAWindow()
    {
        await using var fixture = CreateFixture(authorizer: new ProductionWebAutomationNavigationAuthorizer());

        var exception = await Assert.ThrowsAsync<WebAutomationAuthorizationException>(() =>
            fixture.Queue.EnqueueAsync(fixture.Request((_, _, _) => Task.FromResult(1))));

        Assert.Equal(BaiduAutomationDenialReason.CatalogNotValidated, exception.DenialReason);
        Assert.Equal(0, fixture.Factory.CreatedCount);
    }

    [Fact]
    public async Task UnsafeNavigationIsRejectedEvenWhenTestAuthorizerAllowsIt()
    {
        await using var fixture = CreateFixture();
        var request = fixture.Request((_, _, _) => Task.FromResult(1)) with
        {
            Navigation = fixture.Navigation(new Uri("https://example.com/")),
        };

        await Assert.ThrowsAsync<WebAutomationAuthorizationException>(() => fixture.Queue.EnqueueAsync(request));
        Assert.Equal(0, fixture.Factory.CreatedCount);
    }

    [Fact]
    public async Task HandlerExceptionClosesWindowAndDoesNotBlockFollowingItem()
    {
        await using var fixture = CreateFixture();
        var failed = fixture.Queue.EnqueueAsync(fixture.Request<int>((_, _, _) =>
            throw new InvalidOperationException("synthetic failure")));
        var next = fixture.Queue.EnqueueAsync(fixture.Request((_, _, _) => Task.FromResult(2)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => failed);
        Assert.Equal(2, (await next).Value);
        Assert.All(fixture.Factory.Sessions, session => Assert.True(session.CloseCalls > 0));
    }

    [Fact]
    public async Task ActiveCancellationClosesWindowAndObservesNonCooperativeLateFault()
    {
        await using var fixture = CreateFixture(actionStopTimeout: TimeSpan.FromMilliseconds(30));
        var late = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var active = fixture.Queue.EnqueueAsync(fixture.Request((_, _, _) => late.Task), cancellation.Token);
        var session = await fixture.Factory.WaitForSessionAsync(0);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        Assert.True(session.CloseCalls > 0);
        late.SetException(new InvalidOperationException("late synthetic fault"));
        await Task.Delay(50);
    }

    [Fact]
    public async Task NonCooperativeSessionTimesOutAndClosesBeforeNextItem()
    {
        await using var fixture = CreateFixture(sessionTimeout: TimeSpan.FromMilliseconds(40));
        var never = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timedOut = fixture.Queue.EnqueueAsync(fixture.Request((_, _, _) => never.Task));
        var next = fixture.Queue.EnqueueAsync(fixture.Request((_, _, _) => Task.FromResult(2)));
        var session = await fixture.Factory.WaitForSessionAsync(0);

        await Assert.ThrowsAsync<TimeoutException>(() => timedOut);
        Assert.True(session.CloseCalls > 0);
        Assert.Equal(2, (await next).Value);
        Assert.Equal(1, fixture.Factory.MaximumConcurrentSessions);
    }

    [Fact]
    public async Task NonCooperativeSessionInitializationIsBoundedAndClosedBeforeNextItem()
    {
        await using var fixture = CreateFixture(sessionTimeout: TimeSpan.FromMilliseconds(40));
        fixture.Factory.NextShowNeverCompletes = true;
        var timedOut = fixture.Queue.EnqueueAsync(fixture.Request((_, _, _) => Task.FromResult(1)));
        var next = fixture.Queue.EnqueueAsync(fixture.Request((_, _, _) => Task.FromResult(2)));
        var session = await fixture.Factory.WaitForSessionAsync(0);

        await Assert.ThrowsAsync<TimeoutException>(() => timedOut);
        Assert.True(session.CloseCalls > 0);
        Assert.Equal(2, (await next).Value);
        Assert.Equal(1, fixture.Factory.MaximumConcurrentSessions);
    }

    [Fact]
    public async Task UserClosingWindowOutsideHandoffSafelySkipsAndReleasesSlot()
    {
        await using var fixture = CreateFixture();
        var never = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = fixture.Queue.EnqueueAsync(fixture.Request((_, _, _) => never.Task));
        var next = fixture.Queue.EnqueueAsync(fixture.Request((_, _, _) => Task.FromResult(2)));
        var session = await fixture.Factory.WaitForSessionAsync(0);

        session.CloseByUser();
        var result = await closed;

        Assert.Equal(VisibleWebAutomationDisposition.Skipped, result.Disposition);
        Assert.Equal(WebAutomationAttemptState.DefinitelyNotSubmitted, result.PersistentState);
        Assert.Equal(2, (await next).Value);
    }

    [Fact]
    public async Task NonCooperativeWindowClosePoisonsQueueWithoutOpeningAnotherWindow()
    {
        var fixture = CreateFixture(windowCloseTimeout: TimeSpan.FromMilliseconds(40));
        fixture.Factory.NextCloseNeverCompletes = true;
        var unsafeClose = fixture.Queue.EnqueueAsync(fixture.Request((_, _, _) => Task.FromResult(1)));
        var pending = fixture.Queue.EnqueueAsync(fixture.Request((_, _, _) => Task.FromResult(2)));
        await fixture.Factory.WaitForSessionAsync(0);

        await Assert.ThrowsAsync<InvalidOperationException>(() => unsafeClose);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, fixture.Factory.CreatedCount);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task DisposeCancelsPendingAndClosesActiveWindow()
    {
        var fixture = CreateFixture();
        var never = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = fixture.Queue.EnqueueAsync(fixture.Request((_, _, _) => never.Task));
        var pending = fixture.Queue.EnqueueAsync(fixture.Request((_, _, _) => Task.FromResult(2)));
        var session = await fixture.Factory.WaitForSessionAsync(0);

        await fixture.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(session.CloseCalls > 0);
        Assert.Equal(1, fixture.Factory.CreatedCount);
    }

    [Fact]
    public void ProductionWindowIsVisibleAccessibleAndOffersOnlyTheThreeSafeChoices()
    {
        var directory = Path.Combine(
            FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "Features", "BrowserAutomation");
        var xaml = File.ReadAllText(Path.Combine(directory, "WebAutomationWindow.xaml"));

        Assert.Contains("已处理，继续", xaml, StringComparison.Ordinal);
        Assert.Contains("跳过此项", xaml, StringComparison.Ordinal);
        Assert.Contains("取消批次", xaml, StringComparison.Ordinal);
        Assert.Equal(3, xaml.Split("<Button ", StringSplitOptions.None).Length - 1);
        Assert.Equal(3, Regex.Matches(
            xaml,
            "<Button[^>]+AutomationProperties.Name=",
            RegexOptions.Singleline).Count);
        Assert.Contains("AutomationProperties.LiveSetting=\"Assertive\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<wv2:WebView2", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionSessionHardensWebViewAndContainsNoExecutableAutomationOrRemoteDebugging()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "Features", "BrowserAutomation",
            "WebView2AutomationSession.cs"));

        foreach (var setting in new[]
                 {
                     "AreDevToolsEnabled = false",
                     "AreDefaultContextMenusEnabled = false",
                     "IsStatusBarEnabled = false",
                     "IsPasswordAutosaveEnabled = false",
                     "IsGeneralAutofillEnabled = false",
                 })
            Assert.Contains(setting, source, StringComparison.Ordinal);
        Assert.Contains("NewWindowRequested", source, StringComparison.Ordinal);
        Assert.Contains("DownloadStarting", source, StringComparison.Ordinal);
        Assert.Contains("PermissionRequested", source, StringComparison.Ordinal);
        Assert.Contains("CoreWebView2PermissionState.Deny", source, StringComparison.Ordinal);
        Assert.Contains("BaiduNavigationPolicy.Evaluate", source, StringComparison.Ordinal);
        Assert.Contains("if (!window.IsClosed)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteScriptAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("remote-debugging-port", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoginAcquiresTheSharedProfileLeaseBeforeCreatingWebViewEnvironment()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "Features", "Accounts",
            "BaiduLoginWindow.xaml.cs"));

        var lease = source.IndexOf("profileCloser.Register", StringComparison.Ordinal);
        var environment = source.IndexOf("CoreWebView2Environment", StringComparison.Ordinal);
        Assert.True(lease >= 0);
        Assert.True(environment >= 0);
        Assert.True(lease < environment, "账号登录必须先独占 profile，再创建 WebView2 环境。");
    }

    [Fact]
    public void BootstrapRegistersOneGlobalBrokerWithoutOpeningAWindow()
    {
        var dataRoot = Path.Combine(root, "bootstrap");
        using var host = AppBootstrap.CreateHost(dataRoot);

        var broker = host.Services.GetRequiredService<IVisibleWebAutomationBroker>();
        var queue = host.Services.GetRequiredService<VisibleWebAutomationQueue>();

        Assert.Same(queue, broker);
        Assert.IsType<WebView2AutomationSessionFactory>(
            host.Services.GetRequiredService<IVisibleWebAutomationSessionFactory>());
        Assert.IsType<ProductionWebAutomationNavigationAuthorizer>(
            host.Services.GetRequiredService<IWebAutomationNavigationAuthorizer>());
        Assert.IsType<WebAutomationBatchCancellation>(
            host.Services.GetRequiredService<IWebAutomationBatchCancellation>());
        Assert.IsType<SqliteWebAutomationBatchCancellationStore>(
            host.Services.GetRequiredService<IWebAutomationBatchCancellationStore>());
    }

    private QueueFixture CreateFixture(
        IWebAutomationNavigationAuthorizer? authorizer = null,
        TimeSpan? actionStopTimeout = null,
        TimeSpan? sessionTimeout = null,
        TimeSpan? windowCloseTimeout = null)
    {
        Directory.CreateDirectory(root);
        return new(root, authorizer, actionStopTimeout, sessionTimeout, windowCloseTimeout);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BaiduBatchTransfer.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("未找到测试仓库目录。");
    }

    private sealed class QueueFixture : IAsyncDisposable
    {
        private int sequence;

        public QueueFixture(
            string root,
            IWebAutomationNavigationAuthorizer? authorizer,
            TimeSpan? actionStopTimeout,
            TimeSpan? sessionTimeout,
            TimeSpan? windowCloseTimeout)
        {
            ProfileRoot = Path.Combine(root, "profiles-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ProfileRoot);
            PathPolicy = new FileSystemAccountProfilePathPolicy(ProfileRoot);
            Dispatcher = new RecordingDispatcher();
            Factory = new RecordingSessionFactory(Dispatcher);
            ProfileCloser = new WebView2ProfileCloser();
            Store = new QueueAttemptStore();
            Cancellation = new RecordingCancellation();
            Ownership = new RecordingOwnershipStore();
            var handoff = new UserHandoffCoordinator(Store, Cancellation, TimeProvider.System);
            Queue = new VisibleWebAutomationQueue(
                Factory,
                Dispatcher,
                PathPolicy,
                ProfileCloser,
                handoff,
                Cancellation,
                Ownership,
                authorizer ?? new AllowingAuthorizer(),
                new VisibleWebAutomationQueueOptions
                {
                    ActionStopTimeout = actionStopTimeout ?? TimeSpan.FromSeconds(1),
                    SessionTimeout = sessionTimeout ?? TimeSpan.FromMinutes(1),
                    WindowCloseTimeout = windowCloseTimeout ?? TimeSpan.FromSeconds(1),
                });
            DefaultAccount = Guid.NewGuid();
            CreateProfile(DefaultAccount);
        }

        public string Fingerprint { get; } = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        public string ProfileRoot { get; }
        public Guid DefaultAccount { get; }
        public FileSystemAccountProfilePathPolicy PathPolicy { get; }
        public RecordingDispatcher Dispatcher { get; }
        public RecordingSessionFactory Factory { get; }
        public WebView2ProfileCloser ProfileCloser { get; }
        public QueueAttemptStore Store { get; }
        public RecordingCancellation Cancellation { get; }
        public RecordingOwnershipStore Ownership { get; }
        public VisibleWebAutomationQueue Queue { get; }

        public string CreateProfile(Guid accountId)
        {
            var path = PathPolicy.GetAccountProfilePath(accountId);
            Directory.CreateDirectory(path);
            return path;
        }

        public VisibleWebAutomationRequest<T> Request<T>(
            Func<IVisibleWebAutomationSession, VisibleWebAutomationContext, CancellationToken, Task<T>> action,
            Guid? accountId = null)
        {
            var account = accountId ?? DefaultAccount;
            return new(
                new WebAutomationRequestIdentity(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    account,
                    Interlocked.Increment(ref sequence),
                    WebAutomationOwnerKind.Transfer),
                PathPolicy.GetAccountProfilePath(account),
                Navigation(new Uri("https://pan.baidu.com/disk/main")),
                action);
        }

        public WebAutomationRequestIdentity RequestIdentity(Guid batchId) => new(
            Guid.NewGuid(),
            batchId,
            DefaultAccount,
            Interlocked.Increment(ref sequence),
            WebAutomationOwnerKind.Transfer);

        public BaiduNavigationAuthorizationRequest Navigation(Uri uri) => new(
            BaiduAutomationAction.OpenOfficialUri,
            uri,
            BaiduSelectorCatalog.ProtocolVersion,
            BaiduSelectorCatalog.SelectorVersion,
            "test-build",
            new BaiduAutomationAcceptance(
                true,
                BaiduSelectorCatalog.ProtocolVersion,
                BaiduSelectorCatalog.SelectorVersion,
                "test-build"),
            BaiduProtocolValidationStatus.Validated);

        public ValueTask DisposeAsync() => Queue.DisposeAsync();
    }

    private sealed class AllowingAuthorizer : IWebAutomationNavigationAuthorizer
    {
        public BaiduAutomationDecision Authorize(BaiduNavigationAuthorizationRequest request) =>
            BaiduAutomationDecision.Allowed();
    }

    private sealed class RecordingDispatcher : IWebAutomationUiDispatcher
    {
        private readonly AsyncLocal<int> access = new();
        public bool HasAccess => access.Value > 0;

        public async Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            access.Value++;
            try { return await action(); }
            finally { access.Value--; }
        }

        public async Task InvokeAsync(Func<Task> action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            access.Value++;
            try { await action(); }
            finally { access.Value--; }
        }
    }

    private sealed class RecordingSessionFactory(RecordingDispatcher dispatcher)
        : IVisibleWebAutomationSessionFactory
    {
        private readonly List<RecordingSession> sessions = [];
        private int active;
        public IReadOnlyList<RecordingSession> Sessions => sessions;
        public int CreatedCount => sessions.Count;
        public int MaximumConcurrentSessions { get; private set; }
        public bool AllWindowCallsHadDispatcherAccess => sessions.All(x => x.AllCallsHadDispatcherAccess);
        public bool NextCloseNeverCompletes { get; set; }
        public bool NextShowNeverCompletes { get; set; }

        public Task<IVisibleWebAutomationSession> CreateAsync(
            WebAutomationRequestIdentity identity,
            string profilePath,
            CancellationToken cancellationToken = default)
        {
            Assert.True(dispatcher.HasAccess);
            var session = new RecordingSession(
                dispatcher,
                OnShown,
                OnClosed,
                NextCloseNeverCompletes,
                NextShowNeverCompletes);
            NextCloseNeverCompletes = false;
            NextShowNeverCompletes = false;
            sessions.Add(session);
            return Task.FromResult<IVisibleWebAutomationSession>(session);
        }

        public async Task<RecordingSession> WaitForSessionAsync(int index)
        {
            for (var attempt = 0; attempt < 100 && sessions.Count <= index; attempt++)
                await Task.Delay(10);
            return sessions[index];
        }

        private void OnShown()
        {
            var count = Interlocked.Increment(ref active);
            MaximumConcurrentSessions = Math.Max(MaximumConcurrentSessions, count);
        }

        private void OnClosed() => Interlocked.Decrement(ref active);
    }

    private sealed class RecordingSession(
        RecordingDispatcher dispatcher,
        Action onShown,
        Action onClosed,
        bool closeNeverCompletes,
        bool showNeverCompletes) : IVisibleWebAutomationSession
    {
        private TaskCompletionSource<WebAutomationUserChoice>? handoff;
        private readonly CancellationTokenSource userClosed = new();
        private int closed;
        public TaskCompletionSource HandoffShown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken UserClosed => userClosed.Token;
        public int CloseCalls { get; private set; }
        public bool AllCallsHadDispatcherAccess { get; private set; } = true;

        public Task ShowAsync(Uri initialUri, CancellationToken cancellationToken = default)
        {
            AllCallsHadDispatcherAccess &= dispatcher.HasAccess;
            onShown();
            if (showNeverCompletes)
                return Task.Delay(TimeSpan.FromMilliseconds(200));
            return Task.CompletedTask;
        }

        public Task ShowHandoffAsync(WebAutomationHandoffPrompt prompt, CancellationToken cancellationToken = default)
        {
            AllCallsHadDispatcherAccess &= dispatcher.HasAccess;
            handoff = new(TaskCreationOptions.RunContinuationsAsynchronously);
            HandoffShown.TrySetResult();
            return Task.CompletedTask;
        }

        public Task<WebAutomationUserChoice> WaitForHandoffChoiceAsync(CancellationToken cancellationToken = default)
        {
            AllCallsHadDispatcherAccess &= dispatcher.HasAccess;
            return handoff!.Task.WaitAsync(cancellationToken);
        }

        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            AllCallsHadDispatcherAccess &= dispatcher.HasAccess;
            CloseCalls++;
            if (closeNeverCompletes)
                return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            if (Interlocked.Exchange(ref closed, 1) == 0) onClosed();
            return Task.CompletedTask;
        }

        public void Decide(WebAutomationUserChoice choice) => handoff!.TrySetResult(choice);
        public void CloseByUser() => userClosed.Cancel();

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref closed, 1) == 0) onClosed();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingCancellation : IWebAutomationBatchCancellation
    {
        private readonly Dictionary<(Guid, WebAutomationOwnerKind), CancellationTokenSource> signals = [];
        private readonly HashSet<(Guid, WebAutomationOwnerKind)> cancelledBatches = [];
        public List<WebAutomationRequestIdentity> Cancelled { get; } = [];

        public void SeedCancelled(Guid batchId, WebAutomationOwnerKind ownerKind)
        {
            var key = Key(batchId, ownerKind);
            cancelledBatches.Add(key);
            Signal(key).Cancel();
        }
        public Task<bool> CancelAsync(
            WebAutomationRequestIdentity identity,
            long expectedAttemptRevision,
            WebAutomationAttemptState expectedResumeStage,
            CancellationToken cancellationToken = default)
        {
            Cancelled.Add(identity);
            var key = Key(identity.BatchId, identity.OwnerKind);
            cancelledBatches.Add(key);
            Signal(key).Cancel();
            return Task.FromResult(true);
        }

        public Task<bool> IsCancelledAsync(
            Guid batchId,
            WebAutomationOwnerKind ownerKind,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(cancelledBatches.Contains(Key(batchId, ownerKind)));

        public IDisposable Register(
            Guid batchId,
            WebAutomationOwnerKind ownerKind,
            Action cancellationRequested) =>
            Signal(Key(batchId, ownerKind)).Token.Register(cancellationRequested);

        private CancellationTokenSource Signal((Guid, WebAutomationOwnerKind) key)
        {
            if (!signals.TryGetValue(key, out var signal)) signals[key] = signal = new();
            return signal;
        }

        private static (Guid, WebAutomationOwnerKind) Key(Guid batchId, WebAutomationOwnerKind ownerKind) =>
            (batchId, ownerKind == WebAutomationOwnerKind.StandaloneShare
                ? WebAutomationOwnerKind.StandaloneShare
                : WebAutomationOwnerKind.Transfer);
    }

    private sealed class RecordingOwnershipStore : IWebAutomationRequestOwnershipStore
    {
        public WebAutomationRequestIdentity? RejectedIdentity { get; set; }

        public Task<bool> IsBoundAsync(
            Guid operationId,
            Guid batchId,
            Guid accountId,
            int generation,
            WebAutomationOwnerKind ownerKind,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(RejectedIdentity is null ||
                RejectedIdentity.OperationId != operationId ||
                RejectedIdentity.BatchId != batchId ||
                RejectedIdentity.AccountId != accountId ||
                RejectedIdentity.Generation != generation ||
                RejectedIdentity.OwnerKind != ownerKind);
        }
    }

    private sealed class QueueAttemptStore : IWebAutomationAttemptStore
    {
        private readonly Dictionary<(Guid, Guid, int), WebAutomationAttemptRecord> records = [];
        private readonly object gate = new();

        public void Seed(WebAutomationAttemptRecord record)
        {
            lock (gate) records[Key(record)] = record;
        }

        public Task<bool> BeginAsync(WebAutomationAttemptRecord attempt, CancellationToken cancellationToken = default)
        {
            lock (gate) return Task.FromResult(records.TryAdd(Key(attempt), attempt));
        }

        public Task<WebAutomationAttemptRecord?> GetAsync(Guid operationId, Guid accountId, int generation, CancellationToken cancellationToken = default)
        {
            lock (gate) return Task.FromResult(records.GetValueOrDefault((operationId, accountId, generation)));
        }

        public Task<Page<WebAutomationAttemptRecord>> GetRecoveryPageAsync(PageQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryPersistBaselineAsync(Guid operationId, Guid accountId, int generation, long expectedRevision, WebAutomationBaseline baseline, string observationFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> TryAdvanceAsync(Guid operationId, Guid accountId, int generation, WebAutomationAttemptState expectedState, long expectedRevision, WebAutomationAttemptState nextState, string? observationFingerprint, bool isWaitingForUser, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                var key = (operationId, accountId, generation);
                if (!records.TryGetValue(key, out var current) || current.State != expectedState || current.Revision != expectedRevision)
                    return Task.FromResult(false);
                records[key] = New(current, nextState, nextState == WebAutomationAttemptState.UserHandoff ? expectedState : null, isWaitingForUser, observationFingerprint, updatedAt);
                return Task.FromResult(true);
            }
        }

        public Task<bool> TryResumeFromUserHandoffAsync(Guid operationId, Guid accountId, int generation, long expectedRevision, WebAutomationAttemptState resumeStage, string? observationFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                var key = (operationId, accountId, generation);
                if (!records.TryGetValue(key, out var current) || current.State != WebAutomationAttemptState.UserHandoff || current.ResumeStage != resumeStage || current.Revision != expectedRevision)
                    return Task.FromResult(false);
                records[key] = New(current, resumeStage, null, false, observationFingerprint, updatedAt);
                return Task.FromResult(true);
            }
        }

        public Task<bool> TryResolveUserHandoffAsync(Guid operationId, Guid accountId, int generation, long expectedRevision, WebAutomationAttemptState resumeStage, WebAutomationAttemptState terminalState, string? observationFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                var key = (operationId, accountId, generation);
                if (!records.TryGetValue(key, out var current) || current.State != WebAutomationAttemptState.UserHandoff || current.ResumeStage != resumeStage || current.Revision != expectedRevision)
                    return Task.FromResult(false);
                records[key] = New(current, terminalState, null, false, observationFingerprint, updatedAt);
                return Task.FromResult(true);
            }
        }

        private static (Guid, Guid, int) Key(WebAutomationAttemptRecord value) => (value.OperationId, value.AccountId, value.Generation);
        private static WebAutomationAttemptRecord New(WebAutomationAttemptRecord current, WebAutomationAttemptState state, WebAutomationAttemptState? resume, bool waiting, string? fingerprint, DateTimeOffset updated) =>
            new(current.OperationId, current.AccountId, current.Generation, current.OwnerKind, state, resume, current.Baseline, fingerprint ?? current.ObservationFingerprint, waiting, current.Revision + 1, current.CreatedAt, updated);
    }
}
