namespace BaiduBatchTransfer.Core.Abstractions;

using BaiduBatchTransfer.Core.Domain;

public enum ManagedBrowserProfileLocationStatus
{
    SourceOnly = 1,
    QuarantineOnly = 2,
    BothPresent = 3,
    NeitherPresent = 4,
    IsolationRequired = 5,
}

public enum ManagedBrowserProfileMoveStatus
{
    Moved = 1,
    AlreadyQuarantined = 2,
    Conflict = 3,
    IsolationRequired = 4,
}

public enum ManagedBrowserProfileDeleteStatus
{
    Deleted = 1,
    AlreadyDeleted = 2,
    Conflict = 3,
    IsolationRequired = 4,
}

public enum ManagedBrowserProfileCleanupStatus
{
    Quarantined = 1,
    Deleted = 2,
    CleanupPending = 3,
    Conflict = 4,
    NotFound = 5,
}

public sealed record ManagedBrowserProfileCleanupResult(
    ManagedBrowserProfileCleanupStatus Status,
    Guid ManifestId,
    long? Revision)
{
    public override string ToString() =>
        $"ManagedBrowserProfileCleanupResult {{ Status = {Status}, Identity = [REDACTED], Revision = {Revision} }}";
}

public sealed record ManagedBrowserProfileDeletionConfirmation
{
    public ManagedBrowserProfileDeletionConfirmation(
        Guid manifestId,
        long expectedRevision,
        string confirmationToken)
    {
        if (manifestId == Guid.Empty)
            throw new ArgumentException("A manifest ID is required.", nameof(manifestId));
        if (expectedRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationToken);
        ManifestId = manifestId;
        ExpectedRevision = expectedRevision;
        ConfirmationToken = confirmationToken;
    }

    public Guid ManifestId { get; }
    public long ExpectedRevision { get; }
    public string ConfirmationToken { get; }

    public override string ToString() =>
        $"ManagedBrowserProfileDeletionConfirmation {{ Identity = [REDACTED], ExpectedRevision = {ExpectedRevision}, Token = [REDACTED] }}";
}

public sealed record ManagedBrowserProfileDeletionPreparation(
    ManagedBrowserProfileCleanupStatus Status,
    ManagedBrowserProfileDeletionConfirmation? Confirmation);

public sealed record ManagedBrowserProfileCleanupPreparation
{
    public ManagedBrowserProfileCleanupPreparation(
        ManagedBrowserLifecycleSession session,
        Guid manifestId,
        long manifestRevision,
        string quarantinePath)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        if (manifestId == Guid.Empty)
            throw new ArgumentException("A cleanup manifest ID is required.", nameof(manifestId));
        if (manifestRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(manifestRevision));
        ArgumentException.ThrowIfNullOrWhiteSpace(quarantinePath);
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(quarantinePath));
        if (!Path.IsPathFullyQualified(canonical) ||
            !string.Equals(canonical, Path.TrimEndingDirectorySeparator(quarantinePath),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A canonical absolute quarantine path is required.", nameof(quarantinePath));
        var sourceRoot = Path.GetPathRoot(session.CanonicalProfilePath);
        var quarantineRoot = Path.GetPathRoot(canonical);
        if (!string.Equals(sourceRoot, quarantineRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The quarantine path must be on the managed profile volume.", nameof(quarantinePath));

        ManifestId = manifestId;
        ManifestRevision = manifestRevision;
        QuarantinePath = canonical;
    }

    public ManagedBrowserLifecycleSession Session { get; }
    public Guid ManifestId { get; }
    public long ManifestRevision { get; }
    public string SourceProfilePath => Session.CanonicalProfilePath;
    public string QuarantinePath { get; }
    public long InstallationRevision => Session.InstallationRevision;
    public long SessionRevision => Session.SessionRevision;

    public override string ToString() =>
        $"ManagedBrowserProfileCleanupPreparation {{ Session = [REDACTED], ManifestId = [REDACTED], " +
        $"ManifestRevision = {ManifestRevision}, Paths = [REDACTED] }}";
}

public interface IManagedBrowserProfileStore
{
    ManagedBrowserProfileCleanupPreparation PrepareRemoval(ManagedBrowserLifecycleSession session);

    Task<ManagedBrowserProfileLocationStatus> ObserveAsync(
        ManagedBrowserProfileCleanupPreparation preparation,
        CancellationToken cancellationToken);

    Task<ManagedBrowserProfileMoveStatus> MoveToQuarantineAsync(
        ManagedBrowserProfileCleanupPreparation preparation,
        CancellationToken cancellationToken);

    Task<ManagedBrowserProfileDeleteStatus> DeleteQuarantinedAsync(
        ManagedBrowserProfileCleanupPreparation preparation,
        string confirmationTokenHash,
        CancellationToken cancellationToken);
}
