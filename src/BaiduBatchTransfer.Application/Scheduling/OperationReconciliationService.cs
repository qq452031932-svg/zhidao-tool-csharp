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
    bool RequiresExactShareEvidence = false)
{
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

public sealed class OperationReconciliationService : ITransferResultReconciler
{
    public const string DuplicateRiskWarning = "此前转存结果无法确认，重试可能产生重复资源。";
    private const int MaximumPersistedJsonLength = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 8 };
    private readonly IBaiduNetdiskAdapter adapter;
    private readonly IBaiduSessionStore sessionStore;
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

    public async Task<TransferConfirmation> ReconcileAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation ID is required.", nameof(operationId));
        var pending = await store.LoadPendingAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The result-pending operation no longer exists.");
        var query = BuildQuery(pending);
        var session = await sessionStore.LoadAsync(pending.AccountId.ToString("D"), cancellationToken)
            .ConfigureAwait(false);
        if (session is null) return TransferConfirmation.Ambiguous;

        if (!pending.RequiresExactShareEvidence)
        {
            BaiduAdapterResult<TransferConfirmation> legacyResult;
            try
            {
                legacyResult = await adapter.ConfirmTransferAsync(session, query, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return TransferConfirmation.Ambiguous;
            }

            if (!legacyResult.IsSuccess) return TransferConfirmation.Ambiguous;
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
                    return TransferConfirmation.Ambiguous;
                default:
                    throw new InvalidOperationException("The adapter returned an unsupported confirmation result.");
            }
        }

        BaiduAdapterResult<TransferConfirmationResult> result;
        try
        {
            result = await adapter.ConfirmExactTransferAsync(session, query, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return TransferConfirmation.Ambiguous;
        }

        if (!result.IsSuccess) return TransferConfirmation.Ambiguous;
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
                return TransferConfirmation.Ambiguous;
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
