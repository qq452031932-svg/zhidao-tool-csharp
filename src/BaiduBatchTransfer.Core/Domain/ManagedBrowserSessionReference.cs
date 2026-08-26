namespace BaiduBatchTransfer.Core.Domain;

public sealed class ManagedBrowserSessionReference
{
    public ManagedBrowserSessionReference(
        Guid sessionId,
        Guid installationId,
        Guid accountId,
        string stableBaiduUserId,
        BrowserFamily browserFamily,
        string profileId,
        string profileOwnershipId)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("A session ID is required.", nameof(sessionId));
        if (installationId == Guid.Empty)
            throw new ArgumentException("An installation ID is required.", nameof(installationId));
        if (accountId == Guid.Empty)
            throw new ArgumentException("An account ID is required.", nameof(accountId));
        if (browserFamily is not (BrowserFamily.Chrome or BrowserFamily.Edge))
            throw new ArgumentOutOfRangeException(nameof(browserFamily), browserFamily, "A managed browser family is required.");

        SessionId = sessionId;
        InstallationId = installationId;
        AccountId = accountId;
        StableBaiduUserId = RequireText(stableBaiduUserId, nameof(stableBaiduUserId));
        BrowserFamily = browserFamily;
        ProfileId = RequireText(profileId, nameof(profileId));
        ProfileOwnershipId = RequireText(profileOwnershipId, nameof(profileOwnershipId));
    }

    public Guid SessionId { get; }

    public Guid InstallationId { get; }

    public Guid AccountId { get; }

    public string StableBaiduUserId { get; }

    public BrowserFamily BrowserFamily { get; }

    public string ProfileId { get; }

    public string ProfileOwnershipId { get; }

    public override string ToString() =>
        $"ManagedBrowserSessionReference {{ SessionId = [REDACTED], InstallationId = [REDACTED], " +
        $"AccountId = [REDACTED], StableBaiduUserId = [REDACTED], BrowserFamily = {BrowserFamily}, " +
        "ProfileId = [REDACTED], ProfileOwnershipId = [REDACTED] }";

    private static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value != value.Trim() || value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException("The identity value is invalid or too long.", parameterName);
        return value;
    }
}
