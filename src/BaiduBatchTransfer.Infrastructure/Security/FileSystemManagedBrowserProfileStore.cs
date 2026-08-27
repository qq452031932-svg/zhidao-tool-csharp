using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using Microsoft.Win32.SafeHandles;

namespace BaiduBatchTransfer.Infrastructure.Security;

public sealed class FileSystemManagedBrowserProfileStore : IManagedBrowserProfileStore
{
    private const string ManagedRootName = "managed-browser-profiles";
    private const string QuarantineRootName = "managed-browser-quarantine";
    private const int MaximumMarkerBytes = 512;
    private readonly string appDataRoot;
    private readonly IManagedBrowserProfilePolicy policy;
    private readonly Action<ManagedBrowserProfileCleanupPreparation>? afterDeleteTargetPinned;

    public FileSystemManagedBrowserProfileStore(
        string appDataRoot,
        IManagedBrowserProfilePolicy policy)
        : this(appDataRoot, policy, null)
    {
    }

    internal FileSystemManagedBrowserProfileStore(
        string appDataRoot,
        IManagedBrowserProfilePolicy policy,
        Action<ManagedBrowserProfileCleanupPreparation>? afterDeleteTargetPinned)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataRoot);
        this.appDataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDataRoot));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
        this.afterDeleteTargetPinned = afterDeleteTargetPinned;
    }

    public ManagedBrowserProfileCleanupPreparation PrepareRemoval(ManagedBrowserLifecycleSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        EnsureExactSource(session);
        var manifestId = DeterministicManifestId(session);
        var quarantine = Path.Combine(appDataRoot, QuarantineRootName, manifestId.ToString("N"));
        return new ManagedBrowserProfileCleanupPreparation(session, manifestId, 0, quarantine);
    }

    public async Task<ManagedBrowserProfileLocationStatus> ObserveAsync(
        ManagedBrowserProfileCleanupPreparation preparation,
        CancellationToken cancellationToken)
    {
        ValidatePreparation(preparation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return ManagedBrowserProfileLocationStatus.IsolationRequired;

        try
        {
            using var source = TryOpenDirectory(preparation.SourceProfilePath, allowDelete: false);
            using var destination = TryOpenDirectory(preparation.QuarantinePath, allowDelete: false);
            if (source is not null && !await ValidateSourceAsync(preparation, cancellationToken).ConfigureAwait(false))
                return ManagedBrowserProfileLocationStatus.IsolationRequired;
            if (source is not null && destination is not null)
                return ManagedBrowserProfileLocationStatus.BothPresent;
            if (destination is not null && !ValidatePinnedProfile(destination, preparation.Session))
                return ManagedBrowserProfileLocationStatus.IsolationRequired;
            return (source is not null, destination is not null) switch
            {
                (true, false) => ManagedBrowserProfileLocationStatus.SourceOnly,
                (false, true) => ManagedBrowserProfileLocationStatus.QuarantineOnly,
                (true, true) => ManagedBrowserProfileLocationStatus.BothPresent,
                _ => ManagedBrowserProfileLocationStatus.NeitherPresent,
            };
        }
        catch (IOException)
        {
            return ManagedBrowserProfileLocationStatus.IsolationRequired;
        }
    }

    public async Task<ManagedBrowserProfileMoveStatus> MoveToQuarantineAsync(
        ManagedBrowserProfileCleanupPreparation preparation,
        CancellationToken cancellationToken)
    {
        ValidatePreparation(preparation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return ManagedBrowserProfileMoveStatus.IsolationRequired;
        var observed = await ObserveAsync(preparation, cancellationToken).ConfigureAwait(false);
        if (observed == ManagedBrowserProfileLocationStatus.QuarantineOnly)
            return ManagedBrowserProfileMoveStatus.AlreadyQuarantined;
        if (observed == ManagedBrowserProfileLocationStatus.IsolationRequired)
            return ManagedBrowserProfileMoveStatus.IsolationRequired;
        if (observed != ManagedBrowserProfileLocationStatus.SourceOnly)
            return ManagedBrowserProfileMoveStatus.Conflict;

        var quarantineRootPath = Path.Combine(appDataRoot, QuarantineRootName);
        EnsureCurrentUserOnlyDirectory(quarantineRootPath);
        using var quarantineRoot = OpenDirectory(quarantineRootPath, allowDelete: false);
        using var source = OpenDirectory(preparation.SourceProfilePath, allowDelete: true);
        if (!ValidatePinnedProfile(source, preparation.Session))
            return ManagedBrowserProfileMoveStatus.IsolationRequired;
        cancellationToken.ThrowIfCancellationRequested();
        RenameRelative(source.Handle, quarantineRoot.Handle, preparation.ManifestId.ToString("N"));

        using var moved = TryOpenRelativeDirectory(
            quarantineRoot,
            preparation.ManifestId.ToString("N"),
            allowDelete: false);
        return moved is not null && ValidatePinnedProfile(moved, preparation.Session)
            ? ManagedBrowserProfileMoveStatus.Moved
            : ManagedBrowserProfileMoveStatus.IsolationRequired;
    }

    public Task<ManagedBrowserProfileDeleteStatus> DeleteQuarantinedAsync(
        ManagedBrowserProfileCleanupPreparation preparation,
        string confirmationTokenHash,
        CancellationToken cancellationToken)
    {
        ValidatePreparation(preparation);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationTokenHash);
        if (confirmationTokenHash != confirmationTokenHash.Trim() || confirmationTokenHash.Length > 512 ||
            confirmationTokenHash.Any(char.IsControl))
            throw new ArgumentException("Confirmation evidence is invalid.", nameof(confirmationTokenHash));
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(ManagedBrowserProfileDeleteStatus.IsolationRequired);

        var quarantineRootPath = Path.Combine(appDataRoot, QuarantineRootName);
        PinnedEntry? quarantineRoot = null;
        PinnedEntry? quarantined = null;
        try
        {
            quarantineRoot = TryOpenDirectory(
                quarantineRootPath,
                allowDelete: false,
                protectFromRename: true);
            if (quarantineRoot is null)
                return Task.FromResult(ManagedBrowserProfileDeleteStatus.AlreadyDeleted);
            quarantined = TryOpenRelativeDirectory(
                quarantineRoot,
                preparation.ManifestId.ToString("N"),
                allowDelete: true,
                protectFromRename: true,
                listDirectory: true);
        }
        catch (IOException)
        {
            quarantineRoot?.Dispose();
            return Task.FromResult(ManagedBrowserProfileDeleteStatus.IsolationRequired);
        }
        using (quarantineRoot)
        using (quarantined)
        {
            if (quarantined is null)
                return Task.FromResult(ManagedBrowserProfileDeleteStatus.AlreadyDeleted);
            if (!ValidatePinnedProfile(quarantined, preparation.Session))
                return Task.FromResult(ManagedBrowserProfileDeleteStatus.IsolationRequired);

            try
            {
                afterDeleteTargetPinned?.Invoke(preparation);
                DeletePinnedTree(quarantined, cancellationToken);
                return Task.FromResult(ManagedBrowserProfileDeleteStatus.Deleted);
            }
            catch (IOException)
            {
                return Task.FromResult(ManagedBrowserProfileDeleteStatus.Conflict);
            }
        }
    }

    private async Task<bool> ValidateSourceAsync(
        ManagedBrowserProfileCleanupPreparation preparation,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(preparation.Session.ProfileId, "N", out var profileId) ||
            !Guid.TryParseExact(preparation.Session.ProfileOwnershipId, "N", out var ownershipId))
            return false;
        var expected = new ManagedBrowserProfileDescriptor(
            preparation.Session.AccountId,
            preparation.Session.BrowserFamily,
            profileId,
            ownershipId,
            preparation.SourceProfilePath);
        var result = await policy.OpenExistingAsync(expected, cancellationToken).ConfigureAwait(false);
        return result.Status == ManagedBrowserProfileStatus.Ready;
    }

    private void ValidatePreparation(ManagedBrowserProfileCleanupPreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        EnsureExactSource(preparation.Session);
        var expectedManifest = DeterministicManifestId(preparation.Session);
        var expectedQuarantine = Path.Combine(appDataRoot, QuarantineRootName, expectedManifest.ToString("N"));
        if (preparation.ManifestId != expectedManifest ||
            !string.Equals(preparation.QuarantinePath, expectedQuarantine, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Managed profile cleanup identity is not canonical.");
    }

    private void EnsureExactSource(ManagedBrowserLifecycleSession session)
    {
        var family = session.BrowserFamily switch
        {
            BrowserFamily.Chrome => "chrome",
            BrowserFamily.Edge => "edge",
            _ => throw new InvalidOperationException("A managed browser family is required."),
        };
        var expected = Path.Combine(appDataRoot, ManagedRootName, family, session.AccountId.ToString("N"));
        if (!string.Equals(expected, session.CanonicalProfilePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The managed profile source is outside its exact account namespace.");
    }

    private static Guid DeterministicManifestId(ManagedBrowserLifecycleSession session)
    {
        var text = string.Join('\n',
            "bbt-managed-profile-removal-v1",
            session.SessionId.ToString("N"),
            session.InstallationId.ToString("N"),
            session.AccountId.ToString("N"),
            session.BrowserFamily.ToString(),
            session.ProfileId,
            session.ProfileOwnershipId);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes.AsSpan(0, 16));
    }

    [SupportedOSPlatform("windows")]
    private static bool ValidatePinnedProfile(PinnedEntry directory, ManagedBrowserLifecycleSession session)
    {
        if (!directory.IsDirectory || directory.IsReparsePoint || !HasCurrentUserOnlyAcl(directory.Handle))
            return false;
        using var marker = TryOpenRelativeFile(directory, FileSystemManagedBrowserProfilePolicy.MarkerName);
        if (marker is null || marker.IsDirectory || marker.IsReparsePoint || !HasCurrentUserOnlyAcl(marker.Handle))
            return false;
        var text = ReadMarker(marker.Handle);
        var expected =
            $"bbt-managed-browser-profile:1\naccount-id={session.AccountId:N}\n" +
            $"browser-family={session.BrowserFamily}\nprofile-id={session.ProfileId}\n" +
            $"profile-ownership-id={session.ProfileOwnershipId}\n";
        return string.Equals(text, expected, StringComparison.Ordinal);
    }

    private static string ReadMarker(SafeFileHandle handle)
    {
        var buffer = new byte[MaximumMarkerBytes + 1];
        if (!NativeMethods.ReadFile(handle, buffer, (uint)buffer.Length, out var read, IntPtr.Zero) ||
            read > MaximumMarkerBytes)
            return string.Empty;
        return Encoding.ASCII.GetString(buffer, 0, checked((int)read));
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureCurrentUserOnlyDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            using var existing = OpenDirectory(path, allowDelete: false);
            if (!HasCurrentUserOnlyAcl(existing.Handle))
                throw new IOException("The managed profile quarantine namespace is not private.");
            return;
        }

        var parent = Path.GetDirectoryName(path) ?? throw new IOException("The quarantine root is invalid.");
        using var parentHandle = OpenDirectory(parent, allowDelete: false);
        using var descriptor = SecurityDescriptorBuffer.CreateDirectory();
        using var relative = new RelativeName(Path.GetFileName(path));
        var attributes = new NativeMethods.ObjectAttributes
        {
            Length = Marshal.SizeOf<NativeMethods.ObjectAttributes>(),
            RootDirectory = parentHandle.Handle.DangerousGetHandle(),
            ObjectName = relative.UnicodeStringPointer,
            Attributes = NativeMethods.ObjCaseInsensitive,
            SecurityDescriptor = descriptor.Pointer,
        };
        var status = NativeMethods.NtCreateFile(
            out var handle,
            NativeMethods.FileReadAttributes | NativeMethods.FileTraverse | NativeMethods.ReadControl |
            NativeMethods.Synchronize,
            ref attributes,
            out _,
            IntPtr.Zero,
            NativeMethods.FileAttributeDirectory,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
            NativeMethods.FileOpenIf,
            NativeMethods.FileDirectoryFile | NativeMethods.FileSynchronousIoNonAlert |
            NativeMethods.FileOpenReparsePoint,
            IntPtr.Zero,
            0);
        if (status < 0)
        {
            handle?.Dispose();
            throw new IOException("The managed profile quarantine namespace could not be created.");
        }
        using var created = Inspect(handle, requireDirectory: true);
        if (!HasCurrentUserOnlyAcl(created.Handle))
            throw new IOException("The managed profile quarantine namespace is not private.");
    }

    private static PinnedEntry OpenDirectory(string path, bool allowDelete) =>
        TryOpenDirectory(path, allowDelete) ?? throw new DirectoryNotFoundException();

    private static PinnedEntry? TryOpenDirectory(
        string path,
        bool allowDelete,
        bool protectFromRename = false)
    {
        var access = NativeMethods.FileReadAttributes | NativeMethods.ReadControl |
            (allowDelete ? NativeMethods.Delete : 0);
        var handle = NativeMethods.CreateFileW(
            path,
            access,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite |
            (protectFromRename ? 0 : NativeMethods.FileShareDelete),
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is NativeMethods.ErrorFileNotFound or NativeMethods.ErrorPathNotFound) return null;
            throw new IOException("The managed profile entry could not be pinned.", new Win32Exception(error));
        }
        return Inspect(handle, requireDirectory: true);
    }

    private static PinnedEntry? TryOpenRelativeDirectory(
        PinnedEntry parent,
        string name,
        bool allowDelete,
        bool protectFromRename = false,
        bool listDirectory = false) =>
        OpenRelative(parent, name,
            NativeMethods.FileReadAttributes | NativeMethods.ReadControl | NativeMethods.Synchronize |
            (allowDelete ? NativeMethods.Delete : 0) |
            (listDirectory ? NativeMethods.FileListDirectory : 0),
            NativeMethods.FileDirectoryFile | NativeMethods.FileSynchronousIoNonAlert |
            NativeMethods.FileOpenReparsePoint,
            requireDirectory: true,
            shareAccess: NativeMethods.FileShareRead | NativeMethods.FileShareWrite |
            (protectFromRename ? 0 : NativeMethods.FileShareDelete));

    private static PinnedEntry? TryOpenRelativeFile(PinnedEntry parent, string name) =>
        OpenRelative(parent, name,
            NativeMethods.GenericRead | NativeMethods.ReadControl | NativeMethods.Synchronize,
            NativeMethods.FileNonDirectoryFile | NativeMethods.FileSynchronousIoNonAlert |
            NativeMethods.FileOpenReparsePoint,
            requireDirectory: false,
            shareAccess: NativeMethods.FileShareRead);

    private static PinnedEntry? OpenRelative(
        PinnedEntry parent,
        string name,
        uint access,
        uint options,
        bool? requireDirectory,
        uint shareAccess = NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete)
    {
        ValidateRelativeName(name);
        using var relative = new RelativeName(name);
        var attributes = new NativeMethods.ObjectAttributes
        {
            Length = Marshal.SizeOf<NativeMethods.ObjectAttributes>(),
            RootDirectory = parent.Handle.DangerousGetHandle(),
            ObjectName = relative.UnicodeStringPointer,
            Attributes = NativeMethods.ObjCaseInsensitive,
        };
        var status = NativeMethods.NtCreateFile(
            out var handle, access, ref attributes, out _, IntPtr.Zero,
            requireDirectory == true ? NativeMethods.FileAttributeDirectory : NativeMethods.FileAttributeNormal,
            shareAccess, NativeMethods.FileOpen, options, IntPtr.Zero, 0);
        if (status < 0)
        {
            handle?.Dispose();
            if (status is NativeMethods.StatusObjectNameNotFound or NativeMethods.StatusObjectPathNotFound)
                return null;
            throw new IOException("The managed profile entry could not be opened safely.");
        }
        return Inspect(handle, requireDirectory);
    }

    private static PinnedEntry Inspect(SafeFileHandle handle, bool? requireDirectory)
    {
        if (!NativeMethods.GetFileInformationByHandle(handle, out var information))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("The managed profile entry could not be inspected.", new Win32Exception(error));
        }
        var entry = new PinnedEntry(handle, information.FileAttributes);
        if (entry.IsReparsePoint || (requireDirectory.HasValue && entry.IsDirectory != requireDirectory.Value))
        {
            entry.Dispose();
            throw new IOException("The managed profile entry is not a normal filesystem object.");
        }
        return entry;
    }

    private static void RenameRelative(SafeFileHandle source, SafeFileHandle destinationRoot, string destinationName)
    {
        ValidateRelativeName(destinationName);
        var bytes = Encoding.Unicode.GetBytes(destinationName);
        var offset = Marshal.OffsetOf<NativeMethods.FileRenameInformation>(
            nameof(NativeMethods.FileRenameInformation.FileName)).ToInt32();
        var size = Math.Max(Marshal.SizeOf<NativeMethods.FileRenameInformation>() + bytes.Length,
            offset + bytes.Length + sizeof(char));
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (var index = 0; index < size; index++) Marshal.WriteByte(buffer, index, 0);
            Marshal.WriteInt32(buffer, 0, 0);
            Marshal.WriteIntPtr(buffer, Marshal.OffsetOf<NativeMethods.FileRenameInformation>(
                nameof(NativeMethods.FileRenameInformation.RootDirectory)).ToInt32(),
                destinationRoot.DangerousGetHandle());
            Marshal.WriteInt32(buffer, Marshal.OffsetOf<NativeMethods.FileRenameInformation>(
                nameof(NativeMethods.FileRenameInformation.FileNameLength)).ToInt32(), bytes.Length);
            Marshal.Copy(bytes, 0, IntPtr.Add(buffer, offset), bytes.Length);
            var status = NativeMethods.NtSetInformationFile(
                source,
                out _,
                buffer,
                (uint)size,
                NativeMethods.FileRenameInformationClass);
            if (status < 0)
                throw new IOException("The managed profile could not be quarantined safely.",
                    new Win32Exception(status));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void DeletePinnedTree(PinnedEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.IsDirectory && !entry.IsReparsePoint)
        {
            foreach (var childName in EnumerateRelativeNames(entry))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var child = TryOpenRelativeEntry(entry, childName) ??
                    throw new IOException("A quarantined profile entry changed during cleanup.");
                DeletePinnedTree(child, cancellationToken);
            }
        }
        MarkDelete(entry.Handle);
    }

    private static IReadOnlyList<string> EnumerateRelativeNames(PinnedEntry directory)
    {
        const int bufferSize = 64 * 1024;
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            var names = new List<string>();
            var restart = true;
            while (true)
            {
                var status = NativeMethods.NtQueryDirectoryFile(
                    directory.Handle,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    out var io,
                    buffer,
                    bufferSize,
                    NativeMethods.FileNamesInformationClass,
                    returnSingleEntry: 0,
                    IntPtr.Zero,
                    restartScan: restart ? (byte)1 : (byte)0);
                restart = false;
                if (status == NativeMethods.StatusNoMoreFiles) break;
                if (status < 0 && status != NativeMethods.StatusBufferOverflow)
                    throw new IOException("A quarantined profile directory could not be enumerated safely.");

                var returned = checked((int)io.Information.ToUInt64());
                if (returned <= 0 || returned > bufferSize)
                    throw new IOException("A quarantined profile directory returned invalid enumeration data.");
                var offset = 0;
                while (true)
                {
                    if (offset < 0 || offset > returned - NativeMethods.FileNamesInformationHeaderSize)
                        throw new IOException("A quarantined profile directory returned invalid entry data.");
                    var next = Marshal.ReadInt32(buffer, offset);
                    var nameBytes = Marshal.ReadInt32(buffer, offset + 8);
                    if (nameBytes < 0 || (nameBytes & 1) != 0 ||
                        nameBytes > returned - offset - NativeMethods.FileNamesInformationHeaderSize)
                        throw new IOException("A quarantined profile directory returned an invalid name.");
                    var name = Marshal.PtrToStringUni(
                        IntPtr.Add(buffer, offset + NativeMethods.FileNamesInformationHeaderSize),
                        nameBytes / sizeof(char)) ?? string.Empty;
                    if (name is not ("." or ".."))
                    {
                        ValidateRelativeName(name);
                        names.Add(name);
                    }
                    if (next == 0) break;
                    if (next < NativeMethods.FileNamesInformationHeaderSize || next > returned - offset)
                        throw new IOException("A quarantined profile directory returned an invalid continuation.");
                    offset += next;
                }
            }
            return names;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static PinnedEntry? TryOpenRelativeEntry(PinnedEntry parent, string name) =>
        OpenRelative(
            parent,
            name,
            NativeMethods.FileListDirectory | NativeMethods.FileReadAttributes |
            NativeMethods.Delete | NativeMethods.Synchronize,
            NativeMethods.FileSynchronousIoNonAlert | NativeMethods.FileOpenReparsePoint,
            requireDirectory: null,
            shareAccess: NativeMethods.FileShareRead | NativeMethods.FileShareWrite);

    private static void MarkDelete(SafeFileHandle handle)
    {
        var disposition = new NativeMethods.FileDispositionInformation { DeleteFile = true };
        var size = Marshal.SizeOf<NativeMethods.FileDispositionInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(disposition, buffer, false);
            if (!NativeMethods.SetFileInformationByHandle(
                    handle,
                    NativeMethods.FileInfoByHandleClass.FileDispositionInfo,
                    buffer,
                    (uint)size))
                throw new IOException("A quarantined profile entry could not be deleted.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [SupportedOSPlatform("windows")]
    private static bool HasCurrentUserOnlyAcl(SafeFileHandle handle)
    {
        var result = NativeMethods.GetSecurityInfo(
            handle,
            NativeMethods.SeFileObject,
            NativeMethods.OwnerSecurityInformation | NativeMethods.DaclSecurityInformation,
            out _, out _, out _, out _, out var pointer);
        if (result != 0 || pointer == IntPtr.Zero) return false;
        try
        {
            var bytes = new byte[checked((int)NativeMethods.GetSecurityDescriptorLength(pointer))];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            var descriptor = new RawSecurityDescriptor(bytes, 0);
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User;
            if (user is null || descriptor.Owner != user ||
                (descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0 ||
                descriptor.DiscretionaryAcl is null || descriptor.DiscretionaryAcl.Count == 0)
                return false;
            foreach (GenericAce generic in descriptor.DiscretionaryAcl)
            {
                if (generic is not CommonAce ace || ace.AceQualifier != AceQualifier.AccessAllowed ||
                    ace.SecurityIdentifier != user ||
                    (ace.AccessMask & NativeMethods.FileAllAccess) != NativeMethods.FileAllAccess)
                    return false;
            }
            return true;
        }
        finally { NativeMethods.LocalFree(pointer); }
    }

    private static void ValidateRelativeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Any(char.IsControl) ||
            name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar) ||
            name.Contains(':'))
            throw new InvalidOperationException("The managed profile relative name is invalid.");
    }

    private sealed class PinnedEntry(SafeFileHandle handle, FileAttributes attributes) : IDisposable
    {
        public SafeFileHandle Handle { get; } = handle;
        public bool IsDirectory => (attributes & FileAttributes.Directory) != 0;
        public bool IsReparsePoint => (attributes & FileAttributes.ReparsePoint) != 0;
        public void Dispose() => Handle.Dispose();
    }

    private sealed class RelativeName : IDisposable
    {
        private readonly IntPtr text;
        public RelativeName(string name)
        {
            text = Marshal.StringToHGlobalUni(name);
            var unicode = new NativeMethods.UnicodeString
            {
                Length = checked((ushort)(name.Length * 2)),
                MaximumLength = checked((ushort)((name.Length + 1) * 2)),
                Buffer = text,
            };
            UnicodeStringPointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.UnicodeString>());
            Marshal.StructureToPtr(unicode, UnicodeStringPointer, false);
        }
        public IntPtr UnicodeStringPointer { get; }
        public void Dispose()
        {
            Marshal.FreeHGlobal(UnicodeStringPointer);
            Marshal.FreeHGlobal(text);
        }
    }

    private sealed class SecurityDescriptorBuffer : IDisposable
    {
        private readonly GCHandle pinned;
        private SecurityDescriptorBuffer(byte[] bytes)
        {
            pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            Pointer = pinned.AddrOfPinnedObject();
        }
        public IntPtr Pointer { get; }
        [SupportedOSPlatform("windows")]
        public static SecurityDescriptorBuffer CreateDirectory()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new IOException("The current Windows identity is unavailable.");
            var dacl = new RawAcl(GenericAcl.AclRevision, 1);
            dacl.InsertAce(0, new CommonAce(
                AceFlags.ContainerInherit | AceFlags.ObjectInherit,
                AceQualifier.AccessAllowed,
                NativeMethods.FileAllAccess,
                user,
                false,
                null));
            var descriptor = new RawSecurityDescriptor(
                ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected,
                user,
                null,
                null,
                dacl);
            var bytes = new byte[descriptor.BinaryLength];
            descriptor.GetBinaryForm(bytes, 0);
            return new SecurityDescriptorBuffer(bytes);
        }
        public void Dispose()
        {
            if (pinned.IsAllocated) pinned.Free();
        }
    }

    private static class NativeMethods
    {
        internal const uint GenericRead = 0x80000000;
        internal const uint Delete = 0x00010000;
        internal const uint ReadControl = 0x00020000;
        internal const uint Synchronize = 0x00100000;
        internal const uint FileListDirectory = 0x00000001;
        internal const uint FileReadAttributes = 0x00000080;
        internal const uint FileTraverse = 0x00000020;
        internal const uint FileShareRead = 1;
        internal const uint FileShareWrite = 2;
        internal const uint FileShareDelete = 4;
        internal const uint OpenExisting = 3;
        internal const uint FileFlagBackupSemantics = 0x02000000;
        internal const uint FileFlagOpenReparsePoint = 0x00200000;
        internal const int ErrorFileNotFound = 2;
        internal const int ErrorPathNotFound = 3;
        internal const uint FileAttributeNormal = 0x00000080;
        internal const uint FileAttributeDirectory = 0x00000010;
        internal const uint FileOpen = 1;
        internal const uint FileOpenIf = 3;
        internal const uint FileDirectoryFile = 0x00000001;
        internal const uint FileNonDirectoryFile = 0x00000040;
        internal const uint FileSynchronousIoNonAlert = 0x00000020;
        internal const uint FileOpenReparsePoint = 0x00200000;
        internal const uint ObjCaseInsensitive = 0x00000040;
        internal const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
        internal const int StatusObjectPathNotFound = unchecked((int)0xC000003A);
        internal const int StatusBufferOverflow = unchecked((int)0x80000005);
        internal const int StatusNoMoreFiles = unchecked((int)0x80000006);
        internal const uint SeFileObject = 1;
        internal const uint OwnerSecurityInformation = 0x00000001;
        internal const uint DaclSecurityInformation = 0x00000004;
        internal const int FileAllAccess = 0x001F01FF;
        internal const int FileRenameInformationClass = 10;
        internal const int FileNamesInformationClass = 12;
        internal const int FileNamesInformationHeaderSize = 12;

        internal enum FileInfoByHandleClass { FileRenameInfo = 3, FileDispositionInfo = 4 }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileDispositionInformation
        {
            [MarshalAs(UnmanagedType.Bool)] public bool DeleteFile;
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
        internal struct UnicodeString
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ObjectAttributes
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IoStatusBlock
        {
            public IntPtr Status;
            public UIntPtr Information;
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
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(
            SafeFileHandle file, out ByHandleFileInformation information);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReadFile(
            SafeFileHandle file, byte[] buffer, uint bytesToRead, out uint bytesRead, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFileInformationByHandle(
            SafeFileHandle file, FileInfoByHandleClass informationClass, IntPtr information, uint bufferSize);

        [DllImport("ntdll.dll")]
        internal static extern int NtCreateFile(
            out SafeFileHandle fileHandle, uint desiredAccess, ref ObjectAttributes objectAttributes,
            out IoStatusBlock ioStatusBlock, IntPtr allocationSize, uint fileAttributes, uint shareAccess,
            uint createDisposition, uint createOptions, IntPtr eaBuffer, uint eaLength);

        [DllImport("ntdll.dll")]
        internal static extern int NtSetInformationFile(
            SafeFileHandle fileHandle, out IoStatusBlock ioStatusBlock, IntPtr fileInformation,
            uint length, int fileInformationClass);

        [DllImport("ntdll.dll")]
        internal static extern int NtQueryDirectoryFile(
            SafeFileHandle fileHandle,
            IntPtr @event,
            IntPtr apcRoutine,
            IntPtr apcContext,
            out IoStatusBlock ioStatusBlock,
            IntPtr fileInformation,
            int length,
            int fileInformationClass,
            byte returnSingleEntry,
            IntPtr fileName,
            byte restartScan);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern uint GetSecurityInfo(
            SafeFileHandle handle, uint objectType, uint securityInfo, out IntPtr owner,
            out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr securityDescriptor);

        [DllImport("advapi32.dll")]
        internal static extern uint GetSecurityDescriptorLength(IntPtr securityDescriptor);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr LocalFree(IntPtr memory);
    }
}
