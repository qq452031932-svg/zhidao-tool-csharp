using System.Net;
using System.Text;
using BaiduBatchTransfer.BaiduAdapter.Http;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.Tests;

public sealed class BaiduWebApiClientTests
{
    [Fact]
    public async Task SaveWholeShare_VerifiesCodeTransfersEveryRootItemAndReturnsExactIds()
    {
        var handler = new ScriptedHandler(
            Json("{\"errno\":0,\"result\":{\"bdstoken\":\"token-1\"}}"),
            Json("{\"errno\":0,\"randsk\":\"secret-key\"}"),
            Html("<script>window.yunData={\"shareid\":42,\"share_uk\":88,\"file_list\":{\"list\":[{\"fs_id\":11,\"server_filename\":\"game.apk\",\"size\":123,\"isdir\":0},{\"fs_id\":12,\"server_filename\":\"readme.txt\",\"size\":7,\"isdir\":0}]}};</script>"),
            Json("{\"errno\":0,\"request_id\":\"req-1\",\"extra\":{\"list\":[{\"to_fs_id\":9001,\"server_filename\":\"game.apk\"},{\"to_fs_id\":9002,\"server_filename\":\"readme.txt\"}]}}"));
        var client = new BaiduWebApiClient(handler, TimeProvider.System);

        var result = await client.SaveWholeShareAsync(
            Session(),
            new ShareReference("1rf-nQSX7pDPwTOe8WCi_7g", "https://pan.baidu.com/s/1rf-nQSX7pDPwTOe8WCi_7g", "1111"),
            new RemoteDirectory("/测试目录", new RemotePath("/测试目录")),
            DuplicatePolicy.Rename,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("req-1", result.Value!.AdapterRequestId);
        Assert.Equal(["9001", "9002"], result.Value.RemoteItemIds);
        Assert.Equal(["game.apk", "readme.txt"], result.Value.ExactRemoteItems!.Items.Select(item => item.DisplayedTopLevelName));

        Assert.Collection(handler.Requests,
            request => Assert.Equal("/api/gettemplatevariable", request.Uri.AbsolutePath),
            request =>
            {
                Assert.Equal("/share/verify", request.Uri.AbsolutePath);
                Assert.Contains("surl=rf-nQSX7pDPwTOe8WCi_7g", request.Uri.Query, StringComparison.Ordinal);
                Assert.Contains("pwd=1111", request.Body, StringComparison.Ordinal);
            },
            request =>
            {
                Assert.Equal("/s/1rf-nQSX7pDPwTOe8WCi_7g", request.Uri.AbsolutePath);
                Assert.Contains("BDCLND=secret-key", request.Cookie, StringComparison.Ordinal);
            },
            request =>
            {
                Assert.Equal("/share/transfer", request.Uri.AbsolutePath);
                Assert.Contains("fsidlist=%5B11%2C12%5D", request.Body, StringComparison.Ordinal);
                Assert.Contains("path=%2F%E6%B5%8B%E8%AF%95%E7%9B%AE%E5%BD%95", request.Body, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task CreatePermanentShare_UsesExactTransferredIdsAndReturnsObservedSettings()
    {
        var handler = new ScriptedHandler(
            Json("{\"errno\":0,\"result\":{\"bdstoken\":\"token-1\"}}"),
            Json("{\"errno\":0,\"request_id\":\"req-share\",\"shareid\":\"share-9\",\"link\":\"https://pan.baidu.com/s/1share9\",\"pwd\":\"a1b2\"}"));
        var client = new BaiduWebApiClient(handler, TimeProvider.System);
        var request = new PermanentShareRequest(new RemoteItemIdentitySet(
        [
            new RemoteItemIdentity("9001", "game.apk"),
            new RemoteItemIdentity("9002", "readme.txt"),
        ]), "a1b2");

        var result = await client.CreatePermanentShareAsync(Session(), request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsFinalAcceptance);
        Assert.Equal("https://pan.baidu.com/s/1share9", result.Value.ShareUrl);
        Assert.Equal("a1b2", result.Value.ExtractionCode);
        var submit = Assert.Single(handler.Requests, item => item.Uri.AbsolutePath == "/share/set");
        Assert.Contains("period=0", submit.Body, StringComparison.Ordinal);
        Assert.Contains("fid_list=%5B9001%2C9002%5D", submit.Body, StringComparison.Ordinal);
        Assert.Contains("pwd=a1b2", submit.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerificationChallenge_IsReturnedAsVerificationRequiredWithoutLeakingPayload()
    {
        var handler = new ScriptedHandler(
            Json("{\"errno\":0,\"result\":{\"bdstoken\":\"token-1\"}}"),
            Json("{\"errno\":-62,\"vcode_str\":\"private-captcha-token\"}"));
        var client = new BaiduWebApiClient(handler, TimeProvider.System);

        var result = await client.InspectShareAsync(
            Session(),
            new ShareReference("1share", "https://pan.baidu.com/s/1share", "1111"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduErrorKind.VerificationRequired, result.ErrorKind);
        Assert.DoesNotContain("private-captcha-token", result.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AmbiguousTransferTransportFailure_IsRetriedOnceThenReturnsExactSuccess()
    {
        var handler = new FlakyTransferHandler(failEveryTransfer: false);
        var client = new BaiduWebApiClient(handler, TimeProvider.System);

        var result = await client.SaveWholeShareAsync(
            Session(),
            new ShareReference("1share", "https://pan.baidu.com/s/1share"),
            new RemoteDirectory("/target", new RemotePath("/target")),
            DuplicatePolicy.Rename,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.TransferCalls);
        Assert.Equal("9001", Assert.Single(result.Value!.RemoteItemIds));
    }

    [Fact]
    public async Task TransferFailureAfterRetry_IsFinalFailureInsteadOfPendingConfirmation()
    {
        var handler = new FlakyTransferHandler(failEveryTransfer: true);
        var client = new BaiduWebApiClient(handler, TimeProvider.System);

        var result = await client.SaveWholeShareAsync(
            Session(),
            new ShareReference("1share", "https://pan.baidu.com/s/1share"),
            new RemoteDirectory("/target", new RemotePath("/target")),
            DuplicatePolicy.Rename,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(2, handler.TransferCalls);
        Assert.Equal(BaiduErrorKind.TransientService, result.ErrorKind);
        Assert.Equal(SubmissionAcceptance.DefinitelyNotAccepted, result.SubmissionAcceptance);
    }

    [Fact]
    public async Task ShareTransportFailure_IsRetryableByTheExistingQueueAndNeverMarkedPending()
    {
        var handler = new ShareFailureHandler();
        var client = new BaiduWebApiClient(handler, TimeProvider.System);
        var request = new PermanentShareRequest(new RemoteItemIdentitySet(
            [new RemoteItemIdentity("9001", "game.apk")]));

        var result = await client.CreatePermanentShareAsync(Session(), request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduErrorKind.TransientService, result.ErrorKind);
        Assert.Equal(SubmissionAcceptance.DefinitelyNotAccepted, result.SubmissionAcceptance);
    }

    [Fact]
    public async Task InspectShare_FallsBackToEmbeddedPageFieldsWhenYunDataMarkerChanges()
    {
        var handler = new ScriptedHandler(
            Json("{\"errno\":0,\"result\":{\"bdstoken\":\"token-1\"}}"),
            Html("<script>var context={\"shareid\":42,\"share_uk\":\"88\"};var files=[{\"fs_id\":11,\"server_filename\":\"game.apk\",\"size\":123},{\"fs_id\":12,\"server_filename\":\"readme.txt\",\"size\":7}];</script>"));
        var client = new BaiduWebApiClient(handler, TimeProvider.System);

        var result = await client.InspectShareAsync(
            Session(),
            new ShareReference("1share", "https://pan.baidu.com/s/1share"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.ItemCount);
        Assert.Equal(130, result.Value.TotalBytes);
    }

    [Fact]
    public async Task ConfirmExactTransfer_UsesSourceNamesAndSubmitWindowToRecoverRenamedItems()
    {
        var now = new DateTimeOffset(2026, 9, 1, 9, 20, 0, TimeSpan.Zero);
        var created = now.AddMinutes(-1).ToUnixTimeSeconds();
        var old = now.AddDays(-1).ToUnixTimeSeconds();
        var handler = new ScriptedHandler(
            Json("{\"errno\":0,\"result\":{\"bdstoken\":\"token-1\"}}"),
            Json("{\"errno\":0,\"randsk\":\"secret-key\"}"),
            Html("<script>window.yunData={\"shareid\":42,\"share_uk\":88,\"file_list\":{\"list\":[{\"fs_id\":11,\"server_filename\":\"game.apk\",\"size\":123,\"isdir\":0}]}};</script>"),
            Json($$"""{"errno":0,"list":[{"fs_id":8001,"server_filename":"game.apk","path":"/测试目录/game.apk","isdir":0,"server_ctime":{{old}},"local_ctime":{{old}}},{"fs_id":9001,"server_filename":"game(1).apk","path":"/测试目录/game(1).apk","isdir":0,"server_ctime":{{old}},"local_ctime":{{created}}}]}"""));
        var client = new BaiduWebApiClient(handler, new FixedTimeProvider(now));
        var query = new TransferConfirmationQuery(
            "1share",
            new RemotePath("/测试目录"),
            "outer-fingerprint",
            "operation-fingerprint",
            now.AddMinutes(-2),
            now.AddMinutes(8));

        var result = await client.ConfirmExactTransferAsync(
            Session(),
            new ShareReference("1share", "https://pan.baidu.com/s/1share", "1111"),
            query,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(TransferConfirmationState.ExactPresent, result.Value!.State);
        var item = Assert.Single(result.Value.RemoteItems!.Items);
        Assert.Equal("9001", item.ExactId);
        Assert.Equal("game(1).apk", item.DisplayedTopLevelName);
    }

    [Fact]
    public async Task ConfirmExactTransfer_UsesUniqueNameWhenBaiduPreservesSourceTimestamps()
    {
        var now = new DateTimeOffset(2026, 9, 1, 9, 20, 0, TimeSpan.Zero);
        var preserved = now.AddYears(-1).ToUnixTimeSeconds();
        var handler = new ScriptedHandler(
            Json("{\"errno\":0,\"result\":{\"bdstoken\":\"token-1\"}}"),
            Html("<script>window.yunData={\"shareid\":42,\"share_uk\":88,\"file_list\":{\"list\":[{\"fs_id\":11,\"server_filename\":\"game.apk\",\"size\":123,\"isdir\":0}]}};</script>"),
            Json($$"""{"errno":0,"list":[{"fs_id":9001,"server_filename":"game.apk","path":"/target/game.apk","isdir":0,"server_ctime":{{preserved}},"local_ctime":{{preserved}}}]}"""));
        var client = new BaiduWebApiClient(handler, new FixedTimeProvider(now));
        var query = new TransferConfirmationQuery(
            "1share", new RemotePath("/target"), "outer", "operation",
            now.AddMinutes(-2), now.AddMinutes(8));

        var result = await client.ConfirmExactTransferAsync(
            Session(), new ShareReference("1share", "https://pan.baidu.com/s/1share"), query, default);

        Assert.True(result.IsSuccess);
        Assert.Equal(TransferConfirmationState.ExactPresent, result.Value!.State);
        Assert.Equal("9001", Assert.Single(result.Value.RemoteItems!.Items).ExactId);
    }

    [Fact]
    public async Task ConfirmExactTransfer_DoesNotGuessAmongMultipleOldNameMatches()
    {
        var now = new DateTimeOffset(2026, 9, 1, 9, 20, 0, TimeSpan.Zero);
        var preserved = now.AddYears(-1).ToUnixTimeSeconds();
        var handler = new ScriptedHandler(
            Json("{\"errno\":0,\"result\":{\"bdstoken\":\"token-1\"}}"),
            Html("<script>window.yunData={\"shareid\":42,\"share_uk\":88,\"file_list\":{\"list\":[{\"fs_id\":11,\"server_filename\":\"game.apk\",\"size\":123,\"isdir\":0}]}};</script>"),
            Json($$"""{"errno":0,"list":[{"fs_id":9001,"server_filename":"game.apk","path":"/target/game.apk","isdir":0,"server_ctime":{{preserved}},"local_ctime":{{preserved}}},{"fs_id":9002,"server_filename":"game(1).apk","path":"/target/game(1).apk","isdir":0,"server_ctime":{{preserved}},"local_ctime":{{preserved}}}]}"""));
        var client = new BaiduWebApiClient(handler, new FixedTimeProvider(now));
        var query = new TransferConfirmationQuery(
            "1share", new RemotePath("/target"), "outer", "operation",
            now.AddMinutes(-2), now.AddMinutes(8));

        var result = await client.ConfirmExactTransferAsync(
            Session(), new ShareReference("1share", "https://pan.baidu.com/s/1share"), query, default);

        Assert.True(result.IsSuccess);
        Assert.Equal(TransferConfirmationState.Ambiguous, result.Value!.State);
    }

    private static AccountSession Session() => new(
        Guid.NewGuid(), "123",
        [new SessionCookie("BDUSS", "secret", ".baidu.com", "/", null, true, true)],
        DateTimeOffset.UtcNow);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Html(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "text/html"),
    };

    private sealed class ScriptedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(
                request.RequestUri!,
                body,
                request.Headers.TryGetValues("Cookie", out var values) ? values.Single() : string.Empty));
            if (responses.Count == 0) throw new InvalidOperationException("Unexpected request.");
            return responses.Dequeue();
        }
    }

    private sealed record CapturedRequest(Uri Uri, string Body, string Cookie);

    private sealed class FlakyTransferHandler(bool failEveryTransfer) : HttpMessageHandler
    {
        public int TransferCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/gettemplatevariable")
                return Task.FromResult(Json("{\"errno\":0,\"result\":{\"bdstoken\":\"token-1\"}}"));
            if (path == "/s/1share")
                return Task.FromResult(Html("<script>window.yunData={\"shareid\":42,\"share_uk\":88,\"file_list\":{\"list\":[{\"fs_id\":11,\"server_filename\":\"game.apk\",\"size\":123,\"isdir\":0}]}};</script>"));
            if (path == "/share/transfer")
            {
                TransferCalls++;
                if (failEveryTransfer || TransferCalls == 1)
                    throw new HttpRequestException("synthetic transport failure");
                return Task.FromResult(Json("{\"errno\":0,\"request_id\":\"req-2\",\"extra\":{\"list\":[{\"to_fs_id\":9001,\"server_filename\":\"game.apk\"}]}}"));
            }
            if (path == "/api/list")
                return Task.FromResult(Json("{\"errno\":0,\"list\":[]}"));
            throw new InvalidOperationException($"Unexpected request: {path}");
        }
    }

    private sealed class ShareFailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            request.RequestUri!.AbsolutePath == "/api/gettemplatevariable"
                ? Task.FromResult(Json("{\"errno\":0,\"result\":{\"bdstoken\":\"token-1\"}}"))
                : throw new HttpRequestException("synthetic transport failure");
    }
}
