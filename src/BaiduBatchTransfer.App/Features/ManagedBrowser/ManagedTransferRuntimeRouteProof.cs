using System.IO;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

/// <summary>
/// Production read-only route proof. Static request identity is validated before this seam is called;
/// runtime drift fails closed before any helper or mutation permit can be requested.
/// </summary>
internal sealed class ManagedTransferRuntimeRouteProof : IManagedTransferRouteProof
{
    private readonly ManagedChromiumProcessManager manager;
    private readonly Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send;

    internal ManagedTransferRuntimeRouteProof(
        ManagedChromiumProcessManager manager,
        Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send)
    {
        this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
        this.send = send ?? throw new ArgumentNullException(nameof(send));
    }

    public async Task<bool> VerifyAsync(
        ManagedTransferRouteProofRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            if (!await manager.VerifyTransferRouteAsync(
                request.ExactSession,
                request.Binding,
                request.ExpectedRoute,
                cancellationToken).ConfigureAwait(false))
                return false;
            return await VerifyFixedHelperObservationAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or TimeoutException or ObjectDisposedException or
            ManagedBrowserProtocolException or ManagedBrowserHelperClientException or ArgumentException)
        {
            return false;
        }
    }

    private async Task<bool> VerifyFixedHelperObservationAsync(
        ManagedTransferRouteProofRequest proof,
        CancellationToken cancellationToken)
    {
        var identity = proof.RequestIdentity;
        ManagedBrowserRequest request;
        if (proof.ExpectedRoute.State == ManagedTransferRouteState.OriginalSource)
        {
            request = new ManagedBrowserRequest(Guid.NewGuid(), identity.OperationId, identity.AccountId,
                identity.Generation, ManagedBrowserAction.ReadTransferSource,
                new ManagedBrowserReadTransferSourceRequest(
                    proof.Binding.BrowserId, proof.Binding.TargetId, proof.Binding.DevToolsEndpoint,
                    proof.Binding.TimeoutMilliseconds, proof.Binding.SourceUri,
                    proof.Binding.ExpectedBaiduUserId));
        }
        else
        {
            var route = proof.ExpectedRoute;
            var payload = route.State == ManagedTransferRouteState.OfficialTargetRoot
                ? new ManagedBrowserReadTransferTargetDirectoryRequest(proof.Binding, null, null, null)
                : new ManagedBrowserReadTransferTargetDirectoryRequest(proof.Binding,
                    route.DirectoryExactId, route.CanonicalPath, route.AncestorExactIds);
            request = new ManagedBrowserRequest(Guid.NewGuid(), identity.OperationId, identity.AccountId,
                identity.Generation, ManagedBrowserAction.ReadTransferTargetDirectory, payload);
        }

        var response = await send(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ManagedBrowserProtocol.ValidateResponseFor(request, response);
        return request.Payload switch
        {
            ManagedBrowserReadTransferSourceRequest =>
                response.Result is ManagedBrowserSourceListingResult
                {
                    Kind: ManagedBrowserSourceListingResultKind.Observed,
                    Snapshot: { } source,
                } && SameSource(source, proof.Binding.ExpectedSource, proof.Binding.SourceUri),
            ManagedBrowserReadTransferTargetDirectoryRequest =>
                response.Result is ManagedBrowserReadTransferTargetDirectoryResult
                {
                    Kind: ManagedBrowserReadTransferTargetDirectoryResultKind.Observed,
                    Snapshot: not null,
                },
            _ => false,
        };
    }

    private static bool SameSource(
        ManagedBrowserSourceSnapshot observed,
        ManagedBrowserSourceSnapshot expected,
        string sourceUri) =>
        string.Equals(observed.BaiduUserId, expected.BaiduUserId, StringComparison.Ordinal) &&
        string.Equals(observed.GenerationToken, expected.GenerationToken, StringComparison.Ordinal) &&
        observed.TotalCount == expected.TotalCount &&
        string.Equals(observed.ObservationFingerprint, expected.ObservationFingerprint, StringComparison.Ordinal) &&
        string.Equals(observed.ComputeFingerprint(sourceUri), observed.ObservationFingerprint, StringComparison.Ordinal) &&
        observed.Items.Length == expected.Items.Length &&
        observed.Items.Zip(expected.Items, (left, right) =>
            string.Equals(left.ExactId, right.ExactId, StringComparison.Ordinal) &&
            left.Kind == right.Kind &&
            string.Equals(left.ServerName, right.ServerName, StringComparison.Ordinal)).All(equal => equal);
}
