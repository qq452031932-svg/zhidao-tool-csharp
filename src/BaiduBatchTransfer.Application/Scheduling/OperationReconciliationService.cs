using System.Text.Json;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Scheduling;

public sealed record PendingReconciliationOperation(
    Guid OperationId,
    Guid AccountId,
    int AttemptCount,
    string ConfirmationDescriptorJson,
    string? AdapterRequestId,
    string? RemoteItemIdsJson,
    bool RequiresExactShareEvidence = false,
    Guid BatchId = default,
    ShareReference? SourceShare = null,
    ExecutionProfileSnapshot? PersistedExecutionProfile = null,
    bool AllowsOneAutomaticRetry = false,
    int UnknownResubmissionCount = 0)
{
    public ExecutionProfileSnapshot ExecutionProfile =>
        PersistedExecutionProfile ?? ExecutionProfileSnapshot.LegacyForAccount(AccountId);

    public override string ToString() =>
        "PendingReconciliationOperation { EntityIds = [REDACTED], Confirmation = [REDACTED] }";
}

public interface IOperationReconciliationStore
{
    Task<PendingReconciliationOperation?> LoadPendingAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);

    Task MarkConfirmedPresentAsync(
        PendingReconciliationOperation expected,
        DateTimeOffset confirmedAt,
        CancellationToken cancellationToken = default);

    Task MarkConfirmedPresentWithEvidenceAsync(
        PendingReconciliationOperation expected,
        RemoteItemIdentitySet exactRemoteItems,
        DateTimeOffset confirmedAt,
        CancellationToken cancellationToken = default);

    Task MarkConfirmedAbsentAsync(
        PendingReconciliationOperation expected,
        SubmissionAcceptance submissionAcceptance,
        DateTimeOffset confirmedAt,
        CancellationToken cancellationToken = default);

    Task ResetPendingForUserRetryAsync(
        Guid operationId,
        DateTimeOffset resetAt,
        CancellationToken cancellationToken = default);

    Task ResolveAmbiguousResultAsync(
        PendingReconciliationOperation expected,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed record ManualRetryResult(
    bool RequiresConfirmation,
    bool ResetForRetry,
    string Warning);

public interface ITransferResultReconciler
{
    Task<TransferConfirmation> ReconcileAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);
}

public interface IPendingTransferReconciliationSource
{
    Task<Guid?> TryGetPendingDirectOperationAsync(
        CancellationToken cancellationToken = default);
}

public sealed class OperationReconciliationService : ITransferResultReconciler
{
    public const string DuplicateRiskWarning = "此前转存结果无法确认，重试可能产生重复资源。";
    private const int MaximumPersistedJsonLength = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 8 };
    private readonly IBaiduNetdiskAdapter? adapter;
    private readonly IBaiduSessionStore? sessionStore;
    private readonly ExecutionBoundTransferAdapter? boundAdapter;
    private readonly IBaiduExecutionContextResolver? contextResolver;
    private readonly IOperationReconciliationStore store;
    private readonly TimeProvider timeProvider;

    public OperationReconciliationService(
        IBaiduNetdiskAdapter adapter,
        IBaiduSessionStore sessionStore,
        IOperationReconciliationStore store,
        TimeProvider? timeProvider = null)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public OperationReconciliationService(
        ExecutionBoundTransferAdapter adapter,
        IBaiduExecutionContextResolver contextResolver,
        IOperationReconciliationStore store,
        TimeProvider? timeProvider = null)
    {
        boundAdapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.contextResolver = contextResolver ?? throw new ArgumentNullException(nameof(contextResolver));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<TransferConfirmation> ReconcileAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation ID is required.", nameof(operationId));
        var pending = await store.LoadPendingAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The result-pending operation no longer exists.");
        var query = BuildQuery(pending);
        AccountSession? session = null;
        BaiduExecutionContext? execution = null;
        if (boundAdapter is not null)
        {
            if (pending.BatchId == Guid.Empty || pending.SourceShare is null)
                return await ResolveAmbiguousAsync(pending, cancellationToken).ConfigureAwait(false);
            var resolution = await contextResolver!.ResolveAsync(
                pending.AccountId, pending.ExecutionProfile, cancellationToken).ConfigureAwait(false);
            if (resolution.Status != BaiduExecutionResolutionStatus.Ready)
                return await ResolveAmbiguousAsync(pending, cancellationToken).ConfigureAwait(false);
            execution = resolution.Context!;
        }
        else
        {
            session = await sessionStore!.LoadAsync(pending.AccountId.ToString("D"), cancellationToken)
                .ConfigureAwait(false);
            if (session is null) return TransferConfirmation.Ambiguous;
        }

        if (boundAdapter is null && !pending.RequiresExactShareEvidence &&
            adapter is not IOperationBoundBaiduNetdiskAdapter)
        {
            BaiduAdapterResult<TransferConfirmation> legacyResult;
            try
            {
                legacyResult = await adapter!.ConfirmTransferAsync(session!, query, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return await ResolveAmbiguousAsync(pending, cancellationToken).ConfigureAwait(false);
            }

            if (!legacyResult.IsSuccess)
                return await ResolveAmbiguousAsync(pending, cancellationToken).ConfigureAwait(false);
            switch (legacyResult.Value)
            {
                case TransferConfirmation.ConfirmedPresent:
                    await store.MarkConfirmedPresentAsync(
                        pending,
                        timeProvider.GetUtcNow(),
                        cancellationToken).ConfigureAwait(false);
                    return TransferConfirmation.ConfirmedPresent;
                case TransferConfirmation.ConfirmedAbsent when
                    legacyResult.SubmissionAcceptance == SubmissionAcceptance.DefinitelyNotAccepted:
                    await store.MarkConfirmedAbsentAsync(
                        pending,
                        legacyResult.SubmissionAcceptance,
                        timeProvider.GetUtcNow(),
                        cancellationToken).ConfigureAwait(false);
                    return TransferConfirmation.ConfirmedAbsent;
                case TransferConfirmation.ConfirmedAbsent:
                case TransferConfirmation.Ambiguous:
                    return await ResolveAmbiguousAsync(pending, cancellationToken).ConfigureAwait(false);
                default:
                    throw new InvalidOperationException("The adapter returned an unsupported confirmation result.");
            }
        }

        BaiduAdapterResult<TransferConfirmationResult> result;
        try
        {
            if (boundAdapter is not null)
            {
                result = await boundAdapter.ConfirmExactTransferForOperationAsync(
                    new TransferOperationAutomationContext(
                        pending.OperationId,
                        pending.BatchId,
                        pending.AccountId,
                        pending.AttemptCount,
                        pending.SourceShare!,
                        IsRecovery: true,
                        PersistedExecutionProfile: pending.ExecutionProfile).Validate(),
                    execution!,
                    query,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (adapter is ISourceAwareBaiduTransferConfirmationAdapter sourceAware &&
                pending.SourceShare is not null)
            {
                result = await sourceAware.ConfirmExactTransferAsync(
                    session!, pending.SourceShare, query, cancellationToken).ConfigureAwait(false);
            }
            else if (adapter is IOperationBoundBaiduNetdiskAdapter operationBound)
            {
                if (pending.BatchId == Guid.Empty || pending.SourceShare is null)
                    return await ResolveAmbiguousAsync(pending, cancellationToken).ConfigureAwait(false);
                result = await operationBound.ConfirmExactTransferForOperationAsync(
                    new TransferOperationAutomationContext(
                        pending.OperationId,
                        pending.BatchId,
                        pending.AccountId,
                        pending.AttemptCount,
                        pending.SourceShare,
                        IsRecovery: true,
                        PersistedExecutionProfile: pending.ExecutionProfile).Validate(),
                    session!,
                    query,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result = await adapter!.ConfirmExactTransferAsync(session!, query, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await ResolveAmbiguousAsync(pending, cancellationToken).ConfigureAwait(false);
        }

        if (!result.IsSuccess && boundAdapter is not null &&
            result.ErrorKind == BaiduErrorKind.NotValidated &&
            result.SubmissionAcceptance == SubmissionAcceptance.DefinitelyNotAccepted)
            return TransferConfirmation.Ambiguous;
        if (!result.IsSuccess)
            return await ResolveAmbiguousAsync(pending, cancellationToken).ConfigureAwait(false);
        switch (result.Value!.State)
        {
            case TransferConfirmationState.ExactPresent:
                await store.MarkConfirmedPresentWithEvidenceAsync(
                    pending,
                    result.Value.RemoteItems!,
                    timeProvider.GetUtcNow(),
                    cancellationToken)
                    .ConfigureAwait(false);
                return TransferConfirmation.ConfirmedPresent;
            case TransferConfirmationState.Absent when
                result.SubmissionAcceptance == SubmissionAcceptance.DefinitelyNotAccepted:
                await store.MarkConfirmedAbsentAsync(
                    pending,
                    result.SubmissionAcceptance,
                    timeProvider.GetUtcNow(),
                    cancellationToken)
                    .ConfigureAwait(false);
                return TransferConfirmation.ConfirmedAbsent;
            case TransferConfirmationState.Absent:
            case TransferConfirmationState.Ambiguous:
                return await ResolveAmbiguousAsync(pending, cancellationToken).ConfigureAwait(false);
            default:
                throw new InvalidOperationException("The adapter returned an unsupported confirmation result.");
        }
    }

    public Task<ManualRetryResult> RetryAsync(
        Guid operationId,
        bool duplicateRiskAccepted,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation ID is required.", nameof(operationId));
        _ = duplicateRiskAccepted;
        return Task.FromResult(new ManualRetryResult(true, false, DuplicateRiskWarning));
    }

    private async Task<TransferConfirmation> ResolveAmbiguousAsync(
        PendingReconciliationOperation pending,
        CancellationToken cancellationToken)
    {
        if (pending.AllowsOneAutomaticRetry)
        {
            await store.ResolveAmbiguousResultAsync(
                pending,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        }
        return TransferConfirmation.Ambiguous;
    }

    private static TransferConfirmationQuery BuildQuery(PendingReconciliationOperation pending)
    {
        if (pending.ConfirmationDescriptorJson.Length > MaximumPersistedJsonLength)
            throw new InvalidDataException("The persisted confirmation descriptor is too large.");
        try
        {
            var value = JsonSerializer.Deserialize<PersistedDescriptor>(
                pending.ConfirmationDescriptorJson,
                JsonOptions) ?? throw new InvalidDataException("The persisted confirmation descriptor is empty.");
            var descriptor = new OperationConfirmationDescriptor(
                value.SourceShareId,
                new RemotePath(value.TargetPath),
                value.ExpectedOuterNameFingerprint,
                value.OperationFingerprint,
                value.SubmitWindowStartUtc,
                value.SubmitWindowEndUtc);
            return descriptor.WithResponseIdentifiers(
                pending.AdapterRequestId,
                ParseRemoteItemIds(pending.RemoteItemIdsJson)).ToQuery();
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException("The persisted confirmation descriptor is invalid.", exception);
        }
    }

    private static IReadOnlyList<string> ParseRemoteItemIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        if (json.Length > MaximumPersistedJsonLength)
            throw new InvalidDataException("The persisted response identifiers are too large.");
        return JsonSerializer.Deserialize<string[]>(json, JsonOptions)
            ?? throw new InvalidDataException("The persisted response identifiers are invalid.");
    }

    private sealed record PersistedDescriptor(
        string SourceShareId,
        string TargetPath,
        string ExpectedOuterNameFingerprint,
        string OperationFingerprint,
        DateTimeOffset SubmitWindowStartUtc,
        DateTimeOffset SubmitWindowEndUtc);
}
