using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BaiduBatchTransfer.App.Updates;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace BaiduBatchTransfer.App.Tests.Updates;

public sealed class SignedFeedUpdateSourceTests : IDisposable
{
    private const string BaseUrl = "https://updates.example.test/releases/win-x64/stable/";
    private const string PackageId = "SuperPan.BaiduBatchTransfer";
    private const string Channel = "win-x64-stable";
    private readonly RSA signingKey = RSA.Create(3072);
    private readonly string directory = Directory.CreateTempSubdirectory("signed-feed-source-").FullName;

    [Theory]
    [InlineData("http://updates.example.test/stable/")]
    [InlineData("https://user:secret@updates.example.test/stable/")]
    [InlineData("https://updates.example.test/stable/?mutable=1")]
    [InlineData("https://updates.example.test/stable/#mutable")]
    public void RequiresFixedAbsoluteHttpsOrigin(string value)
    {
        Assert.Throws<ArgumentException>(() => SignedFeedUpdateSource.CreateForTesting(
            new Uri(value), Store(), new RecordingHandler(), signingKey));
    }

    [Fact]
    public void ProductionTransportDisablesRedirectsCookiesAndDecompression()
    {
        using var handler = SignedFeedUpdateSource.CreateProductionHttpHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
    }

    [Fact]
    public void VelopackClientUsesSignedSourceOnlyForProductionHttps()
    {
        var production = new VelopackUpdateSourceConfiguration(
            VelopackUpdateSourceKind.Https,
            BaseUrl,
            Channel,
            "1.0.0",
            Path.Combine(directory, "factory-state.json"));
        using var signed = Assert.IsType<SignedFeedUpdateSource>(
            VelopackUpdateClient.CreateUpdateSource(production));
        var local = VelopackUpdateClient.CreateUpdateSource(new VelopackUpdateSourceConfiguration(
            VelopackUpdateSourceKind.LocalDirectory,
            directory,
            "win-x64-ota-acceptance"));

        Assert.IsType<SimpleFileSource>(local);
    }

    [Fact]
    public void BuildInfoCanonicalizesTheProductionDirectoryAndProvidesDurableStatePath()
    {
        var build = ApplicationBuildInfo.CreateForTests(
            "1.0.0",
            "adapter-test",
            "https://updates.example.test/releases/win-x64/stable");

        Assert.Equal(BaseUrl, build.HttpsFeedUrl);
        Assert.True(Path.IsPathFullyQualified(build.SignedFeedStatePath));
        Assert.EndsWith(
            Path.Combine("BaiduBatchTransfer", "Updates", "signed-feed-state.json"),
            build.SignedFeedStatePath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildInfoRejectsMutableProductionChannelOrMixedFeedModes()
    {
        Assert.Throws<ArgumentException>(() => ApplicationBuildInfo.CreateForTests(
            "1.0.0", "adapter-test", BaseUrl, channel: "win-x64-beta"));
        Assert.Throws<ArgumentException>(() => ApplicationBuildInfo.CreateForTests(
            "1.0.0",
            "adapter-test",
            BaseUrl,
            acceptanceFeedPath: Path.Combine(directory, "acceptance")));
    }

    [Fact]
    public async Task FetchesOnlyFixedSiblingsInManifestSignatureFeedOrder()
    {
        var bundle = Bundle("2.0.0");
        var handler = Handler(bundle);
        var source = SignedFeedUpdateSource.CreateForTesting(
            new Uri(BaseUrl), Store(), handler, signingKey);

        var feed = await source.GetReleaseFeed(
            NullVelopackLogger.Instance, PackageId, Channel, null, null!);

        Assert.Single(feed.Assets);
        Assert.Equal(
            [
                BaseUrl + "upload-manifest.sha256",
                BaseUrl + "upload-manifest.sha256.sig",
                BaseUrl + "releases.win-x64-stable.json"
            ],
            handler.Requests);
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("signature")]
    [InlineData("release")]
    public async Task RejectsResponsesWhoseFinalUriIsNotTheFixedSibling(string response)
    {
        var bundle = Bundle("2.0.0");
        var handler = Handler(bundle, responseToMove: response);
        var source = SignedFeedUpdateSource.CreateForTesting(
            new Uri(BaseUrl), Store(), handler, signingKey);

        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetReleaseFeed(
            NullVelopackLogger.Instance, PackageId, Channel, null, null!));
    }

    [Fact]
    public async Task VerifiesManifestBeforeReadingTheSignedReleaseJson()
    {
        var bundle = Bundle("2.0.0");
        bundle.Signature[0] ^= 0xff;
        var handler = Handler(bundle);
        var source = SignedFeedUpdateSource.CreateForTesting(
            new Uri(BaseUrl), Store(), handler, signingKey);

        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetReleaseFeed(
            NullVelopackLogger.Instance, PackageId, Channel, null, null!));

        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, request => request.EndsWith(".json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectsReleaseJsonNotCoveredByTheSignedManifest()
    {
        var bundle = Bundle("2.0.0");
        var signed = SignManifest(
            (bundle.PackageFileName, bundle.PackageHash));
        bundle.Manifest = signed.Manifest;
        bundle.Signature = signed.Signature;
        var source = SignedFeedUpdateSource.CreateForTesting(
            new Uri(BaseUrl), Store(), Handler(bundle), signingKey);

        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetReleaseFeed(
            NullVelopackLogger.Instance, PackageId, Channel, null, null!));
    }

    [Theory]
    [InlineData("PackageId")]
    [InlineData("Version")]
    [InlineData("FileName")]
    [InlineData("Size")]
    [InlineData("TooLarge")]
    [InlineData("SHA256")]
    public async Task RejectsFeedAssetsOutsideTheSignedClosure(string property)
    {
        var bundle = Bundle("2.0.0", property);
        var source = SignedFeedUpdateSource.CreateForTesting(
            new Uri(BaseUrl), Store(), Handler(bundle), signingKey);

        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetReleaseFeed(
            NullVelopackLogger.Instance, PackageId, Channel, null, null!));
    }

    [Fact]
    public async Task RejectsSignedReplayBelowHighestPreviouslyAcceptedVersion()
    {
        var store = Store();
        var high = Bundle("3.0.0");
        var first = SignedFeedUpdateSource.CreateForTesting(
            new Uri(BaseUrl), store, Handler(high), signingKey);
        await first.GetReleaseFeed(NullVelopackLogger.Instance, PackageId, Channel, null, null!);
        var old = Bundle("2.0.0");
        var replay = SignedFeedUpdateSource.CreateForTesting(
            new Uri(BaseUrl), store, Handler(old), signingKey);

        await Assert.ThrowsAsync<InvalidDataException>(() => replay.GetReleaseFeed(
            NullVelopackLogger.Instance, PackageId, Channel, null, null!));
    }

    [Theory]
    [InlineData("manifest", SignedFeedManifest.MaxManifestBytes + 1)]
    [InlineData("signature", 385)]
    [InlineData("release", SignedFeedUpdateSource.MaxReleaseFeedBytes + 1)]
    public async Task RejectsOversizedBodiesBeforeBuffering(string response, int length)
    {
        var bundle = Bundle("2.0.0");
        var handler = Handler(bundle, oversizedResponse: response, oversizedLength: length);
        var source = SignedFeedUpdateSource.CreateForTesting(
            new Uri(BaseUrl), Store(), handler, signingKey);

        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetReleaseFeed(
            NullVelopackLogger.Instance, PackageId, Channel, null, null!));
        Assert.True(handler.OversizedStreamWasDisposed);
    }

    [Fact]
    public async Task MetadataTimeoutCoversABodyThatStallsAfterHeaders()
    {
        var bundle = Bundle("2.0.0");
        var source = SignedFeedUpdateSource.CreateForTesting(
            new Uri(BaseUrl),
            Store(),
            Handler(bundle, blockedResponse: "manifest"),
            signingKey,
            metadataOperationTimeout: TimeSpan.FromMilliseconds(150));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => source.GetReleaseFeed(
            NullVelopackLogger.Instance, PackageId, Channel, null, null!));

        Assert.IsType<TimeoutException>(exception.InnerException);
    }

    [Fact]
    public async Task PackageDownloadHasAnInactivityTimeoutWithoutMaskingCallerCancellation()
    {
        var bundle = Bundle("2.0.0");
        var timeoutSource = SignedFeedUpdateSource.CreateForTesting(
            new Uri(BaseUrl),
            Store(),
            Handler(bundle, blockedResponse: "package", includePackage: true),
            signingKey,
            packageInactivityTimeout: TimeSpan.FromMilliseconds(150));
        var timeoutFeed = await timeoutSource.GetReleaseFeed(
            NullVelopackLogger.Instance, PackageId, Channel, null, null!);

        var timeout = await Assert.ThrowsAsync<HttpRequestException>(() => timeoutSource.DownloadReleaseEntry(
            NullVelopackLogger.Instance,
            timeoutFeed.Assets.Single(),
            Path.Combine(directory, "timeout.nupkg"),
            _ => { },
            CancellationToken.None));
        Assert.IsType<TimeoutException>(timeout.InnerException);

        var cancellationSource = SignedFeedUpdateSource.CreateForTesting(
            new Uri(BaseUrl),
            Store(),
            Handler(bundle, blockedResponse: "package", includePackage: true),
            signingKey,
            packageInactivityTimeout: TimeSpan.FromSeconds(5));
        var cancellationFeed = await cancellationSource.GetReleaseFeed(
            NullVelopackLogger.Instance, PackageId, Channel, null, null!);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancellationSource.DownloadReleaseEntry(
            NullVelopackLogger.Instance,
            cancellationFeed.Assets.Single(),
            Path.Combine(directory, "cancelled.nupkg"),
            _ => { },
            cancellation.Token));
    }

    [Fact]
    public async Task DownloadAcceptsOnlyAnAssetFromTheLastVerifiedFeed()
    {
        var bundle = Bundle("2.0.0");
        var handler = Handler(bundle, includePackage: true);
        var source = SignedFeedUpdateSource.CreateForTesting(
            new Uri(BaseUrl), Store(), handler, signingKey);
        var feed = await source.GetReleaseFeed(
            NullVelopackLogger.Instance, PackageId, Channel, null, null!);
        var destination = Path.Combine(directory, "download.nupkg");

        await source.DownloadReleaseEntry(
            NullVelopackLogger.Instance,
            feed.Assets.Single(),
            destination,
            _ => { },
            CancellationToken.None);

        Assert.Equal(bundle.PackageBytes, File.ReadAllBytes(destination));
        Assert.Equal(BaseUrl + bundle.PackageFileName, handler.Requests.Last());
        var foreign = feed.Assets.Single();
        foreign = new VelopackAsset
        {
            PackageId = foreign.PackageId,
            Version = foreign.Version,
            Type = foreign.Type,
            FileName = "SuperPan.BaiduBatchTransfer-9.9.9-full.nupkg",
            SHA256 = foreign.SHA256,
            Size = foreign.Size
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => source.DownloadReleaseEntry(
            NullVelopackLogger.Instance, foreign, destination, _ => { }, CancellationToken.None));
    }

    [Fact]
    public async Task ClientClassifiesSignedFeedRejectionAsVerificationFailure()
    {
        using var client = new VelopackUpdateClient(new RejectingManager());

        await Assert.ThrowsAsync<ApplicationUpdateVerificationException>(() =>
            client.CheckForUpdatesAsync(CancellationToken.None));
    }

    private SignedFeedStateStore Store() => new(
        Path.Combine(directory, "state.json"), BaseUrl, "1.0.0");

    private BundleData Bundle(string version, string? mutateProperty = null)
    {
        var packageFileName = $"{PackageId}-{version}-full.nupkg";
        var packageBytes = Encoding.UTF8.GetBytes("package:" + version);
        var packageHash = LowerHash(packageBytes);
        var properties = new Dictionary<string, object?>
        {
            ["PackageId"] = mutateProperty == "PackageId" ? "Foreign.Package" : PackageId,
            ["Version"] = mutateProperty == "Version" ? "9.9.9" : version,
            ["Type"] = "Full",
            ["FileName"] = mutateProperty == "FileName"
                ? $"{PackageId}-9.9.9-full.nupkg"
                : packageFileName,
            ["SHA1"] = new string('A', 40),
            ["SHA256"] = mutateProperty == "SHA256" ? new string('B', 64) : packageHash.ToUpperInvariant(),
            ["Size"] = mutateProperty switch
            {
                "Size" => 0,
                "TooLarge" => SignedFeedUpdateSource.MaxPackageBytes + 1,
                _ => packageBytes.Length
            },
            ["NotesMarkdown"] = "notes"
        };
        var release = JsonSerializer.SerializeToUtf8Bytes(new { Assets = new[] { properties } });
        var releaseHash = LowerHash(release);
        var signed = SignManifest(
            (packageFileName, packageHash),
            (SignedFeedManifest.ReleaseFeedFileName, releaseHash));
        return new BundleData(
            signed.Manifest,
            signed.Signature,
            release,
            packageFileName,
            packageHash,
            packageBytes);
    }

    private SignedBytes SignManifest(params (string FileName, string Hash)[] entries)
    {
        var text = string.Concat(entries
            .OrderBy(entry => Encoding.UTF8.GetBytes(entry.FileName), ByteArrayComparer.Instance)
            .Select(entry => $"{entry.Hash}  {entry.FileName}\n"));
        var manifest = Encoding.UTF8.GetBytes(text);
        var signature = signingKey.SignData(
            manifest, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return new SignedBytes(manifest, signature);
    }

    private static string LowerHash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static RecordingHandler Handler(
        BundleData bundle,
        string? responseToMove = null,
        string? oversizedResponse = null,
        int oversizedLength = 0,
        bool includePackage = false,
        string? blockedResponse = null) => new(request =>
        {
            var name = request.RequestUri!.Segments.Last();
            var responseKey = name switch
            {
                "upload-manifest.sha256" => "manifest",
                "upload-manifest.sha256.sig" => "signature",
                "releases.win-x64-stable.json" => "release",
                _ when includePackage && name == bundle.PackageFileName => "package",
                _ => "unknown"
            };
            if (responseKey == "unknown")
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (responseKey == blockedResponse)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new BlockingStream())
                };
            }
            if (responseKey == oversizedResponse)
            {
                var stream = new TrackingOversizedStream(oversizedLength);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(stream)
                };
            }

            var bytes = responseKey switch
            {
                "manifest" => bundle.Manifest,
                "signature" => bundle.Signature,
                "release" => bundle.ReleaseFeed,
                _ => bundle.PackageBytes
            };
            var result = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
            if (responseKey == responseToMove)
                result.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example.test/moved");
            return result;
        });

    public void Dispose()
    {
        signingKey.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> responder;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
        {
            this.responder = responder ?? (_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        public List<string> Requests { get; } = [];

        public bool OversizedStreamWasDisposed => TrackingOversizedStream.LastDisposed;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            var response = responder(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed class TrackingOversizedStream : Stream
    {
        private long remaining;

        public TrackingOversizedStream(long length)
        {
            remaining = length;
            LastDisposed = false;
        }

        public static bool LastDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (remaining == 0) return 0;
            var read = (int)Math.Min(count, remaining);
            Array.Fill<byte>(buffer, (byte)'x', offset, read);
            remaining -= read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            LastDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed record SignedBytes(byte[] Manifest, byte[] Signature);

    private sealed class BundleData
    {
        public BundleData(
            byte[] manifest,
            byte[] signature,
            byte[] releaseFeed,
            string packageFileName,
            string packageHash,
            byte[] packageBytes)
        {
            Manifest = manifest;
            Signature = signature;
            ReleaseFeed = releaseFeed;
            PackageFileName = packageFileName;
            PackageHash = packageHash;
            PackageBytes = packageBytes;
        }

        public byte[] Manifest { get; set; }
        public byte[] Signature { get; set; }
        public byte[] ReleaseFeed { get; }
        public string PackageFileName { get; }
        public string PackageHash { get; }
        public byte[] PackageBytes { get; }
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new();
        public int Compare(byte[]? x, byte[]? y) =>
            (x ?? []).AsSpan().SequenceCompareTo(y ?? []);
    }

    private sealed class RejectingManager : IVelopackUpdateManagerAdapter
    {
        public bool IsInstalled => true;
        public string? CurrentVersion => "1.0.0";
        public VelopackClientAsset? UpdatePendingRestart => null;
        public Task<VelopackClientAsset?> CheckForUpdatesAsync() =>
            Task.FromException<VelopackClientAsset?>(new InvalidDataException("signed feed rejected"));
        public Task DownloadUpdatesAsync(
            VelopackClientAsset update,
            Action<int> progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public void WaitExitThenApplyUpdates(
            VelopackClientAsset update,
            bool silent,
            bool restart,
            string[] restartArguments) => throw new NotSupportedException();
    }
}
