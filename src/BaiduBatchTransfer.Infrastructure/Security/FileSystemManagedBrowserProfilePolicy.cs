using System.Text;
using System.ComponentModel;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using BaiduBatchTransfer.Core.Domain;
using Microsoft.Win32.SafeHandles;

namespace BaiduBatchTransfer.Infrastructure.Security;

public enum ManagedBrowserProfileStatus
{
    Ready = 1,
    IsolationRequired = 2,
}

public enum ManagedBrowserProfileIsolationReason
{
    None = 0,
    MissingMarker = 1,
    InvalidMarker = 2,
    IdentityMismatch = 3,
    UnsafePath = 4,
    AccessControlMismatch = 5,
    ConcurrentMutation = 6,
}

public sealed class ManagedBrowserProfileDescriptor
{
    internal ManagedBrowserProfileDescriptor(
        Guid accountId,
        BrowserFamily family,
        Guid profileId,
        Guid ownershipId,
        string canonicalSourcePath)
    {
        AccountId = accountId;
        Family = family;
        ProfileId = profileId;
        OwnershipId = ownershipId;
        CanonicalSourcePath = canonicalSourcePath;
    }

    public Guid AccountId { get; }
    public BrowserFamily Family { get; }
    public BrowserFamily BrowserFamily => Family;
    public Guid ProfileId { get; }
    public Guid OwnershipId { get; }
    public Guid ProfileOwnershipId => OwnershipId;
    public string CanonicalSourcePath { get; }

    public override string ToString() =>
        $"ManagedBrowserProfileDescriptor {{ AccountId = [REDACTED], Family = {Family}, " +
        "ProfileId = [REDACTED], OwnershipId = [REDACTED], CanonicalSourcePath = [REDACTED] }";
}

public sealed class ManagedBrowserProfileResult
{
    private ManagedBrowserProfileResult(
        ManagedBrowserProfileStatus status,
        ManagedBrowserProfileDescriptor? profile,
        ManagedBrowserProfileIsolationReason isolationReason)
    {
        Status = status;
        Profile = profile;
        IsolationReason = isolationReason;
    }

    public ManagedBrowserProfileStatus Status { get; }
    public ManagedBrowserProfileDescriptor? Profile { get; }
    public ManagedBrowserProfileIsolationReason IsolationReason { get; }

    internal static ManagedBrowserProfileResult Ready(ManagedBrowserProfileDescriptor profile) =>
        new(ManagedBrowserProfileStatus.Ready, profile, ManagedBrowserProfileIsolationReason.None);

    internal static ManagedBrowserProfileResult IsolationRequired(ManagedBrowserProfileIsolationReason reason) =>
        new(ManagedBrowserProfileStatus.IsolationRequired, null, reason);

    public override string ToString() =>
        $"ManagedBrowserProfileResult {{ Status = {Status}, Profile = " +
        (Profile is null ? "null" : "[REDACTED]") + $", IsolationReason = {IsolationReason} }}";
}

public interface IManagedBrowserProfilePolicy
{
    Task<ManagedBrowserProfileResult> GetOrCreateAsync(
        Guid accountId,
        BrowserFamily family,
        CancellationToken cancellationToken = default);

    Task<ManagedBrowserProfileResult> OpenExistingAsync(
        ManagedBrowserProfileDescriptor expected,
        CancellationToken cancellationToken = default);
}

public sealed class FileSystemManagedBrowserProfilePolicy : IManagedBrowserProfilePolicy
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CreationGates =
        new(StringComparer.OrdinalIgnoreCase);
    internal const string MarkerName = ".bbt-managed-profile-owner.v1";
    private const string RootName = "managed-browser-profiles";
    private readonly string appDataRoot;
    private readonly string managedRoot;
    private readonly Action<string>? beforeMarkerOpen;
    private readonly Action<string>? afterDirectoryPin;
    private readonly IProfilePathFileSystem fileSystem;

    public FileSystemManagedBrowserProfilePolicy(string appDataRoot) :
        this(appDataRoot, PhysicalProfilePathFileSystem.Instance, null)
    {
    }

    internal FileSystemManagedBrowserProfilePolicy(string appDataRoot, Action<string>? beforeMarkerOpen) :
        this(appDataRoot, PhysicalProfilePathFileSystem.Instance, beforeMarkerOpen, null)
    {
    }

    internal FileSystemManagedBrowserProfilePolicy(
        string appDataRoot,
        Action<string>? beforeMarkerOpen,
        Action<string>? afterDirectoryPin) :
        this(appDataRoot, PhysicalProfilePathFileSystem.Instance, beforeMarkerOpen, afterDirectoryPin)
    {
    }

    internal FileSystemManagedBrowserProfilePolicy(
        string appDataRoot,
        IProfilePathFileSystem fileSystem,
        Action<string>? beforeMarkerOpen = null,
        Action<string>? afterDirectoryPin = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataRoot);
        this.appDataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDataRoot));
        if (string.Equals(Path.GetPathRoot(this.appDataRoot), this.appDataRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The application data root cannot be a filesystem root.", nameof(appDataRoot));
        this.fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        managedRoot = Path.Combine(this.appDataRoot, RootName);
        this.beforeMarkerOpen = beforeMarkerOpen;
        this.afterDirectoryPin = afterDirectoryPin;
        _ = new FileSystemAccountProfilePathPolicy(managedRoot, fileSystem);
    }

    public Task<ManagedBrowserProfileResult> GetOrCreateAsync(
        Guid accountId,
        BrowserFamily family,
        CancellationToken cancellationToken = default)
    {
        ValidateArguments(accountId, family);
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetCanonicalPath(accountId, family);
        var gate = CreationGates.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        gate.Wait(cancellationToken);
        try
        {
            var existed = Directory.Exists(path);
            bool accessControlValid;
            try
            {
                accessControlValid = TryEnsureDirectoryTree(path, cancellationToken);
            }
            catch (IOException)
            {
                return Task.FromResult(ManagedBrowserProfileResult.IsolationRequired(
                    ManagedBrowserProfileIsolationReason.UnsafePath));
            }
            if (!accessControlValid)
                return Task.FromResult(ManagedBrowserProfileResult.IsolationRequired(
                    ManagedBrowserProfileIsolationReason.AccessControlMismatch));
            EnsureSafePath(path);
            beforeMarkerOpen?.Invoke(path);
            try
            {
                EnsureSafePath(path);
            }
            catch (IOException)
            {
                return Task.FromResult(ManagedBrowserProfileResult.IsolationRequired(
                    ManagedBrowserProfileIsolationReason.UnsafePath));
            }
            using var pinnedDirectories = PinnedDirectoryChain.Open(path);
            afterDirectoryPin?.Invoke(path);
            if (!pinnedDirectories.MatchesFinalPath(path))
                return Task.FromResult(ManagedBrowserProfileResult.IsolationRequired(
                    ManagedBrowserProfileIsolationReason.ConcurrentMutation));
            var markerPath = Path.Combine(path, MarkerName);
            if (fileSystem.TryGetAttributes(markerPath, out var markerAttributes))
            {
                if (!IsOrdinaryMarker(markerAttributes))
                    return Task.FromResult(ManagedBrowserProfileResult.IsolationRequired(
                        ManagedBrowserProfileIsolationReason.UnsafePath));
                return Task.FromResult(ReadExistingMarker(markerPath, accountId, family, path, null));
            }
            if (existed)
                return Task.FromResult(ManagedBrowserProfileResult.IsolationRequired(
                    ManagedBrowserProfileIsolationReason.MissingMarker));

            var marker = new Marker(accountId, family, Guid.NewGuid(), Guid.NewGuid());
            using (var markerFile = PinnedMarker.TryCreate(markerPath))
            {
                if (markerFile is null)
                    return Task.FromResult(ReadExistingMarker(markerPath, accountId, family, path, null));
                markerFile.WriteText(Format(marker));
                SetCurrentUserOnlyAcl(markerPath, directory: false);
            }

            return Task.FromResult(ManagedBrowserProfileResult.Ready(ToDescriptor(marker, path)));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(ManagedBrowserProfileResult.IsolationRequired(
                ManagedBrowserProfileIsolationReason.AccessControlMismatch));
        }
        catch (IOException)
        {
            return Task.FromResult(ManagedBrowserProfileResult.IsolationRequired(
                ManagedBrowserProfileIsolationReason.UnsafePath));
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<ManagedBrowserProfileResult> OpenExistingAsync(
        ManagedBrowserProfileDescriptor expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ValidateArguments(expected.AccountId, expected.Family);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return OpenExistingCore(expected, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(ManagedBrowserProfileResult.IsolationRequired(
                ManagedBrowserProfileIsolationReason.AccessControlMismatch));
        }
        catch (IOException)
        {
            return Task.FromResult(ManagedBrowserProfileResult.IsolationRequired(
                ManagedBrowserProfileIsolationReason.UnsafePath));
        }
    }

    private Task<ManagedBrowserProfileResult> OpenExistingCore(
        ManagedBrowserProfileDescriptor expected,
        CancellationToken cancellationToken)
    {
        var path = GetCanonicalPath(expected.AccountId, expected.Family);
        if (!string.Equals(path, expected.CanonicalSourcePath, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(ManagedBrowserProfileResult.IsolationRequired(
                ManagedBrowserProfileIsolationReason.IdentityMismatch));
        if (!Directory.Exists(path))
            return Task.FromResult(ManagedBrowserProfileResult.IsolationRequired(
                ManagedBrowserProfileIsolationReason.MissingMarker));
        bool accessControlValid;
        try
        {
            accessControlValid = TryEnsureDirectoryTree(path, cancellationToken);
        }
        catch (IOException)
        {
            return Task.FromResult(ManagedBrowserProfileResult.IsolationRequired(
                ManagedBrowserProfileIsolationReason.UnsafePath));
        }
        if (!accessControlValid)
            return Task.FromResult(ManagedBrowserProfileResult.IsolationRequired(
                ManagedBrowserProfileIsolationReason.AccessControlMismatch));
        EnsureSafePath(path);
        using var pinnedDirectories = PinnedDirectoryChain.Open(path);
        afterDirectoryPin?.Invoke(path);
        if (!pinnedDirectories.MatchesFinalPath(path))
            return Task.FromResult(ManagedBrowserProfileResult.IsolationRequired(
                ManagedBrowserProfileIsolationReason.ConcurrentMutation));
        var markerPath = Path.Combine(path, MarkerName);
        if (!fileSystem.TryGetAttributes(markerPath, out var markerAttributes))
            return Task.FromResult(ManagedBrowserProfileResult.IsolationRequired(
                ManagedBrowserProfileIsolationReason.MissingMarker));
        if (!IsOrdinaryMarker(markerAttributes))
            return Task.FromResult(ManagedBrowserProfileResult.IsolationRequired(
                ManagedBrowserProfileIsolationReason.UnsafePath));
        return Task.FromResult(ReadExistingMarker(
            markerPath, expected.AccountId, expected.Family, path, expected));
    }

    private ManagedBrowserProfileResult ReadExistingMarker(
        string markerPath,
        Guid accountId,
        BrowserFamily family,
        string path,
        ManagedBrowserProfileDescriptor? expected)
    {
        using var markerFile = PinnedMarker.TryOpen(markerPath);
        if (markerFile is null)
            return ManagedBrowserProfileResult.IsolationRequired(
                ManagedBrowserProfileIsolationReason.ConcurrentMutation);
        if (!markerFile.IsOrdinaryFile)
            return ManagedBrowserProfileResult.IsolationRequired(
                ManagedBrowserProfileIsolationReason.UnsafePath);
        if (!HasCurrentUserOnlyAcl(markerPath, directory: false))
            return ManagedBrowserProfileResult.IsolationRequired(
                ManagedBrowserProfileIsolationReason.AccessControlMismatch);
        try
        {
            var marker = Parse(markerFile.ReadText());
            if (marker.AccountId != accountId || marker.Family != family ||
                expected is not null && (marker.ProfileId != expected.ProfileId || marker.OwnershipId != expected.OwnershipId))
                return ManagedBrowserProfileResult.IsolationRequired(
                    ManagedBrowserProfileIsolationReason.IdentityMismatch);
            return ManagedBrowserProfileResult.Ready(ToDescriptor(marker, path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            return ManagedBrowserProfileResult.IsolationRequired(
                ManagedBrowserProfileIsolationReason.InvalidMarker);
        }
    }

    private string GetCanonicalPath(Guid accountId, BrowserFamily family) => Path.Combine(
        managedRoot,
        family == BrowserFamily.Chrome ? "chrome" : "edge",
        accountId.ToString("N"));

    private void EnsureSafePath(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var prefix = appDataRoot + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The managed browser profile path escaped its application data root.");
        string? current = full;
        while (!string.IsNullOrEmpty(current))
        {
            var attributes = fileSystem.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                (attributes & FileAttributes.Directory) == 0)
                throw new IOException("The managed browser profile path is unsafe.");
            if (string.Equals(current, appDataRoot, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current);
        }
        if (!string.Equals(current, appDataRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The managed browser profile path escaped its application data root.");
    }

    private bool TryEnsureDirectoryTree(string accountPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(appDataRoot);
        foreach (var path in new[] { managedRoot, Path.GetDirectoryName(accountPath)!, accountPath })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existed = Directory.Exists(path);
            Directory.CreateDirectory(path);
            EnsureSafePath(path);
            if (OperatingSystem.IsWindows())
            {
                if (existed && !HasCurrentUserOnlyAcl(path, directory: true)) return false;
                if (!existed) SetCurrentUserOnlyAcl(path, directory: true);
            }
        }
        return true;
    }

    private static bool HasCurrentUserOnlyAcl(string path, bool directory)
    {
        if (!OperatingSystem.IsWindows()) return true;
        return HasCurrentUserOnlyAclWindows(path, directory);
    }

    private static bool IsOrdinaryMarker(FileAttributes attributes) =>
        (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;

    [SupportedOSPlatform("windows")]
    private static bool HasCurrentUserOnlyAclWindows(string path, bool directory)
    {
        try
        {
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is null) return false;
            FileSystemSecurity security = directory
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            if (!security.AreAccessRulesProtected ||
                !currentUser.Equals(security.GetOwner(typeof(SecurityIdentifier)))) return false;
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().ToArray();
            return rules.Length > 0 && rules.All(rule =>
                !rule.IsInherited &&
                rule.AccessControlType == AccessControlType.Allow &&
                currentUser.Equals(rule.IdentityReference) &&
                (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void SetCurrentUserOnlyAcl(string path, bool directory)
    {
        if (OperatingSystem.IsWindows()) SetCurrentUserOnlyAclWindows(path, directory);
    }

    [SupportedOSPlatform("windows")]
    private static void SetCurrentUserOnlyAclWindows(string path, bool directory)
    {
        var currentUser = WindowsIdentity.GetCurrent().User ??
            throw new IOException("The current Windows user identity is unavailable.");
        if (directory)
        {
            var security = new DirectorySecurity();
            security.SetOwner(currentUser);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        else
        {
            var security = new FileSecurity();
            security.SetOwner(currentUser);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
    }

    private static void ValidateArguments(Guid accountId, BrowserFamily family)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("A local account ID is required.", nameof(accountId));
        if (family is not (BrowserFamily.Chrome or BrowserFamily.Edge))
            throw new ArgumentOutOfRangeException(nameof(family), family, "A managed browser family is required.");
    }

    private static string Format(Marker marker) =>
        $"bbt-managed-browser-profile:1\naccount-id={marker.AccountId:N}\n" +
        $"browser-family={marker.Family}\nprofile-id={marker.ProfileId:N}\n" +
        $"profile-ownership-id={marker.OwnershipId:N}\n";

    private static Marker Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length != 6 || lines[0] != "bbt-managed-browser-profile:1" || lines[5].Length != 0 ||
            !lines[1].StartsWith("account-id=", StringComparison.Ordinal) ||
            !lines[2].StartsWith("browser-family=", StringComparison.Ordinal) ||
            !lines[3].StartsWith("profile-id=", StringComparison.Ordinal) ||
            !lines[4].StartsWith("profile-ownership-id=", StringComparison.Ordinal) ||
            !Guid.TryParseExact(lines[1]["account-id=".Length..], "N", out var accountId) ||
            !Enum.TryParse<BrowserFamily>(lines[2]["browser-family=".Length..], ignoreCase: false, out var family) ||
            family is not (BrowserFamily.Chrome or BrowserFamily.Edge) ||
            !Guid.TryParseExact(lines[3]["profile-id=".Length..], "N", out var profileId) ||
            !Guid.TryParseExact(lines[4]["profile-ownership-id=".Length..], "N", out var ownershipId) ||
            accountId == Guid.Empty || profileId == Guid.Empty || ownershipId == Guid.Empty)
            throw new FormatException("The managed profile ownership marker is invalid.");
        var marker = new Marker(accountId, family, profileId, ownershipId);
        if (!string.Equals(text, Format(marker), StringComparison.Ordinal))
            throw new FormatException("The managed profile ownership marker is not canonical.");
        return marker;
    }

    private static ManagedBrowserProfileDescriptor ToDescriptor(Marker marker, string path) =>
        new(marker.AccountId, marker.Family, marker.ProfileId, marker.OwnershipId, path);

    private sealed record Marker(Guid AccountId, BrowserFamily Family, Guid ProfileId, Guid OwnershipId);

    private sealed class PinnedDirectoryChain : IDisposable
    {
        private readonly List<SafeFileHandle> handles;
        private readonly NativeMethods.ByHandleFileInformation finalInformation;

        private PinnedDirectoryChain(
            List<SafeFileHandle> handles,
            NativeMethods.ByHandleFileInformation finalInformation)
        {
            this.handles = handles;
            this.finalInformation = finalInformation;
        }

        public static PinnedDirectoryChain Open(string canonicalPath)
        {
            if (!OperatingSystem.IsWindows()) return new PinnedDirectoryChain([], default);
            var root = Path.GetPathRoot(canonicalPath) ??
                throw new IOException("The managed browser profile root is not fully qualified.");
            var paths = new List<string> { root };
            var current = root;
            var relative = Path.GetRelativePath(root, canonicalPath);
            foreach (var component in relative.Split(
                         Path.DirectorySeparatorChar,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                paths.Add(current);
            }

            var handles = new List<SafeFileHandle>(paths.Count);
            NativeMethods.ByHandleFileInformation finalInformation = default;
            try
            {
                foreach (var path in paths)
                {
                    var handle = NativeMethods.CreateFileW(
                        path,
                        NativeMethods.FileReadAttributes,
                        NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                        IntPtr.Zero,
                        NativeMethods.OpenExisting,
                        NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint,
                        IntPtr.Zero);
                    if (handle.IsInvalid)
                    {
                        var error = Marshal.GetLastWin32Error();
                        handle.Dispose();
                        throw new IOException(
                            "A managed browser profile ancestor could not be pinned safely.",
                            new Win32Exception(error));
                    }
                    if (!NativeMethods.GetFileInformationByHandle(handle, out var information))
                    {
                        var error = Marshal.GetLastWin32Error();
                        handle.Dispose();
                        throw new IOException(
                            "A managed browser profile ancestor could not be inspected safely.",
                            new Win32Exception(error));
                    }
                    if ((information.FileAttributes & FileAttributes.Directory) == 0 ||
                        (information.FileAttributes & FileAttributes.ReparsePoint) != 0)
                    {
                        handle.Dispose();
                        throw new IOException("A managed browser profile ancestor is unsafe.");
                    }
                    handles.Add(handle);
                    finalInformation = information;
                }
                return new PinnedDirectoryChain(handles, finalInformation);
            }
            catch
            {
                foreach (var handle in handles) handle.Dispose();
                throw;
            }
        }

        public bool MatchesFinalPath(string path)
        {
            if (!OperatingSystem.IsWindows()) return true;
            var handle = NativeMethods.CreateFileW(
                path,
                NativeMethods.FileReadAttributes,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                IntPtr.Zero,
                NativeMethods.OpenExisting,
                NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return false;
            }
            using (handle)
            {
                return NativeMethods.GetFileInformationByHandle(handle, out var current) &&
                       (current.FileAttributes & FileAttributes.Directory) != 0 &&
                       (current.FileAttributes & FileAttributes.ReparsePoint) == 0 &&
                       current.VolumeSerialNumber == finalInformation.VolumeSerialNumber &&
                       current.FileIndexHigh == finalInformation.FileIndexHigh &&
                       current.FileIndexLow == finalInformation.FileIndexLow;
            }
        }

        public void Dispose()
        {
            for (var index = handles.Count - 1; index >= 0; index--) handles[index].Dispose();
        }
    }

    private sealed class PinnedMarker : IDisposable
    {
        private const int MaximumMarkerBytes = 512;
        private readonly SafeFileHandle handle;
        private readonly FileStream? managedStream;
        private readonly NativeMethods.ByHandleFileInformation information;

        private PinnedMarker(
            SafeFileHandle handle,
            NativeMethods.ByHandleFileInformation information,
            FileStream? managedStream = null)
        {
            this.handle = handle;
            this.information = information;
            this.managedStream = managedStream;
        }

        public bool IsOrdinaryFile =>
            (information.FileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;

        public static PinnedMarker? TryOpen(string path) => Open(path, createNew: false);

        public static PinnedMarker? TryCreate(string path) => Open(path, createNew: true);

        private static PinnedMarker? Open(string path, bool createNew)
        {
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    var stream = new FileStream(
                        path,
                        createNew ? FileMode.CreateNew : FileMode.Open,
                        createNew ? FileAccess.Write : FileAccess.Read,
                        FileShare.Read);
                    var attributes = File.GetAttributes(path);
                    return new PinnedMarker(stream.SafeFileHandle, new NativeMethods.ByHandleFileInformation
                    {
                        FileAttributes = attributes,
                    }, stream);
                }
                catch (IOException) when (createNew || !File.Exists(path))
                {
                    return null;
                }
            }

            var handle = NativeMethods.CreateFileW(
                path,
                createNew ? NativeMethods.GenericWrite | NativeMethods.FileReadAttributes :
                    NativeMethods.GenericRead | NativeMethods.FileReadAttributes,
                NativeMethods.FileShareRead,
                IntPtr.Zero,
                createNew ? NativeMethods.CreateNew : NativeMethods.OpenExisting,
                NativeMethods.FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                if (error is NativeMethods.ErrorFileNotFound or NativeMethods.ErrorPathNotFound ||
                    createNew && error is NativeMethods.ErrorFileExists or NativeMethods.ErrorAlreadyExists)
                    return null;
                throw new IOException(
                    "The managed browser profile marker could not be opened safely.",
                    new Win32Exception(error));
            }
            if (!NativeMethods.GetFileInformationByHandle(handle, out var information))
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException(
                    "The managed browser profile marker could not be inspected safely.",
                    new Win32Exception(error));
            }
            return new PinnedMarker(handle, information);
        }

        public string ReadText()
        {
            var buffer = new byte[MaximumMarkerBytes + 1];
            int length;
            if (managedStream is not null)
            {
                length = managedStream.Read(buffer);
            }
            else
            {
                if (!NativeMethods.ReadFile(handle, buffer, (uint)buffer.Length, out var bytesRead, IntPtr.Zero))
                    throw new IOException(
                        "The managed browser profile marker could not be read safely.",
                        new Win32Exception(Marshal.GetLastWin32Error()));
                length = checked((int)bytesRead);
            }
            if (length > MaximumMarkerBytes)
                throw new FormatException("The managed profile ownership marker is too large.");
            return Encoding.ASCII.GetString(buffer, 0, length);
        }

        public void WriteText(string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            if (managedStream is not null)
            {
                managedStream.Write(bytes);
                managedStream.Flush(flushToDisk: true);
                return;
            }
            if (!NativeMethods.WriteFile(handle, bytes, (uint)bytes.Length, out var bytesWritten, IntPtr.Zero) ||
                bytesWritten != bytes.Length || !NativeMethods.FlushFileBuffers(handle))
                throw new IOException(
                    "The managed browser profile marker could not be written safely.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
        }

        public void Dispose()
        {
            if (managedStream is not null) managedStream.Dispose();
            else handle.Dispose();
        }
    }

    private static class NativeMethods
    {
        internal const uint GenericRead = 0x80000000;
        internal const uint GenericWrite = 0x40000000;
        internal const uint FileReadAttributes = 0x00000080;
        internal const uint FileShareRead = 0x00000001;
        internal const uint FileShareWrite = 0x00000002;
        internal const uint OpenExisting = 3;
        internal const uint CreateNew = 1;
        internal const uint FileFlagBackupSemantics = 0x02000000;
        internal const uint FileFlagOpenReparsePoint = 0x00200000;
        internal const int ErrorFileNotFound = 2;
        internal const int ErrorPathNotFound = 3;
        internal const int ErrorFileExists = 80;
        internal const int ErrorAlreadyExists = 183;

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
        internal static extern bool WriteFile(
            SafeFileHandle file,
            byte[] buffer,
            uint bytesToWrite,
            out uint bytesWritten,
            IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FlushFileBuffers(SafeFileHandle file);
    }
}
