namespace BaiduBatchTransfer.Core.Abstractions;

public enum RemoteSelectionSessionState
{
    Active,
    Confirmed,
    Cancelled,
    NeedsDecision,
}

public sealed record RemoteSelectionSession
{
    public RemoteSelectionSession(
        Guid sessionId,
        Guid workspaceDraftId,
        Guid accountId,
        int generation,
        RemoteSelectionSessionState state,
        long revision,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("A session is required.", nameof(sessionId));
        if (workspaceDraftId == Guid.Empty) throw new ArgumentException("A workspace draft is required.", nameof(workspaceDraftId));
        if (accountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(accountId));
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        if (updatedAt < createdAt) throw new ArgumentOutOfRangeException(nameof(updatedAt));
        SessionId = sessionId;
        WorkspaceDraftId = workspaceDraftId;
        AccountId = accountId;
        Generation = generation;
        State = state;
        Revision = revision;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public Guid SessionId { get; }
    public Guid WorkspaceDraftId { get; }
    public Guid AccountId { get; }
    public int Generation { get; }
    public RemoteSelectionSessionState State { get; }
    public long Revision { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; }
}

public interface IRemoteSelectionSessionStore
{
    Task<RemoteSelectionSession> CreateAsync(
        Guid accountId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<RemoteSelectionSession?> GetAsync(
        Guid sessionId,
        Guid workspaceDraftId,
        Guid accountId,
        int generation,
        CancellationToken cancellationToken = default);

    Task<bool> TryCompleteAsync(
        Guid sessionId,
        Guid workspaceDraftId,
        Guid accountId,
        int generation,
        long expectedRevision,
        RemoteSelectionSessionState terminalState,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<int> RecoverIncompleteAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
