using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.WebAutomation;

public sealed record TransferAutomationRequest(
    ShareReference Share,
    RemoteDirectory Destination,
    DuplicatePolicy DuplicatePolicy)
{
    public TransferAutomationRequest Validate()
    {
        ArgumentNullException.ThrowIfNull(Share);
        ArgumentNullException.ThrowIfNull(Destination);
        if (!Enum.IsDefined(DuplicatePolicy))
            throw new ArgumentOutOfRangeException(nameof(DuplicatePolicy));
        return this;
    }

    public override string ToString() =>
        $"TransferAutomationRequest {{ Share = [REDACTED], Destination = [REDACTED], DuplicatePolicy = {DuplicatePolicy} }}";
}

public sealed record TransferSourceListing
{
    public TransferSourceListing(int topLevelItemCount, string observationFingerprint)
    {
        if (topLevelItemCount < 1) throw new ArgumentOutOfRangeException(nameof(topLevelItemCount));
        WebAutomationAttemptRecord.ValidateFingerprint(observationFingerprint, nameof(observationFingerprint));
        TopLevelItemCount = topLevelItemCount;
        ObservationFingerprint = observationFingerprint;
    }

    public int TopLevelItemCount { get; }
    public string ObservationFingerprint { get; }

    public override string ToString() =>
        $"TransferSourceListing {{ TopLevelItemCount = {TopLevelItemCount}, ObservationFingerprint = [REDACTED] }}";
}

public sealed record TransferDirectoryListing
{
    public TransferDirectoryListing(
        bool isComplete,
        bool isStable,
        IReadOnlyList<RemoteItemIdentity> topLevelItems,
        string observationFingerprint)
    {
        ArgumentNullException.ThrowIfNull(topLevelItems);
        WebAutomationAttemptRecord.ValidateFingerprint(observationFingerprint, nameof(observationFingerprint));
        var snapshot = topLevelItems
            .Select(item => item ?? throw new ArgumentException("Directory items cannot contain null values.", nameof(topLevelItems)))
            .ToArray();
        if (snapshot.Select(item => item.ExactId).Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
            throw new ArgumentException("Directory item identities must be unique.", nameof(topLevelItems));
        IsComplete = isComplete;
        IsStable = isStable;
        TopLevelItems = Array.AsReadOnly(snapshot);
        ObservationFingerprint = observationFingerprint;
    }

    public bool IsComplete { get; }
    public bool IsStable { get; }
    public IReadOnlyList<RemoteItemIdentity> TopLevelItems { get; }
    public string ObservationFingerprint { get; }

    public override string ToString() =>
        $"TransferDirectoryListing {{ IsComplete = {IsComplete}, IsStable = {IsStable}, ItemCount = {TopLevelItems.Count}, ObservationFingerprint = [REDACTED], Items = [REDACTED] }}";
}

public enum TransferOfficialAcceptanceStatus
{
    Accepted = 0,
    ExplicitlyRejected = 1,
    Unobserved = 2,
}

public sealed record TransferOfficialAcceptance
{
    public TransferOfficialAcceptance(
        TransferOfficialAcceptanceStatus status,
        string? adapterRequestId,
        RemoteItemIdentitySet? exactRemoteItems,
        string observationFingerprint)
    {
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        WebAutomationAttemptRecord.ValidateFingerprint(observationFingerprint, nameof(observationFingerprint));
        Status = status;
        AdapterRequestId = string.IsNullOrWhiteSpace(adapterRequestId) ? null : adapterRequestId.Trim();
        ExactRemoteItems = exactRemoteItems;
        ObservationFingerprint = observationFingerprint;
    }

    public TransferOfficialAcceptanceStatus Status { get; }
    public bool IsAccepted => Status == TransferOfficialAcceptanceStatus.Accepted;
    public string? AdapterRequestId { get; }
    public RemoteItemIdentitySet? ExactRemoteItems { get; }
    public string ObservationFingerprint { get; }

    public override string ToString() =>
        $"TransferOfficialAcceptance {{ Status = {Status}, AdapterRequestId = [REDACTED], ExactRemoteItems = [REDACTED] }}";
}

public interface ITransferAutomationSession
{
    Task<TransferSourceListing> OpenExternalShareAsync(
        ShareReference share,
        CancellationToken cancellationToken);

    Task EnterExtractionCodeAsync(string extractionCode, CancellationToken cancellationToken);

    Task<TransferDirectoryListing> OpenDestinationAndReadStableListingAsync(
        RemoteDirectory destination,
        CancellationToken cancellationToken);

    Task PersistBaselineAsync(
        WebAutomationBaseline baseline,
        string observationFingerprint,
        CancellationToken cancellationToken);

    Task ReturnToExternalShareAsync(ShareReference share, CancellationToken cancellationToken);

    Task MarkSubmittingAsync(CancellationToken cancellationToken);

    Task<TransferOfficialAcceptance> SaveWholeShareAsync(
        RemoteDirectory destination,
        DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken);

    Task PersistOfficialAcceptanceAsync(
        TransferOfficialAcceptance acceptance,
        CancellationToken cancellationToken);

    Task<TransferDirectoryListing> ReopenDestinationAndReadStableListingAsync(
        RemoteDirectory destination,
        CancellationToken cancellationToken);
}

public sealed record TransferAutomationPreparation(WebAutomationBaseline Baseline)
{
    public TransferAutomationPreparation Validate()
    {
        ArgumentNullException.ThrowIfNull(Baseline);
        return this;
    }
}

public sealed class TransferAutomationFlow(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<BaiduAdapterResult<TransferReceipt>> ExecuteAsync(
        ITransferAutomationSession session,
        TransferAutomationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        var preparation = await PrepareAsync(session, request, cancellationToken).ConfigureAwait(false);
        if (!preparation.IsSuccess)
            return BaiduAdapterResult<TransferReceipt>.Failure(
                preparation.ErrorKind!.Value,
                adapterDiagnostic: null,
                preparation.RetryAfter,
                preparation.SubmissionAcceptance);
        return await SubmitAsync(session, request, preparation.Value!.Baseline, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<BaiduAdapterResult<TransferAutomationPreparation>> PrepareAsync(
        ITransferAutomationSession session,
        TransferAutomationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        try
        {
            await session.OpenExternalShareAsync(request.Share, cancellationToken).ConfigureAwait(false);
            if (request.Share.ExtractionCode is not null)
                await session.EnterExtractionCodeAsync(request.Share.ExtractionCode, cancellationToken)
                    .ConfigureAwait(false);
            var baselineListing = await session
                .OpenDestinationAndReadStableListingAsync(request.Destination, cancellationToken)
                .ConfigureAwait(false);
            if (!IsStableComplete(baselineListing)) return PreparationFailed();
            var baseline = WebAutomationBaseline.Create(
                baselineListing.TopLevelItems.Select(item => item.ExactId));
            await session.PersistBaselineAsync(
                baseline,
                baselineListing.ObservationFingerprint,
                cancellationToken).ConfigureAwait(false);
            return BaiduAdapterResult<TransferAutomationPreparation>.Success(
                new TransferAutomationPreparation(baseline),
                SubmissionAcceptance.DefinitelyNotAccepted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return PreparationFailed();
        }
    }

    public async Task<BaiduAdapterResult<TransferReceipt>> SubmitAsync(
        ITransferAutomationSession session,
        TransferAutomationRequest request,
        WebAutomationBaseline baseline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(baseline);
        request.Validate();

        var submitted = false;
        try
        {
            var source = await session.OpenExternalShareAsync(request.Share, cancellationToken)
                .ConfigureAwait(false);
            if (request.Share.ExtractionCode is not null)
                await session.EnterExtractionCodeAsync(request.Share.ExtractionCode, cancellationToken)
                    .ConfigureAwait(false);

            submitted = true;
            await session.MarkSubmittingAsync(cancellationToken).ConfigureAwait(false);
            var acceptance = await session.SaveWholeShareAsync(
                request.Destination,
                request.DuplicatePolicy,
                cancellationToken).ConfigureAwait(false);
            if (acceptance.Status == TransferOfficialAcceptanceStatus.ExplicitlyRejected)
                return DefinitelyNotAccepted();
            if (acceptance.Status != TransferOfficialAcceptanceStatus.Accepted)
                return ResultPending();
            await session.PersistOfficialAcceptanceAsync(acceptance, cancellationToken).ConfigureAwait(false);

            var current = await session
                .ReopenDestinationAndReadStableListingAsync(request.Destination, cancellationToken)
                .ConfigureAwait(false);
            if (!IsStableComplete(current)) return ResultPending();

            var baselineIds = baseline.Items.Select(item => item.ExactId).ToHashSet(StringComparer.Ordinal);
            if (baselineIds.Any(id => current.TopLevelItems.All(item => item.ExactId != id)))
                return ResultPending();
            var added = current.TopLevelItems
                .Where(item => !baselineIds.Contains(item.ExactId))
                .ToArray();
            if (!IsExactlyCorrelated(source, acceptance, added)) return ResultPending();

            return BaiduAdapterResult<TransferReceipt>.Success(
                TransferReceipt.WithExactEvidence(
                    timeProvider.GetUtcNow(),
                    acceptance.AdapterRequestId,
                    acceptance.ExactRemoteItems!),
                SubmissionAcceptance.Unknown);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return submitted ? ResultPending() : DefinitelyNotAccepted();
        }
    }

    public async Task<BaiduAdapterResult<TransferConfirmationResult>> ConfirmExactAsync(
        ITransferAutomationSession session,
        ShareReference sourceShare,
        RemoteDirectory destination,
        WebAutomationBaseline baseline,
        TransferOfficialAcceptance officialAcceptance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sourceShare);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(officialAcceptance);
        if (!officialAcceptance.IsAccepted || officialAcceptance.ExactRemoteItems is null)
            return AmbiguousConfirmation();

        try
        {
            var source = await session.OpenExternalShareAsync(sourceShare, cancellationToken)
                .ConfigureAwait(false);
            if (sourceShare.ExtractionCode is not null)
                await session.EnterExtractionCodeAsync(sourceShare.ExtractionCode, cancellationToken)
                    .ConfigureAwait(false);
            var current = await session.ReopenDestinationAndReadStableListingAsync(
                destination,
                cancellationToken).ConfigureAwait(false);
            if (!IsStableComplete(current)) return AmbiguousConfirmation();

            var currentIds = current.TopLevelItems
                .Select(item => item.ExactId)
                .ToHashSet(StringComparer.Ordinal);
            if (baseline.Items.Any(item => !currentIds.Contains(item.ExactId)))
                return AmbiguousConfirmation();
            var baselineIds = baseline.Items.Select(item => item.ExactId).ToHashSet(StringComparer.Ordinal);
            var added = current.TopLevelItems
                .Where(item => !baselineIds.Contains(item.ExactId))
                .ToArray();
            if (!IsExactlyCorrelated(source, officialAcceptance, added))
                return AmbiguousConfirmation();

            return BaiduAdapterResult<TransferConfirmationResult>.Success(
                TransferConfirmationResult.ExactPresent(officialAcceptance.ExactRemoteItems),
                SubmissionAcceptance.Unknown);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return BaiduAdapterResult<TransferConfirmationResult>.Failure(
                BaiduErrorKind.UnknownResponse,
                adapterDiagnostic: null,
                submissionAcceptance: SubmissionAcceptance.Unknown);
        }
    }

    private static bool IsStableComplete(TransferDirectoryListing listing) =>
        listing is { IsComplete: true, IsStable: true };

    private static bool IsExactlyCorrelated(
        TransferSourceListing source,
        TransferOfficialAcceptance acceptance,
        IReadOnlyCollection<RemoteItemIdentity> added)
    {
        if (!acceptance.IsAccepted || acceptance.ExactRemoteItems is null ||
            acceptance.ExactRemoteItems.Count != source.TopLevelItemCount ||
            added.Count != acceptance.ExactRemoteItems.Count)
            return false;
        var addedIds = added.Select(item => item.ExactId).ToHashSet(StringComparer.Ordinal);
        return acceptance.ExactRemoteItems.Items.All(item => addedIds.Contains(item.ExactId));
    }

    private static BaiduAdapterResult<TransferReceipt> DefinitelyNotAccepted() =>
        BaiduAdapterResult<TransferReceipt>.Failure(
            BaiduErrorKind.UnknownResponse,
            adapterDiagnostic: null,
            submissionAcceptance: SubmissionAcceptance.DefinitelyNotAccepted);

    private static BaiduAdapterResult<TransferAutomationPreparation> PreparationFailed() =>
        BaiduAdapterResult<TransferAutomationPreparation>.Failure(
            BaiduErrorKind.UnknownResponse,
            adapterDiagnostic: null,
            submissionAcceptance: SubmissionAcceptance.DefinitelyNotAccepted);

    private static BaiduAdapterResult<TransferReceipt> ResultPending() =>
        BaiduAdapterResult<TransferReceipt>.Failure(
            BaiduErrorKind.UnknownResponse,
            adapterDiagnostic: null,
            submissionAcceptance: SubmissionAcceptance.Unknown);

    private static BaiduAdapterResult<TransferConfirmationResult> AmbiguousConfirmation() =>
        BaiduAdapterResult<TransferConfirmationResult>.Success(
            TransferConfirmationResult.Ambiguous,
            SubmissionAcceptance.Unknown);
}
