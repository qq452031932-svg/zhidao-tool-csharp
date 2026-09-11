using System.IO;
using System.Security.Cryptography;
using System.Text;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

public sealed class ManagedTransferTargetInventoryReadResult
{
    internal ManagedTransferTargetInventoryReadResult(
        ManagedBrowserReadTransferTargetDirectoryResultKind kind,
        ManagedTransferTargetInventoryEvidence? evidence = null)
    {
        if ((kind == ManagedBrowserReadTransferTargetDirectoryResultKind.Observed) != (evidence is not null))
            throw new ArgumentException("Only an observed result may carry target evidence.", nameof(evidence));
        Kind = kind;
        Evidence = evidence;
    }

    public ManagedBrowserReadTransferTargetDirectoryResultKind Kind { get; }
    public ManagedTransferTargetInventoryEvidence? Evidence { get; }

    public override string ToString() =>
        $"ManagedTransferTargetInventoryReadResult {{ Kind = {Kind}, Evidence = [REDACTED] }}";
}

/// <summary>
/// Retains the complete managed wire observation beside its adapter view and smaller durable Core projection.
/// Kind, name and root-inclusive ancestry are intentionally not copied into durable evidence.
/// </summary>
public sealed class ManagedTransferTargetInventoryEvidence
{
    internal ManagedTransferTargetInventoryEvidence(
        ManagedBrowserTargetInventorySnapshot wireSnapshot,
        TransferDirectoryListing allItems,
        TransferTargetPreparationEvidence durableEvidence)
    {
        WireSnapshot = wireSnapshot ?? throw new ArgumentNullException(nameof(wireSnapshot));
        AllItems = allItems ?? throw new ArgumentNullException(nameof(allItems));
        DurableEvidence = durableEvidence ?? throw new ArgumentNullException(nameof(durableEvidence));
    }

    public ManagedBrowserTargetInventorySnapshot WireSnapshot { get; }
    public TransferDirectoryListing AllItems { get; }
    public TransferTargetPreparationEvidence DurableEvidence { get; }

    public override string ToString() =>
        $"ManagedTransferTargetInventoryEvidence {{ ItemCount = {DurableEvidence.TotalCount}, Values = [REDACTED] }}";
}

/// <summary>
/// Reads one fixed all-item target action and projects only wire-verified evidence. It owns no browser navigation,
/// mutation permit, mutation record or persistence boundary.
/// </summary>
public sealed class ManagedTransferTargetInventoryReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send;

    public ManagedTransferTargetInventoryReader(ManagedBrowserHelperClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        send = client.SendAsync;
    }

    internal ManagedTransferTargetInventoryReader(
        Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send) =>
        this.send = send ?? throw new ArgumentNullException(nameof(send));

    public async Task<ManagedTransferTargetInventoryReadResult> ReadAsync(
        Guid operationId,
        Guid accountId,
        long generation,
        ManagedBrowserReadTransferTargetDirectoryRequest payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var request = new ManagedBrowserRequest(Guid.NewGuid(), operationId, accountId, generation,
            ManagedBrowserAction.ReadTransferTargetDirectory, payload);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await send(request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ManagedBrowserProtocol.ValidateResponseFor(request, response);
            if (response.Result is not ManagedBrowserReadTransferTargetDirectoryResult result)
                return Denied(ManagedBrowserReadTransferTargetDirectoryResultKind.PageNotValidated);
            if (result.Snapshot is not { } snapshot)
                return Denied(result.Kind);
            return new(ManagedBrowserReadTransferTargetDirectoryResultKind.Observed, Project(snapshot));
        }
        catch (OperationCanceledException)
        {
            return Denied(ManagedBrowserReadTransferTargetDirectoryResultKind.Cancelled);
        }
        catch (TimeoutException)
        {
            return Denied(ManagedBrowserReadTransferTargetDirectoryResultKind.TimedOut);
        }
        catch (ManagedBrowserProtocolException)
        {
            return Denied(ManagedBrowserReadTransferTargetDirectoryResultKind.PageNotValidated);
        }
        catch (ManagedBrowserHelperClientException error)
        {
            return Denied(error.Error switch
            {
                ManagedBrowserHelperClientError.Cancelled =>
                    ManagedBrowserReadTransferTargetDirectoryResultKind.Cancelled,
                ManagedBrowserHelperClientError.TimedOut =>
                    ManagedBrowserReadTransferTargetDirectoryResultKind.TimedOut,
                ManagedBrowserHelperClientError.ProtocolViolation =>
                    ManagedBrowserReadTransferTargetDirectoryResultKind.PageNotValidated,
                _ => ManagedBrowserReadTransferTargetDirectoryResultKind.BrowserUnavailable,
            });
        }
        catch (IOException)
        {
            return Denied(ManagedBrowserReadTransferTargetDirectoryResultKind.BrowserUnavailable);
        }
        catch (ArgumentException)
        {
            return Denied(ManagedBrowserReadTransferTargetDirectoryResultKind.PageNotValidated);
        }
    }

    internal static ManagedTransferTargetInventoryEvidence Project(
        ManagedBrowserTargetInventorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Scope != ManagedBrowserTargetInventoryScope.AllItems)
            throw new ArgumentOutOfRangeException(nameof(snapshot));
        if (!snapshot.ReachedEnd || !snapshot.IsStable || snapshot.TotalCount != snapshot.Items.Count)
            throw new ArgumentException("Complete stable all-item target evidence is required.", nameof(snapshot));
        if (snapshot.Items.Any(item => item is null || !Enum.IsDefined(item.Kind)) ||
            snapshot.Items.Select(item => item.ExactId).Distinct(StringComparer.Ordinal).Count() != snapshot.Items.Count)
            throw new ArgumentException("Exact unique target items are required.", nameof(snapshot));
        ManagedBrowserTargetInventoryValidation.ValidateDirectoryIdentity(
            snapshot.DirectoryExactId, snapshot.CanonicalPath, snapshot.AncestorExactIds);

        var recomputedWireFingerprint = snapshot.ComputeFingerprint();
        if (!FixedTimeEquals(recomputedWireFingerprint, snapshot.ObservationFingerprint))
            throw new ArgumentException("The target wire fingerprint is invalid.", nameof(snapshot));

        var baseline = WebAutomationBaseline.Create(snapshot.Items.Select(item => item.ExactId));
        var targetPath = new RemotePath(snapshot.CanonicalPath);
        var durableScope = TransferTargetInventoryScope.AllItems;
        var durableFingerprint = TransferTargetPreparationEvidence.ComputeObservationFingerprint(
            snapshot.DirectoryExactId, targetPath, snapshot.TotalCount, snapshot.ReachedEnd,
            snapshot.IsStable, snapshot.GenerationToken, snapshot.BaiduUserId, durableScope, baseline);
        var durable = new TransferTargetPreparationEvidence(
            snapshot.DirectoryExactId, targetPath, snapshot.TotalCount, snapshot.ReachedEnd,
            snapshot.IsStable, snapshot.GenerationToken, snapshot.BaiduUserId, durableScope,
            baseline, durableFingerprint);

        var adapterItems = snapshot.Items.Select(item => new TransferRemoteItem(
            new RemoteItemIdentity(item.ExactId, item.ServerName),
            MapItemKind(item.Kind),
            item.ServerName)).ToArray();
        var allItems = TransferDirectoryListing.Exact(
            new RemoteDirectory(snapshot.DirectoryExactId, targetPath),
            snapshot.AncestorExactIds.Skip(1).ToArray(), adapterItems, snapshot.TotalCount,
            snapshot.ReachedEnd, snapshot.IsStable, snapshot.GenerationToken, snapshot.BaiduUserId,
            snapshot.ObservationFingerprint, MapScope(durable.Scope));
        return new ManagedTransferTargetInventoryEvidence(snapshot, allItems, durable);
    }

    internal static TransferDirectoryListingScope MapScope(TransferTargetInventoryScope scope) => scope switch
    {
        TransferTargetInventoryScope.AllItems => TransferDirectoryListingScope.AllItems,
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    internal static TransferRemoteItemKind MapItemKind(
        ManagedBrowserTargetInventoryItemKind kind) => kind switch
    {
        ManagedBrowserTargetInventoryItemKind.File => TransferRemoteItemKind.File,
        ManagedBrowserTargetInventoryItemKind.Folder => TransferRemoteItemKind.Folder,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static bool FixedTimeEquals(string left, string right)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                StrictUtf8.GetBytes(left), StrictUtf8.GetBytes(right));
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static ManagedTransferTargetInventoryReadResult Denied(
        ManagedBrowserReadTransferTargetDirectoryResultKind kind) => new(kind);
}
