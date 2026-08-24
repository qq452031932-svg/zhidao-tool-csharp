using BaiduBatchTransfer.Core.Domain;

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
        $"StandaloneShareOperationRecord {{ Id = {Id}, BatchId = {BatchId}, AccountId = {AccountId}, SequenceNumber = {SequenceNumber}, Generation = {Generation}, Status = {Status}, SubmissionState = {SubmissionState}, ExactItems = [REDACTED], DisplayName = [REDACTED] }}";
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
