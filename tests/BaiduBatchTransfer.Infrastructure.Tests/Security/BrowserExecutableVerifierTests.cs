using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Security;
using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection.PortableExecutable;

namespace BaiduBatchTransfer.Infrastructure.Tests.Security;

public sealed class BrowserExecutableVerifierTests
{
    private const string DirectoryPath = @"C:\Selected\Application";
    private const string ChromePath = @"C:\Selected\Application\chrome.exe";

    [Fact]
    public async Task LaunchLeaseReopensExecutableIdentityForEachProcessProof()
    {
        var dependencies = ValidDependencies();
        var verifier = dependencies.CreateVerifier();
        var identity = (await verifier.VerifyAsync(DirectoryPath, BrowserFamily.Chrome, CancellationToken.None)).Identity!;
        using var lease = await verifier.PinForLaunchAsync(identity, CancellationToken.None);
        var opens = dependencies.Native.OpenedExecutableComponents.Count;
        Assert.True(lease.MatchesExecutablePath(ChromePath));
        Assert.Equal(opens + 1, dependencies.Native.OpenedExecutableComponents.Count);
    }

    [Fact]
    public async Task LaunchLeaseRetainsVerifiedDirectoryAndExecutablePinsUntilDisposed()
    {
        var dependencies = ValidDependencies();
        var verifier = dependencies.CreateVerifier();
        var identity = (await verifier.VerifyAsync(DirectoryPath, BrowserFamily.Chrome, CancellationToken.None)).Identity!;
        dependencies.Native.Handles.Clear();
        using (var lease = await verifier.PinForLaunchAsync(identity, CancellationToken.None))
        {
            Assert.Same(identity, lease.Identity);
            Assert.True(lease.Validate());
            var retained = dependencies.Native.Handles.ToArray();
            Assert.True(lease.MatchesExecutablePath(ChromePath));
            Assert.False(lease.MatchesExecutablePath(@"C:\Outside\chrome.exe"));
            Assert.All(retained, handle => Assert.False(handle.IsDisposed));
        }
        Assert.All(dependencies.Native.Handles, handle => Assert.True(handle.IsDisposed));
    }

    [Fact]
    public async Task LaunchLeaseRejectsReplacedExecutableAndDisposesPins()
    {
        var dependencies = ValidDependencies();
        var verifier = dependencies.CreateVerifier();
        var identity = (await verifier.VerifyAsync(DirectoryPath, BrowserFamily.Chrome, CancellationToken.None)).Identity!;
        dependencies.Native.FileInfo = new(@"C:\Outside\chrome.exe", false);
        await Assert.ThrowsAsync<IOException>(() => verifier.PinForLaunchAsync(identity, CancellationToken.None));
        Assert.All(dependencies.Native.Handles, handle => Assert.True(handle.IsDisposed));
    }

    [Theory]
    [InlineData(BrowserFamily.Chrome, "chrome.exe", "Google LLC", "Google Chrome")]
    [InlineData(BrowserFamily.Edge, "msedge.exe", "Microsoft Corporation", "Microsoft Edge")]
    [InlineData(BrowserFamily.Chrome, "CHROME.EXE", "Google LLC", "Google Chrome")]
    public void PolicyAcceptsOnlyDocumentedFamilyIdentity(
        BrowserFamily family,
        string fileName,
        string publisher,
        string productName)
    {
        Assert.Equal(
            BrowserExecutableVerificationErrorCode.None,
            BrowserExecutablePolicy.Evaluate(family, fileName, publisher, productName));
    }

    [Theory]
    [InlineData(BrowserFamily.Chrome, "msedge.exe", "Google LLC", "Google Chrome", BrowserExecutableVerificationErrorCode.FamilyMismatch)]
    [InlineData(BrowserFamily.Edge, "chrome.exe", "Microsoft Corporation", "Microsoft Edge", BrowserExecutableVerificationErrorCode.FamilyMismatch)]
    [InlineData(BrowserFamily.Chrome, "chrome.exe", "Microsoft Corporation", "Google Chrome", BrowserExecutableVerificationErrorCode.PublisherMismatch)]
    [InlineData(BrowserFamily.Chrome, "chrome.exe", "google llc", "Google Chrome", BrowserExecutableVerificationErrorCode.PublisherMismatch)]
    [InlineData(BrowserFamily.Edge, "msedge.exe", "Microsoft Corporation", "Microsoft edge", BrowserExecutableVerificationErrorCode.ProductMismatch)]
    [InlineData(BrowserFamily.Chrome, "chrome.exe", "Google LLC", "Microsoft Edge", BrowserExecutableVerificationErrorCode.ProductMismatch)]
    public void PolicyRejectsEveryOtherCombination(
        BrowserFamily family,
        string fileName,
        string publisher,
        string productName,
        BrowserExecutableVerificationErrorCode expected)
    {
        Assert.Equal(expected, BrowserExecutablePolicy.Evaluate(family, fileName, publisher, productName));
    }

    [Theory]
    [InlineData(BrowserSignatureTrust.Unsigned, BrowserExecutableVerificationErrorCode.SignatureMissing)]
    [InlineData(BrowserSignatureTrust.Invalid, BrowserExecutableVerificationErrorCode.SignatureUntrusted)]
    [InlineData(BrowserSignatureTrust.Untrusted, BrowserExecutableVerificationErrorCode.SignatureUntrusted)]
    public async Task RejectsUnsignedInvalidOrUntrustedSignature(
        BrowserSignatureTrust trust,
        BrowserExecutableVerificationErrorCode expected)
    {
        var dependencies = ValidDependencies();
        dependencies.Signature.Result = new BrowserSignatureVerification(trust, null);

        var result = await dependencies.CreateVerifier().VerifyAsync(
            DirectoryPath, BrowserFamily.Chrome, CancellationToken.None);

        Assert.Equal(expected, result.ErrorCode);
        Assert.False(result.IsVerified);
        Assert.Null(result.Identity);
    }

    [Fact]
    public async Task RejectsSelectedDirectoryReparsePointBeforeOpeningExecutable()
    {
        var dependencies = ValidDependencies();
        dependencies.Native.DirectoryInfo = dependencies.Native.DirectoryInfo with { IsReparsePoint = true };

        var result = await dependencies.CreateVerifier().VerifyAsync(
            DirectoryPath, BrowserFamily.Chrome, CancellationToken.None);

        Assert.Equal(BrowserExecutableVerificationErrorCode.DirectoryReparsePoint, result.ErrorCode);
        Assert.Equal(new[] { @"C:\" }, dependencies.Native.OpenedRoots);
        Assert.Empty(dependencies.Native.OpenedDirectoryComponents);
    }

    [Fact]
    public async Task RejectsSelectionResolvedThroughAncestorJunctionOrSymlink()
    {
        var dependencies = ValidDependencies();
        dependencies.Native.DirectoryInfo = dependencies.Native.DirectoryInfo with
        {
            FinalPath = @"C:\Real\Application",
        };

        var result = await dependencies.CreateVerifier().VerifyAsync(
            DirectoryPath, BrowserFamily.Chrome, CancellationToken.None);

        Assert.Equal(BrowserExecutableVerificationErrorCode.DirectoryReparsePoint, result.ErrorCode);
        Assert.Equal(new[] { @"C:\" }, dependencies.Native.OpenedRoots);
        Assert.Empty(dependencies.Native.OpenedDirectoryComponents);
    }

    [Fact]
    public async Task RejectsExecutableReparsePoint()
    {
        var dependencies = ValidDependencies();
        dependencies.Native.FileInfo = dependencies.Native.FileInfo with { IsReparsePoint = true };

        var result = await dependencies.CreateVerifier().VerifyAsync(
            DirectoryPath, BrowserFamily.Chrome, CancellationToken.None);

        Assert.Equal(BrowserExecutableVerificationErrorCode.ExecutableReparsePoint, result.ErrorCode);
    }

    [Theory]
    [InlineData(true, BrowserExecutableVerificationErrorCode.DirectoryUnavailable)]
    [InlineData(false, BrowserExecutableVerificationErrorCode.ExecutableUnavailable)]
    public async Task OpenFailuresReturnTypedSafeError(
        bool directoryFailure,
        BrowserExecutableVerificationErrorCode expected)
    {
        var dependencies = ValidDependencies();
        if (directoryFailure)
            dependencies.Native.DirectoryOpenFailure = new IOException($"secret {DirectoryPath}");
        else
            dependencies.Native.ExecutableOpenFailure = new IOException($"secret {ChromePath}");

        var result = await dependencies.CreateVerifier().VerifyAsync(
            DirectoryPath, BrowserFamily.Chrome, CancellationToken.None);

        Assert.Equal(expected, result.ErrorCode);
        Assert.DoesNotContain(DirectoryPath, result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsFinalParentOutsideSelectedDirectory()
    {
        var dependencies = ValidDependencies();
        dependencies.Native.FileInfo = dependencies.Native.FileInfo with
        {
            FinalPath = @"C:\Outside\chrome.exe",
        };

        var result = await dependencies.CreateVerifier().VerifyAsync(
            DirectoryPath, BrowserFamily.Chrome, CancellationToken.None);

        Assert.Equal(BrowserExecutableVerificationErrorCode.FinalParentMismatch, result.ErrorCode);
    }

    [Theory]
    [InlineData(@"C:\Selected\Application\..\Application")]
    [InlineData(@"C:\Selected\Application\")]
    [InlineData(@"Selected\Application")]
    public async Task RejectsNonCanonicalOrTraversalSelectionWithoutOpeningAnything(string selectedDirectory)
    {
        var dependencies = ValidDependencies();

        var result = await dependencies.CreateVerifier().VerifyAsync(
            selectedDirectory, BrowserFamily.Chrome, CancellationToken.None);

        Assert.Equal(BrowserExecutableVerificationErrorCode.InvalidSelection, result.ErrorCode);
        Assert.Empty(dependencies.Native.OpenedRoots);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"\\server\share\Browser")]
    [InlineData(@"\\?\C:\Browser")]
    public async Task ExplicitlyRejectsUnsupportedDriveRootOrUnpinnedNamespace(string selectedDirectory)
    {
        var dependencies = ValidDependencies();

        var result = await dependencies.CreateVerifier().VerifyAsync(
            selectedDirectory, BrowserFamily.Chrome, CancellationToken.None);

        Assert.Equal(BrowserExecutableVerificationErrorCode.UnsupportedPathNamespace, result.ErrorCode);
        Assert.Empty(dependencies.Native.OpenedRoots);
    }

    [Theory]
    [InlineData("Selected")]
    [InlineData("Application")]
    public async Task RejectsReparsePointAtAnyAncestorComponent(string component)
    {
        var dependencies = ValidDependencies();
        dependencies.Native.ReparseDirectoryComponent = component;

        var result = await dependencies.CreateVerifier().VerifyAsync(
            DirectoryPath, BrowserFamily.Chrome, CancellationToken.None);

        Assert.Equal(BrowserExecutableVerificationErrorCode.DirectoryReparsePoint, result.ErrorCode);
        Assert.Empty(dependencies.Native.OpenedExecutableComponents);
    }

    [Fact]
    public async Task OpensOnlySelectedDirectoryAndItsExactDirectChild()
    {
        var dependencies = ValidDependencies();

        var result = await dependencies.CreateVerifier().VerifyAsync(
            DirectoryPath, BrowserFamily.Chrome, CancellationToken.None);

        Assert.True(result.IsVerified);
        Assert.Equal(new[] { @"C:\" }, dependencies.Native.OpenedRoots);
        Assert.Equal(new[] { "Selected", "Application" }, dependencies.Native.OpenedDirectoryComponents);
        Assert.Equal(new[] { "chrome.exe" }, dependencies.Native.OpenedExecutableComponents);
    }

    [Fact]
    public async Task PinsEveryPathComponentAndOpensExecutableOnlyRelativeToSelectedDirectoryHandle()
    {
        var dependencies = ValidDependencies();

        var result = await dependencies.CreateVerifier().VerifyAsync(
            DirectoryPath, BrowserFamily.Chrome, CancellationToken.None);

        Assert.True(result.IsVerified);
        Assert.Equal(new[] { @"C:\" }, dependencies.Native.OpenedRoots);
        Assert.Equal(new[] { "Selected", "Application" }, dependencies.Native.OpenedDirectoryComponents);
        Assert.Equal(new[] { "chrome.exe" }, dependencies.Native.OpenedExecutableComponents);
        Assert.Equal(4, dependencies.Native.Handles.Count);
    }

    [Fact]
    public async Task NamespaceSwitchThenRestoreCannotMixPathMetadataWithPinnedBytes()
    {
        var dependencies = ValidDependencies();
        var namespacePayload = "original";
        dependencies.Signature.OnRead = () => namespacePayload = "attacker";
        dependencies.Snapshot.ProductName = "Original Product";
        dependencies.Product.Read = snapshot =>
        {
            Assert.Equal("attacker", namespacePayload);
            namespacePayload = "original";
            return Assert.IsType<RecordingSnapshot>(snapshot).ProductName;
        };

        var result = await dependencies.CreateVerifier().VerifyAsync(
            DirectoryPath, BrowserFamily.Chrome, CancellationToken.None);

        Assert.Equal(BrowserExecutableVerificationErrorCode.ProductMismatch, result.ErrorCode);
        Assert.Null(result.Identity);
    }

    [Fact]
    public async Task SignatureAndProductReadTheSameLockedSnapshotAndItIsDisposedAfterward()
    {
        var dependencies = ValidDependencies();
        dependencies.Signature.OnReadSnapshot = snapshot =>
            Assert.Same(dependencies.Snapshot.LastSnapshot, snapshot);
        dependencies.Product.Read = snapshot =>
        {
            Assert.Same(dependencies.Snapshot.LastSnapshot, snapshot);
            Assert.False(Assert.IsType<RecordingSnapshot>(snapshot).IsDisposed);
            return "Google Chrome";
        };

        var result = await dependencies.CreateVerifier().VerifyAsync(
            DirectoryPath, BrowserFamily.Chrome, CancellationToken.None);

        Assert.True(result.IsVerified);
        Assert.True(dependencies.Snapshot.LastSnapshot!.IsDisposed);
    }

    [Fact]
    public async Task SnapshotCreationFailureReturnsTypedSafeErrorAndDisposesPinnedHandles()
    {
        var dependencies = ValidDependencies();
        dependencies.Snapshot.Failure = new IOException(@"secret C:\SafeTemp\snapshot");

        var result = await dependencies.CreateVerifier().VerifyAsync(
            DirectoryPath, BrowserFamily.Chrome, CancellationToken.None);

        Assert.Equal(BrowserExecutableVerificationErrorCode.SnapshotUnavailable, result.ErrorCode);
        Assert.All(dependencies.Native.Handles, handle => Assert.True(handle.IsDisposed));
        Assert.DoesNotContain("SafeTemp", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task PinnedExecutableIdentityOrHashChangeAtAnyMetadataBoundaryFailsClosed(
        int successfulValidationsBeforeChange)
    {
        var dependencies = ValidDependencies();
        for (var index = 0; index < successfulValidationsBeforeChange; index++)
            dependencies.Snapshot.ValidationResults.Enqueue(true);
        dependencies.Snapshot.ValidationResults.Enqueue(false);

        var result = await dependencies.CreateVerifier().VerifyAsync(
            DirectoryPath, BrowserFamily.Chrome, CancellationToken.None);

        Assert.Equal(BrowserExecutableVerificationErrorCode.IdentityChanged, result.ErrorCode);
        Assert.True(dependencies.Snapshot.LastSnapshot!.IsDisposed);
    }

    [Fact]
    public async Task CancellationAfterSnapshotCreationDisposesSnapshotAndAllPinnedHandles()
    {
        var dependencies = ValidDependencies();
        using var cancellation = new CancellationTokenSource();
        dependencies.Signature.OnRead = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dependencies.CreateVerifier().VerifyAsync(
                DirectoryPath, BrowserFamily.Chrome, cancellation.Token));

        Assert.True(dependencies.Snapshot.LastSnapshot!.IsDisposed);
        Assert.All(dependencies.Native.Handles, handle => Assert.True(handle.IsDisposed));
    }

    [Fact]
    public async Task HoldsBothHandlesUntilAllVerificationFinishes()
    {
        var dependencies = ValidDependencies();
        dependencies.Signature.OnRead = () => Assert.All(dependencies.Native.Handles, handle => Assert.False(handle.IsDisposed));
        dependencies.Product.OnRead = () => Assert.All(dependencies.Native.Handles, handle => Assert.False(handle.IsDisposed));
        dependencies.Snapshot.OnCreate = () => Assert.All(dependencies.Native.Handles, handle => Assert.False(handle.IsDisposed));

        var result = await dependencies.CreateVerifier().VerifyAsync(
            DirectoryPath, BrowserFamily.Chrome, CancellationToken.None);

        Assert.True(result.IsVerified);
        Assert.All(dependencies.Native.Handles, handle => Assert.True(handle.IsDisposed));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task RejectsFileReplacementBetweenChecks(int changedRead)
    {
        var dependencies = ValidDependencies();
        dependencies.Identity.ChangeAtRead = changedRead;

        var result = await dependencies.CreateVerifier().VerifyAsync(
            DirectoryPath, BrowserFamily.Chrome, CancellationToken.None);

        Assert.Equal(BrowserExecutableVerificationErrorCode.IdentityChanged, result.ErrorCode);
        Assert.Null(result.Identity);
    }

    [Fact]
    public async Task RejectsLaterFinalPathChange()
    {
        var dependencies = ValidDependencies();
        dependencies.Native.ChangeFileFinalPathAtRead = 2;

        var result = await dependencies.CreateVerifier().VerifyAsync(
            DirectoryPath, BrowserFamily.Chrome, CancellationToken.None);

        Assert.Equal(BrowserExecutableVerificationErrorCode.IdentityChanged, result.ErrorCode);
    }

    [Fact]
    public async Task RejectsDirectoryReplacementBetweenChecks()
    {
        var dependencies = ValidDependencies();
        dependencies.Native.ChangeDirectoryObjectAtRead = 2;

        var result = await dependencies.CreateVerifier().VerifyAsync(
            DirectoryPath, BrowserFamily.Chrome, CancellationToken.None);

        Assert.Equal(BrowserExecutableVerificationErrorCode.IdentityChanged, result.ErrorCode);
    }

    [Fact]
    public async Task RejectsFilesAboveBoundWithoutHashingOrAllocatingWholeFile()
    {
        var dependencies = ValidDependencies();
        dependencies.Identity.Identity = dependencies.Identity.Identity with
        {
            Length = BrowserExecutableVerifier.MaximumSupportedExecutableBytes + 1,
        };

        var result = await dependencies.CreateVerifier().VerifyAsync(
            DirectoryPath, BrowserFamily.Chrome, CancellationToken.None);

        Assert.Equal(BrowserExecutableVerificationErrorCode.FileTooLarge, result.ErrorCode);
        Assert.Equal(0, dependencies.Snapshot.CreateCount);
    }

    [Fact]
    public async Task ProducesImmutableRedactedIdentityOnlyAfterAllChecksPass()
    {
        var dependencies = ValidDependencies();

        var result = await dependencies.CreateVerifier().VerifyAsync(
            DirectoryPath, BrowserFamily.Chrome, CancellationToken.None);

        var identity = Assert.IsType<BrowserInstallationIdentity>(result.Identity);
        Assert.Equal(BrowserFamily.Chrome, identity.BrowserFamily);
        Assert.Equal("Google LLC", identity.Publisher);
        Assert.Equal("Google Chrome", identity.ProductName);
        Assert.DoesNotContain(DirectoryPath, result.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("volume-a", result.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(dependencies.Snapshot.Hash, result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LaunchTimeReverificationFailsClosedWhenIdentityOrHashDiffers()
    {
        var dependencies = ValidDependencies();
        var verifier = dependencies.CreateVerifier();
        var initial = await verifier.VerifyAsync(DirectoryPath, BrowserFamily.Chrome, CancellationToken.None);
        dependencies.Snapshot.Hash = new string('b', 64);

        var launch = await verifier.ReverifyForLaunchAsync(initial.Identity!, CancellationToken.None);

        Assert.Equal(BrowserExecutableVerificationErrorCode.LaunchIdentityMismatch, launch.ErrorCode);
        Assert.Null(launch.Identity);
    }

    [Fact]
    public async Task LaunchTimeReverificationReturnsSavedImmutableIdentityWhenEverythingMatches()
    {
        var dependencies = ValidDependencies();
        var verifier = dependencies.CreateVerifier();
        var initial = await verifier.VerifyAsync(DirectoryPath, BrowserFamily.Chrome, CancellationToken.None);

        var launch = await verifier.ReverifyForLaunchAsync(initial.Identity!, CancellationToken.None);

        Assert.True(launch.IsVerified);
        Assert.Same(initial.Identity, launch.Identity);
    }

    [Fact]
    public async Task CancellationPropagatesWithoutProducingIdentity()
    {
        var dependencies = ValidDependencies();
        using var cancellation = new CancellationTokenSource();
        dependencies.Snapshot.OnCreate = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dependencies.CreateVerifier().VerifyAsync(
                DirectoryPath, BrowserFamily.Chrome, cancellation.Token));
    }

    [Fact]
    public async Task UnexpectedFailuresReturnTypedSafeError()
    {
        var dependencies = ValidDependencies();
        dependencies.Signature.Failure = new IOException($"secret {ChromePath}");

        var result = await dependencies.CreateVerifier().VerifyAsync(
            DirectoryPath, BrowserFamily.Chrome, CancellationToken.None);

        Assert.Equal(BrowserExecutableVerificationErrorCode.VerificationUnavailable, result.ErrorCode);
        Assert.DoesNotContain(ChromePath, result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [SelectedBrowserFact(ChromeIntegrationDirectoryVariable)]
    public Task ExplicitlySelectedChromeCompletesInitialAndLaunchVerification() =>
        VerifyExplicitBrowserAsync(
            ChromeIntegrationDirectoryVariable,
            BrowserFamily.Chrome,
            expectedPublisher: "Google LLC",
            expectedProduct: "Google Chrome");

    [SelectedBrowserFact(EdgeIntegrationDirectoryVariable)]
    public Task ExplicitlySelectedEdgeCompletesInitialAndLaunchVerification() =>
        VerifyExplicitBrowserAsync(
            EdgeIntegrationDirectoryVariable,
            BrowserFamily.Edge,
            expectedPublisher: "Microsoft Corporation",
            expectedProduct: "Microsoft Edge");

    [Theory]
    [InlineData(ChromeIntegrationDirectoryVariable)]
    [InlineData(EdgeIntegrationDirectoryVariable)]
    public void RequiredBrowserIntegrationDoesNotSkipAndFailsClearlyWhenDirectoryIsMissing(
        string directoryVariable)
    {
        var originalRequired = Environment.GetEnvironmentVariable(
            "BAIDU_BATCH_REQUIRE_BROWSER_INTEGRATION");
        var originalDirectory = Environment.GetEnvironmentVariable(directoryVariable);
        try
        {
            Environment.SetEnvironmentVariable("BAIDU_BATCH_REQUIRE_BROWSER_INTEGRATION", "1");
            Environment.SetEnvironmentVariable(directoryVariable, null);

            Assert.Null(new SelectedBrowserFactAttribute(directoryVariable).Skip);
            var failure = Assert.Throws<InvalidOperationException>(() =>
                RequireExplicitDirectory(directoryVariable, BrowserFamily.Chrome));
            Assert.Equal("Required explicit browser integration directory is missing.", failure.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "BAIDU_BATCH_REQUIRE_BROWSER_INTEGRATION", originalRequired);
            Environment.SetEnvironmentVariable(directoryVariable, originalDirectory);
        }
    }

    private const string ChromeIntegrationDirectoryVariable =
        "BAIDU_BATCH_TRANSFER_TEST_CHROME_DIRECTORY";
    private const string EdgeIntegrationDirectoryVariable =
        "BAIDU_BATCH_TRANSFER_TEST_EDGE_DIRECTORY";

    private static async Task VerifyExplicitBrowserAsync(
        string directoryVariable,
        BrowserFamily family,
        string expectedPublisher,
        string expectedProduct)
    {
        var selectedDirectory = RequireExplicitDirectory(directoryVariable, family);

        var verifier = new BrowserExecutableVerifier();
        var initial = await verifier.VerifyAsync(
            selectedDirectory, family, CancellationToken.None);
        Assert.True(
            initial.IsVerified,
            $"{family} initial verification failed with {initial.ErrorCode}.");
        var identity = Assert.IsType<BrowserInstallationIdentity>(initial.Identity);
        Assert.Equal(family, identity.BrowserFamily);
        Assert.Equal(expectedPublisher, identity.Publisher);
        Assert.Equal(expectedProduct, identity.ProductName);
        Assert.True(HasSha256Suffix(identity.FileIdentity), "Verified identity has no SHA-256 suffix.");

        var launch = await verifier.ReverifyForLaunchAsync(identity, CancellationToken.None);
        Assert.True(
            launch.IsVerified,
            $"{family} launch verification failed with {launch.ErrorCode}.");
        Assert.Same(identity, launch.Identity);
    }

    private static bool HasSha256Suffix(string identity)
    {
        var separator = identity.LastIndexOf(':');
        return separator >= 0 && identity[(separator + 1)..] is { Length: 64 } hash &&
               hash.All(Uri.IsHexDigit);
    }

    private static string RequireExplicitDirectory(string directoryVariable, BrowserFamily family)
    {
        var selectedDirectory = Environment.GetEnvironmentVariable(directoryVariable);
        if (string.IsNullOrWhiteSpace(selectedDirectory))
            throw new InvalidOperationException(
                "Required explicit browser integration directory is missing.");
        if (family is not (BrowserFamily.Chrome or BrowserFamily.Edge))
            throw new ArgumentOutOfRangeException(nameof(family));
        return selectedDirectory;
    }

    [Fact]
    public async Task WindowsPinnedExecutableSupportsDirectHandleMetadataWithoutTemporaryArtifact()
    {
        var processPath = Environment.ProcessPath;
        Assert.False(string.IsNullOrWhiteSpace(processPath));
        using var source = new OwnedPinnedHandle(File.OpenHandle(
            processPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.None));
        var expectedProduct = FileVersionInfo.GetVersionInfo(processPath).ProductName;
        var expectedLength = new FileInfo(processPath).Length;
        var snapshot = await new WindowsBrowserExecutableSnapshotFactory().CreateAsync(
            source, expectedLength, BrowserExecutableVerifier.MaximumSupportedExecutableBytes,
            CancellationToken.None);
        try
        {
            Assert.Equal(source.SafeHandle.DangerousGetHandle(), snapshot.SafeHandle.DangerousGetHandle());
            Assert.Equal(expectedProduct, new WindowsBrowserProductReader().ReadProductName(snapshot));
            var signature = new WindowsAuthenticodeSignatureReader().Verify(snapshot);
            Assert.Contains(signature.Trust, Enum.GetValues<BrowserSignatureTrust>());
            Assert.True(await snapshot.ValidateAsync(CancellationToken.None));
        }
        finally
        {
            snapshot.Dispose();
        }

        Assert.True(File.Exists(processPath));
    }

    [Fact]
    public void ResourceIdentifierWithVersionOnlyInLowWordIsRejected()
    {
        var processPath = Environment.ProcessPath;
        Assert.False(string.IsNullOrWhiteSpace(processPath));
        var executableBytes = File.ReadAllBytes(processPath);
        using (var peReader = new PEReader(new MemoryStream(executableBytes, writable: false)))
        {
            var directory = peReader.PEHeaders.PEHeader!.ResourceTableDirectory;
            var section = Assert.Single(peReader.PEHeaders.SectionHeaders.Where(candidate =>
                directory.RelativeVirtualAddress >= candidate.VirtualAddress &&
                directory.RelativeVirtualAddress < candidate.VirtualAddress + candidate.VirtualSize));
            var directoryOffset = checked(
                directory.RelativeVirtualAddress - section.VirtualAddress + section.PointerToRawData);
            var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(
                                 executableBytes.AsSpan(directoryOffset + 12, 2)) +
                             BinaryPrimitives.ReadUInt16LittleEndian(
                                 executableBytes.AsSpan(directoryOffset + 14, 2));
            var versionEntryOffset = Enumerable.Range(0, entryCount)
                .Select(index => directoryOffset + 16 + (index * 8))
                .Single(offset => BinaryPrimitives.ReadUInt32LittleEndian(
                    executableBytes.AsSpan(offset, 4)) == 16);
            BinaryPrimitives.WriteUInt32LittleEndian(
                executableBytes.AsSpan(versionEntryOffset, 4), 0x00010010);
        }

        using var snapshot = new TemporaryFixtureSnapshot(executableBytes);

        Assert.Throws<InvalidDataException>(() =>
            new WindowsBrowserProductReader().ReadProductName(snapshot));
    }

    private static Dependencies ValidDependencies() => new();

    private sealed class Dependencies
    {
        public RecordingNativeHandleReader Native { get; } = new();
        public RecordingIdentityReader Identity { get; } = new();
        public RecordingSnapshotFactory Snapshot { get; } = new();
        public RecordingSignatureReader Signature { get; } = new();
        public RecordingProductReader Product { get; } = new();

        public BrowserExecutableVerifier CreateVerifier() =>
            new(Native, Identity, Snapshot, Signature, Product,
                () => Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
    }

    private sealed class RecordingHandle(string finalPath, bool isDirectory) : IBrowserPinnedHandle
    {
        public SafeFileHandle SafeHandle { get; } = new(new IntPtr(1), ownsHandle: false);
        public string FinalPath { get; } = finalPath;
        public bool IsDirectory { get; } = isDirectory;
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }

    private sealed class OwnedPinnedHandle(SafeFileHandle safeHandle) : IBrowserPinnedHandle
    {
        public SafeFileHandle SafeHandle { get; } = safeHandle;
        public void Dispose() => SafeHandle.Dispose();
    }

    private sealed class TemporaryFixtureSnapshot : IBrowserExecutableSnapshot
    {
        private readonly string path = Path.Combine(
            Path.GetTempPath(), $"bbt-pe-fixture-{Guid.NewGuid():N}.exe");
        private readonly FileStream stream;

        public TemporaryFixtureSnapshot(byte[] bytes)
        {
            stream = new FileStream(
                path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
                bufferSize: 4096, FileOptions.DeleteOnClose);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            stream.Position = 0;
        }

        public SafeFileHandle SafeHandle => stream.SafeFileHandle;
        public string SnapshotPath => path;
        public long Length => stream.Length;
        public string Sha256 => new('0', 64);
        public Task<bool> ValidateAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public void Dispose() => stream.Dispose();
    }

    private sealed class RecordingNativeHandleReader : IBrowserNativeHandleReader
    {
        private int fileInfoReads;
        public BrowserNativeHandleInfo DirectoryInfo { get; set; } = new(DirectoryPath, false);
        public BrowserNativeHandleInfo FileInfo { get; set; } = new(ChromePath, false);
        public int ChangeFileFinalPathAtRead { get; set; }
        public int ChangeDirectoryObjectAtRead { get; set; }
        public Exception? DirectoryOpenFailure { get; set; }
        public Exception? ExecutableOpenFailure { get; set; }
        public string? ReparseDirectoryComponent { get; set; }
        private int directoryInfoReads;
        public List<string> OpenedRoots { get; } = [];
        public List<string> OpenedDirectoryComponents { get; } = [];
        public List<string> OpenedExecutableComponents { get; } = [];
        public List<RecordingHandle> Handles { get; } = [];

        public IBrowserPinnedHandle OpenRoot(string exactRootPath)
        {
            OpenedRoots.Add(exactRootPath);
            if (DirectoryOpenFailure is not null) throw DirectoryOpenFailure;
            return AddHandle(exactRootPath, isDirectory: true);
        }

        public IBrowserPinnedHandle OpenDirectoryChild(
            IBrowserPinnedHandle parentDirectory,
            string exactComponentName)
        {
            OpenedDirectoryComponents.Add(exactComponentName);
            if (DirectoryOpenFailure is not null) throw DirectoryOpenFailure;
            var parent = Assert.IsType<RecordingHandle>(parentDirectory);
            return AddHandle(Path.Combine(parent.FinalPath, exactComponentName), isDirectory: true);
        }

        public IBrowserPinnedHandle OpenExecutableChild(
            IBrowserPinnedHandle selectedDirectory,
            string exactFileName)
        {
            OpenedExecutableComponents.Add(exactFileName);
            if (ExecutableOpenFailure is not null) throw ExecutableOpenFailure;
            var parent = Assert.IsType<RecordingHandle>(selectedDirectory);
            return AddHandle(Path.Combine(parent.FinalPath, exactFileName), isDirectory: false);
        }

        public BrowserNativeHandleInfo ReadInfo(IBrowserPinnedHandle handle)
        {
            var recording = Assert.IsType<RecordingHandle>(handle);
            if (recording.IsDirectory)
            {
                directoryInfoReads++;
                var finalPath = DirectoryInfo.FinalPath == DirectoryPath
                    ? recording.FinalPath
                    : DirectoryInfo.FinalPath;
                return new BrowserNativeHandleInfo(
                    finalPath,
                    DirectoryInfo.IsReparsePoint ||
                    string.Equals(Path.GetFileName(recording.FinalPath), ReparseDirectoryComponent,
                        StringComparison.OrdinalIgnoreCase),
                    directoryInfoReads == ChangeDirectoryObjectAtRead
                        ? "directory-b"
                        : $"directory:{recording.FinalPath}");
            }
            fileInfoReads++;
            var fileFinalPath = FileInfo.FinalPath == ChromePath
                ? recording.FinalPath
                : FileInfo.FinalPath;
            if (fileInfoReads == ChangeFileFinalPathAtRead)
                fileFinalPath = @"C:\Outside\chrome.exe";
            return new BrowserNativeHandleInfo(
                fileFinalPath, FileInfo.IsReparsePoint, $"file:{recording.FinalPath}");
        }

        private RecordingHandle AddHandle(string finalPath, bool isDirectory)
        {
            var handle = new RecordingHandle(finalPath, isDirectory);
            Handles.Add(handle);
            return handle;
        }
    }

    private sealed class RecordingIdentityReader : IBrowserFileIdentityReader
    {
        private int reads;
        public BrowserFileIdentity Identity { get; set; } = new("volume-a", "file-a", 1024);
        public int ChangeAtRead { get; set; }

        public BrowserFileIdentity Read(IBrowserPinnedHandle handle)
        {
            reads++;
            return reads == ChangeAtRead ? Identity with { FileId = "file-b" } : Identity;
        }

    }

    private sealed class RecordingSnapshot : IBrowserExecutableSnapshot
    {
        public SafeFileHandle SafeHandle { get; } = new(new IntPtr(2), ownsHandle: false);
        public string SnapshotPath { get; } = @"C:\SafeTemp\random.snapshot";
        public long Length { get; init; }
        public string Sha256 { get; init; } = new('a', 64);
        public string ProductName { get; init; } = "Google Chrome";
        public Queue<bool> ValidationResults { get; init; } = [];
        public bool IsDisposed { get; private set; }
        public Task<bool> ValidateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ValidationResults.TryDequeue(out var result) ? result : true);
        }
        public void Dispose() => IsDisposed = true;
        public override string ToString() =>
            $"RecordingSnapshot {{ SnapshotPath = [REDACTED], Length = {Length}, Sha256 = [REDACTED] }}";
    }

    private sealed class RecordingSnapshotFactory : IBrowserExecutableSnapshotFactory
    {
        public string Hash { get; set; } = new('a', 64);
        public string ProductName { get; set; } = "Google Chrome";
        public int CreateCount { get; private set; }
        public Action? OnCreate { get; set; }
        public Exception? Failure { get; set; }
        public Queue<bool> ValidationResults { get; } = [];
        public RecordingSnapshot? LastSnapshot { get; private set; }

        public Task<IBrowserExecutableSnapshot> CreateAsync(
            IBrowserPinnedHandle executable,
            long expectedLength,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            CreateCount++;
            OnCreate?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            LastSnapshot = new RecordingSnapshot
            {
                Length = expectedLength,
                Sha256 = Hash,
                ProductName = ProductName,
                ValidationResults = ValidationResults,
            };
            return Task.FromResult<IBrowserExecutableSnapshot>(LastSnapshot);
        }
    }

    private sealed class RecordingSignatureReader : IBrowserSignatureReader
    {
        public BrowserSignatureVerification Result { get; set; } =
            new(BrowserSignatureTrust.Trusted, "Google LLC");
        public Action? OnRead { get; set; }
        public Action<IBrowserExecutableSnapshot>? OnReadSnapshot { get; set; }
        public Exception? Failure { get; set; }

        public BrowserSignatureVerification Verify(IBrowserExecutableSnapshot snapshot)
        {
            OnRead?.Invoke();
            OnReadSnapshot?.Invoke(snapshot);
            if (Failure is not null) throw Failure;
            return Result;
        }
    }

    private sealed class RecordingProductReader : IBrowserProductReader
    {
        public string ProductName { get; set; } = "Google Chrome";
        public Func<IBrowserExecutableSnapshot, string>? Read { get; set; }
        public Action? OnRead { get; set; }

        public string ReadProductName(IBrowserExecutableSnapshot snapshot)
        {
            OnRead?.Invoke();
            return Read?.Invoke(snapshot) ?? ProductName;
        }
    }
}

public sealed class SelectedBrowserFactAttribute : FactAttribute
{
    public SelectedBrowserFactAttribute(string directoryVariable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryVariable);
        if (string.Equals(
                Environment.GetEnvironmentVariable("BAIDU_BATCH_REQUIRE_BROWSER_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(directoryVariable)))
        {
            Skip = $"Set {directoryVariable} to run this read-only integration test.";
        }
    }
}
