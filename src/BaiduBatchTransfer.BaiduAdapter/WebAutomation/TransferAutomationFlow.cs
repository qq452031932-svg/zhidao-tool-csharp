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

public sealed record TransferOfficialAcceptance
{
    public TransferOfficialAcceptance(
        bool isAccepted,
        string? adapterRequestId,
        string observationFingerprint)
    {
        WebAutomationAttemptRecord.ValidateFingerprint(observationFingerprint, nameof(observationFingerprint));
        IsAccepted = isAccepted;
        AdapterRequestId = string.IsNullOrWhiteSpace(adapterRequestId) ? null : adapterRequestId.Trim();
        ObservationFingerprint = observationFingerprint;
    }

    public bool IsAccepted { get; }
    public string? AdapterRequestId { get; }
    public string ObservationFingerprint { get; }

    public override string ToString() =>
        $"TransferOfficialAcceptance {{ IsAccepted = {IsAccepted}, AdapterRequestId = [REDACTED] }}";
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

        var submitted = false;
        try
        {
            var source = await session.OpenExternalShareAsync(request.Share, cancellationToken)
                .ConfigureAwait(false);
            if (request.Share.ExtractionCode is not null)
                await session.EnterExtractionCodeAsync(request.Share.ExtractionCode, cancellationToken)
                    .ConfigureAwait(false);

            var baselineListing = await session
                .OpenDestinationAndReadStableListingAsync(request.Destination, cancellationToken)
                .ConfigureAwait(false);
            if (!IsStableComplete(baselineListing)) return DefinitelyNotAccepted();

            var baseline = WebAutomationBaseline.Create(
                baselineListing.TopLevelItems.Select(item => item.ExactId));
            await session.PersistBaselineAsync(
                baseline,
                baselineListing.ObservationFingerprint,
                cancellationToken).ConfigureAwait(false);
            await session.ReturnToExternalShareAsync(request.Share, cancellationToken).ConfigureAwait(false);

            submitted = true;
            await session.MarkSubmittingAsync(cancellationToken).ConfigureAwait(false);
            var acceptance = await session.SaveWholeShareAsync(
                request.Destination,
                request.DuplicatePolicy,
                cancellationToken).ConfigureAwait(false);
            if (!acceptance.IsAccepted) return DefinitelyNotAccepted();
            await session.PersistOfficialAcceptanceAsync(acceptance, cancellationToken).ConfigureAwait(false);

            var current = await session
                .ReopenDestinationAndReadStableListingAsync(request.Destination, cancellationToken)
                .ConfigureAwait(false);
            if (!IsStableComplete(current)) return ResultPending();

            var baselineIds = baseline.Items.Select(item => item.ExactId).ToHashSet(StringComparer.Ordinal);
            var added = current.TopLevelItems
                .Where(item => !baselineIds.Contains(item.ExactId))
                .ToArray();
            if (added.Length != source.TopLevelItemCount) return ResultPending();

            return BaiduAdapterResult<TransferReceipt>.Success(
                TransferReceipt.WithExactEvidence(
                    timeProvider.GetUtcNow(),
                    acceptance.AdapterRequestId,
                    new RemoteItemIdentitySet(added)),
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
        bool officialAcceptancePersisted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sourceShare);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(baseline);
        if (!officialAcceptancePersisted)
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
            if (added.Length != source.TopLevelItemCount)
                return AmbiguousConfirmation();

            return BaiduAdapterResult<TransferConfirmationResult>.Success(
                TransferConfirmationResult.ExactPresent(new RemoteItemIdentitySet(added)),
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

    private static BaiduAdapterResult<TransferReceipt> DefinitelyNotAccepted() =>
        BaiduAdapterResult<TransferReceipt>.Failure(
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
