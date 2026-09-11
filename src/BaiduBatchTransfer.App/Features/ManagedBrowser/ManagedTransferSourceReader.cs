using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.Core.Domain;
using System.Collections.Immutable;
using System.IO;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

public sealed class ManagedTransferSourceReadResult
{
    internal ManagedTransferSourceReadResult(ManagedBrowserSourceListingResultKind kind, BaiduTransferCandidateObservation? observation = null)
    { Kind = kind; Observation = observation; }
    public ManagedBrowserSourceListingResultKind Kind { get; }
    public BaiduTransferCandidateObservation? Observation { get; }
    public override string ToString() => $"ManagedTransferSourceReadResult {{ Kind = {Kind}, Observation = [REDACTED] }}";
}

/// <summary>Candidate projection only. Caller retains helper ownership; no production/session gates are bypassed.</summary>
public sealed class ManagedTransferSourceReader
{
    private readonly Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send;
    public ManagedTransferSourceReader(ManagedBrowserHelperClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        send = client.SendAsync;
    }
    internal ManagedTransferSourceReader(Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send) =>
        this.send = send ?? throw new ArgumentNullException(nameof(send));

    public async Task<ManagedTransferSourceReadResult> ReadAsync(Guid operationId, Guid accountId, long generation,
        ManagedBrowserReadTransferSourceRequest payload, CancellationToken cancellationToken)
    {
        var request = new ManagedBrowserRequest(Guid.NewGuid(), operationId, accountId, generation,
            ManagedBrowserAction.ReadTransferSource, payload);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await send(request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ManagedBrowserProtocol.ValidateResponseFor(request, response);
            if (response.Result is not ManagedBrowserSourceListingResult result)
                return Denied(ManagedBrowserSourceListingResultKind.PageNotValidated);
            if (result.Snapshot is not { } snapshot) return Denied(result.Kind);
            if (!string.Equals(snapshot.BaiduUserId, payload.Binding.ExpectedBaiduUserId, StringComparison.Ordinal))
                return Denied(ManagedBrowserSourceListingResultKind.AccountIdentityMismatch);
            if (!string.Equals(snapshot.ComputeFingerprint(payload.Binding.SourceUri), snapshot.ObservationFingerprint, StringComparison.Ordinal))
                return Denied(ManagedBrowserSourceListingResultKind.PageNotValidated);
            var items = snapshot.Items.Select(item => new BaiduTransferCandidateItem(item.ExactId,
                item.Kind == ManagedBrowserSourceItemKind.Folder ? RemoteResourceKind.Folder : RemoteResourceKind.File,
                item.ServerName)).ToImmutableArray();
            var observation = new BaiduTransferCandidateObservation(BaiduCandidatePageState.ExternalShare,
                snapshot.BaiduUserId, null, null, [], items, snapshot.TotalCount, true, true, true,
                snapshot.GenerationToken, snapshot.ObservationFingerprint);
            cancellationToken.ThrowIfCancellationRequested();
            return new ManagedTransferSourceReadResult(ManagedBrowserSourceListingResultKind.Observed, observation);
        }
        catch (OperationCanceledException) { return Denied(ManagedBrowserSourceListingResultKind.Cancelled); }
        catch (TimeoutException) { return Denied(ManagedBrowserSourceListingResultKind.TimedOut); }
        catch (ManagedBrowserProtocolException) { return Denied(ManagedBrowserSourceListingResultKind.PageNotValidated); }
        catch (ManagedBrowserHelperClientException error)
        {
            return Denied(error.Error switch
            {
                ManagedBrowserHelperClientError.Cancelled => ManagedBrowserSourceListingResultKind.Cancelled,
                ManagedBrowserHelperClientError.TimedOut => ManagedBrowserSourceListingResultKind.TimedOut,
                ManagedBrowserHelperClientError.ProtocolViolation => ManagedBrowserSourceListingResultKind.PageNotValidated,
                _ => ManagedBrowserSourceListingResultKind.BrowserUnavailable,
            });
        }
        catch (IOException) { return Denied(ManagedBrowserSourceListingResultKind.BrowserUnavailable); }
        catch (ArgumentException) { return Denied(ManagedBrowserSourceListingResultKind.PageNotValidated); }
    }

    private static ManagedTransferSourceReadResult Denied(ManagedBrowserSourceListingResultKind kind) => new(kind);
}
