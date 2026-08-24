using BaiduBatchTransfer.Core.Domain;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace BaiduBatchTransfer.Core.Abstractions;

public enum StandaloneShareBatchStatus
{
    Queued,
    Active,
    Paused,
    Completed,
    CompletedWithFailures,
    Cancelled,
}

public enum StandaloneShareOperationStatus
{
    Queued,
    Reserved,
    Preparing,
    Submitting,
    ResultPending,
    Succeeded,
    Failed,
    Paused,
    Cancelled,
}

public enum StandaloneShareSubmissionState
{
    NotStarted,
    Prepared,
    DefinitelyNotAccepted,
    Accepted,
    ResultPending,
    Completed,
}

public sealed record StandaloneShareBatchRecord
{
    public StandaloneShareBatchRecord(
        Guid id,
        string resultDirectoryReference,
        StandaloneShareBatchStatus status,
        int operationCount,
        long revision,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty) throw new ArgumentException("A batch id is required.", nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(resultDirectoryReference);
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        if (operationCount <= 0) throw new ArgumentOutOfRangeException(nameof(operationCount));
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        if (updatedAt < createdAt) throw new ArgumentOutOfRangeException(nameof(updatedAt));

        Id = id;
        ResultDirectoryReference = resultDirectoryReference.Trim();
        Status = status;
        OperationCount = operationCount;
        Revision = revision;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; }
    public string ResultDirectoryReference { get; }
    public StandaloneShareBatchStatus Status { get; }
    public int OperationCount { get; }
    public long Revision { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; }
    public static StandaloneShareBatchRecord Create(
        Guid id,
        string resultDirectoryReference,
        int operationCount,
        DateTimeOffset createdAt) =>
        new(id, resultDirectoryReference, StandaloneShareBatchStatus.Queued, operationCount, 0, createdAt, createdAt);

    public override string ToString() =>
        $"StandaloneShareBatchRecord {{ Id = {Id}, Status = {Status}, OperationCount = {OperationCount}, Revision = {Revision}, ResultDirectory = [REDACTED] }}";
}

public sealed record StandaloneShareOperationRecord
{
    public StandaloneShareOperationRecord(
        Guid id,
        Guid batchId,
        Guid accountId,
        RemoteItemIdentitySet exactRemoteItems,
        string displayName,
        RemoteResourceKind kind,
        RemotePath parentPath,
        int sequenceNumber,
        int generation,
        StandaloneShareOperationStatus status,
        StandaloneShareSubmissionState submissionState,
        long revision,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty) throw new ArgumentException("An operation id is required.", nameof(id));
        if (batchId == Guid.Empty) throw new ArgumentException("A batch id is required.", nameof(batchId));
        if (accountId == Guid.Empty) throw new ArgumentException("An owning account is required.", nameof(accountId));
        ArgumentNullException.ThrowIfNull(exactRemoteItems);
        if (exactRemoteItems.Count != 1)
            throw new ArgumentException("Exactly one remote item is required.", nameof(exactRemoteItems));
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentNullException.ThrowIfNull(parentPath);
        if (sequenceNumber <= 0) throw new ArgumentOutOfRangeException(nameof(sequenceNumber));
        if (generation <= 0) throw new ArgumentOutOfRangeException(nameof(generation));
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        if (!Enum.IsDefined(submissionState)) throw new ArgumentOutOfRangeException(nameof(submissionState));
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        if (updatedAt < createdAt) throw new ArgumentOutOfRangeException(nameof(updatedAt));

        Id = id;
        BatchId = batchId;
        AccountId = accountId;
        ExactRemoteItems = exactRemoteItems;
        DisplayName = displayName.Trim();
        Kind = kind;
        ParentPath = parentPath;
        SequenceNumber = sequenceNumber;
        Generation = generation;
        Status = status;
        SubmissionState = submissionState;
        Revision = revision;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; }
    public Guid BatchId { get; }
    public Guid AccountId { get; }
    public RemoteItemIdentitySet ExactRemoteItems { get; }
    public string DisplayName { get; }
    public RemoteResourceKind Kind { get; }
    public RemotePath ParentPath { get; }
    public int SequenceNumber { get; }
    public int Generation { get; }
    public StandaloneShareOperationStatus Status { get; }
    public StandaloneShareSubmissionState SubmissionState { get; }
    public long Revision { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; }
    public SubmissionAcceptance SubmissionAcceptance { get; init; } =
        BaiduBatchTransfer.Core.Domain.SubmissionAcceptance.Unknown;
    public string? BaselineFingerprint { get; init; }
    public int? BaselineShareCount { get; init; }
    public DateTimeOffset? BaselineCompletedAt { get; init; }
    public long LeaseGeneration { get; init; }
    public DateTimeOffset? LeaseExpiresAt { get; init; }
    public string? LastErrorCode { get; init; }

    public bool CanSafelyRetry => Status == StandaloneShareOperationStatus.Failed &&
        SubmissionState is StandaloneShareSubmissionState.NotStarted or
            StandaloneShareSubmissionState.DefinitelyNotAccepted;

    public static StandaloneShareOperationRecord Create(
        Guid id,
        Guid batchId,
        OwnedRemoteSelection selection,
        int sequenceNumber,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return new(
            id,
            batchId,
            selection.AccountId,
            selection.RemoteItems,
            selection.DisplayName,
            selection.Kind,
            selection.ParentPath,
            sequenceNumber,
            1,
            StandaloneShareOperationStatus.Queued,
            StandaloneShareSubmissionState.NotStarted,
            0,
            createdAt,
            createdAt);
    }

    public override string ToString() =>
        $"StandaloneShareOperationRecord {{ Id = {Id}, BatchId = {BatchId}, AccountId = {AccountId}, SequenceNumber = {SequenceNumber}, Generation = {Generation}, Status = {Status}, SubmissionState = {SubmissionState}, SubmissionAcceptance = {SubmissionAcceptance}, ExactItems = [REDACTED], Baseline = [REDACTED], Lease = [REDACTED], Error = [REDACTED], DisplayName = [REDACTED], ParentPath = [REDACTED] }}";
}

public sealed record StandaloneShareOperationLease(
    Guid OperationId,
    Guid BatchId,
    Guid AccountId,
    int Generation,
    long LeaseGeneration,
    long Revision,
    DateTimeOffset LeaseExpiresAt)
{
    public override string ToString() =>
        $"StandaloneShareOperationLease {{ OperationId = {OperationId}, BatchId = {BatchId}, AccountId = {AccountId}, Generation = {Generation}, LeaseGeneration = {LeaseGeneration}, Revision = {Revision}, LeaseExpiresAt = [REDACTED] }}";
}

public sealed record StandaloneShareResultRecord(
    Guid Id,
    Guid OperationId,
    Guid BatchId,
    Guid AccountId,
    int Generation,
    string ExactItemSetFingerprint,
    string SettingsFingerprint,
    string ShareId,
    string ShareUrl,
    string ExtractionCode,
    bool IsPermanent,
    bool IsAccessUnlimited,
    DateTimeOffset AcceptedAt,
    DateTimeOffset CreatedAt)
{
    public override string ToString() =>
        $"StandaloneShareResultRecord {{ Id = {Id}, OperationId = {OperationId}, BatchId = {BatchId}, AccountId = {AccountId}, Generation = {Generation}, ExactItems = [REDACTED], Settings = [REDACTED], ShareId = [REDACTED], ShareUrl = [REDACTED], ExtractionCode = [REDACTED], IsPermanent = {IsPermanent}, IsAccessUnlimited = {IsAccessUnlimited} }}";
}

public enum StandaloneShareMutationDisposition
{
    Applied,
    NotFound,
    Conflict,
    InvalidState,
    Unsafe,
}

public sealed record StandaloneShareMutationResult<T>(
    StandaloneShareMutationDisposition Disposition,
    T? Value)
    where T : class;

public sealed record StandaloneSharePendingBatchEnvelope
{
    public StandaloneSharePendingBatchEnvelope(
        string draftFingerprint,
        StandaloneShareBatchRecord batch,
        IReadOnlyList<StandaloneShareOperationRecord> operations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draftFingerprint);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(operations);
        var snapshot = operations.ToImmutableArray();
        ValidateGraph(batch, snapshot);
        var computed = ComputeDraftFingerprint(batch, snapshot);
        if (!string.Equals(draftFingerprint, computed, StringComparison.Ordinal))
            throw new ArgumentException("The draft fingerprint does not match the batch contents.", nameof(draftFingerprint));
        DraftFingerprint = draftFingerprint;
        Batch = batch;
        Operations = snapshot;
    }

    public string DraftFingerprint { get; }
    public StandaloneShareBatchRecord Batch { get; }
    public ImmutableArray<StandaloneShareOperationRecord> Operations { get; }

    public static StandaloneSharePendingBatchEnvelope Create(
        StandaloneShareBatchRecord batch,
        IReadOnlyList<StandaloneShareOperationRecord> operations)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(operations);
        return new(ComputeDraftFingerprint(batch, operations), batch, operations);
    }

    internal static void ValidateGraph(
        StandaloneShareBatchRecord batch,
        IReadOnlyList<StandaloneShareOperationRecord> operations)
    {
        if (operations.Count != batch.OperationCount)
            throw new ArgumentException("The operation count does not match the batch.", nameof(operations));
        if (operations.Select(operation => operation.Id).Distinct().Count() != operations.Count)
            throw new ArgumentException("Operation ids must be unique.", nameof(operations));
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (operation.BatchId != batch.Id || operation.SequenceNumber != index + 1 ||
                operation.ExactRemoteItems.Count != 1)
            {
                throw new ArgumentException("The operation graph does not match the batch.", nameof(operations));
            }
        }
    }

    private static string ComputeDraftFingerprint(
        StandaloneShareBatchRecord batch,
        IReadOnlyList<StandaloneShareOperationRecord> operations)
    {
        ValidateGraph(batch, operations);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), leaveOpen: true))
        {
            writer.Write(batch.ResultDirectoryReference);
            writer.Write(operations.Count);
            foreach (var operation in operations.OrderBy(item => item.SequenceNumber))
            {
                writer.Write(operation.AccountId.ToByteArray());
                writer.Write(operation.ExactRemoteItems.Fingerprint);
                writer.Write(operation.DisplayName);
                writer.Write((int)operation.Kind);
                writer.Write(operation.ParentPath.Value);
            }
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }
}

public sealed record StandaloneShareBatchCreationReceipt
{
    public StandaloneShareBatchCreationReceipt(
        string draftFingerprint,
        StandaloneShareBatchRecord batch,
        IReadOnlyList<StandaloneShareOperationRecord> operations,
        bool alreadyExisted)
    {
        var envelope = new StandaloneSharePendingBatchEnvelope(draftFingerprint, batch, operations);
        DraftFingerprint = envelope.DraftFingerprint;
        Batch = envelope.Batch;
        Operations = envelope.Operations;
        AlreadyExisted = alreadyExisted;
    }

    public string DraftFingerprint { get; }
    public StandaloneShareBatchRecord Batch { get; }
    public ImmutableArray<StandaloneShareOperationRecord> Operations { get; }
    public bool AlreadyExisted { get; }
}

public sealed record StandaloneShareBatchSnapshot
{
    public StandaloneShareBatchSnapshot(
        StandaloneShareBatchRecord batch,
        IReadOnlyList<StandaloneShareOperationRecord> operations)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(operations);
        var snapshot = operations.ToImmutableArray();
        StandaloneSharePendingBatchEnvelope.ValidateGraph(batch, snapshot);
        Batch = batch;
        Operations = snapshot;
    }

    public StandaloneShareBatchRecord Batch { get; }
    public ImmutableArray<StandaloneShareOperationRecord> Operations { get; }
}
