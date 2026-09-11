using BaiduBatchTransfer.App.Features.Accounts;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.BaiduAdapter.Authentication;
using System.Reflection;
using System.Text.RegularExpressions;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class BaiduLoginPolicyTests
{
    private static readonly Uri Landing = new("https://pan.baidu.com/");
    private static readonly Uri SignedIn = new("https://pan.baidu.com/disk/main#/index");

    [Fact]
    public void AutomaticClickIsExactBoundedAndCannotMatchLogout()
    {
        var unauthenticated = BaiduLoginAuthenticationDetector.Evaluate(Landing, []);
        var coordinator = new BaiduLoginDomAutomationCoordinator(maxAttempts: 3);

        Assert.True(BaiduLoginDomClickPolicy.CanAttempt(Landing, unauthenticated));
        Assert.Contains("label(element) === '去登录'", BaiduLoginDomClickPolicy.LoginControlClickScript, StringComparison.Ordinal);
        Assert.DoesNotContain("includes('登录')", BaiduLoginDomClickPolicy.LoginControlClickScript, StringComparison.Ordinal);
        Assert.DoesNotContain("document.cookie", BaiduLoginDomClickPolicy.LoginControlClickScript, StringComparison.OrdinalIgnoreCase);
        for (var index = 0; index < 3; index++)
        {
            Assert.True(coordinator.TryBeginAttempt(Landing, unauthenticated));
            coordinator.CompleteAttempt(false);
        }
        Assert.True(coordinator.IsExhausted);
        Assert.False(coordinator.TryBeginAttempt(Landing, unauthenticated));
    }

    [Fact]
    public void AuthenticationRequiresMinimumCookiesAndStableSignedInPage()
    {
        var cookies = new[]
        {
            new SessionCookie("BDUSS", "a", ".baidu.com", "/", null, true, true),
            new SessionCookie("STOKEN", "b", "pan.baidu.com", "/", null, true, true),
        };

        Assert.False(BaiduLoginAuthenticationDetector.Evaluate(Landing, cookies).IsAuthenticated);
        Assert.True(BaiduLoginAuthenticationDetector.Evaluate(SignedIn, cookies).IsAuthenticated);
        Assert.False(BaiduLoginAuthenticationDetector.Evaluate(SignedIn, cookies.Take(1)).IsAuthenticated);
    }

    [Fact]
    public void LoginCaptureWaitsThreeSecondsAndSkipsTemporaryInvalidCookies()
    {
        var firstSeen = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);
        var gate = new BaiduLoginSettlingGate(TimeSpan.FromSeconds(3));
        var authenticated = new BaiduLoginAuthenticationState(true, true);
        var snapshot = BaiduLoginCookieCapturePolicy.CreateValidSnapshot(
        [
            new("temporary", "", ".baidu.com", "/", null, true, false),
            new("BDUSS", "signed", ".baidu.com", "/", null, true, true),
            new("STOKEN", "token", "pan.baidu.com", "/", null, true, true),
        ]);

        Assert.Equal(2, snapshot.Count);
        Assert.False(gate.IsReady(authenticated, firstSeen));
        Assert.False(gate.IsReady(authenticated, firstSeen.AddMilliseconds(2_999)));
        Assert.True(gate.IsReady(authenticated, firstSeen.AddSeconds(3)));

        Assert.False(gate.IsReady(new(false, true), firstSeen.AddSeconds(4)));
        Assert.False(gate.IsReady(authenticated, firstSeen.AddSeconds(4)));
    }

    [Fact]
    public void SettlingGateResetRequiresANewContinuousAuthenticatedDelay()
    {
        var gate = new BaiduLoginSettlingGate(TimeSpan.FromSeconds(3));
        var authenticated = new BaiduLoginAuthenticationState(true, true);
        var start = DateTimeOffset.Parse("2026-09-01T01:00:00Z");
        Assert.False(gate.IsReady(authenticated, start));
        Assert.True(gate.IsReady(authenticated, start.AddSeconds(3)));

        gate.Reset();

        Assert.False(gate.IsReady(authenticated, start.AddSeconds(4)));
        Assert.True(gate.IsReady(authenticated, start.AddSeconds(7)));
    }

    [Fact]
    public void CookieSnapshotRejectsForeignProfileCookiesBeforeApplyingTheSafetyLimit()
    {
        var cookies = Enumerable.Range(0, 512)
            .Select(index => new CapturedWebCookie(
                $"foreign-{index}", "value", ".example.com", "/", null, true, true))
            .Append(new CapturedWebCookie(
                "BDUSS", "signed", ".baidu.com", "/", null, true, true));

        var snapshot = BaiduLoginCookieCapturePolicy.CreateValidSnapshot(cookies);

        var captured = Assert.Single(snapshot);
        Assert.Equal("BDUSS", captured.Name);
        Assert.Equal(".baidu.com", captured.Domain);
    }

    [Fact]
    public void LoginWindowCapturesTheWholeProfileCookieSnapshot()
    {
        var codeBehind = ReadLoginWindowCodeBehind();

        Assert.Contains("GetCookiesAsync(string.Empty)", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "GetCookiesAsync(BaiduLoginFlowPolicy.InitialLoginUri.AbsoluteUri)",
            codeBehind,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LoginWindowDisablesPasswordSavingAndGeneralAutofill()
    {
        var codeBehind = ReadLoginWindowCodeBehind();

        Assert.Contains(
            "Settings.IsPasswordAutosaveEnabled = false",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "Settings.IsGeneralAutofillEnabled = false",
            codeBehind,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AutoCaptureCanBeginOnlyOnceAndClosesOnlyAfterRegistrationCompletes()
    {
        var state = new BaiduLoginAuthenticationState(true, true);
        var coordinator = new BaiduLoginAutoCaptureCoordinator();
        var closeCount = 0;
        coordinator.CloseRequested += (_, _) => closeCount++;

        Assert.True(coordinator.TryBeginCapture(state));
        Assert.False(coordinator.TryBeginCapture(state));
        coordinator.CompleteRegistration();
        coordinator.CompleteRegistration();

        Assert.Equal(1, closeCount);
    }

    [Theory]
    [InlineData("https://pan.baidu.com/")]
    [InlineData("https://pan.baidu.com/disk/main")]
    [InlineData("https://passport.baidu.com/v2/?login")]
    public void RequiredOfficialTopLevelHostsAreAllowed(string url) =>
        Assert.True(BaiduNavigationPolicy.Evaluate(new Uri(url)).IsAllowed);

    [Theory]
    [InlineData("http://pan.baidu.com/")]
    [InlineData("https://evil.example/")]
    [InlineData("https://pan.baidu.com.evil.example/")]
    [InlineData("file:///C:/temp/login.html")]
    public void NonAllowlistedNavigationIsBlockedWithoutEchoingTheUrl(string url)
    {
        var decision = BaiduNavigationPolicy.Evaluate(new Uri(url));
        Assert.False(decision.IsAllowed);
        Assert.DoesNotContain(url, decision.SafeMessage, StringComparison.Ordinal);
        Assert.Matches("[\\u4e00-\\u9fff]", decision.SafeMessage!);
    }

    [Fact]
    public void LoginWindowTextAndRegistrationFailuresAreSafeChinese()
    {
        var visible = typeof(BaiduLoginUiText).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();
        Assert.NotEmpty(visible);
        Assert.All(visible, value =>
        {
            Assert.Matches("[\\u4e00-\\u9fff]", value);
            Assert.DoesNotMatch(new Regex("[A-Za-z]", RegexOptions.CultureInvariant), value);
        });

        var viewModel = new BaiduLoginViewModel();
        var close = viewModel.ReportRegistrationResult(new(
            AddAccountStatus.DuplicateIdentity,
            Guid.NewGuid(),
            "secret-user-id-and-cookie"));

        Assert.False(close);
        Assert.Contains("已经添加", viewModel.Status);
        Assert.DoesNotContain("secret", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadLoginWindowCodeBehind(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (current is not null)
        {
            var solution = Path.Combine(current.FullName, "BaiduBatchTransfer.sln");
            if (File.Exists(solution))
            {
                return File.ReadAllText(Path.Combine(
                    current.FullName,
                    "src",
                    "BaiduBatchTransfer.App",
                    "Features",
                    "Accounts",
                    "BaiduLoginWindow.xaml.cs"));
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("未找到测试仓库目录。");
    }
}
