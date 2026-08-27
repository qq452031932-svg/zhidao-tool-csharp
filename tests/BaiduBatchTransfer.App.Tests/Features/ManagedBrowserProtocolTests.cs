using BaiduBatchTransfer.App.Features.ManagedBrowser;
using System.Buffers.Binary;
using System.Text;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedBrowserProtocolTests
{
    private static readonly Guid CorrelationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OperationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AccountId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly string Nonce = new('a', 64);

    [Fact]
    public async Task FrameUsesFourByteBigEndianLengthAndUtf8Json()
    {
        var request = HandshakeRequest();
        await using var stream = new MemoryStream();

        await ManagedBrowserProtocol.WriteRequestAsync(stream, request, CancellationToken.None);

        var bytes = stream.ToArray();
        Assert.Equal(bytes.Length - 4, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(0, 4)));
        var json = new UTF8Encoding(false, true).GetString(bytes.AsSpan(4));
        Assert.Contains("\"protocolVersion\":1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\uFEFF", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FragmentedAndCoalescedFramesAreReadExactlyOnce()
    {
        var first = ManagedBrowserProtocol.EncodeRequest(HandshakeRequest());
        var secondRequest = Request(ManagedBrowserAction.Health, ManagedBrowserRequestPayload.Empty);
        var second = ManagedBrowserProtocol.EncodeRequest(secondRequest);
        await using var stream = new FragmentedReadStream(first.Concat(second).ToArray(), 1, 2, 3, 5, 8);

        var decodedFirst = await ManagedBrowserProtocol.ReadRequestAsync(stream, CancellationToken.None);
        var decodedSecond = await ManagedBrowserProtocol.ReadRequestAsync(stream, CancellationToken.None);
        var eof = await ManagedBrowserProtocol.ReadRequestAsync(stream, CancellationToken.None);

        Assert.Equal(ManagedBrowserAction.Handshake, decodedFirst!.Action);
        Assert.Equal(ManagedBrowserAction.Health, decodedSecond!.Action);
        Assert.Null(eof);
    }

    [Fact]
    public async Task CleanEofIsDistinctFromTruncatedHeaderAndPayload()
    {
        Assert.Null(await ManagedBrowserProtocol.ReadRequestAsync(
            new MemoryStream(), CancellationToken.None));

        var headerError = await Assert.ThrowsAsync<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.ReadRequestAsync(
                new MemoryStream([0, 0]), CancellationToken.None));
        Assert.Equal(ManagedBrowserProtocolError.UnexpectedEof, headerError.Error);

        var payloadError = await Assert.ThrowsAsync<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.ReadRequestAsync(
                new MemoryStream([0, 0, 0, 4, (byte)'{']), CancellationToken.None));
        Assert.Equal(ManagedBrowserProtocolError.UnexpectedEof, payloadError.Error);
    }

    [Fact]
    public async Task PendingReadHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await using var stream = new NeverCompletingReadStream();

        var pending = ManagedBrowserProtocol.ReadRequestAsync(stream, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65537)]
    public async Task InvalidFrameLengthIsRejectedBeforeReadingPayload(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, length);
        var error = await Assert.ThrowsAsync<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.ReadRequestAsync(new MemoryStream(header), CancellationToken.None));
        Assert.Equal(ManagedBrowserProtocolError.FrameSizeInvalid, error.Error);
    }

    [Fact]
    public async Task MalformedUtf8AndJsonHaveDistinctTypedErrors()
    {
        var utf8 = await Assert.ThrowsAsync<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.ReadRequestAsync(Frame([0xff]), CancellationToken.None));
        Assert.Equal(ManagedBrowserProtocolError.MalformedUtf8, utf8.Error);

        var json = await Assert.ThrowsAsync<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.ReadRequestAsync(Frame(Encoding.UTF8.GetBytes("{")), CancellationToken.None));
        Assert.Equal(ManagedBrowserProtocolError.MalformedJson, json.Error);
    }

    [Theory]
    [InlineData("protocolVersion", "2", ManagedBrowserProtocolError.ProtocolVersionMismatch)]
    [InlineData("executionEngineVersion", "\"WebView2-v1\"", ManagedBrowserProtocolError.ExecutionEngineVersionMismatch)]
    [InlineData("action", "\"RunScript\"", ManagedBrowserProtocolError.UnknownAction)]
    public void VersionEngineAndActionMismatchesAreRejected(
        string property,
        string replacement,
        ManagedBrowserProtocolError expected)
    {
        var json = HandshakeJson().Replace(
            property switch
            {
                "protocolVersion" => "\"protocolVersion\":1",
                "executionEngineVersion" => "\"executionEngineVersion\":\"ManagedChromium-v1\"",
                _ => "\"action\":\"Handshake\"",
            }, $"\"{property}\":{replacement}", StringComparison.Ordinal);

        var error = Assert.Throws<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.DecodeRequest(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(expected, error.Error);
    }

    [Theory]
    [InlineData("\"protocolVersion\":1", "\"protocolVersion\":\"1\"", ManagedBrowserProtocolError.ProtocolVersionMismatch)]
    [InlineData("\"executionEngineVersion\":\"ManagedChromium-v1\"", "\"executionEngineVersion\":1", ManagedBrowserProtocolError.ExecutionEngineVersionMismatch)]
    [InlineData("\"action\":\"Handshake\"", "\"action\":1", ManagedBrowserProtocolError.UnknownAction)]
    public void MismatchedDiscriminatorTypesUseTheSameTypedErrorsAsPython(
        string original,
        string replacement,
        ManagedBrowserProtocolError expected)
    {
        var json = HandshakeJson().Replace(original, replacement, StringComparison.Ordinal);

        var error = Assert.Throws<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.DecodeRequest(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(expected, error.Error);
    }

    [Theory]
    [InlineData("unexpected")]
    [InlineData("cookie")]
    [InlineData("Authorization")]
    [InlineData("extractionCode")]
    public void UnknownAndSecretLikeFieldsAreRejectedRecursively(string field)
    {
        var json = HandshakeJson().Replace(
            "\"payload\":{",
            $"\"payload\":{{\"{field}\":\"do-not-accept\",", StringComparison.Ordinal);

        var error = Assert.Throws<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.DecodeRequest(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(
            field.Equals("unexpected", StringComparison.Ordinal)
                ? ManagedBrowserProtocolError.UnknownField
                : ManagedBrowserProtocolError.ForbiddenField,
            error.Error);
        Assert.DoesNotContain("do-not-accept", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateJsonFieldsAreRejected()
    {
        var json = HandshakeJson().Replace(
            "\"generation\":1",
            "\"generation\":1,\"generation\":1", StringComparison.Ordinal);

        var error = Assert.Throws<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.DecodeRequest(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(ManagedBrowserProtocolError.DuplicateField, error.Error);
    }

    [Fact]
    public void HandshakeCapabilitiesAreFixedAndNonceIsRedacted()
    {
        var request = HandshakeRequest();
        var payload = Assert.IsType<ManagedBrowserHandshakeRequest>(request.Payload);

        Assert.Equal(ManagedBrowserProtocol.RequiredCapabilities, payload.RequiredCapabilities);
        Assert.DoesNotContain(Nonce, request.ToString(), StringComparison.Ordinal);

        var altered = HandshakeJson().Replace(
            "\"ObserveAccountIdentity\"",
            "\"ExecuteJavaScript\"", StringComparison.Ordinal);
        var error = Assert.Throws<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.DecodeRequest(Encoding.UTF8.GetBytes(altered)));
        Assert.Equal(ManagedBrowserProtocolError.CapabilityMismatch, error.Error);
    }

    [Fact]
    public void ObserveIdentityBindingAndTypedResponsesAreStrictAndRedacted()
    {
        var browserId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var request = Request(
            ManagedBrowserAction.ObserveAccountIdentity,
            new ManagedBrowserObserveAccountIdentityRequest(
                browserId, "target-1", new Uri("http://127.0.0.1:43127/"), 30_000));
        var decoded = ManagedBrowserProtocol.DecodeRequest(
            ManagedBrowserProtocol.EncodeRequestPayload(request));
        var payload = Assert.IsType<ManagedBrowserObserveAccountIdentityRequest>(decoded.Payload);
        Assert.Equal(browserId, payload.BrowserId);
        Assert.Equal("target-1", payload.TargetId);

        const string uid = "stable-secret-uid";
        var response = ManagedBrowserResponse.For(
            request, new ManagedBrowserIdentityResult(ManagedBrowserIdentityResultKind.Observed, uid));
        var roundTrip = ManagedBrowserProtocol.DecodeResponse(
            ManagedBrowserProtocol.EncodeResponsePayload(response));
        var result = Assert.IsType<ManagedBrowserIdentityResult>(roundTrip.Result);
        Assert.Equal(uid, result.StableBaiduUserId);
        Assert.DoesNotContain(uid, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(uid, response.ToString(), StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => new ManagedBrowserObserveAccountIdentityRequest(
            browserId, "", new Uri("http://127.0.0.1:43127/"), 30_000));

        var leaked = Encoding.UTF8.GetString(ManagedBrowserProtocol.EncodeResponsePayload(response))
            .Replace("\"stableBaiduUserId\":\"stable-secret-uid\"",
                "\"stableBaiduUserId\":\"stable-secret-uid\",\"pageUrl\":\"https://pan.baidu.com/private\"",
                StringComparison.Ordinal);
        var leakError = Assert.Throws<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.DecodeResponse(Encoding.UTF8.GetBytes(leaked)));
        Assert.Equal(ManagedBrowserProtocolError.ForbiddenField, leakError.Error);
    }

    [Theory]
    [InlineData("https://pan.baidu.com/", ManagedBrowserRoute.PanRoot)]
    [InlineData("https://passport.baidu.com/", ManagedBrowserRoute.PassportRoot)]
    public void OnlyExactHttpsTopLevelRoutesAreAllowed(string value, ManagedBrowserRoute expected)
    {
        Assert.Equal(expected, ManagedBrowserRoutePolicy.Classify(new Uri(value)));
    }

    [Theory]
    [InlineData("http://pan.baidu.com/")]
    [InlineData("https://user@pan.baidu.com/")]
    [InlineData("https://pan.baidu.com:444/")]
    [InlineData("https://sub.pan.baidu.com/")]
    [InlineData("https://pan.baidu.com.evil.test/")]
    [InlineData("https://pan.baidu.com/disk/home")]
    [InlineData("https://pan.baidu.com/?query=1")]
    public void LookalikeOrNonRootRoutesAreRejected(string value)
    {
        Assert.Equal(ManagedBrowserRoute.Rejected, ManagedBrowserRoutePolicy.Classify(new Uri(value)));
    }

    [Fact]
    public void ResponseMustMatchEveryRequestIdentityField()
    {
        var request = Request(ManagedBrowserAction.Health, ManagedBrowserRequestPayload.Empty);
        var response = ManagedBrowserResponse.For(request, ManagedBrowserHealthResult.Healthy);
        ManagedBrowserProtocol.ValidateResponseFor(request, response);

        var wrong = new ManagedBrowserResponse(
            response.CorrelationId,
            Guid.NewGuid(),
            response.AccountId,
            response.Generation,
            response.Action,
            response.Result);
        var error = Assert.Throws<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.ValidateResponseFor(request, wrong));
        Assert.Equal(ManagedBrowserProtocolError.ResponseIdentityMismatch, error.Error);
    }

    private static ManagedBrowserRequest HandshakeRequest() =>
        Request(ManagedBrowserAction.Handshake,
            new ManagedBrowserHandshakeRequest(Nonce, 1234, ManagedBrowserProtocol.RequiredCapabilities));

    private static ManagedBrowserRequest Request(
        ManagedBrowserAction action,
        ManagedBrowserRequestPayload payload) =>
        new(CorrelationId, OperationId, AccountId, 1, action, payload);

    private static string HandshakeJson() =>
        Encoding.UTF8.GetString(ManagedBrowserProtocol.EncodeRequestPayload(HandshakeRequest()));

    private static MemoryStream Frame(byte[] payload)
    {
        var bytes = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(0, 4), payload.Length);
        payload.CopyTo(bytes, 4);
        return new MemoryStream(bytes);
    }

    private sealed class FragmentedReadStream(byte[] bytes, params int[] chunks) : Stream
    {
        private int offset;
        private int chunkIndex;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (offset >= bytes.Length) return 0;
            var chunk = chunks[chunkIndex++ % chunks.Length];
            var count = Math.Min(Math.Min(chunk, buffer.Length), bytes.Length - offset);
            bytes.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            return count;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => offset; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NeverCompletingReadStream : Stream
    {
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
