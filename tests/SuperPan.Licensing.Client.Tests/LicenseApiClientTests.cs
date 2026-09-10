using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuperPan.Licensing.Client;

namespace SuperPan.Licensing.Client.Tests;

public sealed class LicenseApiClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<HttpStatusCode, string, string, LicenseApiResultKind> SignedMappings => new()
    {
        { HttpStatusCode.OK, LicenseDecisions.Allow, LicenseCodes.Ok, LicenseApiResultKind.Authorized },
        { HttpStatusCode.BadRequest, LicenseDecisions.Deny, LicenseCodes.InvalidRequest, LicenseApiResultKind.Rejected },
        { HttpStatusCode.BadRequest, LicenseDecisions.Deny, LicenseCodes.UnsupportedProtocol, LicenseApiResultKind.Rejected },
        { HttpStatusCode.Forbidden, LicenseDecisions.Deny, LicenseCodes.LicenseNotFound, LicenseApiResultKind.Rejected },
        { HttpStatusCode.Forbidden, LicenseDecisions.Deny, LicenseCodes.LicenseExpired, LicenseApiResultKind.Rejected },
        { HttpStatusCode.Forbidden, LicenseDecisions.Deny, LicenseCodes.AppNotAllowed, LicenseApiResultKind.Rejected },
        { HttpStatusCode.Forbidden, LicenseDecisions.Deny, LicenseCodes.AppDisabled, LicenseApiResultKind.Rejected },
        { HttpStatusCode.Conflict, LicenseDecisions.Deny, LicenseCodes.MachineMismatch, LicenseApiResultKind.Rejected },
    };

    [Theory]
    [MemberData(nameof(SignedMappings))]
    public async Task LicenseApiClientMapsEverySignedHttpDecision(
        HttpStatusCode status,
        string decision,
        string code,
        LicenseApiResultKind expected)
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var handler = new DelegateHandler(async (request, _, _) =>
        {
            var requestId = await ReadRequestIdAsync(request);
            var claims = SignedLicenseEnvelopeVerifierTests.CreateClaims(requestId, decision, code);
            return Response(status, SignedLicenseEnvelopeVerifierTests.Sign(claims, signer));
        });
        using var client = CreateClient(signer, handler);

        var result = await client.ValidateAsync("request-1", "CARD-VALUE", "machine-1", CancellationToken.None);

        Assert.Equal(expected, result.Kind);
        Assert.Equal(code, result.Code);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task LicenseApiClientRejectsStatusAndSignedCodeMismatchWithoutRetry()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var claims = SignedLicenseEnvelopeVerifierTests.CreateClaims("request-1", LicenseDecisions.Deny, LicenseCodes.LicenseExpired);
        var handler = new DelegateHandler((_, _, _) => Task.FromResult(
            Response(HttpStatusCode.BadRequest, SignedLicenseEnvelopeVerifierTests.Sign(claims, signer))));
        using var client = CreateClient(signer, handler);

        var result = await client.ValidateAsync("request-1", "CARD-VALUE", "machine-1", CancellationToken.None);

        Assert.Equal(LicenseApiResultKind.InvalidResponse, result.Kind);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task LicenseApiClientDoesNotRetryDefinitiveFourHundredResponses()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var handler = new DelegateHandler(async (request, _, _) =>
        {
            var requestId = await ReadRequestIdAsync(request);
            var claims = SignedLicenseEnvelopeVerifierTests.CreateClaims(requestId, LicenseDecisions.Deny, LicenseCodes.InvalidRequest);
            return Response(HttpStatusCode.BadRequest, SignedLicenseEnvelopeVerifierTests.Sign(claims, signer));
        });
        var delay = new RecordingDelay();
        using var client = CreateClient(signer, handler, delay: delay);

        var result = await client.ActivateAsync("activation-stable", "CARD-VALUE", "machine-1", CancellationToken.None);

        Assert.Equal(LicenseApiResultKind.Rejected, result.Kind);
        Assert.Equal(1, handler.SendCount);
        Assert.Empty(delay.Delays);
    }

    [Fact]
    public async Task LicenseApiClientRetriesUnsigned429AtMostThreeSendsAndCapsLargerRetryAfterAtTenSeconds()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var handler = new DelegateHandler((_, _, _) =>
        {
            var response = Response(HttpStatusCode.TooManyRequests, Encoding.UTF8.GetBytes("{\"code\":\"rate_limited\"}"));
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return Task.FromResult(response);
        });
        var delay = new RecordingDelay();
        var random = new TestRandomSource([0, 250]);
        using var client = CreateClient(signer, handler, delay: delay, random: random);

        var result = await client.ValidateAsync("request-1", "CARD-VALUE", "machine-1", CancellationToken.None);

        Assert.Equal(LicenseApiResultKind.TemporaryFailure, result.Kind);
        Assert.Equal(LicenseCodes.RateLimited, result.Code);
        Assert.Equal(3, handler.SendCount);
        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)], delay.Delays);
    }

    [Fact]
    public async Task LicenseApiClientUsesExponentialBackoffAndInclusiveJitterWithoutSleeping()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var handler = new DelegateHandler((_, _, _) => Task.FromResult(Response(HttpStatusCode.ServiceUnavailable, [])));
        var delay = new RecordingDelay();
        var random = new TestRandomSource([0, 250]);
        using var client = CreateClient(signer, handler, delay: delay, random: random);

        var result = await client.ValidateAsync("request-1", "CARD-VALUE", "machine-1", CancellationToken.None);

        Assert.Equal(LicenseApiResultKind.TemporaryFailure, result.Kind);
        Assert.Equal(LicenseCodes.ServiceUnavailable, result.Code);
        Assert.Equal(3, handler.SendCount);
        Assert.Equal([TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(750)], delay.Delays);
    }

    [Fact]
    public async Task LicenseApiClientRetriesTransportFailuresAtMostThreeTotalSends()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var handler = new DelegateHandler((_, _, _) => throw new HttpRequestException("offline"));
        var delay = new RecordingDelay();
        using var client = CreateClient(signer, handler, delay: delay);

        var result = await client.ValidateAsync("request-1", "CARD-VALUE", "machine-1", CancellationToken.None);

        Assert.Equal(LicenseApiResultKind.TemporaryFailure, result.Kind);
        Assert.Equal(3, handler.SendCount);
        Assert.Equal(2, delay.Delays.Count);
    }

    [Fact]
    public async Task LicenseApiClientRetriesFiveHundredThenAcceptsSignedSuccess()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var handler = new DelegateHandler(async (request, attempt, _) =>
        {
            if (attempt < 3)
            {
                return Response(HttpStatusCode.BadGateway, []);
            }
            var requestId = await ReadRequestIdAsync(request);
            return Response(HttpStatusCode.OK, SignedLicenseEnvelopeVerifierTests.Sign(
                SignedLicenseEnvelopeVerifierTests.CreateClaims(requestId), signer));
        });
        var delay = new RecordingDelay();
        using var client = CreateClient(signer, handler, delay: delay);

        var result = await client.ValidateAsync("request-1", "CARD-VALUE", "machine-1", CancellationToken.None);

        Assert.Equal(LicenseApiResultKind.Authorized, result.Kind);
        Assert.Equal(3, handler.SendCount);
    }

    [Fact]
    public async Task LicenseApiClientReturnsExactVerifiedEnvelopeWithoutLeakingItFromDiagnostics()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[]? envelope = null;
        var handler = new DelegateHandler(async (request, _, _) =>
        {
            var requestId = await ReadRequestIdAsync(request);
            envelope = SignedLicenseEnvelopeVerifierTests.Sign(
                SignedLicenseEnvelopeVerifierTests.CreateClaims(requestId), signer);
            return Response(HttpStatusCode.OK, envelope);
        });
        using var client = CreateClient(signer, handler);

        var result = await client.ValidateAsync("request-1", "CARD-VALUE", "machine-1", CancellationToken.None);

        Assert.Equal(envelope, result.SignedEnvelope);
        Assert.DoesNotContain(Convert.ToBase64String(envelope!), result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Encoding.UTF8.GetString(envelope!), result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LicenseApiClientActivationRetriesReuseBusinessIdButRefreshTimestampAndNonce()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bodies = new List<JsonElement>();
        var handler = new DelegateHandler(async (request, _, _) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            bodies.Add(document.RootElement.Clone());
            return Response(HttpStatusCode.ServiceUnavailable, []);
        });
        var time = new SteppingTimeProvider(Now, TimeSpan.FromSeconds(1));
        var random = new TestRandomSource([0, 0], nonceSeed: 1);
        using var client = CreateClient(signer, handler, time, new RecordingDelay(), random);

        await client.ActivateAsync("activation-stable", "CARD-VALUE", "machine-1", CancellationToken.None);

        Assert.Equal(3, bodies.Count);
        Assert.All(bodies, body => Assert.Equal("activation-stable", body.GetProperty("activation_request_id").GetString()));
        Assert.Equal(3, bodies.Select(body => body.GetProperty("timestamp").GetString()).Distinct().Count());
        Assert.Equal(3, bodies.Select(body => body.GetProperty("nonce").GetString()).Distinct().Count());
        Assert.All(bodies, body => Assert.Equal(32, body.GetProperty("nonce").GetString()!.Length));
    }

    [Fact]
    public async Task LicenseApiClientCancellationStopsBeforeSendAndDuringRetryDelay()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var handler = new DelegateHandler((_, _, _) => Task.FromResult(Response(HttpStatusCode.ServiceUnavailable, [])));
        using var beforeSend = CreateClient(signer, handler);
        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            beforeSend.ValidateAsync("request-1", "CARD-VALUE", "machine-1", alreadyCancelled.Token));
        Assert.Equal(0, handler.SendCount);

        var blockingDelay = new BlockingDelay();
        using var duringDelay = CreateClient(signer, handler, delay: blockingDelay);
        using var cancellation = new CancellationTokenSource();
        var task = duringDelay.ValidateAsync("request-2", "CARD-VALUE", "machine-1", cancellation.Token);
        await blockingDelay.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task LicenseApiClientRejectsOversizedSignedResponseWithoutRetry()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var handler = new DelegateHandler((_, _, _) => Task.FromResult(
            Response(HttpStatusCode.OK, Enumerable.Repeat((byte)'x', 257).ToArray())));
        using var client = CreateClient(signer, handler, maximumResponseBytes: 256);

        var result = await client.ValidateAsync("request-1", "CARD-VALUE", "machine-1", CancellationToken.None);

        Assert.Equal(LicenseApiResultKind.InvalidResponse, result.Kind);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task LicenseApiClientRejectsCardBeyondServerInputLimitBeforeSend()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var handler = new DelegateHandler((_, _, _) => Task.FromResult(Response(HttpStatusCode.OK, [])));
        using var client = CreateClient(signer, handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ActivateAsync("activation-1", new string('c', 257), "machine-1", CancellationToken.None));

        Assert.Equal(0, handler.SendCount);
    }

    private static LicenseApiClient CreateClient(
        ECDsa signer,
        HttpMessageHandler handler,
        TimeProvider? time = null,
        ILicenseRetryDelay? delay = null,
        ILicenseClientRandomSource? random = null,
        int maximumResponseBytes = 64 * 1024)
    {
        var options = SignedLicenseEnvelopeVerifierTests.CreateOptions(signer, maximumResponseBytes);
        var clock = time ?? new SignedLicenseEnvelopeVerifierTests.FixedTimeProvider(Now);
        var verifier = new SignedLicenseEnvelopeVerifier(options, clock);
        return new LicenseApiClient(options, handler, verifier, clock, delay, random);
    }

    private static async Task<string> ReadRequestIdAsync(HttpRequestMessage request)
    {
        using var document = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
        var root = document.RootElement;
        return root.TryGetProperty("request_id", out var requestId)
            ? requestId.GetString()!
            : root.GetProperty("activation_request_id").GetString()!;
    }

    private static HttpResponseMessage Response(HttpStatusCode status, byte[] content) => new(status)
    {
        Content = new ByteArrayContent(content),
    };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            return callback(request, SendCount, cancellationToken);
        }
    }

    private sealed class RecordingDelay : ILicenseRetryDelay
    {
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingDelay : ILicenseRetryDelay
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class TestRandomSource(IEnumerable<int> jitter, byte nonceSeed = 7) : ILicenseClientRandomSource
    {
        private readonly Queue<int> jitter = new(jitter);
        private byte nonceSeed = nonceSeed;

        public int NextInt32(int minimumInclusive, int maximumExclusive)
        {
            var value = jitter.Count == 0 ? 0 : jitter.Dequeue();
            Assert.InRange(value, minimumInclusive, maximumExclusive - 1);
            return value;
        }

        public byte[] GetBytes(int count)
        {
            var bytes = Enumerable.Repeat(nonceSeed++, count).ToArray();
            return bytes;
        }
    }

    private sealed class SteppingTimeProvider(DateTimeOffset current, TimeSpan step) : TimeProvider
    {
        private DateTimeOffset current = current.Subtract(step);

        public override DateTimeOffset GetUtcNow()
        {
            current = current.Add(step);
            return current;
        }
    }
}
