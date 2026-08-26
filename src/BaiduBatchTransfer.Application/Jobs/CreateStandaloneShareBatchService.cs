using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Jobs;

public interface IStandaloneShareBatchNotifications
{
    void Notify(Guid batchId);
}

public sealed class CreateStandaloneShareBatchService(
    IStandaloneShareStore store,
    IStandaloneShareBatchNotifications notifications)
{
    private readonly IStandaloneShareStore store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IStandaloneShareBatchNotifications notifications =
        notifications ?? throw new ArgumentNullException(nameof(notifications));

    public StandaloneSharePendingBatchEnvelope BuildDraft(
        Guid batchId,
        IReadOnlyList<Guid> operationIds,
        string resultDirectoryReference,
        IReadOnlyList<OwnedRemoteSelection> selections,
        ShareExtractionCodeBehavior extractionCodeBehavior,
        string? fixedExtractionCode,
        DateTimeOffset createdAt,
        ExistingShareDecision existingShareDecision = ExistingShareDecision.Reuse)
    {
        if (batchId == Guid.Empty) throw new ArgumentException("A batch id is required.", nameof(batchId));
        ArgumentNullException.ThrowIfNull(operationIds);
        ArgumentNullException.ThrowIfNull(selections);
        if (selections.Count == 0 || operationIds.Count != selections.Count ||
            operationIds.Any(id => id == Guid.Empty) || operationIds.Distinct().Count() != operationIds.Count)
            throw new ArgumentException("Each selection requires one unique operation id.", nameof(operationIds));

        var batch = StandaloneShareBatchRecord.Create(
            batchId, resultDirectoryReference, selections.Count, createdAt);
        var operations = selections.Select((selection, index) =>
            StandaloneShareOperationRecord.Create(
                operationIds[index], batchId, selection, index + 1, createdAt,
                extractionCodeBehavior, fixedExtractionCode, existingShareDecision,
                ExecutionProfileSnapshot.LegacyForAccount(selection.AccountId))).ToArray();
        return StandaloneSharePendingBatchEnvelope.Create(batch, operations);
    }

    public async Task<StandaloneShareBatchCreationReceipt> CreateAsync(
        StandaloneSharePendingBatchEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var existing = await store.GetBatchCreationAsync(
            envelope.Batch.Id, envelope.DraftFingerprint, cancellationToken).ConfigureAwait(false);
        var receipt = existing ?? await store.CreateOrGetBatchAsync(envelope, cancellationToken).ConfigureAwait(false);
        notifications.Notify(receipt.Batch.Id);
        return receipt;
    }

    public void NotifyExistingBatch(Guid batchId)
    {
        if (batchId == Guid.Empty) throw new ArgumentException("A batch id is required.", nameof(batchId));
        notifications.Notify(batchId);
    }
}
