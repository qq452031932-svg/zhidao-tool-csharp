using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.Http;

public sealed record BaiduWebContext(
    string UserId,
    string CsrfToken,
    string RequestContextFingerprint)
{
    public override string ToString() =>
        "BaiduWebContext { UserId = [REDACTED], CsrfToken = [REDACTED], RequestContextFingerprint = [REDACTED] }";
}

public sealed class BaiduWebContextException(string errorCode, string safeMessage)
    : InvalidOperationException(safeMessage)
{
    public string ErrorCode { get; } = errorCode;
}

public sealed class BaiduWebContextClient(BaiduSameOriginHttpClient transport)
{
    private readonly BaiduSameOriginHttpClient transport =
        transport ?? throw new ArgumentNullException(nameof(transport));

    public async Task<BaiduWebContext> ReadAsync(
        AccountSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(session.BaiduUserId))
            throw new BaiduWebContextException(
                "SESSION_NOT_BOUND", "The selected account session is not bound to an account.");

        var identity = await transport.SendJsonAsync(
            session, BaiduSameOriginRoute.Identity, cancellationToken).ConfigureAwait(false);
        var observedUserId = ReadIdentity(identity.Payload);
        if (!string.Equals(observedUserId, session.BaiduUserId, StringComparison.Ordinal))
            throw new BaiduWebContextException(
                "ACCOUNT_IDENTITY_MISMATCH", "The selected account session identity changed.");

        var contextResponse = await transport.SendJsonAsync(
            session, BaiduSameOriginRoute.WebContext, cancellationToken).ConfigureAwait(false);
        var csrf = ReadCsrf(contextResponse.Payload);
        var fingerprint = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"baidu-web-context/v1\n{observedUserId}\n{csrf}")));
        return new(observedUserId, csrf, fingerprint);
    }

    private static string ReadIdentity(JsonElement root)
    {
        EnsureSuccess(root);
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("uk", out var userId) || userId.ValueKind != JsonValueKind.Number)
            throw InvalidShape();
        return userId.GetRawText();
    }

    private static string ReadCsrf(JsonElement root)
    {
        EnsureSuccess(root);
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("bdstoken", out var token) || token.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(token.GetString()))
            throw InvalidShape();
        return token.GetString()!;
    }

    private static void EnsureSuccess(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("errno", out var error) ||
            error.ValueKind != JsonValueKind.Number || error.GetInt32() != 0)
            throw new BaiduWebContextException(
                "REMOTE_CONTEXT_REJECTED", "The remote account context was rejected.");
    }

    private static BaiduWebContextException InvalidShape() => new(
        "UNEXPECTED_CONTEXT_SHAPE", "The remote account context has an unexpected shape.");
}
