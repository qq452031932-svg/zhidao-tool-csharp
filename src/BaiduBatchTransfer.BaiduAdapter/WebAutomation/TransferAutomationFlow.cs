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

public interface ITransferAutomationSession
{
    Task<TransferSourceListing> OpenExternalShareAsync(
        ShareReference share,
        CancellationToken cancellationToken);

    Task<TransferSourceListing> OpenAndReadSourceForSubmissionAsync(
        ShareReference share,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Exact source reopening is not implemented by this session.");

    Task EnterExtractionCodeAsync(string extractionCode, CancellationToken cancellationToken);

    Task<TransferDirectoryListing> OpenDestinationAndReadStableListingAsync(
        RemoteDirectory destination,
        CancellationToken cancellationToken);

    Task<TransferDirectoryListing> OpenExactTargetAndReadStableListingAsync(
        RemoteDirectory destination,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Exact target reading is not implemented by this session.");

    Task PersistBaselineAsync(
        WebAutomationBaseline baseline,
        string observationFingerprint,
        CancellationToken cancellationToken);

    Task PersistTargetPreparationAsync(
        TransferTargetPreparationEvidence evidence,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Exact target preparation persistence is not implemented by this session.");

    Task ReturnToExternalShareAsync(ShareReference share, CancellationToken cancellationToken);

    Task MarkSubmittingAsync(CancellationToken cancellationToken);

    Task MarkSubmittingAsync(
        TransferSubmissionEvidence evidence,
        CancellationToken cancellationToken) => MarkSubmittingAsync(cancellationToken);

    Task MarkSubmissionResultPendingAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task MarkSubmissionDefinitelyNotAcceptedAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task<TransferOfficialAcceptance> SaveWholeShareAsync(
        RemoteDirectory destination,
        DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken);

    Task<TransferOfficialAcceptance> SaveWholeShareExactlyOnceAsync(
        RemoteDirectory destination,
        TransferSourceEvidence source,
        DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Single-submit transfer is not implemented by this session.");

    Task PrepareWholeShareSubmissionAsync(
        RemoteDirectory destination,
        TransferSourceEvidence source,
        DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Exact transfer preparation is not implemented by this session.");

    Task PersistOfficialAcceptanceAsync(
        TransferOfficialAcceptance acceptance,
        CancellationToken cancellationToken);

    Task PersistTransferAcceptanceAsync(
        TransferOfficialAcceptance acceptance,
        CancellationToken cancellationToken) =>
        PersistOfficialAcceptanceAsync(acceptance, cancellationToken);

    Task<TransferDirectoryListing> ReopenDestinationAndReadStableListingAsync(
        RemoteDirectory destination,
        CancellationToken cancellationToken);
}

public sealed record TransferAutomationPreparation
{
    public TransferAutomationPreparation(
        WebAutomationBaseline baseline,
        TransferSourceEvidence sourceEvidence)
    {
        Baseline = baseline ?? throw new ArgumentNullException(nameof(baseline));
        SourceEvidence = sourceEvidence ?? throw new ArgumentNullException(nameof(sourceEvidence));
    }

    public TransferAutomationPreparation(TransferPreparationEvidence evidence)
    {
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        evidence.Validate();
        Baseline = evidence.Target.Baseline;
        SourceEvidence = evidence.Source;
    }

    public WebAutomationBaseline Baseline { get; }
    public TransferSourceEvidence SourceEvidence { get; }
    public TransferPreparationEvidence? Evidence { get; }

    public TransferAutomationPreparation Validate()
    {
        ArgumentNullException.ThrowIfNull(Baseline);
        ArgumentNullException.ThrowIfNull(SourceEvidence);
        Evidence?.Validate();
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
        return await SubmitAsync(
                session, request, preparation.Value!.Baseline,
                preparation.Value.SourceEvidence, exactPreparation: null, cancellationToken)
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
            var source = await session.OpenExternalShareAsync(request.Share, cancellationToken).ConfigureAwait(false);
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
                new TransferAutomationPreparation(baseline, source.Evidence),
                SubmissionAcceptance.DefinitelyNotAccepted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BaiduAutomationNotValidatedException)
        {
            return PreparationNotValidated();
        }
        catch (Exception)
        {
            return PreparationFailed();
        }
    }

    public async Task<BaiduAdapterResult<TransferAutomationPreparation>> PrepareFromPersistedSourceAsync(
        ITransferAutomationSession session,
        TransferAutomationRequest request,
        TransferSourceEvidence persistedSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(persistedSource);
        request.Validate();

        try
        {
            var listing = await session.OpenExactTargetAndReadStableListingAsync(
                request.Destination, cancellationToken).ConfigureAwait(false);
            if (!IsStableComplete(listing) || listing.Directory is null ||
                listing.Directory != request.Destination ||
                listing.GenerationToken is not { Length: > 0 } targetGeneration ||
                listing.BaiduUserId is not { Length: > 0 } targetUserId ||
                !string.Equals(targetUserId, persistedSource.BaiduUserId, StringComparison.Ordinal))
                return PreparationNotValidated();
            var baseline = WebAutomationBaseline.Create(
                listing.TopLevelItems.Select(item => item.ExactId));
            var durableFingerprint = TransferTargetPreparationEvidence.ComputeObservationFingerprint(
                request.Destination.RemoteId,
                request.Destination.Path,
                listing.TotalCount,
                listing.ReachedEnd,
                listing.IsStable,
                targetGeneration,
                targetUserId,
                TransferTargetInventoryScope.AllItems,
                baseline);
            var target = new TransferTargetPreparationEvidence(
                request.Destination.RemoteId,
                request.Destination.Path,
                listing.TotalCount,
                listing.ReachedEnd,
                listing.IsStable,
                targetGeneration,
                targetUserId,
                TransferTargetInventoryScope.AllItems,
                baseline,
                durableFingerprint);
            await session.PersistTargetPreparationAsync(target, cancellationToken).ConfigureAwait(false);
            return BaiduAdapterResult<TransferAutomationPreparation>.Success(
                new TransferAutomationPreparation(
                    new TransferPreparationEvidence(persistedSource, target).Validate()),
                SubmissionAcceptance.DefinitelyNotAccepted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BaiduAutomationNotValidatedException)
        {
            return PreparationNotValidated();
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
        CancellationToken cancellationToken = default) =>
        await SubmitAsync(
                session, request, baseline, expectedSource: null,
                exactPreparation: null, cancellationToken)
            .ConfigureAwait(false);

    public async Task<BaiduAdapterResult<TransferReceipt>> SubmitAsync(
        ITransferAutomationSession session,
        TransferAutomationRequest request,
        TransferPreparationEvidence preparation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(preparation);
        preparation.Validate();
        if (preparation.Target.TargetPath != request.Destination.Path ||
            !string.Equals(
                preparation.Target.TargetExactId,
                request.Destination.RemoteId,
                StringComparison.Ordinal))
            return DefinitelyNotValidated();
        return await SubmitAsync(
                session,
                request,
                preparation.Target.Baseline,
                preparation.Source,
                preparation,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<BaiduAdapterResult<TransferReceipt>> SubmitAsync(
        ITransferAutomationSession session,
        TransferAutomationRequest request,
        WebAutomationBaseline baseline,
        TransferSourceEvidence? expectedSource,
        TransferPreparationEvidence? exactPreparation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(baseline);
        request.Validate();

        var submitted = false;
        try
        {
            var source = exactPreparation is null
                ? await session.OpenExternalShareAsync(request.Share, cancellationToken).ConfigureAwait(false)
                : await session.OpenAndReadSourceForSubmissionAsync(
                    request.Share, cancellationToken).ConfigureAwait(false);
            if (exactPreparation is null && request.Share.ExtractionCode is not null)
                await session.EnterExtractionCodeAsync(request.Share.ExtractionCode, cancellationToken)
                    .ConfigureAwait(false);
            if (expectedSource is not null && !SameSource(expectedSource, source.Evidence))
                return DefinitelyNotValidated();

            if (exactPreparation is not null)
                await session.PrepareWholeShareSubmissionAsync(
                    request.Destination,
                    exactPreparation.Source,
                    request.DuplicatePolicy,
                    cancellationToken).ConfigureAwait(false);

            if (exactPreparation is null)
            {
                await session.MarkSubmittingAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await session.MarkSubmittingAsync(
                    new TransferSubmissionEvidence(
                        exactPreparation.Source.Items.Fingerprint,
                        exactPreparation.Target.TargetExactId,
                        exactPreparation.Target.Baseline.Fingerprint,
                        exactPreparation.Source.BaiduUserId,
                        exactPreparation.Source.Capability,
                        timeProvider.GetUtcNow()),
                    cancellationToken).ConfigureAwait(false);
            }
            submitted = true;
            var acceptance = exactPreparation is null
                ? await session.SaveWholeShareAsync(
                    request.Destination, request.DuplicatePolicy, cancellationToken).ConfigureAwait(false)
                : await session.SaveWholeShareExactlyOnceAsync(
                    request.Destination, exactPreparation.Source,
                    request.DuplicatePolicy, cancellationToken).ConfigureAwait(false);
            if (acceptance.Status == TransferOfficialAcceptanceStatus.ExplicitlyRejected)
            {
                if (exactPreparation is not null)
                    await session.MarkSubmissionDefinitelyNotAcceptedAsync(cancellationToken)
                        .ConfigureAwait(false);
                return DefinitelyNotAccepted();
            }
            if (acceptance.Status != TransferOfficialAcceptanceStatus.Accepted)
            {
                if (exactPreparation is not null)
                    await session.MarkSubmissionResultPendingAsync(cancellationToken)
                        .ConfigureAwait(false);
                return ResultPending();
            }
            if (exactPreparation is not null &&
                (string.IsNullOrWhiteSpace(acceptance.AdapterRequestId) ||
                 !string.Equals(
                     acceptance.TargetExactId,
                     exactPreparation.Target.TargetExactId,
                     StringComparison.Ordinal)))
            {
                await session.MarkSubmissionResultPendingAsync(cancellationToken)
                    .ConfigureAwait(false);
                return ResultPending();
            }
            if (exactPreparation is null)
                await session.PersistOfficialAcceptanceAsync(acceptance, cancellationToken).ConfigureAwait(false);
            else
                await session.PersistTransferAcceptanceAsync(acceptance, cancellationToken).ConfigureAwait(false);

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
        catch (BaiduAutomationNotValidatedException) when (!submitted)
        {
            return DefinitelyNotValidated();
        }
        catch (Exception)
        {
            if (submitted && exactPreparation is not null)
            {
                try
                {
                    await session.MarkSubmissionResultPendingAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the original outcome; recovery also treats Submitting as unresolved.
                }
            }
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

    public async Task<BaiduAdapterResult<TransferConfirmationResult>> ConfirmExactFromTargetAsync(
        ITransferAutomationSession session,
        RemoteDirectory destination,
        TransferPreparationEvidence preparation,
        TransferSubmissionEvidence submission,
        TransferOfficialAcceptance? officialAcceptance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(submission);
        preparation.Validate();
        if (preparation.Target.TargetPath != destination.Path ||
            !string.Equals(preparation.Target.TargetExactId, destination.RemoteId, StringComparison.Ordinal) ||
            !string.Equals(submission.SourceFingerprint,
                preparation.Source.Items.Fingerprint, StringComparison.Ordinal) ||
            !string.Equals(submission.TargetExactId,
                preparation.Target.TargetExactId, StringComparison.Ordinal) ||
            !string.Equals(submission.TargetBaselineFingerprint,
                preparation.Target.Baseline.Fingerprint, StringComparison.Ordinal) ||
            !string.Equals(submission.BaiduUserId,
                preparation.Source.BaiduUserId, StringComparison.Ordinal) ||
            submission.Capability != preparation.Source.Capability)
            return BaiduAdapterResult<TransferConfirmationResult>.Failure(
                BaiduErrorKind.NotValidated,
                adapterDiagnostic: null,
                submissionAcceptance: SubmissionAcceptance.Unknown);

        try
        {
            var current = await session.ReopenDestinationAndReadStableListingAsync(
                destination, cancellationToken).ConfigureAwait(false);
            if (!IsStableComplete(current) || current.Directory != destination ||
                !string.Equals(current.BaiduUserId,
                    preparation.Source.BaiduUserId, StringComparison.Ordinal))
                return AmbiguousConfirmation();
            if (officialAcceptance is null || !officialAcceptance.IsAccepted ||
                officialAcceptance.IdentityMapping is null ||
                officialAcceptance.ExactRemoteItems is null ||
                string.IsNullOrWhiteSpace(officialAcceptance.AdapterRequestId) ||
                !string.Equals(officialAcceptance.TargetExactId,
                    destination.RemoteId, StringComparison.Ordinal))
                return AmbiguousConfirmation();

            var baselineIds = preparation.Target.Baseline.Items
                .Select(item => item.ExactId).ToHashSet(StringComparer.Ordinal);
            var currentIds = current.TopLevelItems
                .Select(item => item.ExactId).ToHashSet(StringComparer.Ordinal);
            if (baselineIds.Any(id => !currentIds.Contains(id)))
                return AmbiguousConfirmation();
            var added = current.TopLevelItems
                .Where(item => !baselineIds.Contains(item.ExactId)).ToArray();
            if (!IsExactlyCorrelated(
                    new TransferSourceListing(preparation.Source), officialAcceptance, added))
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
            return AmbiguousConfirmation();
        }
    }

    private static bool IsStableComplete(TransferDirectoryListing listing) =>
        listing is { Scope: TransferDirectoryListingScope.AllItems, IsComplete: true, IsStable: true };

    private static bool IsExactlyCorrelated(
        TransferSourceListing source,
        TransferOfficialAcceptance acceptance,
        IReadOnlyCollection<RemoteItemIdentity> added)
    {
        if (!acceptance.IsAccepted || acceptance.ExactRemoteItems is null ||
            acceptance.IdentityMapping is null ||
            !acceptance.IdentityMapping.Covers(source.ExactItems) ||
            acceptance.ExactRemoteItems.Count != source.TopLevelItemCount ||
            added.Count != acceptance.ExactRemoteItems.Count)
            return false;
        var mappedIds = acceptance.IdentityMapping.Entries
            .Select(entry => entry.DestinationExactId)
            .ToHashSet(StringComparer.Ordinal);
        if (mappedIds.Count != acceptance.ExactRemoteItems.Count ||
            acceptance.ExactRemoteItems.Items.Any(item => !mappedIds.Contains(item.ExactId)))
            return false;
        var addedIds = added.Select(item => item.ExactId).ToHashSet(StringComparer.Ordinal);
        return acceptance.ExactRemoteItems.Items.All(item => addedIds.Contains(item.ExactId));
    }

    private static bool SameSource(TransferSourceEvidence expected, TransferSourceEvidence current) =>
        expected.Items == current.Items &&
        expected.TotalCount == current.TotalCount &&
        expected.ReachedEnd == current.ReachedEnd &&
        expected.IsStable == current.IsStable &&
        string.Equals(expected.GenerationToken, current.GenerationToken, StringComparison.Ordinal) &&
        string.Equals(expected.BaiduUserId, current.BaiduUserId, StringComparison.Ordinal) &&
        expected.Capability == current.Capability &&
        string.Equals(expected.ObservationFingerprint, current.ObservationFingerprint, StringComparison.Ordinal);

    private static BaiduAdapterResult<TransferReceipt> DefinitelyNotAccepted() =>
        BaiduAdapterResult<TransferReceipt>.Failure(
            BaiduErrorKind.UnknownResponse,
            adapterDiagnostic: null,
            submissionAcceptance: SubmissionAcceptance.DefinitelyNotAccepted);

    private static BaiduAdapterResult<TransferReceipt> DefinitelyNotValidated() =>
        BaiduAdapterResult<TransferReceipt>.Failure(
            BaiduErrorKind.NotValidated,
            adapterDiagnostic: null,
            submissionAcceptance: SubmissionAcceptance.DefinitelyNotAccepted);

    private static BaiduAdapterResult<TransferAutomationPreparation> PreparationFailed() =>
        BaiduAdapterResult<TransferAutomationPreparation>.Failure(
            BaiduErrorKind.UnknownResponse,
            adapterDiagnostic: null,
            submissionAcceptance: SubmissionAcceptance.DefinitelyNotAccepted);

    private static BaiduAdapterResult<TransferAutomationPreparation> PreparationNotValidated() =>
        BaiduAdapterResult<TransferAutomationPreparation>.Failure(
            BaiduErrorKind.NotValidated,
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
