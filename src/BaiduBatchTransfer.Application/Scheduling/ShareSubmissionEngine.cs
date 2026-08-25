using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Scheduling;

public sealed record ShareDiscoverySnapshot(
    WebAutomationBaseline FullListing,
    IReadOnlyList<BaiduShareRecord> ExactMatches,
    IReadOnlyList<BaiduShareRecord> AllItems);

public enum ExistingShareAction
{
    SubmitNew,
    Reuse,
    Cancel,
    RequiresDecision,
    Incompatible,
}

public sealed record ExistingShareResolution(ExistingShareAction Action, BaiduShareRecord? ReusableShare = null);

public enum ShareSubmissionDisposition
{
    Accepted,
    DefinitelyNotAccepted,
    ResultPending,
    PreparationFailed,
    Stale,
}

public sealed record ShareSubmissionResult(
    ShareSubmissionDisposition Disposition,
    ShareCreationReceipt? Receipt = null,
    BaiduErrorKind? ErrorKind = null,
    string? SafeMessage = null,
    int CompletedRetries = 0);

public sealed record ShareSubmissionPorts(
    Func<CancellationToken, Task<BaiduAdapterResult<SharePreparationReceipt>>> PrepareAsync,
    Func<CancellationToken, Task<bool>> EnterDurableSubmittingBoundaryAsync,
    Func<int, CancellationToken, Task<bool>> BeginRetryAsync,
    Func<CancellationToken, Task<BaiduAdapterResult<ShareCreationReceipt>>> SubmitAsync,
    Func<int, CancellationToken, Task<bool>> RecordDefinitelyNotAcceptedAsync,
    Func<CancellationToken, Task<bool>> PersistResultPendingAsync);

public sealed class ShareSubmissionEngine
{
    private const int MaximumPages = 10_000;

    public async Task<ShareSubmissionResult> ExecuteSubmissionAsync(
        PermanentShareRequest request,
        ShareSubmissionPorts ports,
        RetryPolicy retryPolicy,
        IAsyncDelay delay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(retryPolicy);
        ArgumentNullException.ThrowIfNull(delay);

        BaiduAdapterResult<SharePreparationReceipt> preparation;
        try
        {
            preparation = await ports.PrepareAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            return new(ShareSubmissionDisposition.PreparationFailed,
                ErrorKind: BaiduErrorKind.UnknownResponse,
                SafeMessage: "The service returned an unrecognized response.");
        }
        if (!preparation.IsSuccess)
            return new(ShareSubmissionDisposition.PreparationFailed,
                ErrorKind: preparation.ErrorKind,
                SafeMessage: preparation.SafeMessage);

        if (!await ports.EnterDurableSubmittingBoundaryAsync(cancellationToken).ConfigureAwait(false))
            return new(ShareSubmissionDisposition.Stale);

        var completedRetries = 0;
        while (true)
        {
            if (completedRetries > 0 &&
                !await ports.BeginRetryAsync(completedRetries, cancellationToken).ConfigureAwait(false))
                return new(ShareSubmissionDisposition.Stale, CompletedRetries: completedRetries);

            BaiduAdapterResult<ShareCreationReceipt> submitted;
            try
            {
                submitted = await ports.SubmitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await ports.PersistResultPendingAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch
            {
                var persisted = await ports.PersistResultPendingAsync(CancellationToken.None).ConfigureAwait(false);
                return new(persisted ? ShareSubmissionDisposition.ResultPending : ShareSubmissionDisposition.Stale,
                    CompletedRetries: completedRetries);
            }

            if (submitted.IsSuccess)
            {
                if (IsExactAcceptedReceipt(request, submitted.Value!))
                    return new(ShareSubmissionDisposition.Accepted, submitted.Value,
                        CompletedRetries: completedRetries);
                var persisted = await ports.PersistResultPendingAsync(cancellationToken).ConfigureAwait(false);
                return new(persisted ? ShareSubmissionDisposition.ResultPending : ShareSubmissionDisposition.Stale,
                    CompletedRetries: completedRetries);
            }
            if (submitted.SubmissionAcceptance != SubmissionAcceptance.DefinitelyNotAccepted)
            {
                var persisted = await ports.PersistResultPendingAsync(cancellationToken).ConfigureAwait(false);
                return new(persisted ? ShareSubmissionDisposition.ResultPending : ShareSubmissionDisposition.Stale,
                    ErrorKind: submitted.ErrorKind, SafeMessage: submitted.SafeMessage,
                    CompletedRetries: completedRetries);
            }

            var completedAttempts = checked(completedRetries + 1);
            if (!await ports.RecordDefinitelyNotAcceptedAsync(completedAttempts, cancellationToken).ConfigureAwait(false))
                return new(ShareSubmissionDisposition.Stale, CompletedRetries: completedRetries);
            var retry = retryPolicy.Decide(submitted.ErrorKind!.Value, completedRetries, submitted.RetryAfter);
            if (!retry.ShouldRetry)
                return new(ShareSubmissionDisposition.DefinitelyNotAccepted,
                    ErrorKind: submitted.ErrorKind, SafeMessage: submitted.SafeMessage,
                    CompletedRetries: completedRetries);
            completedRetries++;
            await delay.DelayAsync(retry.Delay, cancellationToken).ConfigureAwait(false);
        }
    }

    public ShareSubmissionResult EvaluateConfirmation(
        PermanentShareRequest request,
        BaiduAdapterResult<ShareCreationReceipt> confirmation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(confirmation);
        if (!confirmation.IsSuccess)
            return confirmation.SubmissionAcceptance == SubmissionAcceptance.DefinitelyNotAccepted
                ? new(ShareSubmissionDisposition.DefinitelyNotAccepted,
                    ErrorKind: confirmation.ErrorKind, SafeMessage: confirmation.SafeMessage)
                : new(ShareSubmissionDisposition.ResultPending,
                    ErrorKind: confirmation.ErrorKind, SafeMessage: confirmation.SafeMessage);
        return IsExactAcceptedReceipt(request, confirmation.Value!)
            ? new(ShareSubmissionDisposition.Accepted, confirmation.Value)
            : new(ShareSubmissionDisposition.ResultPending);
    }

    public ExistingShareResolution ResolveExistingShare(
        ExistingShareDecision? decision,
        PermanentShareRequest request,
        IReadOnlyCollection<BaiduShareRecord> exactMatches,
        BaiduShareRecord? confirmed = null,
        bool requireExistingForReuse = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(exactMatches);
        if (decision == ExistingShareDecision.Cancel) return new(ExistingShareAction.Cancel);
        if (decision == ExistingShareDecision.Regenerate) return new(ExistingShareAction.SubmitNew);
        if (decision is null)
            return exactMatches.Count == 0
                ? new(ExistingShareAction.SubmitNew)
                : new(ExistingShareAction.RequiresDecision);

        var compatible = exactMatches.Where(item => IsCompatible(item, request)).ToArray();
        if (confirmed is not null)
        {
            var exactConfirmed = compatible.SingleOrDefault(item => Same(item, confirmed));
            if (exactConfirmed is not null) return new(ExistingShareAction.Reuse, exactConfirmed);
            return new(ExistingShareAction.RequiresDecision);
        }
        if (compatible.Length == 1) return new(ExistingShareAction.Reuse, compatible[0]);
        if (compatible.Length > 1) return new(ExistingShareAction.RequiresDecision);
        return exactMatches.Count == 0
            ? new(requireExistingForReuse ? ExistingShareAction.RequiresDecision : ExistingShareAction.SubmitNew)
            : new(ExistingShareAction.Incompatible);
    }

    public async Task<BaiduAdapterResult<ShareDiscoverySnapshot>> DiscoverAsync(
        IBaiduShareAdapter adapter,
        AccountSession session,
        PermanentShareRequest request,
        CancellationToken cancellationToken) =>
        await DiscoverCoreAsync(adapter, session, request, null, null, cancellationToken).ConfigureAwait(false);

    public async Task<BaiduAdapterResult<ShareDiscoverySnapshot>> DiscoverAsync(
        IBaiduShareAdapter adapter,
        AccountSession session,
        PermanentShareRequest request,
        RetryPolicy retryPolicy,
        IAsyncDelay delay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retryPolicy);
        ArgumentNullException.ThrowIfNull(delay);
        return await DiscoverCoreAsync(adapter, session, request, retryPolicy, delay, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<BaiduAdapterResult<ShareDiscoverySnapshot>> DiscoverCoreAsync(
        IBaiduShareAdapter adapter,
        AccountSession session,
        PermanentShareRequest request,
        RetryPolicy? retryPolicy,
        IAsyncDelay? delay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        var cursor = (string?)null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var exact = new List<BaiduShareRecord>();
        var all = new List<BaiduShareRecord>();
        for (var pageNumber = 0; pageNumber < MaximumPages; pageNumber++)
        {
            BaiduAdapterResult<BaiduSharePage> page;
            var completedRetries = 0;
            while (true)
            {
                try
                {
                    page = await adapter.ListSharesAsync(session, cursor, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch
                {
                    return Failure("SHARE_DISCOVERY_EXCEPTION");
                }
                if (page.IsSuccess) break;
                var retry = retryPolicy?.Decide(
                    page.ErrorKind!.Value, completedRetries, page.RetryAfter) ?? RetryDecision.Stop;
                if (!retry.ShouldRetry)
                    return BaiduAdapterResult<ShareDiscoverySnapshot>.Failure(
                        page.ErrorKind!.Value, adapterDiagnostic: null, page.RetryAfter,
                        SubmissionAcceptance.DefinitelyNotAccepted);
                completedRetries++;
                await delay!.DelayAsync(retry.Delay, cancellationToken).ConfigureAwait(false);
            }
            foreach (var item in page.Value!.Items)
            {
                if (!ids.Add(item.ShareId))
                    return Failure("DUPLICATE_SHARE_ID");
                all.Add(item);
                if (item.RemoteItems == request.RemoteItems) exact.Add(item);
            }
            cursor = page.Value.NextCursor;
            if (cursor is null)
            {
                var baseline = WebAutomationBaseline.Create(ids);
                return BaiduAdapterResult<ShareDiscoverySnapshot>.Success(
                    new ShareDiscoverySnapshot(baseline, exact, all),
                    SubmissionAcceptance.DefinitelyNotAccepted);
            }
            if (string.IsNullOrWhiteSpace(cursor) || !seenCursors.Add(cursor))
                return Failure("INVALID_SHARE_CURSOR");
        }
        return Failure("SHARE_PAGE_LIMIT");
    }

    public ShareCreationReceipt? ConfirmUniqueExact(
        PermanentShareRequest request,
        WebAutomationBaseline baseline,
        IReadOnlyCollection<BaiduShareRecord> fullAfterListing)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(fullAfterListing);
        var ids = fullAfterListing.Select(item => item.ShareId).ToHashSet(StringComparer.Ordinal);
        var baselineIds = baseline.Items.Select(item => item.ExactId).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != fullAfterListing.Count || baselineIds.Any(id => !ids.Contains(id))) return null;
        var added = fullAfterListing.Where(item => !baselineIds.Contains(item.ShareId)).ToArray();
        if (added.Length != 1 || added[0].RemoteItems != request.RemoteItems) return null;
        var candidate = added[0];
        var behavior = request.UsesRandomExtractionCode
            ? ShareExtractionCodeBehavior.Random
            : ShareExtractionCodeBehavior.Fixed;
        var receipt = new ShareCreationReceipt(
            DateTimeOffset.UtcNow, candidate.ShareId, request, candidate.ShareId, candidate.ShareUrl,
            new EffectiveShareSettings(
                candidate.IsPermanent, candidate.IsAccessUnlimited, behavior, candidate.ExtractionCode));
        return IsExactAcceptedReceipt(request, receipt) ? receipt : null;
    }

    public bool IsExactAcceptedReceipt(PermanentShareRequest request, ShareCreationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(receipt);
        return receipt.Request.RemoteItems == request.RemoteItems &&
               receipt.Request.UsesRandomExtractionCode == request.UsesRandomExtractionCode &&
               string.Equals(receipt.Request.FixedExtractionCode, request.FixedExtractionCode, StringComparison.Ordinal) &&
               receipt.IsFinalAcceptance && !string.IsNullOrWhiteSpace(receipt.ExtractionCode);
    }

    private static BaiduAdapterResult<ShareDiscoverySnapshot> Failure(string code) =>
        BaiduAdapterResult<ShareDiscoverySnapshot>.Failure(
            BaiduErrorKind.UnknownResponse, code,
            submissionAcceptance: SubmissionAcceptance.DefinitelyNotAccepted);

    private static bool IsCompatible(BaiduShareRecord share, PermanentShareRequest request) =>
        share.RemoteItems == request.RemoteItems && share.IsPermanent && share.IsAccessUnlimited &&
        (request.UsesRandomExtractionCode
            ? !string.IsNullOrWhiteSpace(share.ExtractionCode)
            : string.Equals(share.ExtractionCode, request.FixedExtractionCode, StringComparison.Ordinal));

    private static bool Same(BaiduShareRecord left, BaiduShareRecord right) =>
        string.Equals(left.ShareId, right.ShareId, StringComparison.Ordinal) &&
        left.RemoteItems == right.RemoteItems &&
        string.Equals(left.ShareUrl, right.ShareUrl, StringComparison.Ordinal) &&
        string.Equals(left.ExtractionCode, right.ExtractionCode, StringComparison.Ordinal) &&
        left.IsPermanent == right.IsPermanent && left.IsAccessUnlimited == right.IsAccessUnlimited;
}
