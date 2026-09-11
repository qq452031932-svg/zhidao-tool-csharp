using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using System.Data.Common;
using System.Diagnostics;
using System.IO;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

internal sealed class ManagedTransferSourceLeasedReadResult
{
    internal ManagedTransferSourceLeasedReadResult(ManagedTransferSourceReadResult readResult, Task pendingSendsSettled)
    {
        ReadResult = readResult ?? throw new ArgumentNullException(nameof(readResult));
        PendingSendsSettled = pendingSendsSettled ?? throw new ArgumentNullException(nameof(pendingSendsSettled));
    }

    public ManagedTransferSourceReadResult ReadResult { get; }
    public Task PendingSendsSettled { get; }
    public override string ToString() =>
        $"ManagedTransferSourceLeasedReadResult {{ ReadResult = [REDACTED], PendingSendsSettled = {PendingSendsSettled.Status} }}";
}

/// <summary>Account-bound candidate reading only. The caller owns the helper and exact browser handle.</summary>
public sealed class ManagedTransferSourceSession
{
    private readonly ManagedBrowserLifecycleCoordinator lifecycle;
    private readonly ManagedChromiumProcessManager manager;
    private readonly Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send;
    private readonly Func<Guid, CancellationToken, Task<IDisposable>> acquireLease;

    public ManagedTransferSourceSession(ManagedBrowserLifecycleCoordinator lifecycle, ManagedChromiumProcessManager manager,
        ManagedBrowserHelperClient helper) : this(lifecycle, manager,
            helper is null ? throw new ArgumentNullException(nameof(helper)) : helper.SendAsync) { }

    internal ManagedTransferSourceSession(ManagedBrowserLifecycleCoordinator lifecycle, ManagedChromiumProcessManager manager,
        Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send,
        Func<Guid, CancellationToken, Task<IDisposable>>? acquireAccountLease = null)
    {
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
        this.send = send ?? throw new ArgumentNullException(nameof(send));
        acquireLease = acquireAccountLease ?? this.lifecycle.AcquireAccountLeaseAsync;
    }

    public async Task<ManagedTransferSourceReadResult> OpenAndReadAsync(Guid operationId, long generation,
        BaiduExecutionContext executionContext, ManagedChromiumSessionHandle exactHandle, string canonicalSourceUri,
        int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        (await OpenAndReadCoreAsync(operationId, generation, executionContext, exactHandle, canonicalSourceUri,
            timeoutMilliseconds, acquireAccountLease: true, cancellationToken).ConfigureAwait(false)).ReadResult;

    /// <summary>The caller must hold the account lease until <see cref="ManagedTransferSourceLeasedReadResult.PendingSendsSettled"/> completes.</summary>
    internal Task<ManagedTransferSourceLeasedReadResult> OpenAndReadUnderAccountLeaseAsync(Guid operationId, long generation,
        BaiduExecutionContext executionContext, ManagedChromiumSessionHandle exactHandle, string canonicalSourceUri,
        int timeoutMilliseconds, CancellationToken cancellationToken = default) =>
        OpenAndReadCoreAsync(operationId, generation, executionContext, exactHandle, canonicalSourceUri,
            timeoutMilliseconds, acquireAccountLease: false, cancellationToken);

    private async Task<ManagedTransferSourceLeasedReadResult> OpenAndReadCoreAsync(Guid operationId, long generation,
        BaiduExecutionContext executionContext, ManagedChromiumSessionHandle exactHandle, string canonicalSourceUri,
        int timeoutMilliseconds, bool acquireAccountLease, CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty || generation <= 0 || executionContext?.Engine != BaiduExecutionEngine.ManagedChromiumV1 ||
            executionContext.ManagedSession is not { } identity || exactHandle is null ||
            timeoutMilliseconds is < 1 or > ManagedBrowserProtocol.MaximumOperationTimeoutMilliseconds)
            return Settled(Denied(ManagedBrowserSourceListingResultKind.PageNotValidated));

        // Validate every public input before acquiring resources or invoking the helper.
        try { _ = Binding(timeoutMilliseconds); }
        catch (ArgumentException) { return Settled(Denied(ManagedBrowserSourceListingResultKind.PageNotValidated)); }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var elapsed = Stopwatch.StartNew();
        deadline.CancelAfter(timeoutMilliseconds);
        var token = deadline.Token;
        IDisposable? accountLease = null;
        var pendingSends = new List<Task>(2);
        var settlement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (acquireAccountLease)
            {
                RequireBudget();
                accountLease = await acquireLease(identity.AccountId, token).ConfigureAwait(false);
            }
            RequireBudget();
            var initial = await LoadSnapshotAsync(identity.AccountId, token).ConfigureAwait(false);
            RequireBudget();
            if (initial is null || !SameIdentity(initial.Session, identity) || !MatchesHandle(initial, exactHandle))
                return Result(Denied(ManagedBrowserSourceListingResultKind.AccountIdentityMismatch));

            var ready = await manager.ReconnectAsync(exactHandle, token).ConfigureAwait(false);
            RequireBudget();
            if (ready.Status != ManagedChromiumLaunchStatus.Ready || !ReferenceEquals(ready.Session, exactHandle))
                return Result(Denied(ManagedBrowserSourceListingResultKind.PageNotValidated));

            var open = new ManagedBrowserRequest(Guid.NewGuid(), operationId, identity.AccountId, generation,
                ManagedBrowserAction.OpenTransferSource, Binding(RemainingMilliseconds()));
            var response = await SendBoundedAsync(open, token).ConfigureAwait(false);
            RequireBudget();
            ManagedBrowserProtocol.ValidateResponseFor(open, response);
            if (response.Result is not ManagedBrowserTransferSourceResult stage)
                return Result(Denied(ManagedBrowserSourceListingResultKind.PageNotValidated));
            if (stage.Kind != ManagedBrowserTransferSourceResultKind.SourceOpened) return Result(Denied(Map(stage.Kind)));

            var verified = await manager.VerifyTransferSourceAsync(exactHandle, Binding(RemainingMilliseconds()), token).ConfigureAwait(false);
            RequireBudget();
            if (!verified) return Result(Denied(ManagedBrowserSourceListingResultKind.PageNotValidated));

            var reader = new ManagedTransferSourceReader(SendBoundedAsync);
            var result = await reader.ReadAsync(operationId, identity.AccountId, generation,
                new ManagedBrowserReadTransferSourceRequest(Binding(RemainingMilliseconds())), token).ConfigureAwait(false);
            RequireBudget();
            if (result.Kind != ManagedBrowserSourceListingResultKind.Observed || result.Observation is null) return Result(result);

            verified = await manager.VerifyTransferSourceAsync(exactHandle, Binding(RemainingMilliseconds()), token).ConfigureAwait(false);
            RequireBudget();
            if (!verified) return Result(Denied(ManagedBrowserSourceListingResultKind.PageNotValidated));
            var final = await LoadSnapshotAsync(identity.AccountId, token).ConfigureAwait(false);
            RequireBudget();
            if (final is null || !SameIdentity(final.Session, identity) || !MatchesHandle(final, exactHandle) ||
                initial.InstallationRevision != final.InstallationRevision || initial.SessionRevision != final.SessionRevision)
                return Result(Denied(ManagedBrowserSourceListingResultKind.AccountIdentityMismatch));
            RequireBudget();
            return Result(result);
        }
        catch (OperationCanceledException)
        {
            return Result(Denied(cancellationToken.IsCancellationRequested ? ManagedBrowserSourceListingResultKind.Cancelled :
                elapsed.Elapsed.TotalMilliseconds >= timeoutMilliseconds || deadline.IsCancellationRequested
                    ? ManagedBrowserSourceListingResultKind.TimedOut : ManagedBrowserSourceListingResultKind.Cancelled));
        }
        catch (TimeoutException) { return Result(Denied(cancellationToken.IsCancellationRequested
            ? ManagedBrowserSourceListingResultKind.Cancelled : ManagedBrowserSourceListingResultKind.TimedOut)); }
        catch (ManagedBrowserProtocolException) { return Result(Denied(ManagedBrowserSourceListingResultKind.PageNotValidated)); }
        catch (ManagedBrowserHelperClientException error)
        {
            return Result(Denied(cancellationToken.IsCancellationRequested ? ManagedBrowserSourceListingResultKind.Cancelled :
                elapsed.Elapsed.TotalMilliseconds >= timeoutMilliseconds ? ManagedBrowserSourceListingResultKind.TimedOut : error.Error switch
                {
                    ManagedBrowserHelperClientError.Cancelled => ManagedBrowserSourceListingResultKind.Cancelled,
                    ManagedBrowserHelperClientError.TimedOut => ManagedBrowserSourceListingResultKind.TimedOut,
                    ManagedBrowserHelperClientError.ProtocolViolation => ManagedBrowserSourceListingResultKind.PageNotValidated,
                    _ => ManagedBrowserSourceListingResultKind.BrowserUnavailable,
                }));
        }
        catch (Exception error) when (error is IOException or DbException or UnauthorizedAccessException or ObjectDisposedException)
        { return Result(Denied(ManagedBrowserSourceListingResultKind.BrowserUnavailable)); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { return Result(Denied(ManagedBrowserSourceListingResultKind.PageNotValidated)); }
        finally
        {
            // An uncooperative send may still navigate after the caller's deadline. Keep the account fenced
            // until it settles, observing late faults without disposing the caller's helper or browser.
            _ = CompleteSendSettlementAsync(pendingSends.ToArray(), settlement);
            if (accountLease is not null) _ = ReleaseAfterSettlementAsync(accountLease, settlement.Task);
        }

        ManagedTransferSourceLeasedReadResult Result(ManagedTransferSourceReadResult result) => new(result, settlement.Task);
        ManagedBrowserOpenTransferSourceRequest Binding(int milliseconds) => new(exactHandle.BrowserId, exactHandle.TargetId,
            exactHandle.Endpoint, milliseconds, canonicalSourceUri, identity.StableBaiduUserId);
        void RequireBudget()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (elapsed.Elapsed.TotalMilliseconds >= timeoutMilliseconds) throw new TimeoutException();
            token.ThrowIfCancellationRequested();
        }
        int RemainingMilliseconds()
        {
            RequireBudget();
            return Math.Max(1, (int)Math.Ceiling(timeoutMilliseconds - elapsed.Elapsed.TotalMilliseconds));
        }
        async Task<ManagedBrowserResponse> SendBoundedAsync(ManagedBrowserRequest request, CancellationToken sendToken)
        {
            RequireBudget();
            var pending = send(request, sendToken);
            pendingSends.Add(pending);
            var reply = await pending.WaitAsync(sendToken).ConfigureAwait(false);
            RequireBudget(); // A synchronous late return can bypass WaitAsync's deadline.
            return reply;
        }
    }

    private static ManagedTransferSourceLeasedReadResult Settled(ManagedTransferSourceReadResult result) =>
        new(result, Task.CompletedTask);

    private async Task<ManagedBrowserLifecycleSession?> LoadSnapshotAsync(Guid accountId, CancellationToken token)
    {
        try { return await lifecycle.LoadPersistedAsync(accountId, token).ConfigureAwait(false); }
        catch (InvalidOperationException) { return null; } // Missing/ambiguous durable identity is never inferred.
    }

    private static bool SameIdentity(ManagedBrowserSessionReference left, ManagedBrowserSessionReference right) =>
        left.SessionId == right.SessionId && left.InstallationId == right.InstallationId && left.AccountId == right.AccountId &&
        left.BrowserFamily == right.BrowserFamily && string.Equals(left.StableBaiduUserId, right.StableBaiduUserId, StringComparison.Ordinal) &&
        string.Equals(left.ProfileId, right.ProfileId, StringComparison.Ordinal) &&
        string.Equals(left.ProfileOwnershipId, right.ProfileOwnershipId, StringComparison.Ordinal);

    private static bool MatchesHandle(ManagedBrowserLifecycleSession snapshot, ManagedChromiumSessionHandle handle) =>
        handle.Installation.InstallationId == snapshot.InstallationId && handle.Installation.BrowserFamily == snapshot.BrowserFamily &&
        handle.Profile.AccountId == snapshot.AccountId && handle.Profile.Family == snapshot.BrowserFamily &&
        Guid.TryParse(snapshot.ProfileId, out var profileId) && handle.Profile.ProfileId == profileId &&
        Guid.TryParse(snapshot.ProfileOwnershipId, out var ownershipId) && handle.Profile.OwnershipId == ownershipId &&
        string.Equals(handle.Profile.CanonicalSourcePath, snapshot.CanonicalProfilePath, StringComparison.OrdinalIgnoreCase);

    private static async Task CompleteSendSettlementAsync(Task[] pending, TaskCompletionSource settlement)
    {
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch (Exception) { /* Late completion never publishes a candidate or sensitive exception. */ }
        finally { settlement.TrySetResult(); }
    }

    private static async Task ReleaseAfterSettlementAsync(IDisposable lease, Task settlement)
    {
        try { await settlement.ConfigureAwait(false); }
        catch (Exception) { /* Settlement failures are observed without exposing private details. */ }
        try { lease.Dispose(); }
        catch (Exception) { /* Disposal failures are observed; the fire-and-forget release task never faults. */ }
    }

    private static ManagedBrowserSourceListingResultKind Map(ManagedBrowserTransferSourceResultKind kind) => kind switch
    {
        ManagedBrowserTransferSourceResultKind.ExtractionRequired => ManagedBrowserSourceListingResultKind.ExtractionRequired,
        ManagedBrowserTransferSourceResultKind.LoginRequired => ManagedBrowserSourceListingResultKind.LoginRequired,
        ManagedBrowserTransferSourceResultKind.VerificationRequired => ManagedBrowserSourceListingResultKind.VerificationRequired,
        ManagedBrowserTransferSourceResultKind.BrowserUnavailable => ManagedBrowserSourceListingResultKind.BrowserUnavailable,
        ManagedBrowserTransferSourceResultKind.AccountIdentityMismatch => ManagedBrowserSourceListingResultKind.AccountIdentityMismatch,
        ManagedBrowserTransferSourceResultKind.TimedOut => ManagedBrowserSourceListingResultKind.TimedOut,
        ManagedBrowserTransferSourceResultKind.Cancelled => ManagedBrowserSourceListingResultKind.Cancelled,
        _ => ManagedBrowserSourceListingResultKind.PageNotValidated,
    };
    private static ManagedTransferSourceReadResult Denied(ManagedBrowserSourceListingResultKind kind) => new(kind);
}
