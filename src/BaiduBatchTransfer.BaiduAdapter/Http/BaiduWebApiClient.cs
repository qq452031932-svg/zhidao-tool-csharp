using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.Http;

/// <summary>
/// Cookie-authenticated client for the small set of pan.baidu.com web routes used by
/// direct background transfer. It never follows redirects and never accepts a caller supplied host.
/// </summary>
public sealed class BaiduWebApiClient
{
    private const int MaximumJsonBytes = 1024 * 1024;
    private const int MaximumHtmlBytes = 4 * 1024 * 1024;
    private static readonly Uri Origin = new("https://pan.baidu.com");
    private readonly HttpMessageInvoker http;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan requestTimeout;

    public BaiduWebApiClient(
        HttpMessageHandler? handler = null,
        TimeProvider? timeProvider = null,
        TimeSpan? requestTimeout = null)
    {
        handler ??= new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        var timeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        http = new HttpMessageInvoker(handler, disposeHandler: false);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.requestTimeout = timeout;
    }

    public async Task<BaiduAdapterResult<ShareMetadata>> InspectShareAsync(
        AccountSession session,
        ShareReference share,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(share);
        try
        {
            var source = await ResolveShareAsync(session, share, cancellationToken).ConfigureAwait(false);
            var outerName = source.Items.Length == 1
                ? source.Items[0].Name
                : $"{source.Items.Length}项文件";
            return BaiduAdapterResult<ShareMetadata>.Success(new(
                share,
                outerName,
                source.Items.Length,
                source.Items.Sum(item => item.Size),
                Fingerprint(source.Items.Select(item => $"{item.Id}\0{item.Name}"))));
        }
        catch (BaiduWebApiException exception)
        {
            return Failure<ShareMetadata>(exception);
        }
    }

    public async Task<BaiduAdapterResult<ImmutableArray<RemoteDirectory>>> ListDirectoriesAsync(
        AccountSession session,
        RemotePath path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            var token = await GetTokenAsync(session, cancellationToken).ConfigureAwait(false);
            var items = await ListOwnDriveAsync(session, token, path.Value, cancellationToken).ConfigureAwait(false);
            return BaiduAdapterResult<ImmutableArray<RemoteDirectory>>.Success(items
                .Where(item => item.IsDirectory)
                .Select(item => new RemoteDirectory(item.Id, new RemotePath(item.Path)))
                .ToImmutableArray());
        }
        catch (BaiduWebApiException exception)
        {
            return Failure<ImmutableArray<RemoteDirectory>>(exception);
        }
    }

    public async Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryAsync(
        AccountSession session,
        RemotePath path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            var token = await GetTokenAsync(session, cancellationToken).ConfigureAwait(false);
            if (path.Value == "/") return BaiduAdapterResult<RemoteDirectory>.Success(new("/", path));

            var current = "/";
            var currentId = "/";
            foreach (var segment in path.Value.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = current == "/" ? $"/{segment}" : $"{current}/{segment}";
                var children = await ListOwnDriveAsync(session, token, current, cancellationToken).ConfigureAwait(false);
                var existing = children.FirstOrDefault(item =>
                    item.IsDirectory && string.Equals(item.Path, next, StringComparison.Ordinal));
                if (existing is null)
                {
                    var uri = ApiUri("/api/create", new()
                    {
                        ["a"] = "commit",
                        ["bdstoken"] = token,
                        ["clienttype"] = "0",
                        ["web"] = "1",
                    });
                    using var document = await PostFormJsonAsync(session, uri, new()
                    {
                        ["path"] = next,
                        ["isdir"] = "1",
                        ["block_list"] = "[]",
                    }, cancellationToken).ConfigureAwait(false);
                    EnsureSuccess(document.RootElement, SubmissionAcceptance.Unknown);
                    currentId = ReadString(document.RootElement, "fs_id", "fsid", "request_id") ?? next;
                }
                else currentId = existing.Id;
                current = next;
            }
            return BaiduAdapterResult<RemoteDirectory>.Success(new(currentId, path));
        }
        catch (BaiduWebApiException exception)
        {
            return Failure<RemoteDirectory>(exception);
        }
    }

    public async Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareAsync(
        AccountSession session,
        ShareReference share,
        RemoteDirectory destination,
        DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(share);
        ArgumentNullException.ThrowIfNull(destination);
        _ = duplicatePolicy;
        try
        {
            var token = await GetTokenAsync(session, cancellationToken).ConfigureAwait(false);
            var source = await ResolveShareAsync(session, share, token, cancellationToken).ConfigureAwait(false);
            var uri = ApiUri("/share/transfer", new()
            {
                ["shareid"] = source.ShareId,
                ["from"] = source.ShareUk,
                ["bdstoken"] = token,
                ["channel"] = "chunlei",
                ["web"] = "1",
                ["clienttype"] = "0",
            });
            JsonDocument? document = null;
            List<RemoteItemIdentity>? recovered = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    document = await PostFormJsonAsync(session, uri, new()
                    {
                        ["fsidlist"] = JsonSerializer.Serialize(source.Items.Select(item => ParseJsonId(item.Id)).ToArray()),
                        ["path"] = destination.Path.Value,
                    }, cancellationToken, source.ShareCookie).ConfigureAwait(false);
                    EnsureSuccess(document.RootElement, SubmissionAcceptance.Unknown);
                    break;
                }
                catch (BaiduWebApiException exception) when (
                    exception.ErrorKind is BaiduErrorKind.TransientService or BaiduErrorKind.UnknownResponse)
                {
                    document?.Dispose();
                    document = null;
                    if (attempt == 0)
                    {
                        try
                        {
                            var current = await ListOwnDriveAsync(
                                session, token, destination.Path.Value, cancellationToken).ConfigureAwait(false);
                            var observed = MatchTransferredItems(source.Items, current);
                            if (observed.Count == source.Items.Length)
                            {
                                recovered = observed;
                                break;
                            }
                        }
                        catch (BaiduWebApiException) { }
                        continue;
                    }
                    throw new BaiduWebApiException(
                        exception.ErrorKind, SubmissionAcceptance.DefinitelyNotAccepted);
                }
            }

            if (recovered is not null)
            {
                return BaiduAdapterResult<TransferReceipt>.Success(
                    TransferReceipt.WithExactEvidence(
                        timeProvider.GetUtcNow(), null, new RemoteItemIdentitySet(recovered)));
            }
            using (document)
            {
                if (document is null)
                    throw new BaiduWebApiException(
                        BaiduErrorKind.TransientService, SubmissionAcceptance.DefinitelyNotAccepted);
            var exact = ReadTransferredItems(document.RootElement);
            if (exact.Count != source.Items.Length)
            {
                var listed = await ListOwnDriveAsync(session, token, destination.Path.Value, cancellationToken)
                    .ConfigureAwait(false);
                exact = MatchTransferredItems(source.Items, listed);
            }
            if (exact.Count != source.Items.Length)
                throw new BaiduWebApiException(BaiduErrorKind.TransientService, SubmissionAcceptance.Unknown);

            var requestId = ReadString(document.RootElement, "request_id", "requestid");
            return BaiduAdapterResult<TransferReceipt>.Success(
                TransferReceipt.WithExactEvidence(
                    timeProvider.GetUtcNow(), requestId, new RemoteItemIdentitySet(exact)),
                SubmissionAcceptance.Unknown);
            }
        }
        catch (BaiduWebApiException exception)
        {
            return Failure<TransferReceipt>(exception);
        }
    }

    public async Task<BaiduAdapterResult<ShareCreationReceipt>> CreatePermanentShareAsync(
        AccountSession session,
        PermanentShareRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var token = await GetTokenAsync(session, cancellationToken).ConfigureAwait(false);
            var extractionCode = request.FixedExtractionCode ?? RandomExtractionCode();
            var uri = ApiUri("/share/set", new()
            {
                ["channel"] = "chunlei",
                ["bdstoken"] = token,
                ["clienttype"] = "0",
                ["app_id"] = "250528",
                ["web"] = "1",
            });
            using var document = await PostFormJsonAsync(session, uri, new()
            {
                ["period"] = "0",
                ["pwd"] = extractionCode,
                ["eflag_disable"] = "true",
                ["channel_list"] = "[]",
                ["schannel"] = "4",
                ["fid_list"] = JsonSerializer.Serialize(request.RemoteItems.Items.Select(item => ParseJsonId(item.ExactId)).ToArray()),
            }, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(document.RootElement, SubmissionAcceptance.Unknown);
            var shareUrl = ReadString(document.RootElement, "link", "shorturl")
                ?? throw new BaiduWebApiException(BaiduErrorKind.UnknownResponse, SubmissionAcceptance.Unknown);
            var shareId = ReadString(document.RootElement, "shareid", "share_id") ?? ShareIdFromUrl(shareUrl);
            var requestId = ReadString(document.RootElement, "request_id", "requestid") ?? shareId;
            var observedCode = ReadString(document.RootElement, "pwd") ?? extractionCode;
            var behavior = request.UsesRandomExtractionCode
                ? ShareExtractionCodeBehavior.Random
                : ShareExtractionCodeBehavior.Fixed;
            var receipt = new ShareCreationReceipt(
                timeProvider.GetUtcNow(), requestId, request, shareId, shareUrl,
                new EffectiveShareSettings(true, true, behavior, observedCode));
            return BaiduAdapterResult<ShareCreationReceipt>.Success(receipt, SubmissionAcceptance.Unknown);
        }
        catch (BaiduWebApiException exception)
        {
            return exception.ErrorKind is BaiduErrorKind.TransientService or BaiduErrorKind.UnknownResponse
                ? BaiduAdapterResult<ShareCreationReceipt>.Failure(
                    exception.ErrorKind, null,
                    submissionAcceptance: SubmissionAcceptance.DefinitelyNotAccepted)
                : Failure<ShareCreationReceipt>(exception);
        }
        catch (ArgumentException)
        {
            return BaiduAdapterResult<ShareCreationReceipt>.Failure(
                BaiduErrorKind.UnknownResponse, null, submissionAcceptance: SubmissionAcceptance.Unknown);
        }
    }

    public async Task<BaiduAdapterResult<TransferConfirmationResult>> ConfirmExactTransferAsync(
        AccountSession session,
        ShareReference sourceShare,
        TransferConfirmationQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sourceShare);
        ArgumentNullException.ThrowIfNull(query);
        try
        {
            var token = await GetTokenAsync(session, cancellationToken).ConfigureAwait(false);
            var source = await ResolveShareAsync(session, sourceShare, token, cancellationToken).ConfigureAwait(false);
            var destination = await ListOwnDriveAsync(
                session, token, query.TargetPath.Value, cancellationToken).ConfigureAwait(false);
            var exact = MatchTransferredItemsInWindow(source.Items, destination, query);
            if (exact.Count == source.Items.Length)
            {
                return BaiduAdapterResult<TransferConfirmationResult>.Success(
                    TransferConfirmationResult.ExactPresent(new RemoteItemIdentitySet(exact)),
                    SubmissionAcceptance.Unknown);
            }

            return BaiduAdapterResult<TransferConfirmationResult>.Success(
                timeProvider.GetUtcNow() > query.SubmitWindowEndUtc
                    ? TransferConfirmationResult.Absent
                    : TransferConfirmationResult.Ambiguous,
                timeProvider.GetUtcNow() > query.SubmitWindowEndUtc
                    ? SubmissionAcceptance.DefinitelyNotAccepted
                    : SubmissionAcceptance.Unknown);
        }
        catch (BaiduWebApiException exception)
        {
            return Failure<TransferConfirmationResult>(exception);
        }
    }

    public Task<BaiduAdapterResult<BaiduSharePage>> ListSharesAsync(
        AccountSession session,
        string? cursor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        _ = cursor;
        // Direct batches always regenerate a share. Returning an empty discovery snapshot keeps
        // the existing durable share pipeline while avoiding a second, unrelated listing protocol.
        return Task.FromResult(BaiduAdapterResult<BaiduSharePage>.Success(new([], null)));
    }

    private async Task<ResolvedShare> ResolveShareAsync(
        AccountSession session,
        ShareReference share,
        CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(session, cancellationToken).ConfigureAwait(false);
        return await ResolveShareAsync(session, share, token, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ResolvedShare> ResolveShareAsync(
        AccountSession session,
        ShareReference share,
        string token,
        CancellationToken cancellationToken)
    {
        if (!IsStableShareId(share.StableShareId))
            throw new BaiduWebApiException(BaiduErrorKind.InvalidLink, SubmissionAcceptance.DefinitelyNotAccepted);
        string? shareCookie = null;
        if (share.ExtractionCode is not null)
        {
            var verifyUri = ApiUri("/share/verify", new()
            {
                ["surl"] = share.StableShareId[0] == '1' ? share.StableShareId[1..] : share.StableShareId,
                ["bdstoken"] = token,
                ["t"] = timeProvider.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                ["channel"] = "chunlei",
                ["web"] = "1",
                ["clienttype"] = "0",
            });
            using var verify = await PostFormJsonAsync(session, verifyUri, new()
            {
                ["pwd"] = share.ExtractionCode,
                ["vcode"] = string.Empty,
                ["vcode_str"] = string.Empty,
            }, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(verify.RootElement, SubmissionAcceptance.DefinitelyNotAccepted);
            var randsk = ReadString(verify.RootElement, "randsk")
                ?? throw new BaiduWebApiException(BaiduErrorKind.UnknownResponse, SubmissionAcceptance.DefinitelyNotAccepted);
            shareCookie = $"BDCLND={randsk}";
        }

        var shareUri = new Uri(Origin, $"/s/{share.StableShareId}");
        var html = await SendTextAsync(
            session, HttpMethod.Get, shareUri, null, MaximumHtmlBytes, cancellationToken, shareCookie)
            .ConfigureAwait(false);
        string? shareId = null;
        string? shareUk = null;
        ImmutableArray<ShareSourceItem> items = [];
        try
        {
            var root = ExtractYunData(html);
            shareId = ReadString(root, "shareid");
            shareUk = ReadString(root, "share_uk", "uk");
            items = ReadShareItems(root);
        }
        catch (BaiduWebApiException) { }
        if (shareId is null || shareUk is null || items.Length == 0)
        {
            var fallback = ExtractEmbeddedShareFields(html);
            shareId ??= fallback.ShareId;
            shareUk ??= fallback.ShareUk;
            if (items.Length == 0) items = fallback.Items;
        }
        if (shareId is null || shareUk is null)
            throw new BaiduWebApiException(BaiduErrorKind.InvalidLink, SubmissionAcceptance.DefinitelyNotAccepted);
        if (items.Length == 0)
            throw new BaiduWebApiException(BaiduErrorKind.InvalidLink, SubmissionAcceptance.DefinitelyNotAccepted);
        return new(shareId, shareUk, items, shareCookie);
    }

    private async Task<string> GetTokenAsync(AccountSession session, CancellationToken cancellationToken)
    {
        var uri = ApiUri("/api/gettemplatevariable", new()
        {
            ["clienttype"] = "0",
            ["app_id"] = "38824127",
            ["web"] = "1",
            ["fields"] = "[\"bdstoken\"]",
        });
        using var document = await GetJsonAsync(session, uri, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(document.RootElement, SubmissionAcceptance.DefinitelyNotAccepted);
        var root = document.RootElement;
        if (root.TryGetProperty("result", out var result)) root = result;
        return ReadString(root, "bdstoken")
            ?? throw new BaiduWebApiException(BaiduErrorKind.ExpiredLogin, SubmissionAcceptance.DefinitelyNotAccepted);
    }

    private async Task<IReadOnlyList<OwnDriveItem>> ListOwnDriveAsync(
        AccountSession session,
        string token,
        string path,
        CancellationToken cancellationToken)
    {
        var uri = ApiUri("/api/list", new()
        {
            ["order"] = "time",
            ["desc"] = "1",
            ["showempty"] = "0",
            ["web"] = "1",
            ["page"] = "1",
            ["num"] = "1000",
            ["dir"] = path,
            ["bdstoken"] = token,
            ["clienttype"] = "0",
        });
        using var document = await GetJsonAsync(session, uri, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(document.RootElement, SubmissionAcceptance.DefinitelyNotAccepted);
        if (!document.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
            return [];
        return list.EnumerateArray().Select(item => new OwnDriveItem(
            ReadString(item, "fs_id", "fsid") ?? string.Empty,
            ReadString(item, "server_filename", "filename") ?? "未命名资源",
            ReadString(item, "path") ?? string.Empty,
            ReadLong(item, "isdir") == 1,
            // local_ctime is the time this account received the item. server_ctime may retain
            // the source file's original creation time after a share transfer.
            ReadUnixTime(item, "local_ctime", "server_ctime")))
            .Where(item => item.Id.Length > 0 && item.Path.Length > 0).ToArray();
    }

    private Task<JsonDocument> GetJsonAsync(
        AccountSession session,
        Uri uri,
        CancellationToken cancellationToken) =>
        SendJsonAsync(session, HttpMethod.Get, uri, null, cancellationToken);

    private Task<JsonDocument> PostFormJsonAsync(
        AccountSession session,
        Uri uri,
        Dictionary<string, string> form,
        CancellationToken cancellationToken,
        string? additionalCookie = null) =>
        SendJsonAsync(session, HttpMethod.Post, uri, new FormUrlEncodedContent(form), cancellationToken, additionalCookie);

    private async Task<JsonDocument> SendJsonAsync(
        AccountSession session,
        HttpMethod method,
        Uri uri,
        HttpContent? content,
        CancellationToken cancellationToken,
        string? additionalCookie = null)
    {
        var text = await SendTextAsync(
            session, method, uri, content, MaximumJsonBytes, cancellationToken, additionalCookie)
            .ConfigureAwait(false);
        try { return JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 96 }); }
        catch (JsonException)
        {
            throw new BaiduWebApiException(BaiduErrorKind.UnknownResponse, SubmissionAcceptance.Unknown);
        }
    }

    private async Task<string> SendTextAsync(
        AccountSession session,
        HttpMethod method,
        Uri uri,
        HttpContent? content,
        int maximumBytes,
        CancellationToken cancellationToken,
        string? additionalCookie = null)
    {
        ValidateUri(uri);
        var cookie = BuildCookieHeader(session.SessionCookies, uri, timeProvider.GetUtcNow());
        if (!string.IsNullOrWhiteSpace(additionalCookie))
            cookie = cookie.Length == 0 ? additionalCookie : $"{cookie}; {additionalCookie}";
        if (cookie.Length == 0)
            throw new BaiduWebApiException(BaiduErrorKind.ExpiredLogin, SubmissionAcceptance.DefinitelyNotAccepted);
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124 Safari/537.36");
        request.Headers.Referrer = Origin;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var deadline = new CancellationTokenSource(requestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            using var response = await http.SendAsync(request, linked.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                throw new BaiduWebApiException(BaiduErrorKind.ExpiredLogin, SubmissionAcceptance.DefinitelyNotAccepted);
            if ((int)response.StatusCode >= 500)
                throw new BaiduWebApiException(BaiduErrorKind.TransientService, SubmissionAcceptance.Unknown);
            if (!response.IsSuccessStatusCode)
                throw new BaiduWebApiException(BaiduErrorKind.UnknownResponse, SubmissionAcceptance.Unknown);
            var bytes = await ReadBoundedAsync(response.Content, maximumBytes, linked.Token).ConfigureAwait(false);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BaiduWebApiException(BaiduErrorKind.TransientService, SubmissionAcceptance.Unknown);
        }
        catch (HttpRequestException)
        {
            throw new BaiduWebApiException(BaiduErrorKind.TransientService, SubmissionAcceptance.Unknown);
        }
        catch (InvalidDataException)
        {
            throw new BaiduWebApiException(BaiduErrorKind.UnknownResponse, SubmissionAcceptance.Unknown);
        }
    }

    private static JsonElement ExtractYunData(string html)
    {
        foreach (var marker in new[] { "window.yunData=", "window.yunData =", "locals.mset(" })
        {
            var markerIndex = html.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0) continue;
            var start = html.IndexOf('{', markerIndex + marker.Length);
            if (start < 0) continue;
            var json = ExtractBalancedObject(html, start);
            if (json is null) continue;
            try
            {
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 96 });
                return document.RootElement.Clone();
            }
            catch (JsonException) { }
        }
        throw new BaiduWebApiException(BaiduErrorKind.InvalidLink, SubmissionAcceptance.DefinitelyNotAccepted);
    }

    private static string? ExtractBalancedObject(string value, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < value.Length; index++)
        {
            var character = value[index];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inString = false;
                continue;
            }
            if (character == '"') inString = true;
            else if (character == '{') depth++;
            else if (character == '}' && --depth == 0) return value[start..(index + 1)];
        }
        return null;
    }

    private static ImmutableArray<ShareSourceItem> ReadShareItems(JsonElement root)
    {
        JsonElement list = default;
        if (root.TryGetProperty("file_list", out var fileList) && fileList.ValueKind == JsonValueKind.Object)
            fileList.TryGetProperty("list", out list);
        if (list.ValueKind != JsonValueKind.Array && root.TryGetProperty("list", out var direct)) list = direct;
        if (list.ValueKind != JsonValueKind.Array) return [];
        return list.EnumerateArray().Select(item => new ShareSourceItem(
            ReadString(item, "fs_id", "fsid") ?? string.Empty,
            ReadString(item, "server_filename", "filename") ?? "未命名资源",
            Math.Max(0, ReadLong(item, "size"))))
            .Where(item => item.Id.Length > 0).ToImmutableArray();
    }

    private static ResolvedEmbeddedFields ExtractEmbeddedShareFields(string html)
    {
        static string? First(string input, string pattern)
        {
            var match = Regex.Match(input, pattern, RegexOptions.CultureInvariant);
            return match.Success ? match.Groups["value"].Value : null;
        }

        var shareId = First(html, "\\\"shareid\\\"\\s*:\\s*\\\"?(?<value>[0-9]+)\\\"?");
        var shareUk = First(html, "\\\"share_uk\\\"\\s*:\\s*\\\"?(?<value>[0-9]+)\\\"?");
        var ids = Regex.Matches(
            html, "\\\"fs_id\\\"\\s*:\\s*\\\"?(?<value>[0-9]+)\\\"?", RegexOptions.CultureInvariant);
        var names = Regex.Matches(
            html, "\\\"server_filename\\\"\\s*:\\s*\\\"(?<value>(?:\\\\.|[^\\\"\\\\])*)\\\"",
            RegexOptions.CultureInvariant);
        var sizes = Regex.Matches(
            html, "\\\"size\\\"\\s*:\\s*\\\"?(?<value>[0-9]+)\\\"?", RegexOptions.CultureInvariant);
        var count = Math.Min(ids.Count, names.Count);
        var items = ImmutableArray.CreateBuilder<ShareSourceItem>(count);
        for (var index = 0; index < count; index++)
        {
            var encodedName = names[index].Groups["value"].Value;
            string name;
            try { name = JsonSerializer.Deserialize<string>($"\"{encodedName}\"") ?? "未命名资源"; }
            catch (JsonException) { name = "未命名资源"; }
            var size = index < sizes.Count && long.TryParse(
                sizes[index].Groups["value"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedSize) ? parsedSize : 0;
            items.Add(new(ids[index].Groups["value"].Value, name, size));
        }
        return new(shareId, shareUk, items.MoveToImmutable());
    }

    private static List<RemoteItemIdentity> ReadTransferredItems(JsonElement root)
    {
        foreach (var containerName in new[] { "extra", "info" })
        {
            if (!root.TryGetProperty(containerName, out var container)) continue;
            if (container.ValueKind == JsonValueKind.Object && container.TryGetProperty("list", out var list))
                return ReadTransferredArray(list);
            if (container.ValueKind == JsonValueKind.Array) return ReadTransferredArray(container);
        }
        return root.TryGetProperty("list", out var rootList) ? ReadTransferredArray(rootList) : [];
    }

    private static List<RemoteItemIdentity> ReadTransferredArray(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array) return [];
        return list.EnumerateArray().Select(item =>
        {
            var id = ReadString(item, "to_fs_id", "fs_id", "fsid");
            var name = ReadString(item, "server_filename", "filename", "to") ?? "未命名资源";
            if (name.Contains('/')) name = name[(name.LastIndexOf('/') + 1)..];
            return id is null ? null : new RemoteItemIdentity(id, name);
        }).Where(item => item is not null).Cast<RemoteItemIdentity>().ToList();
    }

    private static List<RemoteItemIdentity> MatchTransferredItems(
        ImmutableArray<ShareSourceItem> source,
        IReadOnlyList<OwnDriveItem> destination)
    {
        var matches = new List<RemoteItemIdentity>();
        foreach (var item in source)
        {
            var candidates = destination.Where(candidate =>
                string.Equals(candidate.Name, item.Name, StringComparison.Ordinal) ||
                candidate.Name.StartsWith(Path.GetFileNameWithoutExtension(item.Name) + "(", StringComparison.Ordinal))
                .ToArray();
            if (candidates.Length != 1) return [];
            matches.Add(new RemoteItemIdentity(candidates[0].Id, candidates[0].Name));
        }
        return matches;
    }

    private static List<RemoteItemIdentity> MatchTransferredItemsInWindow(
        ImmutableArray<ShareSourceItem> source,
        IReadOnlyList<OwnDriveItem> destination,
        TransferConfirmationQuery query)
    {
        var earliest = query.SubmitWindowStartUtc.AddMinutes(-2);
        var latest = query.SubmitWindowEndUtc.AddMinutes(2);
        var matches = new List<RemoteItemIdentity>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in source)
        {
            var candidates = destination.Where(candidate =>
                    !usedIds.Contains(candidate.Id) &&
                    MatchesTransferredName(item.Name, candidate.Name) &&
                    candidate.CreatedAtUtc is { } created && created >= earliest && created <= latest)
                .OrderByDescending(candidate => candidate.CreatedAtUtc)
                .ThenByDescending(candidate => candidate.Id, StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length == 0)
            {
                // Baidu sometimes preserves both source timestamps during a share transfer.
                // A single name match is still exact; multiple old matches are not safe to guess.
                candidates = destination.Where(candidate =>
                        !usedIds.Contains(candidate.Id) &&
                        MatchesTransferredName(item.Name, candidate.Name))
                    .ToArray();
            }
            if (candidates.Length != 1) return [];
            var selected = candidates[0];
            usedIds.Add(selected.Id);
            matches.Add(new RemoteItemIdentity(selected.Id, selected.Name));
        }
        return matches;
    }

    private static bool MatchesTransferredName(string sourceName, string destinationName)
    {
        if (string.Equals(sourceName, destinationName, StringComparison.Ordinal)) return true;
        var extension = Path.GetExtension(sourceName);
        var stem = Path.GetFileNameWithoutExtension(sourceName);
        return destinationName.StartsWith(stem + "(", StringComparison.Ordinal) &&
            (extension.Length == 0 || destinationName.EndsWith(extension, StringComparison.Ordinal));
    }

    private static void EnsureSuccess(JsonElement root, SubmissionAcceptance acceptance)
    {
        var errno = ReadLong(root, "errno", "error_code");
        if (errno == 0) return;
        var error = errno switch
        {
            -9 => BaiduErrorKind.InvalidExtractionCode,
            -6 or -7 or -10 => BaiduErrorKind.ExpiredLogin,
            -62 or -64 or 9019 => BaiduErrorKind.VerificationRequired,
            -8 or 31034 => BaiduErrorKind.TransientService,
            12 when TryReadDuplicatedPaths(root, out _) => BaiduErrorKind.DuplicateSkipped,
            12 when root.TryGetProperty("target_file_nums_limit", out _) ||
                    root.TryGetProperty("non_vip_limit", out _) ||
                    root.TryGetProperty("target_file_nums", out _) => BaiduErrorKind.FileCountLimitExceeded,
            12 => BaiduErrorKind.TransientService,
            120 or 121 => BaiduErrorKind.InsufficientCapacity,
            2 or 3 or 4 => BaiduErrorKind.InvalidTargetPath,
            _ => BaiduErrorKind.UnknownResponse,
        };
        throw new BaiduWebApiException(error, acceptance);
    }

    private static bool TryReadDuplicatedPaths(JsonElement root, out HashSet<string> paths)
    {
        paths = new HashSet<string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("duplicated", out var duplicated) ||
            duplicated.ValueKind != JsonValueKind.Object ||
            !duplicated.TryGetProperty("list", out var list) ||
            list.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var path = ReadString(item, "path");
            if (path is not null) paths.Add(path);
        }
        return true;
    }

    private static BaiduAdapterResult<T> Failure<T>(BaiduWebApiException exception) =>
        BaiduAdapterResult<T>.Failure(exception.ErrorKind, null,
            submissionAcceptance: exception.SubmissionAcceptance);

    private static Uri ApiUri(string path, Dictionary<string, string> query)
    {
        var encoded = string.Join("&", query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new Uri(Origin, $"{path}?{encoded}");
    }

    private static void ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            !string.Equals(uri.Host, Origin.Host, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new BaiduWebApiException(BaiduErrorKind.InvalidLink, SubmissionAcceptance.DefinitelyNotAccepted);
    }

    private static bool IsStableShareId(string value) =>
        value.Length is >= 5 and <= 128 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static object ParseJsonId(string value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : value;

    private static string ShareIdFromUrl(string shareUrl)
    {
        if (!Uri.TryCreate(shareUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Host, Origin.Host, StringComparison.OrdinalIgnoreCase))
            throw new BaiduWebApiException(BaiduErrorKind.UnknownResponse, SubmissionAcceptance.Unknown);
        var value = uri.Segments.LastOrDefault()?.Trim('/');
        return string.IsNullOrWhiteSpace(value) ?
            throw new BaiduWebApiException(BaiduErrorKind.UnknownResponse, SubmissionAcceptance.Unknown) : value;
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
        }
        return null;
    }

    private static long ReadLong(JsonElement root, params string[] names)
    {
        var value = ReadString(root, names);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static DateTimeOffset? ReadUnixTime(JsonElement root, params string[] names)
    {
        var seconds = ReadLong(root, names);
        if (seconds <= 0) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static string Fingerprint(IEnumerable<string> values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var value in values.Order(StringComparer.Ordinal))
            hash.AppendData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string RandomExtractionCode()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        Span<char> result = stackalloc char[4];
        for (var index = 0; index < result.Length; index++) result[index] = alphabet[bytes[index] % alphabet.Length];
        return new string(result);
    }

    private static string BuildCookieHeader(IEnumerable<SessionCookie> cookies, Uri uri, DateTimeOffset now) =>
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
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes) throw new InvalidDataException();
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes) throw new InvalidDataException();
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private sealed record ShareSourceItem(string Id, string Name, long Size);
    private sealed record ResolvedEmbeddedFields(
        string? ShareId,
        string? ShareUk,
        ImmutableArray<ShareSourceItem> Items);
    private sealed record OwnDriveItem(
        string Id,
        string Name,
        string Path,
        bool IsDirectory,
        DateTimeOffset? CreatedAtUtc);
    private sealed record ResolvedShare(
        string ShareId,
        string ShareUk,
        ImmutableArray<ShareSourceItem> Items,
        string? ShareCookie);

    private sealed class BaiduWebApiException(
        BaiduErrorKind errorKind,
        SubmissionAcceptance submissionAcceptance) : Exception
    {
        public BaiduErrorKind ErrorKind { get; } = errorKind;
        public SubmissionAcceptance SubmissionAcceptance { get; } = submissionAcceptance;
    }
}
