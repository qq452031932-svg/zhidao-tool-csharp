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
        string observedStableBaiduUserId)
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

        ObservedStableBaiduUserId = observedStableBaiduUserId;
    }

    public StoredBrowserInstallation ExpectedInstallation { get; }
    public StoredManagedBrowserSession ExpectedSession { get; }
    public BrowserInstallationIdentity TargetInstallation { get; }
    public ManagedBrowserSessionReference TargetSession { get; }
    public string ObservedStableBaiduUserId { get; }

    public override string ToString() =>
        "ManagedBrowserFamilySwitchTransitionRequest { Identities = [REDACTED] }";
}
