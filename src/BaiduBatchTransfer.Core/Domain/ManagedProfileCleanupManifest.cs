namespace BaiduBatchTransfer.Core.Domain;

public enum ManagedProfileCleanupState
{
    Prepared = 1,
    QuarantineMoveIntent = 2,
    Quarantined = 3,
    DeleteIntent = 4,
    CleanupPending = 5,
    Deleted = 6
}

public sealed class ManagedProfileCleanupManifest
{
    public ManagedProfileCleanupManifest(
        Guid manifestId,
        Guid accountId,
        Guid sessionId,
        string sourceProfilePath,
        string quarantinePath,
        string expectedOwnershipId,
        string? confirmationTokenHash)
    {
        if (manifestId == Guid.Empty)
            throw new ArgumentException("A manifest ID is required.", nameof(manifestId));
        if (accountId == Guid.Empty)
            throw new ArgumentException("An account ID is required.", nameof(accountId));
        if (sessionId == Guid.Empty)
            throw new ArgumentException("A session ID is required.", nameof(sessionId));
        ManifestId = manifestId;
        AccountId = accountId;
        SessionId = sessionId;
        SourceProfilePath = RequireText(sourceProfilePath, nameof(sourceProfilePath), 1024);
        QuarantinePath = RequireText(quarantinePath, nameof(quarantinePath), 1024);
        ExpectedOwnershipId = RequireText(expectedOwnershipId, nameof(expectedOwnershipId), 256);
        ConfirmationTokenHash = confirmationTokenHash is null
            ? null
            : RequireText(confirmationTokenHash, nameof(confirmationTokenHash), 512);
    }

    public Guid ManifestId { get; }
    public Guid AccountId { get; }
    public Guid SessionId { get; }
    public string SourceProfilePath { get; }
    public string QuarantinePath { get; }
    public string ExpectedOwnershipId { get; }
    public string? ConfirmationTokenHash { get; }

    public override string ToString() =>
        "ManagedProfileCleanupManifest { Identity = [REDACTED], Paths = [REDACTED], " +
        $"HasConfirmation = {ConfirmationTokenHash is not null} }}";

    private static string RequireText(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value != value.Trim() || value.Length > maximumLength || value.Any(char.IsControl))
            throw new ArgumentException("The manifest value is invalid or too long.", parameterName);
        return value;
    }
}

public sealed record StoredManagedProfileCleanupManifest
{
    public StoredManagedProfileCleanupManifest(
        ManagedProfileCleanupManifest manifest,
        ManagedProfileCleanupState state,
        long revision)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state), state, "A cleanup state is required.");
        if (revision < 0)
            throw new ArgumentOutOfRangeException(nameof(revision), revision, "A non-negative revision is required.");
        State = state;
        Revision = revision;
    }

    public ManagedProfileCleanupManifest Manifest { get; }
    public ManagedProfileCleanupState State { get; }
    public long Revision { get; }

    public override string ToString() =>
        $"StoredManagedProfileCleanupManifest {{ Manifest = [REDACTED], State = {State}, Revision = {Revision} }}";
}

public sealed record ManagedProfileCleanupTransitionRequest
{
    public ManagedProfileCleanupTransitionRequest(
        Guid manifestId,
        long expectedRevision,
        ManagedProfileCleanupState expectedState,
        ManagedProfileCleanupState targetState)
    {
        if (manifestId == Guid.Empty)
            throw new ArgumentException("A manifest ID is required.", nameof(manifestId));
        if (expectedRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision), expectedRevision, "A non-negative revision is required.");
        if (!Enum.IsDefined(expectedState))
            throw new ArgumentOutOfRangeException(nameof(expectedState), expectedState, "An expected cleanup state is required.");
        if (!Enum.IsDefined(targetState))
            throw new ArgumentOutOfRangeException(nameof(targetState), targetState, "A target cleanup state is required.");
        if (expectedState == ManagedProfileCleanupState.Deleted ||
            targetState == ManagedProfileCleanupState.Prepared ||
            expectedState == targetState)
            throw new ArgumentException("The cleanup state transition is invalid.", nameof(targetState));
        ManifestId = manifestId;
        ExpectedRevision = expectedRevision;
        ExpectedState = expectedState;
        TargetState = targetState;
    }

    public Guid ManifestId { get; }
    public long ExpectedRevision { get; }
    public ManagedProfileCleanupState ExpectedState { get; }
    public ManagedProfileCleanupState TargetState { get; }

    public override string ToString() =>
        $"ManagedProfileCleanupTransitionRequest {{ ManifestId = [REDACTED], ExpectedRevision = {ExpectedRevision}, " +
        $"ExpectedState = {ExpectedState}, TargetState = {TargetState} }}";
}
