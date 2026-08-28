using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

public enum ManagedBrowserHelperClientError
{
    LaunchFailed, HandshakeRejected, ProtocolViolation, TimedOut, Cancelled, ChildExited,
    Busy, DuplicateCorrelationId, RestartRequired, Disposed, Unavailable, InvalidAction,
}

public enum ManagedBrowserHelperDiagnostic
{
    HelperProtocolError, HelperRuntimeError, HelperSessionLimitReached,
    UnrecognizedStderr, LateOrDuplicateResponse, ProtocolViolation, TransportFailure,
    TerminationFailed, CleanupDeferred,
}

public sealed class ManagedBrowserHelperClientException(ManagedBrowserHelperClientError error)
    : Exception($"Managed browser helper client failure: {error}.")
{
    public ManagedBrowserHelperClientError Error { get; } = error;
    public override string ToString() => $"ManagedBrowserHelperClientException {{ Error = {Error} }}";
}

internal sealed record ManagedBrowserHelperClientOptions(
    TimeSpan HandshakeTimeout, TimeSpan CommandTimeout, TimeSpan ShutdownTimeout)
{
    public static ManagedBrowserHelperClientOptions Default { get; } = new(
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2));
}

internal interface IManagedBrowserHelperProcess : IDisposable
{
    bool HasStarted { get; }
    void Start();
    int Id { get; }
    Stream StandardInput { get; }
    Stream StandardOutput { get; }
    Stream StandardError { get; }
    Task ExitTask { get; }
    void Kill();
}

/// <summary>
/// One owned child and one live command; concurrent commands fail with Busy (no queue).
/// Timeouts/cancellation invalidate the session, never replay work, and kill only this child.
/// The 1,024-frame budget includes handshake and reserves its final frame for shutdown.
/// Dispose is bounded; if exit or pipe completion cannot be confirmed, the background cleanup
/// task continues owning the verified lease. No browser process is attached to or terminated.
/// </summary>
public sealed class ManagedBrowserHelperClient : IAsyncDisposable, IManagedBrowserLoginHelper
{
    private readonly object sync = new();
    private readonly IManagedBrowserHelperProcess process;
    private readonly IDisposable lease;
    private readonly ManagedBrowserHelperClientOptions options;
    private readonly HashSet<Guid> seen = [];
    private readonly Queue<ManagedBrowserHelperDiagnostic> diagnostics = new(64);
    private readonly Task stdoutPump;
    private readonly Task stderrPump;
    private readonly Task cleanup;
    private Task writeTask = Task.CompletedTask;
    private Task? disposeTask;
    private Pending? pending;
    private ManagedBrowserHelperClientError? failure;
    private bool ready;
    private bool disposing;
    private int killScheduled;

    private ManagedBrowserHelperClient(IManagedBrowserHelperProcess process, IDisposable lease,
        ManagedBrowserHelperClientOptions options)
    {
        this.process = process;
        this.lease = lease;
        this.options = options;
        stdoutPump = Task.Run(PumpStdoutAsync);
        stderrPump = Task.Run(PumpStderrAsync);
        cleanup = Task.Run(CleanupAfterExitAsync);
    }

    public IReadOnlyList<ManagedBrowserHelperDiagnostic> Diagnostics
    {
        get { lock (sync) return diagnostics.ToArray(); }
    }

    /// <summary>Verifies and privately snapshots the bundled helper before any executable launch.</summary>
    public static async Task<ManagedBrowserHelperClient> StartAsync(
        string helperRoot, CancellationToken cancellationToken)
    {
        var verified = await new ManagedBrowserHelperIntegrityVerifier()
            .VerifyAsync(helperRoot, cancellationToken).ConfigureAwait(false);
        return await StartAsync(verified,
            () => new OwnedProcess(CreateStartInfo(verified.ExecutablePath, verified.MainScriptPath)),
            ManagedBrowserHelperClientOptions.Default, cancellationToken).ConfigureAwait(false);
    }

    // The factory only creates a process object. Ownership is established before Start, so
    // post-start initialization failures cannot release the snapshot underneath a live child.
    internal static async Task<ManagedBrowserHelperClient> StartAsync(IDisposable lease,
        Func<IManagedBrowserHelperProcess> createProcess, ManagedBrowserHelperClientOptions options,
        CancellationToken cancellationToken)
    {
        IManagedBrowserHelperProcess? process = null;
        try
        {
            if (cancellationToken.IsCancellationRequested)
                throw Failure(ManagedBrowserHelperClientError.Cancelled);
            process = createProcess();
            process.Start();
        }
        catch (Exception error)
        {
            var reason = error is ManagedBrowserHelperClientException clientError
                ? clientError.Error : ManagedBrowserHelperClientError.LaunchFailed;
            if (process is { HasStarted: true })
                new ManagedBrowserHelperClient(process, lease, options).Terminate(reason);
            else
            {
                try { process?.Dispose(); }
                catch (Exception) { /* Only an unstarted process object can be released here. */ }
                lease.Dispose();
            }
            throw Failure(reason);
        }

        var client = new ManagedBrowserHelperClient(process, lease, options);
        try
        {
            // The nonce is created only after launch, and is sent only in the first framed stdin message.
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var request = ControlRequest(ManagedBrowserAction.Handshake,
                new ManagedBrowserHandshakeRequest(nonce, Environment.ProcessId, ManagedBrowserProtocol.RequiredCapabilities));
            var response = await client.ExchangeAsync(request, options.HandshakeTimeout,
                cancellationToken, handshake: true).ConfigureAwait(false);
            var result = (ManagedBrowserHandshakeResult)response.Result;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(nonce), Encoding.ASCII.GetBytes(result.Nonce)) ||
                result.ParentProcessId != Environment.ProcessId || result.HelperProcessId != process.Id ||
                !ManagedBrowserProtocol.CapabilitiesMatch(result.Capabilities))
                throw Failure(ManagedBrowserHelperClientError.HandshakeRejected);
            lock (client.sync)
            {
                if (client.failure is { } failure) throw Failure(failure);
                client.ready = true;
            }
            return client;
        }
        catch (Exception error)
        {
            var reason = error is ManagedBrowserHelperClientException clientError
                ? clientError.Error : ManagedBrowserHelperClientError.HandshakeRejected;
            client.Terminate(reason);
            throw Failure(reason);
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string executablePath, string mainScriptPath)
    {
        var info = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(mainScriptPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-B", "-E", "-s", mainScriptPath }) info.ArgumentList.Add(argument);
        return info;
    }

    /// <summary>Only post-handshake commands are accepted. Shutdown is owned by DisposeAsync.</summary>
    public Task<ManagedBrowserResponse> SendAsync(ManagedBrowserRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Action is ManagedBrowserAction.Handshake or ManagedBrowserAction.Shutdown)
            return Task.FromException<ManagedBrowserResponse>(Failure(ManagedBrowserHelperClientError.InvalidAction));
        var timeout = request.Payload is ManagedBrowserObserveAccountIdentityRequest observation
            ? TimeSpan.FromMilliseconds(observation.TimeoutMilliseconds) : options.CommandTimeout;
        return ExchangeAsync(request, timeout, cancellationToken, handshake: false);
    }

    private async Task<ManagedBrowserResponse> ExchangeAsync(ManagedBrowserRequest request,
        TimeSpan timeout, CancellationToken cancellationToken, bool handshake)
    {
        Pending current;
        Task writing;
        lock (sync)
        {
            if (disposing) throw Failure(ManagedBrowserHelperClientError.Disposed);
            if (failure is { } error) throw Failure(error);
            if (!handshake && !ready) throw Failure(ManagedBrowserHelperClientError.Unavailable);
            if (cancellationToken.IsCancellationRequested)
                return TerminalResponse(request, ManagedBrowserHelperClientError.Cancelled);
            if (seen.Contains(request.CorrelationId)) throw Failure(ManagedBrowserHelperClientError.DuplicateCorrelationId);
            if (pending is not null) throw Failure(ManagedBrowserHelperClientError.Busy);
            if (seen.Count >= ManagedBrowserProtocol.MaximumRequestsPerSession - 1)
                throw Failure(ManagedBrowserHelperClientError.RestartRequired);
            seen.Add(request.CorrelationId);
            current = pending = new Pending(request);
            writing = writeTask = Task.Run(() => WriteAsync(request));
        }

        try
        {
            // WaitAsync supplies the hard deadline even when a stream ignores its cancellation token.
            var response = await AwaitExchangeAsync(writing, current)
                .WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return response ?? TerminalResponse(request, await current.Interrupted.Task.ConfigureAwait(false));
        }
        catch (TimeoutException)
        {
            Terminate(ManagedBrowserHelperClientError.TimedOut);
            return TerminalResponse(request, ManagedBrowserHelperClientError.TimedOut);
        }
        catch (OperationCanceledException)
        {
            Terminate(ManagedBrowserHelperClientError.Cancelled);
            return TerminalResponse(request, ManagedBrowserHelperClientError.Cancelled);
        }
        catch (ManagedBrowserHelperClientException error) when (error.Error == ManagedBrowserHelperClientError.Cancelled)
        {
            return TerminalResponse(request, error.Error);
        }
        finally
        {
            lock (sync) { if (ReferenceEquals(pending, current)) pending = null; }
        }
    }

    private static async Task<ManagedBrowserResponse?> AwaitExchangeAsync(Task writing, Pending current)
    {
        // Interruption is separate from the response: an already received response must not
        // prevent Dispose/exit from interrupting a write that has still not completed.
        var completed = Task.WhenAll(writing, current.Completion.Task);
        if (await Task.WhenAny(completed, current.Interrupted.Task).ConfigureAwait(false) != completed)
            return null; // A timed-out caller leaves no faulted background exchange task behind.
        await completed.ConfigureAwait(false);
        return await current.Completion.Task.ConfigureAwait(false);
    }

    private async Task WriteAsync(ManagedBrowserRequest request)
    {
        try
        {
            await ManagedBrowserProtocol.WriteRequestAsync(process.StandardInput, request, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Record(ManagedBrowserHelperDiagnostic.TransportFailure);
            Terminate(ManagedBrowserHelperClientError.Unavailable);
        }
    }

    private async Task PumpStdoutAsync()
    {
        try
        {
            while (await ManagedBrowserProtocol.ReadResponseAsync(process.StandardOutput, CancellationToken.None).ConfigureAwait(false) is { } response)
            {
                lock (sync)
                {
                    if (pending is not { } current || current.Completion.Task.IsCompleted || current.Interrupted.Task.IsCompleted ||
                        response.CorrelationId != current.Request.CorrelationId)
                    {
                        Record(ManagedBrowserHelperDiagnostic.LateOrDuplicateResponse);
                        continue;
                    }
                    ManagedBrowserProtocol.ValidateResponseFor(current.Request, response);
                    current.Completion.TrySetResult(response);
                }
            }
            Terminate(ManagedBrowserHelperClientError.ChildExited);
        }
        catch (ManagedBrowserProtocolException)
        {
            Record(ManagedBrowserHelperDiagnostic.ProtocolViolation);
            Terminate(ManagedBrowserHelperClientError.ProtocolViolation);
        }
        catch (Exception)
        {
            Record(ManagedBrowserHelperDiagnostic.TransportFailure);
            Terminate(ManagedBrowserHelperClientError.Unavailable);
        }
    }

    private async Task PumpStderrAsync()
    {
        // Never use ReadLine (unbounded) or expose a raw line. Overlong/non-ASCII lines are
        // discarded through their newline, so a safe-looking suffix cannot be accepted.
        var buffer = new byte[256];
        var line = new byte[64];
        var length = 0;
        var invalid = false;
        try
        {
            int count;
            while ((count = await process.StandardError.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false)) != 0)
            {
                for (var index = 0; index < count; index++)
                {
                    var value = buffer[index];
                    if (value == '\n')
                    {
                        RecordStderr(line, length, invalid);
                        length = 0;
                        invalid = false;
                    }
                    else if (length == line.Length || value > 127) invalid = true;
                    else if (!invalid) line[length++] = value;
                }
            }
            if (length > 0 || invalid) RecordStderr(line, length, invalid);
        }
        catch (Exception) { Record(ManagedBrowserHelperDiagnostic.TransportFailure); }
    }

    private void RecordStderr(byte[] line, int length, bool invalid)
    {
        if (length > 0 && line[length - 1] == '\r') length--;
        var code = invalid ? null : Encoding.ASCII.GetString(line, 0, length);
        Record(code switch
        {
            "HELPER_PROTOCOL_ERROR" => ManagedBrowserHelperDiagnostic.HelperProtocolError,
            "HELPER_RUNTIME_ERROR" => ManagedBrowserHelperDiagnostic.HelperRuntimeError,
            "HELPER_SESSION_LIMIT_REACHED" => ManagedBrowserHelperDiagnostic.HelperSessionLimitReached,
            _ => ManagedBrowserHelperDiagnostic.UnrecognizedStderr,
        });
    }

    private void Record(ManagedBrowserHelperDiagnostic diagnostic)
    {
        lock (sync)
        {
            if (diagnostics.Count == 64) diagnostics.Dequeue();
            diagnostics.Enqueue(diagnostic);
        }
    }

    private void Terminate(ManagedBrowserHelperClientError reason)
    {
        lock (sync)
        {
            failure ??= reason;
            pending?.Interrupted.TrySetResult(failure.Value);
        }
        if (Interlocked.Exchange(ref killScheduled, 1) != 0) return;
        _ = Task.Run(() =>
        {
            try { if (!process.ExitTask.IsCompleted) process.Kill(); }
            catch (Exception) { Record(ManagedBrowserHelperDiagnostic.TerminationFailed); }
        });
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposeTask is not null) return new ValueTask(disposeTask);
            disposing = true;
            pending?.Interrupted.TrySetResult(ManagedBrowserHelperClientError.Cancelled);
            pending = null;
            // A shutdown write is chained after the existing write, never interleaved with it.
            // The independent deadline below also covers a write that never completes.
            if (failure is null && seen.Count < ManagedBrowserProtocol.MaximumRequestsPerSession)
            {
                var request = ControlRequest(ManagedBrowserAction.Shutdown, ManagedBrowserRequestPayload.Empty);
                seen.Add(request.CorrelationId);
                var previousWrite = writeTask;
                writeTask = Task.Run(async () =>
                {
                    await previousWrite.ConfigureAwait(false);
                    lock (sync)
                    {
                        if (failure is not null || process.ExitTask.IsCompleted) return;
                        pending = new Pending(request);
                    }
                    await WriteAsync(request).ConfigureAwait(false);
                });
            }
            disposeTask = Task.Run(DisposeBoundedAsync);
            return new ValueTask(disposeTask);
        }
    }

    private async Task DisposeBoundedAsync()
    {
        try { await cleanup.WaitAsync(options.ShutdownTimeout).ConfigureAwait(false); return; }
        catch (TimeoutException) { }
        Terminate(ManagedBrowserHelperClientError.Disposed);
        try { await cleanup.WaitAsync(options.ShutdownTimeout).ConfigureAwait(false); }
        catch (TimeoutException) { Record(ManagedBrowserHelperDiagnostic.CleanupDeferred); }
    }

    private async Task CleanupAfterExitAsync()
    {
        // This task deliberately retains this instance/lease if child exit or a pipe cannot
        // be confirmed. Never release an integrity snapshot just because Start/Dispose returned.
        // Exit is fail-closed: a response still queued in stdout may lose the race and be
        // discarded. We do not keep an operation alive indefinitely to await a dead child.
        await process.ExitTask.ConfigureAwait(false);
        lock (sync)
        {
            failure ??= ManagedBrowserHelperClientError.ChildExited;
            pending?.Interrupted.TrySetResult(failure.Value);
        }
        await Task.WhenAll(stdoutPump, stderrPump).ConfigureAwait(false);
        Task writing;
        lock (sync) writing = writeTask;
        await writing.ConfigureAwait(false);
        try { process.Dispose(); }
        catch (Exception) { Record(ManagedBrowserHelperDiagnostic.TransportFailure); }
        lease.Dispose();
    }

    private static ManagedBrowserResponse TerminalResponse(ManagedBrowserRequest request, ManagedBrowserHelperClientError error)
    {
        if (request.Action == ManagedBrowserAction.ObserveAccountIdentity &&
            error is ManagedBrowserHelperClientError.Cancelled or ManagedBrowserHelperClientError.TimedOut)
            return ManagedBrowserResponse.For(request, new ManagedBrowserIdentityResult(
                error == ManagedBrowserHelperClientError.Cancelled
                    ? ManagedBrowserIdentityResultKind.Cancelled : ManagedBrowserIdentityResultKind.TimedOut));
        throw Failure(error);
    }

    private static ManagedBrowserRequest ControlRequest(ManagedBrowserAction action, ManagedBrowserRequestPayload payload) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, action, payload);

    private static ManagedBrowserHelperClientException Failure(ManagedBrowserHelperClientError error) => new(error);

    private sealed class Pending(ManagedBrowserRequest request)
    {
        public ManagedBrowserRequest Request { get; } = request;
        public TaskCompletionSource<ManagedBrowserResponse> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ManagedBrowserHelperClientError> Interrupted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class OwnedProcess : IManagedBrowserHelperProcess
    {
        private readonly Process child;
        private Task? exitTask;
        public OwnedProcess(ProcessStartInfo info) => child = new Process { StartInfo = info };
        public bool HasStarted { get; private set; }
        public void Start()
        {
            if (!child.Start()) throw Failure(ManagedBrowserHelperClientError.LaunchFailed);
            HasStarted = true;
            exitTask = ObserveExitAsync();
        }
        public int Id => child.Id;
        public Stream StandardInput => child.StandardInput.BaseStream;
        public Stream StandardOutput => child.StandardOutput.BaseStream;
        public Stream StandardError => child.StandardError.BaseStream;
        public Task ExitTask => exitTask!;
        public void Kill() => child.Kill(entireProcessTree: false);
        public void Dispose() => child.Dispose();

        private async Task ObserveExitAsync()
        {
            // A transient OS wait failure is not proof of exit. Keep ownership and retry;
            // callers retain their independent operation/disposal deadlines.
            while (true)
            {
                try { await child.WaitForExitAsync().ConfigureAwait(false); return; }
                catch (Exception) { await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            }
        }
    }
}
