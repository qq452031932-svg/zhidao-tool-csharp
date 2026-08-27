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
    IBrowserPinnedHandle OpenDirectory(string exactPath);
    IBrowserPinnedHandle OpenExecutable(string exactPath);
    BrowserNativeHandleInfo ReadInfo(IBrowserPinnedHandle handle);
}

public interface IBrowserFileIdentityReader
{
    BrowserFileIdentity Read(IBrowserPinnedHandle handle);
    Task<string> ComputeSha256Async(
        IBrowserPinnedHandle handle,
        long maximumBytes,
        CancellationToken cancellationToken);
}

public interface IBrowserSignatureReader
{
    BrowserSignatureVerification Verify(IBrowserPinnedHandle handle, string retainedFinalPath);
}

public interface IBrowserProductReader
{
    string ReadProductName(IBrowserPinnedHandle handle, string retainedFinalPath);
}

public sealed class BrowserExecutableVerifier : IBrowserExecutableVerifier
{
    public const long MaximumSupportedExecutableBytes = 512L * 1024 * 1024;

    private readonly IBrowserNativeHandleReader nativeHandles;
    private readonly IBrowserFileIdentityReader fileIdentities;
    private readonly IBrowserSignatureReader signatures;
    private readonly IBrowserProductReader products;
    private readonly Func<Guid> installationIdFactory;

    public BrowserExecutableVerifier()
        : this(
            new WindowsBrowserNativeHandleReader(),
            new WindowsBrowserFileIdentityReader(),
            new WindowsAuthenticodeSignatureReader(),
            new WindowsBrowserProductReader(),
            Guid.NewGuid)
    {
    }

    public BrowserExecutableVerifier(
        IBrowserNativeHandleReader nativeHandles,
        IBrowserFileIdentityReader fileIdentities,
        IBrowserSignatureReader signatures,
        IBrowserProductReader products,
        Func<Guid>? installationIdFactory = null)
    {
        this.nativeHandles = nativeHandles ?? throw new ArgumentNullException(nameof(nativeHandles));
        this.fileIdentities = fileIdentities ?? throw new ArgumentNullException(nameof(fileIdentities));
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
        if (family is not (BrowserFamily.Chrome or BrowserFamily.Edge) ||
            !TryValidateSelectedDirectory(selectedDirectory))
        {
            return Failed(BrowserExecutableVerificationErrorCode.InvalidSelection);
        }

        try
        {
            using var directoryHandle = OpenDirectory(selectedDirectory);
            var initialDirectory = nativeHandles.ReadInfo(directoryHandle);
            cancellationToken.ThrowIfCancellationRequested();
            if (initialDirectory.IsReparsePoint)
                return Failed(BrowserExecutableVerificationErrorCode.DirectoryReparsePoint);
            if (!TryNormalizeFinalPath(initialDirectory.FinalPath, out var finalDirectory))
                return Failed(BrowserExecutableVerificationErrorCode.VerificationUnavailable);
            if (!string.Equals(selectedDirectory, finalDirectory, StringComparison.OrdinalIgnoreCase))
                return Failed(BrowserExecutableVerificationErrorCode.DirectoryReparsePoint);

            var executableName = BrowserExecutablePolicy.ExecutableName(family);
            var exactExecutablePath = Path.Combine(selectedDirectory, executableName);
            using var executableHandle = OpenExecutable(exactExecutablePath);
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

            var signature = signatures.Verify(executableHandle, finalExecutable);
            cancellationToken.ThrowIfCancellationRequested();
            if (!StillSame(
                    directoryHandle, initialDirectory,
                    executableHandle, initialExecutable, initialFileIdentity,
                    finalDirectory, finalExecutable))
            {
                return Failed(BrowserExecutableVerificationErrorCode.IdentityChanged);
            }
            if (signature.Trust == BrowserSignatureTrust.Unsigned)
                return Failed(BrowserExecutableVerificationErrorCode.SignatureMissing);
            if (signature.Trust != BrowserSignatureTrust.Trusted || string.IsNullOrWhiteSpace(signature.Publisher))
                return Failed(BrowserExecutableVerificationErrorCode.SignatureUntrusted);

            var productName = products.ReadProductName(executableHandle, finalExecutable);
            cancellationToken.ThrowIfCancellationRequested();
            if (!StillSame(
                    directoryHandle, initialDirectory,
                    executableHandle, initialExecutable, initialFileIdentity,
                    finalDirectory, finalExecutable))
            {
                return Failed(BrowserExecutableVerificationErrorCode.IdentityChanged);
            }

            var policyError = BrowserExecutablePolicy.Evaluate(
                family, Path.GetFileName(finalExecutable), signature.Publisher, productName);
            if (policyError != BrowserExecutableVerificationErrorCode.None)
                return Failed(policyError);

            var sha256 = await fileIdentities.ComputeSha256Async(
                executableHandle, MaximumSupportedExecutableBytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsSha256(sha256) ||
                !StillSame(
                    directoryHandle, initialDirectory,
                    executableHandle, initialExecutable, initialFileIdentity,
                    finalDirectory, finalExecutable))
            {
                return Failed(BrowserExecutableVerificationErrorCode.IdentityChanged);
            }

            var identity = new BrowserInstallationIdentity(
                installationIdFactory(), family, finalDirectory, finalExecutable,
                signature.Publisher, productName,
                $"{initialFileIdentity.VolumeId}:{initialFileIdentity.FileId}:{sha256.ToLowerInvariant()}");
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
    }

    private IBrowserPinnedHandle OpenDirectory(string selectedDirectory)
    {
        try
        {
            return nativeHandles.OpenDirectory(selectedDirectory);
        }
        catch
        {
            throw new VerificationFailureException(
                BrowserExecutableVerificationErrorCode.DirectoryUnavailable);
        }
    }

    private IBrowserPinnedHandle OpenExecutable(string executablePath)
    {
        try
        {
            return nativeHandles.OpenExecutable(executablePath);
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
        IBrowserPinnedHandle directoryHandle,
        BrowserNativeHandleInfo initialDirectory,
        IBrowserPinnedHandle executableHandle,
        BrowserNativeHandleInfo initialExecutable,
        BrowserFileIdentity initialFileIdentity,
        string finalDirectory,
        string finalExecutable)
    {
        var currentDirectory = nativeHandles.ReadInfo(directoryHandle);
        var currentExecutable = nativeHandles.ReadInfo(executableHandle);
        var currentFileIdentity = fileIdentities.Read(executableHandle);
        return currentDirectory == initialDirectory &&
               currentExecutable == initialExecutable &&
               currentFileIdentity == initialFileIdentity &&
               TryNormalizeFinalPath(currentDirectory.FinalPath, out var currentFinalDirectory) &&
               TryNormalizeFinalPath(currentExecutable.FinalPath, out var currentFinalExecutable) &&
               string.Equals(currentFinalDirectory, finalDirectory, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(currentFinalExecutable, finalExecutable, StringComparison.OrdinalIgnoreCase) &&
               DirectParentMatches(currentFinalDirectory, currentFinalExecutable);
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

    private static bool TryValidateSelectedDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() ||
            value.Any(char.IsControl) || Path.EndsInDirectorySeparator(value) ||
            !Path.IsPathFullyQualified(value))
        {
            return false;
        }

        try
        {
            return string.Equals(value, Path.GetFullPath(value), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

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
    private const uint GenericRead = 0x80000000;
    private const uint FileReadAttributes = 0x80;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileAttributeReparsePoint = 0x400;
    private const uint FinalPathFlags = 0;

    public IBrowserPinnedHandle OpenDirectory(string exactPath) =>
        Open(exactPath, FileReadAttributes, FileFlagBackupSemantics | FileFlagOpenReparsePoint);

    public IBrowserPinnedHandle OpenExecutable(string exactPath) =>
        Open(exactPath, GenericRead, FileFlagOpenReparsePoint | FileFlagOverlapped);

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

    private static IBrowserPinnedHandle Open(string path, uint desiredAccess, uint flags)
    {
        var handle = CreateFileW(
            path, desiredAccess, FileShare.Read, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw SafeIoFailure();
        }
        return new WindowsBrowserPinnedHandle(handle);
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

    public async Task<string> ComputeSha256Async(
        IBrowserPinnedHandle handle,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var identity = Read(handle);
        if (identity.Length < 0 || identity.Length > maximumBytes)
            throw new IOException("Browser executable exceeds the supported verification size.");

        using var borrowedHandle = new SafeFileHandle(handle.SafeHandle.DangerousGetHandle(), ownsHandle: false);
        await using var stream = new FileStream(
            borrowedHandle, FileAccess.Read, bufferSize: 64 * 1024, isAsync: true);
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total = checked(total + read);
            if (total > maximumBytes)
                throw new IOException("Browser executable exceeds the supported verification size.");
            hash.AppendData(buffer, 0, read);
        }
        if (total != identity.Length)
            throw new IOException("Browser executable changed during verification.");
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
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

internal sealed class WindowsBrowserProductReader : IBrowserProductReader
{
    public string ReadProductName(IBrowserPinnedHandle handle, string retainedFinalPath)
    {
        if (handle.SafeHandle.IsInvalid || handle.SafeHandle.IsClosed)
            throw new IOException("Browser verification operation failed.");
        var productName = FileVersionInfo.GetVersionInfo(retainedFinalPath).ProductName;
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

    public BrowserSignatureVerification Verify(IBrowserPinnedHandle handle, string retainedFinalPath)
    {
        if (handle.SafeHandle.IsInvalid || handle.SafeHandle.IsClosed)
            throw new IOException("Browser verification operation failed.");

        var pathPointer = Marshal.StringToCoTaskMemUni(retainedFinalPath);
        var fileInfoPointer = IntPtr.Zero;
        var trustDataPointer = IntPtr.Zero;
        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = pathPointer,
                FileHandle = handle.SafeHandle.DangerousGetHandle(),
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
                using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(retainedFinalPath));
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
