using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Domain;
using System.Diagnostics;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

internal sealed record ManagedTransferPickerOpenResult(
    ManagedBrowserTransferPickerResultKind Kind, BaiduTransferCandidateObservation? Observation = null)
{
    public override string ToString() => $"ManagedTransferPickerOpenResult {{ Kind = {Kind}, Observation = [REDACTED] }}";
}

/// <summary>
/// Internal integration primitive, not a production session. Caller must retain its account lease,
/// exact process/target ownership and engine binding until this call and final verification finish.
/// OutcomeUnknown requires account quarantine/reconciliation before that ownership may be reused.
/// This primitive never releases the caller's fence or retries a dispatched mutation.
/// Its optional fixed revalidation runs under the acquired permit immediately before dispatch.
/// </summary>
internal sealed class ManagedTransferPickerOpener
{
    private readonly Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send;
    private readonly IBaiduCandidateActionAuthorizationSource authorizationSource;
    private readonly Func<CancellationToken, Task<bool>> preDispatchValidation;
    private readonly TimeProvider timeProvider;
    internal ManagedTransferPickerOpener(
        Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send,
        IBaiduCandidateActionAuthorizationSource authorizationSource,
        Func<CancellationToken, Task<bool>>? preDispatchValidation = null,
        TimeProvider? timeProvider = null)
    {
        this.send = send ?? throw new ArgumentNullException(nameof(send));
        this.authorizationSource = authorizationSource ?? throw new ArgumentNullException(nameof(authorizationSource));
        this.preDispatchValidation = preDispatchValidation ?? (static _ => Task.FromResult(true));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal async Task<ManagedTransferPickerOpenResult> OpenAsync(BaiduCandidateDomMutationRequest request,
        ManagedBrowserOpenTransferSourceRequest binding, BaiduTransferCandidateObservation source,
        CancellationToken cancellationToken) =>
        await OpenCoreAsync(request, binding, source, AdapterWriteAction.EnsureDirectory,
            cancellationToken).ConfigureAwait(false);

    internal async Task<ManagedTransferPickerOpenResult> OpenForSubmissionAsync(
        BaiduCandidateDomMutationRequest request,
        ManagedBrowserOpenTransferSourceRequest binding,
        BaiduTransferCandidateObservation source,
        CancellationToken cancellationToken = default) =>
        await OpenCoreAsync(request, binding, source, AdapterWriteAction.SubmitTransfer,
            cancellationToken).ConfigureAwait(false);

    private async Task<ManagedTransferPickerOpenResult> OpenCoreAsync(
        BaiduCandidateDomMutationRequest request,
        ManagedBrowserOpenTransferSourceRequest binding,
        BaiduTransferCandidateObservation source,
        AdapterWriteAction requiredStage,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Denied(ManagedBrowserTransferPickerResultKind.Cancelled);
        ManagedBrowserSourceSnapshot snapshot;
        try
        {
            if (!Matches(request, binding, source, requiredStage, timeProvider.GetUtcNow()))
                return Denied(ManagedBrowserTransferPickerResultKind.PageNotValidated);
            snapshot = new(source.BaiduUserId, source.GenerationToken, source.TotalCount,
                source.ReachedEnd, source.IsStable, source.Items.Select(item => new ManagedBrowserSourceItem(item.ExactId,
                    item.Kind == RemoteResourceKind.File ? ManagedBrowserSourceItemKind.File : ManagedBrowserSourceItemKind.Folder,
                    item.ServerName)), source.ObservationToken);
            _ = new ManagedBrowserOpenTransferPickerRequest(binding, snapshot);
        }
        catch (ArgumentException) { return Denied(ManagedBrowserTransferPickerResultKind.PageNotValidated); }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(binding.TimeoutMilliseconds);
        var elapsed = Stopwatch.StartNew();
        var dispatched = false;
        IBaiduCandidateMutationPermit? permit = null;
        var result = await ExecuteAsync().ConfigureAwait(false);
        try
        {
            if (permit is not null) await permit.DisposeAsync().ConfigureAwait(false);
            RequireBudget();
        }
        catch (Exception)
        {
            return Denied(dispatched ? ManagedBrowserTransferPickerResultKind.OutcomeUnknown :
                cancellationToken.IsCancellationRequested ? ManagedBrowserTransferPickerResultKind.Cancelled :
                elapsed.Elapsed.TotalMilliseconds >= binding.TimeoutMilliseconds || deadline.IsCancellationRequested
                    ? ManagedBrowserTransferPickerResultKind.TimedOut : ManagedBrowserTransferPickerResultKind.PageNotValidated);
        }
        return result;

        async Task<ManagedTransferPickerOpenResult> ExecuteAsync()
        {
            try
            {
                permit = await authorizationSource.TryAcquireTransferMutationPermitAsync(request, deadline.Token).ConfigureAwait(false);
                RequireBudget();
                if (permit is null) return Denied(ManagedBrowserTransferPickerResultKind.PageNotValidated);
                if (!await preDispatchValidation(deadline.Token).ConfigureAwait(false))
                    return Denied(ManagedBrowserTransferPickerResultKind.PageNotValidated);
                RequireBudget();
                var auth = request.Authorization;
                var command = new ManagedBrowserRequest(Guid.NewGuid(), auth.OperationId, auth.AccountId, auth.Generation,
                    ManagedBrowserAction.OpenTransferPicker, new ManagedBrowserOpenTransferPickerRequest(RemainingBinding(), snapshot));
                RequireBudget();
                dispatched = true;
                // No WaitAsync: cancellation must not release an in-flight mutation permit/account fence.
                var response = await send(command, deadline.Token).ConfigureAwait(false);
                RequireBudget();
                ManagedBrowserProtocol.ValidateResponseFor(command, response);
                if (response.Result is not ManagedBrowserTransferPickerResult { Kind: ManagedBrowserTransferPickerResultKind.PickerOpened })
                    return Denied(ManagedBrowserTransferPickerResultKind.OutcomeUnknown);
                var reader = new ManagedTransferDirectoryReader(SendReadAsync);
                var directory = await reader.ReadAsync(auth.OperationId, auth.AccountId, auth.Generation,
                    new ManagedBrowserReadTransferDirectoryRequest(RemainingBinding(), null, null), deadline.Token).ConfigureAwait(false);
                RequireBudget();
                return directory.Kind == ManagedBrowserDirectoryListingResultKind.Observed && directory.Observation is not null
                    ? new(ManagedBrowserTransferPickerResultKind.PickerOpened, directory.Observation)
                    : Denied(ManagedBrowserTransferPickerResultKind.OutcomeUnknown);
            }
            catch (Exception)
            {
                // No exception details or partial observations cross this boundary, and never infer a safe retry.
                return Denied(dispatched ? ManagedBrowserTransferPickerResultKind.OutcomeUnknown :
                    cancellationToken.IsCancellationRequested ? ManagedBrowserTransferPickerResultKind.Cancelled :
                    elapsed.Elapsed.TotalMilliseconds >= binding.TimeoutMilliseconds || deadline.IsCancellationRequested
                        ? ManagedBrowserTransferPickerResultKind.TimedOut : ManagedBrowserTransferPickerResultKind.PageNotValidated);
            }
        }

        void RequireBudget()
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (elapsed.Elapsed.TotalMilliseconds >= binding.TimeoutMilliseconds) throw new TimeoutException();
        }
        ManagedBrowserOpenTransferSourceRequest RemainingBinding()
        {
            RequireBudget();
            return new(binding.BrowserId, binding.TargetId, binding.DevToolsEndpoint,
                Math.Max(1, (int)Math.Ceiling(binding.TimeoutMilliseconds - elapsed.Elapsed.TotalMilliseconds)),
                binding.SourceUri, binding.ExpectedBaiduUserId);
        }
        async Task<ManagedBrowserResponse> SendReadAsync(ManagedBrowserRequest read, CancellationToken token)
        {
            RequireBudget();
            var result = await send(read, token).ConfigureAwait(false);
            RequireBudget();
            return result;
        }
    }

    private static bool Matches(BaiduCandidateDomMutationRequest request,
        ManagedBrowserOpenTransferSourceRequest binding, BaiduTransferCandidateObservation source,
        AdapterWriteAction requiredStage, DateTimeOffset now)
    {
        if (request is null || binding is null || source is null) return false;
        var auth = request.Authorization;
        var grant = request.Binding;
        var read = request.ReadBinding;
        return request.Action == BaiduAutomationAction.OpenDestinationPicker &&
            request.TypedValue is null && request.TargetExactId is null &&
            auth.Capability.AdapterKind == "BaiduTransfer" && auth.Action == requiredStage &&
            requiredStage is AdapterWriteAction.EnsureDirectory or AdapterWriteAction.SubmitTransfer &&
            !auth.IsRecovery && !grant.IsRecovery && auth.AccountId != Guid.Empty && auth.OperationId != Guid.Empty &&
            auth.BatchId != Guid.Empty && auth.Generation > 0 && grant.IssuedAtUtc <= now &&
            grant.ExpiresAtUtc > now && grant.ExpiresAtUtc > grant.IssuedAtUtc &&
            auth.Capability == grant.Capability && auth.AccountId == grant.AccountId && auth.OperationId == grant.OperationId &&
            auth.Generation == grant.Generation && auth.BatchId == grant.BatchId &&
            read.OfficialUri.AbsoluteUri == binding.SourceUri && read.ExpectedBaiduUserId == binding.ExpectedBaiduUserId &&
            read.ExpectedPageState == BaiduCandidatePageState.ExternalShare && read.ExpectedParentExactId is null &&
            read.ExpectedCanonicalPath is null && source.PageState == BaiduCandidatePageState.ExternalShare &&
            source.BaiduUserId == binding.ExpectedBaiduUserId && source.ParentExactId is null && source.CanonicalPath is null &&
            !source.AncestorExactIds.IsDefault && source.AncestorExactIds.IsEmpty && source.IsComplete &&
            source.IsStable && source.ReachedEnd && !source.Items.IsDefaultOrEmpty && source.Items.Length <= 32 &&
            source.Items.All(item => item is not null && item.Kind is RemoteResourceKind.File or RemoteResourceKind.Folder) &&
            read.ExpectedExactIds.SequenceEqual(source.Items.Select(item => item.ExactId).Order(StringComparer.Ordinal)) &&
            source.OfficialRequestId is null && source.OfficialIdentityMapping is null;
    }
    private static ManagedTransferPickerOpenResult Denied(ManagedBrowserTransferPickerResultKind kind) => new(kind);
}
