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
    string? RemoteItemIdsJson)
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

    Task MarkConfirmedAbsentAsync(
        PendingReconciliationOperation expected,
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

public sealed class OperationReconciliationService
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

        BaiduAdapterResult<TransferConfirmation> result;
        try
        {
            result = await adapter.ConfirmTransferAsync(session, query, cancellationToken).ConfigureAwait(false);
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
        switch (result.Value)
        {
            case TransferConfirmation.ConfirmedPresent:
                await store.MarkConfirmedPresentAsync(pending, timeProvider.GetUtcNow(), cancellationToken)
                    .ConfigureAwait(false);
                return TransferConfirmation.ConfirmedPresent;
            case TransferConfirmation.ConfirmedAbsent:
                await store.MarkConfirmedAbsentAsync(pending, timeProvider.GetUtcNow(), cancellationToken)
                    .ConfigureAwait(false);
                return TransferConfirmation.ConfirmedAbsent;
            case TransferConfirmation.Ambiguous:
                return TransferConfirmation.Ambiguous;
            default:
                throw new InvalidOperationException("The adapter returned an unsupported confirmation result.");
        }
    }

    public async Task<ManualRetryResult> RetryAsync(
        Guid operationId,
        bool duplicateRiskAccepted,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation ID is required.", nameof(operationId));
        if (!duplicateRiskAccepted)
            return new ManualRetryResult(true, false, DuplicateRiskWarning);

        await store.ResetPendingForUserRetryAsync(operationId, timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        return new ManualRetryResult(false, true, DuplicateRiskWarning);
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
