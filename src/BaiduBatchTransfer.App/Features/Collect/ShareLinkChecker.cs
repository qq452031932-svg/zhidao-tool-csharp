using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace BaiduBatchTransfer.App.Features.Collect;

public enum CollectLinkStatus
{
    Unchecked,
    Checking,
    Accessible,
    Invalid,
    PasswordProblem,
    Undetermined,
}

/// <summary>一次检测的结论。Reason 只写给用户看的原因和百度返回码，不含链接、提取码。</summary>
public sealed record ShareLinkCheckResult(CollectLinkStatus Status, string Reason, bool LooksRateLimited = false);

public interface IShareLinkChecker
{
    Task<ShareLinkCheckResult> CheckAsync(string link, string pwd, CancellationToken cancellationToken);
}

/// <summary>百度网盘分享链接的识别与规范化，打开链接和检测共用。</summary>
public static class BaiduShareLink
{
    /// <summary>
    /// 识别 pan.baidu.com/s/1xxx 与 pan.baidu.com/share/init?surl=xxx 两种形式。
    /// shortUrl 为带前导「1」的完整短链编号；linkPwd 为链接里唯一的 pwd 参数（没有或有多个时为空）。
    /// </summary>
    public static bool TryParse(string? link, out string shortUrl, out string linkPwd)
    {
        shortUrl = linkPwd = string.Empty;
        if (string.IsNullOrWhiteSpace(link) || !Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return false;
        if (!uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        if (!string.Equals(uri.IdnHost, "pan.baidu.com", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.IdnHost, "yun.baidu.com", StringComparison.OrdinalIgnoreCase)) return false;
        var query = ReadQuery(uri);
        string candidate;
        if (uri.AbsolutePath.StartsWith("/s/", StringComparison.Ordinal))
            candidate = uri.AbsolutePath[3..].TrimEnd('/');
        else if (uri.AbsolutePath.Equals("/share/init", StringComparison.OrdinalIgnoreCase) &&
                 query.TryGetValue("surl", out var surls) && surls.Count == 1)
            candidate = "1" + surls[0];
        else return false;
        if (candidate.Length is < 5 or > 128 ||
            !candidate.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            return false;
        shortUrl = candidate;
        if (query.TryGetValue("pwd", out var pwds) && pwds.Count == 1) linkPwd = pwds[0].Trim();
        return true;
    }

    /// <summary>
    /// 浏览器要打开的地址：原链接是 https 的 /s/ 形式就保留原样，否则换成等价的规范地址；
    /// 记录里有提取码而链接没有时补上 pwd。链接自带的提取码与记录不一致时不覆盖，由 conflict 告知调用方。
    /// </summary>
    public static Uri? BuildOpenUri(string link, string recordPwd, out bool conflict)
    {
        conflict = false;
        if (!TryParse(link, out var shortUrl, out var linkPwd)) return null;
        var pwd = recordPwd.Trim();
        var original = new Uri(link.Trim());
        var keepOriginal = original.Scheme == Uri.UriSchemeHttps &&
            string.Equals(original.IdnHost, "pan.baidu.com", StringComparison.OrdinalIgnoreCase) &&
            original.AbsolutePath.StartsWith("/s/", StringComparison.Ordinal) &&
            string.IsNullOrEmpty(original.Fragment);
        var hasPwdParameter = ReadQuery(original).ContainsKey("pwd");
        if (linkPwd.Length > 0 && pwd.Length > 0 && !string.Equals(linkPwd, pwd, StringComparison.Ordinal))
            conflict = true;
        if (keepOriginal)
        {
            if (hasPwdParameter || pwd.Length == 0 || !IsSafePwd(pwd)) return original;
            var separator = original.Query.Length > 1 ? "&" : original.Query.Length == 1 ? string.Empty : "?";
            return new Uri(original.AbsoluteUri + separator + "pwd=" + Uri.EscapeDataString(pwd));
        }
        var chosen = linkPwd.Length > 0 ? linkPwd : pwd;
        var canonical = $"https://pan.baidu.com/s/{shortUrl}";
        return new Uri(chosen.Length > 0 && IsSafePwd(chosen) ? $"{canonical}?pwd={Uri.EscapeDataString(chosen)}" : canonical);
    }

    private static bool IsSafePwd(string pwd) => pwd.Length is > 0 and <= 16 && pwd.All(char.IsAsciiLetterOrDigit);

    private static Dictionary<string, List<string>> ReadQuery(Uri uri)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            string key, value;
            try
            {
                key = Uri.UnescapeDataString(pair[0]);
                value = pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : string.Empty;
            }
            catch (UriFormatException) { continue; }
            if (!result.TryGetValue(key, out var values)) result[key] = values = [];
            values.Add(value);
        }
        return result;
    }
}

/// <summary>
/// 不登录、只读地检查百度网盘分享：先查分享信息，需要时校验提取码，最后读一次文件列表。
/// 只有真正读到文件列表才算可访问；只有百度明确说链接不存在、已取消或已过期才算失效；
/// 其余情况（超时、验证码、请求受限、认不出的返回）一律是「暂无法判断」。不转存、不下载。
/// </summary>
public sealed class ShareLinkChecker : IShareLinkChecker
{
    private const string Origin = "https://pan.baidu.com";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    // 2026-10-15 实测：编造的短链 shorturlinfo 返回 errno 140「啊哦，链接出错了」。
    private static readonly HashSet<long> InvalidErrnos = [140];
    // 需要验证码或操作过于频繁；这些不是链接本身的问题。
    private static readonly HashSet<long> VerificationErrnos = [-62, -63, -64, 9019];
    private static readonly string[] InvalidMessageMarkers =
        ["链接出错", "不存在", "已失效", "已过期", "已被取消", "已取消", "已被删除", "已删除", "来晚了", "违规", "侵权"];

    private static readonly HttpClient SharedClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
        MaxResponseContentBufferSize = 512 * 1024,
    };

    private readonly HttpClient client;
    private readonly TimeProvider timeProvider;

    public ShareLinkChecker(HttpClient? client = null, TimeProvider? timeProvider = null)
    {
        this.client = client ?? SharedClient;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ShareLinkCheckResult> CheckAsync(string link, string pwd, CancellationToken cancellationToken)
    {
        if (!BaiduShareLink.TryParse(link, out var shortUrl, out var linkPwd))
            return new(CollectLinkStatus.Undetermined, "不是可识别的百度网盘分享链接，未检测。");
        var candidates = new[] { pwd.Trim(), linkPwd }
            .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        try
        {
            using var info = await GetJsonAsync(
                $"{Origin}/api/shorturlinfo?web=5&app_id=250528&clienttype=5&shorturl={Uri.EscapeDataString(shortUrl)}",
                cancellationToken).ConfigureAwait(false);
            var errno = ReadErrno(info.RootElement);
            if (errno == 0) return await ListAsync(shortUrl, null, cancellationToken).ConfigureAwait(false);
            if (errno != -9) return Classify(errno, info.RootElement, "读取分享信息");
            if (candidates.Length == 0)
                return new(CollectLinkStatus.PasswordProblem, "这条分享需要提取码，记录里没有提取码。");

            ShareLinkCheckResult? last = null;
            foreach (var candidate in candidates)
            {
                using var verify = await PostVerifyAsync(shortUrl, candidate, cancellationToken).ConfigureAwait(false);
                var verifyErrno = ReadErrno(verify.RootElement);
                if (verifyErrno == 0)
                {
                    var randsk = ReadString(verify.RootElement, "randsk");
                    if (string.IsNullOrEmpty(randsk))
                        return new(CollectLinkStatus.Undetermined, "提取码校验通过，但百度没有返回访问凭据。");
                    return await ListAsync(shortUrl, randsk, cancellationToken).ConfigureAwait(false);
                }
                last = verifyErrno == -9
                    ? new(CollectLinkStatus.PasswordProblem, candidates.Length > 1
                        ? "记录和链接里的提取码都不正确（百度返回 -9）。"
                        : "提取码不正确（百度返回 -9）。")
                    : Classify(verifyErrno, verify.RootElement, "校验提取码");
                if (last.Status != CollectLinkStatus.PasswordProblem) return last;
            }
            return last!;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(CollectLinkStatus.Undetermined, "请求超时，未能确认。");
        }
        catch (HttpRequestException)
        {
            return new(CollectLinkStatus.Undetermined, "网络请求失败，未能确认。");
        }
        catch (UnexpectedResponseException error)
        {
            return new(CollectLinkStatus.Undetermined, error.Message, error.LooksRateLimited);
        }
    }

    private async Task<ShareLinkCheckResult> ListAsync(string shortUrl, string? randsk, CancellationToken cancellationToken)
    {
        using var list = await GetJsonAsync(
            $"{Origin}/share/list?shorturl={Uri.EscapeDataString(shortUrl[1..])}&root=1&page=1&num=20&web=1&clienttype=0&app_id=250528",
            cancellationToken, randsk is null ? null : $"BDCLND={randsk}").ConfigureAwait(false);
        var root = list.RootElement;
        var errno = ReadErrno(root);
        if (errno != 0) return Classify(errno, root, "读取文件列表");
        if (!root.TryGetProperty("list", out var items) || items.ValueKind != JsonValueKind.Array)
            return new(CollectLinkStatus.Undetermined, "百度返回成功，但没有文件列表。");
        var count = items.GetArrayLength();
        return count == 0
            ? new(CollectLinkStatus.Undetermined, "分享能打开，但文件列表为空。")
            : new(CollectLinkStatus.Accessible, $"可访问，读到 {count}{(count >= 20 ? "+" : string.Empty)} 项文件。");
    }

    private static ShareLinkCheckResult Classify(long errno, JsonElement root, string stage)
    {
        var message = ReadString(root, "show_msg") ?? ReadString(root, "err_msg") ?? string.Empty;
        var suffix = message.Length > 0 ? $"：{Truncate(message)}" : string.Empty;
        if (InvalidErrnos.Contains(errno) || InvalidMessageMarkers.Any(marker => message.Contains(marker, StringComparison.Ordinal)))
            return new(CollectLinkStatus.Invalid, $"分享已失效（百度返回 {errno}{suffix}）。");
        if (VerificationErrnos.Contains(errno))
            return new(CollectLinkStatus.Undetermined, $"百度要求验证或请求过于频繁（{errno}{suffix}），未能确认。", true);
        return new(CollectLinkStatus.Undetermined, $"{stage}时百度返回无法识别的结果（{errno}{suffix}），未能确认。");
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken, string? cookie = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> PostVerifyAsync(string shortUrl, string pwd, CancellationToken cancellationToken)
    {
        var surl = shortUrl[1..];
        var time = timeProvider.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{Origin}/share/verify?surl={Uri.EscapeDataString(surl)}&t={time}&channel=chunlei&web=1&clienttype=0")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["pwd"] = pwd,
                ["vcode"] = string.Empty,
                ["vcode_str"] = string.Empty,
            }),
        };
        request.Headers.Referrer = new Uri($"{Origin}/share/init?surl={Uri.EscapeDataString(surl)}");
        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        request.Headers.Referrer ??= new Uri(Origin + "/");
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var code = (int)response.StatusCode;
        if (code is 403 or 429)
            throw new UnexpectedResponseException($"百度拒绝了请求（HTTP {code}），可能请求过于频繁，未能确认。", true);
        if (code is >= 300 and < 400)
            throw new UnexpectedResponseException($"百度要求跳转（HTTP {code}），可能需要验证，未能确认。", true);
        if (!response.IsSuccessStatusCode)
            throw new UnexpectedResponseException($"百度服务暂不可用（HTTP {code}），未能确认。");
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
        }
        catch (JsonException) { }
        throw new UnexpectedResponseException("百度返回的不是可识别的数据（可能是验证页面），未能确认。", true);
    }

    private static long ReadErrno(JsonElement root)
    {
        foreach (var name in new[] { "errno", "error_code" })
        {
            if (!root.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String &&
                long.TryParse(value.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number))
                return number;
        }
        throw new UnexpectedResponseException("百度返回的数据缺少状态码，未能确认。");
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Truncate(string value) => value.Length <= 40 ? value : value[..40] + "…";

    private sealed class UnexpectedResponseException(string message, bool looksRateLimited = false) : Exception(message)
    {
        public bool LooksRateLimited { get; } = looksRateLimited;
    }
}
