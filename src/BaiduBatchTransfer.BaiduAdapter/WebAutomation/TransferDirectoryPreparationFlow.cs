using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.WebAutomation;

public sealed record TransferDirectoryPreparationRequest(
    Guid OperationId,
    Guid BatchId,
    Guid AccountId,
    int Generation,
    ShareReference SourceShare,
    RemotePath TargetPath,
    AdapterCapabilityVersion Capability)
{
    public TransferDirectoryPreparationRequest Validate()
    {
        if (OperationId == Guid.Empty) throw new ArgumentException("An operation is required.", nameof(OperationId));
        if (BatchId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(BatchId));
        if (AccountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(AccountId));
        if (Generation < 1) throw new ArgumentOutOfRangeException(nameof(Generation));
        ArgumentNullException.ThrowIfNull(SourceShare);
        ArgumentNullException.ThrowIfNull(TargetPath);
        ArgumentNullException.ThrowIfNull(Capability);
        return this;
    }

    public override string ToString() =>
        "TransferDirectoryPreparationRequest { Identity = [REDACTED], Values = [REDACTED] }";
}

public interface ITransferDirectoryPreparationSession
{
    Task OpenSourceAsync(ShareReference share, CancellationToken cancellationToken);
    Task EnterExtractionCodeAsync(string extractionCode, CancellationToken cancellationToken);
    Task<TransferSourceListing> ReadCompleteSourceAsync(CancellationToken cancellationToken);
    Task<TransferDirectoryListing> OpenDestinationPickerAsync(CancellationToken cancellationToken);
    Task<TransferDirectoryListing> OpenVerifiedRootAsync(CancellationToken cancellationToken);
    Task<TransferDirectoryListing> EnterDirectoryAsync(
        RemoteDirectory directory,
        CancellationToken cancellationToken);
    Task BeginCreateDirectoryAsync(string segment, CancellationToken cancellationToken);
    Task ConfirmDirectoryCreationAsync(CancellationToken cancellationToken);
    Task<TransferDirectoryListing> ReadCurrentDirectoryAsync(CancellationToken cancellationToken);
    Task CancelDestinationPickerAsync(CancellationToken cancellationToken);
    Task<TransferDirectoryListing> ReadExactParentForRecoveryAsync(
        RemoteDirectory parent,
        IReadOnlyList<string> expectedAncestors,
        TransferDirectoryRecoveryAuthorization authorization,
        CancellationToken cancellationToken);
}

public sealed record TransferDirectoryRecoveryAuthorizationRequest(
    Guid BatchId,
    int CurrentGeneration,
    TransferDirectoryMutationRecord Mutation);

public sealed record TransferDirectoryRecoveryAuthorization(
    Guid OperationId,
    Guid BatchId,
    Guid AccountId,
    int OriginGeneration,
    long OwningAttemptRevision,
    string ParentExactId,
    RemotePath ParentPath,
    RemotePath CanonicalChildPath,
    string BaiduUserId,
    AdapterCapabilityVersion Capability,
    DateTimeOffset ExpiresAtUtc)
{
    public override string ToString() =>
        "TransferDirectoryRecoveryAuthorization { Identity = [REDACTED], Values = [REDACTED] }";
}

public interface ITransferDirectoryPreparationEvidenceStore
{
    Task<WebAutomationAttemptRecord?> GetAttemptAsync(
        TransferDirectoryPreparationRequest request,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<TransferDirectoryMutationRecord>> GetUnresolvedMutationsAsync(
        Guid operationId,
        Guid accountId,
        CancellationToken cancellationToken);
    Task<TransferDirectoryRecoveryAuthorization?> AuthorizeRecoveryAsync(
        TransferDirectoryRecoveryAuthorizationRequest request,
        CancellationToken cancellationToken);
    Task<bool> PersistSourceEvidenceAsync(
        TransferDirectoryPreparationRequest request,
        long expectedRevision,
        TransferSourceEvidence evidence,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken);
    Task<bool> BeginMutationAsync(
        TransferDirectoryMutationRecord mutation,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken);
    Task<bool> ResolveMutationAsync(
        TransferDirectoryMutationRecord expected,
        TransferDirectoryMutationState nextState,
        string? childExactId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken);
}

public sealed class TransferDirectoryPreparationFlow(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<TransferDirectoryPreparationOutcome> ExecuteAsync(
        ITransferDirectoryPreparationSession session,
        ITransferDirectoryPreparationEvidenceStore store,
        TransferDirectoryPreparationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        var attempt = await store.GetAttemptAsync(request, cancellationToken).ConfigureAwait(false);
        if (!MatchesPreparingAttempt(attempt, request)) return NotValidated(new RemotePath("/"));

        var unresolved = (await store.GetUnresolvedMutationsAsync(
                request.OperationId, request.AccountId, cancellationToken).ConfigureAwait(false))
            .Where(item => item.State is TransferDirectoryMutationState.Intent or
                TransferDirectoryMutationState.OutcomeUnknown)
            .OrderBy(item => item.Generation)
            .ThenBy(item => item.SegmentOrdinal)
            .FirstOrDefault();
        if (unresolved is not null)
            return await ReconcileUnresolvedAsync(session, store, request, unresolved, cancellationToken)
                .ConfigureAwait(false);

        var source = attempt!.SourceEvidence;
        if (source is null)
        {
            try
            {
                await session.OpenSourceAsync(request.SourceShare, cancellationToken).ConfigureAwait(false);
                if (request.SourceShare.ExtractionCode is not null)
                    await session.EnterExtractionCodeAsync(
                        request.SourceShare.ExtractionCode, cancellationToken).ConfigureAwait(false);
                source = (await session.ReadCompleteSourceAsync(cancellationToken).ConfigureAwait(false)).Evidence;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return NotValidated(new RemotePath("/")); }

            if (!MatchesSource(source, request) || !await store.PersistSourceEvidenceAsync(
                    request, attempt.Revision, source, timeProvider.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false))
                return NotValidated(new RemotePath("/"));
            attempt = await store.GetAttemptAsync(request, cancellationToken).ConfigureAwait(false);
            if (!MatchesPreparingAttempt(attempt, request) || attempt!.SourceEvidence is null)
                return NotValidated(new RemotePath("/"));
            source = attempt.SourceEvidence;
        }
        else if (!MatchesSource(source, request))
        {
            return NotValidated(new RemotePath("/"));
        }

        TransferDirectoryListing current;
        try
        {
            current = await session.OpenDestinationPickerAsync(cancellationToken).ConfigureAwait(false);
            if (!IsExactListing(current, source.BaiduUserId)) return NotValidated(new RemotePath("/"));
            if (!IsSameOrAncestorPath(current.Directory!.Path, request.TargetPath))
            {
                current = await session.OpenVerifiedRootAsync(cancellationToken).ConfigureAwait(false);
                if (!IsExactListing(current, source.BaiduUserId) || current.Directory!.Path.Value != "/" ||
                    current.AncestorExactIds.Count != 0)
                    return NotValidated(new RemotePath("/"));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return NotValidated(new RemotePath("/")); }

        var confirmedPath = current.Directory!.Path;
        var suffix = RemainingSegments(confirmedPath, request.TargetPath);
        var baseOrdinal = Segments(confirmedPath).Count;
        for (var ordinal = 0; ordinal < suffix.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segment = suffix[ordinal];
            var childPath = Append(confirmedPath, segment);
            var sameName = current.DetailedItems
                .Where(item => string.Equals(item.ExactServerName, segment, StringComparison.Ordinal))
                .ToArray();
            if (sameName.Length > 1 || sameName.Any(item => item.Kind != TransferRemoteItemKind.Folder))
                return NotValidated(confirmedPath);

            RemoteDirectory child;
            if (sameName.Length == 1)
            {
                child = new RemoteDirectory(sameName[0].Identity.ExactId, childPath);
            }
            else
            {
                var intent = new TransferDirectoryMutationRecord(
                    request.OperationId, request.AccountId, request.Generation, baseOrdinal + ordinal,
                    current.Directory!.RemoteId, null, childPath, current.GenerationToken!,
                    source.BaiduUserId, request.Capability, TransferDirectoryMutationState.Intent,
                    attempt!.Revision);
                if (!await store.BeginMutationAsync(intent, timeProvider.GetUtcNow(), cancellationToken)
                    .ConfigureAwait(false))
                    return NeedsAttention(confirmedPath);
                try
                {
                    await session.BeginCreateDirectoryAsync(segment, cancellationToken).ConfigureAwait(false);
                    await session.ConfirmDirectoryCreationAsync(cancellationToken).ConfigureAwait(false);
                    var refreshed = await session.ReadCurrentDirectoryAsync(cancellationToken).ConfigureAwait(false);
                    if (!SameDirectory(current, refreshed, source.BaiduUserId))
                        throw new BaiduAutomationNotValidatedException(
                            BaiduAutomationDenialReason.ObservationNotValidated,
                            "Directory changed during creation.");
                    var created = refreshed.DetailedItems.Where(item =>
                        item.Kind == TransferRemoteItemKind.Folder &&
                        string.Equals(item.ExactServerName, segment, StringComparison.Ordinal)).ToArray();
                    if (created.Length != 1) throw new BaiduAutomationNotValidatedException(
                        BaiduAutomationDenialReason.CandidateNotUnique,
                        "Created directory identity is not unique.");
                    child = new RemoteDirectory(created[0].Identity.ExactId, childPath);
                    if (!await store.ResolveMutationAsync(
                            intent, TransferDirectoryMutationState.Confirmed, child.RemoteId,
                            timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false))
                        return NeedsAttention(confirmedPath);
                    current = refreshed;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await ResolveUnknownSafelyAsync(store, intent).ConfigureAwait(false);
                    throw;
                }
                catch
                {
                    await ResolveUnknownSafelyAsync(store, intent).ConfigureAwait(false);
                    return NeedsAttention(confirmedPath);
                }
            }

            try
            {
                var entered = await session.EnterDirectoryAsync(child, cancellationToken).ConfigureAwait(false);
                if (!IsEnteredChild(current, entered, child, source.BaiduUserId))
                    return NotValidated(confirmedPath);
                current = entered;
                confirmedPath = childPath;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return NeedsAttention(confirmedPath); }
        }

        try
        {
            await session.CancelDestinationPickerAsync(cancellationToken).ConfigureAwait(false);
            return TransferDirectoryPreparationOutcome.Prepared(current.Directory!);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return NeedsAttention(confirmedPath); }
    }

    private async Task<TransferDirectoryPreparationOutcome> ReconcileUnresolvedAsync(
        ITransferDirectoryPreparationSession session,
        ITransferDirectoryPreparationEvidenceStore store,
        TransferDirectoryPreparationRequest request,
        TransferDirectoryMutationRecord unresolved,
        CancellationToken cancellationToken)
    {
        var parentPath = Parent(unresolved.CanonicalChildPath);
        try
        {
            var authorization = await store.AuthorizeRecoveryAsync(
                new TransferDirectoryRecoveryAuthorizationRequest(
                    request.BatchId, request.Generation, unresolved), cancellationToken)
                .ConfigureAwait(false);
            if (authorization is null || authorization.ExpiresAtUtc <= timeProvider.GetUtcNow() ||
                authorization.OperationId != request.OperationId ||
                authorization.BatchId != request.BatchId ||
                authorization.AccountId != request.AccountId ||
                authorization.OriginGeneration != unresolved.Generation ||
                authorization.OwningAttemptRevision != unresolved.OwningAttemptRevision ||
                authorization.ParentExactId != unresolved.ParentExactId ||
                authorization.ParentPath != parentPath ||
                authorization.CanonicalChildPath != unresolved.CanonicalChildPath ||
                authorization.BaiduUserId != unresolved.BaiduUserId ||
                authorization.Capability != unresolved.Capability)
                return NeedsAttention(parentPath);
            var listing = await session.ReadExactParentForRecoveryAsync(
                new RemoteDirectory(unresolved.ParentExactId, parentPath), [], authorization,
                cancellationToken)
                .ConfigureAwait(false);
            if (!IsExactListing(listing, unresolved.BaiduUserId) ||
                listing.Directory!.RemoteId != unresolved.ParentExactId || listing.Directory.Path != parentPath ||
                unresolved.Capability != request.Capability)
                return NeedsAttention(parentPath);
            var segment = LastSegment(unresolved.CanonicalChildPath);
            var matches = listing.DetailedItems.Where(item =>
                item.Kind == TransferRemoteItemKind.Folder &&
                string.Equals(item.ExactServerName, segment, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
            {
                if (unresolved.State == TransferDirectoryMutationState.Intent)
                    await store.ResolveMutationAsync(
                        unresolved, TransferDirectoryMutationState.OutcomeUnknown, null,
                        timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                return NeedsAttention(parentPath);
            }
            if (!await store.ResolveMutationAsync(
                    unresolved, TransferDirectoryMutationState.Confirmed, matches[0].Identity.ExactId,
                    timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false))
                return NeedsAttention(parentPath);
            var child = new RemoteDirectory(matches[0].Identity.ExactId, unresolved.CanonicalChildPath);
            return NeedsAttention(child.Path);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return NeedsAttention(parentPath); }
    }

    private async Task ResolveUnknownSafelyAsync(
        ITransferDirectoryPreparationEvidenceStore store,
        TransferDirectoryMutationRecord intent)
    {
        try
        {
            await store.ResolveMutationAsync(
                intent, TransferDirectoryMutationState.OutcomeUnknown, null,
                timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
    }

    private static bool MatchesPreparingAttempt(
        WebAutomationAttemptRecord? attempt,
        TransferDirectoryPreparationRequest request) =>
        attempt is { OwnerKind: WebAutomationOwnerKind.Transfer, State: WebAutomationAttemptState.Preparing } &&
        attempt.OperationId == request.OperationId && attempt.AccountId == request.AccountId &&
        attempt.Generation == request.Generation;

    private static bool MatchesSource(
        TransferSourceEvidence evidence,
        TransferDirectoryPreparationRequest request) =>
        evidence.TotalCount == evidence.Items.Count && evidence.ReachedEnd && evidence.IsStable &&
        evidence.Capability == request.Capability;

    private static bool IsExactListing(TransferDirectoryListing listing, string expectedUid) =>
        listing is { Directory: not null, IsComplete: true, IsStable: true, ReachedEnd: true,
            GenerationToken: not null, BaiduUserId: not null } &&
        listing.TotalCount == listing.DetailedItems.Count &&
        string.Equals(listing.BaiduUserId, expectedUid, StringComparison.Ordinal);

    private static bool SameDirectory(
        TransferDirectoryListing expected,
        TransferDirectoryListing actual,
        string expectedUid) =>
        IsExactListing(actual, expectedUid) && expected.Directory == actual.Directory &&
        expected.AncestorExactIds.SequenceEqual(actual.AncestorExactIds, StringComparer.Ordinal);

    private static bool IsEnteredChild(
        TransferDirectoryListing parent,
        TransferDirectoryListing childListing,
        RemoteDirectory child,
        string expectedUid) =>
        IsExactListing(childListing, expectedUid) && childListing.Directory == child &&
        childListing.AncestorExactIds.Count == parent.AncestorExactIds.Count + 1 &&
        childListing.AncestorExactIds.Take(parent.AncestorExactIds.Count)
            .SequenceEqual(parent.AncestorExactIds, StringComparer.Ordinal) &&
        string.Equals(childListing.AncestorExactIds[^1], child.RemoteId, StringComparison.Ordinal);

    private static bool IsSameOrAncestorPath(RemotePath candidate, RemotePath target) =>
        candidate.Value == "/" || target.Value == candidate.Value ||
        target.Value.StartsWith(candidate.Value + '/', StringComparison.Ordinal);

    private static IReadOnlyList<string> RemainingSegments(RemotePath parent, RemotePath target)
    {
        if (!IsSameOrAncestorPath(parent, target)) return [];
        var parentSegments = Segments(parent);
        return Segments(target).Skip(parentSegments.Count).ToArray();
    }

    private static IReadOnlyList<string> Segments(RemotePath path) =>
        path.Value.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static RemotePath Append(RemotePath parent, string segment) =>
        new(parent.Value == "/" ? '/' + segment : parent.Value + '/' + segment);

    private static RemotePath Parent(RemotePath child)
    {
        var segments = Segments(child);
        return segments.Count <= 1 ? new RemotePath("/") :
            new RemotePath('/' + string.Join('/', segments.Take(segments.Count - 1)));
    }

    private static string LastSegment(RemotePath path) => Segments(path)[^1];

    private static TransferDirectoryPreparationOutcome NotValidated(RemotePath partial) =>
        TransferDirectoryPreparationOutcome.NeedsAttention(partial, BaiduErrorKind.NotValidated);

    private static TransferDirectoryPreparationOutcome NeedsAttention(RemotePath partial) =>
        TransferDirectoryPreparationOutcome.NeedsAttention(partial, BaiduErrorKind.UnknownResponse);
}
