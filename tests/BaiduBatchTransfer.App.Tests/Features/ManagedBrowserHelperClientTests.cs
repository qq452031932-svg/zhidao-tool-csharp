using BaiduBatchTransfer.App.Features.ManagedBrowser;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace BaiduBatchTransfer.App.Tests.Features;

[Collection(NonParallelCollection)]
public sealed partial class ManagedBrowserHelperClientTests
{
    public const string NonParallelCollection = "Managed browser helper client timing tests";

    private static readonly ManagedBrowserHelperClientOptions Fast = new(
        TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(100));

    [Fact]
    public async Task FirstFrameIsPrivateHandshakeWithFreshNonceAndActualProcessIdentity()
    {
        var first = new FakeProcess();
        var second = new FakeProcess();
        await using var a = await Start(first);
        await using var b = await Start(second);
        var one = Assert.IsType<ManagedBrowserHandshakeRequest>(first.Requests.Single().Payload);
        var two = Assert.IsType<ManagedBrowserHandshakeRequest>(second.Requests.Single().Payload);
        Assert.Matches("^[0-9a-f]{64}$", one.Nonce);
        Assert.NotEqual(one.Nonce, two.Nonce);
        Assert.Equal(Environment.ProcessId, one.ParentProcessId);
        Assert.Equal(ManagedBrowserProtocol.RequiredCapabilities, one.RequiredCapabilities);
        Assert.Equal(3, ManagedBrowserProtocol.ProtocolVersion);
        Assert.Equal("ManagedChromium-v3", ManagedBrowserProtocol.ExecutionEngineVersion);
        Assert.Equal(24, one.RequiredCapabilities.Count);
        var health = await a.SendAsync(Request(), CancellationToken.None);
        Assert.IsType<ManagedBrowserHealthResult>(health.Result);
        Assert.Equal(ManagedBrowserAction.Handshake, first.Requests.First().Action);
    }

    [Fact]
    public void LaunchConfigurationUsesOnlyVerifiedPathsAndRedirectedPrivateStreams()
    {
        var info = ManagedBrowserHelperClient.CreateStartInfo(@"C:\verified\python.exe", @"C:\verified\main.py");
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.True(info.RedirectStandardInput && info.RedirectStandardOutput && info.RedirectStandardError);
        Assert.Equal(@"C:\verified\python.exe", info.FileName);
        Assert.Equal(new[] { "-B", "-E", "-s", @"C:\verified\main.py" }, info.ArgumentList);
        Assert.DoesNotContain(info.Environment.Keys, key => key.Contains("NONCE", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("parent")]
    [InlineData("helper")]
    [InlineData("protocol")]
    [InlineData("engine")]
    [InlineData("capability")]
    public async Task MismatchedHandshakeIsRejectedAndOwnedChildIsTerminated(string mismatch)
    {
        var process = new FakeProcess();
        var lease = new FakeLease();
        process.OnRequest = request =>
        {
            var response = process.Response(request);
            var json = Encoding.UTF8.GetString(ManagedBrowserProtocol.EncodeResponsePayload(response));
            var payload = (ManagedBrowserHandshakeRequest)request.Payload;
            json = mismatch switch
            {
                "nonce" => json.Replace(payload.Nonce, new string('0', 64)),
                "parent" => json.Replace($"\"parentProcessId\":{Environment.ProcessId}", "\"parentProcessId\":1"),
                "helper" => json.Replace($"\"helperProcessId\":{process.Id}", "\"helperProcessId\":1"),
                "protocol" => json.Replace("\"protocolVersion\":3", "\"protocolVersion\":2"),
                "engine" => json.Replace("ManagedChromium-v3", "ManagedChromium-v2"),
                _ => json.Replace("ObserveAccountIdentity\"", "OtherCapability\""),
            };
            process.Output.Emit(Frame(Encoding.UTF8.GetBytes(json)));
            return Task.CompletedTask;
        };
        var error = await Assert.ThrowsAsync<ManagedBrowserHelperClientException>(() => Start(process, lease));
        Assert.Contains(error.Error, new[] { ManagedBrowserHelperClientError.HandshakeRejected, ManagedBrowserHelperClientError.ProtocolViolation });
        await Until(() => process.Killed && lease.Released);
        Assert.Single(process.Requests);
    }

    [Fact]
    public async Task LaunchFailureIsSanitizedAndReleasesUnlaunchedLease()
    {
        var lease = new FakeLease();
        var error = await Assert.ThrowsAsync<ManagedBrowserHelperClientException>(() =>
            ManagedBrowserHelperClient.StartAsync(lease, () => throw new IOException("cookie=secret"), Fast, CancellationToken.None));
        Assert.Equal(ManagedBrowserHelperClientError.LaunchFailed, error.Error);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.True(lease.Released);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupFailureKeepsOwnershipWhenChildWasAlreadyStarted(bool afterStart)
    {
        var process = new FakeProcess { FailBeforeStart = !afterStart, FailAfterStart = afterStart, KeepOutputOpen = true };
        var lease = new FakeLease();
        ManagedBrowserHelperClient? unexpectedClient = null;
        try
        {
            var error = await Assert.ThrowsAsync<ManagedBrowserHelperClientException>(async () => unexpectedClient = await Start(process, lease));
            Assert.Equal(ManagedBrowserHelperClientError.LaunchFailed, error.Error);
            Assert.DoesNotContain("secret", error.ToString());
            Assert.Empty(process.Requests);
            if (afterStart)
            {
                await Until(() => process.Killed);
                Assert.False(lease.Released);
                process.CompleteOutput();
                await Until(() => lease.Released);
            }
            else
            {
                Assert.False(process.Killed);
                Assert.True(lease.Released);
            }
        }
        finally
        {
            process.Exit();
            process.CompleteOutput();
            if (unexpectedClient is not null) await unexpectedClient.DisposeAsync();
        }
    }

    [Fact]
    public async Task StderrIsBoundedAndOnlyExactSafeEventCodesSurvive()
    {
        var process = new FakeProcess();
        await using var client = await Start(process);
        process.Error.Emit(Encoding.UTF8.GetBytes("HELPER_PROTOCOL_ERROR\nHELPER_RUNTIME_ERROR\r\nHELPER_SESSION_LIMIT_REACHED\nhttps://secret/?cookie=secret\n" + new string('x', 100_000) + "HELPER_RUNTIME_ERROR\n"));
        await Until(() => client.Diagnostics.Count >= 5);
        Assert.Equal(new[] { ManagedBrowserHelperDiagnostic.HelperProtocolError, ManagedBrowserHelperDiagnostic.HelperRuntimeError,
            ManagedBrowserHelperDiagnostic.HelperSessionLimitReached, ManagedBrowserHelperDiagnostic.UnrecognizedStderr,
            ManagedBrowserHelperDiagnostic.UnrecognizedStderr }, client.Diagnostics);
        process.Error.Emit(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("cookie=secret\n", 200))));
        await Until(() => client.Diagnostics.Count == 64);
        Assert.DoesNotContain("secret", string.Join(",", client.Diagnostics));
        Assert.IsType<ManagedBrowserHealthResult>((await client.SendAsync(Request(), CancellationToken.None)).Result);
    }

    [Fact]
    public async Task UnexpectedStdoutBytesPoisonSessionWithoutLeakingBytes()
    {
        var process = new FakeProcess();
        await using var client = await Start(process);
        process.OnRequest = _ => { process.Output.Emit(Encoding.UTF8.GetBytes("cookie=secret")); return Task.CompletedTask; };
        var error = await Assert.ThrowsAsync<ManagedBrowserHelperClientException>(() => client.SendAsync(Request(), CancellationToken.None));
        Assert.Equal(ManagedBrowserHelperClientError.ProtocolViolation, error.Error);
        Assert.DoesNotContain("secret", error.ToString());
        await Until(() => process.Killed);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("account")]
    [InlineData("generation")]
    [InlineData("action")]
    public async Task ResponseMustMatchWholeLiveRequestIdentity(string mismatch)
    {
        var process = new FakeProcess();
        await using var client = await Start(process);
        process.OnRequest = request =>
        {
            process.Emit(new ManagedBrowserResponse(request.CorrelationId,
                mismatch == "operation" ? Guid.NewGuid() : request.OperationId,
                mismatch == "account" ? Guid.NewGuid() : request.AccountId,
                mismatch == "generation" ? request.Generation + 1 : request.Generation,
                mismatch == "action" ? ManagedBrowserAction.Shutdown : request.Action,
                mismatch == "action" ? ManagedBrowserShutdownResult.ShuttingDown : ManagedBrowserHealthResult.Healthy));
            return Task.CompletedTask;
        };
        var error = await Assert.ThrowsAsync<ManagedBrowserHelperClientException>(() => client.SendAsync(Request(), CancellationToken.None));
        Assert.Equal(ManagedBrowserHelperClientError.ProtocolViolation, error.Error);
    }

    [Fact]
    public async Task UnknownAndDuplicateResponsesCannotCompleteAnotherRequest()
    {
        var process = new FakeProcess();
        await using var client = await Start(process);
        var previous = Request();
        await client.SendAsync(previous, CancellationToken.None);
        process.OnRequest = request =>
        {
            process.Emit(process.Response(previous));
            process.Emit(process.Response(Request()));
            process.Emit(process.Response(request));
            process.Emit(process.Response(request));
            return Task.CompletedTask;
        };
        var current = Request();
        Assert.Equal(current.CorrelationId, (await client.SendAsync(current, CancellationToken.None)).CorrelationId);
        await Until(() => client.Diagnostics.Count >= 3);
        Assert.All(client.Diagnostics, item => Assert.Equal(ManagedBrowserHelperDiagnostic.LateOrDuplicateResponse, item));
    }

    [Fact]
    public async Task RepeatedCorrelationIsRejectedBeforeSecondWrite()
    {
        var process = new FakeProcess();
        await using var client = await Start(process);
        var request = Request();
        await client.SendAsync(request, CancellationToken.None);
        var error = await Assert.ThrowsAsync<ManagedBrowserHelperClientException>(() => client.SendAsync(request, CancellationToken.None));
        Assert.Equal(ManagedBrowserHelperClientError.DuplicateCorrelationId, error.Error);
        Assert.Equal(2, process.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HardTimeoutCoversUncooperativeReadAndWrite(bool stallWrite)
    {
        var process = new FakeProcess();
        var lease = new FakeLease();
        await using var client = await Start(process, lease);
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OnRequest = _ => stallWrite ? stalled.Task : Task.CompletedTask;
        var request = IdentityRequest(60);
        var result = await client.SendAsync(request, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ManagedBrowserIdentityResultKind.TimedOut, Assert.IsType<ManagedBrowserIdentityResult>(result.Result).Kind);
        ManagedBrowserProtocol.ValidateResponseFor(request, result);
        await Until(() => process.Killed);
        if (stallWrite) Assert.False(lease.Released);
        stalled.TrySetResult();
        await Until(() => lease.Released);
    }

    [Fact]
    public async Task CancellationKillsChildAndReturnsTypedIdentityWithoutWaitingForWrite()
    {
        var process = new FakeProcess();
        await using var client = await Start(process);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OnRequest = _ => gate.Task;
        using var cancellation = new CancellationTokenSource();
        var operation = client.SendAsync(IdentityRequest(1000), cancellation.Token);
        await Until(() => process.Requests.Count == 2);
        cancellation.Cancel();
        var result = await operation.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ManagedBrowserIdentityResultKind.Cancelled, Assert.IsType<ManagedBrowserIdentityResult>(result.Result).Kind);
        await Until(() => process.Killed);
        gate.SetResult();
    }

    [Fact]
    public async Task PreCancelledRequestDoesNotWriteOrKillHealthyChild()
    {
        var process = new FakeProcess();
        await using var client = await Start(process);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await client.SendAsync(IdentityRequest(), cancellation.Token);
        Assert.Equal(ManagedBrowserIdentityResultKind.Cancelled, Assert.IsType<ManagedBrowserIdentityResult>(result.Result).Kind);
        Assert.Single(process.Requests);
        Assert.False(process.Killed);
        await client.SendAsync(Request(), CancellationToken.None);
    }

    [Fact]
    public async Task ChildExitCompletesPendingRequestEvenIfStdoutRemainsOpen()
    {
        var process = new FakeProcess { KeepOutputOpen = true };
        var lease = new FakeLease();
        await using var client = await Start(process, lease);
        process.OnRequest = _ => { process.Exit(); return Task.CompletedTask; };
        var error = await Assert.ThrowsAsync<ManagedBrowserHelperClientException>(() => client.SendAsync(Request(), CancellationToken.None));
        Assert.Equal(ManagedBrowserHelperClientError.ChildExited, error.Error);
        Assert.False(lease.Released);
        process.CompleteOutput();
        await Until(() => lease.Released);
    }

    [Fact]
    public async Task LateResponseAfterTimeoutIsDiscardedWhileLeaseWaitsForBothPumps()
    {
        var process = new FakeProcess { KeepOutputOpen = true };
        var lease = new FakeLease();
        await using var client = await Start(process, lease);
        process.OnRequest = _ => Task.CompletedTask;
        var request = IdentityRequest(50);
        var response = await client.SendAsync(request, CancellationToken.None);
        Assert.Equal(ManagedBrowserIdentityResultKind.TimedOut, Assert.IsType<ManagedBrowserIdentityResult>(response.Result).Kind);
        await Until(() => process.Killed);
        process.Emit(ManagedBrowserResponse.For(request, new ManagedBrowserIdentityResult(ManagedBrowserIdentityResultKind.Observed, "private-uid")));
        await Until(() => client.Diagnostics.Contains(ManagedBrowserHelperDiagnostic.LateOrDuplicateResponse));
        Assert.False(lease.Released);
        process.Output.Complete();
        await Task.Yield();
        Assert.False(lease.Released);
        process.Error.Complete();
        await Until(() => lease.Released);
    }

    [Fact]
    public async Task ConcurrentRequestIsRejectedAndDisposeDoesNotQueueBehindHungOperation()
    {
        var process = new FakeProcess();
        var lease = new FakeLease();
        var client = await Start(process, lease);
        process.OnRequest = _ => Task.CompletedTask;
        var pending = client.SendAsync(IdentityRequest(120000), CancellationToken.None);
        await Until(() => process.Requests.Count == 2);
        var busy = await Assert.ThrowsAsync<ManagedBrowserHelperClientException>(() => client.SendAsync(Request(), CancellationToken.None));
        Assert.Equal(ManagedBrowserHelperClientError.Busy, busy.Error);
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ManagedBrowserIdentityResultKind.Cancelled, Assert.IsType<ManagedBrowserIdentityResult>((await pending).Result).Kind);
        Assert.True(process.Killed);
        Assert.Equal(ManagedBrowserAction.Shutdown, process.Requests.Last().Action);
        await Until(() => lease.Released);
        var disposed = await Assert.ThrowsAsync<ManagedBrowserHelperClientException>(() => client.SendAsync(Request(), CancellationToken.None));
        Assert.Equal(ManagedBrowserHelperClientError.Disposed, disposed.Error);
    }

    [Fact]
    public async Task GracefulDisposeWaitsForExitWithoutKillAndIsIdempotent()
    {
        var process = new FakeProcess();
        var lease = new FakeLease();
        var client = await Start(process, lease);
        await Task.WhenAll(client.DisposeAsync().AsTask(), client.DisposeAsync().AsTask());
        Assert.Equal(ManagedBrowserAction.Shutdown, process.Requests.Last().Action);
        Assert.False(process.Killed);
        Assert.True(lease.Released);
        Assert.Equal(1, lease.ReleaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeOrExitInterruptsEvenAfterResponseArrivesBeforeStalledWriteFinishes(bool exit)
    {
        var process = new FakeProcess();
        var lease = new FakeLease();
        var client = await Start(process, lease);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OnRequest = request =>
        {
            process.Emit(ManagedBrowserResponse.For(request, new ManagedBrowserIdentityResult(ManagedBrowserIdentityResultKind.LoginRequired)));
            return gate.Task;
        };
        var pending = client.SendAsync(IdentityRequest(120000), CancellationToken.None);
        await Until(() => process.Requests.Count == 2);
        // A duplicate diagnostic proves the first response was consumed, not merely queued.
        process.Emit(ManagedBrowserResponse.For(process.Requests.Last(), new ManagedBrowserIdentityResult(ManagedBrowserIdentityResultKind.LoginRequired)));
        await Until(() => client.Diagnostics.Contains(ManagedBrowserHelperDiagnostic.LateOrDuplicateResponse));
        try
        {
            if (exit)
            {
                process.Exit();
                var error = await Assert.ThrowsAsync<ManagedBrowserHelperClientException>(() => pending.WaitAsync(TimeSpan.FromSeconds(1)));
                Assert.Equal(ManagedBrowserHelperClientError.ChildExited, error.Error);
            }
            else
            {
                await client.DisposeAsync();
                var result = await pending.WaitAsync(TimeSpan.FromSeconds(1));
                Assert.Equal(ManagedBrowserIdentityResultKind.Cancelled, Assert.IsType<ManagedBrowserIdentityResult>(result.Result).Kind);
            }
            Assert.False(lease.Released);
        }
        finally
        {
            gate.TrySetResult();
            await client.DisposeAsync();
        }
        await Until(() => lease.Released);
    }

    [Fact]
    public async Task ShutdownAckWithoutExitStillKillsAfterBound()
    {
        var process = new FakeProcess();
        var client = await Start(process);
        process.OnRequest = request => { process.Emit(process.Response(request)); return Task.CompletedTask; };
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(process.Killed);
    }

    [Fact]
    public async Task SessionBoundReservesLastFrameForShutdown()
    {
        var process = new FakeProcess();
        var client = await Start(process);
        for (var index = 0; index < ManagedBrowserProtocol.MaximumRequestsPerSession - 2; index++)
            await client.SendAsync(Request(), CancellationToken.None);
        var error = await Assert.ThrowsAsync<ManagedBrowserHelperClientException>(() => client.SendAsync(Request(), CancellationToken.None));
        Assert.Equal(ManagedBrowserHelperClientError.RestartRequired, error.Error);
        await client.DisposeAsync();
        Assert.Equal(ManagedBrowserProtocol.MaximumRequestsPerSession, process.Requests.Count);
        Assert.Equal(ManagedBrowserAction.Shutdown, process.Requests.Last().Action);
    }

    [Fact]
    public async Task HandshakeTimeoutAlsoKillsAndRetainsLeaseUntilPumpsFinish()
    {
        var process = new FakeProcess { KeepOutputOpen = true, OnRequest = _ => Task.CompletedTask };
        var lease = new FakeLease();
        var error = await Assert.ThrowsAsync<ManagedBrowserHelperClientException>(() => Start(process, lease));
        Assert.Equal(ManagedBrowserHelperClientError.TimedOut, error.Error);
        await Until(() => process.Killed);
        Assert.False(lease.Released);
        process.CompleteOutput();
        await Until(() => lease.Released);
    }

    [Fact]
    public async Task FailedTerminationDoesNotReleaseLeaseOrLeakExceptionDetails()
    {
        var process = new FakeProcess { FailKill = true };
        var lease = new FakeLease();
        var client = await Start(process, lease);
        process.OnRequest = _ => Task.CompletedTask;
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(lease.Released);
        Assert.Contains(ManagedBrowserHelperDiagnostic.TerminationFailed, client.Diagnostics);
        Assert.Contains(ManagedBrowserHelperDiagnostic.CleanupDeferred, client.Diagnostics);
        Assert.DoesNotContain("secret", string.Join(",", client.Diagnostics));
        process.Exit();
        await Until(() => lease.Released);
    }

    [Fact]
    public async Task HandshakeCancellationKillsOwnedChildAndUsesSafeTypedException()
    {
        var process = new FakeProcess { OnRequest = _ => Task.CompletedTask };
        var lease = new FakeLease();
        using var cancellation = new CancellationTokenSource();
        var starting = ManagedBrowserHelperClient.StartAsync(lease, () => process, Fast, cancellation.Token);
        await Until(() => process.Requests.Count == 1);
        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<ManagedBrowserHelperClientException>(() => starting);
        Assert.Equal(ManagedBrowserHelperClientError.Cancelled, error.Error);
        await Until(() => process.Killed && lease.Released);
    }

    [SelectedManagedHelperPackageFact("BAIDU_BATCH_TRANSFER_TEST_HELPER_DIRECTORY")]
    public async Task PackagedHelperHandshakeHealthShutdownNeedsNoBrowserAndRetainsVerifiedLease()
    {
        var root = Environment.GetEnvironmentVariable("BAIDU_BATCH_TRANSFER_TEST_HELPER_DIRECTORY")
            ?? throw new InvalidOperationException("The selected packaged helper is required.");
        var client = await ManagedBrowserHelperClient.StartAsync(root, CancellationToken.None);
        try
        {
            var response = await client.SendAsync(Request(), CancellationToken.None);
            Assert.IsType<ManagedBrowserHealthResult>(response.Result);
        }
        finally { await client.DisposeAsync(); }
        Assert.Empty(client.Diagnostics);
    }

    [SelectedManagedHelperPackageFact("BAIDU_BATCH_TRANSFER_TEST_HELPER_DIRECTORY")]
    public async Task PackagedHelperCanObserveTheSelectedLiveAcceptanceTarget()
    {
        var root = Environment.GetEnvironmentVariable("BAIDU_BATCH_TRANSFER_TEST_HELPER_DIRECTORY")
            ?? throw new InvalidOperationException("The selected packaged helper is required.");
        var port = int.Parse(Environment.GetEnvironmentVariable("BAIDU_BATCH_TRANSFER_TEST_DEVTOOLS_PORT")
            ?? throw new InvalidOperationException("The selected DevTools port is required."));
        var browserId = Guid.Parse(Environment.GetEnvironmentVariable("BAIDU_BATCH_TRANSFER_TEST_BROWSER_ID")
            ?? throw new InvalidOperationException("The selected browser ID is required."));
        var targetId = Environment.GetEnvironmentVariable("BAIDU_BATCH_TRANSFER_TEST_TARGET_ID")
            ?? throw new InvalidOperationException("The selected target ID is required.");
        await using var client = await ManagedBrowserHelperClient.StartAsync(root, CancellationToken.None);
        var operationId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var health = new ManagedBrowserRequest(Guid.NewGuid(), operationId, accountId, 1,
            ManagedBrowserAction.Health, ManagedBrowserRequestPayload.Empty);
        Assert.IsType<ManagedBrowserHealthResult>((await client.SendAsync(health, CancellationToken.None)).Result);
        var observe = new ManagedBrowserRequest(Guid.NewGuid(), operationId, accountId, 1,
            ManagedBrowserAction.ObserveAccountIdentity,
            new ManagedBrowserObserveAccountIdentityRequest(browserId, targetId,
                new Uri($"http://127.0.0.1:{port}/"), 5000));
        var response = await client.SendAsync(observe, CancellationToken.None);
        Assert.Equal(ManagedBrowserIdentityResultKind.Observed,
            Assert.IsType<ManagedBrowserIdentityResult>(response.Result).Kind);
        Assert.Empty(client.Diagnostics);
    }

    [Fact]
    public async Task TargetNavigationPrecancellationIsKnownNotDispatchedAndSendsNoFrame()
    {
        var process = new FakeProcess();
        await using var client = await Start(process);
        var before = process.Requests.Count;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var binding = TargetBinding();
        var requests = new[]
        {
            new ManagedBrowserRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
                ManagedBrowserAction.OpenTransferTargetRoot, new ManagedBrowserOpenTransferTargetRootRequest(binding)),
            new ManagedBrowserRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
                ManagedBrowserAction.OpenTransferSourceAfterTargetRead,
                new ManagedBrowserOpenTransferSourceAfterTargetReadRequest(binding, TargetInventory().ToHeader())),
        };

        var root = await client.SendAsync(requests[0], cancelled.Token);
        var restore = await client.SendAsync(requests[1], cancelled.Token);

        Assert.Equal(ManagedBrowserTargetNavigationState.KnownNotDispatched,
            Assert.IsType<ManagedBrowserOpenTransferTargetRootResult>(root.Result).NavigationState);
        Assert.Equal(ManagedBrowserTargetNavigationState.KnownNotDispatched,
            Assert.IsType<ManagedBrowserOpenTransferSourceAfterTargetReadResult>(restore.Result).NavigationState);
        Assert.Equal(before, process.Requests.Count);
    }

    [Fact]
    public async Task TargetNavigationCancellationRaceBeforeWriteTaskIsKnownNotDispatched()
    {
        var process = new FakeProcess();
        await using var client = await Start(process);
        var before = process.Requests.Count;
        var sync = typeof(ManagedBrowserHelperClient)
            .GetField("sync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(client)!;
        using var cancellation = new CancellationTokenSource();
        Task<ManagedBrowserResponse> sending;
        Monitor.Enter(sync);
        try
        {
            sending = Task.Run(() => client.SendAsync(
                new ManagedBrowserRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
                    ManagedBrowserAction.OpenTransferTargetRoot,
                    new ManagedBrowserOpenTransferTargetRootRequest(TargetBinding())), cancellation.Token));
            Thread.Sleep(30);
            cancellation.Cancel();
        }
        finally { Monitor.Exit(sync); }

        var result = Assert.IsType<ManagedBrowserOpenTransferTargetRootResult>((await sending).Result);
        Assert.Equal(ManagedBrowserOpenTransferTargetRootResultKind.Cancelled, result.Kind);
        Assert.Equal(ManagedBrowserTargetNavigationState.KnownNotDispatched, result.NavigationState);
        Assert.Equal(before, process.Requests.Count);
    }

    [Fact]
    public async Task TargetNavigationTimeoutWhileWaitingBeforeWriteTaskIsKnownNotDispatched()
    {
        var process = new FakeProcess();
        await using var client = await Start(process);
        var before = process.Requests.Count;
        var sync = typeof(ManagedBrowserHelperClient)
            .GetField("sync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(client)!;
        Task<ManagedBrowserResponse> sending;
        Monitor.Enter(sync);
        try
        {
            sending = Task.Run(() => client.SendAsync(
                new ManagedBrowserRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
                    ManagedBrowserAction.OpenTransferTargetRoot,
                    new ManagedBrowserOpenTransferTargetRootRequest(TargetBinding(timeoutMilliseconds: 10))),
                CancellationToken.None));
            Thread.Sleep(40);
        }
        finally { Monitor.Exit(sync); }

        var result = Assert.IsType<ManagedBrowserOpenTransferTargetRootResult>((await sending).Result);
        Assert.Equal(ManagedBrowserOpenTransferTargetRootResultKind.TimedOut, result.Kind);
        Assert.Equal(ManagedBrowserTargetNavigationState.KnownNotDispatched, result.NavigationState);
        Assert.Equal(before, process.Requests.Count);
    }

    [Fact]
    public async Task TargetNavigationChildExitAfterDispatchIsUnavailableAndRouteOutcomeUnknown()
    {
        var process = new FakeProcess();
        await using var client = await Start(process);
        process.OnRequest = _ =>
        {
            process.Exit();
            return Task.CompletedTask;
        };
        var request = new ManagedBrowserRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
            ManagedBrowserAction.OpenTransferTargetRoot,
            new ManagedBrowserOpenTransferTargetRootRequest(TargetBinding()));

        var response = await client.SendAsync(request, CancellationToken.None);

        var result = Assert.IsType<ManagedBrowserOpenTransferTargetRootResult>(response.Result);
        Assert.Equal(ManagedBrowserOpenTransferTargetRootResultKind.BrowserUnavailable, result.Kind);
        Assert.Equal(ManagedBrowserTargetNavigationState.RouteOutcomeUnknown, result.NavigationState);
    }

    private static Task<ManagedBrowserHelperClient> Start(FakeProcess process, FakeLease? lease = null) =>
        ManagedBrowserHelperClient.StartAsync(lease ?? new FakeLease(), () => process, Fast, CancellationToken.None);

    private static ManagedBrowserRequest Request() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
        ManagedBrowserAction.Health, ManagedBrowserRequestPayload.Empty);

    private static ManagedBrowserRequest IdentityRequest(int timeout = 1000) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 7,
        ManagedBrowserAction.ObserveAccountIdentity,
        new ManagedBrowserObserveAccountIdentityRequest(Guid.NewGuid(), "target", new Uri("http://127.0.0.1:9222/"), timeout));

    private static ManagedBrowserTargetActionBinding TargetBinding(int timeoutMilliseconds = 1_000)
    {
        const string uri = "https://pan.baidu.com/s/source_1?pwd=A1b2";
        var source = new ManagedBrowserSourceSnapshot("uid-1", "source-generation", 1, true, true,
            [new ManagedBrowserSourceItem("source-1", ManagedBrowserSourceItemKind.File, "source.txt")],
            "4cdb5ac7a450d6125ebaef9287cb8431f3976b5374ebc5de96c2570786995956");
        return new ManagedBrowserTargetActionBinding(
            Guid.Parse("44444444-4444-4444-4444-444444444444"), "target-1",
            new Uri("http://127.0.0.1:43127/"), timeoutMilliseconds, uri, "uid-1", source);
    }

    private static ManagedBrowserTargetInventorySnapshot TargetInventory() => new(
        Guid.Parse("44444444-4444-4444-4444-444444444444"), "target-1",
        new Uri("http://127.0.0.1:43127/"), "uid-1", "generation-1", "parent-1", "/parent",
        ["root-1", "parent-1"], 1, true, true, 1,
        [new ManagedBrowserTargetInventoryItem("file-1", ManagedBrowserTargetInventoryItemKind.File, "one.txt")]);

    private static byte[] Frame(byte[] payload)
    {
        var result = new byte[payload.Length + 4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(result, payload.Length);
        payload.CopyTo(result, 4);
        return result;
    }

    private static async Task Until(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(timeout.Elapsed < TimeSpan.FromSeconds(5), "Condition did not complete within test bound.");
            await Task.Delay(5);
        }
    }

    private sealed class FakeLease : IDisposable
    {
        private int count;
        public bool Released => Volatile.Read(ref count) > 0;
        public int ReleaseCount => Volatile.Read(ref count);
        public void Dispose() => Interlocked.Increment(ref count);
    }

    private sealed class FakeProcess : IManagedBrowserHelperProcess
    {
        public bool HasStarted { get; private set; }
        public bool FailBeforeStart { get; init; }
        public bool FailAfterStart { get; init; }
        public void Start()
        {
            if (FailBeforeStart) throw new IOException("cookie=secret");
            HasStarted = true;
            if (FailAfterStart) throw new IOException("cookie=secret");
        }
        private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FakeProcess() => StandardInput = new WriteStream(async bytes =>
        {
            var request = ManagedBrowserProtocol.DecodeRequest(bytes.Span[4..]);
            Requests.Enqueue(request);
            if (OnRequest is not null) await OnRequest(request);
            else
            {
                Emit(Response(request));
                if (request.Action == ManagedBrowserAction.Shutdown) Exit();
            }
        });
        public int Id => 987654;
        public Stream StandardInput { get; }
        public FeedStream Output { get; } = new();
        public FeedStream Error { get; } = new();
        public Stream StandardOutput => Output;
        public Stream StandardError => Error;
        public Task ExitTask => exited.Task;
        public ConcurrentQueue<ManagedBrowserRequest> Requests { get; } = new();
        public Func<ManagedBrowserRequest, Task>? OnRequest { get; set; }
        public bool KeepOutputOpen { get; init; }
        public bool FailKill { get; init; }
        public bool Killed { get; private set; }
        public void Kill() { if (FailKill) throw new IOException("cookie=secret"); Killed = true; Exit(); }
        public void Exit() { exited.TrySetResult(); if (!KeepOutputOpen) CompleteOutput(); }
        public void CompleteOutput() { Output.Complete(); Error.Complete(); }
        public void Emit(ManagedBrowserResponse response) => Output.Emit(ManagedBrowserProtocol.EncodeResponse(response));
        public ManagedBrowserResponse Response(ManagedBrowserRequest request) => ManagedBrowserResponse.For(request, request.Payload is ManagedBrowserHandshakeRequest handshake
            ? new ManagedBrowserHandshakeResult(handshake.Nonce, handshake.ParentProcessId, Id, ManagedBrowserProtocol.RequiredCapabilities)
            : request.Action == ManagedBrowserAction.Shutdown ? ManagedBrowserShutdownResult.ShuttingDown : ManagedBrowserHealthResult.Healthy);
        public void Dispose() { StandardInput.Dispose(); Output.Dispose(); Error.Dispose(); }
    }

    private abstract class TestStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class WriteStream(Func<ReadOnlyMemory<byte>, Task> write) : TestStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => new(write(buffer));
    }

    private sealed class FeedStream : TestStream
    {
        private readonly Channel<byte[]> chunks = Channel.CreateUnbounded<byte[]>();
        private ReadOnlyMemory<byte> pending;
        public void Emit(byte[] bytes) => chunks.Writer.TryWrite(bytes);
        public void Complete() => chunks.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (pending.IsEmpty)
            {
                if (!await chunks.Reader.WaitToReadAsync()) return 0;
                pending = await chunks.Reader.ReadAsync();
            }
            var count = Math.Min(buffer.Length, pending.Length);
            pending[..count].CopyTo(buffer);
            pending = pending[count..];
            return count;
        }
    }
}

[CollectionDefinition(ManagedBrowserHelperClientTests.NonParallelCollection, DisableParallelization = true)]
public sealed class ManagedBrowserHelperClientTimingTestCollection;
