namespace BaiduBatchTransfer.Core.Domain;

public sealed class BaiduExecutionContext
{
    private BaiduExecutionContext(
        BaiduExecutionEngine engine,
        AccountSession? legacySession,
        ManagedBrowserSessionReference? managedSession)
    {
        if ((legacySession is null) == (managedSession is null))
            throw new ArgumentException("Exactly one execution session is required.");

        Engine = engine;
        LegacySession = legacySession;
        ManagedSession = managedSession;
    }

    public BaiduExecutionEngine Engine { get; }

    public AccountSession? LegacySession { get; }

    public ManagedBrowserSessionReference? ManagedSession { get; }

    public static BaiduExecutionContext Legacy(AccountSession session) =>
        new(BaiduExecutionEngine.WebView2V1, session ?? throw new ArgumentNullException(nameof(session)), null);

    public static BaiduExecutionContext Managed(ManagedBrowserSessionReference session) =>
        new(BaiduExecutionEngine.ManagedChromiumV1, null, session ?? throw new ArgumentNullException(nameof(session)));

    public override string ToString() =>
        $"BaiduExecutionContext {{ Engine = {Engine}, LegacySession = " +
        (LegacySession is null ? "null" : "[REDACTED]") + ", ManagedSession = " +
        (ManagedSession is null ? "null" : "[REDACTED]") + " }";
}
