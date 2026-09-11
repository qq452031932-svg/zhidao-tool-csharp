using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Domain;
using System.Collections.Immutable;
using System.IO;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

public sealed class ManagedTransferDirectoryReadResult
{
    internal ManagedTransferDirectoryReadResult(
        ManagedBrowserDirectoryListingResultKind kind,
        ManagedTransferDirectoryEvidence? evidence = null)
    {
        if ((kind == ManagedBrowserDirectoryListingResultKind.Observed) != (evidence is not null))
            throw new ArgumentException("Only observed results carry directory evidence.", nameof(evidence));
        Kind = kind;
        Evidence = evidence;
    }
    public ManagedBrowserDirectoryListingResultKind Kind { get; }
    /// <summary>Immutable wire-validated snapshot and its root-inclusive proof.</summary>
    public ManagedTransferDirectoryEvidence? Evidence { get; }
    /// <summary>Legacy picker-only folder projection; never a destination inventory baseline.</summary>
    public TransferDirectoryListing? FolderCandidates => Evidence?.FolderCandidates;
    public BaiduTransferCandidateObservation? Observation => Evidence?.Observation;
    public override string ToString() => $"ManagedTransferDirectoryReadResult {{ Kind = {Kind}, Observation = [REDACTED] }}";
}

/// <summary>Pairs wire-validated root-inclusive directory evidence with its legacy folder-only projection.</summary>
public sealed class ManagedTransferDirectoryEvidence
{
    private ManagedTransferDirectoryEvidence(ManagedBrowserDirectorySnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        var depth = ManagedBrowserDirectorySnapshot.ValidateCanonicalPath(snapshot.CanonicalPath);
        if (snapshot.AncestorExactIds.Length != depth + 1 ||
            snapshot.AncestorExactIds.IsDefaultOrEmpty ||
            !string.Equals(snapshot.AncestorExactIds[^1], snapshot.ParentExactId, StringComparison.Ordinal) ||
            snapshot.AncestorExactIds.Distinct(StringComparer.Ordinal).Count() != snapshot.AncestorExactIds.Length ||
            snapshot.Items.Any(item => item is null ||
                item.Kind != ManagedBrowserSourceItemKind.Folder ||
                string.IsNullOrWhiteSpace(item.ServerName)))
            throw new ArgumentException("Complete stable root-inclusive folder evidence is required.", nameof(snapshot));

        RootExactId = snapshot.AncestorExactIds[0];
        RootInclusiveAncestorExactIds = snapshot.AncestorExactIds;
        var legacyAncestors = snapshot.AncestorExactIds.Skip(1).ToImmutableArray();
        var listingItems = snapshot.Items.Select(item => new TransferRemoteItem(
            new RemoteItemIdentity(item.ExactId, item.ServerName),
            TransferRemoteItemKind.Folder, item.ServerName)).ToArray();
        FolderCandidates = TransferDirectoryListing.Exact(
            new RemoteDirectory(snapshot.ParentExactId, new RemotePath(snapshot.CanonicalPath)),
            legacyAncestors, listingItems, snapshot.TotalCount, true, true,
            snapshot.GenerationToken, snapshot.BaiduUserId, snapshot.ObservationFingerprint,
            TransferDirectoryListingScope.FolderCandidates);
        var observationItems = snapshot.Items.Select(item => new BaiduTransferCandidateItem(
            item.ExactId, RemoteResourceKind.Folder, item.ServerName)).ToImmutableArray();
        Observation = new BaiduTransferCandidateObservation(
            BaiduCandidatePageState.DestinationPicker, snapshot.BaiduUserId, snapshot.ParentExactId,
            new RemotePath(snapshot.CanonicalPath), legacyAncestors, observationItems, snapshot.TotalCount,
            true, true, true, snapshot.GenerationToken,
            snapshot.ObservationFingerprint);
    }

    internal static ManagedTransferDirectoryEvidence CreateFromReaderValidated(
        ManagedBrowserDirectorySnapshot snapshot) => new(snapshot);

    public ManagedBrowserDirectorySnapshot Snapshot { get; }
    public string RootExactId { get; }
    public ImmutableArray<string> RootInclusiveAncestorExactIds { get; }
    /// <summary>Explicit picker-only directory projection; never an all-item destination inventory.</summary>
    public TransferDirectoryListing FolderCandidates { get; }
    /// <summary>Compatibility view for existing picker callers.</summary>
    public BaiduTransferCandidateObservation Observation { get; }
}

/// <summary>Projects picker-folder candidates only, never saved-file inventory. Caller owns helper and page proofs.</summary>
public sealed class ManagedTransferDirectoryReader
{
    private readonly Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send;
    public ManagedTransferDirectoryReader(ManagedBrowserHelperClient client)
    {
        ArgumentNullException.ThrowIfNull(client); send = client.SendAsync;
    }
    internal ManagedTransferDirectoryReader(Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send) =>
        this.send = send ?? throw new ArgumentNullException(nameof(send));

    public async Task<ManagedTransferDirectoryReadResult> ReadAsync(Guid operationId, Guid accountId, long generation,
        ManagedBrowserReadTransferDirectoryRequest payload, CancellationToken cancellationToken)
    {
        var request = new ManagedBrowserRequest(Guid.NewGuid(), operationId, accountId, generation, ManagedBrowserAction.ReadTransferDirectory, payload);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await send(request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ManagedBrowserProtocol.ValidateResponseFor(request, response);
            if (response.Result is not ManagedBrowserDirectoryListingResult result) return Denied(ManagedBrowserDirectoryListingResultKind.PageNotValidated);
            if (result.Snapshot is not { } snapshot) return Denied(result.Kind);
            if (!string.Equals(snapshot.BaiduUserId, payload.Binding.ExpectedBaiduUserId, StringComparison.Ordinal)) return Denied(ManagedBrowserDirectoryListingResultKind.AccountIdentityMismatch);
            if ((payload.ExpectedParentExactId is not null && (snapshot.ParentExactId != payload.ExpectedParentExactId || snapshot.CanonicalPath != payload.ExpectedCanonicalPath)) ||
                snapshot.ComputeFingerprint(payload.Binding.SourceUri) != snapshot.ObservationFingerprint) return Denied(ManagedBrowserDirectoryListingResultKind.PageNotValidated);
            var evidence = ManagedTransferDirectoryEvidence.CreateFromReaderValidated(snapshot);
            cancellationToken.ThrowIfCancellationRequested();
            return new(ManagedBrowserDirectoryListingResultKind.Observed, evidence);
        }
        catch (OperationCanceledException) { return Denied(ManagedBrowserDirectoryListingResultKind.Cancelled); }
        catch (TimeoutException) { return Denied(ManagedBrowserDirectoryListingResultKind.TimedOut); }
        catch (ManagedBrowserProtocolException) { return Denied(ManagedBrowserDirectoryListingResultKind.PageNotValidated); }
        catch (ManagedBrowserHelperClientException error)
        {
            return Denied(error.Error switch
            {
                ManagedBrowserHelperClientError.Cancelled => ManagedBrowserDirectoryListingResultKind.Cancelled,
                ManagedBrowserHelperClientError.TimedOut => ManagedBrowserDirectoryListingResultKind.TimedOut,
                ManagedBrowserHelperClientError.ProtocolViolation => ManagedBrowserDirectoryListingResultKind.PageNotValidated,
                _ => ManagedBrowserDirectoryListingResultKind.BrowserUnavailable,
            });
        }
        catch (IOException) { return Denied(ManagedBrowserDirectoryListingResultKind.BrowserUnavailable); }
        catch (ArgumentException) { return Denied(ManagedBrowserDirectoryListingResultKind.PageNotValidated); }
    }
    private static ManagedTransferDirectoryReadResult Denied(ManagedBrowserDirectoryListingResultKind kind) => new(kind);
}
