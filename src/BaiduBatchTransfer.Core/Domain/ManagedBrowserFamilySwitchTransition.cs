namespace BaiduBatchTransfer.Core.Domain;

public enum ManagedBrowserFamilySwitchTransitionStatus
{
    Applied = 1,
    ActiveWorkBlocked = 2,
    Conflict = 3,
}

public sealed record ManagedBrowserFamilySwitchTransitionResult(
    ManagedBrowserFamilySwitchTransitionStatus Status);

public sealed record ManagedBrowserFamilySwitchTransitionRequest
{
    public ManagedBrowserFamilySwitchTransitionRequest(
        StoredBrowserInstallation expectedInstallation,
        StoredManagedBrowserSession expectedSession,
        BrowserInstallationIdentity targetInstallation,
        ManagedBrowserSessionReference targetSession,
        string observedStableBaiduUserId,
        StoredBrowserInstallation? expectedTargetInstallation = null)
    {
        ExpectedInstallation = expectedInstallation ??
            throw new ArgumentNullException(nameof(expectedInstallation));
        ExpectedSession = expectedSession ?? throw new ArgumentNullException(nameof(expectedSession));
        TargetInstallation = targetInstallation ?? throw new ArgumentNullException(nameof(targetInstallation));
        TargetSession = targetSession ?? throw new ArgumentNullException(nameof(targetSession));
        ArgumentException.ThrowIfNullOrWhiteSpace(observedStableBaiduUserId);

        if (expectedInstallation.State != BrowserInstallationLifecycleState.Active ||
            expectedSession.State != ManagedBrowserSessionLifecycleState.Active ||
            expectedSession.Identity.InstallationId != expectedInstallation.Identity.InstallationId ||
            expectedSession.Identity.BrowserFamily != expectedInstallation.Identity.BrowserFamily ||
            targetInstallation.InstallationId == expectedInstallation.Identity.InstallationId ||
            targetInstallation.BrowserFamily == expectedInstallation.Identity.BrowserFamily ||
            targetSession.SessionId == expectedSession.Identity.SessionId ||
            targetSession.AccountId != expectedSession.Identity.AccountId ||
            targetSession.InstallationId != targetInstallation.InstallationId ||
            targetSession.BrowserFamily != targetInstallation.BrowserFamily ||
            string.Equals(targetSession.ProfileId, expectedSession.Identity.ProfileId, StringComparison.Ordinal) ||
            string.Equals(targetSession.ProfileOwnershipId,
                expectedSession.Identity.ProfileOwnershipId, StringComparison.Ordinal) ||
            !string.Equals(expectedSession.Identity.StableBaiduUserId,
                observedStableBaiduUserId, StringComparison.Ordinal) ||
            !string.Equals(targetSession.StableBaiduUserId,
                observedStableBaiduUserId, StringComparison.Ordinal))
            throw new ArgumentException("The managed browser family switch tuple is invalid.");

        if (expectedTargetInstallation is not null &&
            (expectedTargetInstallation.State != BrowserInstallationLifecycleState.Retired ||
             expectedTargetInstallation.Identity.InstallationId == expectedInstallation.Identity.InstallationId ||
             !SameExecutableIdentity(expectedTargetInstallation.Identity, targetInstallation)))
            throw new ArgumentException("The reusable target installation is invalid.",
                nameof(expectedTargetInstallation));

        ObservedStableBaiduUserId = observedStableBaiduUserId;
        ExpectedTargetInstallation = expectedTargetInstallation;
    }

    public StoredBrowserInstallation ExpectedInstallation { get; }
    public StoredManagedBrowserSession ExpectedSession { get; }
    public BrowserInstallationIdentity TargetInstallation { get; }
    public ManagedBrowserSessionReference TargetSession { get; }
    public string ObservedStableBaiduUserId { get; }
    public StoredBrowserInstallation? ExpectedTargetInstallation { get; }

    public override string ToString() =>
        "ManagedBrowserFamilySwitchTransitionRequest { Identities = [REDACTED] }";

    private static bool SameExecutableIdentity(
        BrowserInstallationIdentity left,
        BrowserInstallationIdentity right) =>
        left.BrowserFamily == right.BrowserFamily &&
        string.Equals(left.InstallationDirectory, right.InstallationDirectory,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.ExecutablePath, right.ExecutablePath,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Publisher, right.Publisher, StringComparison.Ordinal) &&
        string.Equals(left.ProductName, right.ProductName, StringComparison.Ordinal) &&
        string.Equals(left.FileIdentity, right.FileIdentity, StringComparison.Ordinal);
}
