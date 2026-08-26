using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Tests.Domain;

public sealed class ManagedBrowserContractsTests
{
    private static readonly Guid AccountId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid InstallationId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid SessionId = Guid.Parse("30000000-0000-0000-0000-000000000001");

    [Theory]
    [InlineData(BaiduExecutionEngine.WebView2V1, BrowserFamily.LegacyWebView2)]
    [InlineData(BaiduExecutionEngine.ManagedChromiumV1, BrowserFamily.Chrome)]
    [InlineData(BaiduExecutionEngine.ManagedChromiumV1, BrowserFamily.Edge)]
    public void ExecutionProfileSnapshot_AcceptsLegalEngineAndFamilyCombinations(
        BaiduExecutionEngine engine,
        BrowserFamily family)
    {
        var snapshot = engine == BaiduExecutionEngine.WebView2V1
            ? new ExecutionProfileSnapshot(engine, family, "legacy-profile")
            : new ExecutionProfileSnapshot(engine, family, "profile-1", InstallationId, SessionId);

        Assert.Equal(engine, snapshot.Engine);
        Assert.Equal(family, snapshot.BrowserFamily);
        Assert.NotEmpty(snapshot.ProfileId);
    }

    [Fact]
    public void ExecutionProfileSnapshot_RejectsIllegalCombinationsAndMissingIdentity()
    {
        Assert.Throws<ArgumentException>(() => new ExecutionProfileSnapshot(
            BaiduExecutionEngine.WebView2V1, BrowserFamily.Chrome, "legacy-profile"));
        Assert.Throws<ArgumentException>(() => new ExecutionProfileSnapshot(
            BaiduExecutionEngine.ManagedChromiumV1, BrowserFamily.LegacyWebView2,
            "profile-1", InstallationId, SessionId));
        Assert.Throws<ArgumentException>(() => new ExecutionProfileSnapshot(
            BaiduExecutionEngine.ManagedChromiumV1, BrowserFamily.Chrome,
            "profile-1", null, SessionId));
        Assert.Throws<ArgumentException>(() => new ExecutionProfileSnapshot(
            BaiduExecutionEngine.ManagedChromiumV1, BrowserFamily.Chrome,
            "profile-1", InstallationId, null));
        Assert.Throws<ArgumentException>(() => new ExecutionProfileSnapshot(
            BaiduExecutionEngine.WebView2V1, BrowserFamily.LegacyWebView2, " "));
    }

    [Fact]
    public void BrowserInstallationIdentity_AcceptsChromeAndEdgeAndRejectsLegacy()
    {
        var chrome = CreateInstallation(BrowserFamily.Chrome);
        var edge = CreateInstallation(BrowserFamily.Edge);

        Assert.Equal(InstallationId, chrome.InstallationId);
        Assert.Equal(BrowserFamily.Chrome, chrome.BrowserFamily);
        Assert.Equal(BrowserFamily.Edge, edge.BrowserFamily);
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateInstallation(BrowserFamily.LegacyWebView2));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("C:\\Browsers\\Chrome", "")]
    [InlineData("C:\\Browsers\\Chrome", "C:\\Browsers\\Chrome\\..\\chrome.exe")]
    [InlineData("C:\\Browsers\\Chrome", "chrome.exe")]
    public void BrowserInstallationIdentity_RejectsEmptyOrNoncanonicalPaths(
        string directory,
        string executablePath)
    {
        Assert.Throws<ArgumentException>(() => new BrowserInstallationIdentity(
            InstallationId,
            BrowserFamily.Chrome,
            directory,
            executablePath,
            "Google LLC",
            "Google Chrome",
            "file-identity"));
    }

    [Fact]
    public void BrowserInstallationIdentity_RejectsEmptyInstallationId()
    {
        Assert.Throws<ArgumentException>(() => new BrowserInstallationIdentity(
            Guid.Empty,
            BrowserFamily.Chrome,
            "C:\\Browsers\\Chrome",
            "C:\\Browsers\\Chrome\\chrome.exe",
            "Google LLC",
            "Google Chrome",
            "file-identity"));
    }

    [Fact]
    public void ManagedBrowserSessionReference_RequiresImmutableNonemptyIdentityFields()
    {
        var reference = new ManagedBrowserSessionReference(
            SessionId, InstallationId, AccountId, "stable-uid", BrowserFamily.Chrome,
            "profile-1", "ownership-1");

        Assert.Equal(SessionId, reference.SessionId);
        Assert.Equal(InstallationId, reference.InstallationId);
        Assert.Equal(AccountId, reference.AccountId);
        Assert.Equal("stable-uid", reference.StableBaiduUserId);
        Assert.Equal("profile-1", reference.ProfileId);
        Assert.Equal("ownership-1", reference.ProfileOwnershipId);

        Assert.Throws<ArgumentException>(() => new ManagedBrowserSessionReference(
            Guid.Empty, InstallationId, AccountId, "stable-uid", BrowserFamily.Chrome,
            "profile-1", "ownership-1"));
        Assert.Throws<ArgumentException>(() => new ManagedBrowserSessionReference(
            SessionId, Guid.Empty, AccountId, "stable-uid", BrowserFamily.Chrome,
            "profile-1", "ownership-1"));
        Assert.Throws<ArgumentException>(() => new ManagedBrowserSessionReference(
            SessionId, InstallationId, AccountId, "", BrowserFamily.Chrome,
            "profile-1", "ownership-1"));
        Assert.Throws<ArgumentException>(() => new ManagedBrowserSessionReference(
            SessionId, InstallationId, AccountId, "stable-uid", BrowserFamily.Chrome,
            "", "ownership-1"));
        Assert.Throws<ArgumentException>(() => new ManagedBrowserSessionReference(
            SessionId, InstallationId, AccountId, "stable-uid", BrowserFamily.Chrome,
            "profile-1", ""));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ManagedBrowserSessionReference(
            SessionId, InstallationId, AccountId, "stable-uid", BrowserFamily.LegacyWebView2,
            "profile-1", "ownership-1"));
    }

    [Fact]
    public void ManagedContextCarriesReferenceWithoutCookies()
    {
        var reference = new ManagedBrowserSessionReference(
            SessionId, InstallationId, AccountId, "stable-uid", BrowserFamily.Chrome,
            "profile-1", "ownership-1");

        var context = BaiduExecutionContext.Managed(reference);

        Assert.Equal(BaiduExecutionEngine.ManagedChromiumV1, context.Engine);
        Assert.Null(context.LegacySession);
        Assert.Same(reference, context.ManagedSession);
        Assert.DoesNotContain("stable-uid", context.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("profile-1", context.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ownership-1", context.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyContextCarriesAccountSessionAndCannotCarryManagedReference()
    {
        var accountSession = new AccountSession(
            AccountId,
            "stable-uid",
            new[] { new SessionCookie("BDUSS", "sensitive-cookie", ".baidu.com", "/", null, true, true) },
            DateTimeOffset.UtcNow);
        var context = BaiduExecutionContext.Legacy(accountSession);

        Assert.Equal(BaiduExecutionEngine.WebView2V1, context.Engine);
        Assert.Same(accountSession, context.LegacySession);
        Assert.Null(context.ManagedSession);
        Assert.DoesNotContain("sensitive-cookie", context.ToString(), StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => BaiduExecutionContext.Legacy(null!));
        Assert.Throws<ArgumentNullException>(() => BaiduExecutionContext.Managed(null!));
    }

    [Fact]
    public void PublicContractToStringsRedactSensitiveIdentityValues()
    {
        var installation = CreateInstallation(BrowserFamily.Chrome);
        var reference = new ManagedBrowserSessionReference(
            SessionId, InstallationId, AccountId, "stable-uid", BrowserFamily.Chrome,
            "profile-1", "ownership-1");
        var snapshot = new ExecutionProfileSnapshot(
            BaiduExecutionEngine.ManagedChromiumV1, BrowserFamily.Chrome,
            "profile-1", InstallationId, SessionId);

        foreach (var value in new object[] { installation, reference, snapshot })
        {
            var text = value.ToString()!;
            Assert.DoesNotContain("C:\\Browsers", text, StringComparison.Ordinal);
            Assert.DoesNotContain("chrome.exe", text, StringComparison.Ordinal);
            Assert.DoesNotContain("stable-uid", text, StringComparison.Ordinal);
            Assert.DoesNotContain("profile-1", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ownership-1", text, StringComparison.Ordinal);
            Assert.DoesNotContain(InstallationId.ToString(), text, StringComparison.Ordinal);
            Assert.DoesNotContain(SessionId.ToString(), text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ManagedBrowserStateStore_IsAsyncCancellableContract()
    {
        var methods = typeof(IManagedBrowserStateStore).GetMethods();

        Assert.NotEmpty(methods);
        Assert.All(methods, method =>
        {
            Assert.True(typeof(Task).IsAssignableFrom(method.ReturnType));
            Assert.Contains(method.GetParameters(), parameter => parameter.ParameterType == typeof(CancellationToken));
        });
        Assert.Contains(methods, method => method.Name.Contains("Installation", StringComparison.Ordinal));
        Assert.Contains(methods, method => method.Name.Contains("Session", StringComparison.Ordinal));
        Assert.Contains(methods, method => method.Name.Contains("Retain", StringComparison.Ordinal));
        Assert.Contains(methods, method => method.Name.Contains("Activate", StringComparison.Ordinal));
        Assert.Contains(methods, method => method.Name.Contains("Retire", StringComparison.Ordinal));
    }

    private static BrowserInstallationIdentity CreateInstallation(BrowserFamily family) => new(
        InstallationId,
        family,
        "C:\\Browsers\\Chrome",
        "C:\\Browsers\\Chrome\\chrome.exe",
        "Google LLC",
        "Google Chrome",
        "file-identity");
}
