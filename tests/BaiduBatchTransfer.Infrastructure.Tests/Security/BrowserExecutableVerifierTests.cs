using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Security;
using Microsoft.Win32.SafeHandles;

namespace BaiduBatchTransfer.Infrastructure.Tests.Security;

public sealed class BrowserExecutableVerifierTests
{
    private const string DirectoryPath = @"C:\Selected\Application";
    private const string ChromePath = @"C:\Selected\Application\chrome.exe";

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
        Assert.Equal(new[] { DirectoryPath }, dependencies.Native.OpenedPaths);
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
        Assert.Equal(new[] { DirectoryPath }, dependencies.Native.OpenedPaths);
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
        Assert.Empty(dependencies.Native.OpenedPaths);
    }

    [Fact]
    public async Task OpensOnlySelectedDirectoryAndItsExactDirectChild()
    {
        var dependencies = ValidDependencies();

        var result = await dependencies.CreateVerifier().VerifyAsync(
            DirectoryPath, BrowserFamily.Chrome, CancellationToken.None);

        Assert.True(result.IsVerified);
        Assert.Equal(new[] { DirectoryPath, ChromePath }, dependencies.Native.OpenedPaths);
    }

    [Fact]
    public async Task HoldsBothHandlesUntilAllVerificationFinishes()
    {
        var dependencies = ValidDependencies();
        dependencies.Signature.OnRead = () => Assert.All(dependencies.Native.Handles, handle => Assert.False(handle.IsDisposed));
        dependencies.Product.OnRead = () => Assert.All(dependencies.Native.Handles, handle => Assert.False(handle.IsDisposed));
        dependencies.Identity.OnHash = () => Assert.All(dependencies.Native.Handles, handle => Assert.False(handle.IsDisposed));

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
        Assert.Equal(0, dependencies.Identity.HashCount);
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
        Assert.DoesNotContain(dependencies.Identity.Hash, result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LaunchTimeReverificationFailsClosedWhenIdentityOrHashDiffers()
    {
        var dependencies = ValidDependencies();
        var verifier = dependencies.CreateVerifier();
        var initial = await verifier.VerifyAsync(DirectoryPath, BrowserFamily.Chrome, CancellationToken.None);
        dependencies.Identity.Hash = new string('b', 64);

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
        dependencies.Identity.OnHash = cancellation.Cancel;

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

    [SelectedBrowserFact]
    public async Task ExplicitlySelectedWindowsInstallationCanBeVerifiedReadOnly()
    {
        var selectedDirectory = Environment.GetEnvironmentVariable("BAIDU_BATCH_TRANSFER_TEST_BROWSER_DIRECTORY");
        var familyText = Environment.GetEnvironmentVariable("BAIDU_BATCH_TRANSFER_TEST_BROWSER_FAMILY");
        Assert.True(Enum.TryParse<BrowserFamily>(familyText, ignoreCase: true, out var family));

        var result = await new BrowserExecutableVerifier().VerifyAsync(
            selectedDirectory!, family, CancellationToken.None);

        Assert.True(result.IsVerified, result.ErrorCode.ToString());
    }

    private static Dependencies ValidDependencies() => new();

    private sealed class Dependencies
    {
        public RecordingNativeHandleReader Native { get; } = new();
        public RecordingIdentityReader Identity { get; } = new();
        public RecordingSignatureReader Signature { get; } = new();
        public RecordingProductReader Product { get; } = new();

        public BrowserExecutableVerifier CreateVerifier() =>
            new(Native, Identity, Signature, Product, () => Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
    }

    private sealed class RecordingHandle : IBrowserPinnedHandle
    {
        public SafeFileHandle SafeHandle { get; } = new(new IntPtr(1), ownsHandle: false);
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
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
        private int directoryInfoReads;
        public List<string> OpenedPaths { get; } = [];
        public List<RecordingHandle> Handles { get; } = [];
        private HashSet<RecordingHandle> DirectoryHandles { get; } = [];

        public IBrowserPinnedHandle OpenDirectory(string exactPath)
        {
            OpenedPaths.Add(exactPath);
            if (DirectoryOpenFailure is not null) throw DirectoryOpenFailure;
            var handle = AddHandle();
            DirectoryHandles.Add(handle);
            return handle;
        }

        public IBrowserPinnedHandle OpenExecutable(string exactPath)
        {
            OpenedPaths.Add(exactPath);
            if (ExecutableOpenFailure is not null) throw ExecutableOpenFailure;
            return AddHandle();
        }

        public BrowserNativeHandleInfo ReadInfo(IBrowserPinnedHandle handle)
        {
            if (DirectoryHandles.Contains(handle))
            {
                directoryInfoReads++;
                return directoryInfoReads == ChangeDirectoryObjectAtRead
                    ? DirectoryInfo with { ObjectIdentity = "directory-b" }
                    : DirectoryInfo;
            }
            fileInfoReads++;
            return fileInfoReads == ChangeFileFinalPathAtRead
                ? FileInfo with { FinalPath = @"C:\Outside\chrome.exe" }
                : FileInfo;
        }

        private RecordingHandle AddHandle()
        {
            var handle = new RecordingHandle();
            Handles.Add(handle);
            return handle;
        }
    }

    private sealed class RecordingIdentityReader : IBrowserFileIdentityReader
    {
        private int reads;
        public BrowserFileIdentity Identity { get; set; } = new("volume-a", "file-a", 1024);
        public int ChangeAtRead { get; set; }
        public string Hash { get; set; } = new('a', 64);
        public int HashCount { get; private set; }
        public Action? OnHash { get; set; }

        public BrowserFileIdentity Read(IBrowserPinnedHandle handle)
        {
            reads++;
            return reads == ChangeAtRead ? Identity with { FileId = "file-b" } : Identity;
        }

        public Task<string> ComputeSha256Async(
            IBrowserPinnedHandle handle,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            HashCount++;
            OnHash?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Hash);
        }
    }

    private sealed class RecordingSignatureReader : IBrowserSignatureReader
    {
        public BrowserSignatureVerification Result { get; set; } =
            new(BrowserSignatureTrust.Trusted, "Google LLC");
        public Action? OnRead { get; set; }
        public Exception? Failure { get; set; }

        public BrowserSignatureVerification Verify(IBrowserPinnedHandle handle, string retainedFinalPath)
        {
            OnRead?.Invoke();
            if (Failure is not null) throw Failure;
            return Result;
        }
    }

    private sealed class RecordingProductReader : IBrowserProductReader
    {
        public string ProductName { get; set; } = "Google Chrome";
        public Action? OnRead { get; set; }

        public string ReadProductName(IBrowserPinnedHandle handle, string retainedFinalPath)
        {
            OnRead?.Invoke();
            return ProductName;
        }
    }
}

public sealed class SelectedBrowserFactAttribute : FactAttribute
{
    public SelectedBrowserFactAttribute()
    {
        var selectedDirectory = Environment.GetEnvironmentVariable(
            "BAIDU_BATCH_TRANSFER_TEST_BROWSER_DIRECTORY");
        var familyText = Environment.GetEnvironmentVariable(
            "BAIDU_BATCH_TRANSFER_TEST_BROWSER_FAMILY");
        if (string.IsNullOrWhiteSpace(selectedDirectory) ||
            !Enum.TryParse<BrowserFamily>(familyText, ignoreCase: true, out var family) ||
            family is not (BrowserFamily.Chrome or BrowserFamily.Edge))
        {
            Skip = "Set BAIDU_BATCH_TRANSFER_TEST_BROWSER_DIRECTORY and " +
                   "BAIDU_BATCH_TRANSFER_TEST_BROWSER_FAMILY to run this read-only integration test.";
        }
    }
}
