using System.IO;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

internal sealed class ManagedTransferReadinessResult
{
    internal ManagedTransferReadinessResult(
        ManagedBrowserTransferSubmissionReadinessResultKind kind,
        ManagedBrowserTransferSubmissionReadiness? readiness = null)
    {
        Kind = kind;
        Readiness = readiness;
    }

    internal ManagedBrowserTransferSubmissionReadinessResultKind Kind { get; }
    internal ManagedBrowserTransferSubmissionReadiness? Readiness { get; }
    public override string ToString() =>
        $"ManagedTransferReadinessResult {{ Kind = {Kind}, Evidence = [REDACTED] }}";
}

internal sealed class ManagedTransferSubmitResult
{
    internal ManagedTransferSubmitResult(
        ManagedBrowserTransferSubmitResultKind kind,
        ManagedBrowserTransferSubmitDispatchState dispatchState)
    {
        Kind = kind;
        DispatchState = dispatchState;
    }

    internal ManagedBrowserTransferSubmitResultKind Kind { get; }
    internal ManagedBrowserTransferSubmitDispatchState DispatchState { get; }
    internal bool IsSuccess => false;
    public override string ToString() =>
        $"ManagedTransferSubmitResult {{ Kind = {Kind}, DispatchState = {DispatchState} }}";
}

internal sealed class ManagedTransferOfficialOutcome
{
    internal ManagedTransferOfficialOutcome(
        ManagedBrowserTransferOutcomeKind kind,
        ManagedBrowserTransferSubmissionOutcomeResult? officialOutcome = null)
    {
        Kind = kind;
        OfficialOutcome = officialOutcome;
    }

    internal ManagedBrowserTransferOutcomeKind Kind { get; }
    internal ManagedBrowserTransferSubmissionOutcomeResult? OfficialOutcome { get; }
    public override string ToString() =>
        $"ManagedTransferOfficialOutcome {{ Kind = {Kind}, Evidence = [REDACTED] }}";
}

/// <summary>
/// A fixed-action helper boundary only. It owns no account lease, mutation permit, durable state or retry policy.
/// The one submit send is preserved as an attempt boundary and is never interpreted as transfer success.
/// </summary>
internal sealed class ManagedTransferSubmissionOperator
{
    private readonly Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send;

    internal ManagedTransferSubmissionOperator(ManagedBrowserHelperClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        send = client.SendAsync;
    }

    internal ManagedTransferSubmissionOperator(
        Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send) =>
        this.send = send ?? throw new ArgumentNullException(nameof(send));

    internal async Task<ManagedTransferReadinessResult> ObserveReadyAsync(
        Guid operationId, Guid accountId, long generation,
        ManagedBrowserTransferSubmissionContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateIdentity(operationId, accountId, generation);
            ArgumentNullException.ThrowIfNull(context);
            cancellationToken.ThrowIfCancellationRequested();
            var request = NewRequest(operationId, accountId, generation,
                ManagedBrowserAction.ObserveTransferSubmissionReady,
                new ManagedBrowserObserveTransferSubmissionReadyRequest(context));
            var response = await send(request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ManagedBrowserProtocol.ValidateResponseFor(request, response);
            if (response.Result is not ManagedBrowserTransferSubmissionReadinessResult result)
                return ReadinessDenied(ManagedBrowserTransferSubmissionReadinessResultKind.PageNotValidated);
            if (result.Kind != ManagedBrowserTransferSubmissionReadinessResultKind.Ready)
                return ReadinessDenied(result.Kind);
            if (result.Readiness is not { } readiness || !readiness.Context.EqualsEvidence(context))
                return ReadinessDenied(ManagedBrowserTransferSubmissionReadinessResultKind.PageNotValidated);
            return new(result.Kind, readiness);
        }
        catch (OperationCanceledException)
        {
            return ReadinessDenied(ManagedBrowserTransferSubmissionReadinessResultKind.Cancelled);
        }
        catch (TimeoutException)
        {
            return ReadinessDenied(ManagedBrowserTransferSubmissionReadinessResultKind.TimedOut);
        }
        catch (ManagedBrowserHelperClientException error)
        {
            return ReadinessDenied(error.Error switch
            {
                ManagedBrowserHelperClientError.Cancelled => ManagedBrowserTransferSubmissionReadinessResultKind.Cancelled,
                ManagedBrowserHelperClientError.TimedOut => ManagedBrowserTransferSubmissionReadinessResultKind.TimedOut,
                ManagedBrowserHelperClientError.ProtocolViolation => ManagedBrowserTransferSubmissionReadinessResultKind.PageNotValidated,
                _ => ManagedBrowserTransferSubmissionReadinessResultKind.BrowserUnavailable,
            });
        }
        catch (Exception error) when (error is IOException or ManagedBrowserProtocolException or ArgumentException)
        {
            return ReadinessDenied(error is IOException
                ? ManagedBrowserTransferSubmissionReadinessResultKind.BrowserUnavailable
                : ManagedBrowserTransferSubmissionReadinessResultKind.PageNotValidated);
        }
        catch (Exception)
        {
            return ReadinessDenied(ManagedBrowserTransferSubmissionReadinessResultKind.BrowserUnavailable);
        }
    }

    internal async Task<ManagedTransferSubmitResult> SubmitOnceAsync(
        Guid operationId, Guid accountId, long generation,
        ManagedBrowserTransferSubmissionReadiness readiness,
        CancellationToken cancellationToken = default)
    {
        var sendInvoked = false;
        try
        {
            ValidateIdentity(operationId, accountId, generation);
            ArgumentNullException.ThrowIfNull(readiness);
            cancellationToken.ThrowIfCancellationRequested();
            var request = NewRequest(operationId, accountId, generation,
                ManagedBrowserAction.SubmitTransferOnce,
                new ManagedBrowserSubmitTransferOnceRequest(
                    readiness.Context, readiness.ReadinessFingerprint));
            sendInvoked = true;
            var response = await send(request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ManagedBrowserProtocol.ValidateResponseFor(request, response);
            return response.Result is ManagedBrowserSubmitTransferOnceResult result
                ? new(result.Kind, result.DispatchState)
                : SubmitDenied(ManagedBrowserTransferSubmitResultKind.PageNotValidated,
                    ManagedBrowserTransferSubmitDispatchState.DispatchOutcomeUnknown);
        }
        catch (OperationCanceledException)
        {
            return SubmitDenied(ManagedBrowserTransferSubmitResultKind.Cancelled,
                sendInvoked ? ManagedBrowserTransferSubmitDispatchState.DispatchOutcomeUnknown :
                    ManagedBrowserTransferSubmitDispatchState.KnownNotDispatched);
        }
        catch (TimeoutException)
        {
            return SubmitDenied(ManagedBrowserTransferSubmitResultKind.TimedOut,
                sendInvoked ? ManagedBrowserTransferSubmitDispatchState.DispatchOutcomeUnknown :
                    ManagedBrowserTransferSubmitDispatchState.KnownNotDispatched);
        }
        catch (ManagedBrowserHelperClientException error)
        {
            return SubmitDenied(error.Error switch
            {
                ManagedBrowserHelperClientError.Cancelled => ManagedBrowserTransferSubmitResultKind.Cancelled,
                ManagedBrowserHelperClientError.TimedOut => ManagedBrowserTransferSubmitResultKind.TimedOut,
                ManagedBrowserHelperClientError.ProtocolViolation => ManagedBrowserTransferSubmitResultKind.PageNotValidated,
                _ => ManagedBrowserTransferSubmitResultKind.BrowserUnavailable,
            }, sendInvoked ? ManagedBrowserTransferSubmitDispatchState.DispatchOutcomeUnknown :
                ManagedBrowserTransferSubmitDispatchState.KnownNotDispatched);
        }
        catch (Exception error) when (error is IOException or ManagedBrowserProtocolException or ArgumentException or InvalidOperationException)
        {
            return SubmitDenied(error is IOException
                    ? ManagedBrowserTransferSubmitResultKind.BrowserUnavailable
                    : ManagedBrowserTransferSubmitResultKind.PageNotValidated,
                sendInvoked ? ManagedBrowserTransferSubmitDispatchState.DispatchOutcomeUnknown :
                    ManagedBrowserTransferSubmitDispatchState.KnownNotDispatched);
        }
        catch (Exception)
        {
            return SubmitDenied(ManagedBrowserTransferSubmitResultKind.BrowserUnavailable,
                sendInvoked ? ManagedBrowserTransferSubmitDispatchState.DispatchOutcomeUnknown :
                    ManagedBrowserTransferSubmitDispatchState.KnownNotDispatched);
        }
    }

    internal async Task<ManagedTransferOfficialOutcome> ObserveOutcomeAsync(
        Guid operationId, Guid accountId, long generation,
        ManagedBrowserTransferSubmissionReadiness readiness,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateIdentity(operationId, accountId, generation);
            ArgumentNullException.ThrowIfNull(readiness);
            cancellationToken.ThrowIfCancellationRequested();
            var context = readiness.Context;
            var request = NewRequest(operationId, accountId, generation,
                ManagedBrowserAction.ObserveTransferSubmissionOutcome,
                new ManagedBrowserObserveTransferSubmissionOutcomeRequest(
                    context, readiness.ReadinessFingerprint));
            var response = await send(request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ManagedBrowserProtocol.ValidateResponseFor(request, response);
            if (response.Result is not ManagedBrowserTransferSubmissionOutcomeResult result)
                return OutcomeDenied(ManagedBrowserTransferOutcomeKind.PageNotValidated);
            if (result.Kind != ManagedBrowserTransferOutcomeKind.AcceptedBound)
                return OutcomeDenied(result.Kind);
            if (!string.Equals(result.ExactTargetId, context.SelectedTargetExactId, StringComparison.Ordinal) ||
                !string.Equals(result.SourceExactId, context.SourceExactId, StringComparison.Ordinal))
                return OutcomeDenied(ManagedBrowserTransferOutcomeKind.PageNotValidated);
            return new(result.Kind, result);
        }
        catch (OperationCanceledException)
        {
            return OutcomeDenied(ManagedBrowserTransferOutcomeKind.Cancelled);
        }
        catch (TimeoutException)
        {
            return OutcomeDenied(ManagedBrowserTransferOutcomeKind.TimedOut);
        }
        catch (ManagedBrowserHelperClientException error)
        {
            return OutcomeDenied(error.Error switch
            {
                ManagedBrowserHelperClientError.Cancelled => ManagedBrowserTransferOutcomeKind.Cancelled,
                ManagedBrowserHelperClientError.TimedOut => ManagedBrowserTransferOutcomeKind.TimedOut,
                ManagedBrowserHelperClientError.ProtocolViolation => ManagedBrowserTransferOutcomeKind.PageNotValidated,
                _ => ManagedBrowserTransferOutcomeKind.BrowserUnavailable,
            });
        }
        catch (Exception error) when (error is IOException or ManagedBrowserProtocolException or ArgumentException)
        {
            return OutcomeDenied(error is IOException
                ? ManagedBrowserTransferOutcomeKind.BrowserUnavailable
                : ManagedBrowserTransferOutcomeKind.PageNotValidated);
        }
        catch (Exception)
        {
            return OutcomeDenied(ManagedBrowserTransferOutcomeKind.BrowserUnavailable);
        }
    }

    private static ManagedBrowserRequest NewRequest(
        Guid operationId, Guid accountId, long generation,
        ManagedBrowserAction action, ManagedBrowserRequestPayload payload) =>
        new(Guid.NewGuid(), operationId, accountId, generation, action, payload);

    private static void ValidateIdentity(Guid operationId, Guid accountId, long generation)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A bound operation is required.", nameof(operationId));
        if (accountId == Guid.Empty) throw new ArgumentException("A bound account is required.", nameof(accountId));
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
    }

    private static ManagedTransferReadinessResult ReadinessDenied(
        ManagedBrowserTransferSubmissionReadinessResultKind kind) => new(kind);
    private static ManagedTransferSubmitResult SubmitDenied(
        ManagedBrowserTransferSubmitResultKind kind,
        ManagedBrowserTransferSubmitDispatchState dispatchState) => new(kind, dispatchState);
    private static ManagedTransferOfficialOutcome OutcomeDenied(
        ManagedBrowserTransferOutcomeKind kind) => new(kind);
}
