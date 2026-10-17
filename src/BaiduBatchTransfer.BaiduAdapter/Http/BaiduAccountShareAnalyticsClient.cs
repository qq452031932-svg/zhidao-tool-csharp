using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.Http;

public sealed class BaiduAccountShareAnalyticsClient : IAccountShareAnalyticsClient, IDisposable
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private const int MaximumPage = 10_000;
    private const int PageSize = 100;
    /// <summary>HttpMessageInvoker 本身没有超时：百度不回应时刷新会一直挂着、按钮一直是灰的。</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpMessageInvoker http;
    private readonly TimeSpan requestTimeout;
    private readonly Action<string>? reportFailure;

    public BaiduAccountShareAnalyticsClient()
        : this(CreateDefaultHandler())
    {
    }

    /// <param name="reportFailure">
    /// 读取失败时收到一行原因（第几页、HTTP 状态、百度 errno、哪个字段不对），不含 Cookie 和链接。
    /// 原来这些原因全被丢掉，界面只剩「暂时无法读取分享数据」，没法排查。
    /// </param>
    public BaiduAccountShareAnalyticsClient(Action<string>? reportFailure)
        : this(CreateDefaultHandler(), null, reportFailure)
    {
    }

    public BaiduAccountShareAnalyticsClient(
        HttpMessageHandler handler,
        TimeSpan? requestTimeout = null,
        Action<string>? reportFailure = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        this.reportFailure = reportFailure;
        this.requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        if (this.requestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        if (handler is HttpClientHandler concrete)
        {
            concrete.AllowAutoRedirect = false;
            concrete.UseCookies = false;
            concrete.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        }
        http = new HttpMessageInvoker(handler, disposeHandler: true);
    }

    public async Task<BaiduAdapterResult<AccountSharePage>> ReadPageAsync(
        AccountSession session,
        string? cursor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var note = new FailureNote();
        var result = await ReadPageCoreAsync(session, cursor, note, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess && note.Text is not null)
            reportFailure?.Invoke($"读取分享列表第 {cursor ?? "1"} 页失败：{note.Text}");
        return result;
    }

    private async Task<BaiduAdapterResult<AccountSharePage>> ReadPageCoreAsync(
        AccountSession session,
        string? cursor,
        FailureNote note,
        CancellationToken cancellationToken)
    {
        if (!TryReadPage(cursor, out var pageNumber))
            return Failure(note, BaiduErrorKind.UnknownResponse, "页码游标无效");

        var uri = BuildPageUri(pageNumber);
        var cookie = BuildCookieHeader(session.SessionCookies, uri, DateTimeOffset.UtcNow);
        if (cookie.Length == 0)
            return Failure(note, BaiduErrorKind.ExpiredLogin, "账号没有可用的登录 Cookie");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(requestTimeout);
            try
            {
                var outcome = await SendOnceAsync(uri, cookie, pageNumber, attempt, note, timeout.Token).ConfigureAwait(false);
                if (outcome is not null) return outcome;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                // 超时按网络临时失败处理：第一次再试一次，第二次报「获取失败，请稍后重试」。
                if (attempt == 0) continue;
                return Failure(note, BaiduErrorKind.TransientService, $"两次请求都超过 {requestTimeout.TotalSeconds:0} 秒没有回应");
            }
        }

        return Failure(note, BaiduErrorKind.TransientService, "重试后仍失败");
    }

    /// <returns>null 表示这一次可以重试。</returns>
    private async Task<BaiduAdapterResult<AccountSharePage>?> SendOnceAsync(
        Uri uri,
        string cookie,
        int pageNumber,
        int attempt,
        FailureNote note,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException error)
        {
            if (attempt == 0) return null;
            return Failure(note, BaiduErrorKind.TransientService, $"网络错误 {error.GetType().Name}");
        }

        using (response)
        {
            if (IsTransient(response.StatusCode))
            {
                if (attempt == 0) return null;
                note.Text = $"HTTP {(int)response.StatusCode}";
                return BaiduAdapterResult<AccountSharePage>.Failure(
                    BaiduErrorKind.TransientService,
                    response.StatusCode.ToString(),
                    ReadRetryAfter(response.Headers.RetryAfter));
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return Failure(note, BaiduErrorKind.ExpiredLogin, "HTTP 401");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return Failure(note, BaiduErrorKind.VerificationRequired, "HTTP 403");
            if (!response.IsSuccessStatusCode)
                return Failure(note, BaiduErrorKind.UnknownResponse, $"HTTP {(int)response.StatusCode}");

            byte[] payload;
            try
            {
                payload = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                return Failure(note, BaiduErrorKind.UnknownResponse, "响应超过 1 MB");
            }

            try
            {
                using var document = JsonDocument.Parse(
                    payload,
                    new JsonDocumentOptions { MaxDepth = 64 });
                return ParsePage(document.RootElement, pageNumber, note);
            }
            catch (JsonException error)
            {
                return Failure(note, BaiduErrorKind.UnknownResponse, $"响应不是有效 JSON（{error.GetType().Name}）");
            }
            catch (ArgumentException error)
            {
                return Failure(note, BaiduErrorKind.UnknownResponse, error.GetType().Name);
            }
            catch (InvalidOperationException error)
            {
                return Failure(note, BaiduErrorKind.UnknownResponse, error.GetType().Name);
            }
        }
    }

    public void Dispose() => http.Dispose();

    private static HttpClientHandler CreateDefaultHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        UseCookies = false,
    };

    private static BaiduAdapterResult<AccountSharePage> ParsePage(
        JsonElement root,
        int pageNumber,
        FailureNote note)
    {
        if (!TryReadInt64(root, "errno", out var errno))
            return Failure(note, BaiduErrorKind.UnknownResponse, "响应里没有 errno");
        if (errno != 0)
        {
            var kind = errno switch
            {
                -6 => BaiduErrorKind.ExpiredLogin,
                -62 or -20 => BaiduErrorKind.VerificationRequired,
                _ => BaiduErrorKind.UnknownResponse,
            };
            return Failure(note, kind, $"百度返回 errno={errno.ToString(CultureInfo.InvariantCulture)}");
        }
        if (!root.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
            return Failure(note, BaiduErrorKind.UnknownResponse, "响应里没有 list 数组");
        if (list.GetArrayLength() > PageSize)
            return Failure(note, BaiduErrorKind.UnknownResponse, $"一页有 {list.GetArrayLength()} 条，超过 {PageSize}");
        if (!TryReadInt64(root, "count", out var count) || count != list.GetArrayLength())
            return Failure(note, BaiduErrorKind.UnknownResponse,
                $"count 与 list 条数不一致（list {list.GetArrayLength()} 条，count {(TryReadInt64(root, "count", out var raw) ? raw.ToString(CultureInfo.InvariantCulture) : "缺失")}）");
        if (!TryReadInt64(root, "nextpage", out var nextMarker))
            return Failure(note, BaiduErrorKind.UnknownResponse, "响应里没有 nextpage");

        var items = new List<AccountShareObservation>(list.GetArrayLength());
        var index = 0;
        foreach (var element in list.EnumerateArray())
        {
            index++;
            if (!TryParseItem(element, out var item, out var reason))
                return Failure(note, BaiduErrorKind.UnknownResponse, $"本页第 {index} 条分享{reason}");
            items.Add(item!);
        }

        string? nextCursor = null;
        if (list.GetArrayLength() == PageSize && nextMarker > 0)
        {
            if (pageNumber >= MaximumPage)
                return Failure(note, BaiduErrorKind.UnknownResponse, $"超过 {MaximumPage} 页上限");
            nextCursor = checked(pageNumber + 1).ToString(CultureInfo.InvariantCulture);
        }

        try
        {
            return BaiduAdapterResult<AccountSharePage>.Success(new(items, nextCursor));
        }
        catch (ArgumentException error)
        {
            return Failure(note, BaiduErrorKind.UnknownResponse, $"本页数据不合规（{error.GetType().Name}）");
        }
    }

    /// <param name="reason">失败时说明哪个字段不对，只含字段名与类型，不含字段内容。</param>
    private static bool TryParseItem(JsonElement element, out AccountShareObservation? item, out string reason)
    {
        item = null;
        reason = string.Empty;
        if (element.ValueKind != JsonValueKind.Object)
        {
            reason = $"不是对象（{element.ValueKind}）";
            return false;
        }
        if (!TryReadPositiveIdentifier(element, "shareId", out var shareId))
        {
            reason = $"的 shareId 无效（{DescribeField(element, "shareId")}）";
            return false;
        }

        var title = ReadTitle(element, shareId);
        if (!TryReadShareUri(element, out var shareUri))
        {
            reason = $"没有可用的分享链接（shortlink {DescribeField(element, "shortlink")}，shorturl {DescribeField(element, "shorturl")}）";
            return false;
        }
        if (!TryReadInt64(element, "ctime", out var seconds) || seconds < 0)
        {
            reason = $"的 ctime 无效（{DescribeField(element, "ctime")}）";
            return false;
        }
        if (!TryReadInt64(element, "status", out var statusNumber))
        {
            reason = $"的 status 无效（{DescribeField(element, "status")}）";
            return false;
        }

        DateTimeOffset sharedAtUtc;
        try { sharedAtUtc = DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException)
        {
            reason = "的 ctime 超出范围";
            return false;
        }

        var status = statusNumber == 0
            ? AccountShareRemoteStatus.Valid
            : AccountShareRemoteStatus.Invalid;
        var metrics = new[]
        {
            new AccountShareMetricObservation(AccountShareMetric.Transfer, ReadOptionalCount(element, "tCnt")),
            new AccountShareMetricObservation(AccountShareMetric.View, ReadOptionalCount(element, "vCnt")),
            new AccountShareMetricObservation(AccountShareMetric.Download, ReadOptionalCount(element, "dCnt")),
        };
        try
        {
            item = new(shareId, title, shareUri!, sharedAtUtc, status, metrics);
            return true;
        }
        catch (ArgumentException error)
        {
            reason = $"数据不合规（{error.ParamName ?? error.GetType().Name}）";
            return false;
        }
    }

    /// <summary>只描述字段是否存在、是什么类型、文本多长，不写字段内容（可能是链接）。</summary>
    private static string DescribeField(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)) return "缺失";
        return value.ValueKind switch
        {
            JsonValueKind.String => $"文本，长 {value.GetString()?.Length ?? 0}",
            JsonValueKind.Number => value.TryGetInt64(out var number) ? $"数字 {number.ToString(CultureInfo.InvariantCulture)}" : "数字（非整数）",
            _ => value.ValueKind.ToString(),
        };
    }

    private static bool TryReadPositiveIdentifier(
        JsonElement root,
        string propertyName,
        out string identifier)
    {
        identifier = string.Empty;
        if (!root.TryGetProperty(propertyName, out var value)) return false;
        identifier = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) && number > 0 =>
                number.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => value.GetString()?.Trim() ?? string.Empty,
            _ => string.Empty,
        };
        return identifier.Length is > 0 and <= 256 &&
            identifier.All(character => !char.IsControl(character)) &&
            (value.ValueKind != JsonValueKind.String || identifier.All(char.IsDigit));
    }

    private static string ReadTitle(JsonElement root, string shareId)
    {
        var typicalPath = ReadBoundedString(root, "typicalPath");
        if (typicalPath is not null)
        {
            var segment = typicalPath.Split(
                ['/', '\\'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            if (IsValidTitleCandidate(segment)) return segment!;
        }

        var name = ReadBoundedString(root, "name");
        return IsValidTitleCandidate(name) ? name! : shareId;
    }

    private static bool IsValidTitleCandidate(string? candidate) =>
        candidate is not null && !string.Equals(candidate.Trim(), "0", StringComparison.Ordinal);

    private static bool TryReadShareUri(JsonElement root, out Uri? uri)
    {
        uri = null;
        var raw = ReadBoundedString(root, "shortlink");
        if (raw is null)
        {
            var shortUrl = ReadBoundedString(root, "shorturl");
            if (shortUrl is null) return false;
            raw = $"https://pan.baidu.com/s/{shortUrl.TrimStart('/')}";
        }
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var candidate) ||
            candidate.Scheme != Uri.UriSchemeHttps || !candidate.IsDefaultPort ||
            !string.Equals(candidate.Host, "pan.baidu.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(candidate.UserInfo) || !string.IsNullOrEmpty(candidate.Fragment))
            return false;
        uri = candidate;
        return true;
    }

    private static string? ReadBoundedString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) || text.Length > 4096 || text.Any(char.IsControl)
            ? null
            : text;
    }

    private static long? ReadOptionalCount(JsonElement root, string propertyName) =>
        TryReadInt64(root, propertyName, out var value) && value >= 0 ? value : null;

    private static bool TryReadInt64(JsonElement root, string propertyName, out long value)
    {
        value = 0;
        if (!root.TryGetProperty(propertyName, out var element)) return false;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(
                element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    private static bool TryReadPage(string? cursor, out int pageNumber)
    {
        if (cursor is null)
        {
            pageNumber = 1;
            return true;
        }
        return int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out pageNumber) &&
            pageNumber is >= 1 and <= MaximumPage;
    }

    private static Uri BuildPageUri(int pageNumber) => new(
        $"https://pan.baidu.com/share/record?page={pageNumber.ToString(CultureInfo.InvariantCulture)}&desc=1&order=time");

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private static TimeSpan? ReadRetryAfter(RetryConditionHeaderValue? value)
    {
        if (value?.Delta is { } delta && delta >= TimeSpan.Zero) return delta;
        if (value?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }
        return null;
    }

    private static string BuildCookieHeader(
        IEnumerable<SessionCookie> cookies,
        Uri uri,
        DateTimeOffset now) =>
        string.Join("; ", cookies
            .Where(cookie => cookie.ExpiresUtc is null || cookie.ExpiresUtc > now)
            .Where(cookie => !cookie.IsSecure || uri.Scheme == Uri.UriSchemeHttps)
            .Where(cookie => DomainMatches(uri.Host, cookie.Domain))
            .Where(cookie => uri.AbsolutePath.StartsWith(cookie.Path, StringComparison.Ordinal))
            .Select(cookie => $"{cookie.Name}={cookie.Value}"));

    private static bool DomainMatches(string host, string domain)
    {
        var normalized = domain.TrimStart('.');
        return string.Equals(host, normalized, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith('.' + normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
            throw new InvalidDataException("SHARE_ANALYTICS_RESPONSE_TOO_LARGE");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("SHARE_ANALYTICS_RESPONSE_TOO_LARGE");
            buffer.Write(chunk, 0, read);
        }
    }

    private static BaiduAdapterResult<AccountSharePage> Failure(
        FailureNote note,
        BaiduErrorKind kind,
        string diagnostic)
    {
        note.Text = diagnostic;
        return BaiduAdapterResult<AccountSharePage>.Failure(kind, diagnostic);
    }

    /// <summary>一次 ReadPageAsync 的失败原因，失败时交给 reportFailure。</summary>
    private sealed class FailureNote
    {
        public string? Text { get; set; }
    }
}
