namespace BaiduBatchTransfer.Core.Domain;

public enum ManagedBrowserRetentionOwnerKind
{
    Operation = 1,
    Attempt = 2,
    CleanupManifest = 3
}

public sealed record ManagedBrowserRetentionReference
{
    public ManagedBrowserRetentionReference(
        ManagedBrowserRetentionOwnerKind ownerKind,
        Guid ownerId,
        Guid installationId,
        Guid sessionId)
    {
        if (ownerKind is not (ManagedBrowserRetentionOwnerKind.Operation or
                              ManagedBrowserRetentionOwnerKind.Attempt or
                              ManagedBrowserRetentionOwnerKind.CleanupManifest))
            throw new ArgumentOutOfRangeException(nameof(ownerKind), ownerKind, "A retention owner kind is required.");
        if (ownerId == Guid.Empty)
            throw new ArgumentException("A retention owner ID is required.", nameof(ownerId));
        if (installationId == Guid.Empty)
            throw new ArgumentException("An installation ID is required.", nameof(installationId));
        if (sessionId == Guid.Empty)
            throw new ArgumentException("A session ID is required.", nameof(sessionId));

        OwnerKind = ownerKind;
        OwnerId = ownerId;
        InstallationId = installationId;
        SessionId = sessionId;
    }

    public ManagedBrowserRetentionOwnerKind OwnerKind { get; }

    public Guid OwnerId { get; }

    public Guid InstallationId { get; }

    public Guid SessionId { get; }

    public override string ToString() =>
        $"ManagedBrowserRetentionReference {{ OwnerKind = {OwnerKind}, OwnerId = [REDACTED], " +
        "InstallationId = [REDACTED], SessionId = [REDACTED] }";
}
