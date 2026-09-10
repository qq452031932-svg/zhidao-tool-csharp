using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuperPan.Licensing.Client;

public enum LicenseApiResultKind
{
    Authorized,
    Rejected,
    TemporaryFailure,
    InvalidResponse,
}

public sealed record LicenseApiResult(LicenseApiResultKind Kind, string? Code, LicenseClaims? Claims)
{
    public override string ToString() => $"LicenseApiResult {{ Kind = {Kind}, Code = {Code ?? "none"} }}";
}

public interface ILicenseRetryDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public interface ILicenseClientRandomSource
{
    int NextInt32(int minimumInclusive, int maximumExclusive);
    byte[] GetBytes(int count);
}

public sealed class LicenseApiClient : IDisposable
{
    private const int MaximumAttempts = 3;
    private const int MaximumCardUtf8Bytes = 256;
    private static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromSeconds(10);
    private readonly LicenseClientOptions options;
    private readonly HttpMessageInvoker http;
    private readonly SignedLicenseEnvelopeVerifier verifier;
    private readonly TimeProvider timeProvider;
    private readonly ILicenseRetryDelay delay;
    private readonly ILicenseClientRandomSource random;
    private readonly bool ownsVerifier;
    private bool disposed;

    public LicenseApiClient(
        LicenseClientOptions options,
        TimeProvider? timeProvider = null,
        ILicenseRetryDelay? delay = null,
        ILicenseClientRandomSource? random = null)
        : this(
            options,
            CreatePinnedHandler(options),
            new SignedLicenseEnvelopeVerifier(options, timeProvider),
            timeProvider,
            delay,
            random,
            ownsVerifier: true)
    {
    }

    public LicenseApiClient(
        LicenseClientOptions options,
        HttpMessageHandler handler,
        SignedLicenseEnvelopeVerifier verifier,
        TimeProvider? timeProvider = null,
        ILicenseRetryDelay? delay = null,
        ILicenseClientRandomSource? random = null)
        : this(options, handler, verifier, timeProvider, delay, random, ownsVerifier: false)
    {
    }

    private LicenseApiClient(
        LicenseClientOptions options,
        HttpMessageHandler handler,
        SignedLicenseEnvelopeVerifier verifier,
        TimeProvider? timeProvider,
        ILicenseRetryDelay? delay,
        ILicenseClientRandomSource? random,
        bool ownsVerifier)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(handler);
        this.verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.delay = delay ?? SystemLicenseRetryDelay.Instance;
        this.random = random ?? CryptographicLicenseRandomSource.Instance;
        this.ownsVerifier = ownsVerifier;
        http = new HttpMessageInvoker(handler, disposeHandler: true);
    }

    public Task<LicenseApiResult> ActivateAsync(
        string activationRequestId,
        string card,
        string machineHash,
        CancellationToken cancellationToken) =>
        SendAsync("activate", activationRequestId, card, machineHash, isActivation: true, cancellationToken);

    public Task<LicenseApiResult> ValidateAsync(
        string requestId,
        string card,
        string machineHash,
        CancellationToken cancellationToken) =>
        SendAsync("validate", requestId, card, machineHash, isActivation: false, cancellationToken);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        http.Dispose();
        if (ownsVerifier)
        {
            verifier.Dispose();
        }
    }

    private async Task<LicenseApiResult> SendAsync(
        string route,
        string requestId,
        string card,
        string machineHash,
        bool isActivation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidateRequestInput(requestId, card, machineHash);

        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = CreateRequest(route, requestId, card, machineHash, isActivation);
                using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (attempt == MaximumAttempts)
                    {
                        return Temporary(LicenseCodes.RateLimited);
                    }
                    await DelayBeforeRetryAsync(attempt, response.Headers.RetryAfter, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if ((int)response.StatusCode >= 500 && (int)response.StatusCode <= 599)
                {
                    if (attempt == MaximumAttempts)
                    {
                        return Temporary(LicenseCodes.ServiceUnavailable);
                    }
                    await DelayBeforeRetryAsync(attempt, null, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (!IsSignedDecisionStatus(response.StatusCode))
                {
                    return InvalidResponse();
                }

                var body = await ReadLimitedBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
                LicenseClaims claims;
                try
                {
                    claims = verifier.Verify(body, requestId, options.SoftwareId, machineHash);
                }
                catch (LicenseEnvelopeVerificationException)
                {
                    return InvalidResponse();
                }
                return MapSignedDecision(response.StatusCode, claims);
            }
            catch (ResponseTooLargeException)
            {
                return InvalidResponse();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
            {
                if (attempt == MaximumAttempts)
                {
                    return Temporary(LicenseCodes.ServiceUnavailable);
                }
                await DelayBeforeRetryAsync(attempt, null, cancellationToken).ConfigureAwait(false);
            }
        }

        return Temporary(LicenseCodes.ServiceUnavailable);
    }

    private HttpRequestMessage CreateRequest(
        string route,
        string requestId,
        string card,
        string machineHash,
        bool isActivation)
    {
        var nonceBytes = random.GetBytes(24);
        if (nonceBytes.Length != 24)
        {
            throw new InvalidOperationException("The random source returned an invalid nonce length.");
        }
        var nonce = Convert.ToBase64String(nonceBytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var timestamp = timeProvider.GetUtcNow();
        object payload = isActivation
            ? new ActivationRequest(LicenseProtocol.Version, requestId, card, options.SoftwareId, machineHash, timestamp, nonce)
            : new ValidationRequest(LicenseProtocol.Version, requestId, card, options.SoftwareId, machineHash, timestamp, nonce);
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType());
        var endpoint = new UriBuilder(options.ServiceEndpoint)
        {
            Path = $"/license-api/v1/{route}",
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri;
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return request;
    }

    private async Task<byte[]> ReadLimitedBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > options.MaximumResponseBytes)
        {
            throw new ResponseTooLargeException();
        }
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[Math.Min(8192, options.MaximumResponseBytes + 1)];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return destination.ToArray();
            }
            if (destination.Length + read > options.MaximumResponseBytes)
            {
                throw new ResponseTooLargeException();
            }
            destination.Write(buffer, 0, read);
        }
    }

    private async Task DelayBeforeRetryAsync(
        int failedAttempt,
        RetryConditionHeaderValue? retryAfter,
        CancellationToken cancellationToken)
    {
        var jitterMilliseconds = random.NextInt32(0, 251);
        if (jitterMilliseconds is < 0 or > 250)
        {
            throw new InvalidOperationException("The random source returned jitter outside the requested range.");
        }
        var backoff = TimeSpan.FromMilliseconds((250 * (1 << (failedAttempt - 1))) + jitterMilliseconds);
        var requestedDelay = ParseRetryAfter(retryAfter);
        if (requestedDelay > backoff)
        {
            backoff = requestedDelay;
        }
        await delay.DelayAsync(backoff, cancellationToken).ConfigureAwait(false);
    }

    private TimeSpan ParseRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter is null)
        {
            return TimeSpan.Zero;
        }
        var requested = retryAfter.Delta ?? (retryAfter.Date - timeProvider.GetUtcNow());
        if (requested is null || requested <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }
        return requested > MaximumRetryAfter ? MaximumRetryAfter : requested.Value;
    }

    private static LicenseApiResult MapSignedDecision(HttpStatusCode status, LicenseClaims claims)
    {
        var valid = status switch
        {
            HttpStatusCode.OK => claims.Decision == LicenseDecisions.Allow && claims.Code == LicenseCodes.Ok,
            HttpStatusCode.BadRequest => claims.Decision == LicenseDecisions.Deny &&
                claims.Code is LicenseCodes.InvalidRequest or LicenseCodes.UnsupportedProtocol,
            HttpStatusCode.Forbidden => claims.Decision == LicenseDecisions.Deny &&
                claims.Code is LicenseCodes.LicenseNotFound or LicenseCodes.LicenseExpired or
                    LicenseCodes.AppNotAllowed or LicenseCodes.AppDisabled,
            HttpStatusCode.Conflict => claims.Decision == LicenseDecisions.Deny && claims.Code == LicenseCodes.MachineMismatch,
            _ => false,
        };
        if (!valid)
        {
            return InvalidResponse();
        }
        return new LicenseApiResult(
            status == HttpStatusCode.OK ? LicenseApiResultKind.Authorized : LicenseApiResultKind.Rejected,
            claims.Code,
            claims);
    }

    private static bool IsSignedDecisionStatus(HttpStatusCode status) =>
        status is HttpStatusCode.OK or HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.Conflict;

    private static LicenseApiResult Temporary(string code) =>
        new(LicenseApiResultKind.TemporaryFailure, code, null);

    private static LicenseApiResult InvalidResponse() =>
        new(LicenseApiResultKind.InvalidResponse, null, null);

    private static void ValidateRequestInput(string requestId, string card, string machineHash)
    {
        ValidateIdentifier(requestId, nameof(requestId));
        ValidateIdentifier(machineHash, nameof(machineHash));
        ArgumentException.ThrowIfNullOrEmpty(card);
        if (card != card.Trim() || Encoding.UTF8.GetByteCount(card) > MaximumCardUtf8Bytes)
        {
            throw new ArgumentException("The card value is invalid.", nameof(card));
        }
    }

    private static void ValidateIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, parameterName);
        if (value != value.Trim() || Encoding.UTF8.GetByteCount(value) > LicenseProtocol.MaxIdentifierLength)
        {
            throw new ArgumentException("The identifier is invalid.", parameterName);
        }
    }

    private static HttpMessageHandler CreatePinnedHandler(LicenseClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, certificate, _, policyErrors) =>
                {
                    if (certificate is null)
                    {
                        return false;
                    }
                    if (certificate is X509Certificate2 certificate2)
                    {
                        return options.ValidateServerCertificate(certificate2, policyErrors);
                    }
                    using var converted = new X509Certificate2(certificate);
                    return options.ValidateServerCertificate(converted, policyErrors);
                },
            },
        };
    }

    private sealed record ActivationRequest(
        [property: JsonPropertyName("protocol_version")] int ProtocolVersion,
        [property: JsonPropertyName("activation_request_id")] string ActivationRequestId,
        [property: JsonPropertyName("card")] string Card,
        [property: JsonPropertyName("software_id")] string SoftwareId,
        [property: JsonPropertyName("machine_hash")] string MachineHash,
        [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
        [property: JsonPropertyName("nonce")] string Nonce);

    private sealed record ValidationRequest(
        [property: JsonPropertyName("protocol_version")] int ProtocolVersion,
        [property: JsonPropertyName("request_id")] string RequestId,
        [property: JsonPropertyName("card")] string Card,
        [property: JsonPropertyName("software_id")] string SoftwareId,
        [property: JsonPropertyName("machine_hash")] string MachineHash,
        [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
        [property: JsonPropertyName("nonce")] string Nonce);

    private sealed class ResponseTooLargeException : Exception;

    private sealed class SystemLicenseRetryDelay : ILicenseRetryDelay
    {
        public static readonly SystemLicenseRetryDelay Instance = new();
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
    }

    private sealed class CryptographicLicenseRandomSource : ILicenseClientRandomSource
    {
        public static readonly CryptographicLicenseRandomSource Instance = new();
        public int NextInt32(int minimumInclusive, int maximumExclusive) =>
            RandomNumberGenerator.GetInt32(minimumInclusive, maximumExclusive);
        public byte[] GetBytes(int count) => RandomNumberGenerator.GetBytes(count);
    }
}
