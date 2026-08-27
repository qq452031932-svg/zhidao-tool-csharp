using BaiduBatchTransfer.Core.Domain;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace BaiduBatchTransfer.Infrastructure.Security;

public enum BrowserExecutableVerificationErrorCode
{
    None = 0,
    InvalidSelection = 1,
    DirectoryUnavailable = 2,
    DirectoryReparsePoint = 3,
    ExecutableUnavailable = 4,
    ExecutableReparsePoint = 5,
    FinalParentMismatch = 6,
    SignatureMissing = 7,
    SignatureUntrusted = 8,
    FamilyMismatch = 9,
    PublisherMismatch = 10,
    ProductMismatch = 11,
    FileTooLarge = 12,
    IdentityChanged = 13,
    LaunchIdentityMismatch = 14,
    VerificationUnavailable = 15,
    UnsupportedPathNamespace = 16,
    SnapshotUnavailable = 17,
}

public enum BrowserSignatureTrust
{
    Trusted = 1,
    Unsigned = 2,
    Invalid = 3,
    Untrusted = 4,
}

public sealed class BrowserExecutableVerificationResult
{
    private BrowserExecutableVerificationResult(
        BrowserExecutableVerificationErrorCode errorCode,
        BrowserInstallationIdentity? identity)
    {
        ErrorCode = errorCode;
        Identity = identity;
    }

    public BrowserExecutableVerificationErrorCode ErrorCode { get; }
    public BrowserInstallationIdentity? Identity { get; }
    public bool IsVerified => ErrorCode == BrowserExecutableVerificationErrorCode.None && Identity is not null;

    public static BrowserExecutableVerificationResult Verified(BrowserInstallationIdentity identity) =>
        new(BrowserExecutableVerificationErrorCode.None, identity ?? throw new ArgumentNullException(nameof(identity)));

    public static BrowserExecutableVerificationResult Failed(BrowserExecutableVerificationErrorCode errorCode)
    {
        if (errorCode == BrowserExecutableVerificationErrorCode.None)
            throw new ArgumentOutOfRangeException(nameof(errorCode));
        return new BrowserExecutableVerificationResult(errorCode, null);
    }

    public override string ToString() =>
        $"BrowserExecutableVerificationResult {{ ErrorCode = {ErrorCode}, Identity = [REDACTED] }}";
}

public static class BrowserExecutablePolicy
{
    public static BrowserExecutableVerificationErrorCode Evaluate(
        BrowserFamily family,
        string fileName,
        string publisher,
        string productName)
    {
        var expected = family switch
        {
            BrowserFamily.Chrome => (FileName: "chrome.exe", Publisher: "Google LLC", Product: "Google Chrome"),
            BrowserFamily.Edge => (FileName: "msedge.exe", Publisher: "Microsoft Corporation", Product: "Microsoft Edge"),
            _ => default,
        };
        if (expected.FileName is null ||
            !string.Equals(fileName, expected.FileName, StringComparison.OrdinalIgnoreCase))
        {
            return BrowserExecutableVerificationErrorCode.FamilyMismatch;
        }
        if (!string.Equals(publisher, expected.Publisher, StringComparison.Ordinal))
            return BrowserExecutableVerificationErrorCode.PublisherMismatch;
        if (!string.Equals(productName, expected.Product, StringComparison.Ordinal))
            return BrowserExecutableVerificationErrorCode.ProductMismatch;
        return BrowserExecutableVerificationErrorCode.None;
    }

    internal static string ExecutableName(BrowserFamily family) => family switch
    {
        BrowserFamily.Chrome => "chrome.exe",
        BrowserFamily.Edge => "msedge.exe",
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };
}

public interface IBrowserExecutableVerifier
{
    Task<BrowserExecutableVerificationResult> VerifyAsync(
        string selectedDirectory,
        BrowserFamily family,
        CancellationToken cancellationToken);

    Task<BrowserExecutableVerificationResult> ReverifyForLaunchAsync(
        BrowserInstallationIdentity expectedIdentity,
        CancellationToken cancellationToken);
}

public interface IBrowserPinnedHandle : IDisposable
{
    SafeFileHandle SafeHandle { get; }
}

public sealed record BrowserNativeHandleInfo(
    string FinalPath,
    bool IsReparsePoint,
    string ObjectIdentity = "unknown")
{
    public override string ToString() =>
        $"BrowserNativeHandleInfo {{ FinalPath = [REDACTED], IsReparsePoint = {IsReparsePoint}, ObjectIdentity = [REDACTED] }}";
}

public sealed record BrowserFileIdentity(string VolumeId, string FileId, long Length)
{
    public override string ToString() =>
        $"BrowserFileIdentity {{ VolumeId = [REDACTED], FileId = [REDACTED], Length = {Length} }}";
}

public sealed record BrowserSignatureVerification(BrowserSignatureTrust Trust, string? Publisher)
{
    public override string ToString() =>
        $"BrowserSignatureVerification {{ Trust = {Trust}, Publisher = [REDACTED] }}";
}

public interface IBrowserNativeHandleReader
{
    IBrowserPinnedHandle OpenRoot(string exactRootPath);
    IBrowserPinnedHandle OpenDirectoryChild(IBrowserPinnedHandle parentDirectory, string exactComponentName);
    IBrowserPinnedHandle OpenExecutableChild(IBrowserPinnedHandle selectedDirectory, string exactFileName);
    BrowserNativeHandleInfo ReadInfo(IBrowserPinnedHandle handle);
}

public interface IBrowserFileIdentityReader
{
    BrowserFileIdentity Read(IBrowserPinnedHandle handle);
}

public interface IBrowserExecutableSnapshot : IDisposable
{
    SafeFileHandle SafeHandle { get; }
    string SnapshotPath { get; }
    long Length { get; }
    string Sha256 { get; }
}

public interface IBrowserExecutableSnapshotFactory
{
    Task<IBrowserExecutableSnapshot> CreateAsync(
        IBrowserPinnedHandle executable,
        long expectedLength,
        long maximumBytes,
        CancellationToken cancellationToken);
}

public interface IBrowserSignatureReader
{
    BrowserSignatureVerification Verify(IBrowserExecutableSnapshot snapshot);
}

public interface IBrowserProductReader
{
    string ReadProductName(IBrowserExecutableSnapshot snapshot);
}

public sealed class BrowserExecutableVerifier : IBrowserExecutableVerifier
{
    public const long MaximumSupportedExecutableBytes = 512L * 1024 * 1024;

    private readonly IBrowserNativeHandleReader nativeHandles;
    private readonly IBrowserFileIdentityReader fileIdentities;
    private readonly IBrowserExecutableSnapshotFactory snapshots;
    private readonly IBrowserSignatureReader signatures;
    private readonly IBrowserProductReader products;
    private readonly Func<Guid> installationIdFactory;

    public BrowserExecutableVerifier()
        : this(
            new WindowsBrowserNativeHandleReader(),
            new WindowsBrowserFileIdentityReader(),
            new WindowsBrowserExecutableSnapshotFactory(),
            new WindowsAuthenticodeSignatureReader(),
            new WindowsBrowserProductReader(),
            Guid.NewGuid)
    {
    }

    public BrowserExecutableVerifier(
        IBrowserNativeHandleReader nativeHandles,
        IBrowserFileIdentityReader fileIdentities,
        IBrowserExecutableSnapshotFactory snapshots,
        IBrowserSignatureReader signatures,
        IBrowserProductReader products,
        Func<Guid>? installationIdFactory = null)
    {
        this.nativeHandles = nativeHandles ?? throw new ArgumentNullException(nameof(nativeHandles));
        this.fileIdentities = fileIdentities ?? throw new ArgumentNullException(nameof(fileIdentities));
        this.snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        this.signatures = signatures ?? throw new ArgumentNullException(nameof(signatures));
        this.products = products ?? throw new ArgumentNullException(nameof(products));
        this.installationIdFactory = installationIdFactory ?? Guid.NewGuid;
    }

    public async Task<BrowserExecutableVerificationResult> VerifyAsync(
        string selectedDirectory,
        BrowserFamily family,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (family is not (BrowserFamily.Chrome or BrowserFamily.Edge))
            return Failed(BrowserExecutableVerificationErrorCode.InvalidSelection);
        var pathError = TryParsePinnedDrivePath(
            selectedDirectory, out var rootPath, out var directoryComponents);
        if (pathError != BrowserExecutableVerificationErrorCode.None) return Failed(pathError);

        var directoryHandles = new List<IBrowserPinnedHandle>(directoryComponents.Length + 1);
        var initialDirectoryInfos = new List<BrowserNativeHandleInfo>(directoryComponents.Length + 1);
        IBrowserPinnedHandle? executableHandle = null;
        try
        {
            var rootHandle = OpenRoot(rootPath);
            directoryHandles.Add(rootHandle);
            var initialRoot = nativeHandles.ReadInfo(rootHandle);
            cancellationToken.ThrowIfCancellationRequested();
            if (!PinnedDirectoryMatches(initialRoot, rootPath))
                return Failed(BrowserExecutableVerificationErrorCode.DirectoryReparsePoint);
            initialDirectoryInfos.Add(initialRoot);

            var expectedDirectory = rootPath;
            foreach (var component in directoryComponents)
            {
                var childHandle = OpenDirectoryChild(directoryHandles[^1], component);
                directoryHandles.Add(childHandle);
                expectedDirectory = Path.Combine(expectedDirectory, component);
                var childInfo = nativeHandles.ReadInfo(childHandle);
                cancellationToken.ThrowIfCancellationRequested();
                if (!PinnedDirectoryMatches(childInfo, expectedDirectory))
                    return Failed(BrowserExecutableVerificationErrorCode.DirectoryReparsePoint);
                initialDirectoryInfos.Add(childInfo);
            }

            var selectedDirectoryHandle = directoryHandles[^1];
            var finalDirectory = expectedDirectory;
            if (!DirectoriesStillSame(directoryHandles, initialDirectoryInfos))
                return Failed(BrowserExecutableVerificationErrorCode.IdentityChanged);

            var executableName = BrowserExecutablePolicy.ExecutableName(family);
            executableHandle = OpenExecutableChild(selectedDirectoryHandle, executableName);
            var initialExecutable = nativeHandles.ReadInfo(executableHandle);
            cancellationToken.ThrowIfCancellationRequested();
            if (initialExecutable.IsReparsePoint)
                return Failed(BrowserExecutableVerificationErrorCode.ExecutableReparsePoint);
            if (!TryNormalizeFinalPath(initialExecutable.FinalPath, out var finalExecutable) ||
                !DirectParentMatches(finalDirectory, finalExecutable))
            {
                return Failed(BrowserExecutableVerificationErrorCode.FinalParentMismatch);
            }

            var initialFileIdentity = fileIdentities.Read(executableHandle);
            cancellationToken.ThrowIfCancellationRequested();
            if (initialFileIdentity.Length < 0 || initialFileIdentity.Length > MaximumSupportedExecutableBytes)
                return Failed(BrowserExecutableVerificationErrorCode.FileTooLarge);

            using var snapshot = await CreateSnapshotAsync(
                executableHandle,
                initialFileIdentity.Length,
                MaximumSupportedExecutableBytes,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot.Length != initialFileIdentity.Length || !IsSha256(snapshot.Sha256) ||
                !StillSame(
                    directoryHandles, initialDirectoryInfos,
                    executableHandle, initialExecutable, initialFileIdentity,
                    finalDirectory, finalExecutable))
            {
                return Failed(BrowserExecutableVerificationErrorCode.IdentityChanged);
            }

            var signature = signatures.Verify(snapshot);
            cancellationToken.ThrowIfCancellationRequested();
            if (!StillSame(
                    directoryHandles, initialDirectoryInfos,
                    executableHandle, initialExecutable, initialFileIdentity,
                    finalDirectory, finalExecutable))
            {
                return Failed(BrowserExecutableVerificationErrorCode.IdentityChanged);
            }
            if (signature.Trust == BrowserSignatureTrust.Unsigned)
                return Failed(BrowserExecutableVerificationErrorCode.SignatureMissing);
            if (signature.Trust != BrowserSignatureTrust.Trusted || string.IsNullOrWhiteSpace(signature.Publisher))
                return Failed(BrowserExecutableVerificationErrorCode.SignatureUntrusted);

            var productName = products.ReadProductName(snapshot);
            cancellationToken.ThrowIfCancellationRequested();
            if (!StillSame(
                    directoryHandles, initialDirectoryInfos,
                    executableHandle, initialExecutable, initialFileIdentity,
                    finalDirectory, finalExecutable))
            {
                return Failed(BrowserExecutableVerificationErrorCode.IdentityChanged);
            }

            var policyError = BrowserExecutablePolicy.Evaluate(
                family, Path.GetFileName(finalExecutable), signature.Publisher, productName);
            if (policyError != BrowserExecutableVerificationErrorCode.None)
                return Failed(policyError);

            if (!StillSame(
                    directoryHandles, initialDirectoryInfos,
                    executableHandle, initialExecutable, initialFileIdentity,
                    finalDirectory, finalExecutable))
                return Failed(BrowserExecutableVerificationErrorCode.IdentityChanged);

            var identity = new BrowserInstallationIdentity(
                installationIdFactory(), family, finalDirectory, finalExecutable,
                signature.Publisher, productName,
                $"{initialFileIdentity.VolumeId}:{initialFileIdentity.FileId}:{snapshot.Sha256.ToLowerInvariant()}");
            return BrowserExecutableVerificationResult.Verified(identity);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (VerificationFailureException exception)
        {
            return Failed(exception.ErrorCode);
        }
        catch
        {
            return Failed(BrowserExecutableVerificationErrorCode.VerificationUnavailable);
        }
        finally
        {
            executableHandle?.Dispose();
            for (var index = directoryHandles.Count - 1; index >= 0; index--)
                directoryHandles[index].Dispose();
        }
    }

    private IBrowserPinnedHandle OpenRoot(string rootPath)
    {
        try
        {
            return nativeHandles.OpenRoot(rootPath);
        }
        catch
        {
            throw new VerificationFailureException(
                BrowserExecutableVerificationErrorCode.DirectoryUnavailable);
        }
    }

    private async Task<IBrowserExecutableSnapshot> CreateSnapshotAsync(
        IBrowserPinnedHandle executable,
        long expectedLength,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            return await snapshots.CreateAsync(
                executable, expectedLength, maximumBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new VerificationFailureException(
                BrowserExecutableVerificationErrorCode.SnapshotUnavailable);
        }
    }

    private IBrowserPinnedHandle OpenDirectoryChild(
        IBrowserPinnedHandle parentDirectory,
        string exactComponentName)
    {
        try
        {
            return nativeHandles.OpenDirectoryChild(parentDirectory, exactComponentName);
        }
        catch
        {
            throw new VerificationFailureException(
                BrowserExecutableVerificationErrorCode.DirectoryUnavailable);
        }
    }

    private IBrowserPinnedHandle OpenExecutableChild(
        IBrowserPinnedHandle selectedDirectory,
        string exactFileName)
    {
        try
        {
            return nativeHandles.OpenExecutableChild(selectedDirectory, exactFileName);
        }
        catch
        {
            throw new VerificationFailureException(
                BrowserExecutableVerificationErrorCode.ExecutableUnavailable);
        }
    }

    public async Task<BrowserExecutableVerificationResult> ReverifyForLaunchAsync(
        BrowserInstallationIdentity expectedIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        var current = await VerifyAsync(
            expectedIdentity.InstallationDirectory,
            expectedIdentity.BrowserFamily,
            cancellationToken).ConfigureAwait(false);
        if (!current.IsVerified || current.Identity is null) return current;

        return SamePersistedIdentity(expectedIdentity, current.Identity)
            ? BrowserExecutableVerificationResult.Verified(expectedIdentity)
            : Failed(BrowserExecutableVerificationErrorCode.LaunchIdentityMismatch);
    }

    private bool StillSame(
        IReadOnlyList<IBrowserPinnedHandle> directoryHandles,
        IReadOnlyList<BrowserNativeHandleInfo> initialDirectoryInfos,
        IBrowserPinnedHandle executableHandle,
        BrowserNativeHandleInfo initialExecutable,
        BrowserFileIdentity initialFileIdentity,
        string finalDirectory,
        string finalExecutable)
    {
        if (!DirectoriesStillSame(directoryHandles, initialDirectoryInfos)) return false;
        var currentExecutable = nativeHandles.ReadInfo(executableHandle);
        var currentFileIdentity = fileIdentities.Read(executableHandle);
        return currentExecutable == initialExecutable &&
               currentFileIdentity == initialFileIdentity &&
               TryNormalizeFinalPath(currentExecutable.FinalPath, out var currentFinalExecutable) &&
               string.Equals(currentFinalExecutable, finalExecutable, StringComparison.OrdinalIgnoreCase) &&
               DirectParentMatches(finalDirectory, currentFinalExecutable);
    }

    private bool DirectoriesStillSame(
        IReadOnlyList<IBrowserPinnedHandle> handles,
        IReadOnlyList<BrowserNativeHandleInfo> initialInfos)
    {
        if (handles.Count != initialInfos.Count) return false;
        for (var index = 0; index < handles.Count; index++)
        {
            if (nativeHandles.ReadInfo(handles[index]) != initialInfos[index]) return false;
        }
        return true;
    }

    private static bool SamePersistedIdentity(
        BrowserInstallationIdentity expected,
        BrowserInstallationIdentity current) =>
        expected.BrowserFamily == current.BrowserFamily &&
        string.Equals(expected.InstallationDirectory, current.InstallationDirectory, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(expected.ExecutablePath, current.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(expected.Publisher, current.Publisher, StringComparison.Ordinal) &&
        string.Equals(expected.ProductName, current.ProductName, StringComparison.Ordinal) &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected.FileIdentity),
            Encoding.UTF8.GetBytes(current.FileIdentity));

    private static BrowserExecutableVerificationErrorCode TryParsePinnedDrivePath(
        string value,
        out string rootPath,
        out string[] components)
    {
        rootPath = string.Empty;
        components = [];
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() ||
            value.Any(char.IsControl) || value.Contains('/') || !Path.IsPathFullyQualified(value))
        {
            return BrowserExecutableVerificationErrorCode.InvalidSelection;
        }

        if (value.StartsWith(@"\\", StringComparison.Ordinal) ||
            value.Length < 3 || !char.IsAsciiLetter(value[0]) || value[1] != ':' || value[2] != '\\')
            return BrowserExecutableVerificationErrorCode.UnsupportedPathNamespace;

        try
        {
            if (!string.Equals(value, Path.GetFullPath(value), StringComparison.OrdinalIgnoreCase))
                return BrowserExecutableVerificationErrorCode.InvalidSelection;
        }
        catch
        {
            return BrowserExecutableVerificationErrorCode.InvalidSelection;
        }

        rootPath = value[..3];
        if (value.Length == rootPath.Length)
            return BrowserExecutableVerificationErrorCode.UnsupportedPathNamespace;
        if (Path.EndsInDirectorySeparator(value))
            return BrowserExecutableVerificationErrorCode.InvalidSelection;

        components = value[rootPath.Length..].Split('\\', StringSplitOptions.None);
        if (components.Length == 0 || components.Any(component =>
                string.IsNullOrWhiteSpace(component) || component is "." or ".." ||
                component.Any(char.IsControl) || component.IndexOfAny(['\\', '/', ':']) >= 0))
        {
            components = [];
            return BrowserExecutableVerificationErrorCode.InvalidSelection;
        }
        return BrowserExecutableVerificationErrorCode.None;
    }

    private static bool PinnedDirectoryMatches(BrowserNativeHandleInfo info, string expectedPath) =>
        !info.IsReparsePoint &&
        TryNormalizeFinalPath(info.FinalPath, out var finalPath) &&
        string.Equals(finalPath, Path.TrimEndingDirectorySeparator(expectedPath), StringComparison.OrdinalIgnoreCase);

    private static bool TryNormalizeFinalPath(string value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)) return false;
        var candidate = value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            ? @"\\" + value[8..]
            : value.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
                ? value[4..]
                : value;
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            if (!Path.IsPathFullyQualified(full)) return false;
            normalized = full;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool DirectParentMatches(string directory, string executable) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(executable) ?? string.Empty),
            Path.TrimEndingDirectorySeparator(directory),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSha256(string value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static BrowserExecutableVerificationResult Failed(
        BrowserExecutableVerificationErrorCode errorCode) =>
        BrowserExecutableVerificationResult.Failed(errorCode);

    private sealed class VerificationFailureException(
        BrowserExecutableVerificationErrorCode errorCode) : Exception("Browser verification failed.")
    {
        public BrowserExecutableVerificationErrorCode ErrorCode { get; } = errorCode;
    }
}

internal sealed class WindowsBrowserPinnedHandle(SafeFileHandle safeHandle) : IBrowserPinnedHandle
{
    public SafeFileHandle SafeHandle { get; } = safeHandle;
    public void Dispose() => SafeHandle.Dispose();
}

internal sealed class WindowsBrowserNativeHandleReader : IBrowserNativeHandleReader
{
    private const uint FileReadData = 0x00000001;
    private const uint FileReadAttributes = 0x80;
    private const uint Synchronize = 0x00100000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeReparsePoint = 0x400;
    private const uint FileAttributeNormal = 0x80;
    private const uint FinalPathFlags = 0;
    private const uint ObjectCaseInsensitive = 0x40;
    private const uint FileOpen = 1;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileOpenReparsePoint = 0x00200000;

    public IBrowserPinnedHandle OpenRoot(string exactRootPath) =>
        OpenRootPath(exactRootPath);

    public IBrowserPinnedHandle OpenDirectoryChild(
        IBrowserPinnedHandle parentDirectory,
        string exactComponentName) =>
        OpenRelative(
            parentDirectory, exactComponentName,
            FileReadAttributes | Synchronize,
            FileDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint,
            (uint)(FileShare.Read | FileShare.Write));

    public IBrowserPinnedHandle OpenExecutableChild(
        IBrowserPinnedHandle selectedDirectory,
        string exactFileName) =>
        OpenRelative(
            selectedDirectory, exactFileName,
            FileReadData | FileReadAttributes,
            FileNonDirectoryFile | FileOpenReparsePoint,
            (uint)FileShare.Read);

    public BrowserNativeHandleInfo ReadInfo(IBrowserPinnedHandle handle)
    {
        if (!GetFileInformationByHandle(handle.SafeHandle, out var information))
            throw SafeIoFailure();
        var finalPath = ReadFinalPath(handle.SafeHandle);
        var objectIdentity = $"{information.VolumeSerialNumber:x8}:" +
                             $"{information.FileIndexHigh:x8}{information.FileIndexLow:x8}";
        return new BrowserNativeHandleInfo(
            finalPath,
            (information.FileAttributes & FileAttributeReparsePoint) != 0,
            objectIdentity);
    }

    private static IBrowserPinnedHandle OpenRootPath(string path)
    {
        var handle = CreateFileW(
            path,
            FileReadAttributes,
            FileShare.Read | FileShare.Write,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw SafeIoFailure();
        }
        return new WindowsBrowserPinnedHandle(handle);
    }

    private static IBrowserPinnedHandle OpenRelative(
        IBrowserPinnedHandle parent,
        string component,
        uint desiredAccess,
        uint createOptions,
        uint shareAccess)
    {
        if (string.IsNullOrWhiteSpace(component) || component is "." or ".." ||
            component.Any(char.IsControl) || component.IndexOfAny(['\\', '/', ':']) >= 0)
        {
            throw new IOException("Browser verification operation failed.");
        }

        var componentBuffer = Marshal.StringToHGlobalUni(component);
        var unicodeStringPointer = IntPtr.Zero;
        try
        {
            var unicodeString = new UnicodeString
            {
                Length = checked((ushort)(component.Length * sizeof(char))),
                MaximumLength = checked((ushort)((component.Length + 1) * sizeof(char))),
                Buffer = componentBuffer,
            };
            unicodeStringPointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicodeString, unicodeStringPointer, false);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.SafeHandle.DangerousGetHandle(),
                ObjectName = unicodeStringPointer,
                Attributes = ObjectCaseInsensitive,
            };
            var status = NtCreateFile(
                out var handle,
                desiredAccess,
                ref attributes,
                out _,
                IntPtr.Zero,
                FileAttributeNormal,
                shareAccess,
                FileOpen,
                createOptions,
                IntPtr.Zero,
                0);
            if (status < 0 || handle.IsInvalid)
            {
                handle.Dispose();
                throw SafeIoFailure(unchecked((int)RtlNtStatusToDosError(status)));
            }
            return new WindowsBrowserPinnedHandle(handle);
        }
        finally
        {
            if (unicodeStringPointer != IntPtr.Zero) Marshal.FreeHGlobal(unicodeStringPointer);
            Marshal.FreeHGlobal(componentBuffer);
        }
    }

    private static string ReadFinalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, FinalPathFlags);
            if (length == 0) throw SafeIoFailure();
            if (length < buffer.Capacity) return buffer.ToString();
            capacity = checked((int)length + 1);
        }
        throw new IOException("Browser verification operation failed.");
    }

    private static IOException SafeIoFailure() =>
        new("Browser verification operation failed.", new Win32Exception(Marshal.GetLastWin32Error()));

    private static IOException SafeIoFailure(int errorCode) =>
        new("Browser verification operation failed.", new Win32Exception(errorCode));

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out SafeFileHandle fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}

internal sealed class WindowsBrowserFileIdentityReader : IBrowserFileIdentityReader
{
    public BrowserFileIdentity Read(IBrowserPinnedHandle handle)
    {
        if (!GetFileInformationByHandle(handle.SafeHandle, out var information))
            throw new IOException("Browser verification operation failed.");
        var length = ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
        return new BrowserFileIdentity(
            information.VolumeSerialNumber.ToString("x8"),
            $"{information.FileIndexHigh:x8}{information.FileIndexLow:x8}",
            length);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);
}

internal sealed class WindowsBrowserExecutableSnapshot(
    FileStream stream,
    string snapshotPath,
    long length,
    string sha256) : IBrowserExecutableSnapshot
{
    public SafeFileHandle SafeHandle => stream.SafeFileHandle;
    public string SnapshotPath { get; } = snapshotPath;
    public long Length { get; } = length;
    public string Sha256 { get; } = sha256;
    public void Dispose() => stream.Dispose();
    public override string ToString() =>
        $"WindowsBrowserExecutableSnapshot {{ SnapshotPath = [REDACTED], Length = {Length}, Sha256 = [REDACTED] }}";
}

internal sealed class WindowsBrowserExecutableSnapshotFactory : IBrowserExecutableSnapshotFactory
{
    private const int BufferSize = 64 * 1024;

    public async Task<IBrowserExecutableSnapshot> CreateAsync(
        IBrowserPinnedHandle executable,
        long expectedLength,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (expectedLength < 0 || expectedLength > maximumBytes)
            throw new IOException("Browser executable exceeds the supported verification size.");

        FileStream? snapshotStream = null;
        string? snapshotPath = null;
        try
        {
            for (var attempt = 0; attempt < 4 && snapshotStream is null; attempt++)
            {
                snapshotPath = Path.Combine(
                    Path.GetTempPath(),
                    $"bbt-browser-{Guid.NewGuid():N}.snapshot");
                try
                {
                    snapshotStream = new FileStream(
                        snapshotPath,
                        FileMode.CreateNew,
                        FileAccess.ReadWrite,
                        FileShare.Read,
                        BufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
                }
                catch (IOException) when (attempt < 3)
                {
                    // A cryptographically random name collision is retried without inspecting the directory.
                }
            }
            if (snapshotStream is null || snapshotPath is null)
                throw new IOException("Browser snapshot creation failed.");

            using var borrowedHandle = new SafeFileHandle(
                executable.SafeHandle.DangerousGetHandle(), ownsHandle: false);
            await using var source = new FileStream(
                borrowedHandle, FileAccess.Read, BufferSize, isAsync: true);
            source.Position = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[BufferSize];
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                total = checked(total + read);
                if (total > maximumBytes)
                    throw new IOException("Browser executable exceeds the supported verification size.");
                hash.AppendData(buffer, 0, read);
                await snapshotStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }
            if (total != expectedLength)
                throw new IOException("Browser executable changed during verification.");
            await snapshotStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            snapshotStream.Position = 0;
            var result = new WindowsBrowserExecutableSnapshot(
                snapshotStream,
                snapshotPath,
                total,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
            snapshotStream = null;
            return result;
        }
        finally
        {
            if (snapshotStream is not null)
                await snapshotStream.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal sealed class WindowsBrowserProductReader : IBrowserProductReader
{
    public string ReadProductName(IBrowserExecutableSnapshot snapshot)
    {
        if (snapshot.SafeHandle.IsInvalid || snapshot.SafeHandle.IsClosed)
            throw new IOException("Browser verification operation failed.");
        var productName = FileVersionInfo.GetVersionInfo(snapshot.SnapshotPath).ProductName;
        return string.IsNullOrWhiteSpace(productName)
            ? throw new InvalidDataException("Browser product metadata is unavailable.")
            : productName;
    }
}

internal sealed class WindowsAuthenticodeSignatureReader : IBrowserSignatureReader
{
    private static readonly Guid ActionGenericVerifyV2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
    private const uint UiNone = 2;
    private const uint RevokeNone = 0;
    private const uint ChoiceFile = 1;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;
    private const uint CacheOnlyUrlRetrieval = 0x00001000;
    private const int TrustENoSignature = unchecked((int)0x800B0100);

    public BrowserSignatureVerification Verify(IBrowserExecutableSnapshot snapshot)
    {
        if (snapshot.SafeHandle.IsInvalid || snapshot.SafeHandle.IsClosed)
            throw new IOException("Browser verification operation failed.");

        var pathPointer = Marshal.StringToCoTaskMemUni(snapshot.SnapshotPath);
        var fileInfoPointer = IntPtr.Zero;
        var trustDataPointer = IntPtr.Zero;
        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = pathPointer,
                FileHandle = snapshot.SafeHandle.DangerousGetHandle(),
                KnownSubject = IntPtr.Zero,
            };
            fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var trustData = CreateTrustData(fileInfoPointer, StateActionVerify);
            trustDataPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(trustData, trustDataPointer, false);

            var action = ActionGenericVerifyV2;
            var status = WinVerifyTrust(IntPtr.Zero, ref action, trustDataPointer);
            if (status == TrustENoSignature)
                return new BrowserSignatureVerification(BrowserSignatureTrust.Unsigned, null);
            if (status != 0)
                return new BrowserSignatureVerification(BrowserSignatureTrust.Untrusted, null);

            try
            {
#pragma warning disable SYSLIB0057
                using var certificate = new X509Certificate2(
                    X509Certificate.CreateFromSignedFile(snapshot.SnapshotPath));
#pragma warning restore SYSLIB0057
                var publisher = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
                return string.IsNullOrWhiteSpace(publisher)
                    ? new BrowserSignatureVerification(BrowserSignatureTrust.Invalid, null)
                    : new BrowserSignatureVerification(BrowserSignatureTrust.Trusted, publisher);
            }
            catch (CryptographicException)
            {
                return new BrowserSignatureVerification(BrowserSignatureTrust.Invalid, null);
            }
        }
        finally
        {
            if (trustDataPointer != IntPtr.Zero)
            {
                var closeData = Marshal.PtrToStructure<WinTrustData>(trustDataPointer);
                closeData.StateAction = StateActionClose;
                Marshal.StructureToPtr(closeData, trustDataPointer, false);
                var action = ActionGenericVerifyV2;
                _ = WinVerifyTrust(IntPtr.Zero, ref action, trustDataPointer);
                Marshal.FreeCoTaskMem(trustDataPointer);
            }
            if (fileInfoPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(fileInfoPointer);
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    private static WinTrustData CreateTrustData(IntPtr fileInfo, uint stateAction) => new()
    {
        StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
        PolicyCallbackData = IntPtr.Zero,
        SipClientData = IntPtr.Zero,
        UiChoice = UiNone,
        RevocationChecks = RevokeNone,
        UnionChoice = ChoiceFile,
        FileInfo = fileInfo,
        StateAction = stateAction,
        StateData = IntPtr.Zero,
        UrlReference = IntPtr.Zero,
        ProviderFlags = CacheOnlyUrlRetrieval,
        UiContext = 0,
        SignatureSettings = IntPtr.Zero,
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        [In] ref Guid actionId,
        IntPtr trustData);
}
