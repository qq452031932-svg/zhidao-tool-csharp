using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using BaiduBatchTransfer.Core.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace BaiduBatchTransfer.Infrastructure.Security;

public sealed class FileSystemWebViewProfileStore : IWebViewProfileStore
{
    private readonly FileSystemAccountProfilePathPolicy pathPolicy;
    private readonly IWebViewProfileCloser profileCloser;
    private readonly IProfileDeletionFileSystem fileSystem;

    public FileSystemWebViewProfileStore(
        FileSystemAccountProfilePathPolicy pathPolicy,
        IWebViewProfileCloser profileCloser) :
        this(pathPolicy, profileCloser, new PhysicalProfileDeletionFileSystem())
    {
    }

    internal FileSystemWebViewProfileStore(
        FileSystemAccountProfilePathPolicy pathPolicy,
        IWebViewProfileCloser profileCloser,
        IProfileDeletionFileSystem fileSystem)
    {
        this.pathPolicy = pathPolicy ?? throw new ArgumentNullException(nameof(pathPolicy));
        this.profileCloser = profileCloser ?? throw new ArgumentNullException(nameof(profileCloser));
        this.fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public Task CloseAsync(Guid accountId, CancellationToken cancellationToken) =>
        profileCloser.CloseAsync(accountId, cancellationToken);

    public Task RegisterOwnershipAsync(
        Guid accountId,
        string exactProfilePath,
        Guid ownershipToken,
        CancellationToken cancellationToken)
    {
        Validate(accountId, exactProfilePath, ownershipToken);
        fileSystem.RegisterOwnership(pathPolicy.CanonicalRoot, exactProfilePath, ownershipToken, cancellationToken);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid accountId, string exactProfilePath, CancellationToken cancellationToken)
    {
        Validate(accountId, exactProfilePath, null);
        return fileSystem.DeleteProfileAsync(
            pathPolicy.CanonicalRoot, exactProfilePath, QuarantineName(accountId), null, null, cancellationToken);
    }

    public Task<bool> CommitOwnedAsync(
        Guid accountId,
        string exactProfilePath,
        Guid ownershipToken,
        Func<CancellationToken, Task> commitAction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commitAction);
        Validate(accountId, exactProfilePath, ownershipToken);
        return fileSystem.CommitOwnedAsync(
            pathPolicy.CanonicalRoot, exactProfilePath, ownershipToken, commitAction, cancellationToken);
    }

    public Task<OwnedProfileDeletionStatus> DeleteOwnedAsync(
        Guid accountId,
        string exactProfilePath,
        Guid ownershipToken,
        Func<CancellationToken, Task<bool>> confirmNoActiveAccount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmNoActiveAccount);
        Validate(accountId, exactProfilePath, ownershipToken);
        return fileSystem.DeleteProfileAsync(
            pathPolicy.CanonicalRoot,
            exactProfilePath,
            QuarantineName(accountId),
            ownershipToken,
            confirmNoActiveAccount,
            cancellationToken);
    }

    private void Validate(Guid accountId, string exactProfilePath, Guid? ownershipToken)
    {
        if (ownershipToken == Guid.Empty) throw new ArgumentException("A profile ownership token is required.", nameof(ownershipToken));
        pathPolicy.EnsureSafeAccountPath(accountId, exactProfilePath);
        pathPolicy.EnsureSafeRoot();
    }

    private static string QuarantineName(Guid accountId) => $".delete-{accountId:N}";
}

internal interface IProfileDeletionFileSystem
{
    void RegisterOwnership(
        string canonicalRoot,
        string exactProfilePath,
        Guid ownershipToken,
        CancellationToken cancellationToken);

    Task<bool> CommitOwnedAsync(
        string canonicalRoot,
        string exactProfilePath,
        Guid ownershipToken,
        Func<CancellationToken, Task> commitAction,
        CancellationToken cancellationToken);

    Task<OwnedProfileDeletionStatus> DeleteProfileAsync(
        string canonicalRoot,
        string exactProfilePath,
        string quarantineName,
        Guid? expectedOwnershipToken,
        Func<CancellationToken, Task<bool>>? confirmNoActiveAccount,
        CancellationToken cancellationToken);
}

internal sealed class PhysicalProfileDeletionFileSystem : IProfileDeletionFileSystem
{
    internal const string OwnershipMarkerName = ".bbt-profile-owner";
    private readonly Action<string>? beforeEntryOpen;
    private readonly Func<string, int?>? ancestorOpenError;
    private readonly Func<string, bool>? testOnlyPinnedAncestor;

    public PhysicalProfileDeletionFileSystem(
        Action<string>? beforeEntryOpen = null,
        Func<string, int?>? ancestorOpenError = null,
        Func<string, bool>? testOnlyPinnedAncestor = null)
    {
        this.beforeEntryOpen = beforeEntryOpen;
        this.ancestorOpenError = ancestorOpenError;
        this.testOnlyPinnedAncestor = testOnlyPinnedAncestor;
    }

    public void RegisterOwnership(
        string canonicalRoot,
        string exactProfilePath,
        Guid ownershipToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            RegisterOwnershipManaged(exactProfilePath, ownershipToken);
            return;
        }

        using var ancestors = PinnedDirectoryChain.Open(canonicalRoot, ancestorOpenError, testOnlyPinnedAncestor);
        using var profile = OpenPinned(exactProfilePath, requireDirectory: true);
        if (profile.IsReparsePoint) throw new IOException("A profile ownership marker cannot be written through a link.");

        var markerPath = Path.Combine(exactProfilePath, OwnershipMarkerName);
        var marker = NativeMethods.CreateFileW(
            markerPath,
            NativeMethods.GenericWrite,
            NativeMethods.FileShareRead,
            IntPtr.Zero,
            NativeMethods.CreateNew,
            NativeMethods.FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (marker.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            marker.Dispose();
            if (error == NativeMethods.ErrorFileExists && ReadOwnership(exactProfilePath) == ownershipToken) return;
            throw new IOException("The profile ownership marker could not be created.", new Win32Exception(error));
        }

        using (marker)
        using (var stream = new FileStream(marker, FileAccess.Write, bufferSize: 64, isAsync: false))
        {
            var bytes = Encoding.ASCII.GetBytes(ownershipToken.ToString("D"));
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
    }

    public async Task<bool> CommitOwnedAsync(
        string canonicalRoot,
        string exactProfilePath,
        Guid ownershipToken,
        Func<CancellationToken, Task> commitAction,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            if (ReadOwnershipManaged(exactProfilePath) != ownershipToken) return false;
            await commitAction(cancellationToken).ConfigureAwait(false);
            return true;
        }

        using var ancestors = PinnedDirectoryChain.Open(canonicalRoot, ancestorOpenError, testOnlyPinnedAncestor);
        beforeEntryOpen?.Invoke(exactProfilePath);
        var source = TryOpenPinned(exactProfilePath, requireDirectory: true);
        if (source is null) return false;
        using (source)
        {
            var marker = TryOpenOwnershipMarker(exactProfilePath);
            if (marker is null) return false;
            using (marker)
            {
                if (ReadOwnership(marker) != ownershipToken) return false;
                await commitAction(cancellationToken).ConfigureAwait(false);
                return true;
            }
        }
    }

    public async Task<OwnedProfileDeletionStatus> DeleteProfileAsync(
        string canonicalRoot,
        string exactProfilePath,
        string quarantineName,
        Guid? expectedOwnershipToken,
        Func<CancellationToken, Task<bool>>? confirmNoActiveAccount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return await DeleteProfileManagedAsync(
                canonicalRoot,
                exactProfilePath,
                quarantineName,
                expectedOwnershipToken,
                confirmNoActiveAccount,
                cancellationToken).ConfigureAwait(false);

        using var ancestors = PinnedDirectoryChain.Open(canonicalRoot, ancestorOpenError, testOnlyPinnedAncestor);
        var quarantinePath = Path.Combine(canonicalRoot, quarantineName);
        var recoveryOwnerPath = Path.Combine(canonicalRoot, quarantineName + ".owner");

        // A deterministic account-scoped quarantine makes interrupted deletion discoverable and retryable.
        var recovery = TryOpenPinned(quarantinePath, requireDirectory: false);
        if (recovery is not null)
        {
            using (recovery)
            {
                if (expectedOwnershipToken is not null &&
                    ReadRecoveryOwnership(recoveryOwnerPath) != expectedOwnershipToken)
                    return OwnedProfileDeletionStatus.OwnershipMismatch;
                if (confirmNoActiveAccount is not null &&
                    !await confirmNoActiveAccount(cancellationToken).ConfigureAwait(false))
                    return OwnedProfileDeletionStatus.OwnershipMismatch;
                DeletePinnedTree(quarantinePath, recovery, cancellationToken);
            }
            DeleteRecoveryOwnership(recoveryOwnerPath);
        }
        else DeleteRecoveryOwnership(recoveryOwnerPath);

        beforeEntryOpen?.Invoke(exactProfilePath);
        var source = TryOpenPinned(exactProfilePath, requireDirectory: false);
        if (source is null) return OwnedProfileDeletionStatus.Completed;
        using (source)
        {
            PinnedEntry? ownershipMarker = null;
            try
            {
                if (expectedOwnershipToken is not null)
                {
                    if (!source.IsDirectory || source.IsReparsePoint)
                        return OwnedProfileDeletionStatus.OwnershipMismatch;
                    ownershipMarker = TryOpenOwnershipMarker(exactProfilePath);
                    if (ownershipMarker is null || ReadOwnership(ownershipMarker) != expectedOwnershipToken)
                        return OwnedProfileDeletionStatus.OwnershipMismatch;
                }

                if (confirmNoActiveAccount is not null &&
                    !await confirmNoActiveAccount(cancellationToken).ConfigureAwait(false))
                    return OwnedProfileDeletionStatus.OwnershipMismatch;

                // The pinned source directory still excludes competing commit/delete leases.
                // Release the child marker handle before renaming its parent directory.
                ownershipMarker?.Dispose();
                ownershipMarker = null;
                WriteRecoveryOwnership(recoveryOwnerPath, expectedOwnershipToken);
                Rename(source.Handle, quarantinePath);
                DeletePinnedTree(quarantinePath, source, cancellationToken);
            }
            finally { ownershipMarker?.Dispose(); }
        }
        DeleteRecoveryOwnership(recoveryOwnerPath);

        return OwnedProfileDeletionStatus.Completed;
    }

    private static Guid? ReadOwnership(string directoryPath)
    {
        var marker = TryOpenOwnershipMarker(directoryPath);
        if (marker is null) return null;
        using (marker) return ReadOwnership(marker);
    }

    private static PinnedEntry? TryOpenOwnershipMarker(string directoryPath) =>
        TryOpenPinned(
            Path.Combine(directoryPath, OwnershipMarkerName),
            requireDirectory: false,
            readData: true,
            shareWrite: false);

    private static Guid? ReadOwnership(PinnedEntry marker)
    {
        if (marker.IsDirectory || marker.IsReparsePoint) return null;
        var buffer = new byte[37];
        if (!NativeMethods.ReadFile(marker.Handle, buffer, (uint)buffer.Length, out var length, IntPtr.Zero))
            throw new IOException("The profile ownership marker could not be read.", new Win32Exception(Marshal.GetLastWin32Error()));
        if (length != 36) return null;
        return Guid.TryParseExact(Encoding.ASCII.GetString(buffer, 0, (int)length), "D", out var token) ? token : null;
    }

    private static Guid? ReadRecoveryOwnership(string path)
    {
        var marker = TryOpenPinned(path, requireDirectory: false, readData: true);
        if (marker is null) return null;
        using (marker)
        {
            if (marker.IsDirectory || marker.IsReparsePoint) return null;
            using var stream = new FileStream(marker.Handle, FileAccess.Read, bufferSize: 64, isAsync: false);
            Span<byte> buffer = stackalloc byte[37];
            var length = stream.Read(buffer);
            return length == 36 && stream.ReadByte() == -1 &&
                   Guid.TryParseExact(Encoding.ASCII.GetString(buffer[..length]), "D", out var token)
                ? token
                : null;
        }
    }

    private static void WriteRecoveryOwnership(string path, Guid? ownershipToken)
    {
        var handle = NativeMethods.CreateFileW(
            path,
            NativeMethods.GenericWrite | NativeMethods.Delete,
            NativeMethods.FileShareRead,
            IntPtr.Zero,
            NativeMethods.CreateNew,
            NativeMethods.FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("The quarantine recovery marker could not be created.", new Win32Exception(error));
        }

        using (handle)
        using (var stream = new FileStream(handle, FileAccess.Write, bufferSize: 64, isAsync: false))
        {
            var text = ownershipToken?.ToString("D") ?? "unowned";
            var bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
    }

    private static void DeleteRecoveryOwnership(string path)
    {
        var marker = TryOpenPinned(path, requireDirectory: false);
        if (marker is null) return;
        using (marker) MarkDelete(marker.Handle);
    }

    private void DeletePinnedTree(string path, PinnedEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.IsDirectory && !entry.IsReparsePoint)
        {
            foreach (var childPath in Directory.EnumerateFileSystemEntries(path).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                beforeEntryOpen?.Invoke(childPath);
                var child = TryOpenPinned(childPath, requireDirectory: false);
                if (child is null) continue;
                using (child) DeletePinnedTree(childPath, child, cancellationToken);
            }
        }

        MarkDelete(entry.Handle);
    }

    private static PinnedEntry OpenPinned(
        string path,
        bool requireDirectory,
        bool allowDelete = true,
        bool readDirectory = false) =>
        TryOpenPinned(path, requireDirectory, allowDelete: allowDelete, readDirectory: readDirectory) ??
        throw new DirectoryNotFoundException("The profile path no longer exists.");

    private static PinnedEntry? TryOpenPinned(
        string path,
        bool requireDirectory,
        bool readData = false,
        bool allowDelete = true,
        bool readDirectory = false,
        bool shareWrite = true)
    {
        var access = allowDelete || readData ? NativeMethods.FileReadAttributes : 0u;
        if (allowDelete) access |= NativeMethods.Delete;
        if (readData) access |= NativeMethods.GenericRead;
        if (readDirectory) access |= NativeMethods.FileListDirectory;
        var handle = NativeMethods.CreateFileW(
            path,
            access,
            NativeMethods.FileShareRead | (shareWrite ? NativeMethods.FileShareWrite : 0u),
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is NativeMethods.ErrorFileNotFound or NativeMethods.ErrorPathNotFound) return null;
            throw new IOException("The profile entry could not be opened safely.", new Win32Exception(error));
        }

        var information = GetInformation(handle);
        var entry = new PinnedEntry(handle, information);
        if (requireDirectory && (!entry.IsDirectory || entry.IsReparsePoint))
        {
            entry.Dispose();
            throw new IOException("The profile path is not a normal directory.");
        }

        return entry;
    }

    private static NativeMethods.ByHandleFileInformation GetInformation(SafeFileHandle handle)
    {
        if (!NativeMethods.GetFileInformationByHandle(handle, out var information))
            throw new IOException(
                "The profile entry could not be inspected safely.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        return information;
    }

    private static void Rename(SafeFileHandle source, string destinationPath)
    {
        var nativeDestination = destinationPath.StartsWith(@"\??\", StringComparison.Ordinal)
            ? destinationPath
            : @"\??\" + destinationPath;
        var nameBytes = Encoding.Unicode.GetBytes(nativeDestination);
        var nameOffset = Marshal.OffsetOf<NativeMethods.FileRenameInformation>(
            nameof(NativeMethods.FileRenameInformation.FileName)).ToInt32();
        var bufferSize = Math.Max(
            Marshal.SizeOf<NativeMethods.FileRenameInformation>() + nameBytes.Length,
            nameOffset + nameBytes.Length + sizeof(char));
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            for (var index = 0; index < bufferSize; index++) Marshal.WriteByte(buffer, index, 0);
            Marshal.WriteInt32(buffer, 0, 0);
            Marshal.WriteIntPtr(buffer, Marshal.OffsetOf<NativeMethods.FileRenameInformation>(
                nameof(NativeMethods.FileRenameInformation.RootDirectory)).ToInt32(), IntPtr.Zero);
            Marshal.WriteInt32(buffer, Marshal.OffsetOf<NativeMethods.FileRenameInformation>(
                nameof(NativeMethods.FileRenameInformation.FileNameLength)).ToInt32(), nameBytes.Length);
            Marshal.Copy(nameBytes, 0, IntPtr.Add(buffer, nameOffset), nameBytes.Length);
            if (!NativeMethods.SetFileInformationByHandle(
                    source, NativeMethods.FileInfoByHandleClass.FileRenameInfo, buffer, (uint)bufferSize))
                throw new IOException(
                    "The profile could not be quarantined safely.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void MarkDelete(SafeFileHandle handle)
    {
        var disposition = new NativeMethods.FileDispositionInformation { DeleteFile = true };
        var size = Marshal.SizeOf<NativeMethods.FileDispositionInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(disposition, buffer, fDeleteOld: false);
            if (!NativeMethods.SetFileInformationByHandle(
                    handle, NativeMethods.FileInfoByHandleClass.FileDispositionInfo, buffer, (uint)size))
                throw new IOException(
                    "The quarantined profile entry could not be deleted.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void RegisterOwnershipManaged(string profilePath, Guid token)
    {
        var marker = Path.Combine(profilePath, OwnershipMarkerName);
        if (File.Exists(marker) &&
            Guid.TryParseExact(File.ReadAllText(marker), "D", out var existing) &&
            existing == token)
            return;
        using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var bytes = Encoding.ASCII.GetBytes(token.ToString("D"));
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static Guid? ReadOwnershipManaged(string profilePath)
    {
        var marker = Path.Combine(profilePath, OwnershipMarkerName);
        return File.Exists(marker) && Guid.TryParseExact(File.ReadAllText(marker), "D", out var token) ? token : null;
    }

    private static async Task<OwnedProfileDeletionStatus> DeleteProfileManagedAsync(
        string root,
        string source,
        string quarantineName,
        Guid? expected,
        Func<CancellationToken, Task<bool>>? confirmNoActiveAccount,
        CancellationToken cancellationToken)
    {
        var quarantine = Path.Combine(root, quarantineName);
        var recoveryOwner = Path.Combine(root, quarantineName + ".owner");
        if (Directory.Exists(quarantine))
        {
            if (expected is not null &&
                (!File.Exists(recoveryOwner) ||
                 !Guid.TryParseExact(File.ReadAllText(recoveryOwner), "D", out var recovered) ||
                 recovered != expected))
                return OwnedProfileDeletionStatus.OwnershipMismatch;
            if (confirmNoActiveAccount is not null &&
                !await confirmNoActiveAccount(cancellationToken).ConfigureAwait(false))
                return OwnedProfileDeletionStatus.OwnershipMismatch;
            DeleteManagedTree(quarantine, cancellationToken);
            File.Delete(recoveryOwner);
        }
        else File.Delete(recoveryOwner);
        if (!Directory.Exists(source)) return OwnedProfileDeletionStatus.Completed;
        if (expected is not null)
        {
            if (ReadOwnershipManaged(source) != expected)
                return OwnedProfileDeletionStatus.OwnershipMismatch;
        }
        if (confirmNoActiveAccount is not null &&
            !await confirmNoActiveAccount(cancellationToken).ConfigureAwait(false))
            return OwnedProfileDeletionStatus.OwnershipMismatch;
        File.WriteAllText(recoveryOwner, expected?.ToString("D") ?? "unowned", Encoding.ASCII);
        Directory.Move(source, quarantine);
        DeleteManagedTree(quarantine, cancellationToken);
        File.Delete(recoveryOwner);
        return OwnedProfileDeletionStatus.Completed;
    }

    private static void DeleteManagedTree(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
        {
            if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path, false);
            else File.Delete(path);
            return;
        }
        foreach (var child in Directory.EnumerateFileSystemEntries(path).ToArray()) DeleteManagedTree(child, cancellationToken);
        Directory.Delete(path, false);
    }

    private sealed class PinnedEntry(
        SafeFileHandle handle,
        NativeMethods.ByHandleFileInformation information) : IDisposable
    {
        public SafeFileHandle Handle { get; } = handle;
        public bool IsDirectory => (information.FileAttributes & FileAttributes.Directory) != 0;
        public bool IsReparsePoint => (information.FileAttributes & FileAttributes.ReparsePoint) != 0;
        public void Dispose() => Handle.Dispose();
    }

    private sealed class PinnedDirectoryChain : IDisposable
    {
        private readonly List<PinnedEntry> entries;
        private PinnedDirectoryChain(List<PinnedEntry> entries) => this.entries = entries;
        public static PinnedDirectoryChain Open(
            string canonicalRoot,
            Func<string, int?>? injectedOpenError,
            Func<string, bool>? testOnlyPinnedAncestor)
        {
            var root = Path.GetPathRoot(canonicalRoot) ?? throw new IOException("The profile root is not fully qualified.");
            var paths = new List<string> { root };
            var current = root;
            var relative = Path.GetRelativePath(root, canonicalRoot);
            if (relative != ".")
            {
                foreach (var component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, component);
                    paths.Add(current);
                }
            }

            var entries = new List<PinnedEntry>(paths.Count);
            try
            {
                for (var index = 0; index < paths.Count; index++)
                {
                    if (injectedOpenError?.Invoke(paths[index]) is { } error)
                        throw new IOException(
                            "A profile ancestor could not be pinned safely.",
                            new Win32Exception(error));
                    if (testOnlyPinnedAncestor?.Invoke(paths[index]) == true) continue;
                    entries.Add(OpenPinned(
                        paths[index],
                        requireDirectory: true,
                        allowDelete: false,
                        readDirectory: index == paths.Count - 1));
                }
                return new PinnedDirectoryChain(entries);
            }
            catch
            {
                foreach (var entry in entries) entry.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            for (var index = entries.Count - 1; index >= 0; index--) entries[index].Dispose();
        }
    }

    private static class NativeMethods
    {
        internal const uint GenericRead = 0x80000000;
        internal const uint GenericWrite = 0x40000000;
        internal const uint Delete = 0x00010000;
        internal const uint FileReadAttributes = 0x00000080;
        internal const uint FileListDirectory = 0x00000001;
        internal const uint FileShareRead = 0x00000001;
        internal const uint FileShareWrite = 0x00000002;
        internal const uint OpenExisting = 3;
        internal const uint CreateNew = 1;
        internal const uint FileFlagBackupSemantics = 0x02000000;
        internal const uint FileFlagOpenReparsePoint = 0x00200000;
        internal const int ErrorFileNotFound = 2;
        internal const int ErrorPathNotFound = 3;
        internal const int ErrorFileExists = 80;

        internal enum FileInfoByHandleClass
        {
            FileRenameInfo = 3,
            FileDispositionInfo = 4
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileDispositionInformation
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool DeleteFile;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct FileRenameInformation
        {
            [MarshalAs(UnmanagedType.Bool)] public bool ReplaceIfExists;
            public IntPtr RootDirectory;
            public uint FileNameLength;
            public char FileName;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileTime
        {
            public uint LowDateTime;
            public uint HighDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ByHandleFileInformation
        {
            public FileAttributes FileAttributes;
            public FileTime CreationTime;
            public FileTime LastAccessTime;
            public FileTime LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation fileInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReadFile(
            SafeFileHandle file,
            byte[] buffer,
            uint bytesToRead,
            out uint bytesRead,
            IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFileInformationByHandle(
            SafeFileHandle file,
            FileInfoByHandleClass fileInformationClass,
            IntPtr fileInformation,
            uint bufferSize);
    }
}
