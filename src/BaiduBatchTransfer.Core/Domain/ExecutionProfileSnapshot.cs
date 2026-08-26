namespace BaiduBatchTransfer.Core.Domain;

public sealed class ExecutionProfileSnapshot
{
    public ExecutionProfileSnapshot(
        BaiduExecutionEngine engine,
        BrowserFamily browserFamily,
        string profileId,
        Guid? installationId = null,
        Guid? sessionId = null)
    {
        if (engine is not (BaiduExecutionEngine.WebView2V1 or BaiduExecutionEngine.ManagedChromiumV1))
            throw new ArgumentOutOfRangeException(nameof(engine), engine, "An execution engine is required.");
        if (browserFamily is not (BrowserFamily.LegacyWebView2 or BrowserFamily.Chrome or BrowserFamily.Edge))
            throw new ArgumentOutOfRangeException(nameof(browserFamily), browserFamily, "A browser family is required.");

        ProfileId = RequireText(profileId, nameof(profileId));
        Engine = engine;
        BrowserFamily = browserFamily;

        if (engine == BaiduExecutionEngine.WebView2V1)
        {
            if (browserFamily != BrowserFamily.LegacyWebView2 || installationId.HasValue || sessionId.HasValue)
                throw new ArgumentException("Legacy WebView2 snapshots cannot carry managed browser identity.");
        }
        else if (browserFamily is not (BrowserFamily.Chrome or BrowserFamily.Edge) ||
                 !installationId.HasValue || installationId == Guid.Empty ||
                 !sessionId.HasValue || sessionId == Guid.Empty)
        {
            throw new ArgumentException("Managed Chromium snapshots require a managed browser identity.");
        }

        InstallationId = installationId;
        SessionId = sessionId;
    }

    public BaiduExecutionEngine Engine { get; }

    public BrowserFamily BrowserFamily { get; }

    public string ProfileId { get; }

    public Guid? InstallationId { get; }

    public Guid? SessionId { get; }

    public static ExecutionProfileSnapshot Legacy(string profileId) =>
        new(BaiduExecutionEngine.WebView2V1, BrowserFamily.LegacyWebView2, profileId);

    public static ExecutionProfileSnapshot Managed(
        BrowserFamily browserFamily,
        string profileId,
        Guid installationId,
        Guid sessionId) =>
        new(BaiduExecutionEngine.ManagedChromiumV1, browserFamily, profileId, installationId, sessionId);

    public override string ToString() =>
        $"ExecutionProfileSnapshot {{ Engine = {Engine}, BrowserFamily = {BrowserFamily}, " +
        "ProfileId = [REDACTED], InstallationId = [REDACTED], SessionId = [REDACTED] }";

    private static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Any(char.IsControl))
            throw new ArgumentException("The value cannot contain control characters.", parameterName);
        return value.Trim();
    }
}
