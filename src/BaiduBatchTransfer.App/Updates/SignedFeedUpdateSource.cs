using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace BaiduBatchTransfer.App.Updates;

internal sealed class SignedFeedUpdateSource : IUpdateSource, IDisposable
{
    internal const int MaxReleaseFeedBytes = 1024 * 1024;
    internal const long MaxPackageBytes = 2L * 1024 * 1024 * 1024;

    private const int SignatureBytes = 3072 / 8;
    private const string ManifestFileName = "upload-manifest.sha256";
    private const string ManifestSignatureFileName = "upload-manifest.sha256.sig";
    private const string ExpectedPackageId = "SuperPan.BaiduBatchTransfer";
    private const string ExpectedChannel = "win-x64-stable";
    private static readonly TimeSpan DefaultMetadataOperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultPackageInactivityTimeout = TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly object sync = new();
    private readonly Uri baseUri;
    private readonly Uri manifestUri;
    private readonly Uri signatureUri;
    private readonly Uri releaseFeedUri;
    private readonly SignedFeedStateStore stateStore;
    private readonly HttpClient httpClient;
    private readonly Func<byte[], byte[], SignedFeedManifest> verifyManifest;
    private readonly TimeSpan metadataOperationTimeout;
    private readonly TimeSpan packageInactivityTimeout;
    private SignedFeedVerificationSnapshot? latestSnapshot;
    private int disposed;

    internal SignedFeedUpdateSource(Uri baseUri, SignedFeedStateStore stateStore)
        : this(
            NormalizeBaseUri(baseUri),
            stateStore,
            new HttpClient(CreateProductionHttpHandler(), disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan
            },
            static (manifest, signature) => SignedFeedManifest.Verify(manifest, signature),
            DefaultMetadataOperationTimeout,
            DefaultPackageInactivityTimeout)
    {
    }

    private SignedFeedUpdateSource(
        Uri normalizedBaseUri,
        SignedFeedStateStore stateStore,
        HttpClient httpClient,
        Func<byte[], byte[], SignedFeedManifest> verifyManifest,
        TimeSpan metadataOperationTimeout,
        TimeSpan packageInactivityTimeout)
    {
        this.baseUri = normalizedBaseUri;
        this.stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        if (!string.Equals(this.stateStore.FeedIdentity, this.baseUri.AbsoluteUri, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The replay state identity must match the signed feed origin.",
                nameof(stateStore));
        }
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.verifyManifest = verifyManifest ?? throw new ArgumentNullException(nameof(verifyManifest));
        this.metadataOperationTimeout = ValidateTimeout(
            metadataOperationTimeout,
            nameof(metadataOperationTimeout));
        this.packageInactivityTimeout = ValidateTimeout(
            packageInactivityTimeout,
            nameof(packageInactivityTimeout));
        manifestUri = FixedSibling(ManifestFileName);
        signatureUri = FixedSibling(ManifestSignatureFileName);
        releaseFeedUri = FixedSibling(SignedFeedManifest.ReleaseFeedFileName);
    }

    // Hermetic test seam only. Production construction always uses OtaPublisherTrust.
    internal static SignedFeedUpdateSource CreateForTesting(
        Uri baseUri,
        SignedFeedStateStore stateStore,
        HttpMessageHandler handler,
        RSA publicKey,
        TimeSpan? metadataOperationTimeout = null,
        TimeSpan? packageInactivityTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(publicKey);
        return new SignedFeedUpdateSource(
            NormalizeBaseUri(baseUri),
            stateStore,
            new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan },
            (manifest, signature) => SignedFeedManifest.VerifyWithPublicKeyForTesting(
                manifest,
                signature,
                publicKey),
            metadataOperationTimeout ?? DefaultMetadataOperationTimeout,
            packageInactivityTimeout ?? DefaultPackageInactivityTimeout);
    }

    internal static HttpClientHandler CreateProductionHttpHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None
    };

    internal SignedFeedVerificationSnapshot? LatestSnapshot
    {
        get
        {
            lock (sync) return latestSnapshot;
        }
    }

    public async Task<VelopackAssetFeed> GetReleaseFeed(
        IVelopackLogger logger,
        string? appId,
        string channel,
        Guid? stagingId = null,
        VelopackAsset? latestLocalRelease = null)
    {
        ThrowIfDisposed();
        if (!string.Equals(appId, ExpectedPackageId, StringComparison.Ordinal) ||
            !string.Equals(channel, ExpectedChannel, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The signed feed package identity or channel is invalid.");
        }

        using var metadataTimeout = new CancellationTokenSource(metadataOperationTimeout);
        try
        {
            var manifestBytes = await GetBoundedBytesAsync(
                manifestUri,
                SignedFeedManifest.MaxManifestBytes,
                "manifest",
                metadataTimeout.Token).ConfigureAwait(false);
            var signatureBytes = await GetBoundedBytesAsync(
                signatureUri,
                SignatureBytes,
                "manifest signature",
                metadataTimeout.Token).ConfigureAwait(false);
            var manifest = verifyManifest(manifestBytes, signatureBytes);

            var releaseBytes = await GetBoundedBytesAsync(
                releaseFeedUri,
                MaxReleaseFeedBytes,
                "release feed",
                metadataTimeout.Token).ConfigureAwait(false);
            if (!HashMatches(releaseBytes, manifest.ReleaseFeedEntry.Sha256))
                throw new InvalidDataException("The release feed is outside the signed manifest closure.");

            VelopackAssetFeed feed;
            try
            {
                var text = StrictUtf8.GetString(releaseBytes);
                feed = VelopackAssetFeed.FromJson(text);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and
                                              not StackOverflowException and
                                              not OperationCanceledException)
            {
                throw new InvalidDataException("The signed release feed is malformed.", exception);
            }

            var verifiedAssets = ValidateClosure(feed, manifest);
            var highestVersion = verifiedAssets.Values
                .Select(asset => SignedFeedProductVersion.Parse(asset.Version, "asset version"))
                .Max();
            var manifestSha256 = LowerHash(manifestBytes);
            stateStore.Accept(highestVersion.ToString(), manifestSha256);
            var snapshot = new SignedFeedVerificationSnapshot(
                baseUri.AbsoluteUri,
                highestVersion.ToString(),
                manifestSha256,
                new ReadOnlyDictionary<string, SignedFeedVerifiedAsset>(verifiedAssets));
            lock (sync) latestSnapshot = snapshot;
            return feed;
        }
        catch (OperationCanceledException exception) when (metadataTimeout.IsCancellationRequested)
        {
            throw MetadataTimeoutException(exception);
        }
    }

    public async Task DownloadReleaseEntry(
        IVelopackLogger logger,
        VelopackAsset releaseEntry,
        string localFile,
        Action<int> progress,
        CancellationToken cancelToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(releaseEntry);
        ArgumentException.ThrowIfNullOrWhiteSpace(localFile);
        ArgumentNullException.ThrowIfNull(progress);

        SignedFeedVerifiedAsset expected;
        lock (sync)
        {
            if (latestSnapshot is null ||
                string.IsNullOrEmpty(releaseEntry.FileName) ||
                !latestSnapshot.Assets.TryGetValue(releaseEntry.FileName, out expected!) ||
                !AssetMatches(releaseEntry, expected))
            {
                throw new InvalidDataException("The requested update asset is not in the verified signed feed.");
            }
        }

        using var inactivity = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
        try
        {
            inactivity.CancelAfter(packageInactivityTimeout);
            var assetUri = FixedSibling(expected.FileName);
            using var request = new HttpRequestMessage(HttpMethod.Get, assetUri);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                inactivity.Token).ConfigureAwait(false);
            ValidateResponse(response, assetUri, "update asset");
            if (response.Content.Headers.ContentLength is { } declaredLength && declaredLength != expected.Size)
                throw new InvalidDataException("The update asset length does not match the signed feed.");

            var destination = Path.GetFullPath(localFile);
            var parent = Path.GetDirectoryName(destination)
                ?? throw new InvalidDataException("The update destination is invalid.");
            Directory.CreateDirectory(parent);
            var temporary = Path.Combine(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
            try
            {
                inactivity.CancelAfter(packageInactivityTimeout);
                await using var input = await response.Content.ReadAsStreamAsync(inactivity.Token).ConfigureAwait(false);
                inactivity.CancelAfter(Timeout.InfiniteTimeSpan);
                await using (var output = new FileStream(
                                 temporary,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 81920,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    while (true)
                    {
                        inactivity.CancelAfter(packageInactivityTimeout);
                        var read = await input.ReadAsync(buffer, inactivity.Token).ConfigureAwait(false);
                        inactivity.CancelAfter(Timeout.InfiniteTimeSpan);
                        if (read == 0) break;
                        total += read;
                        if (total > expected.Size)
                            throw new InvalidDataException("The update asset exceeds its signed size.");
                        await output.WriteAsync(buffer.AsMemory(0, read), cancelToken).ConfigureAwait(false);
                        progress((int)Math.Min(99, total * 100 / expected.Size));
                    }

                    if (total != expected.Size)
                        throw new InvalidDataException("The update asset is shorter than its signed size.");
                    await output.FlushAsync(cancelToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }

                File.Move(temporary, destination, overwrite: true);
                progress(100);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch
                {
                    // An incomplete temporary never replaces the destination package.
                }
            }
        }
        catch (OperationCanceledException exception) when (!cancelToken.IsCancellationRequested &&
                                                           inactivity.IsCancellationRequested)
        {
            throw PackageTimeoutException(exception);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
            httpClient.Dispose();
    }

    private Dictionary<string, SignedFeedVerifiedAsset> ValidateClosure(
        VelopackAssetFeed feed,
        SignedFeedManifest manifest)
    {
        if (feed.Assets is null || feed.Assets.Length == 0 || feed.Assets.Length > SignedFeedManifest.MaxEntries)
            throw new InvalidDataException("The signed release feed has an invalid asset count.");

        var verified = new Dictionary<string, SignedFeedVerifiedAsset>(StringComparer.Ordinal);
        var caseInsensitiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in feed.Assets)
        {
            var packageId = asset.PackageId ?? string.Empty;
            var fileName = asset.FileName ?? string.Empty;
            var version = asset.Version?.ToString() ?? string.Empty;
            var type = asset.Type.ToString();
            var sha256 = asset.SHA256 ?? string.Empty;
            _ = SignedFeedProductVersion.Parse(version, "asset version");
            if (!string.Equals(packageId, ExpectedPackageId, StringComparison.Ordinal) ||
                asset.Type is not (VelopackAssetType.Full or VelopackAssetType.Delta) ||
                asset.Size is <= 0 or > MaxPackageBytes ||
                !IsSha256(sha256) ||
                !manifest.TryGetEntry(fileName, out var manifestEntry) ||
                !string.Equals(manifestEntry.Sha256, sha256, StringComparison.OrdinalIgnoreCase) ||
                !MatchesCanonicalPackageFileName(fileName, version, asset.Type) ||
                !caseInsensitiveNames.Add(fileName) ||
                !verified.TryAdd(fileName, new SignedFeedVerifiedAsset(
                    packageId,
                    version,
                    type,
                    fileName,
                    asset.Size,
                    sha256.ToLowerInvariant())))
            {
                throw new InvalidDataException("A release asset is outside the signed manifest closure.");
            }
        }

        var manifestPackages = manifest.Entries
            .Where(entry => entry.FileName.EndsWith(".nupkg", StringComparison.Ordinal))
            .Select(entry => entry.FileName)
            .ToArray();
        if (manifestPackages.Length != verified.Count ||
            manifestPackages.Any(fileName => !verified.ContainsKey(fileName)))
        {
            throw new InvalidDataException("The signed manifest and release feed package sets differ.");
        }

        return verified;
    }

    private async Task<byte[]> GetBoundedBytesAsync(
        Uri uri,
        int maxBytes,
        string resourceName,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        ValidateResponse(response, uri, resourceName);
        if (response.Content.Headers.ContentLength is { } declaredLength &&
            (declaredLength < 0 || declaredLength > maxBytes))
        {
            throw new InvalidDataException($"The signed {resourceName} exceeds its size limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(Math.Min(maxBytes, 81920));
        var chunk = new byte[81920];
        while (true)
        {
            var remaining = maxBytes - checked((int)buffer.Length);
            var requested = Math.Min(chunk.Length, remaining + 1);
            var read = await stream.ReadAsync(
                chunk.AsMemory(0, requested),
                cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maxBytes)
                throw new InvalidDataException($"The signed {resourceName} exceeds its size limit.");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static void ValidateResponse(
        HttpResponseMessage response,
        Uri expectedUri,
        string resourceName)
    {
        if (response.StatusCode != HttpStatusCode.OK ||
            response.RequestMessage?.RequestUri is not { } finalUri ||
            Uri.Compare(finalUri, expectedUri, UriComponents.AbsoluteUri, UriFormat.UriEscaped, StringComparison.Ordinal) != 0 ||
            response.Content.Headers.ContentEncoding.Count != 0)
        {
            throw new InvalidDataException($"The signed {resourceName} response is invalid.");
        }
    }

    private static bool AssetMatches(VelopackAsset asset, SignedFeedVerifiedAsset expected) =>
        string.Equals(asset.PackageId, expected.PackageId, StringComparison.Ordinal) &&
        string.Equals(asset.Version?.ToString(), expected.Version, StringComparison.Ordinal) &&
        string.Equals(asset.Type.ToString(), expected.Type, StringComparison.Ordinal) &&
        string.Equals(asset.FileName, expected.FileName, StringComparison.Ordinal) &&
        asset.Size == expected.Size &&
        string.Equals(asset.SHA256, expected.Sha256, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesCanonicalPackageFileName(
        string fileName,
        string version,
        VelopackAssetType type) => string.Equals(
            fileName,
            $"{ExpectedPackageId}-{version}-{(type == VelopackAssetType.Full ? "full" : "delta")}.nupkg",
            StringComparison.Ordinal);

    private static bool HashMatches(byte[] bytes, string expectedLowerHex)
    {
        var expected = Convert.FromHexString(expectedLowerHex);
        var actual = SHA256.HashData(bytes);
        try { return CryptographicOperations.FixedTimeEquals(actual, expected); }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));

    private static string LowerHash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static TimeSpan ValidateTimeout(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(parameterName);
        return value;
    }

    private static HttpRequestException MetadataTimeoutException(Exception exception) =>
        new(
            "The signed update metadata request timed out.",
            new TimeoutException("The signed update metadata operation timed out.", exception));

    private static HttpRequestException PackageTimeoutException(Exception exception) =>
        new(
            "The update package transfer became inactive.",
            new TimeoutException("The update package response timed out due to inactivity.", exception));

    private Uri FixedSibling(string fileName)
    {
        var uri = new Uri(baseUri, fileName);
        if (!string.Equals(uri.GetLeftPart(UriPartial.Authority), baseUri.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal) ||
            !uri.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The signed feed sibling URI escaped its fixed origin.");
        }

        return uri;
    }

    private static Uri NormalizeBaseUri(Uri value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.IsAbsoluteUri ||
            !string.Equals(value.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            string.IsNullOrEmpty(value.Host) ||
            !string.IsNullOrEmpty(value.UserInfo) ||
            !string.IsNullOrEmpty(value.Query) ||
            !string.IsNullOrEmpty(value.Fragment))
        {
            throw new ArgumentException("The signed feed origin must be an absolute HTTPS URI.", nameof(value));
        }

        var builder = new UriBuilder(value)
        {
            Path = value.AbsolutePath.TrimEnd('/') + "/",
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
}
