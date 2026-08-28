using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Abstractions;

public enum ManagedBrowserStopStatus
{
    Stopped = 1,
    NotRunning = 2,
    Blocked = 3,
}

public sealed record ManagedBrowserLifecycleSession
{
    public ManagedBrowserLifecycleSession(
        ManagedBrowserSessionReference session,
        long installationRevision,
        long sessionRevision,
        string canonicalProfilePath)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        if (installationRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(installationRevision));
        if (sessionRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(sessionRevision));
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalProfilePath);
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(canonicalProfilePath));
        if (!Path.IsPathFullyQualified(canonical) ||
            !string.Equals(canonical, Path.TrimEndingDirectorySeparator(canonicalProfilePath),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A canonical absolute managed profile path is required.", nameof(canonicalProfilePath));

        InstallationRevision = installationRevision;
        SessionRevision = sessionRevision;
        CanonicalProfilePath = canonical;
    }

    public ManagedBrowserSessionReference Session { get; }
    public Guid SessionId => Session.SessionId;
    public Guid InstallationId => Session.InstallationId;
    public Guid AccountId => Session.AccountId;
    public BrowserFamily BrowserFamily => Session.BrowserFamily;
    public string ProfileId => Session.ProfileId;
    public string ProfileOwnershipId => Session.ProfileOwnershipId;
    public long InstallationRevision { get; }
    public long SessionRevision { get; }
    public string CanonicalProfilePath { get; }

    public override string ToString() =>
        $"ManagedBrowserLifecycleSession {{ Session = [REDACTED], InstallationRevision = {InstallationRevision}, " +
        $"SessionRevision = {SessionRevision}, CanonicalProfilePath = [REDACTED] }}";
}

public interface IManagedBrowserLifecycle
{
    Task<ManagedBrowserLifecycleSession?> LoadActiveSessionAsync(
        Guid accountId,
        CancellationToken cancellationToken);

    Task<ManagedBrowserStopStatus> StopAsync(
        ManagedBrowserLifecycleSession exactSession,
        CancellationToken cancellationToken);
}

/// <summary>
/// Process-local coordination under the app's single-instance guard. Callers hold this account lease
/// across login observation/binding or removal's durable preparation; SQLite remains the CAS authority.
/// Pending ownership is separate from persisted identity: it must never be represented by a fake UID.
/// </summary>
public interface IManagedBrowserAccountLifecycle : IManagedBrowserLifecycle
{
    Task<IDisposable> AcquireAccountLeaseAsync(Guid accountId, CancellationToken cancellationToken);
    Task<ManagedBrowserStopStatus> StopPendingAsync(Guid accountId, CancellationToken cancellationToken);
    Task<ManagedBrowserStopStatus> PreparePendingRemovalAsync(Guid accountId, CancellationToken cancellationToken);
}
