using BaiduBatchTransfer.BaiduAdapter.Authentication;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Features.Accounts;

public static class BaiduLoginFlowPolicy
{
    public static Uri InitialLoginUri { get; } = new("https://pan.baidu.com/");
}

public sealed record BaiduLoginAuthenticationState(bool HasMinimumCookies, bool IsStableSignedInPage)
{
    public bool IsAuthenticated => HasMinimumCookies && IsStableSignedInPage;
}

public static class BaiduLoginAuthenticationDetector
{
    private static readonly HashSet<string> MinimumCookieNames = new(StringComparer.Ordinal)
    {
        "BDUSS",
        "STOKEN",
    };

    public static BaiduLoginAuthenticationState Evaluate(Uri? topLevelUri, IEnumerable<SessionCookie> cookies)
    {
        ArgumentNullException.ThrowIfNull(cookies);
        var names = cookies
            .Where(cookie => !string.IsNullOrWhiteSpace(cookie.Value))
            .Select(cookie => cookie.Name)
            .ToHashSet(StringComparer.Ordinal);
        var stablePage = topLevelUri is { IsAbsoluteUri: true } &&
            topLevelUri.Scheme == Uri.UriSchemeHttps &&
            topLevelUri.IsDefaultPort &&
            string.IsNullOrEmpty(topLevelUri.UserInfo) &&
            string.Equals(topLevelUri.IdnHost, "pan.baidu.com", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(topLevelUri.AbsolutePath, "/disk/main", StringComparison.OrdinalIgnoreCase) ||
             topLevelUri.AbsolutePath.StartsWith("/disk/main/", StringComparison.OrdinalIgnoreCase));
        return new(MinimumCookieNames.IsSubsetOf(names), stablePage);
    }
}

public static class BaiduLoginCookieCapturePolicy
{
    private const int MaximumCookieCount = 512;

    public static IReadOnlyList<CapturedWebCookie> CreateValidSnapshot(
        IEnumerable<CapturedWebCookie> cookies)
    {
        ArgumentNullException.ThrowIfNull(cookies);
        var valid = new List<CapturedWebCookie>();
        foreach (var cookie in cookies)
        {
            if (valid.Count >= MaximumCookieCount) break;
            if (cookie is null || string.IsNullOrWhiteSpace(cookie.Value)) continue;
            try
            {
                _ = new SessionCookie(
                    cookie.Name,
                    cookie.Value,
                    cookie.Domain,
                    cookie.Path,
                    cookie.ExpiresUtc,
                    cookie.IsSecure,
                    cookie.IsHttpOnly);
                valid.Add(cookie);
            }
            catch (ArgumentException)
            {
                // 百度页面会短暂暴露过渡 Cookie；它们不能阻断有效登录会话。
            }
        }
        return valid;
    }
}

public sealed class BaiduLoginSettlingGate
{
    private readonly TimeSpan delay;
    private DateTimeOffset? authenticatedSince;

    public BaiduLoginSettlingGate(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero || delay > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(delay));
        this.delay = delay;
    }

    public bool IsReady(BaiduLoginAuthenticationState authenticationState, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(authenticationState);
        if (!authenticationState.IsAuthenticated)
        {
            authenticatedSince = null;
            return false;
        }

        if (authenticatedSince is null || observedAt < authenticatedSince)
        {
            authenticatedSince = observedAt;
            return delay == TimeSpan.Zero;
        }
        return observedAt - authenticatedSince >= delay;
    }

    public void Reset() => authenticatedSince = null;
}

public static class BaiduLoginDomClickPolicy
{
    public const string LoginControlClickScript = """
        (() => {
            if (location.protocol !== 'https:' || location.hostname !== 'pan.baidu.com' || location.pathname !== '/') return false;
            if (window.__baiduBatchTransferLoginClicked === true) return true;
            const candidates = Array.from(document.querySelectorAll('button,a,[role="button"]'));
            const visible = element => {
                const bounds = element.getBoundingClientRect();
                const style = window.getComputedStyle(element);
                return bounds.width > 0 && bounds.height > 0 && style.display !== 'none' &&
                    style.visibility !== 'hidden' && element.getAttribute('aria-disabled') !== 'true' &&
                    !element.hasAttribute('disabled');
            };
            const label = element => (element.getAttribute('aria-label') || element.innerText || '')
                .replace(/\s+/g, '').trim();
            const target = candidates.find(element => visible(element) &&
                (label(element) === '登录' || label(element) === '去登录' || label(element) === '立即登录'));
            if (!target) return false;
            window.__baiduBatchTransferLoginClicked = true;
            target.click();
            return true;
        })()
        """;

    public static bool CanAttempt(Uri? uri, BaiduLoginAuthenticationState state) =>
        !state.HasMinimumCookies &&
        uri is { IsAbsoluteUri: true } &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        string.Equals(uri.IdnHost, "pan.baidu.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query);

    public static bool WasClickPerformed(string? result) =>
        string.Equals(result, "true", StringComparison.OrdinalIgnoreCase);
}

public sealed class BaiduLoginDomAutomationCoordinator
{
    private readonly object gate = new();
    private readonly int maxAttempts;
    private bool inProgress;
    private bool succeeded;

    // 轮询间隔 700ms。默认 5 次意味着只等 3.5 秒就判定“找不到登录按钮”，
    // 而 pan.baidu.com 是单页应用，网络稍慢时首页按钮远不止 3.5 秒才渲染出来，
    // 用户看到的是页面明明已经显示「去登录」却报错。预算放宽到约 45 秒。
    public BaiduLoginDomAutomationCoordinator(int maxAttempts = 64)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxAttempts, 256);
        this.maxAttempts = maxAttempts;
    }

    public int AttemptCount { get; private set; }
    public bool IsExhausted { get { lock (gate) return !succeeded && AttemptCount >= maxAttempts; } }

    public bool TryBeginAttempt(Uri? uri, BaiduLoginAuthenticationState state)
    {
        if (!BaiduLoginDomClickPolicy.CanAttempt(uri, state)) return false;
        lock (gate)
        {
            if (inProgress || succeeded || AttemptCount >= maxAttempts) return false;
            AttemptCount++;
            inProgress = true;
            return true;
        }
    }

    public void CompleteAttempt(bool clickPerformed)
    {
        lock (gate)
        {
            if (!inProgress) throw new InvalidOperationException("An attempt must begin before completion.");
            inProgress = false;
            succeeded = clickPerformed;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            inProgress = false;
            succeeded = false;
            AttemptCount = 0;
        }
    }
}

public sealed class BaiduLoginAutoCaptureCoordinator
{
    private readonly object gate = new();
    private CaptureState state;

    public event EventHandler? CloseRequested;

    public bool TryBeginCapture(BaiduLoginAuthenticationState authenticationState)
    {
        ArgumentNullException.ThrowIfNull(authenticationState);
        if (!authenticationState.IsAuthenticated) return false;
        lock (gate)
        {
            if (state != CaptureState.Waiting) return false;
            state = CaptureState.Capturing;
            return true;
        }
    }

    public void CompleteRegistration()
    {
        lock (gate)
        {
            if (state == CaptureState.Completed) return;
            if (state != CaptureState.Capturing)
                throw new InvalidOperationException("Capture must begin before registration completes.");
            state = CaptureState.Completed;
        }
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ResetAfterFailure()
    {
        lock (gate)
            if (state == CaptureState.Capturing) state = CaptureState.Waiting;
    }

    private enum CaptureState { Waiting, Capturing, Completed }
}
