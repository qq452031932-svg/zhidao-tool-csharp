namespace BaiduBatchTransfer.Core.Domain;

public enum BrowserInstallationLifecycleState
{
    Active = 1,
    Retired = 2
}

public enum ManagedBrowserSessionLifecycleState
{
    Active = 1,
    Retired = 2,
    QuarantinePending = 3,
    Quarantined = 4,
    CleanupPending = 5,
    Deleted = 6
}

public sealed record StoredBrowserInstallation
{
    public StoredBrowserInstallation(
        BrowserInstallationIdentity identity,
        BrowserInstallationLifecycleState state,
        long revision)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        if (state is not (BrowserInstallationLifecycleState.Active or BrowserInstallationLifecycleState.Retired))
            throw new ArgumentOutOfRangeException(nameof(state), state, "A valid installation lifecycle state is required.");
        if (revision < 0)
            throw new ArgumentOutOfRangeException(nameof(revision), revision, "A non-negative revision is required.");

        State = state;
        Revision = revision;
    }

    public BrowserInstallationIdentity Identity { get; }

    public BrowserInstallationLifecycleState State { get; }

    public long Revision { get; }

    public override string ToString() =>
        $"StoredBrowserInstallation {{ Identity = [REDACTED], State = {State}, Revision = {Revision} }}";
}

public sealed record StoredManagedBrowserSession
{
    public StoredManagedBrowserSession(
        ManagedBrowserSessionReference identity,
        ManagedBrowserSessionLifecycleState state,
        long revision)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        if (state is not (ManagedBrowserSessionLifecycleState.Active or
                          ManagedBrowserSessionLifecycleState.Retired or
                          ManagedBrowserSessionLifecycleState.QuarantinePending or
                          ManagedBrowserSessionLifecycleState.Quarantined or
                          ManagedBrowserSessionLifecycleState.CleanupPending or
                          ManagedBrowserSessionLifecycleState.Deleted))
            throw new ArgumentOutOfRangeException(nameof(state), state, "A valid session lifecycle state is required.");
        if (revision < 0)
            throw new ArgumentOutOfRangeException(nameof(revision), revision, "A non-negative revision is required.");

        State = state;
        Revision = revision;
    }

    public ManagedBrowserSessionReference Identity { get; }

    public ManagedBrowserSessionLifecycleState State { get; }

    public long Revision { get; }

    public override string ToString() =>
        $"StoredManagedBrowserSession {{ Identity = [REDACTED], State = {State}, Revision = {Revision} }}";
}
