using BaiduBatchTransfer.App.Features.ManagedBrowser;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedBrowserHelperIntegrityVerifierTests
{
    private const string HelperIntegrationDirectoryVariable =
        "BAIDU_BATCH_TRANSFER_TEST_HELPER_DIRECTORY";

    [Fact]
    public async Task ValidExactBundleIsCopiedToPrivateReadOnlySnapshotForLaunch()
    {
        using var fixture = HelperFixture.Create();
        var verifier = new ManagedBrowserHelperIntegrityVerifier(fixture.ManifestBytes);

        using var verified = await verifier.VerifyAsync(fixture.Root, CancellationToken.None);

        var executablePath = verified.ExecutablePath;
        var mainScriptPath = verified.MainScriptPath;
        var snapshotRoot = Path.GetDirectoryName(executablePath)!;
        var snapshotContainer = Directory.GetParent(snapshotRoot)!.FullName;
        Assert.EndsWith("python.exe", executablePath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("main.py", mainScriptPath, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(Path.GetFullPath(fixture.Root), Path.GetFullPath(snapshotRoot));
        Assert.Equal(fixture.BuildIdentity, verified.BuildIdentity);
        using (var ownership = JsonDocument.Parse(
                   ReadAllTextShared(Path.Combine(snapshotContainer, "ownership.json"))))
        using (var ready = JsonDocument.Parse(
                   ReadAllTextShared(Path.Combine(snapshotContainer, "ownership.ready.json"))))
        {
            Assert.Equal("Incomplete", ownership.RootElement.GetProperty("State").GetString());
            Assert.Equal("Ready1", ready.RootElement.GetProperty("State").GetString());
            Assert.Equal(ownership.RootElement.GetProperty("SnapshotId").GetString(),
                ready.RootElement.GetProperty("SnapshotId").GetString());
        }
        File.WriteAllText(Path.Combine(fixture.Root, "late-injection.pth"), "import injected");
        Assert.False(File.Exists(Path.Combine(snapshotRoot, "late-injection.pth")));
        Assert.Throws<UnauthorizedAccessException>(() =>
            File.WriteAllText(mainScriptPath, "replacement"));
        Assert.Throws<UnauthorizedAccessException>(() =>
            File.WriteAllText(Path.Combine(snapshotRoot, "late-injection.pth"), "import injected"));
        verified.Dispose();
        Assert.False(Directory.Exists(snapshotRoot));
        Assert.Throws<ObjectDisposedException>(() => _ = verified.ExecutablePath);
    }

    [Fact]
    public async Task MissingManifestIsRejectedBeforeLaunch()
    {
        using var fixture = HelperFixture.Create();
        File.Delete(Path.Combine(fixture.Root, "helper-manifest.json"));
        var verifier = new ManagedBrowserHelperIntegrityVerifier(fixture.ManifestBytes);

        var error = await Assert.ThrowsAsync<ManagedBrowserHelperIntegrityException>(() =>
            verifier.VerifyAsync(fixture.Root, CancellationToken.None));

        Assert.Equal(ManagedBrowserHelperIntegrityError.MissingManifest, error.Error);
    }

    [Fact]
    public async Task ExternalManifestMustExactlyMatchEmbeddedBytes()
    {
        using var fixture = HelperFixture.Create();
        File.AppendAllText(Path.Combine(fixture.Root, "helper-manifest.json"), "\n");
        var verifier = new ManagedBrowserHelperIntegrityVerifier(fixture.ManifestBytes);

        var error = await Assert.ThrowsAsync<ManagedBrowserHelperIntegrityException>(() =>
            verifier.VerifyAsync(fixture.Root, CancellationToken.None));

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
            verifier.VerifyAsync(fixture.Root, CancellationToken.None));

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

    [Fact]
    public async Task TreeEntryLimitIsEnforcedWhileEnumerating()
    {
        using var fixture = HelperFixture.Create();
        var verifier = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes,
            new ManagedBrowserHelperIntegrityLimits(
                MaximumTreeEntries: 3,
                MaximumTreeDepth: 8,
                MaximumTreeBytes: 1024 * 1024));

        var error = await Assert.ThrowsAsync<ManagedBrowserHelperIntegrityException>(() =>
            verifier.VerifyAsync(fixture.Root, CancellationToken.None));

        Assert.Equal(ManagedBrowserHelperIntegrityError.ResourceLimitExceeded, error.Error);
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
    public async Task VerificationFailureNeverReturnsAnOwnedBundle()
    {
        using var fixture = HelperFixture.Create();
        File.WriteAllText(Path.Combine(fixture.Root, "main.py"), "tampered");
        var verifier = new ManagedBrowserHelperIntegrityVerifier(fixture.ManifestBytes);

        await Assert.ThrowsAsync<ManagedBrowserHelperIntegrityException>(() =>
            verifier.VerifyAsync(fixture.Root, CancellationToken.None));
    }

    [Fact]
    public async Task OwnedBundleKeepsSnapshotForTheWholeChildAndDrainLifetime()
    {
        using var fixture = HelperFixture.Create();
        var verifier = new ManagedBrowserHelperIntegrityVerifier(fixture.ManifestBytes);
        var verified = await verifier.VerifyAsync(fixture.Root, CancellationToken.None);
        var snapshotRoot = Path.GetDirectoryName(verified.ExecutablePath)!;
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            Arguments = "/d /c ping 127.0.0.1 -n 2 >nul & echo drained",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("The lifetime probe did not start.");

        Assert.True(Directory.Exists(snapshotRoot));
        await process.WaitForExitAsync();
        _ = await process.StandardOutput.ReadToEndAsync();
        _ = await process.StandardError.ReadToEndAsync();
        Assert.True(Directory.Exists(snapshotRoot));

        verified.Dispose();

        Assert.Equal(ManagedBrowserHelperCleanupState.Cleaned, verified.CleanupState);
        Assert.False(Directory.Exists(snapshotRoot));
    }

    [Fact]
    public async Task CallerRetainsOwnershipAcrossStartHandshakeAndCancellationFailures()
    {
        using var fixture = HelperFixture.Create();
        var verified = await new ManagedBrowserHelperIntegrityVerifier(fixture.ManifestBytes)
            .VerifyAsync(fixture.Root, CancellationToken.None);
        var snapshotRoot = Path.GetDirectoryName(verified.ExecutablePath)!;

        foreach (var failure in new Exception[]
                 {
                     new InvalidOperationException("start"),
                     new EndOfStreamException("handshake"),
                     new OperationCanceledException("cancel"),
                 })
        {
            try { throw failure; }
            catch { Assert.True(Directory.Exists(snapshotRoot)); }
        }

        verified.Dispose();
        Assert.False(Directory.Exists(snapshotRoot));
    }

    [Fact]
    public async Task ExpiredCrashOrphanIsRecoveredButActiveAndForeignSnapshotsAreUntouched()
    {
        using var fixture = HelperFixture.Create();
        var created = new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero);
        var firstClock = new FixedTimeProvider(created);
        var firstVerifier = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes, timeProvider: firstClock,
            orphanMinimumAge: TimeSpan.FromHours(1));
        var crashed = await firstVerifier.VerifyAsync(fixture.Root, CancellationToken.None);
        var orphanContainer = Directory.GetParent(
            Path.GetDirectoryName(crashed.ExecutablePath)!)!.FullName;
        crashed.AbandonForProcessCrashSimulation();

        var active = await firstVerifier.VerifyAsync(fixture.Root, CancellationToken.None);
        var activeContainer = Directory.GetParent(
            Path.GetDirectoryName(active.ExecutablePath)!)!.FullName;
        var foreign = Path.Combine(VerifiedHelperSnapshotLease.SnapshotParentForDiagnostics,
            $"snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "foreign.txt"), "not ours");

        var laterVerifier = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes,
            timeProvider: new FixedTimeProvider(created.AddHours(2)),
            orphanMinimumAge: TimeSpan.FromHours(1));
        using var verified = await laterVerifier.VerifyAsync(fixture.Root, CancellationToken.None);

        Assert.False(Directory.Exists(orphanContainer));
        Assert.True(Directory.Exists(activeContainer));
        Assert.True(Directory.Exists(foreign));
        active.Dispose();
        Directory.Delete(foreign, recursive: true);
    }

    [Fact]
    public void BuildScriptPinsEachPrivateArchiveForFinalHashAndSameStreamExtraction()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var script = File.ReadAllText(Path.Combine(
            repositoryRoot, "scripts", "build_managed_browser_helper.ps1"));

        Assert.Contains("function Open-VerifiedZipInput", script, StringComparison.Ordinal);
        Assert.Contains("[IO.FileShare]::Read", script, StringComparison.Ordinal);
        Assert.Contains("$ArchiveStream.Position = 0", script, StringComparison.Ordinal);
        Assert.Contains("[IO.Compression.ZipArchive]::new($ArchiveStream", script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[IO.Compression.ZipFile]::OpenRead($Archive)", script,
            StringComparison.Ordinal);
        Assert.Contains("Assert-NonReparseAncestors", script, StringComparison.Ordinal);
        Assert.Contains("[IO.Path]::GetDirectoryName($currentPath)", script,
            StringComparison.Ordinal);
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

    [Fact]
    public async Task PreexistingSnapshotParentJunctionDoesNotChangeExternalTarget()
    {
        using var fixture = HelperFixture.Create();
        var testRoot = Path.Combine(Path.GetTempPath(), $"managed-helper-boundary-{Guid.NewGuid():N}");
        var trustedParent = Path.Combine(testRoot, "trusted");
        var snapshotParent = Path.Combine(trustedParent, "snapshots");
        var outside = Path.Combine(testRoot, "outside");
        Directory.CreateDirectory(trustedParent);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "untouched");
        try
        {
            CreateDirectoryJunction(snapshotParent, outside);
            var beforeAcl = new DirectoryInfo(outside).GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner)
                .GetSecurityDescriptorBinaryForm();
            var verifier = new ManagedBrowserHelperIntegrityVerifier(
                fixture.ManifestBytes, snapshotParent: snapshotParent);

            var error = await Assert.ThrowsAsync<ManagedBrowserHelperIntegrityException>(() =>
                verifier.VerifyAsync(fixture.Root, CancellationToken.None));

            Assert.Equal(ManagedBrowserHelperIntegrityError.UnsafeFilesystemEntry, error.Error);
            Assert.Equal("untouched", File.ReadAllText(Path.Combine(outside, "sentinel.txt")));
            Assert.Equal(beforeAcl, new DirectoryInfo(outside).GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner)
                .GetSecurityDescriptorBinaryForm());
        }
        finally
        {
            if (Directory.Exists(snapshotParent)) Directory.Delete(snapshotParent);
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("ownership.json", "SnapshotId")]
    [InlineData("ownership.json", "State")]
    [InlineData("ownership.json", "BuildIdentity")]
    [InlineData("ownership.json", "FileId")]
    [InlineData("ownership.json", "OwnerSid")]
    [InlineData("ownership.ready.json", "SnapshotId")]
    [InlineData("ownership.ready.json", "State")]
    [InlineData("ownership.ready.json", "BuildIdentity")]
    [InlineData("ownership.ready.json", "FileId")]
    [InlineData("ownership.ready.json", "OwnerSid")]
    public async Task NullSnapshotMarkerFieldIsIsolatedAndDoesNotBlockVerification(
        string markerFileName, string field)
    {
        using var fixture = HelperFixture.Create();
        var snapshotParent = Path.Combine(Path.GetTempPath(),
            $"managed-helper-orphans-{Guid.NewGuid():N}", "snapshots");
        var created = new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero);
        var first = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes, timeProvider: new FixedTimeProvider(created),
            orphanMinimumAge: TimeSpan.FromHours(1), snapshotParent: snapshotParent);
        var crashed = await first.VerifyAsync(fixture.Root, CancellationToken.None);
        var orphanContainer = Directory.GetParent(
            Path.GetDirectoryName(crashed.ExecutablePath)!)!.FullName;
        crashed.AbandonForProcessCrashSimulation();
        MakeWritable(Path.Combine(orphanContainer, markerFileName), isDirectory: false);
        var markerPath = Path.Combine(orphanContainer, markerFileName);
        var marker = File.ReadAllText(markerPath);
        marker = System.Text.RegularExpressions.Regex.Replace(
            marker, $"\"{field}\":\"[^\"]*\"", $"\"{field}\":null");
        File.WriteAllText(markerPath, marker);
        Assert.Contains($"\"{field}\":null", marker, StringComparison.Ordinal);

        var later = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes, timeProvider: new FixedTimeProvider(created.AddHours(2)),
            orphanMinimumAge: TimeSpan.FromHours(1), snapshotParent: snapshotParent);
        using var verified = await later.VerifyAsync(fixture.Root, CancellationToken.None);

        Assert.True(Directory.Exists(orphanContainer));
        verified.Dispose();
        MakeWritableTree(orphanContainer);
        Directory.Delete(orphanContainer, recursive: true);
        Directory.Delete(Path.GetDirectoryName(snapshotParent)!, recursive: true);
    }

    [Fact]
    public async Task CancellationDuringSnapshotCopyLeavesNoIncompleteOwnedContainer()
    {
        using var fixture = HelperFixture.Create();
        var snapshotParent = Path.Combine(Path.GetTempPath(),
            $"managed-helper-cancel-{Guid.NewGuid():N}", "snapshots");
        using var cancellation = new CancellationTokenSource();
        var verifier = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes, snapshotParent: snapshotParent,
            snapshotCreationObserver: phase =>
            {
                if (phase == ManagedBrowserHelperSnapshotPhase.CopyingPayload)
                    cancellation.Cancel();
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            verifier.VerifyAsync(fixture.Root, cancellation.Token));

        Assert.True(Directory.Exists(snapshotParent));
        Assert.Empty(Directory.EnumerateFileSystemEntries(snapshotParent));
        Directory.Delete(Path.GetDirectoryName(snapshotParent)!, recursive: true);
    }

    [Fact]
    public async Task PayloadRootReplacementDuringCopyDoesNotWriteOutsideOwnedContainer()
    {
        using var fixture = HelperFixture.Create(additionalContents: new Dictionary<string, byte[]>
        {
            ["nested/payload.bin"] = [0x10, 0x20, 0x30, 0x40],
        });
        var testRoot = Path.Combine(Path.GetTempPath(),
            $"managed-helper-copy-root-race-{Guid.NewGuid():N}");
        var snapshotParent = Path.Combine(testRoot, "snapshots");
        var outside = Path.Combine(testRoot, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "untouched");
        var outsideAcl = AclSddl(outside, isDirectory: true);
        var sentinelAcl = AclSddl(sentinel, isDirectory: false);
        string? container = null;
        var verifier = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes, snapshotParent: snapshotParent,
            snapshotCreationObserver: phase =>
            {
                if (phase != ManagedBrowserHelperSnapshotPhase.ContainerCreated) return;
                container = Assert.Single(Directory.EnumerateDirectories(snapshotParent));
                var payload = Path.Combine(container, "payload");
                Directory.Move(payload, Path.Combine(container, "payload.parked"));
                CreateDirectoryJunction(payload, outside);
            });

        await Assert.ThrowsAsync<ManagedBrowserHelperIntegrityException>(() =>
            verifier.VerifyAsync(fixture.Root, CancellationToken.None));

        var outsideEntries = Directory.EnumerateFileSystemEntries(outside)
            .Select(path => Path.GetFileName(path)!).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var sentinelContents = File.ReadAllText(sentinel);
        var outsideAclAfter = AclSddl(outside, isDirectory: true);
        var sentinelAclAfter = AclSddl(sentinel, isDirectory: false);
        Assert.NotNull(container);
        var junction = Path.Combine(container!, "payload");
        if (Directory.Exists(junction)) Directory.Delete(junction);
        if (Directory.Exists(container))
        {
            MakeWritableTree(container);
            Directory.Delete(container, recursive: true);
        }
        Directory.Delete(testRoot, recursive: true);

        Assert.Equal(new[] { "sentinel.txt" }, outsideEntries);
        Assert.Equal("untouched", sentinelContents);
        Assert.Equal(outsideAcl, outsideAclAfter);
        Assert.Equal(sentinelAcl, sentinelAclAfter);
    }

    [Fact]
    public async Task NestedPayloadDirectoryReplacementDuringCopyDoesNotWriteOutsideOwnedContainer()
    {
        using var fixture = HelperFixture.Create(additionalContents: new Dictionary<string, byte[]>
        {
            ["nested/payload.bin"] = [0x10, 0x20, 0x30, 0x40],
        });
        var testRoot = Path.Combine(Path.GetTempPath(),
            $"managed-helper-copy-nested-race-{Guid.NewGuid():N}");
        var snapshotParent = Path.Combine(testRoot, "snapshots");
        var outside = Path.Combine(testRoot, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "untouched");
        var outsideAcl = AclSddl(outside, isDirectory: true);
        var sentinelAcl = AclSddl(sentinel, isDirectory: false);
        string? container = null;
        var injected = false;
        var verifier = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes, snapshotParent: snapshotParent,
            snapshotCreationObserver: phase =>
            {
                if (phase != ManagedBrowserHelperSnapshotPhase.CopyingPayload || injected) return;
                injected = true;
                container = Assert.Single(Directory.EnumerateDirectories(snapshotParent));
                var nested = Path.Combine(container, "payload", "nested");
                Directory.Move(nested, Path.Combine(container, "payload", "nested.parked"));
                CreateDirectoryJunction(nested, outside);
            });

        await Assert.ThrowsAsync<ManagedBrowserHelperIntegrityException>(() =>
            verifier.VerifyAsync(fixture.Root, CancellationToken.None));

        var outsideEntries = Directory.EnumerateFileSystemEntries(outside)
            .Select(path => Path.GetFileName(path)!).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var sentinelContents = File.ReadAllText(sentinel);
        var outsideAclAfter = AclSddl(outside, isDirectory: true);
        var sentinelAclAfter = AclSddl(sentinel, isDirectory: false);
        Assert.NotNull(container);
        var junction = Path.Combine(container!, "payload", "nested");
        if (Directory.Exists(junction)) Directory.Delete(junction);
        if (Directory.Exists(container))
        {
            MakeWritableTree(container);
            Directory.Delete(container, recursive: true);
        }
        Directory.Delete(testRoot, recursive: true);

        Assert.Equal(new[] { "sentinel.txt" }, outsideEntries);
        Assert.Equal("untouched", sentinelContents);
        Assert.Equal(outsideAcl, outsideAclAfter);
        Assert.Equal(sentinelAcl, sentinelAclAfter);
    }

    [Fact]
    public async Task LockedCopiedPayloadBecomesRecoverableIncompleteSnapshotAfterCancellation()
    {
        using var fixture = HelperFixture.Create(additionalContents: new Dictionary<string, byte[]>
        {
            ["_asyncio.pyd"] = [0x4d, 0x5a, 0x01, 0x02],
        });
        var snapshotParent = Path.Combine(Path.GetTempPath(),
            $"managed-helper-locked-copy-{Guid.NewGuid():N}", "snapshots");
        var created = new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        FileStream? lockedPayload = null;
        var verifier = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes, timeProvider: new FixedTimeProvider(created),
            orphanMinimumAge: TimeSpan.Zero, snapshotParent: snapshotParent,
            snapshotFileCopiedObserver: (relative, destination) =>
            {
                if (!string.Equals(relative, "_asyncio.pyd", StringComparison.Ordinal)) return;
                lockedPayload = new FileStream(destination, FileMode.Open, FileAccess.Read,
                    FileShare.Read | FileShare.Write);
                cancellation.Cancel();
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            verifier.VerifyAsync(fixture.Root, cancellation.Token));
        var incomplete = Assert.Single(Directory.EnumerateDirectories(snapshotParent));
        using (var marker = JsonDocument.Parse(
                   File.ReadAllText(Path.Combine(incomplete, "ownership.json"))))
        {
            Assert.Equal(2, marker.RootElement.GetProperty("Version").GetInt32());
            Assert.Equal("Incomplete", marker.RootElement.GetProperty("State").GetString());
        }
        Assert.False(File.Exists(Path.Combine(incomplete, "ownership.ready.json")));
        Assert.NotNull(lockedPayload);
        lockedPayload.Dispose();

        using var recovered = await new ManagedBrowserHelperIntegrityVerifier(
                fixture.ManifestBytes, timeProvider: new FixedTimeProvider(created.AddHours(1)),
                orphanMinimumAge: TimeSpan.Zero, snapshotParent: snapshotParent)
            .VerifyAsync(fixture.Root, CancellationToken.None);

        Assert.False(Directory.Exists(incomplete));
        recovered.Dispose();
        Directory.Delete(Path.GetDirectoryName(snapshotParent)!, recursive: true);
    }

    [Fact]
    public async Task IncompleteOwnedSnapshotWithoutActiveLockIsRecovered()
    {
        using var fixture = HelperFixture.Create();
        var snapshotParent = Path.Combine(Path.GetTempPath(),
            $"managed-helper-missing-active-{Guid.NewGuid():N}", "snapshots");
        var created = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
        var first = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes, timeProvider: new FixedTimeProvider(created),
            orphanMinimumAge: TimeSpan.Zero, snapshotParent: snapshotParent);
        var crashed = await first.VerifyAsync(fixture.Root, CancellationToken.None);
        var incomplete = Directory.GetParent(Path.GetDirectoryName(crashed.ExecutablePath)!)!.FullName;
        crashed.AbandonForProcessCrashSimulation();
        MakeWritableTree(incomplete);
        File.Delete(Path.Combine(incomplete, "ownership.ready.json"));
        File.Delete(Path.Combine(incomplete, "active.lock"));

        using var recovered = await new ManagedBrowserHelperIntegrityVerifier(
                fixture.ManifestBytes, timeProvider: new FixedTimeProvider(created.AddHours(1)),
                orphanMinimumAge: TimeSpan.Zero, snapshotParent: snapshotParent)
            .VerifyAsync(fixture.Root, CancellationToken.None);

        Assert.False(Directory.Exists(incomplete));
        recovered.Dispose();
        Directory.Delete(Path.GetDirectoryName(snapshotParent)!, recursive: true);
    }

    [Fact]
    public async Task ReadyMarkerNameThatIsADirectoryIsMalformedAndLeftUntouched()
    {
        using var fixture = HelperFixture.Create();
        var snapshotParent = Path.Combine(Path.GetTempPath(),
            $"managed-helper-ready-directory-{Guid.NewGuid():N}", "snapshots");
        var created = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
        var first = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes, timeProvider: new FixedTimeProvider(created),
            orphanMinimumAge: TimeSpan.Zero, snapshotParent: snapshotParent);
        var crashed = await first.VerifyAsync(fixture.Root, CancellationToken.None);
        var malformed = Directory.GetParent(Path.GetDirectoryName(crashed.ExecutablePath)!)!.FullName;
        crashed.AbandonForProcessCrashSimulation();
        MakeWritableTree(malformed);
        var readyPath = Path.Combine(malformed, "ownership.ready.json");
        File.Delete(readyPath);
        Directory.CreateDirectory(readyPath);

        using var verified = await new ManagedBrowserHelperIntegrityVerifier(
                fixture.ManifestBytes, timeProvider: new FixedTimeProvider(created.AddHours(1)),
                orphanMinimumAge: TimeSpan.Zero, snapshotParent: snapshotParent)
            .VerifyAsync(fixture.Root, CancellationToken.None);

        Assert.True(Directory.Exists(malformed));
        verified.Dispose();
        MakeWritableTree(malformed);
        Directory.Delete(malformed, recursive: true);
        Directory.Delete(Path.GetDirectoryName(snapshotParent)!, recursive: true);
    }

    [Fact]
    public async Task ReadyMarkerNameThatIsAReparsePointIsMalformedAndLeavesTargetUntouched()
    {
        using var fixture = HelperFixture.Create();
        var testRoot = Path.Combine(Path.GetTempPath(),
            $"managed-helper-ready-reparse-{Guid.NewGuid():N}");
        var snapshotParent = Path.Combine(testRoot, "snapshots");
        var outside = Path.Combine(testRoot, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "untouched");
        var created = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
        var first = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes, timeProvider: new FixedTimeProvider(created),
            orphanMinimumAge: TimeSpan.Zero, snapshotParent: snapshotParent);
        var crashed = await first.VerifyAsync(fixture.Root, CancellationToken.None);
        var malformed = Directory.GetParent(Path.GetDirectoryName(crashed.ExecutablePath)!)!.FullName;
        crashed.AbandonForProcessCrashSimulation();
        MakeWritableTree(malformed);
        var readyPath = Path.Combine(malformed, "ownership.ready.json");
        File.Delete(readyPath);
        CreateDirectoryJunction(readyPath, outside);

        using var verified = await new ManagedBrowserHelperIntegrityVerifier(
                fixture.ManifestBytes, timeProvider: new FixedTimeProvider(created.AddHours(1)),
                orphanMinimumAge: TimeSpan.Zero, snapshotParent: snapshotParent)
            .VerifyAsync(fixture.Root, CancellationToken.None);

        Assert.True(Directory.Exists(malformed));
        Assert.Equal("untouched", File.ReadAllText(Path.Combine(outside, "sentinel.txt")));
        verified.Dispose();
        Directory.Delete(readyPath);
        MakeWritableTree(malformed);
        Directory.Delete(malformed, recursive: true);
        Directory.Delete(testRoot, recursive: true);
    }

    [Fact]
    public async Task ReadyPreparingMarkerIsRecoveredAsIncomplete()
    {
        using var fixture = HelperFixture.Create();
        var snapshotParent = Path.Combine(Path.GetTempPath(),
            $"managed-helper-ready-preparing-{Guid.NewGuid():N}", "snapshots");
        var created = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
        var first = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes, timeProvider: new FixedTimeProvider(created),
            orphanMinimumAge: TimeSpan.Zero, snapshotParent: snapshotParent);
        var crashed = await first.VerifyAsync(fixture.Root, CancellationToken.None);
        var incomplete = Directory.GetParent(Path.GetDirectoryName(crashed.ExecutablePath)!)!.FullName;
        crashed.AbandonForProcessCrashSimulation();
        var readyPath = Path.Combine(incomplete, "ownership.ready.json");
        MakeWritable(readyPath, isDirectory: false);
        var committed = File.ReadAllText(readyPath);
        var preparing = committed.Replace("\"State\":\"Ready1\"", "\"State\":\"Ready0\"",
            StringComparison.Ordinal);
        Assert.Equal(committed.Length, preparing.Length);
        Assert.NotEqual(committed, preparing);
        File.WriteAllText(readyPath, preparing);

        using var recovered = await new ManagedBrowserHelperIntegrityVerifier(
                fixture.ManifestBytes, timeProvider: new FixedTimeProvider(created.AddHours(1)),
                orphanMinimumAge: TimeSpan.Zero, snapshotParent: snapshotParent)
            .VerifyAsync(fixture.Root, CancellationToken.None);

        Assert.False(Directory.Exists(incomplete));
        recovered.Dispose();
        Directory.Delete(Path.GetDirectoryName(snapshotParent)!, recursive: true);
    }

    [Fact]
    public async Task UnexpectedFixedLengthReadyStateIsMalformedAndLeftUntouched()
    {
        using var fixture = HelperFixture.Create();
        var snapshotParent = Path.Combine(Path.GetTempPath(),
            $"managed-helper-ready-state-{Guid.NewGuid():N}", "snapshots");
        var created = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
        var first = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes, timeProvider: new FixedTimeProvider(created),
            orphanMinimumAge: TimeSpan.Zero, snapshotParent: snapshotParent);
        var crashed = await first.VerifyAsync(fixture.Root, CancellationToken.None);
        var malformed = Directory.GetParent(Path.GetDirectoryName(crashed.ExecutablePath)!)!.FullName;
        crashed.AbandonForProcessCrashSimulation();
        var readyPath = Path.Combine(malformed, "ownership.ready.json");
        MakeWritable(readyPath, isDirectory: false);
        var committed = File.ReadAllText(readyPath);
        var malformedMarker = committed.Replace("\"State\":\"Ready1\"", "\"State\":\"ReadyX\"",
            StringComparison.Ordinal);
        Assert.Equal(committed.Length, malformedMarker.Length);
        Assert.NotEqual(committed, malformedMarker);
        File.WriteAllText(readyPath, malformedMarker);

        using var verified = await new ManagedBrowserHelperIntegrityVerifier(
                fixture.ManifestBytes, timeProvider: new FixedTimeProvider(created.AddHours(1)),
                orphanMinimumAge: TimeSpan.Zero, snapshotParent: snapshotParent)
            .VerifyAsync(fixture.Root, CancellationToken.None);

        Assert.True(Directory.Exists(malformed));
        verified.Dispose();
        MakeWritableTree(malformed);
        Directory.Delete(malformed, recursive: true);
        Directory.Delete(Path.GetDirectoryName(snapshotParent)!, recursive: true);
    }

    [Theory]
    [InlineData((int)ManagedBrowserHelperSnapshotPhase.OwnershipPersisted)]
    [InlineData((int)ManagedBrowserHelperSnapshotPhase.PayloadSealed)]
    [InlineData((int)ManagedBrowserHelperSnapshotPhase.OwnershipSealed)]
    [InlineData((int)ManagedBrowserHelperSnapshotPhase.ReadyPreparingSealed)]
    [InlineData((int)ManagedBrowserHelperSnapshotPhase.ContainerSealed)]
    public async Task EveryPrecommitSealPhaseStillHasOnlyIncompleteOwnership(int phaseValue)
    {
        using var fixture = HelperFixture.Create();
        var target = (ManagedBrowserHelperSnapshotPhase)phaseValue;
        var snapshotParent = Path.Combine(Path.GetTempPath(),
            $"managed-helper-precommit-{Guid.NewGuid():N}", "snapshots");
        var inspected = false;
        var verifier = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes, snapshotParent: snapshotParent,
            snapshotCreationObserver: phase =>
            {
                if (phase != target) return;
                var container = Assert.Single(Directory.EnumerateDirectories(snapshotParent));
                Assert.True(File.Exists(Path.Combine(container, "ownership.json")));
                var readyPath = Path.Combine(container, "ownership.ready.json");
                if (File.Exists(readyPath))
                {
                    using var ready = JsonDocument.Parse(ReadAllTextShared(readyPath));
                    Assert.Equal("Ready0", ready.RootElement.GetProperty("State").GetString());
                }
                inspected = true;
                throw new IOException("simulated precommit crash");
            });

        var error = await Assert.ThrowsAsync<ManagedBrowserHelperIntegrityException>(() =>
            verifier.VerifyAsync(fixture.Root, CancellationToken.None));

        Assert.Equal(ManagedBrowserHelperIntegrityError.VerificationIoFailure, error.Error);
        Assert.True(inspected);
        Assert.Empty(Directory.EnumerateFileSystemEntries(snapshotParent));
        Directory.Delete(Path.GetDirectoryName(snapshotParent)!, recursive: true);
    }

    [Fact]
    public async Task ReadyCommitIsObservedOnlyAfterEveryBoundaryIsReadOnly()
    {
        using var fixture = HelperFixture.Create();
        var snapshotParent = Path.Combine(Path.GetTempPath(),
            $"managed-helper-ready-commit-{Guid.NewGuid():N}", "snapshots");
        var inspected = false;
        var verifier = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes, snapshotParent: snapshotParent,
            snapshotCreationObserver: phase =>
            {
                if (phase != ManagedBrowserHelperSnapshotPhase.ReadyCommitted) return;
                var container = Assert.Single(Directory.EnumerateDirectories(snapshotParent));
                var readyPath = Path.Combine(container, "ownership.ready.json");
                Assert.True(File.Exists(readyPath));
                using (var ready = JsonDocument.Parse(ReadAllTextShared(readyPath)))
                    Assert.Equal("Ready1", ready.RootElement.GetProperty("State").GetString());
                Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(
                    Path.Combine(container, "ownership.json"), "replacement"));
                Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(
                    Path.Combine(container, "ownership.ready.json"), "replacement"));
                Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(
                    Path.Combine(container, "payload", "main.py"), "replacement"));
                Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(
                    Path.Combine(container, "late-injection.pth"), "replacement"));
                inspected = true;
            });

        using var verified = await verifier.VerifyAsync(fixture.Root, CancellationToken.None);

        Assert.True(inspected);
        verified.Dispose();
        Directory.Delete(Path.GetDirectoryName(snapshotParent)!, recursive: true);
    }

    [Fact]
    public async Task ReadyCommitChangesExactlyOneByteWithoutChangingMarkerLength()
    {
        using var fixture = HelperFixture.Create();
        var snapshotParent = Path.Combine(Path.GetTempPath(),
            $"managed-helper-ready-byte-{Guid.NewGuid():N}", "snapshots");
        byte[]? preparing = null;
        byte[]? committed = null;
        var verifier = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes, snapshotParent: snapshotParent,
            snapshotCreationObserver: phase =>
            {
                if (phase is not (ManagedBrowserHelperSnapshotPhase.ReadyPreparingSealed or
                    ManagedBrowserHelperSnapshotPhase.ReadyCommitted)) return;
                var container = Assert.Single(Directory.EnumerateDirectories(snapshotParent));
                var bytes = ReadAllBytesShared(Path.Combine(container, "ownership.ready.json"));
                if (phase == ManagedBrowserHelperSnapshotPhase.ReadyPreparingSealed)
                    preparing = bytes;
                else
                    committed = bytes;
            });

        using var verified = await verifier.VerifyAsync(fixture.Root, CancellationToken.None);

        Assert.NotNull(preparing);
        Assert.NotNull(committed);
        Assert.Equal(preparing.Length, committed.Length);
        var differences = preparing.Zip(committed, (before, after) => (before, after))
            .Where(pair => pair.before != pair.after).ToArray();
        var difference = Assert.Single(differences);
        Assert.Equal((byte)'0', difference.before);
        Assert.Equal((byte)'1', difference.after);
        verified.Dispose();
        Directory.Delete(Path.GetDirectoryName(snapshotParent)!, recursive: true);
    }

    [Fact]
    public async Task SnapshotSealingRejectsInjectedJunctionWithoutChangingExternalTree()
    {
        using var fixture = HelperFixture.Create();
        var testRoot = Path.Combine(Path.GetTempPath(), $"managed-helper-seal-race-{Guid.NewGuid():N}");
        var snapshotParent = Path.Combine(testRoot, "snapshots");
        var outside = Path.Combine(testRoot, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "untouched");
        var outsideAcl = AclSddl(outside, isDirectory: true);
        var sentinelAcl = AclSddl(sentinel, isDirectory: false);
        string? container = null;
        var verifier = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes, snapshotParent: snapshotParent,
            snapshotCreationObserver: phase =>
            {
                if (phase == ManagedBrowserHelperSnapshotPhase.SealingAcl)
                {
                    container = Assert.Single(Directory.EnumerateDirectories(snapshotParent));
                    CreateDirectoryJunction(Path.Combine(container, "payload", "injected"), outside);
                }
                else if (phase == ManagedBrowserHelperSnapshotPhase.PayloadSealed)
                {
                    throw new IOException("stop after sealing boundary");
                }
            });

        await Assert.ThrowsAsync<ManagedBrowserHelperIntegrityException>(() =>
            verifier.VerifyAsync(fixture.Root, CancellationToken.None));

        Assert.Equal("untouched", File.ReadAllText(sentinel));
        Assert.Equal(outsideAcl, AclSddl(outside, isDirectory: true));
        Assert.Equal(sentinelAcl, AclSddl(sentinel, isDirectory: false));
        Assert.NotNull(container);
        var junction = Path.Combine(container!, "payload", "injected");
        if (Directory.Exists(junction))
        {
            MakeWritable(Path.GetDirectoryName(junction)!, isDirectory: true);
            Directory.Delete(junction);
        }
        if (Directory.Exists(container))
        {
            MakeWritableTree(container);
            Directory.Delete(container, recursive: true);
        }
        Directory.Delete(testRoot, recursive: true);
    }

    [Fact]
    public async Task CreateFailureReplacementDoesNotChangeReplacementAclOrContents()
    {
        using var fixture = HelperFixture.Create();
        var testRoot = Path.Combine(Path.GetTempPath(), $"managed-helper-create-replace-{Guid.NewGuid():N}");
        var snapshotParent = Path.Combine(testRoot, "snapshots");
        var replacementSource = Path.Combine(testRoot, "replacement");
        Directory.CreateDirectory(replacementSource);
        var sentinel = Path.Combine(replacementSource, "sentinel.txt");
        File.WriteAllText(sentinel, "untouched");
        var sentinelAcl = AclSddl(sentinel, isDirectory: false);
        string? container = null;
        string? parkedMarker = null;
        var verifier = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes, snapshotParent: snapshotParent,
            snapshotCreationObserver: phase =>
            {
                if (phase != ManagedBrowserHelperSnapshotPhase.OwnershipPersisted) return;
                container = Assert.Single(Directory.EnumerateDirectories(snapshotParent));
                var marker = Path.Combine(container, "ownership.json");
                parkedMarker = Path.Combine(container, "ownership.parked.json");
                File.Move(marker, parkedMarker);
                File.Move(sentinel, marker);
                throw new IOException("replacement injected before create cleanup");
            });

        await Assert.ThrowsAsync<ManagedBrowserHelperIntegrityException>(() =>
            verifier.VerifyAsync(fixture.Root, CancellationToken.None));

        Assert.NotNull(container);
        var injected = Path.Combine(container!, "ownership.json");
        Assert.Equal("untouched", File.ReadAllText(injected));
        Assert.Equal(sentinelAcl, AclSddl(injected, isDirectory: false));
        MakeWritableTree(container);
        Directory.Delete(container, recursive: true);
        if (File.Exists(parkedMarker))
        {
            MakeWritable(parkedMarker!, isDirectory: false);
            File.Delete(parkedMarker!);
        }
        Directory.Delete(testRoot, recursive: true);
    }

    [Fact]
    public async Task CleanupReplacementAfterActiveDeleteDoesNotChangeReplacementAclOrContents()
    {
        using var fixture = HelperFixture.Create();
        var testRoot = Path.Combine(Path.GetTempPath(), $"managed-helper-clean-replace-{Guid.NewGuid():N}");
        var snapshotParent = Path.Combine(testRoot, "snapshots");
        var replacementSource = Path.Combine(testRoot, "replacement");
        Directory.CreateDirectory(replacementSource);
        var replacementReady = Path.Combine(replacementSource, "ownership.ready.json");
        File.WriteAllText(replacementReady, "replacement-ready");
        var readyAcl = AclSddl(replacementReady, isDirectory: false);
        string? container = null;
        string? parkedReady = null;
        var verifier = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes, snapshotParent: snapshotParent,
            snapshotCleanupObserver: phase =>
            {
                if (phase != ManagedBrowserHelperCleanupPhase.ActiveLockDeleted) return;
                var ready = Path.Combine(container!, "ownership.ready.json");
                parkedReady = Path.Combine(container!, "ownership.ready.parked.json");
                MakeWritable(container!, isDirectory: true);
                MakeWritable(ready, isDirectory: false);
                File.Move(ready, parkedReady);
                File.Move(replacementReady, ready);
            });
        var bundle = await verifier.VerifyAsync(fixture.Root, CancellationToken.None);
        container = Directory.GetParent(Path.GetDirectoryName(bundle.ExecutablePath)!)!.FullName;

        bundle.Dispose();

        Assert.Equal(ManagedBrowserHelperCleanupState.DeferredCleanupRegistered, bundle.CleanupState);
        Assert.Equal("replacement-ready", ReadAllTextShared(
            Path.Combine(container!, "ownership.ready.json")));
        Assert.Equal(readyAcl, AclSddl(Path.Combine(container, "ownership.ready.json"), isDirectory: false));
        MakeWritable(Path.Combine(container, "ownership.ready.json"), isDirectory: false);
        File.Delete(Path.Combine(container, "ownership.ready.json"));
        VerifiedHelperSnapshotLease.RecoverExpiredOrphans(
            snapshotParent, ManagedBrowserHelperIntegrityLimits.Default,
            DateTimeOffset.UtcNow, TimeSpan.MaxValue, CancellationToken.None);
        Assert.Equal(ManagedBrowserHelperCleanupState.Cleaned, bundle.CleanupState);
        if (File.Exists(parkedReady)) File.Delete(parkedReady!);
        Directory.Delete(testRoot, recursive: true);
    }

    [Fact]
    public async Task OrphanFallbackRejectsInjectedJunctionWithoutChangingExternalTree()
    {
        using var fixture = HelperFixture.Create();
        var testRoot = Path.Combine(Path.GetTempPath(), $"managed-helper-fallback-junction-{Guid.NewGuid():N}");
        var snapshotParent = Path.Combine(testRoot, "snapshots");
        var outside = Path.Combine(testRoot, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "untouched");
        var outsideAcl = AclSddl(outside, isDirectory: true);
        var sentinelAcl = AclSddl(sentinel, isDirectory: false);
        string? container = null;
        var attempts = 0;
        var limits = ManagedBrowserHelperIntegrityLimits.Default;
        var verifier = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes, limits, snapshotParent: snapshotParent,
            cleanupAttemptObserver: () =>
            {
                attempts++;
                if (attempts == VerifiedHelperSnapshotLease.MaximumDeferredCleanupAttempts)
                {
                    container ??= Assert.Single(Directory.EnumerateDirectories(snapshotParent));
                    MakeWritable(container, isDirectory: true);
                    CreateDirectoryJunction(Path.Combine(container, "injected"), outside);
                }
                throw new IOException("persistent cleanup failure");
            });
        var bundle = await verifier.VerifyAsync(fixture.Root, CancellationToken.None);
        container = Directory.GetParent(Path.GetDirectoryName(bundle.ExecutablePath)!)!.FullName;

        bundle.Dispose();
        VerifiedHelperSnapshotLease.RecoverExpiredOrphans(
            snapshotParent, limits, DateTimeOffset.UtcNow, TimeSpan.MaxValue, CancellationToken.None);
        VerifiedHelperSnapshotLease.RecoverExpiredOrphans(
            snapshotParent, limits, DateTimeOffset.UtcNow, TimeSpan.MaxValue, CancellationToken.None);

        Assert.Equal(ManagedBrowserHelperCleanupState.OrphanRecoveryPending, bundle.CleanupState);
        Assert.Equal("untouched", File.ReadAllText(sentinel));
        Assert.Equal(outsideAcl, AclSddl(outside, isDirectory: true));
        Assert.Equal(sentinelAcl, AclSddl(sentinel, isDirectory: false));
        var junction = Path.Combine(container, "injected");
        if (Directory.Exists(junction)) Directory.Delete(junction);
        MakeWritableTree(container);
        Directory.Delete(container, recursive: true);
        Directory.Delete(testRoot, recursive: true);
    }

    [Theory]
    [InlineData((int)ManagedBrowserHelperSnapshotPhase.ContainerCreated)]
    [InlineData((int)ManagedBrowserHelperSnapshotPhase.MarkerWritten)]
    [InlineData((int)ManagedBrowserHelperSnapshotPhase.PinningSnapshot)]
    [InlineData((int)ManagedBrowserHelperSnapshotPhase.SealingAcl)]
    public async Task SnapshotCreationFailureAtEveryOwnedPhaseLeavesNoContainer(
        int failurePhaseValue)
    {
        var failurePhase = (ManagedBrowserHelperSnapshotPhase)failurePhaseValue;
        using var fixture = HelperFixture.Create();
        var snapshotParent = Path.Combine(Path.GetTempPath(),
            $"managed-helper-create-failure-{Guid.NewGuid():N}", "snapshots");
        var verifier = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes, snapshotParent: snapshotParent,
            snapshotCreationObserver: phase =>
            {
                if (phase == failurePhase) throw new IOException("injected creation failure");
            });

        var error = await Assert.ThrowsAsync<ManagedBrowserHelperIntegrityException>(() =>
            verifier.VerifyAsync(fixture.Root, CancellationToken.None));

        Assert.Equal(ManagedBrowserHelperIntegrityError.VerificationIoFailure, error.Error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(snapshotParent));
        Directory.Delete(Path.GetDirectoryName(snapshotParent)!, recursive: true);
    }

    [Fact]
    public async Task DeferredCleanupSuccessIsVisibleThroughDisposedBundleState()
    {
        using var fixture = HelperFixture.Create();
        var snapshotParent = Path.Combine(Path.GetTempPath(),
            $"managed-helper-deferred-state-{Guid.NewGuid():N}", "snapshots");
        var failCleanup = true;
        var verifier = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes, snapshotParent: snapshotParent,
            cleanupAttemptObserver: () =>
            {
                if (failCleanup) throw new IOException("injected cleanup failure");
            });
        var bundle = await verifier.VerifyAsync(fixture.Root, CancellationToken.None);
        var container = Directory.GetParent(Path.GetDirectoryName(bundle.ExecutablePath)!)!.FullName;

        bundle.Dispose();
        Assert.Equal(ManagedBrowserHelperCleanupState.DeferredCleanupRegistered, bundle.CleanupState);
        failCleanup = false;
        using var trigger = await new ManagedBrowserHelperIntegrityVerifier(
                fixture.ManifestBytes, snapshotParent: snapshotParent)
            .VerifyAsync(fixture.Root, CancellationToken.None);

        Assert.Equal(ManagedBrowserHelperCleanupState.Cleaned, bundle.CleanupState);
        Assert.False(Directory.Exists(container));
        trigger.Dispose();
        Directory.Delete(Path.GetDirectoryName(snapshotParent)!, recursive: true);
    }

    [Fact]
    public async Task PersistentCleanupFailuresKeepQueueAndHandlesBoundedThenBecomeRecoverableOrphans()
    {
        using var fixture = HelperFixture.Create();
        var snapshotParent = Path.Combine(Path.GetTempPath(),
            $"managed-helper-deferred-bounds-{Guid.NewGuid():N}", "snapshots");
        var created = new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero);
        var failing = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes, timeProvider: new FixedTimeProvider(created),
            orphanMinimumAge: TimeSpan.Zero, snapshotParent: snapshotParent,
            cleanupAttemptObserver: () => throw new IOException("persistent cleanup failure"));
        var bundles = new List<VerifiedManagedBrowserHelperBundle>();
        var containers = new List<string>();
        for (var index = 0; index < VerifiedHelperSnapshotLease.MaximumDeferredLeaseCount + 3; index++)
        {
            var bundle = await failing.VerifyAsync(fixture.Root, CancellationToken.None);
            bundles.Add(bundle);
            containers.Add(Directory.GetParent(Path.GetDirectoryName(bundle.ExecutablePath)!)!.FullName);
        }
        foreach (var bundle in bundles) bundle.Dispose();

        Assert.All(bundles.Take(VerifiedHelperSnapshotLease.MaximumDeferredLeaseCount), bundle =>
            Assert.Equal(ManagedBrowserHelperCleanupState.DeferredCleanupRegistered,
                bundle.CleanupState));
        Assert.All(bundles.Skip(VerifiedHelperSnapshotLease.MaximumDeferredLeaseCount), bundle =>
            Assert.Equal(ManagedBrowserHelperCleanupState.OrphanRecoveryPending,
                bundle.CleanupState));

        Assert.InRange(VerifiedHelperSnapshotLease.DeferredLeaseCountForDiagnostics,
            0, VerifiedHelperSnapshotLease.MaximumDeferredLeaseCount);
        Assert.InRange(VerifiedHelperSnapshotLease.DeferredHandleCountForDiagnostics,
            0, VerifiedHelperSnapshotLease.MaximumDeferredHandleCount);

        var recovery = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes, timeProvider: new FixedTimeProvider(created.AddHours(1)),
            orphanMinimumAge: TimeSpan.Zero, snapshotParent: snapshotParent);
        for (var attempt = 0; attempt < VerifiedHelperSnapshotLease.MaximumDeferredCleanupAttempts + 2; attempt++)
        {
            using var trigger = await recovery.VerifyAsync(fixture.Root, CancellationToken.None);
        }

        Assert.Equal(0, VerifiedHelperSnapshotLease.DeferredLeaseCountForDiagnostics);
        Assert.Equal(0, VerifiedHelperSnapshotLease.DeferredHandleCountForDiagnostics);
        Assert.All(containers, container => Assert.False(Directory.Exists(container)));
        Assert.All(bundles, bundle =>
            Assert.Equal(ManagedBrowserHelperCleanupState.Cleaned, bundle.CleanupState));
        Directory.Delete(Path.GetDirectoryName(snapshotParent)!, recursive: true);
    }

    [Fact]
    public async Task RepeatedVerificationRotatesPastOneHundredTwentyEightForeignCandidates()
    {
        using var fixture = HelperFixture.Create();
        var snapshotParent = Path.Combine(Path.GetTempPath(),
            $"managed-helper-orphan-cursor-{Guid.NewGuid():N}", "snapshots");
        Directory.CreateDirectory(snapshotParent);
        for (var index = 0; index < 128; index++)
        {
            var foreign = Path.Combine(snapshotParent, $"snapshot-{index + 1:x32}");
            Directory.CreateDirectory(foreign);
            File.WriteAllText(Path.Combine(foreign, "foreign.txt"), "not-owned");
        }
        var created = new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero);
        var first = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes, timeProvider: new FixedTimeProvider(created),
            orphanMinimumAge: TimeSpan.Zero, snapshotParent: snapshotParent);
        var crashed = await first.VerifyAsync(fixture.Root, CancellationToken.None);
        var orphan = Directory.GetParent(Path.GetDirectoryName(crashed.ExecutablePath)!)!.FullName;
        crashed.AbandonForProcessCrashSimulation();

        var later = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes, timeProvider: new FixedTimeProvider(created.AddHours(1)),
            orphanMinimumAge: TimeSpan.Zero, snapshotParent: snapshotParent);
        for (var attempt = 0; attempt < 3 && Directory.Exists(orphan); attempt++)
        {
            using var trigger = await later.VerifyAsync(fixture.Root, CancellationToken.None);
        }

        Assert.False(Directory.Exists(orphan));
        foreach (var foreign in Directory.EnumerateDirectories(snapshotParent))
            Directory.Delete(foreign, recursive: true);
        Directory.Delete(Path.GetDirectoryName(snapshotParent)!, recursive: true);
    }

    [Fact]
    public async Task ProtectedAndOverLimitCandidatesRemainUntouchedWithoutBlockingValidVerification()
    {
        using var fixture = HelperFixture.Create();
        var snapshotParent = Path.Combine(Path.GetTempPath(),
            $"managed-helper-candidate-isolation-{Guid.NewGuid():N}", "snapshots");
        var created = new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero);
        var first = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes, timeProvider: new FixedTimeProvider(created),
            orphanMinimumAge: TimeSpan.Zero, snapshotParent: snapshotParent);
        var crashed = await first.VerifyAsync(fixture.Root, CancellationToken.None);
        var overLimit = Directory.GetParent(Path.GetDirectoryName(crashed.ExecutablePath)!)!.FullName;
        crashed.AbandonForProcessCrashSimulation();
        MakeWritableTree(overLimit);
        for (var index = 0; index < 16; index++)
            File.WriteAllText(Path.Combine(overLimit, "payload", $"extra-{index:D2}.txt"), "x");

        var protectedCandidate = Path.Combine(snapshotParent, $"snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(protectedCandidate);
        File.WriteAllText(Path.Combine(protectedCandidate, "foreign.txt"), "foreign");
        DenyCurrentUserAccess(protectedCandidate);

        var limited = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes,
            new ManagedBrowserHelperIntegrityLimits(10, 8, 1024 * 1024),
            new FixedTimeProvider(created.AddHours(1)), TimeSpan.Zero, snapshotParent);
        using var verified = await limited.VerifyAsync(fixture.Root, CancellationToken.None);

        Assert.True(Directory.Exists(overLimit));
        Assert.True(Directory.Exists(protectedCandidate));
        verified.Dispose();
        MakeWritableTree(overLimit);
        Directory.Delete(overLimit, recursive: true);
        MakeWritable(protectedCandidate, isDirectory: true);
        Directory.Delete(protectedCandidate, recursive: true);
        Directory.Delete(Path.GetDirectoryName(snapshotParent)!, recursive: true);
    }

    [Fact]
    public async Task ExactSourceEntryLimitStillAllowsOwnedContainerRecovery()
    {
        using var fixture = HelperFixture.Create();
        var limits = new ManagedBrowserHelperIntegrityLimits(7, 1, 1024 * 1024);
        var snapshotParent = Path.Combine(Path.GetTempPath(),
            $"managed-helper-container-exact-{Guid.NewGuid():N}", "snapshots");
        var created = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
        var first = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes, limits, new FixedTimeProvider(created), TimeSpan.Zero, snapshotParent);
        var crashed = await first.VerifyAsync(fixture.Root, CancellationToken.None);
        var orphan = Directory.GetParent(Path.GetDirectoryName(crashed.ExecutablePath)!)!.FullName;
        crashed.AbandonForProcessCrashSimulation();

        using var recovered = await new ManagedBrowserHelperIntegrityVerifier(
                fixture.ManifestBytes, limits, new FixedTimeProvider(created.AddHours(1)),
                TimeSpan.Zero, snapshotParent)
            .VerifyAsync(fixture.Root, CancellationToken.None);

        Assert.False(Directory.Exists(orphan));
        recovered.Dispose();
        Directory.Delete(Path.GetDirectoryName(snapshotParent)!, recursive: true);
    }

    [Fact]
    public async Task ContainerOneEntryBeyondCheckedRecoveryLimitIsLeftUntouched()
    {
        using var fixture = HelperFixture.Create();
        var limits = new ManagedBrowserHelperIntegrityLimits(7, 1, 1024 * 1024);
        var snapshotParent = Path.Combine(Path.GetTempPath(),
            $"managed-helper-container-over-{Guid.NewGuid():N}", "snapshots");
        var created = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
        var first = new ManagedBrowserHelperIntegrityVerifier(
            fixture.ManifestBytes, limits, new FixedTimeProvider(created), TimeSpan.Zero, snapshotParent);
        var crashed = await first.VerifyAsync(fixture.Root, CancellationToken.None);
        var overLimit = Directory.GetParent(Path.GetDirectoryName(crashed.ExecutablePath)!)!.FullName;
        crashed.AbandonForProcessCrashSimulation();
        MakeWritableTree(overLimit);
        for (var index = 0; index < 5; index++)
            File.WriteAllText(Path.Combine(overLimit, $"over-{index}.txt"), "x");

        using var verified = await new ManagedBrowserHelperIntegrityVerifier(
                fixture.ManifestBytes, limits, new FixedTimeProvider(created.AddHours(1)),
                TimeSpan.Zero, snapshotParent)
            .VerifyAsync(fixture.Root, CancellationToken.None);

        Assert.True(Directory.Exists(overLimit));
        verified.Dispose();
        MakeWritableTree(overLimit);
        Directory.Delete(overLimit, recursive: true);
        Directory.Delete(Path.GetDirectoryName(snapshotParent)!, recursive: true);
    }

    [Fact]
    public void NtStatusErrorsAreClassifiedAsWin32SharingAndAccessDenied()
    {
        Assert.Equal(32, PinnedHelperEntry.MapNtStatusToWin32Error(
            unchecked((int)0xC0000043)));
        Assert.Equal(5, PinnedHelperEntry.MapNtStatusToWin32Error(
            unchecked((int)0xC0000022)));
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

    [SelectedManagedHelperPackageFact(HelperIntegrationDirectoryVariable)]
    public void PackagedRuntimeDisablesSiteAndCarriesCompleteLockedLicenseCorpus()
    {
        var root = RequiredIntegrationRoot();
        var pth = File.ReadAllText(Path.Combine(root, "python314._pth"));
        var notices = File.ReadAllText(Path.Combine(root, "THIRD_PARTY_NOTICES.txt"));

        Assert.DoesNotContain("import site", pth, StringComparison.Ordinal);
        Assert.Contains("===== Locked MPL-2.0 complete license text =====", notices,
            StringComparison.Ordinal);
        Assert.Contains("2.1. Grants", notices, StringComparison.Ordinal);
        Assert.Contains("===== lxml-6.1.2.dist-info/licenses/LICENSES.txt =====", notices,
            StringComparison.Ordinal);
        Assert.Contains("**zlib**: zlib-license", notices, StringComparison.Ordinal);
        Assert.Contains("**iconv**: LGPL 2.1", notices, StringComparison.Ordinal);
        Assert.Contains("**libxml2**: MIT", notices, StringComparison.Ordinal);
        Assert.Contains("**libxslt**: MIT", notices, StringComparison.Ordinal);
        Assert.Contains("**libexslt**: MIT", notices, StringComparison.Ordinal);
        var mplMarker = "===== Locked MPL-2.0 complete license text =====\n";
        var mplStart = notices.IndexOf(mplMarker, StringComparison.Ordinal) + mplMarker.Length;
        var mplEnd = notices.IndexOf("\n===== ", mplStart, StringComparison.Ordinal);
        Assert.True(mplStart >= mplMarker.Length && mplEnd > mplStart);
        var mplBytes = Encoding.UTF8.GetBytes(notices[mplStart..mplEnd]);
        Assert.Equal(
            "3f3d9e0024b1921b067d6f7f88deb4a60cbe7a78e76c64e3f1d7fc3b779b9d04",
            Convert.ToHexString(SHA256.HashData(mplBytes)).ToLowerInvariant());

        foreach (var inventoryMarker in new[]
                 {
                     "certifi-2026.7.22.dist-info/licenses/LICENSE",
                     "charset_normalizer-3.5.1.dist-info/licenses/LICENSE",
                     "click-8.5.0.dist-info/licenses/LICENSE.txt",
                     "cssselect-1.5.0.dist-info/licenses/LICENSE",
                     "drissionget-1.2.1.dist-info/licenses/LICENSE",
                     "drissionpage-4.1.1.4.dist-info/licenses/LICENSE",
                     "drissionrecord-2.0.1.dist-info/licenses/LICENSE",
                     "et_xmlfile-2.0.0.dist-info/LICENCE.python",
                     "filelock-3.32.4.dist-info/licenses/LICENSE",
                     "idna-3.19.dist-info/licenses/LICENSE.md",
                     "lxml-6.1.2.dist-info/licenses/LICENSES.txt",
                     "openpyxl-3.1.5.dist-info/LICENCE.rst",
                     "psutil-7.2.2.dist-info/LICENSE",
                     "requests_file-3.0.1.dist-info/licenses/LICENSE",
                     "requests-2.34.2.dist-info/licenses/NOTICE",
                     "tldextract-5.3.2.dist-info/licenses/LICENSE",
                     "urllib3-2.7.0.dist-info/licenses/LICENSE.txt",
                     "websocket_client-1.9.0.dist-info/licenses/LICENSE",
                 })
            Assert.Contains($"===== {inventoryMarker} =====", notices, StringComparison.Ordinal);
    }

    [SelectedManagedHelperPackageFact(HelperIntegrationDirectoryVariable)]
    public async Task PackagedRuntimeImportCreatesNoFilesAndSnapshotCanBeVerifiedAgain()
    {
        var verifier = new ManagedBrowserHelperIntegrityVerifier();
        using var verified = await verifier.VerifyAsync(
            RequiredIntegrationRoot(), CancellationToken.None);
        var snapshotRoot = Path.GetDirectoryName(verified.ExecutablePath)!;
        var before = EnumerateRelativeEntries(snapshotRoot);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = verified.ExecutablePath,
            WorkingDirectory = snapshotRoot,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            ArgumentList =
            {
                "-I",
                "-c",
                "scope={'__name__':'managed_helper_import_test'}; " +
                "exec(compile(open('main.py','rb').read(),'main.py','exec'), scope); " +
                "import DrissionPage",
            },
        }) ?? throw new InvalidOperationException("The packaged helper runtime did not start.");
        await process.WaitForExitAsync();
        var error = await process.StandardError.ReadToEndAsync();

        Assert.True(process.ExitCode == 0, error);
        Assert.Equal(string.Empty, error);
        Assert.Equal(before, EnumerateRelativeEntries(snapshotRoot));
        using var verifiedAgain = await verifier.VerifyAsync(snapshotRoot, CancellationToken.None);
        Assert.False(verifiedAgain.IsDisposed);
    }

    private static string RequiredIntegrationRoot() =>
        Environment.GetEnvironmentVariable(HelperIntegrationDirectoryVariable) is { Length: > 0 } root
            ? root
            : throw new InvalidOperationException("Required managed helper package directory is missing.");

    private static string[] EnumerateRelativeEntries(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static void CreateDirectoryJunction(string junction, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            Arguments = $"/d /c mklink /J \"{junction}\" \"{target}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("The junction helper did not start.");
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
    }

    private static void MakeWritableTree(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            MakeWritable(file, isDirectory: false);
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
            MakeWritable(directory, isDirectory: true);
        MakeWritable(root, isDirectory: true);
    }

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Write | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static byte[] ReadAllBytesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Write | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string AclSddl(string path, bool isDirectory)
    {
        FileSystemSecurity security = isDirectory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        return security.GetSecurityDescriptorSddlForm(
            AccessControlSections.Access | AccessControlSections.Owner);
    }

    private static void MakeWritable(string path, bool isDirectory)
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException();
        FileSystemSecurity security = isDirectory ? new DirectorySecurity() : new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            user, FileSystemRights.FullControl,
            isDirectory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None,
            PropagationFlags.None, AccessControlType.Allow));
        if (isDirectory)
            new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        else
            new FileInfo(path).SetAccessControl((FileSecurity)security);
    }

    private static void DenyCurrentUserAccess(string path)
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Deny));
        new DirectoryInfo(path).SetAccessControl(security);
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
            string? buildIdentityOverride = null,
            IReadOnlyDictionary<string, byte[]>? additionalContents = null)
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
            if (additionalContents is not null)
                foreach (var pair in additionalContents)
                    contents.Add(pair.Key, pair.Value);
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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
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
