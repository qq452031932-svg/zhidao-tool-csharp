using BaiduBatchTransfer.App.Features.ManagedBrowser;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedBrowserHelperIntegrityVerifierTests
{
    private const string HelperIntegrationDirectoryVariable =
        "BAIDU_BATCH_TRANSFER_TEST_HELPER_DIRECTORY";

    [Fact]
    public async Task ValidExactBundleIsPinnedForLaunch()
    {
        using var fixture = HelperFixture.Create();
        var verifier = new ManagedBrowserHelperIntegrityVerifier(fixture.ManifestBytes);

        var verified = await verifier.VerifyAsync(fixture.Root, CancellationToken.None);

        Assert.EndsWith("python.exe", verified.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("main.py", verified.MainScriptPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(fixture.BuildIdentity, verified.BuildIdentity);
        Assert.ThrowsAny<IOException>(() =>
            File.WriteAllText(Path.Combine(fixture.Root, "main.py"), "replacement"));
        verified.Dispose();
        File.WriteAllText(Path.Combine(fixture.Root, "main.py"), "replacement after disposal");
    }

    [Fact]
    public async Task MissingManifestIsRejectedBeforeLaunch()
    {
        using var fixture = HelperFixture.Create();
        File.Delete(Path.Combine(fixture.Root, "helper-manifest.json"));
        var launched = false;
        var verifier = new ManagedBrowserHelperIntegrityVerifier(fixture.ManifestBytes);

        var error = await Assert.ThrowsAsync<ManagedBrowserHelperIntegrityException>(() =>
            verifier.VerifyThenLaunchAsync(
                fixture.Root,
                (_, _) => { launched = true; return Task.FromResult(0); },
                CancellationToken.None));

        Assert.Equal(ManagedBrowserHelperIntegrityError.MissingManifest, error.Error);
        Assert.False(launched);
    }

    [Fact]
    public async Task ExternalManifestMustExactlyMatchEmbeddedBytes()
    {
        using var fixture = HelperFixture.Create();
        File.AppendAllText(Path.Combine(fixture.Root, "helper-manifest.json"), "\n");
        var verifier = new ManagedBrowserHelperIntegrityVerifier(fixture.ManifestBytes);

        var error = await Assert.ThrowsAsync<ManagedBrowserHelperIntegrityException>(() =>
            verifier.VerifyThenLaunchAsync(
                fixture.Root, (_, _) => Task.FromResult(0), CancellationToken.None));

        Assert.Equal(ManagedBrowserHelperIntegrityError.ManifestMismatch, error.Error);
    }

    [Fact]
    public async Task AlteredPayloadByteIsRejected()
    {
        using var fixture = HelperFixture.Create();
        File.WriteAllText(Path.Combine(fixture.Root, "protocol.py"), "altered");
        var verifier = new ManagedBrowserHelperIntegrityVerifier(fixture.ManifestBytes);

        var error = await Assert.ThrowsAsync<ManagedBrowserHelperIntegrityException>(() =>
            verifier.VerifyAsync(fixture.Root, CancellationToken.None));

        Assert.Equal(ManagedBrowserHelperIntegrityError.PayloadMismatch, error.Error);
    }

    [Fact]
    public async Task MissingListedPayloadIsRejected()
    {
        using var fixture = HelperFixture.Create();
        File.Delete(Path.Combine(fixture.Root, "protocol.py"));
        var verifier = new ManagedBrowserHelperIntegrityVerifier(fixture.ManifestBytes);

        var error = await Assert.ThrowsAsync<ManagedBrowserHelperIntegrityException>(() =>
            verifier.VerifyAsync(fixture.Root, CancellationToken.None));

        Assert.Equal(ManagedBrowserHelperIntegrityError.MissingPayload, error.Error);
    }

    [Fact]
    public async Task ActualPayloadCasingMustExactlyMatchTheManifest()
    {
        using var fixture = HelperFixture.Create();
        var original = Path.Combine(fixture.Root, "main.py");
        var intermediate = Path.Combine(fixture.Root, "rename.tmp");
        File.Move(original, intermediate);
        File.Move(intermediate, Path.Combine(fixture.Root, "MAIN.PY"));
        var verifier = new ManagedBrowserHelperIntegrityVerifier(fixture.ManifestBytes);

        var error = await Assert.ThrowsAsync<ManagedBrowserHelperIntegrityException>(() =>
            verifier.VerifyThenLaunchAsync(
                fixture.Root, (_, _) => Task.FromResult(0), CancellationToken.None));

        Assert.Equal(ManagedBrowserHelperIntegrityError.MissingPayload, error.Error);
    }

    [Fact]
    public async Task ExtraUnlistedExecutableIsRejected()
    {
        using var fixture = HelperFixture.Create();
        File.WriteAllBytes(Path.Combine(fixture.Root, "surprise.exe"), [0x4d, 0x5a]);
        var verifier = new ManagedBrowserHelperIntegrityVerifier(fixture.ManifestBytes);

        var error = await Assert.ThrowsAsync<ManagedBrowserHelperIntegrityException>(() =>
            verifier.VerifyAsync(fixture.Root, CancellationToken.None));

        Assert.Equal(ManagedBrowserHelperIntegrityError.UnlistedPayload, error.Error);
    }

    [Theory]
    [InlineData(0, "ManagedChromium-v1", "3.14.0")]
    [InlineData(1, "ManagedChromium-v0", "3.14.0")]
    [InlineData(1, "ManagedChromium-v1", "3.13.9")]
    public async Task OldProtocolEngineOrRuntimeIsRejected(
        int protocolVersion,
        string engineVersion,
        string pythonVersion)
    {
        using var fixture = HelperFixture.Create(
            protocolVersion: protocolVersion,
            engineVersion: engineVersion,
            pythonVersion: pythonVersion);
        var verifier = new ManagedBrowserHelperIntegrityVerifier(fixture.ManifestBytes);

        var error = await Assert.ThrowsAsync<ManagedBrowserHelperIntegrityException>(() =>
            verifier.VerifyAsync(fixture.Root, CancellationToken.None));

        Assert.Equal(ManagedBrowserHelperIntegrityError.IncompatibleVersion, error.Error);
    }

    [Theory]
    [InlineData("../outside.exe")]
    [InlineData("C:/outside.exe")]
    [InlineData("main.py:alternate")]
    [InlineData("line\nbreak.py")]
    [InlineData("CON")]
    public async Task UnsafeManifestPathIsRejectedBeforeFilesystemAccess(string unsafePath)
    {
        using var fixture = HelperFixture.Create(additionalManifestPath: unsafePath);
        var verifier = new ManagedBrowserHelperIntegrityVerifier(fixture.ManifestBytes);

        var error = await Assert.ThrowsAsync<ManagedBrowserHelperIntegrityException>(() =>
            verifier.VerifyAsync(fixture.Root, CancellationToken.None));

        Assert.Equal(ManagedBrowserHelperIntegrityError.InvalidManifest, error.Error);
    }

    [Fact]
    public async Task DuplicateCaseInsensitiveManifestPathIsRejected()
    {
        using var fixture = HelperFixture.Create(additionalManifestPath: "MAIN.PY");
        var verifier = new ManagedBrowserHelperIntegrityVerifier(fixture.ManifestBytes);

        var error = await Assert.ThrowsAsync<ManagedBrowserHelperIntegrityException>(() =>
            verifier.VerifyAsync(fixture.Root, CancellationToken.None));

        Assert.Equal(ManagedBrowserHelperIntegrityError.InvalidManifest, error.Error);
    }

    [Fact]
    public async Task MissingLicenseNoticeIsRejectedEvenWhenManifestAgrees()
    {
        using var fixture = HelperFixture.Create(includeNotices: false);
        var verifier = new ManagedBrowserHelperIntegrityVerifier(fixture.ManifestBytes);

        var error = await Assert.ThrowsAsync<ManagedBrowserHelperIntegrityException>(() =>
            verifier.VerifyAsync(fixture.Root, CancellationToken.None));

        Assert.Equal(ManagedBrowserHelperIntegrityError.InvalidManifest, error.Error);
    }

    [Fact]
    public async Task IncorrectBuildIdentityIsRejected()
    {
        using var fixture = HelperFixture.Create(buildIdentityOverride: new string('0', 64));
        var verifier = new ManagedBrowserHelperIntegrityVerifier(fixture.ManifestBytes);

        var error = await Assert.ThrowsAsync<ManagedBrowserHelperIntegrityException>(() =>
            verifier.VerifyAsync(fixture.Root, CancellationToken.None));

        Assert.Equal(ManagedBrowserHelperIntegrityError.InvalidManifest, error.Error);
    }

    [Fact]
    public async Task VerificationFailureNeverCallsLaunchDelegate()
    {
        using var fixture = HelperFixture.Create();
        File.WriteAllText(Path.Combine(fixture.Root, "main.py"), "tampered");
        var launched = false;
        var verifier = new ManagedBrowserHelperIntegrityVerifier(fixture.ManifestBytes);

        await Assert.ThrowsAsync<ManagedBrowserHelperIntegrityException>(() =>
            verifier.VerifyThenLaunchAsync(
                fixture.Root,
                (_, _) => { launched = true; return Task.FromResult(0); },
                CancellationToken.None));

        Assert.False(launched);
    }

    [Fact]
    public async Task ReparsePointInsideBundleIsRejectedAsUnsafe()
    {
        using var fixture = HelperFixture.Create();
        var outside = Path.Combine(Path.GetTempPath(), $"managed-helper-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(fixture.Root, "linked"), outside);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                return; // The host has not enabled unprivileged Windows symbolic links.
            }
            var verifier = new ManagedBrowserHelperIntegrityVerifier(fixture.ManifestBytes);

            var error = await Assert.ThrowsAsync<ManagedBrowserHelperIntegrityException>(() =>
                verifier.VerifyAsync(fixture.Root, CancellationToken.None));

            Assert.Equal(ManagedBrowserHelperIntegrityError.UnsafeFilesystemEntry, error.Error);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [SelectedManagedHelperPackageFact(HelperIntegrationDirectoryVariable)]
    public async Task PackagedReleaseBundleMatchesTheEmbeddedManifestAndEveryPayloadHash()
    {
        var root = Environment.GetEnvironmentVariable(HelperIntegrationDirectoryVariable);
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("Required managed helper package directory is missing.");

        using var verified = await new ManagedBrowserHelperIntegrityVerifier()
            .VerifyAsync(root, CancellationToken.None);

        Assert.False(verified.IsDisposed);
        Assert.Matches("^[0-9a-f]{64}$", verified.BuildIdentity);
    }

    private sealed class HelperFixture : IDisposable
    {
        private HelperFixture(string root, byte[] manifestBytes, string buildIdentity)
        {
            Root = root;
            ManifestBytes = manifestBytes;
            BuildIdentity = buildIdentity;
        }

        public string Root { get; }
        public byte[] ManifestBytes { get; }
        public string BuildIdentity { get; }

        public static HelperFixture Create(
            int protocolVersion = ManagedBrowserProtocol.ProtocolVersion,
            string engineVersion = ManagedBrowserProtocol.ExecutionEngineVersion,
            string pythonVersion = ManagedBrowserHelperIntegrityVerifier.RequiredPythonVersion,
            bool includeNotices = true,
            string? additionalManifestPath = null,
            string? buildIdentityOverride = null)
        {
            var root = Path.Combine(Path.GetTempPath(), $"managed-helper-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["main.py"] = Encoding.UTF8.GetBytes("import protocol\n"),
                ["protocol.py"] = Encoding.UTF8.GetBytes("PROTOCOL_VERSION = 1\n"),
                ["requirements.lock"] = Encoding.UTF8.GetBytes("locked\n"),
                ["python.exe"] = [0x4d, 0x5a, 0, 1],
                ["python314.dll"] = [0x4d, 0x5a, 0, 2],
            };
            if (includeNotices)
                contents["THIRD_PARTY_NOTICES.txt"] = Encoding.UTF8.GetBytes("test notices\n");
            foreach (var (path, bytes) in contents)
            {
                var fullPath = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllBytes(fullPath, bytes);
            }

            var entries = contents.Select(pair => new ManifestEntry(
                pair.Key,
                pair.Value.LongLength,
                Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant())).ToList();
            if (additionalManifestPath is not null)
                entries.Add(new ManifestEntry(additionalManifestPath, 1, new string('a', 64)));
            entries.Sort((left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
            var computedIdentity = ComputeBuildIdentity(
                protocolVersion, engineVersion, pythonVersion, entries);
            var manifestBytes = WriteManifest(
                protocolVersion,
                engineVersion,
                pythonVersion,
                buildIdentityOverride ?? computedIdentity,
                entries);
            File.WriteAllBytes(Path.Combine(root, "helper-manifest.json"), manifestBytes);
            return new HelperFixture(root, manifestBytes, computedIdentity);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }

        private static string ComputeBuildIdentity(
            int protocolVersion,
            string engineVersion,
            string pythonVersion,
            IReadOnlyList<ManifestEntry> entries)
        {
            var builder = new StringBuilder();
            builder.Append("manifestVersion=1\n")
                .Append("protocolVersion=").Append(protocolVersion).Append('\n')
                .Append("executionEngineVersion=").Append(engineVersion).Append('\n')
                .Append("pythonVersion=").Append(pythonVersion).Append('\n')
                .Append("distributionMode=PersonalNonCommercial\n");
            foreach (var entry in entries)
                builder.Append(entry.Path).Append('\0').Append(entry.Length).Append('\0')
                    .Append(entry.Sha256).Append('\n');
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
                .ToLowerInvariant();
        }

        private static byte[] WriteManifest(
            int protocolVersion,
            string engineVersion,
            string pythonVersion,
            string buildIdentity,
            IEnumerable<ManifestEntry> entries)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("manifestVersion", 1);
                writer.WriteNumber("protocolVersion", protocolVersion);
                writer.WriteString("executionEngineVersion", engineVersion);
                writer.WriteString("pythonVersion", pythonVersion);
                writer.WriteString("distributionMode", "PersonalNonCommercial");
                writer.WriteString("buildIdentity", buildIdentity);
                writer.WriteStartArray("files");
                foreach (var entry in entries)
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", entry.Path);
                    writer.WriteNumber("length", entry.Length);
                    writer.WriteString("sha256", entry.Sha256);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            var json = Encoding.UTF8.GetString(stream.ToArray())
                .Replace("\r\n", "\n", StringComparison.Ordinal);
            return Encoding.UTF8.GetBytes(json + "\n");
        }

        private sealed record ManifestEntry(string Path, long Length, string Sha256);
    }
}

public sealed class SelectedManagedHelperPackageFactAttribute : FactAttribute
{
    public SelectedManagedHelperPackageFactAttribute(string directoryVariable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryVariable);
        if (string.Equals(
                Environment.GetEnvironmentVariable("BAIDU_BATCH_REQUIRE_HELPER_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(directoryVariable)))
            Skip = $"Set {directoryVariable} to run the packaged-helper integrity gate.";
    }
}
