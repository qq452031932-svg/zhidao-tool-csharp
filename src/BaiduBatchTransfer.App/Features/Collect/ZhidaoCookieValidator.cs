using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace BaiduBatchTransfer.App.Features.Collect;

public sealed record ZhidaoCookieValidationResult(bool IsValid, string Message);

public interface IZhidaoCookieValidator
{
    Task<ZhidaoCookieValidationResult> ValidateAsync(string cookie, CancellationToken cancellationToken);
}

/// <summary>只读查询知道账号信息；不启动采集，不上传文件，不输出 Cookie 或服务端原文。</summary>
public sealed class ZhidaoCookieValidator : IZhidaoCookieValidator
{
    // 与 Python lxds.zhidao_bulk 的账号信息查询使用同一接口和直连约定。
    private static readonly HttpClient SharedClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
        MaxResponseContentBufferSize = 64 * 1024,
    };

    private readonly HttpClient client;

    public ZhidaoCookieValidator(HttpClient? client = null) => this.client = client ?? SharedClient;

    public async Task<ZhidaoCookieValidationResult> ValidateAsync(string cookie, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cookie))
            return new(false, "请先填入知道账号的 Cookie。");
        if (cookie.Contains('\r') || cookie.Contains('\n'))
            return new(false, "Cookie 含有换行，请重新粘贴完整的 Cookie 请求头内容。");

        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://zhidao.baidu.com/qa/api/bulkqa/userinfo");
        request.Headers.Add("Cookie", cookie.Trim());
        request.Headers.Add("Accept", "application/json, text/plain, */*");
        request.Headers.Add("Accept-Language", "zh-CN,zh;q=0.9");
        request.Headers.Add("Origin", "https://zhidao.baidu.com");
        request.Headers.Referrer = new Uri("https://zhidao.baidu.com/b/batch/batch-upload");

        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return Expired();
            if (!response.IsSuccessStatusCode)
                return new(false, "未能确认 Cookie：百度知道请求受限或服务暂不可用，请在浏览器确认登录后重试。");

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Unconfirmed();
            var data = root.TryGetProperty("data", out var nested) ? nested : root;
            if (!TryReadCode(root, out var code) && !TryReadCode(data, out code))
                return Unconfirmed();
            if (code is 401 or 403 or -1)
                return Expired();
            if (code != 200 || data.ValueKind != JsonValueKind.Object)
                return Unconfirmed();
            if (data.TryGetProperty("isLogin", out var login) && login.ValueKind == JsonValueKind.False)
                return Expired();
            // 拒绝把空的成功信封当成有效登录；hasPermission 是现有账号接口的契约字段。
            if (!data.TryGetProperty("hasPermission", out var permission) ||
                permission.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return Unconfirmed();

            return new(true, permission.GetBoolean()
                ? "Cookie 验证通过，知道账号处于登录状态。"
                : "Cookie 验证通过；该账号无批量发布权限，采集不依赖此权限。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "Cookie 验证超时，未能确认登录状态，请稍后重试。");
        }
        catch (HttpRequestException)
        {
            return new(false, "Cookie 验证网络请求失败，未能确认登录状态，请检查网络后重试。");
        }
        catch (JsonException)
        {
            return Unconfirmed();
        }
    }

    private static bool TryReadCode(JsonElement value, out int code)
    {
        code = 0;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty("code", out var field) &&
            (field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out code) ||
             field.ValueKind == JsonValueKind.String && int.TryParse(field.GetString(), out code));
    }

    private static ZhidaoCookieValidationResult Expired() =>
        new(false, "Cookie 已失效或未登录，请登录百度知道后重新复制 Cookie。");

    private static ZhidaoCookieValidationResult Unconfirmed() =>
        new(false, "百度知道未返回可确认的账号信息，无法验证 Cookie；请在浏览器确认登录后重试。");
}
